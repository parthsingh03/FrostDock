using System.Runtime.InteropServices;

namespace FrostDock;

/// <summary>
/// Master volume via the Windows Core Audio API (MMDeviceEnumerator +
/// IAudioEndpointVolume), with a widening fallback chain, plus a legacy
/// winmm waveOut last resort. Never fakes a value: when every layer fails,
/// TryGet returns false and LastError explains why (Control Center shows
/// that reason instead of a bare "n/a").
/// Chain (v1.4): default render endpoint → first ACTIVE render endpoint
/// → first render endpoint in ANY state → winmm waveOut (legacy mixer).
/// v1.3 stopped after the ACTIVE step, which is why Parth's machine still
/// showed n/a: either no device reports ACTIVE, or enumeration itself
/// fails — both cases now fall through to the wider steps instead.
/// Only the vtable slots actually used are declared, in order.
/// </summary>
internal static class AudioVolume
{
    private const uint CLSCTX_ALL = 23;

    /// <summary>Human-readable reason for the last TryGet/TrySet failure; empty on success.</summary>
    public static string LastError { get; private set; } = string.Empty;

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692");
    private static readonly Guid IID_MMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_AudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
        ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IMMDeviceEnumerator ppv);

    // Legacy mixer fallback (winmm.dll). WAVE_MAPPER = -1: the default wave device.
    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutGetVolume(IntPtr hwo, out uint dwVolume);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutSetVolume(IntPtr hwo, uint dwVolume);

    private static readonly IntPtr WAVE_MAPPER = new(-1);

    public static bool TryGet(out float level)
    {
        level = 0;
        LastError = string.Empty;
        if (TryGetCore(out level)) return true;
        // Core Audio exhausted: try the legacy mixer before giving up.
        try
        {
            if (waveOutGetVolume(WAVE_MAPPER, out uint vol) == 0)
            {
                level = (vol & 0xFFFF) / 65535f;
                return true;
            }
        }
        catch { }
        if (string.IsNullOrEmpty(LastError)) LastError = "No audio device found";
        return false;
    }

    public static bool TrySet(float level)
    {
        LastError = string.Empty;
        if (TrySetCore(level)) return true;
        try
        {
            uint v = (uint)Math.Round(Math.Clamp(level, 0f, 1f) * 65535f);
            uint both = v | (v << 16);
            if (waveOutSetVolume(WAVE_MAPPER, both) == 0) return true;
        }
        catch { }
        if (string.IsNullOrEmpty(LastError)) LastError = "No audio device found";
        return false;
    }

    private static bool TryGetCore(out float level)
    {
        level = 0;
        IAudioEndpointVolume? vol = null;
        try
        {
            vol = GetEndpoint();
            if (vol == null) return false;
            int hr = vol.GetMasterVolumeLevelScalar(out level);
            return hr == 0;
        }
        catch { LastError = "Audio service unreachable"; return false; }
        finally
        {
            if (vol != null) { try { Marshal.ReleaseComObject(vol); } catch { } }
        }
    }

    private static bool TrySetCore(float level)
    {
        IAudioEndpointVolume? vol = null;
        try
        {
            vol = GetEndpoint();
            if (vol == null) return false;
            int hr = vol.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), Guid.Empty);
            return hr == 0;
        }
        catch { LastError = "Audio service unreachable"; return false; }
        finally
        {
            if (vol != null) { try { Marshal.ReleaseComObject(vol); } catch { } }
        }
    }

    /// <summary>
    /// Walks the candidate chain, activating each device in turn; the first
    /// device that yields a working IAudioEndpointVolume wins. Candidates
    /// that fail Activate are released and skipped (a dead "default" must
    /// never block a live device behind it).
    /// </summary>
    private static IAudioEndpointVolume? GetEndpoint()
    {
        var clsid = CLSID_MMDeviceEnumerator;
        var iid = IID_MMDeviceEnumerator;
        IMMDeviceEnumerator enumerator;
        try
        {
            CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out enumerator);
        }
        catch
        {
            LastError = "Audio service unreachable";
            return null;
        }
        try
        {
            // 1) Default render endpoint (eRender = 0, eConsole = 0).
            int hr = enumerator.GetDefaultAudioEndpoint(0, 0, out IMMDevice def);
            if (hr == 0 && def != null)
            {
                var vol = TryActivate(def);
                try { Marshal.ReleaseComObject(def); } catch { }
                if (vol != null) return vol;
            }
            // 2) First ACTIVE render endpoint that activates (v1.3 fallback).
            var active = TryActivateFromCollection(enumerator, 1);
            if (active != null) return active;
            // 3) v1.4: ANY-state render endpoint that activates (disabled /
            // unplugged devices still expose a working endpoint on some
            // machines, and a machine with no ACTIVE device at all needs it).
            var any = TryActivateFromCollection(enumerator, 0xF);
            if (any != null) return any;
            LastError = "No audio device found";
            return null;
        }
        finally
        {
            try { Marshal.ReleaseComObject(enumerator); } catch { }
        }
    }

    /// <summary>Iterates an endpoint collection, returning the first device that activates.</summary>
    private static IAudioEndpointVolume? TryActivateFromCollection(IMMDeviceEnumerator enumerator, int stateMask)
    {
        IntPtr collPtr = IntPtr.Zero;
        IMMDeviceCollection? coll = null;
        try
        {
            int hr = enumerator.EnumAudioEndpoints(0, stateMask, out collPtr);
            if (hr != 0 || collPtr == IntPtr.Zero) return null;
            coll = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(collPtr);
            hr = coll.GetCount(out uint count);
            if (hr != 0) return null;
            for (uint i = 0; i < count; i++)
            {
                hr = coll.GetDevice(i, out IMMDevice dev);
                if (hr != 0 || dev == null) continue;
                try
                {
                    var vol = TryActivate(dev);
                    if (vol != null) return vol;
                }
                finally
                {
                    try { Marshal.ReleaseComObject(dev); } catch { }
                }
            }
            return null;
        }
        catch { return null; }
        finally
        {
            if (coll != null) { try { Marshal.ReleaseComObject(coll); } catch { } }
            if (collPtr != IntPtr.Zero) { try { Marshal.Release(collPtr); } catch { } }
        }
    }

    private static IAudioEndpointVolume? TryActivate(IMMDevice device)
    {
        try
        {
            var audioIid = IID_AudioEndpointVolume;
            int hr = device.Activate(ref audioIid, CLSCTX_ALL, IntPtr.Zero, out IAudioEndpointVolume volume);
            return hr == 0 ? volume : null;
        }
        catch { return null; }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-AE912DCDDAF2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, out IAudioEndpointVolume ppInterface);
    }

    [ComImport, Guid("0BD7A1BE-7A15-4A2D-9DB0-3C5E3F6B2A0F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint pcDevices);
        [PreserveSig] int GetDevice(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int GetChannelCount(out uint pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, Guid pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, Guid pguidEventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
    }
}
