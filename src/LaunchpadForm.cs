using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace FrostDock;

/// <summary>
/// v1.2 Launchpad: a full-screen frosted overlay (blurred wallpaper + dark
/// tint) showing a grid of Start Menu apps as squircle tiles with white
/// name labels below (macOS style). A search field at the top filters the
/// grid as you type. Click an app to launch it and close; Esc or a click on
/// empty space closes without launching. Paged with arrow keys / wheel /
/// dots when the grid overflows one screen.
/// Icons come from the same discovery path as Quick Search (Start Menu
/// .lnk files) resolved through ShellIcons (IShellItemImageFactory 256px)
/// and rendered with the shared SquircleTiles renderer, so Launchpad tiles
/// match the dock tiles exactly. Icons load on first open and are cached
/// for the session (first open can take a few seconds on large menus).
/// Opened from the dock's Launchpad icon, the dock menu, or F4.
/// </summary>
internal sealed class LaunchpadForm : Form
{
    private const int TileSize = 72;
    private const int CellW = 116;
    private const int CellH = 108;
    private const int GridTop = 140;
    private const int GridBottomPad = 60;

    private readonly TextBox _search = new();
    private readonly List<LaunchItem> _all = new();
    private List<LaunchItem> _filtered = new();
    private readonly Dictionary<string, Bitmap> _tileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Rectangle> _cellRects = new();
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

        _search.BorderStyle = BorderStyle.FixedSingle;
        _search.BackColor = Color.FromArgb(40, 40, 46);
        _search.ForeColor = Color.White;
        _search.Font = new Font("Segoe UI", 14f);
        _search.PlaceholderText = "Search";
        _search.SetBounds((Width - 400) / 2, 64, 400, 36);
        _search.TextChanged += (_, _) => { _page = 0; ApplyFilter(); Invalidate(); };
        _search.KeyDown += Search_KeyDown;
        Controls.Add(_search);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; }
            // Arrow paging must not hijack caret movement while typing.
            else if (e.KeyCode == Keys.Right && !_search.Focused) { ChangePage(1); e.Handled = true; }
            else if (e.KeyCode == Keys.Left && !_search.Focused) { ChangePage(-1); e.Handled = true; }
        };
        MouseWheel += (_, e) => ChangePage(e.Delta < 0 ? 1 : -1);
    }

    private void Search_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; }
        else if (e.KeyCode == Keys.Enter)
        {
            if (_filtered.Count > 0) Launch(_filtered[0]);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    public void ShowLaunchpad()
    {
        if (!_loaded) { LoadApps(); _loaded = true; }
        _search.Text = string.Empty;
        _page = 0;
        ApplyFilter();
        Show();
        Activate();
        _search.Focus();
        Invalidate();
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
                    if (seen.Add(name)) _all.Add(new LaunchItem(name, file));
                }
            }
            catch { }
        }
        _all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyFilter()
    {
        string q = _search.Text.Trim();
        _filtered = q.Length == 0
            ? _all.ToList()
            : _all.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        int perPage = PerPage();
        _pageCount = Math.Max(1, (int)Math.Ceiling((double)_filtered.Count / perPage));
        _page = Math.Clamp(_page, 0, _pageCount - 1);
    }

    private int PerPage()
    {
        int cols = Math.Max(1, (Width - 160) / CellW);
        int rows = Math.Max(1, (Height - GridTop - GridBottomPad) / CellH);
        return cols * rows;
    }

    private void ChangePage(int delta)
    {
        int next = Math.Clamp(_page + delta, 0, _pageCount - 1);
        if (next != _page) { _page = next; Invalidate(); }
    }

    private Bitmap GetTile(LaunchItem item)
    {
        if (_tileCache.TryGetValue(item.Path, out var tile)) return tile;
        Bitmap? icon = null;
        try
        {
            string target = ShellIcons.ResolveLinkTarget(item.Path);
            if (!string.IsNullOrWhiteSpace(target))
                icon = ShellIcons.GetImageForParsingName(target);
        }
        catch { }
        icon ??= ShellIcons.GenericIcon();
        tile = SquircleTiles.RenderTile(icon, 96);
        try { icon.Dispose(); } catch { } // tile is its own bitmap
        _tileCache[item.Path] = tile;
        return tile;
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
        // (Clicks on the search box go to the TextBox control, not here.)
        for (int i = 0; i < _cellRects.Count; i++)
        {
            if (_cellRects[i].Contains(e.Location))
            {
                int perPage = PerPage();
                int idx = _page * perPage + i;
                if (idx < _filtered.Count) Launch(_filtered[idx]);
                return;
            }
        }
        Hide();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Frosted background: blurred wallpaper stretched full-screen + tint.
        var blurred = WallpaperBlur.GetBlurredScreen();
        if (blurred != null)
        {
            try { g.DrawImage(blurred, new Rectangle(0, 0, Width, Height)); } catch { }
        }
        using (var tint = new SolidBrush(Color.FromArgb(110, 10, 10, 16)))
            g.FillRectangle(tint, new Rectangle(0, 0, Width, Height));

        // Grid.
        _cellRects.Clear();
        int cols = Math.Max(1, (Width - 160) / CellW);
        int rows = Math.Max(1, (Height - GridTop - GridBottomPad) / CellH);
        int perPage = cols * rows;
        _pageCount = Math.Max(1, (int)Math.Ceiling((double)_filtered.Count / perPage));
        _page = Math.Clamp(_page, 0, _pageCount - 1);
        int gridW = cols * CellW;
        int x0 = (Width - gridW) / 2;
        int start = _page * perPage;
        int count = Math.Min(perPage, _filtered.Count - start);
        using var labelFont = new Font("Segoe UI", 10f);
        using var labelBrush = new SolidBrush(Color.White);
        using var shadowBrush = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
        var fmt = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        for (int i = 0; i < count; i++)
        {
            int r = i / cols, c = i % cols;
            int cx = x0 + c * CellW, cy = GridTop + r * CellH;
            var cell = new Rectangle(cx, cy, CellW, CellH);
            _cellRects.Add(cell);
            var item = _filtered[start + i];
            var tileRect = new Rectangle(cx + (CellW - TileSize) / 2, cy, TileSize, TileSize);
            g.DrawImage(GetTile(item), tileRect);
            var labelRect = new Rectangle(cx, cy + TileSize + 4, CellW, 20);
            // Soft shadow under the label for readability on any wallpaper.
            g.DrawString(item.Name, labelFont, shadowBrush, new Rectangle(labelRect.X + 1, labelRect.Y + 1, labelRect.Width, labelRect.Height), fmt);
            g.DrawString(item.Name, labelFont, labelBrush, labelRect, fmt);
        }

        // Page dots (only when more than one page and not filtering).
        if (_pageCount > 1 && string.IsNullOrWhiteSpace(_search.Text))
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
