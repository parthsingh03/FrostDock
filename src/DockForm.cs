using System.Drawing.Drawing2D;

namespace FrostDock;

/// <summary>
/// The FrostDock dock: a floating, bottom-centred bar of app icons with
/// macOS-style magnification on hover and a dot under running apps.
/// All rendering is our own GDI+ code - no third-party shell involved.
/// </summary>
internal sealed class DockForm : Form
{
    private const int BaseSize = 48;
    private const int MaxSize = 78;
    private const int Gap = 10;
    private const int PadX = 14;
    private const int PadTop = 8;
    private const int DotArea = 12;
    private const int HotkeyAltSpace = 1;
    private const int HotkeyCtrlSpace = 2;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly List<DockApp> _apps;
    private readonly List<Rectangle> _iconRects = new();
    private readonly ToolTip _tip = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1500 };
    private Point _mouse = new(-9999, -9999);
    private bool _mouseIn;
    private int _hoverIndex = -1;
    private bool _autoHideTaskbar;
    private int _originalTaskbarState;
    private bool _taskbarTouched;

    public event EventHandler? ShowSearchRequested;
    public event EventHandler? ToggleTopBarRequested;
    public event EventHandler? ExitRequested;

    public DockForm(List<DockApp> apps)
    {
        _apps = apps;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(36, 36, 40);
        Opacity = 0.94;
        DoubleBuffered = true;
        Height = MaxSize + PadTop + DotArea + 6;
        Width = PadX * 2 + _apps.Count * BaseSize + (_apps.Count - 1) * Gap;
        BuildContextMenu();
        _refreshTimer.Tick += (_, _) => Invalidate();
        _refreshTimer.Start();
        Recenter();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
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
        ApplyRoundedRegion();
        try { NativeMethods.RegisterHotKey(Handle, HotkeyAltSpace, NativeMethods.MOD_ALT, NativeMethods.VK_SPACE); } catch { }
        try { NativeMethods.RegisterHotKey(Handle, HotkeyCtrlSpace, NativeMethods.MOD_CONTROL, NativeMethods.VK_SPACE); } catch { }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try { NativeMethods.UnregisterHotKey(Handle, HotkeyAltSpace); } catch { }
        try { NativeMethods.UnregisterHotKey(Handle, HotkeyCtrlSpace); } catch { }
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
        base.WndProc(ref m);
    }

    private void ApplyRoundedRegion()
    {
        using var path = RoundedRectPath(new Rectangle(0, 0, Width, Height), 24);
        Region = new Region(path);
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

    public void Recenter()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - 6);
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
            _tip.SetToolTip(this, idx >= 0 ? _apps[idx].Name : string.Empty);
        }
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouseIn = false;
        _mouse = new Point(-9999, -9999);
        _hoverIndex = -1;
        _tip.SetToolTip(this, string.Empty);
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        int idx = HitTest(e.Location);
        if (idx >= 0) _apps[idx].LaunchOrFocus();
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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        // Frosted-glass style background (solid translucent + hairline border)
        using (var bg = RoundedRectPath(new Rectangle(0, 0, Width - 1, Height - 1), 24))
        {
            using var fill = new SolidBrush(Color.FromArgb(215, 48, 48, 54));
            g.FillPath(fill, bg);
            using var pen = new Pen(Color.FromArgb(90, 255, 255, 255), 1f);
            g.DrawPath(pen, bg);
        }

        // Base layout centres (unmagnified), used for the magnification falloff
        int n = _apps.Count;
        var baseCenters = new int[n];
        for (int i = 0; i < n; i++)
            baseCenters[i] = PadX + BaseSize / 2 + i * (BaseSize + Gap);

        var sizes = new int[n];
        int total = PadX * 2;
        for (int i = 0; i < n; i++)
        {
            sizes[i] = IconSizeAt(i, baseCenters);
            total += sizes[i] + (i > 0 ? Gap : 0);
        }

        if (total != Width)
        {
            Width = total;
            ApplyRoundedRegion();
            Recenter();
        }

        _iconRects.Clear();
        int x = PadX;
        int baseline = Height - DotArea - 4;
        for (int i = 0; i < n; i++)
        {
            int s = sizes[i];
            var rect = new Rectangle(x, baseline - s, s, s);
            _iconRects.Add(rect);
            var icon = _apps[i].IconBitmap;
            if (icon != null) g.DrawImage(icon, rect);
            x += s + Gap;

            if (_apps[i].IsRunning)
            {
                int cx = rect.Left + rect.Width / 2;
                using var dot = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
                g.FillEllipse(dot, cx - 2, baseline + 3, 5, 5);
            }
        }
    }
}
