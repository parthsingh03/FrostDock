using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;

namespace FrostDock;

/// <summary>
/// v1.3 Launchpad: a full-screen frosted overlay (blurred wallpaper + dark
/// tint) showing Start Menu apps as macOS-style icons with white name
/// labels below. No dark-grey tile plates: full-bleed square artwork is
/// squircle-clipped, freeform artwork draws directly with a drop shadow —
/// the same rule as the dock.
/// - GRID: fixed 7 columns x 5 rows (35 per page), 72px icons, ~110px
///   label width so names stop truncating at 10 characters. Pages via
///   mouse wheel / arrow keys / dots at the bottom.
/// - JUNK FILTER: shortcuts matching Uninstall*, dfrgui, iscsicpl,
///   odbcad32 are dropped, and entries are deduplicated by NORMALIZED
///   display name (parentheticals + version numbers stripped), so there
///   are no 4x PowerShell / 3x Python repeats.
/// - SEARCH: a custom-painted centered rounded pill (16px radius) with a
///   magnifying-glass glyph + "Search" placeholder — not a WinForms
///   TextBox. Typing anywhere filters; Enter launches the first match.
/// Click an app to launch it and close; Esc or a click on empty space
/// closes without launching. Icons load on first open and are cached
/// for the session (first open can take a few seconds on large menus).
/// Opened from the dock's Launchpad icon, the dock menu, or F4.
/// </summary>
internal sealed class LaunchpadForm : Form
{
    private const int Cols = 7;
    private const int Rows = 5;
    private const int TileSize = 72;
    private const int CellW = 132; // ~110px label width + padding
    private const int CellH = 116;
    private const int GridTop = 140;
    private const int PerPage = Cols * Rows;

    private string _query = string.Empty;
    private readonly List<LaunchItem> _all = new();
    private List<LaunchItem> _filtered = new();
    private readonly Dictionary<string, Bitmap> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Rectangle> _cellRects = new();
    private readonly List<Rectangle> _dotRects = new();
    private Rectangle _searchPill = Rectangle.Empty;
    private bool _loaded;
    private int _page;
    private int _pageCount = 1;
    private bool _searchFocused;
    // v1.4 drag-to-pin state.
    private LaunchItem? _dragItem;
    private Point _dragStart;
    private bool _dragging;
    private bool _suppressClick;
    private Form? _ghost;
    // v1.5: last hover-over-dock state, so the pill highlight only repaints on change.
    private bool _overDock;
    private readonly System.Windows.Forms.Timer _dragTimer = new() { Interval = 30 };
    // v1.4 zoom in/out state (scale applied in OnPaint, fade via Opacity).
    private readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 16 };
    private DateTime _zoomT0 = DateTime.MinValue;
    private int _zoomDir = 1;
    private bool _zooming;
    private float _zoomScale = 1f;

    internal sealed record LaunchItem(string Name, string Path);

    /// <summary>Fired when the user drops a Launchpad cell on the dock (pin it).</summary>
    public event EventHandler<LaunchItem>? PinDropped;
    /// <summary>Fired when an app is launched, with a process-name hint for dock bounce.</summary>
    public event EventHandler<string>? AppLaunched;
    /// <summary>Set by the host: the dock's screen rectangle (drop target).</summary>
    public Func<Rectangle>? GetDockRect { get; set; }
    /// <summary>
    /// v1.5: fired when a drag starts. The host brings the dock ABOVE
    /// Launchpad (both are TopMost; Launchpad opened later, so it normally
    /// hides the dock — the reported "dock does not appear to drop").
    /// </summary>
    public event EventHandler? DragRevealDockRequested;
    /// <summary>v1.5: set by the host to highlight the dock pill while the cursor hovers the drop rect mid-drag.</summary>
    public Action<bool>? SetDockDropHighlight { get; set; }
    /// <summary>Set by the host before ShowLaunchpad: the dock icon's screen position (zoom origin).</summary>
    public Point ZoomOriginScreen = Point.Empty;

    /// <summary>Animated hide (used by the host's toggle path, so closing zooms out too).</summary>
    public void HideLaunchpad() => CloseAnimated();

    public LaunchpadForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        KeyPreview = true;

        var area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        Bounds = area;

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { CloseAnimated(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter)
            {
                if (_filtered.Count > 0) Launch(_filtered[0]);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Right) { ChangePage(1); e.Handled = true; }
            else if (e.KeyCode == Keys.Left) { ChangePage(-1); e.Handled = true; }
            else if (e.KeyCode == Keys.Back && _query.Length > 0)
            {
                _query = _query[..^1];
                _page = 0; ApplyFilter(); Invalidate();
                e.Handled = true;
            }
        };
        KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar))
            {
                _query += e.KeyChar;
                _page = 0; ApplyFilter(); Invalidate();
                e.Handled = true;
            }
        };
        MouseWheel += (_, e) => ChangePage(e.Delta < 0 ? 1 : -1);
        _dragTimer.Tick += (_, _) => DragTick();
        _animTimer.Tick += (_, _) => AnimTick();
    }

    public void ShowLaunchpad()
    {
        if (!_loaded) { LoadApps(); _loaded = true; }
        _query = string.Empty;
        _page = 0;
        _searchFocused = false;
        _suppressClick = false;
        _dragItem = null;
        _dragging = false;
        ApplyFilter();
        Show();
        Activate();
        // v1.4 zoom-in: fade + scale from 0.94 over ~200ms.
        _zooming = true;
        _zoomDir = 1;
        _zoomT0 = DateTime.UtcNow;
        _zoomScale = 0.94f;
        Opacity = 0;
        _animTimer.Start();
        Invalidate();
    }

    /// <summary>v1.4 animated close: zoom-out + fade over ~150ms, then Hide.</summary>
    private void CloseAnimated()
    {
        if (_zooming && _zoomDir == -1) return;
        _zooming = true;
        _zoomDir = -1;
        _zoomT0 = DateTime.UtcNow;
        _animTimer.Start();
    }

    private void AnimTick()
    {
        double elapsed = (DateTime.UtcNow - _zoomT0).TotalMilliseconds;
        double dur = _zoomDir == 1 ? 200 : 150;
        double p = Math.Clamp(elapsed / dur, 0, 1);
        if (_zoomDir == 1)
        {
            _zoomScale = 0.94f + 0.06f * (float)p;
            Opacity = p;
        }
        else
        {
            _zoomScale = 1f - 0.05f * (float)p;
            Opacity = 1 - p;
        }
        Invalidate();
        if (p >= 1)
        {
            _animTimer.Stop();
            _zooming = false;
            _zoomScale = 1f;
            Opacity = 1;
            if (_zoomDir == -1) Hide();
        }
    }

    /// <summary>
    /// v1.4 audited junk rule: drop ONLY true junk. Token-based for
    /// help/website/support so real apps ("Helper", "SupportAssist"
    /// is still dropped only if a token matches — when in doubt, KEEP).
    /// </summary>
    private static bool IsJunk(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.StartsWith("uninstall") || n.StartsWith("unins")) return true;
        if (n.Contains("readme") || n.Contains("documentation") || n.Contains("user guide")) return true;
        if (n.Contains("dfrgui") || n.Contains("iscsicpl") || n.Contains("odbcad32")) return true;
        foreach (var token in n.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "help" || token == "website" || token == "support" || token == "homepage") return true;
        }
        return false;
    }

    /// <summary>
    /// Normalize for dedupe: strip parentheticals ("(x86)"), version
    /// numbers ("3.14"), collapse whitespace, lowercase — so "Windows
    /// PowerShell", "Windows PowerShell (x86)" and "Windows PowerShell
    /// ISE" no longer all appear as separate entries.
    /// </summary>
    private static string NormalizeName(string name)
    {
        string n = Regex.Replace(name, @"\(.*?\)", string.Empty);
        n = Regex.Replace(n, @"\d+(\.\d+)*", string.Empty);
        n = Regex.Replace(n, @"\s+", " ").Trim().ToLowerInvariant();
        return n;
    }

    private void LoadApps()
    {
        // v1.4 three sources, deduped by (target/AUMID) then normalized name:
        // 1) Start Menu .lnk, 2) Desktop .lnk, 3) shell:AppsFolder (Store/UWP
        // apps such as WhatsApp, which have NO .lnk file and could never
        // appear before — the reported "missing apps" bug).
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool TryAdd(string name, string path, string? targetKey)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) return false;
            if (IsJunk(name)) return false;
            string key = targetKey ?? path;
            if (!seenTargets.Add(key)) return false;
            if (!seenNames.Add(NormalizeName(name))) { seenTargets.Remove(key); return false; }
            _all.Add(new LaunchItem(name, path));
            return true;
        }

        void ScanLinks(string? root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    string target = ShellIcons.ResolveLinkTarget(file);
                    TryAdd(name, file, string.IsNullOrWhiteSpace(target) ? file : target);
                }
            }
            catch { }
        }

        ScanLinks(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
        ScanLinks(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));
        ScanLinks(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        ScanLinks(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));

        // Store/UWP apps via Shell.Application COM (best-effort: if the COM
        // enumeration fails, the .lnk sources above still populate the grid).
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                try
                {
                    dynamic folder = shell.NameSpace("shell:AppsFolder");
                    try
                    {
                        foreach (dynamic item in folder.Items())
                        {
                            string name = (string)item.Name;
                            string path = (string)item.Path;
                            if (string.IsNullOrWhiteSpace(path) ||
                                !path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) continue;
                            TryAdd(name, path, path);
                        }
                    }
                    finally
                    {
                        try { System.Runtime.InteropServices.Marshal.ReleaseComObject((object)folder); } catch { }
                    }
                }
                finally
                {
                    try { System.Runtime.InteropServices.Marshal.ReleaseComObject((object)shell); } catch { }
                }
            }
        }
        catch { }

        _all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyFilter()
    {
        string q = _query.Trim();
        _filtered = q.Length == 0
            ? _all.ToList()
            : _all.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        _pageCount = Math.Max(1, (int)Math.Ceiling((double)_filtered.Count / PerPage));
        _page = Math.Clamp(_page, 0, _pageCount - 1);
    }

    private void ChangePage(int delta)
    {
        int next = Math.Clamp(_page + delta, 0, _pageCount - 1);
        if (next != _page) { _page = next; Invalidate(); }
    }

    private Bitmap GetIcon(LaunchItem item)
    {
        if (_iconCache.TryGetValue(item.Path, out var icon)) return icon;
        Bitmap? bmp = null;
        try
        {
            // v1.5 fallback chain — the generic tile is ONLY the last resort.
            // v1.4 fell straight to GenericIcon() when the factory failed,
            // which is why Claude, WhatsApp, Explorer, Control Panel, Run…
            // all showed the 4-square tile. Chain per item kind:
            //   shell:AppsFolder item → factory (with internal retries)
            //   .lnk item → factory(resolved target) → classic(target)
            //              → factory(.lnk path itself) → classic(.lnk path)
            if (item.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                bmp = ShellIcons.GetImageForParsingName(item.Path);
            }
            else
            {
                string target = ShellIcons.ResolveLinkTarget(item.Path);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    bmp = ShellIcons.GetImageForParsingName(target)
                        ?? ShellIcons.ClassicIcon(target);
                }
                bmp ??= ShellIcons.GetImageForParsingName(item.Path)
                    ?? ShellIcons.ClassicIcon(item.Path);
            }
        }
        catch { }
        bmp ??= ShellIcons.GenericIcon();
        _iconCache[item.Path] = bmp;
        return bmp;
    }

    private void Launch(LaunchItem item)
    {
        CloseAnimated();
        try
        {
            string target = item.Path;
            string? hint = null;
            if (!item.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                string resolved = ShellIcons.ResolveLinkTarget(item.Path);
                if (!string.IsNullOrWhiteSpace(resolved)) target = resolved;
                // v1.4 Explorer trap (same as the dock): a File Explorer
                // .lnk must open a new window, never focus the shell.
                if (Path.GetFileName(target).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                    Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo { FileName = item.Path, UseShellExecute = true });
                try { hint = Path.GetFileNameWithoutExtension(target); } catch { }
            }
            else
            {
                Process.Start(new ProcessStartInfo { FileName = item.Path, UseShellExecute = true });
            }
            if (!string.IsNullOrWhiteSpace(hint)) AppLaunched?.Invoke(this, hint);
        }
        catch { }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        // v1.4 drag-to-pin: remember a possible drag start on a cell; the
        // click handler below launches only if no drag actually started.
        for (int i = 0; i < _cellRects.Count; i++)
        {
            if (_cellRects[i].Contains(e.Location))
            {
                int idx = _page * PerPage + i;
                if (idx < _filtered.Count)
                {
                    _dragItem = _filtered[idx];
                    _dragStart = e.Location;
                }
                return;
            }
        }
        _dragItem = null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragItem != null && !_dragging &&
            (e.Button & MouseButtons.Left) == MouseButtons.Left)
        {
            int dx = e.Location.X - _dragStart.X, dy = e.Location.Y - _dragStart.Y;
            if (dx * dx + dy * dy > 36) StartDrag();
        }
    }

    private void StartDrag()
    {
        if (_dragItem == null) return;
        _dragging = true;
        _suppressClick = true;
        var ghost = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            TopMost = true,
            Size = new Size(72, 72),
            BackColor = Color.Magenta,
            TransparencyKey = Color.Magenta,
            StartPosition = FormStartPosition.Manual
        };
        var icon = GetIcon(_dragItem);
        ghost.Paint += (_, pe) =>
        {
            pe.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            pe.Graphics.DrawImage(icon, new Rectangle(4, 4, 64, 64));
        };
        _ghost = ghost;
        // v1.5: reveal the dock BEFORE the ghost appears — the host brings
        // the dock above Launchpad so the drop target is actually visible.
        _overDock = false;
        try { DragRevealDockRequested?.Invoke(this, EventArgs.Empty); } catch { }
        var pos = Cursor.Position;
        ghost.Location = new Point(pos.X - 36, pos.Y - 36);
        ghost.Show();
        _dragTimer.Start();
    }

    private void DragTick()
    {
        if (!_dragging || _ghost == null) { _dragTimer.Stop(); return; }
        // Mouse released (or left button gone): drop or cancel.
        if ((Control.MouseButtons & MouseButtons.Left) != MouseButtons.Left)
        {
            _dragTimer.Stop();
            var item = _dragItem;
            var pos = Cursor.Position;
            try { _ghost.Hide(); _ghost.Dispose(); } catch { }
            _ghost = null;
            _dragging = false;
            _dragItem = null;
            // v1.5: always clear the pill highlight when the drag ends.
            if (_overDock) { _overDock = false; try { SetDockDropHighlight?.Invoke(false); } catch { } }
            bool dropped = false;
            if (item != null && GetDockRect != null)
            {
                try
                {
                    if (GetDockRect().Contains(pos))
                    {
                        dropped = true;
                        PinDropped?.Invoke(this, item);
                        CloseAnimated();
                    }
                }
                catch { }
            }
            // v1.5: on cancel, Launchpad returns above the dock (the drag
            // had brought the dock forward); on drop, CloseAnimated already
            // closes Launchpad, so nothing to restore.
            if (!dropped)
            {
                try { NativeMethods.BringToFrontNoActivate(Handle); } catch { }
            }
            return;
        }
        var cur = Cursor.Position;
        // v1.5 hover feedback: highlight the dock pill while the cursor is
        // over the drop rect, so the user can SEE where to release.
        try
        {
            bool over = GetDockRect != null && GetDockRect().Contains(cur);
            if (over != _overDock)
            {
                _overDock = over;
                SetDockDropHighlight?.Invoke(over);
            }
        }
        catch { }
        _ghost.Location = new Point(cur.X - 36, cur.Y - 36);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_suppressClick) { _suppressClick = false; return; }
        if (e.Button != MouseButtons.Left) return;
        // v1.4 FIX: the search pill is painted text, not a TextBox — but a
        // click on it must NEVER close Launchpad (the reported bug). It
        // focuses the form so typing filters, and stays open.
        if (_searchPill.Contains(e.Location))
        {
            Focus();
            Activate();
            _searchFocused = true;
            Invalidate();
            return;
        }
        // Pager dots jump to their page; they never close either.
        for (int p = 0; p < _dotRects.Count; p++)
        {
            if (_dotRects[p].Contains(e.Location)) { _page = p; Invalidate(); return; }
        }
        for (int i = 0; i < _cellRects.Count; i++)
        {
            if (_cellRects[i].Contains(e.Location))
            {
                int idx = _page * PerPage + i;
                if (idx < _filtered.Count) Launch(_filtered[idx]);
                return;
            }
        }
        CloseAnimated();
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

    /// <summary>Same icon rule as the dock: no tile plates.</summary>
    private static void DrawIcon(Graphics g, Bitmap icon, Rectangle rect)
    {
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
        var shadowRect = new Rectangle(rect.X, rect.Y + 2, rect.Width, rect.Height);
        using (var attrs = new ImageAttributes())
        {
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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Frosted background: blurred wallpaper stretched full-screen + tint.
        var blurred = WallpaperBlur.GetBlurredScreen();
        if (blurred != null)
        {
            try { g.DrawImage(blurred, new Rectangle(0, 0, Width, Height)); } catch { }
        }
        using (var tint = new SolidBrush(Color.FromArgb(110, 10, 10, 16)))
            g.FillRectangle(tint, new Rectangle(0, 0, Width, Height));

        // v1.4 zoom transform (open/close animation). Applied to the pill,
        // grid and dots only — never to the background. Hit-test rects are
        // computed under this transform during the ~200ms animation, which
        // is too short for a click to land wrong in practice.
        if (_zooming && _zoomScale < 1f)
        {
            // v1.4: zoom from the dock icon position when the host set it,
            // otherwise from the screen center.
            float cx = ZoomOriginScreen == Point.Empty ? Width / 2f : ZoomOriginScreen.X - Location.X;
            float cy = ZoomOriginScreen == Point.Empty ? Height / 2f : ZoomOriginScreen.Y - Location.Y;
            g.TranslateTransform(cx, cy);
            g.ScaleTransform(_zoomScale, _zoomScale);
            g.TranslateTransform(-cx, -cy);
        }
        _dotRects.Clear();

        // Search pill: centered, 16px radius, magnifying-glass glyph +
        // "Search" placeholder (or the live query). Not a TextBox.
        // v1.4 Liquid Glass: blurred wallpaper region behind the pill +
        // tint + specular top edge, same material as the dock pill.
        _searchPill = new Rectangle((Width - 320) / 2, 64, 320, 36);
        using (var path = RoundedRectPath(_searchPill, 16))
        {
            var state = g.Save();
            g.SetClip(path);
            try
            {
                if (blurred != null)
                {
                    try
                    {
                        var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
                        int sx = Location.X + _searchPill.X, sy = Location.Y + _searchPill.Y;
                        var src = new Rectangle(
                            (int)Math.Round((double)(sx - screen.Left) * blurred.Width / screen.Width),
                            (int)Math.Round((double)(sy - screen.Top) * blurred.Height / screen.Height),
                            Math.Max(1, (int)Math.Round((double)_searchPill.Width * blurred.Width / screen.Width)),
                            Math.Max(1, (int)Math.Round((double)_searchPill.Height * blurred.Height / screen.Height)));
                        g.DrawImage(blurred, _searchPill, src, GraphicsUnit.Pixel);
                    }
                    catch { }
                }
                using var tintFill = new SolidBrush(Color.FromArgb(90, 40, 40, 48));
                g.FillRectangle(tintFill, _searchPill);
                using var gloss = new LinearGradientBrush(
                    new Rectangle(_searchPill.X, _searchPill.Y, _searchPill.Width, 14),
                    Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255),
                    LinearGradientMode.Vertical);
                g.FillRectangle(gloss, new Rectangle(_searchPill.X, _searchPill.Y, _searchPill.Width, 14));
            }
            finally { g.Restore(state); }
            using var pen = new Pen(Color.FromArgb(71, 255, 255, 255), 1f);
            g.DrawPath(pen, path);
        }
        using var searchFont = new Font("Segoe UI", 13f);
        using var glyphFont = new Font("Segoe Fluent Icons", 14f);
        bool empty = string.IsNullOrEmpty(_query);
        using var glyphBrush = new SolidBrush(empty
            ? Color.FromArgb(170, 200, 200, 200) : Color.White);
        using var textBrush = new SolidBrush(empty
            ? Color.FromArgb(170, 200, 200, 200) : Color.White);
        g.DrawString("\uE721", glyphFont, glyphBrush, new PointF(_searchPill.X + 12, _searchPill.Y + 7));
        g.DrawString(empty ? "Search" : _query, searchFont, textBrush,
            new PointF(_searchPill.X + 38, _searchPill.Y + 7));
        // v1.4 caret: shows typing focus after a pill click (painted pill,
        // so the caret is painted too — 2px bar after the query text).
        if (_searchFocused)
        {
            float tw = empty ? 0 : g.MeasureString(_query, searchFont).Width;
            using var caret = new SolidBrush(Color.White);
            g.FillRectangle(caret, _searchPill.X + 38 + tw + 2, _searchPill.Y + 8, 2, 20);
        }

        // Grid: fixed 7 x 5.
        _cellRects.Clear();
        _pageCount = Math.Max(1, (int)Math.Ceiling((double)_filtered.Count / PerPage));
        _page = Math.Clamp(_page, 0, _pageCount - 1);
        int gridW = Cols * CellW;
        int x0 = (Width - gridW) / 2;
        int start = _page * PerPage;
        int count = Math.Min(PerPage, _filtered.Count - start);
        using var labelFont = new Font("Segoe UI", 10f);
        using var labelBrush = new SolidBrush(Color.White);
        using var shadowBrush = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
        var fmt = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        for (int i = 0; i < count; i++)
        {
            int r = i / Cols, c = i % Cols;
            int cx = x0 + c * CellW, cy = GridTop + r * CellH;
            var cell = new Rectangle(cx, cy, CellW, CellH);
            _cellRects.Add(cell);
            var item = _filtered[start + i];
            var iconRect = new Rectangle(cx + (CellW - TileSize) / 2, cy, TileSize, TileSize);
            DrawIcon(g, GetIcon(item), iconRect);
            var labelRect = new Rectangle(cx, cy + TileSize + 4, CellW, 20);
            // Soft shadow under the label for readability on any wallpaper.
            g.DrawString(item.Name, labelFont, shadowBrush, new Rectangle(labelRect.X + 1, labelRect.Y + 1, labelRect.Width, labelRect.Height), fmt);
            g.DrawString(item.Name, labelFont, labelBrush, labelRect, fmt);
        }

        // Page dots (only when more than one page and not filtering).
        if (_pageCount > 1 && string.IsNullOrWhiteSpace(_query))
        {
            int dotGap = 16, dotR = 4;
            int totalW = _pageCount * dotGap - (dotGap - dotR * 2);
            int dx = (Width - totalW) / 2, dy = Height - 28;
            for (int p = 0; p < _pageCount; p++)
            {
                _dotRects.Add(new Rectangle(dx + p * dotGap, dy, dotR * 2, dotR * 2));
                using var b = new SolidBrush(p == _page
                    ? Color.FromArgb(255, 255, 255, 255)
                    : Color.FromArgb(120, 255, 255, 255));
                g.FillEllipse(b, dx + p * dotGap, dy, dotR * 2, dotR * 2);
            }
        }
    }
}
