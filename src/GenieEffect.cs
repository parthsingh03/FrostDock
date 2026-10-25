using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace FrostDock;

/// <summary>
/// v1.5 genie minimize/restore — MINIMAL SAFE SCOPE ONLY.
/// Only a DOCK-CLICK minimize/restore gets the effect. The native titlebar
/// "-" button is untouched (standard Windows behavior).
///
/// What it is: a 150-row GDI+ HORIZONTAL strip-warp (~280 ms) played on a
/// click-through UpdateLayeredWindow overlay. The window bitmap is
/// downscaled ONCE to 640x360, then each row warps toward (minimize) or out
/// of (restore) the dock icon's screen point. This is a stylized strip-warp
/// approximation of the macOS genie curve, not a pixel-exact replica.
///
/// HARD RULES (never relax these):
/// - NO hooks, NO WH_CBT, NO DLL injection, NO cross-process code of any
///   kind. The effect only ever touches windows through ShowWindow,
///   PrintWindow, GetWindowRect/Placement and DwmSetWindowAttribute.
/// - Minimize ALWAYS uses SW_MINIMIZE, restore uses SW_RESTORE — never
///   SW_HIDE, so a window can never be lost.
/// - The native animation is suppressed per-window with
///   DWMWA_TRANSITIONS_FORCEDISABLED only while an effect is in flight or
///   while the window sits minimized after an effected minimize (so the
///   restore effect doesn't double up with the native one). The flag is
///   cleared on restore and on clean exit (backstop). ACCEPTED CAVEAT: a
///   Task Manager kill while a window is minimized-after-effect (or during
///   the 280 ms effect) leaves that window's transitions flag set —
///   cosmetic only, not data loss; the user can restore it by re-running
///   the app and restoring the window from the dock, or by restarting.
/// - Any effect failure degrades to a plain minimize/restore: the window
///   action itself must never fail because the animation did.
/// </summary>
internal static class GenieEffect
{
    private const int Rows = 150;
    private const int DurationMs = 280;
    private const int FrameMs = 16;
    private const int SnapW = 640;
    private const int SnapH = 360;

    // Windows whose transition-suppression flag is currently set.
    private static readonly HashSet<IntPtr> _suppressed = new();
    private static readonly object _gate = new();

    private static void SuppressTransitions(IntPtr hWnd)
    {
        try
        {
            int on = 1;
            if (NativeMethods.DwmSetWindowAttribute(hWnd,
                    NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, ref on, 4) == 0)
            {
                lock (_gate) { _suppressed.Add(hWnd); }
            }
        }
        catch { /* suppression is cosmetic; the effect still plays */ }
    }

    private static void ClearTransitions(IntPtr hWnd)
    {
        try
        {
            int off = 0;
            NativeMethods.DwmSetWindowAttribute(hWnd,
                NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, ref off, 4);
        }
        catch { }
        lock (_gate) { _suppressed.Remove(hWnd); }
    }

    /// <summary>Backstop: clear the suppression flag on every tracked window (clean exit).</summary>
    public static void RestoreAllTransitions()
    {
        IntPtr[] copy;
        lock (_gate) { copy = _suppressed.ToArray(); _suppressed.Clear(); }
        foreach (var h in copy)
        {
            try
            {
                int off = 0;
                NativeMethods.DwmSetWindowAttribute(h, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, ref off, 4);
            }
            catch { }
        }
    }

    /// <summary>
    /// Dock-click minimize with genie. If no live capture is available the
    /// window simply minimizes with no effect (and no flag is set).
    /// </summary>
    public static void Minimize(IntPtr hWnd, Point dockIconCenter)
    {
        try
        {
            if (hWnd == IntPtr.Zero) return;
            if (!NativeMethods.GetWindowRect(hWnd, out var rc)) { PlainMinimize(hWnd); return; }
            var winRect = Rectangle.FromLTRB(rc.left, rc.top, rc.right, rc.bottom);
            if (winRect.Width <= 0 || winRect.Height <= 0) { PlainMinimize(hWnd); return; }
            // NOTE: no `using` here — PlayWarp owns the bitmap and disposes
            // it when the async effect finishes (disposing at method exit
            // would kill the animation on its first frame).
            var snap = Capture(hWnd, winRect);
            if (snap == null) { PlainMinimize(hWnd); return; }
            // Flag stays set while minimized (cleared by Restore); the
            // native minimize animation is suppressed for the effect.
            SuppressTransitions(hWnd);
            try
            {
                PlayWarp(snap, winRect, dockIconCenter, reverse: false, onDone: () =>
                {
                    try { NativeMethods.ShowWindow(hWnd, NativeMethods.SW_MINIMIZE); } catch { }
                });
            }
            catch
            {
                // Effect couldn't even start (allocation failure): don't
                // leave the flag set — plain minimize instead.
                snap.Dispose();
                ClearTransitions(hWnd);
                PlainMinimize(hWnd);
            }
        }
        catch { PlainMinimize(hWnd); }
    }

    /// <summary>Dock-click restore with genie (reverse warp out of the dock point).</summary>
    public static void Restore(IntPtr hWnd, Point dockIconCenter)
    {
        try
        {
            if (hWnd == IntPtr.Zero) return;
            var placement = new NativeMethods.WINDOWPLACEMENT { length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
            if (!NativeMethods.GetWindowPlacement(hWnd, ref placement)) { PlainRestore(hWnd); return; }
            var winRect = Rectangle.FromLTRB(
                placement.rcNormalPosition.left, placement.rcNormalPosition.top,
                placement.rcNormalPosition.right, placement.rcNormalPosition.bottom);
            if (winRect.Width <= 0 || winRect.Height <= 0) { PlainRestore(hWnd); return; }
            // Minimized windows usually don't paint: try a live capture,
            // otherwise warp a frosted glass panel (predictable, never blank).
            // NOTE: no `using` — ownership passes to PlayWarp (see Minimize).
            var snap = Capture(hWnd, winRect);
            var art = snap != null && !IsBlank(snap)
                ? new Bitmap(snap)
                : GlassPanel();
            snap?.Dispose();
            SuppressTransitions(hWnd);
            try
            {
                PlayWarp(art, winRect, dockIconCenter, reverse: true, onDone: () =>
                {
                    try
                    {
                        NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
                        NativeMethods.SetForegroundWindow(hWnd);
                    }
                    catch { }
                    ClearTransitions(hWnd);
                });
            }
            catch
            {
                art.Dispose();
                ClearTransitions(hWnd);
                PlainRestore(hWnd);
            }
        }
        catch { PlainRestore(hWnd); }
    }

    private static void PlainMinimize(IntPtr hWnd)
    {
        try { NativeMethods.ShowWindow(hWnd, NativeMethods.SW_MINIMIZE); } catch { }
    }

    private static void PlainRestore(IntPtr hWnd)
    {
        try
        {
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
            NativeMethods.SetForegroundWindow(hWnd);
        }
        catch { }
    }

    /// <summary>
    /// Captures a window via PrintWindow (PW_RENDERFULLCONTENT) and
    /// downscales ONCE to 640x360. Null when the capture fails.
    /// </summary>
    private static Bitmap? Capture(IntPtr hWnd, Rectangle winRect)
    {
        Bitmap? full = null;
        try
        {
            full = new Bitmap(winRect.Width, winRect.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(full))
            {
                IntPtr hdc = g.GetHdc();
                try
                {
                    if (!NativeMethods.PrintWindow(hWnd, hdc, NativeMethods.PW_RENDERFULLCONTENT))
                        return null;
                }
                finally { g.ReleaseHdc(hdc); }
            }
            var small = new Bitmap(SnapW, SnapH, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, new Rectangle(0, 0, SnapW, SnapH));
            }
            return small;
        }
        catch { return null; }
        finally { full?.Dispose(); }
    }

    private static bool IsBlank(Bitmap bmp)
    {
        try
        {
            int w = bmp.Width, h = bmp.Height;
            foreach (var (x, y) in new[] { (w / 2, h / 2), (w / 4, h / 4), (3 * w / 4, h / 4), (w / 4, 3 * h / 4), (3 * w / 4, 3 * h / 4) })
            {
                var px = bmp.GetPixel(x, y);
                if (px.R > 8 || px.G > 8 || px.B > 8) return false;
            }
            return true;
        }
        catch { return true; }
    }

    /// <summary>Frosted glass panel used when a minimized window has no live capture.</summary>
    private static Bitmap GlassPanel()
    {
        var bmp = new Bitmap(SnapW, SnapH, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(new Rectangle(4, 4, SnapW - 8, SnapH - 8), 24);
        using var fill = new SolidBrush(Color.FromArgb(215, 38, 38, 46));
        g.FillPath(fill, path);
        using var edge = new Pen(Color.FromArgb(160, 255, 255, 255), 2f);
        g.DrawPath(edge, path);
        using var gloss = new System.Drawing.Drawing2D.LinearGradientBrush(
            new Rectangle(4, 4, SnapW - 8, 60),
            Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255),
            LinearGradientMode.Vertical);
        g.FillRectangle(gloss, new Rectangle(4, 4, SnapW - 8, 60));
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

    /// <summary>
    /// Plays the strip-warp on a click-through layered overlay covering the
    /// primary screen (the dock lives there), then runs onDone. The warp is
    /// HORIZONTAL strips: each row's collapse speed grows toward the bottom
    /// rows (nearest a bottom dock), so the sheet appears sucked into the
    /// dock point. Reverse plays the same warp backwards (restore).
    /// </summary>
    private static void PlayWarp(Bitmap art, Rectangle winRect, Point dockCenter, bool reverse, Action onDone)
    {
        var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        var overlay = new OverlayForm(screen);
        // NOTE: Format32bppArgb (not PArgb) — GetHbitmap drops the alpha of
        // premultiplied bitmaps, which would make the overlay fully opaque.
        var frame = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppArgb);
        var timer = new System.Windows.Forms.Timer { Interval = FrameMs };
        DateTime t0 = DateTime.UtcNow;
        bool finished = false;

        void Finish()
        {
            if (finished) return;
            finished = true;
            try { timer.Stop(); } catch { }
            try { timer.Dispose(); } catch { }
            try { overlay.Close(); overlay.Dispose(); } catch { }
            try { frame.Dispose(); } catch { }
            // The effect owns the artwork bitmap: release it only now that
            // no more frames will read it.
            try { art.Dispose(); } catch { }
            try { onDone(); } catch { }
        }

        timer.Tick += (_, _) =>
        {
            try
            {
                double t = Math.Clamp((DateTime.UtcNow - t0).TotalMilliseconds / DurationMs, 0, 1);
                // Collapse progress: minimize 0→1, restore 1→0 (same warp backwards).
                double c = reverse ? 1 - t : t;
                // Alpha: minimize fades out in the last 20%, restore fades in first 20%.
                byte alpha = reverse
                    ? (byte)Math.Round(255 * Math.Clamp(t / 0.2, 0, 1))
                    : (byte)Math.Round(255 * (1 - Math.Clamp((t - 0.8) / 0.2, 0, 1)));
                RenderFrame(frame, art, winRect, screen.Location, dockCenter, c);
                overlay.Push(frame, alpha);
                if (t >= 1) Finish();
            }
            catch { Finish(); }
        };
        try
        {
            overlay.Show();
            timer.Start();
        }
        catch { Finish(); }
    }

    private static void RenderFrame(Bitmap frame, Bitmap art, Rectangle winRect, Point screenOrigin, Point dockCenter, double c)
    {
        using var g = Graphics.FromImage(frame);
        g.Clear(Color.Transparent);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        double cx0 = winRect.X + winRect.Width / 2.0 - screenOrigin.X;
        double dockX = dockCenter.X - screenOrigin.X;
        double dockY = dockCenter.Y - screenOrigin.Y;
        double rowH0 = (double)winRect.Height / Rows;
        double srcRowH = (double)SnapH / Rows;
        for (int r = 0; r < Rows; r++)
        {
            // Bottom rows (nearest the dock) collapse faster: exponent
            // shrinks with f, so pow(c, e) grows faster for larger f, and
            // pow(1, e) == 1 for every row — all rows meet at the dock point.
            double f = (r + 0.5) / Rows;
            double k = Math.Pow(Math.Clamp(c, 0, 1), 1.0 - 0.6 * f);
            double rowY0 = winRect.Y - screenOrigin.Y + r * rowH0;
            double y = rowY0 + (dockY - rowY0) * k;
            double cx = cx0 + (dockX - cx0) * k;
            double w = winRect.Width * (1 - 0.95 * k);
            double h = Math.Max(1, rowH0 * (1 - 0.45 * k));
            if (w < 0.5) continue;
            int sy = (int)Math.Round(r * srcRowH);
            int sh = (int)Math.Min(Math.Ceiling(srcRowH) + 1, SnapH - sy);
            if (sh <= 0) continue;
            g.DrawImage(art,
                new RectangleF((float)(cx - w / 2), (float)y, (float)w, (float)h),
                new RectangleF(0, sy, SnapW, sh),
                GraphicsUnit.Pixel);
        }
    }

    /// <summary>Borderless click-through layered window driven by UpdateLayeredWindow.</summary>
    private sealed class OverlayForm : Form
    {
        public OverlayForm(Rectangle bounds)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // WS_EX_LAYERED (per-pixel alpha) + WS_EX_TRANSPARENT
                // (click-through) + WS_EX_TOOLWINDOW (no taskbar/alt-tab).
                cp.ExStyle |= 0x00080000 | 0x00000020 | 0x00000080;
                return cp;
            }
        }

        public void Push(Bitmap frame, byte alpha)
        {
            IntPtr hdcScreen = IntPtr.Zero;
            IntPtr hdcMem = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr hOld = IntPtr.Zero;
            try
            {
                hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
                hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
                hBitmap = frame.GetHbitmap(Color.FromArgb(0, 0, 0, 0));
                hOld = NativeMethods.SelectObject(hdcMem, hBitmap);
                var pptDst = new NativeMethods.POINT { x = Location.X, y = Location.Y };
                var psize = new NativeMethods.SIZE { cx = Width, cy = Height };
                var pptSrc = new NativeMethods.POINT { x = 0, y = 0 };
                var blend = new NativeMethods.BLENDFUNCTION
                {
                    BlendOp = NativeMethods.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = alpha,
                    AlphaFormat = NativeMethods.AC_SRC_ALPHA
                };
                NativeMethods.UpdateLayeredWindow(Handle, hdcScreen, ref pptDst, ref psize,
                    hdcMem, ref pptSrc, 0, ref blend, NativeMethods.ULW_ALPHA);
            }
            catch { /* a dropped frame never kills the effect */ }
            finally
            {
                if (hOld != IntPtr.Zero) { try { NativeMethods.SelectObject(hdcMem, hOld); } catch { } }
                if (hBitmap != IntPtr.Zero) { try { NativeMethods.DeleteObject(hBitmap); } catch { } }
                if (hdcMem != IntPtr.Zero) { try { NativeMethods.DeleteDC(hdcMem); } catch { } }
                if (hdcScreen != IntPtr.Zero) { try { NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen); } catch { } }
            }
        }
    }
}
