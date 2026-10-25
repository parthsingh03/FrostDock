using System.Runtime.InteropServices;

namespace FrostDock;

/// <summary>
/// Master volume via the Windows Core Audio API (MMDeviceEnumerator +
/// IAudioEndpointVolume). No new dependencies, no fake slider: if the
/// endpoint cannot be reached, IsAvailable is false and the Control Center
/// volume row disables itself instead of pretending.
/// Only the vtable slots actually used are declared, in order.
/// </summary>
internal static class AudioVolume
{
    private const uint CLSCTX_ALL = 23;
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692");
    private static readonly Guid IID_MMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_AudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
        ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IMMDeviceEnumerator ppv);

    public static bool TryGet(out float level)
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
        catch { return false; }
        finally
        {
            if (vol != null) { try { Marshal.ReleaseComObject(vol); } catch { } }
        }
    }

    public static bool TrySet(float level)
    {
        IAudioEndpointVolume? vol = null;
        try
        {
            vol = GetEndpoint();
            if (vol == null) return false;
            int hr = vol.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), Guid.Empty);
            return hr == 0;
        }
        catch { return false; }
        finally
        {
            if (vol != null) { try { Marshal.ReleaseComObject(vol); } catch { } }
        }
    }

    private static IAudioEndpointVolume? GetEndpoint()
    {
        var clsid = CLSID_MMDeviceEnumerator;
        var iid = IID_MMDeviceEnumerator;
        CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out IMMDeviceEnumerator enumerator);
        try
        {
            // eRender = 0, eConsole = 0.
            int hr = enumerator.GetDefaultAudioEndpoint(0, 0, out IMMDevice device);
            if (hr != 0 || device == null) return null;
            try
            {
                var audioIid = IID_AudioEndpointVolume;
                hr = device.Activate(ref audioIid, CLSCTX_ALL, IntPtr.Zero, out IAudioEndpointVolume volume);
                return hr == 0 ? volume : null;
            }
            finally
            {
                try { Marshal.ReleaseComObject(device); } catch { }
            }
        }
        finally
        {
            try { Marshal.ReleaseComObject(enumerator); } catch { }
        }
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
