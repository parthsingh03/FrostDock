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
    private Rectangle _searchPill = Rectangle.Empty;
    private bool _loaded;
    private int _page;
    private int _pageCount = 1;

    private sealed record LaunchItem(string Name, string Path);

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
            if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; }
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
    }

    public void ShowLaunchpad()
    {
        if (!_loaded) { LoadApps(); _loaded = true; }
        _query = string.Empty;
        _page = 0;
        ApplyFilter();
        Show();
        Activate();
        Invalidate();
    }

    /// <summary>True for shortcuts that are system junk, not launchable apps.</summary>
    private static bool IsJunk(string name)
    {
        string n = name.ToLowerInvariant();
        return n.StartsWith("uninstall") ||
               n.Contains("dfrgui") || n.Contains("iscsicpl") || n.Contains("odbcad32");
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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (IsJunk(name)) continue;
                    if (seen.Add(NormalizeName(name))) _all.Add(new LaunchItem(name, file));
                }
            }
            catch { }
        }
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
            string target = ShellIcons.ResolveLinkTarget(item.Path);
            if (!string.IsNullOrWhiteSpace(target))
                bmp = ShellIcons.GetImageForParsingName(target);
        }
        catch { }
        bmp ??= ShellIcons.GenericIcon();
        _iconCache[item.Path] = bmp;
        return bmp;
    }

    private void Launch(LaunchItem item)
    {
        Hide();
        try { Process.Start(new ProcessStartInfo { FileName = item.Path, UseShellExecute = true }); } catch { }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        // Clicks on cells launch; clicks anywhere else close Launchpad.
        for (int i = 0; i < _cellRects.Count; i++)
        {
            if (_cellRects[i].Contains(e.Location))
            {
                int idx = _page * PerPage + i;
                if (idx < _filtered.Count) Launch(_filtered[idx]);
                return;
            }
        }
        Hide();
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

        // Search pill: centered, 16px radius, magnifying-glass glyph +
        // "Search" placeholder (or the live query). Not a TextBox.
        _searchPill = new Rectangle((Width - 320) / 2, 64, 320, 36);
        using (var path = RoundedRectPath(_searchPill, 16))
        using (var fill = new SolidBrush(Color.FromArgb(90, 40, 40, 48)))
            g.FillPath(fill, path);
        using (var edge = RoundedRectPath(_searchPill, 16))
        using (var pen = new Pen(Color.FromArgb(71, 255, 255, 255), 1f))
            g.DrawPath(pen, edge);
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
                using var b = new SolidBrush(p == _page
                    ? Color.FromArgb(255, 255, 255, 255)
                    : Color.FromArgb(120, 255, 255, 255));
                g.FillEllipse(b, dx + p * dotGap, dy, dotR * 2, dotR * 2);
            }
        }
    }
}
