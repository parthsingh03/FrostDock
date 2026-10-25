using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace FrostDock;

/// <summary>
/// High-resolution icon extraction for v1.1.
/// Replaces Icon.ExtractAssociatedIcon (32x32 only) with IShellItemImageFactory
/// at 256px, converts the HBITMAP to a Bitmap WITHOUT dropping the alpha
/// channel (Bitmap.FromHbitmap would discard it), resolves UWP/packaged apps
/// via their AUMID, and resolves .lnk targets. The letter-tile fallback is
/// gone: unresolvable apps get a neutral generic tile, never a monogram.
/// </summary>
internal static class ShellIcons
{
    [Flags]
    public enum SIIGBF : uint
    {
        RESIZETOFIT = 0x00000000,
        BIGGERSIZEOK = 0x00000001,
        MEMORYONLY = 0x00000002,
        ICONONLY = 0x00000004,
        THUMBNAILONLY = 0x00000008,
        INCACHEONLY = 0x00000010,
        CROPTOSQUARE = 0x00000020
    }

    private static readonly Guid IID_ShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IID_ShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IID_PersistFile = new("0000010B-0000-0000-C000-000000000046");

    private const int IconPx = 256;

    /// <summary>Best-effort 256px icon for a parsing name (file path or shell:AppsFolder AUMID).</summary>
    public static Bitmap? GetImageForParsingName(string parsingName)
    {
        IShellItemImageFactory? factory = null;
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            var iid = IID_ShellItemImageFactory;
            int hr = NativeMethods.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out factory);
            if (hr != 0 || factory == null) return null;
            hr = factory.GetImage(new NativeMethods.SIZE { cx = IconPx, cy = IconPx },
                SIIGBF.BIGGERSIZEOK | SIIGBF.ICONONLY, out hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero) return null;
            return HBitmapToBitmapAlpha(hBitmap);
        }
        catch { return null; }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                try { NativeMethods.DeleteObject(hBitmap); } catch { }
            }
            if (factory != null)
            {
                try { Marshal.ReleaseComObject(factory); } catch { }
            }
        }
    }

    /// <summary>
    /// Converts an HBITMAP to a Bitmap, preserving per-pixel alpha.
    /// Bitmap.FromHbitmap is deliberately NOT used: it drops the alpha channel.
    /// </summary>
    public static Bitmap? HBitmapToBitmapAlpha(IntPtr hBitmap)
    {
        IntPtr screenDc = IntPtr.Zero;
        try
        {
            if (NativeMethods.GetObject(hBitmap, Marshal.SizeOf<NativeMethods.BITMAP>(), out var bm) == 0) return null;
            if (bm.bmWidth <= 0 || bm.bmHeight <= 0 || bm.bmBitsPixel < 24) return null;
            int width = bm.bmWidth, height = bm.bmHeight;

            var bmi = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // top-down DIB
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0 // BI_RGB
                }
            };
            int stride = width * 4;
            IntPtr bits = Marshal.AllocHGlobal(stride * height);
            try
            {
                screenDc = NativeMethods.GetDC(IntPtr.Zero);
                int lines = NativeMethods.GetDIBits(screenDc, hBitmap, 0, (uint)height, bits, ref bmi, 0);
                if (lines == 0) return null;
                var bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    int srcStride = Math.Abs(data.Stride);
                    var row = new byte[stride];
                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(bits, y * stride), row, 0, stride);
                        Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * srcStride), stride);
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
                return bmp;
            }
            finally
            {
                Marshal.FreeHGlobal(bits);
            }
        }
        catch { return null; }
        finally
        {
            if (screenDc != IntPtr.Zero)
            {
                try { NativeMethods.ReleaseDC(IntPtr.Zero, screenDc); } catch { }
            }
        }
    }

    /// <summary>Resolves a .lnk file to its target path (empty string if it cannot be resolved).</summary>
    public static string ResolveLinkTarget(string lnkPath)
    {
        IShellLinkW? link = null;
        try
        {
            var clsid = CLSID_ShellLink;
            var iid = IID_ShellLinkW;
            int hr = NativeMethods.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out link);
            if (hr != 0 || link == null) return string.Empty;
            var persistIid = IID_PersistFile;
            IntPtr punk = Marshal.GetIUnknownForObject(link);
            try
            {
                IntPtr persistPtr;
                hr = Marshal.QueryInterface(punk, ref persistIid, out persistPtr);
                if (hr != 0 || persistPtr == IntPtr.Zero) return string.Empty;
                var persist = (IPersistFile)Marshal.GetObjectForIUnknown(persistPtr);
                try
                {
                    hr = persist.Load(lnkPath, 0);
                    if (hr != 0) return string.Empty;
                }
                finally
                {
                    try { Marshal.Release(persistPtr); } catch { }
                }
            }
            finally
            {
                Marshal.Release(punk);
            }
            var sb = new StringBuilder(260);
            hr = link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return hr == 0 ? sb.ToString() : string.Empty;
        }
        catch { return string.Empty; }
        finally
        {
            if (link != null)
            {
                try { Marshal.ReleaseComObject(link); } catch { }
            }
        }
    }

    /// <summary>
    /// For a window owned by ApplicationFrameHost.exe, finds the real app
    /// process via its child Windows.UI.Core.CoreWindow and returns its
    /// package family name plus real process id. False if not a UWP app.
    /// </summary>
    public static bool TryResolveUwpApp(IntPtr frameHostWindow, out string? family, out uint realPid)
    {
        family = null;
        realPid = 0;
        try
        {
            uint foundPid = 0;
            IntPtr foundCore = IntPtr.Zero;
            NativeMethods.EnumChildWindows(frameHostWindow, (hWnd, _) =>
            {
                var cls = new StringBuilder(256);
                NativeMethods.GetClassName(hWnd, cls, cls.Capacity);
                if (cls.ToString() == "Windows.UI.Core.CoreWindow")
                {
                    NativeMethods.GetWindowThreadProcessId(hWnd, out foundPid);
                    foundCore = hWnd;
                    return false; // stop enumerating
                }
                return true;
            }, IntPtr.Zero);
            if (foundPid == 0 || foundCore == IntPtr.Zero) return false;
            family = GetPackageFamilyName(foundPid);
            if (family == null) return false;
            realPid = foundPid;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// For a process running under ApplicationFrameHost.exe, finds the real app
    /// process via its child Windows.UI.Core.CoreWindow and returns its package
    /// family name (for building shell:AppsFolder\Family!App). Null if not a UWP app.
    /// </summary>
    public static string? GetUwpFamilyNameForFrameHost(IntPtr frameHostWindow)
    {
        return TryResolveUwpApp(frameHostWindow, out string? family, out _) ? family : null;
    }

    /// <summary>Package family name for a process id, or null.</summary>
    public static string? GetPackageFamilyName(uint pid)
    {
        IntPtr hProcess = IntPtr.Zero;
        try
        {
            hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero) return null;
            uint len = 0;
            NativeMethods.GetPackageFullName(hProcess, ref len, new StringBuilder());
            if (len == 0) return null;
            var sb = new StringBuilder((int)len);
            int hr = NativeMethods.GetPackageFullName(hProcess, ref len, sb);
            if (hr != 0) return null;
            string full = sb.ToString();
            int underscore = full.IndexOf('_');
            if (underscore <= 0) return null;
            // Package full name: FamilyName_Version_Arch_... ; family name is up to
            // the publisher-hash segment, i.e. everything before "_<version>".
            // The family name is FamilyName_PublisherId: find the second underscore
            // only if the segment after the first looks like a version (starts with a digit).
            // Simpler robust rule used by most AUMID builders: family = full name up to
            // the last '_' segment pair is unreliable, so use PackageFamilyName via the
            // full name minus version/arch/resource suffixes: split and take the first
            // two segments when the second is a version number, else the first segment
            // plus publisher id (last segment).
            var parts = full.Split('_');
            if (parts.Length >= 2 && parts[1].Length > 0 && char.IsDigit(parts[1][0]))
            {
                // Name_Version_Arch_Resource_PublisherId -> family = Name_PublisherId
                return parts[0] + "_" + parts[^1];
            }
            return parts[0];
        }
        catch { return null; }
        finally
        {
            if (hProcess != IntPtr.Zero)
            {
                try { NativeMethods.CloseHandle(hProcess); } catch { }
            }
        }
    }

    /// <summary>Heuristic: is this icon a full-bleed square (opaque corners)?</summary>
    public static bool IsFullBleedSquare(Bitmap bmp)
    {
        try
        {
            int w = bmp.Width, h = bmp.Height;
            if (w < 16 || h < 16) return false;
            int probe = Math.Max(2, Math.Min(w, h) / 16);
            foreach (var (cx, cy) in new[] { (probe, probe), (w - 1 - probe, probe), (probe, h - 1 - probe), (w - 1 - probe, h - 1 - probe) })
            {
                var px = bmp.GetPixel(cx, cy);
                if (px.A < 200) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Neutral generic tile for truly unresolvable apps. No letters, no monogram.</summary>
    public static Bitmap GenericIcon()
    {
        const int s = 256;
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = RoundedRect(new Rectangle(8, 8, s - 16, s - 16), 56);
        using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
            new Rectangle(8, 8, s - 16, s - 16),
            Color.FromArgb(255, 96, 96, 104), Color.FromArgb(255, 52, 52, 60), 45f);
        g.FillPath(brush, path);
        // Subtle app-grid glyph: four small rounded squares, no text.
        using var tile = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
        int t = 44, gap = 18, x0 = s / 2 - t - gap / 2, y0 = s / 2 - t - gap / 2;
        foreach (var (x, y) in new[] { (x0, y0), (x0 + t + gap, y0), (x0, y0 + t + gap), (x0 + t + gap, y0 + t + gap) })
        {
            using var r = RoundedRect(new Rectangle(x, y, t, t), 12);
            g.FillPath(tile, r);
        }
        return bmp;
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
