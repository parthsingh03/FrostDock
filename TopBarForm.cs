using System.Diagnostics;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;

namespace FrostDock;

/// <summary>
/// FrostDock top bar v1.3 (v1.1/v1.2 AppBar behaviour, hardened):
/// - Registered as a real Windows AppBar (SHAppBarMessage, top edge, 28px),
///   so desktop icons and maximized windows move down instead of sliding
///   underneath it. ABM_REMOVE runs on every exit path; a stale reservation
///   after a hard crash can only be cleared by an Explorer restart (the
///   known v1.1 caveat — nothing here reintroduces it: registration is
///   fully try/catch-guarded and a failed ABM_NEW no longer claims success).
/// - v1.3: ABM_NEW's return value is checked (v1.2 set "registered" even
///   on failure), and display changes re-run the workarea positioning.
/// - Acrylic blur via SetWindowCompositionAttribute (safe here: the blurred
///   region equals 100% of this window's rectangle).
/// - Fully custom-painted (ClearType): wordmark + active app + static menu
///   labels on the left; Segoe Fluent Icons status glyphs + battery % +
///   clock + a Control Center glyph on the right, with real right padding.
/// - Active-app rule: when the foreground app is FrostDock itself, or the
///   desktop, show "Desktop" (fixes the v1.0 "FrostDock FrostDock" bug).
/// - v1.2 additions: clicking the ◆ FrostDock wordmark opens a small menu
///   (About FrostDock, Exit FrostDock — both functional); clicking the
///   status cluster or the Control Center glyph raises
///   ControlCenterRequested. The other menu labels (File/Edit/View/Go/
///   Window/Help) stay labels-only, as in v1.1.
/// </summary>
internal sealed class TopBarForm : Form
{
    private const int BarHeight = 28;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly IntPtr _callbackMsg;
    private readonly ContextMenuStrip _wordmarkMenu = new();
    private bool _appBarRegistered;
    private string _appName = "Desktop";
    private string _clockText = string.Empty;
    private int _batteryPct = -1; // -1 = no battery
    private bool _charging;
    private Rectangle _wordmarkRect = Rectangle.Empty;
    private Rectangle _statusRect = Rectangle.Empty;
    private Rectangle _ccRect = Rectangle.Empty;

    public event EventHandler? ControlCenterRequested;
    public event EventHandler? ExitRequested;

    public TopBarForm()
    {
        var about = new ToolStripMenuItem("About FrostDock");
        about.Click += (_, _) => MessageBox.Show(
            "FrostDock 1.4\n\nA macOS-style dock, top bar, Quick search, Launchpad and Control Center for Windows.\nOriginal code, not affiliated with Apple.",
            "About FrostDock", MessageBoxButtons.OK, MessageBoxIcon.None);
        var exit = new ToolStripMenuItem("Exit FrostDock");
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _wordmarkMenu.Items.Add(about);
        _wordmarkMenu.Items.Add(new ToolStripSeparator());
        _wordmarkMenu.Items.Add(exit);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(30, 30, 34);
        Height = BarHeight;

        var area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        Width = area.Width;
        Location = new Point(area.Left, area.Top);

        _callbackMsg = NativeMethods.RegisterWindowMessage("FrostDockAppBarCallback");

        _timer.Tick += (_, _) => { RefreshInfo(); Invalidate(); };
        _timer.Start();
        RefreshInfo();
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

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Acrylic gradient: alpha 0xCC, RGB 30,30,34 (bytes are A,B,G,R).
        NativeMethods.EnableAcrylic(Handle, unchecked((int)0xCC221E1E));
        RegisterAppBar();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        UnregisterAppBar();
        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (_callbackMsg != IntPtr.Zero && m.Msg == _callbackMsg.ToInt32() &&
            m.WParam.ToInt32() == (int)NativeMethods.ABN_POSCHANGED)
        {
            RepositionAppBar();
            return;
        }
        if (m.Msg == NativeMethods.WM_DISPLAYCHANGE)
        {
            // Resolution / monitor change: re-reserve the workarea at the
            // new geometry instead of sitting at a stale position.
            var area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
            Width = area.Width;
            Location = new Point(area.Left, area.Top);
            RepositionAppBar();
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (_wordmarkRect.Contains(e.Location))
        {
            _wordmarkMenu.Show(this, e.Location);
            return;
        }
        if (_statusRect.Contains(e.Location) || _ccRect.Contains(e.Location))
            ControlCenterRequested?.Invoke(this, EventArgs.Empty);
    }

    public void RegisterAppBar()
    {
        if (_appBarRegistered || !IsHandleCreated) return;
        try
        {
            var data = new NativeMethods.APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
                hWnd = Handle,
                uCallbackMessage = (uint)_callbackMsg.ToInt32(),
                uEdge = NativeMethods.ABE_TOP
            };
            // v1.3: honour ABM_NEW's result — v1.2 claimed registration
            // even when the call failed, leaving the bar overlapping.
            UIntPtr result = NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref data);
            _appBarRegistered = result != UIntPtr.Zero;
            if (_appBarRegistered) RepositionAppBar();
        }
        catch { _appBarRegistered = false; }
    }

    public void UnregisterAppBar()
    {
        if (!_appBarRegistered) return;
        try
        {
            var data = new NativeMethods.APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
                hWnd = Handle
            };
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref data);
        }
        catch { }
        _appBarRegistered = false;
    }

    /// <summary>Hide the bar and give the reserved workspace back.</summary>
    public void HideBar()
    {
        UnregisterAppBar();
        Hide();
    }

    /// <summary>Show the bar and reserve the workspace again.</summary>
    public void ShowBar()
    {
        Show();
        RegisterAppBar();
    }

    private void RepositionAppBar()
    {
        if (!_appBarRegistered) return;
        try
        {
            var area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
            var data = new NativeMethods.APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
                hWnd = Handle,
                uEdge = NativeMethods.ABE_TOP,
                rc = new NativeMethods.RECT
                {
                    left = area.Left,
                    top = area.Top,
                    right = area.Right,
                    bottom = area.Top + BarHeight
                }
            };
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_QUERYPOS, ref data);
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETPOS, ref data);
            Bounds = Rectangle.FromLTRB(data.rc.left, data.rc.top, data.rc.right, data.rc.bottom);
        }
        catch { }
    }

    private void RefreshInfo()
    {
        try
        {
            _clockText = DateTime.Now.ToString("ddd MMM d h:mm tt");

            var power = SystemInformation.PowerStatus;
            if (power.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery))
            {
                _batteryPct = -1;
            }
            else
            {
                _batteryPct = (int)Math.Round(power.BatteryLifePercent * 100);
                _charging = power.PowerLineStatus == PowerLineStatus.Online;
            }

            _appName = GetActiveAppName();
        }
        catch { /* top bar must never crash the app */ }
    }

    private string GetActiveAppName()
    {
        try
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return "Desktop";
            NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == 0) return "Desktop";
            if (pid == (uint)Process.GetCurrentProcess().Id) return "Desktop";

            // Desktop windows (Progman / WorkerW) are not an "app".
            var cls = new StringBuilder(256);
            NativeMethods.GetClassName(fg, cls, cls.Capacity);
            string className = cls.ToString();
            if (className == "Progman" || className == "WorkerW") return "Desktop";

            using var p = Process.GetProcessById((int)pid);
            string pname = p.ProcessName;
            if (pname.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                // UWP foreground app: resolve the real app behind the frame host.
                if (ShellIcons.TryResolveUwpApp(fg, out _, out uint realPid) && realPid != 0)
                {
                    try { return IconHelper.FriendlyAppName(Process.GetProcessById((int)realPid).ProcessName); }
                    catch { }
                }
                return "Desktop";
            }
            if (pname.Equals("FrostDock", StringComparison.OrdinalIgnoreCase)) return "Desktop";
            return IconHelper.FriendlyAppName(pname);
        }
        catch { return "Desktop"; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using var brandFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        using var textFont = new Font("Segoe UI", 9.5f);
        using var menuFont = new Font("Segoe UI", 9.5f);
        using var glyphFont = new Font("Segoe Fluent Icons", 13f);

        // Left cluster: wordmark + active app + static menu labels.
        int x = 12;
        int y = 7;
        int wordmarkW = TextRenderer.MeasureText(g, "◆ FrostDock", brandFont).Width;
        TextRenderer.DrawText(g, "◆ FrostDock", brandFont, new Point(x, y), Color.White);
        _wordmarkRect = new Rectangle(x, 0, wordmarkW, BarHeight);
        x += wordmarkW + 16;
        TextRenderer.DrawText(g, _appName, textFont, new Point(x, y), Color.FromArgb(220, 220, 220));
        x += TextRenderer.MeasureText(g, _appName, textFont).Width + 24;
        foreach (string item in new[] { "File", "Edit", "View", "Go", "Window", "Help" })
        {
            TextRenderer.DrawText(g, item, menuFont, new Point(x, y), Color.FromArgb(190, 255, 255, 255));
            x += TextRenderer.MeasureText(g, item, menuFont).Width + 16;
        }

        // Right cluster, right-aligned with 16px padding (never crammed to the bezel).
        // Order: Wi-Fi, volume, battery, clock, then the Control Center glyph
        // at the bar's right end (macOS puts Control Center at the far right).
        const int ccW = 24;
        int clockW = TextRenderer.MeasureText(g, _clockText, textFont).Width;
        int battTextW = _batteryPct >= 0 ? TextRenderer.MeasureText(g, _batteryPct + "%", textFont).Width : 0;
        int battW = _batteryPct >= 0 ? 26 + 4 + battTextW : 0;
        int wifiW = TextRenderer.MeasureText(g, "\uE701", glyphFont).Width;
        int volW = TextRenderer.MeasureText(g, "\uE767", glyphFont).Width;
        int total = wifiW + 10 + volW + (_batteryPct >= 0 ? 14 + battW : 0) + 14 + clockW + 14 + ccW;
        int rx = Width - 16 - total;
        _statusRect = new Rectangle(rx, 0, total - 14 - ccW, BarHeight);

        TextRenderer.DrawText(g, "\uE701", glyphFont, new Point(rx, 6), Color.White);
        rx += wifiW + 10;
        TextRenderer.DrawText(g, "\uE767", glyphFont, new Point(rx, 6), Color.White);
        rx += volW + 14;

        if (_batteryPct >= 0)
        {
            // Small battery outline + proportional fill + percentage.
            var outline = new Rectangle(rx, 9, 22, 11);
            using var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 1f);
            g.DrawRectangle(pen, outline);
            g.FillRectangle(Brushes.White, new Rectangle(outline.Right + 1, outline.Top + 3, 2, 5));
            int fillW = (int)Math.Round((outline.Width - 4) * Math.Clamp(_batteryPct, 0, 100) / 100.0);
            if (fillW > 0)
            {
                using var fill = new SolidBrush(_charging ? Color.FromArgb(255, 180, 255, 180) : Color.White);
                g.FillRectangle(fill, new Rectangle(outline.X + 2, outline.Y + 2, fillW, outline.Height - 4));
            }
            rx += 26 + 4;
            TextRenderer.DrawText(g, _batteryPct + "%", textFont, new Point(rx, y), Color.FromArgb(220, 220, 220));
            rx += battTextW + 14;
        }

        TextRenderer.DrawText(g, _clockText, textFont, new Point(rx, y), Color.White);
        rx += clockW + 14;

        // Control Center glyph: two stacked pill toggles (drawn, not a font
        // glyph, so it renders identically on every machine).
        _ccRect = new Rectangle(rx, 0, ccW, BarHeight);
        int gx = rx + 2, gy = 8;
        using (var pillFill = new SolidBrush(Color.White))
            g.FillRectangle(pillFill, new Rectangle(gx, gy, 20, 6));
        using (var pillEdge = new Pen(Color.White, 1f))
            g.DrawRectangle(pillEdge, new Rectangle(gx, gy + 8, 20, 6));

        // v1.4 Liquid Glass accent: specular hairline along the bar's
        // bottom edge (paint-only; acrylic body and labels unchanged).
        using (var hairline = new Pen(Color.FromArgb(40, 255, 255, 255), 1f))
            g.DrawLine(hairline, 0, BarHeight - 1, Width, BarHeight - 1);
    }
}
