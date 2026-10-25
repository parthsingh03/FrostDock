using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FrostDock;

/// <summary>
/// FrostDock dock v1.1 (Argon plan): a WS_EX_LAYERED window rendered to an
/// off-screen 32-bit alpha bitmap and pushed with UpdateLayeredWindow.
/// The Form itself is a FIXED full-width, ~140px tall surface; only the
/// bottom 64px pill is visible content, and magnified icons burst above it.
/// The Form is never resized or moved per animation frame (that caused
/// jitter in v1.0); all animation happens inside bitmap coordinates.
/// Clicks pass through empty alpha pixels via WM_NCHITTEST = HTTRANSPARENT.
/// v1.0 sins removed: Form.Region (1-bit aliased corners), TransparencyKey,
/// per-frame Width changes, 32px upscaled icons, letter tiles.
/// </summary>
internal sealed class DockForm : Form
{
    // Geometry (Argon plan): 64px pill = 8px top + 48px icon + 8px bottom.
    private const int BaseSize = 48;
    private const int MaxSize = 78;
    private const int Gap = 10;
    private const int PadX = 12;
    private const int PillHeight = 64;
    private const int PillRadius = 18;
    private const int Shadow = 12;
    private const int FormHeight = 140;
    private const int BottomMargin = 6;
    private const int MaxRunning = 12;
    private const int HotkeyAltSpace = 1;
    private const int HotkeyCtrlSpace = 2;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly List<DockApp> _pinned;
    private List<DockApp> _running = new();
    private List<DockApp> _display = new();
    private readonly List<Rectangle> _iconRects = new();
    private readonly Dictionary<string, Bitmap> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Bitmap, bool> _squareCache = new();
    private readonly ToolTip _tip = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1500 };
    private Point _mouse = new(-9999, -9999);
    private bool _mouseIn;
    private int _hoverIndex = -1;
    private bool _autoHideTaskbar;
    private int _originalTaskbarState;
    private bool _taskbarTouched;
    private Rectangle _pillRect = Rectangle.Empty;
    private Rectangle _lastBlurRect = Rectangle.Empty;
    private IntPtr _blurRegion = IntPtr.Zero;

    public event EventHandler? ShowSearchRequested;
    public event EventHandler? ToggleTopBarRequested;
    public event EventHandler? ExitRequested;

    public DockForm(List<DockApp> apps)
    {
        _pinned = apps;
        _display = new List<DockApp>(_pinned);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        // No BackColor/Opacity/Region: a layered window's appearance comes
        // entirely from the bitmap pushed with UpdateLayeredWindow.
        Height = FormHeight;
        BuildContextMenu();
        _refreshTimer.Tick += (_, _) => { RefreshRunning(); RenderDock(); };
        _refreshTimer.Start();
        Recenter();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        var quickSearch = new ToolStripMenuItem("Quick search (Alt+Space)");
        quickSearch.Click += (_, _) => ShowSearchRequested?.Invoke(this, EventArgs.Empty);
        var topBar = new ToolStripMenuItem("Show / hide top bar");
        topBar.Click += (_, _) => ToggleTopBarRequested?.Invoke(this, EventArgs.Empty);
        var autoHide = new ToolStripMenuItem("Auto-hide Windows taskbar") { CheckOnClick = true };
        autoHide.Click += (_, _) => SetAutoHideTaskbar(autoHide.Checked);
        menu.Items.Add(quickSearch);
        menu.Items.Add(topBar);
        menu.Items.Add(autoHide);
        menu.Items.Add(new ToolStripSeparator());
        var exit = new ToolStripMenuItem("Exit FrostDock");
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(exit);
        ContextMenuStrip = menu;
    }

    private void SetAutoHideTaskbar(bool on)
    {
        try
        {
            if (!_taskbarTouched)
            {
                _originalTaskbarState = NativeMethods.GetTaskbarState();
                _taskbarTouched = true;
            }
            _autoHideTaskbar = on;
            NativeMethods.SetTaskbarAutoHide(on);
        }
        catch { }
    }

    public void RestoreTaskbar()
    {
        try
        {
            if (_taskbarTouched && _autoHideTaskbar)
                NativeMethods.SetTaskbarAutoHide((_originalTaskbarState & NativeMethods.ABS_AUTOHIDE) != 0);
        }
        catch { }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Recenter();
        RefreshRunning();
        RenderDock();
        try { NativeMethods.RegisterHotKey(Handle, HotkeyAltSpace, NativeMethods.MOD_ALT, NativeMethods.VK_SPACE); } catch { }
        try { NativeMethods.RegisterHotKey(Handle, HotkeyCtrlSpace, NativeMethods.MOD_CONTROL, NativeMethods.VK_SPACE); } catch { }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try { NativeMethods.UnregisterHotKey(Handle, HotkeyAltSpace); } catch { }
        try { NativeMethods.UnregisterHotKey(Handle, HotkeyCtrlSpace); } catch { }
        if (_blurRegion != IntPtr.Zero)
        {
            try { NativeMethods.DeleteObject(_blurRegion); } catch { }
            _blurRegion = IntPtr.Zero;
        }
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY &&
            (m.WParam.ToInt32() == HotkeyAltSpace || m.WParam.ToInt32() == HotkeyCtrlSpace))
        {
            ShowSearchRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (m.Msg == NativeMethods.WM_NCHITTEST)
        {
            // Clicks on icons or the pill itself are real clicks; everything
            // else (empty alpha above the pill, shadow halo) passes through
            // to the desktop/apps below.
            int l = m.LParam.ToInt32();
            int sx = (short)(l & 0xFFFF);
            int sy = (short)((l >> 16) & 0xFFFF);
            var client = PointToClient(new Point(sx, sy));
            bool hit = _pillRect.Contains(client);
            if (!hit)
            {
                foreach (var r in _iconRects)
                {
                    if (r.Contains(client)) { hit = true; break; }
                }
            }
            m.Result = (IntPtr)(hit ? NativeMethods.HTCLIENT : NativeMethods.HTTRANSPARENT);
            return;
        }
        if (m.Msg == NativeMethods.WM_DISPLAYCHANGE)
        {
            Recenter();
            RenderDock();
            base.WndProc(ref m);
            return;
        }
        base.WndProc(ref m);
    }

    public void Recenter()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Width = area.Width;
        Location = new Point(area.Left, area.Bottom - FormHeight - BottomMargin);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.Location;
        _mouseIn = true;
        int idx = HitTest(e.Location);
        if (idx != _hoverIndex)
        {
            _hoverIndex = idx;
            _tip.SetToolTip(this, idx >= 0 && idx < _display.Count ? _display[idx].Name : string.Empty);
        }
        RenderDock();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouseIn = false;
        _mouse = new Point(-9999, -9999);
        _hoverIndex = -1;
        _tip.SetToolTip(this, string.Empty);
        RenderDock();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        int idx = HitTest(e.Location);
        if (idx >= 0 && idx < _display.Count) _display[idx].LaunchOrFocus();
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _iconRects.Count; i++)
            if (_iconRects[i].Contains(p)) return i;
        return -1;
    }

    private int IconSizeAt(int index, int[] baseCenters)
    {
        if (!_mouseIn) return BaseSize;
        double dist = Math.Abs(_mouse.X - baseCenters[index]);
        double falloff = Math.Max(0, 1 - dist / 130.0);
        falloff *= falloff;
        return BaseSize + (int)((MaxSize - BaseSize) * falloff);
    }

    /// <summary>
    /// Rebuilds the running-apps section: titled top-level windows whose
    /// process is not already pinned. UWP apps (under ApplicationFrameHost)
    /// are resolved to their real app identity for icon + focus.
    /// </summary>
    private void RefreshRunning()
    {
        var pinnedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in _pinned)
            if (!string.IsNullOrWhiteSpace(a.ProcessName)) pinnedNames.Add(a.ProcessName);
        int ownPid = Process.GetCurrentProcess().Id;
        var list = new List<DockApp>();
        var liveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == ownPid) continue;
                IntPtr h = p.MainWindowHandle;
                if (h == IntPtr.Zero) continue;
                if (string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                string pname = p.ProcessName;
                if (pinnedNames.Contains(pname)) continue;

                if (pname.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                {
                    if (!ShellIcons.TryResolveUwpApp(h, out string? family, out uint realPid) || family == null || realPid == 0)
                        continue;
                    string realName;
                    try { realName = Process.GetProcessById((int)realPid).ProcessName; }
                    catch { continue; }
                    if (pinnedNames.Contains(realName)) continue;
                    string aumid = family + "!App";
                    var app = new DockApp
                    {
                        Name = IconHelper.FriendlyAppName(realName),
                        LaunchTarget = "shell:AppsFolder\\" + aumid,
                        ProcessName = realName,
                        AumidHint = aumid
                    };
                    app.IconBitmap = GetCachedIcon(aumid, () => IconHelper.GetAppIcon(app));
                    liveKeys.Add(aumid);
                    list.Add(app);
                }
                else
                {
                    string? module = null;
                    try { module = p.MainModule?.FileName; } catch { }
                    string key = module ?? pname;
                    var app = new DockApp
                    {
                        Name = IconHelper.FriendlyAppName(pname),
                        LaunchTarget = module ?? pname,
                        ProcessName = pname,
                        IconPath = module
                    };
                    app.IconBitmap = GetCachedIcon(key, () => IconHelper.GetAppIcon(app));
                    liveKeys.Add(key);
                    list.Add(app);
                }
                if (list.Count >= MaxRunning) break;
            }
            catch { }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }

        // Prune cached icons for apps that are no longer running.
        var dead = new List<string>();
        foreach (var key in _iconCache.Keys)
            if (!liveKeys.Contains(key)) dead.Add(key);
        foreach (var key in dead)
        {
            try { _squareCache.Remove(_iconCache[key]); } catch { }
            try { _iconCache[key].Dispose(); } catch { }
            _iconCache.Remove(key);
        }

        _running = list;
        _display = new List<DockApp>(_pinned);
        _display.AddRange(_running);
    }

    private Bitmap GetCachedIcon(string key, Func<Bitmap> load)
    {
        if (_iconCache.TryGetValue(key, out var bmp)) return bmp;
        bmp = load();
        _iconCache[key] = bmp;
        return bmp;
    }

    private bool IsSquareTile(Bitmap bmp)
    {
        if (_squareCache.TryGetValue(bmp, out bool v)) return v;
        v = ShellIcons.IsFullBleedSquare(bmp);
        _squareCache[bmp] = v;
        return v;
    }

    private static GraphicsPath RoundedRectPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Renders the whole dock to an off-screen 32-bit alpha bitmap and pushes
    /// it with UpdateLayeredWindow. The Form's size/location never change here.
    /// </summary>
    private void RenderDock()
    {
        if (!IsHandleCreated || _display.Count == 0) return;
        int n = _display.Count;
        int pinnedCount = _pinned.Count;
        bool hasRunning = _running.Count > 0;
        int sepSpace = hasRunning ? Gap + 1 + Gap : 0;

        // Base (unmagnified) layout: centres for the magnification falloff.
        int baseWidth = PadX * 2 + n * BaseSize + (n - 1) * Gap + sepSpace;
        int basePillX = (Width - baseWidth) / 2;
        var baseCenters = new int[n];
        {
            int x = basePillX + PadX;
            for (int i = 0; i < n; i++)
            {
                baseCenters[i] = x + BaseSize / 2;
                x += BaseSize + Gap;
                if (hasRunning && i == pinnedCount - 1) x += Gap + 1 + Gap;
            }
        }

        var sizes = new int[n];
        for (int i = 0; i < n; i++) sizes[i] = IconSizeAt(i, baseCenters);

        // Magnified pill geometry (bitmap coordinates only).
        int magWidth = PadX * 2;
        for (int i = 0; i < n; i++) magWidth += sizes[i] + (i > 0 ? Gap : 0);
        magWidth += sepSpace;
        int pillX = (Width - magWidth) / 2;
        int pillBottom = FormHeight - Shadow - 2;
        int pillTop = pillBottom - PillHeight;
        var pill = new Rectangle(pillX, pillTop, magWidth, PillHeight);
        _pillRect = pill;

        using var bmp = new Bitmap(Width, FormHeight, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Soft drop shadow: stacked expanding rounded rects, low alpha each.
            for (int i = Shadow; i >= 1; i--)
            {
                var r = new Rectangle(pill.X - i, pill.Y - i, pill.Width + i * 2, pill.Height + i * 2);
                using var path = RoundedRectPath(r, PillRadius + i);
                using var brush = new SolidBrush(Color.FromArgb(8, 0, 0, 0));
                g.FillPath(brush, path);
            }

            // Pill background: translucent tint over the DWM-blurred region
            // (see UpdateBlurRegion). If DWM blur is unavailable, the tint's
            // own translucency still shows the desktop through softly.
            using (var bg = RoundedRectPath(pill, PillRadius))
            {
                using var fill = new SolidBrush(Color.FromArgb(140, 30, 30, 36));
                g.FillPath(fill, bg);
                using var outer = new Pen(Color.FromArgb(90, 0, 0, 0), 1f);
                g.DrawPath(outer, bg);
                var innerRect = new Rectangle(pill.X + 1, pill.Y + 1, pill.Width - 2, pill.Height - 2);
                using var innerPath = RoundedRectPath(innerRect, PillRadius - 1);
                using var grad = new LinearGradientBrush(innerRect,
                    Color.FromArgb(75, 255, 255, 255), Color.FromArgb(20, 255, 255, 255),
                    LinearGradientMode.Vertical);
                using var inner = new Pen(grad, 1f);
                g.DrawPath(inner, innerPath);
            }

            // Icons, running dots, separator.
            _iconRects.Clear();
            int x = pill.X + PadX;
            int iconBottom = pill.Bottom - 8;
            int sepX = -1;
            for (int i = 0; i < n; i++)
            {
                if (hasRunning && i == pinnedCount)
                {
                    sepX = x + Gap;
                    x = sepX + 1 + Gap;
                }
                int s = sizes[i];
                var rect = new Rectangle(x, iconBottom - s, s, s);
                _iconRects.Add(rect);
                var icon = _display[i].IconBitmap;
                if (icon != null)
                {
                    if (IsSquareTile(icon))
                    {
                        // Optical sizing: full-bleed squares draw smaller so
                        // their visual weight matches circles/freeform icons.
                        int ds = s * 42 / 48;
                        var centered = new Rectangle(rect.X + (s - ds) / 2, rect.Y + (s - ds) / 2, ds, ds);
                        g.DrawImage(icon, centered);
                    }
                    else
                    {
                        g.DrawImage(icon, rect);
                    }
                }
                x += s + Gap;

                if (_display[i].IsRunning)
                {
                    int cx = rect.Left + rect.Width / 2;
                    using var dot = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
                    g.FillEllipse(dot, cx - 2, pill.Bottom - 6, 4, 4);
                }
            }

            if (sepX >= 0)
            {
                using var pen = new Pen(Color.FromArgb(50, 255, 255, 255), 1f);
                int y0 = pill.Top + (PillHeight - 36) / 2;
                g.DrawLine(pen, sepX, y0, sepX, y0 + 36);
            }
        }

        UpdateBlurRegion(pill);
        PushLayeredBitmap(bmp);
    }

    /// <summary>
    /// Primary blur path (Argon plan, step a): real DWM blur behind, clipped
    /// to the pill region only, so a window behind the dock blurs correctly.
    /// If DWM ignores layered windows, the pill tint's own translucency is
    /// the graceful fallback (NOT an opaque slab like v1.0).
    /// Wallpaper-sampled blur is deliberately not shipped: it cannot be
    /// proven necessary from here, and it would show blurred wallpaper
    /// instead of a blurred window whenever a window sits behind the dock.
    /// </summary>
    private void UpdateBlurRegion(Rectangle pill)
    {
        if (pill == _lastBlurRect) return;
        try
        {
            var rgn = NativeMethods.CreateRoundRectRgn(pill.Left, pill.Top, pill.Right, pill.Bottom, PillRadius * 2, PillRadius * 2);
            if (rgn == IntPtr.Zero) return;
            var blur = new NativeMethods.DWM_BLURBEHIND
            {
                dwFlags = NativeMethods.DWM_BB_ENABLE | NativeMethods.DWM_BB_BLURREGION,
                fEnable = true,
                hRgnBlur = rgn,
                fTransitionOnMaximized = false
            };
            NativeMethods.DwmEnableBlurBehindWindow(Handle, ref blur);
            if (_blurRegion != IntPtr.Zero)
            {
                try { NativeMethods.DeleteObject(_blurRegion); } catch { }
            }
            _blurRegion = rgn;
            _lastBlurRect = pill;
        }
        catch { /* blur is cosmetic - the translucent tint still works */ }
    }

    /// <summary>
    /// Pushes a 32-bit alpha bitmap via UpdateLayeredWindow using a DIB
    /// section (Bitmap.GetHbitmap would drop the alpha channel).
    /// </summary>
    private void PushLayeredBitmap(Bitmap bmp)
    {
        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc = IntPtr.Zero;
        IntPtr hDib = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;
        try
        {
            screenDc = NativeMethods.GetDC(IntPtr.Zero);
            var bmi = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = bmp.Width,
                    biHeight = -bmp.Height, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };
            IntPtr bits;
            hDib = CreateDIBSection(screenDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
            if (hDib == IntPtr.Zero || bits == IntPtr.Zero) return;
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int stride = Math.Abs(data.Stride);
                var row = new byte[stride];
                for (int y = 0; y < bmp.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, stride);
                    Marshal.Copy(row, 0, IntPtr.Add(bits, y * stride), stride);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            oldObj = NativeMethods.SelectObject(memDc, hDib);
            var ptDst = new NativeMethods.POINT { x = Location.X, y = Location.Y };
            var size = new NativeMethods.SIZE { cx = bmp.Width, cy = bmp.Height };
            var ptSrc = new NativeMethods.POINT { x = 0, y = 0 };
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA
            };
            NativeMethods.UpdateLayeredWindow(Handle, screenDc, ref ptDst, ref size,
                memDc, ref ptSrc, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        catch { /* a failed push leaves the previous frame on screen */ }
        finally
        {
            if (oldObj != IntPtr.Zero && memDc != IntPtr.Zero)
            {
                try { NativeMethods.SelectObject(memDc, oldObj); } catch { }
            }
            if (hDib != IntPtr.Zero)
            {
                try { NativeMethods.DeleteObject(hDib); } catch { }
            }
            if (memDc != IntPtr.Zero)
            {
                try { NativeMethods.DeleteDC(memDc); } catch { }
            }
            if (screenDc != IntPtr.Zero)
            {
                try { NativeMethods.ReleaseDC(IntPtr.Zero, screenDc); } catch { }
            }
        }
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref NativeMethods.BITMAPINFO pbmi,
        uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);
}
