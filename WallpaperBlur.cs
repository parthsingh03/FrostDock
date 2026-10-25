using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace FrostDock;

/// <summary>
/// v1.2 frosted-glass source: the v1.1 DWM blur-region path did NOT visibly
/// frost on the user's screenshots (the pill read as a flat dark slab), so
/// the dock and Launchpad now paint a BLURRED COPY OF THE DESKTOP WALLPAPER
/// behind their surfaces instead. This guarantees wallpaper colour bleeds
/// through visibly. Known trade-off (documented, same as the v1.1 analysis):
/// when a window sits behind the dock, the pill shows blurred WALLPAPER,
/// not the blurred window — wallpaper is what the user sees 99% of the
/// time on his desktop, and it is the only blur path provable from here.
/// The blurred image is cached and rebuilt only when the wallpaper file,
/// its timestamp, or the screen size changes (cheap checks on every call),
/// or when Invalidate() is called (display / settings change).
/// </summary>
internal static class WallpaperBlur
{
    private static Bitmap? _blurred;
    private static string? _cachedPath;
    private static DateTime _cachedWriteUtc;
    private static Size _cachedScreen;

    public static void Invalidate()
    {
        try { _blurred?.Dispose(); } catch { }
        _blurred = null;
        _cachedPath = null;
    }

    /// <summary>
    /// Returns a small blurred bitmap that represents the whole primary
    /// screen (cover-scaled wallpaper, downscaled + box-blurred). Callers
    /// stretch-draw it; because the content is already blur, stretching
    /// cannot introduce visible artifacts. Null when no wallpaper can be
    /// read (callers fall back to a flat tint).
    /// </summary>
    public static Bitmap? GetBlurredScreen()
    {
        try
        {
            var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
            string? path = CurrentWallpaperPath();
            DateTime write = DateTime.MinValue;
            if (!string.IsNullOrWhiteSpace(path))
            {
                try { if (File.Exists(path)) write = File.GetLastWriteTimeUtc(path); } catch { }
            }
            if (_blurred != null && _cachedPath == path && _cachedWriteUtc == write && _cachedScreen == screen.Size)
                return _blurred;

            Invalidate();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            using var wallpaper = LoadWallpaper(path);
            if (wallpaper == null) return null;

            // Cover-scale the wallpaper to the screen (Windows "Fill" style,
            // the common case; other styles still look close enough once blurred).
            using var cover = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(cover))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                double scale = Math.Max((double)screen.Width / wallpaper.Width, (double)screen.Height / wallpaper.Height);
                int dw = (int)Math.Round(wallpaper.Width * scale);
                int dh = (int)Math.Round(wallpaper.Height * scale);
                g.DrawImage(wallpaper, new Rectangle((screen.Width - dw) / 2, (screen.Height - dh) / 2, dw, dh));
            }

            // Downscale hard (blur hides all detail anyway), then box-blur.
            int smallW = Math.Max(64, screen.Width / 6);
            int smallH = Math.Max(48, screen.Height / 6);
            var small = new Bitmap(smallW, smallH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(cover, new Rectangle(0, 0, smallW, smallH));
            }
            BoxBlur(small, radius: 6, iterations: 2);

            _blurred = small;
            _cachedPath = path;
            _cachedWriteUtc = write;
            _cachedScreen = screen.Size;
            return _blurred;
        }
        catch { return null; }
    }

    private static Bitmap? LoadWallpaper(string path)
    {
        try
        {
            // Image.FromFile locks the file; copy through a stream instead.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var img = Image.FromStream(fs);
            return new Bitmap(img);
        }
        catch { return null; }
    }

    private static string? CurrentWallpaperPath()
    {
        try
        {
            var sb = new StringBuilder(260);
            if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0))
            {
                string p = sb.ToString();
                if (!string.IsNullOrWhiteSpace(p)) return p;
            }
        }
        catch { }
        // Fallback: the registry value (slideshows keep it stale, but it is
        // better than nothing when SystemParametersInfo fails).
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return key?.GetValue("Wallpaper") as string;
        }
        catch { return null; }
    }

    /// <summary>Separable box blur on a 32bppArgb bitmap, in place.</summary>
    private static void BoxBlur(Bitmap bmp, int radius, int iterations)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int w = bmp.Width, h = bmp.Height, stride = Math.Abs(data.Stride);
            var px = new byte[stride * h];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            var tmp = new byte[px.Length];
            for (int it = 0; it < iterations; it++)
            {
                BlurPass(px, tmp, w, h, stride, radius, horizontal: true);
                BlurPass(tmp, px, w, h, stride, radius, horizontal: false);
            }
            Marshal.Copy(px, 0, data.Scan0, px.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static void BlurPass(byte[] src, byte[] dst, int w, int h, int stride, int r, bool horizontal)
    {
        // Box blur with a sliding window; radius r, window size 2r+1.
        // Implemented per channel on BGRA bytes.
        int div = 2 * r + 1;
        if (horizontal)
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                long[] sum = new long[4];
                for (int x = -r; x <= r; x++)
                {
                    int cx = Math.Clamp(x, 0, w - 1) * 4;
                    for (int c = 0; c < 4; c++) sum[c] += src[row + cx + c];
                }
                for (int x = 0; x < w; x++)
                {
                    int o = row + x * 4;
                    for (int c = 0; c < 4; c++) dst[o + c] = (byte)(sum[c] / div);
                    int xAdd = Math.Clamp(x + r + 1, 0, w - 1) * 4;
                    int xSub = Math.Clamp(x - r, 0, w - 1) * 4;
                    for (int c = 0; c < 4; c++) sum[c] += src[row + xAdd + c] - src[row + xSub + c];
                }
            }
        }
        else
        {
            for (int x = 0; x < w; x++)
            {
                long[] sum = new long[4];
                for (int y = -r; y <= r; y++)
                {
                    int cy = Math.Clamp(y, 0, h - 1) * stride + x * 4;
                    for (int c = 0; c < 4; c++) sum[c] += src[cy + c];
                }
                for (int y = 0; y < h; y++)
                {
                    int o = y * stride + x * 4;
                    for (int c = 0; c < 4; c++) dst[o + c] = (byte)(sum[c] / div);
                    int yAdd = Math.Clamp(y + r + 1, 0, h - 1) * stride + x * 4;
                    int ySub = Math.Clamp(y - r, 0, h - 1) * stride + x * 4;
                    for (int c = 0; c < 4; c++) sum[c] += src[yAdd + c] - src[ySub + c];
                }
            }
        }
    }
}
