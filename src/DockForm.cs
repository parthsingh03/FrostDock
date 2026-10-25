using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FrostDock;

/// <summary>
/// FrostDock dock v1.3 (Argon top-5 scope, on the v1.2 base):
/// - NO ICON TILES: freeform icons draw directly onto the frosted pill
///   glass with a subtle drop shadow; full-bleed square artwork is clipped
///   to a squircle AFTER scaling, so hover can never turn it sharp-cornered.
/// - COSINE MAGNIFICATION: icons within 2.5 slots of the cursor scale on a
///   cosine falloff and push their neighbours outward; the pill width is
///   recomputed from the scaled widths every frame.
/// - PILL BORDER: a single 1px inner specular highlight (white, alpha 71)
///   plus the soft outer drop shadow — no double ring.
/// - v1.2 behaviour kept: wallpaper-sampled frost, bottom-flush anchoring
///   (Bounds, pill 4px above the screen bottom), Launchpad + Trash entries.
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
    private const int BottomMargin = 4;
    private const int MaxRunning = 12;
    private const int HotkeyAltSpace = 1;
    private const int HotkeyCtrlSpace = 2;
    private const int HotkeyLaunchpad = 3;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly List<DockApp> _pinned;
    private List<DockApp> _running = new();
    private List<DockApp> _display = new();
    private readonly List<Rectangle> _iconRects = new();
    private readonly Dictionary<string, Bitmap> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<int> _sepXs = new();
    private DockApp _launchpadApp = null!;
    private DockApp _trashApp = null!;
    private bool? _trashFull;
    private readonly ToolTip _tip = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1500 };
    private Point _mouse = new(-9999, -9999);
    private bool _mouseIn;
    private int _hoverIndex = -1;
    private bool _autoHideTaskbar;
    private int _originalTaskbarState;
    private bool _taskbarTouched;
    private Rectangle _pillRect = Rectangle.Empty;
    // v1.5: true while a Launchpad drag hovers the dock pill (drop highlight).
    private bool _dropHighlight;
    // v1.4 launch animation + user pins.
    private readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 30 };
    private int _bounceIndex = -1;
    private DateTime _bounceT0 = DateTime.MinValue;
    private string? _pendingBounce;
    private DateTime _pendingBounceSince = DateTime.MinValue;
    private readonly List<(Point Center, DateTime T0)> _flashes = new();
    private readonly HashSet<string> _userPinPaths = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? ShowSearchRequested;
    public event EventHandler? ToggleTopBarRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? LaunchpadRequested;

    public DockForm(List<DockApp> apps)
    {
        _pinned = apps;
        _launchpadApp = new DockApp
        {
            Name = "Launchpad",
            LaunchTarget = string.Empty,
            IconIsTile = true,
            SpecialAction = () => LaunchpadRequested?.Invoke(this, EventArgs.Empty)
        };
        _launchpadApp.IconBitmap = SquircleTiles.LaunchpadIcon();
        _trashApp = new DockApp
        {
            Name = "Trash",
            LaunchTarget = "shell:RecycleBinFolder",
            SpecialAction = () =>
            {
                try { Process.Start(new ProcessStartInfo { FileName = "shell:RecycleBinFolder", UseShellExecute = true }); } catch { }
            }
        };
        _trashApp.IconBitmap = LoadTrashIcon();
        _display = new List<DockApp>(_pinned) { _launchpadApp, _trashApp };
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        // No BackColor/Opacity/Region: a layered window's appearance comes
        // entirely from the bitmap pushed with UpdateLayeredWindow.
        Height = FormHeight;
        // v1.4: the context menu is built fresh on each right-click (so a
        // user-pinned icon can offer Unpin); nothing is assigned here.
        _refreshTimer.Tick += (_, _) => { RefreshRunning(); RenderDock(); };
        _refreshTimer.Start();
        _animTimer.Tick += (_, _) => AnimTick();
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

    /// <summary>
    /// v1.4: built fresh on every right-click. A user-pinned icon (dragged
    /// from Launchpad) gets an "Unpin from dock" entry on top; built-in
    /// defaults never do — they cannot be unpinned.
    /// </summary>
    private ContextMenuStrip BuildContextMenu(DockApp? target)
    {
        var menu = new ContextMenuStrip();
        if (target != null && _userPinPaths.Contains(target.LaunchTarget))
        {
            var unpin = new ToolStripMenuItem("Unpin from dock");
            unpin.Click += (_, _) => UnpinApp(target);
            menu.Items.Add(unpin);
            menu.Items.Add(new ToolStripSeparator());
        }
        var quickSearch = new ToolStripMenuItem("Quick search (Alt+Space)");
        quickSearch.Click += (_, _) => ShowSearchRequested?.Invoke(this, EventArgs.Empty);
        var launchpad = new ToolStripMenuItem("Launchpad (F4)");
        launchpad.Click += (_, _) => LaunchpadRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(launchpad);
        var topBar = new ToolStripMenuItem("Show / hide top bar");
        topBar.Click += (_, _) => ToggleTopBarRequested?.Invoke(this, EventArgs.Empty);
        var autoHide = new ToolStripMenuItem("Auto-hide Windows taskbar") { CheckOnClick = true, Checked = _autoHideTaskbar };
        autoHide.Click += (_, _) => SetAutoHideTaskbar(autoHide.Checked);
        menu.Items.Add(quickSearch);
        menu.Items.Add(topBar);
        menu.Items.Add(autoHide);
        menu.Items.Add(new ToolStripSeparator());
        var exit = new ToolStripMenuItem("Exit FrostDock");
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(exit);
        return menu;
    }

    /// <summary>Converts a user pin (Launchpad item / pinned.json entry) to a DockApp.</summary>
    public static DockApp DockAppFromPin(string name, string path)
    {
        string? aumid = null;
        string? iconPath = null;
        string? processName = null;
        if (path.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
        {
            aumid = path["shell:AppsFolder\\".Length..];
        }
        else
        {
            iconPath = path;
            try
            {
                string target = ShellIcons.ResolveLinkTarget(path);
                processName = Path.GetFileNameWithoutExtension(
                    string.IsNullOrWhiteSpace(target) ? path : target);
            }
            catch { }
        }
        var app = new DockApp
        {
            Name = name,
            LaunchTarget = path,
            AumidHint = aumid,
            IconPath = iconPath,
            ProcessName = processName
        };
        app.IconBitmap = IconHelper.GetAppIcon(app);
        return app;
    }

    /// <summary>Pins a Launchpad item to the dock (drop handler) + persists it.</summary>
    public void PinApp(LaunchpadForm.LaunchItem item)
    {
        foreach (var a in _pinned)
        {
            if (a.LaunchTarget.Equals(item.Path, StringComparison.OrdinalIgnoreCase) ||
                a.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)) return;
        }
        var app = DockAppFromPin(item.Name, item.Path);
        _pinned.Add(app);
        _userPinPaths.Add(item.Path);
        PinnedStore.Add(new PinnedEntry(item.Name, item.Path, item.Path, app.ProcessName ?? string.Empty));
        RefreshRunning();
        RenderDock();
    }

    /// <summary>Removes a user pin from the dock + the pins file. Built-in defaults ignore this.</summary>
    public void UnpinApp(DockApp app)
    {
        if (!_userPinPaths.Contains(app.LaunchTarget)) return;
        _pinned.Remove(app);
        _userPinPaths.Remove(app.LaunchTarget);
        PinnedStore.Remove(app.LaunchTarget);
        RefreshRunning();
        RenderDock();
    }

    public void SetUserPinPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths) _userPinPaths.Add(p);
    }

    /// <summary>
    /// v1.5: highlights (or clears) the dock pill as a drag-drop target.
    /// Called by Launchpad mid-drag; repaints only on change.
    /// </summary>
    public void SetDropHighlight(bool on)
    {
        if (_dropHighlight == on) return;
        _dropHighlight = on;
        RenderDock();
    }

    /// <summary>
    /// v1.5: brings the dock above Launchpad during a drag WITHOUT stealing
    /// activation (both are TopMost; plain BringToFront could steal focus
    /// from the drag). Used by the host when a Launchpad drag starts.
    /// </summary>
    public void BringToFrontNoActivate()
    {
        try { NativeMethods.BringToFrontNoActivate(Handle); } catch { }
    }

    /// <summary>Screen rectangle of the dock pill (the drag-drop target, not the whole form).</summary>
    public Rectangle PillScreenRect => _pillRect == Rectangle.Empty
        ? new Rectangle(Location, Size)
        : new Rectangle(Location.X + _pillRect.X, Location.Y + _pillRect.Y, _pillRect.Width, _pillRect.Height);

    /// <summary>Screen center of the Launchpad dock icon (zoom origin for Launchpad open).</summary>
    public Point LaunchpadIconScreenCenter
    {
        get
        {
            int i = _display.IndexOf(_launchpadApp);
            if (i >= 0 && i < _iconRects.Count)
            {
                var r = _iconRects[i];
                return new Point(Location.X + r.X + r.Width / 2, Location.Y + r.Y + r.Height / 2);
            }
            return new Point(Location.X + Width / 2, Location.Y + FormHeight - 40);
        }
    }

    /// <summary>Asks the dock to bounce an icon once its process shows up (launches from Launchpad/Quick Search).</summary>
    public void RequestBounce(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;
        _pendingBounce = processName;
        _pendingBounceSince = DateTime.UtcNow;
    }

    private void StartBounce(int index)
    {
        if (index < 0 || index >= _display.Count || index >= _iconRects.Count) return;
        _bounceIndex = index;
        _bounceT0 = DateTime.UtcNow;
        var r = _iconRects[index];
        _flashes.Add((new Point(r.X + r.Width / 2, r.Y + r.Height / 2), DateTime.UtcNow));
        _animTimer.Start();
    }

    /// <summary>
    /// 30ms animation tick: settles the bounce when the app's main window
    /// appears or after ~3s, prunes expired flashes, re-renders while any
    /// animation is alive. Stops itself when idle so it costs nothing.
    /// </summary>
    private void AnimTick()
    {
        var now = DateTime.UtcNow;
        if (_bounceIndex >= 0)
        {
            double elapsed = (now - _bounceT0).TotalMilliseconds;
            bool settled = elapsed > 3000;
            if (!settled && _bounceIndex < _display.Count)
            {
                var proc = _display[_bounceIndex].RunningProcess;
                if (proc != null && proc.MainWindowHandle != IntPtr.Zero) settled = true;
            }
            if (settled) _bounceIndex = -1;
        }
        _flashes.RemoveAll(f => (now - f.T0).TotalMilliseconds > 700);
        if (_bounceIndex < 0 && _flashes.Count == 0) { _animTimer.Stop(); return; }
        RenderDock();
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
            // Bounds-anchored now, but re-anchor anyway so the dock never
            // drifts after a taskbar state change.
            Recenter();
            RenderDock();
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
        try { NativeMethods.RegisterHotKey(Handle, HotkeyLaunchpad, 0, NativeMethods.VK_F4); } catch { }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try { NativeMethods.UnregisterHotKey(Handle, HotkeyAltSpace); } catch { }
        try { NativeMethods.UnregisterHotKey(Handle, HotkeyCtrlSpace); } catch { }
        try { NativeMethods.UnregisterHotKey(Handle, HotkeyLaunchpad); } catch { }
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HotkeyAltSpace || id == HotkeyCtrlSpace)
            {
                ShowSearchRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (id == HotkeyLaunchpad)
            {
                LaunchpadRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
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
        if (m.Msg == NativeMethods.WM_DISPLAYCHANGE || m.Msg == NativeMethods.WM_SETTINGCHANGE)
        {
            // Wallpaper, resolution or work-area change: re-anchor and
            // rebuild the blurred wallpaper cache.
            WallpaperBlur.Invalidate();
            Recenter();
            RenderDock();
            base.WndProc(ref m);
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// v1.2 bottom-flush anchor: full screen Bounds, NEVER WorkingArea, so
    /// the taskbar's state (shown / auto-hidden) can never push the dock up.
    /// The pill itself ends BottomMargin (4px) above the screen's bottom
    /// edge — a macOS-like hairline gap, visually AT the very bottom.
    /// </summary>
    public void Recenter()
    {
        var area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        Width = area.Width;
        Location = new Point(area.Left, area.Bottom - FormHeight);
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
        if (e.Button == MouseButtons.Right)
        {
            int ridx = HitTest(e.Location);
            DockApp? target = ridx >= 0 && ridx < _display.Count ? _display[ridx] : null;
            BuildContextMenu(target).Show(Cursor.Position);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        int idx = HitTest(e.Location);
        if (idx < 0 || idx >= _display.Count) return;
        var app = _display[idx];
        // v1.5 genie toggle — DOCK CLICKS ONLY (titlebar "-" untouched):
        // a running app's dock icon toggles it: foreground window →
        // genie minimize, minimized window → genie restore. Explorer
        // (always opens a new window) and special actions (Launchpad,
        // Trash) keep their v1.4 behavior; a running-but-background app
        // still just comes to the foreground below.
        if (!app.IsExplorer && app.SpecialAction == null)
        {
            var running = app.RunningProcess;
            if (running != null && running.MainWindowHandle != IntPtr.Zero)
            {
                var h = running.MainWindowHandle;
                var r = idx < _iconRects.Count ? _iconRects[idx] : Rectangle.Empty;
                var iconCenter = r == Rectangle.Empty
                    ? new Point(Location.X + Width / 2, Location.Y + FormHeight - 40)
                    : new Point(Location.X + r.X + r.Width / 2, Location.Y + r.Y + r.Height / 2);
                if (NativeMethods.IsIconic(h)) { GenieEffect.Restore(h, iconCenter); return; }
                if (NativeMethods.GetForegroundWindow() == h) { GenieEffect.Minimize(h, iconCenter); return; }
            }
        }
        // v1.4 bounce on LAUNCH only, not on focus: bounce when the app was
        // not running — plus Explorer, which always opens a new window.
        bool wasRunning = app.IsRunning;
        app.LaunchOrFocus();
        if (!wasRunning || app.IsExplorer) StartBounce(idx);
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _iconRects.Count; i++)
            if (_iconRects[i].Contains(p)) return i;
        return -1;
    }

    /// <summary>
    /// v1.3 cosine magnification: icons within 2.5 slots of the cursor X
    /// scale by 1 + maxBoost * cos(distSlots / 2.5 * PI/2), so the falloff
    /// is smooth across ~5 neighbours instead of a single-icon step, and
    /// reaches exactly 1.0 at 2.5 slots. Slot pitch = icon + gap.
    /// </summary>
    private int IconSizeAt(int index, int[] baseCenters)
    {
        if (!_mouseIn) return BaseSize;
        double pitch = BaseSize + Gap;
        double distSlots = Math.Abs(_mouse.X - baseCenters[index]) / pitch;
        if (distSlots >= 2.5) return BaseSize;
        double boost = (double)(MaxSize - BaseSize) / BaseSize;
        double scale = 1.0 + boost * Math.Cos(distSlots / 2.5 * Math.PI / 2.0);
        return (int)Math.Round(BaseSize * scale);
    }

    /// <summary>
    /// Rebuilds the running-apps section: titled top-level windows whose
    /// process is not already pinned. UWP apps (under ApplicationFrameHost)
    /// are resolved to their real app identity for icon + focus.
    /// </summary>
    private void RefreshRunning()
    {
        var pinnedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pinnedAumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in _pinned)
        {
            if (!string.IsNullOrWhiteSpace(a.ProcessName)) pinnedNames.Add(a.ProcessName);
            if (!string.IsNullOrWhiteSpace(a.AumidHint)) pinnedAumids.Add(a.AumidHint);
        }
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
                    // v1.4: a user-pinned Store app must not ALSO appear in
                    // the running section (same AUMID, two icons).
                    if (pinnedAumids.Contains(aumid)) continue;
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
            try { _iconCache[key].Dispose(); } catch { }
            _iconCache.Remove(key);
        }

        // Trash full/empty state (SHQueryRecycleBin): re-fetch the shell
        // Trash icon only when the state flips, so the icon tracks reality.
        try
        {
            if (NativeMethods.TryGetRecycleBinCount(out long items))
            {
                bool full = items > 0;
                if (_trashFull != full)
                {
                    _trashFull = full;
                    _trashApp.IconBitmap = LoadTrashIcon();
                }
            }
        }
        catch { }

        _running = list;
        _display = new List<DockApp>(_pinned);
        _display.AddRange(_running);
        _display.Add(_launchpadApp);
        _display.Add(_trashApp);

        // v1.4: a launch from Launchpad/Quick Search asked for a bounce —
        // start it once the app shows up in the display list (4s cap,
        // then the request is dropped).
        if (_pendingBounce != null)
        {
            if ((DateTime.UtcNow - _pendingBounceSince).TotalSeconds > 4) _pendingBounce = null;
            else
            {
                for (int i = 0; i < _display.Count; i++)
                {
                    string? pn = _display[i].ProcessName;
                    if (pn != null && pn.Equals(_pendingBounce, StringComparison.OrdinalIgnoreCase))
                    {
                        _pendingBounce = null;
                        StartBounce(i);
                        break;
                    }
                }
            }
        }
    }

    private static Bitmap LoadTrashIcon()
    {
        // The shell's own Recycle Bin icon (state-aware). Falls back to the
        // neutral generic tile if the shell path cannot be resolved.
        var bmp = ShellIcons.GetImageForParsingName("shell:RecycleBinFolder")
            ?? ShellIcons.GetImageForParsingName("::{645FF040-5081-101B-9F08-00AA002F954E}");
        return bmp ?? ShellIcons.GenericIcon();
    }

    /// <summary>
    /// v1.3 icon drawing — no dark-grey tile plates anywhere:
    /// - Finished tile artwork (Launchpad icon): drawn as-is; its corners
    ///   are already transparent.
    /// - Full-bleed square artwork (e.g. Settings' blue square): clipped to
    ///   a squircle path AT the drawn size, so magnification can never turn
    ///   it into a sharp-cornered square.
    /// - Freeform artwork (Folder, Chrome, Edge, Recycle Bin, ...): drawn
    ///   directly onto the frosted pill glass with a subtle drop shadow
    ///   (black, alpha 80, 2px down) made from the icon's own alpha mask.
    /// </summary>
    private static void DrawDockIcon(Graphics g, DockApp app, Rectangle rect)
    {
        var icon = app.IconBitmap ?? ShellIcons.GenericIcon();
        if (app.IconIsTile)
        {
            g.DrawImage(icon, rect);
            return;
        }
        if (ShellIcons.IsFullBleedSquare(icon))
        {
            int radius = Math.Max(1, rect.Width * 22 / 100);
            using var clip = RoundedRectPath(rect, radius);
            var state = g.Save();
            try
            {
                g.SetClip(clip);
                g.DrawImage(icon, rect);
            }
            finally { g.Restore(state); }
            return;
        }
        // Freeform: silhouette shadow 2px down, then the icon itself.
        var shadowRect = new Rectangle(rect.X, rect.Y + 2, rect.Width, rect.Height);
        using (var attrs = new ImageAttributes())
        {
            // Map RGB -> black, scale alpha to 80/255.
            var matrix = new ColorMatrix(new float[][]
            {
                new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, 80f / 255f, 0 },
                new float[] { 0, 0, 0, 0, 1 },
            });
            attrs.SetColorMatrix(matrix);
            g.DrawImage(icon, shadowRect, 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, attrs);
        }
        g.DrawImage(icon, rect);
    }

    private Bitmap GetCachedIcon(string key, Func<Bitmap> load)
    {
        if (_iconCache.TryGetValue(key, out var bmp)) return bmp;
        bmp = load();
        _iconCache[key] = bmp;
        return bmp;
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
        int runningCount = _running.Count;
        bool hasRunning = runningCount > 0;

        // Separators: one between pinned and running (when any are running),
        // one before the Launchpad/Trash group at the dock's right end.
        var sepBefore = new bool[n];
        if (hasRunning) sepBefore[pinnedCount] = true;
        sepBefore[pinnedCount + runningCount] = true;
        int sepCount = 0;
        foreach (bool b in sepBefore) if (b) sepCount++;
        int sepSpace = sepCount * (Gap + 1 + Gap);

        // Base (unmagnified) layout: centres for the magnification falloff.
        int baseWidth = PadX * 2 + n * BaseSize + (n - 1) * Gap + sepSpace;
        int basePillX = (Width - baseWidth) / 2;
        var baseCenters = new int[n];
        {
            int x = basePillX + PadX;
            for (int i = 0; i < n; i++)
            {
                if (sepBefore[i]) x += Gap + 1 + Gap;
                baseCenters[i] = x + BaseSize / 2;
                x += BaseSize + Gap;
            }
        }

        var sizes = new int[n];
        for (int i = 0; i < n; i++) sizes[i] = IconSizeAt(i, baseCenters);

        // Magnified pill geometry (bitmap coordinates only). The pill bottom
        // sits BottomMargin (4px) above the form's bottom edge, and the form
        // itself ends at the screen's bottom edge (Bounds anchor) — so the
        // pill is visually AT the very bottom, never floating ~20px up.
        int magWidth = PadX * 2;
        for (int i = 0; i < n; i++) magWidth += sizes[i] + (i > 0 ? Gap : 0);
        magWidth += sepSpace;
        int pillX = (Width - magWidth) / 2;
        int pillBottom = FormHeight - BottomMargin;
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

            // Soft drop shadow: stacked expanding rounded rects, low alpha
            // each. The bottom rows clip at the form's bottom edge (the dock
            // sits at the screen edge, like macOS) — top/side shadow reads.
            for (int i = Shadow; i >= 1; i--)
            {
                var r = new Rectangle(pill.X - i, pill.Y - i, pill.Width + i * 2, pill.Height + i * 2);
                using var path = RoundedRectPath(r, PillRadius + i);
                using var brush = new SolidBrush(Color.FromArgb(8, 0, 0, 0));
                g.FillPath(brush, path);
            }

            // Pill background: WALLPAPER-SAMPLED blur (v1.2) — a blurred copy
            // of the wallpaper region behind the pill, so wallpaper colour
            // visibly bleeds through. The tint is deliberately lighter than
            // v1.1's 140-alpha slab so the bleed-through actually reads.
            // If no wallpaper can be read, the tint alone is the fallback.
            var blurred = WallpaperBlur.GetBlurredScreen();
            if (blurred != null)
            {
                try
                {
                    var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
                    int sx = Location.X + pill.X;
                    int sy = Location.Y + pill.Y;
                    var src = new Rectangle(
                        (int)Math.Round((double)(sx - screen.Left) * blurred.Width / screen.Width),
                        (int)Math.Round((double)(sy - screen.Top) * blurred.Height / screen.Height),
                        Math.Max(1, (int)Math.Round((double)pill.Width * blurred.Width / screen.Width)),
                        Math.Max(1, (int)Math.Round((double)pill.Height * blurred.Height / screen.Height)));
                    using var clip = RoundedRectPath(pill, PillRadius);
                    g.SetClip(clip);
                    g.DrawImage(blurred, pill, src, GraphicsUnit.Pixel);
                    g.ResetClip();
                }
                catch { /* tint fallback below still paints the pill */ }
            }
            using (var bg = RoundedRectPath(pill, PillRadius))
            {
                using var fill = new SolidBrush(Color.FromArgb(80, 30, 30, 36));
                g.FillPath(fill, bg);
                // v1.3: ONE 1px inner specular highlight (white, alpha 71 =
                // rgba(255,255,255,0.28)) — the old outer dark ring + inner
                // gradient made a heavy "double-tube" outline. The soft
                // outer drop shadow above stays.
                using var highlight = new Pen(Color.FromArgb(71, 255, 255, 255), 1f);
                g.DrawPath(highlight, bg);
                // v1.4 Liquid Glass: specular gradient across the pill's
                // top edge (paint-only; blur + tint above are unchanged).
                var gs = g.Save();
                g.SetClip(bg);
                using (var gloss = new LinearGradientBrush(
                    new Rectangle(pill.X, pill.Y, pill.Width, 14),
                    Color.FromArgb(60, 255, 255, 255), Color.FromArgb(0, 255, 255, 255),
                    LinearGradientMode.Vertical))
                    g.FillRectangle(gloss, new Rectangle(pill.X, pill.Y, pill.Width, 14));
                g.Restore(gs);
                // v1.5 drop-target highlight: a bright wash + 2px outline so
                // the user can SEE the pill is ready to accept the drop.
                if (_dropHighlight)
                {
                    using var wash = new SolidBrush(Color.FromArgb(70, 255, 255, 255));
                    g.FillPath(wash, bg);
                    using var edge = new Pen(Color.FromArgb(220, 255, 255, 255), 2f);
                    g.DrawPath(edge, bg);
                }
            }

            // Icons, running dots, separators. The WHOLE icon magnifies.
            _iconRects.Clear();
            _sepXs.Clear();
            int x = pill.X + PadX;
            int iconBottom = pill.Bottom - 8;
            for (int i = 0; i < n; i++)
            {
                if (sepBefore[i])
                {
                    int sepX = x + Gap;
                    _sepXs.Add(sepX);
                    x = sepX + 1 + Gap;
                }
                int s = sizes[i];
                var rect = new Rectangle(x, iconBottom - s, s, s);
                // v1.4 launch bounce: the icon scales IN PLACE (neighbours
                // do not move — the x advance below still uses the
                // un-bounced size, so magnification geometry is untouched).
                if (i == _bounceIndex)
                {
                    double t = (DateTime.UtcNow - _bounceT0).TotalMilliseconds;
                    double decay = Math.Max(0, 1 - t / 3000.0);
                    double bounce = 1 + 0.30 * Math.Abs(Math.Sin(t / 250.0 * Math.PI)) * decay;
                    int bs = Math.Max(1, (int)Math.Round(s * bounce));
                    int cx = x + s / 2;
                    rect = new Rectangle(cx - bs / 2, iconBottom - bs, bs, bs);
                }
                _iconRects.Add(rect);
                DrawDockIcon(g, _display[i], rect);
                x += s + Gap;

                if (_display[i].IsRunning)
                {
                    int cx = rect.Left + rect.Width / 2;
                    using var dot = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
                    g.FillEllipse(dot, cx - 2, pill.Bottom - 6, 4, 4);
                }
            }

            // v1.4 launch flash: expanding fading rings from the icon.
            var flashNow = DateTime.UtcNow;
            foreach (var f in _flashes)
            {
                double age = (flashNow - f.T0).TotalMilliseconds;
                if (age > 700) continue;
                double p = age / 700.0;
                int radius = (int)Math.Round(12 + 48 * p);
                int alpha = (int)Math.Round(160 * (1 - p));
                using var pen = new Pen(Color.FromArgb(alpha, 255, 255, 255), 2f);
                g.DrawEllipse(pen, f.Center.X - radius, f.Center.Y - radius, radius * 2, radius * 2);
            }

            using (var pen = new Pen(Color.FromArgb(50, 255, 255, 255), 1f))
            {
                int y0 = pill.Top + (PillHeight - 36) / 2;
                foreach (int sepX in _sepXs)
                    g.DrawLine(pen, sepX, y0, sepX, y0 + 36);
            }
        }

        PushLayeredBitmap(bmp);
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
