using System.Diagnostics;

namespace FrostDock;

/// <summary>
/// Quick-search-style search: a centred frosted panel with one search box.
/// Searches Start Menu shortcuts; Enter launches the top match.
/// Opened with Alt+Space / Ctrl+Space, closed with Esc.
/// v1.2: macOS-style traffic lights top-left (red = close, yellow = hide,
/// green = toggle normal/expanded size). The search box shifts right so
/// the lights never overlap it.
/// </summary>
internal sealed class QuickSearchForm : Form
{
    private readonly TextBox _search = new();
    private readonly ListBox _results = new();
    private readonly Panel _line = new();
    private readonly List<SearchItem> _all = new();
    private List<SearchItem> _filtered = new();
    private bool _expanded;

    private sealed record SearchItem(string Name, string Path);

    /// <summary>Fired when an app is launched, with a process-name hint for dock bounce.</summary>
    public event EventHandler<string>? AppLaunched;

    public QuickSearchForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(34, 34, 40);
        // No Opacity: acrylic (applied in OnLoad) provides the translucency.
        Width = 640;
        Height = 420;

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + 120);

        // Traffic lights: red close, yellow hide, green size toggle.
        AddLight(16, Color.FromArgb(255, 95, 87), "Close", (_, _) => Hide());
        AddLight(32, Color.FromArgb(255, 189, 46), "Hide (app keeps running)", (_, _) => Hide());
        AddLight(48, Color.FromArgb(255, 40, 205, 65), "Toggle size", (_, _) => ToggleSize());

        _search.BorderStyle = BorderStyle.None;
        _search.BackColor = Color.FromArgb(34, 34, 40);
        _search.ForeColor = Color.White;
        _search.Font = new Font("Segoe UI", 20f);
        _search.PlaceholderText = "Quick Search";
        _search.TextChanged += (_, _) => ApplyFilter();
        _search.KeyDown += Search_KeyDown;
        Controls.Add(_search);

        _line.BackColor = Color.FromArgb(80, 255, 255, 255);
        _line.Height = 1;
        Controls.Add(_line);

        _results.BorderStyle = BorderStyle.None;
        _results.BackColor = Color.FromArgb(34, 34, 40);
        _results.ForeColor = Color.FromArgb(235, 235, 235);
        _results.Font = new Font("Segoe UI", 12f);
        _results.ItemHeight = 30;
        _results.DoubleClick += (_, _) => LaunchSelected();
        _results.KeyDown += Search_KeyDown;
        Controls.Add(_results);

        LayoutControls();
        LoadApps();
        ApplyFilter();
    }

    private void AddLight(int x, Color color, string tip, EventHandler onClick)
    {
        var light = new Panel { BackColor = Color.Transparent, Size = new Size(12, 12), Location = new Point(x, 20) };
        light.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            e.Graphics.FillEllipse(brush, 0, 0, 11, 11);
        };
        light.Click += onClick;
        new ToolTip().SetToolTip(light, tip);
        Controls.Add(light);
    }

    private void LayoutControls()
    {
        _search.SetBounds(72, 18, Width - 96, 44);
        _line.SetBounds(24, 72, Width - 48, 1);
        _results.SetBounds(16, 84, Width - 32, Height - 100);
    }

    private void ToggleSize()
    {
        _expanded = !_expanded;
        var area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        if (_expanded)
        {
            Width = Math.Min(900, area.Width - 80);
            Height = Math.Min(600, area.Height - 120);
        }
        else
        {
            Width = 640;
            Height = 420;
        }
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + 120);
        LayoutControls();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Acrylic blur: safe here because the blurred region is 100% of this
        // window's rectangle (unlike the taller dock form).
        NativeMethods.EnableAcrylic(Handle, unchecked((int)0xE6282222));
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
                    if (seen.Add(name)) _all.Add(new SearchItem(name, file));
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
            ? _all.Take(10).ToList()
            : _all.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
        _results.Items.Clear();
        foreach (var item in _filtered) _results.Items.Add("   " + item.Name);
        if (_results.Items.Count > 0) _results.SelectedIndex = 0;
    }

    private void Search_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Hide();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            LaunchSelected();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Down && ReferenceEquals(sender, _search))
        {
            if (_results.Items.Count > 0)
                _results.SelectedIndex = Math.Min(_results.SelectedIndex + 1, _results.Items.Count - 1);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Up && ReferenceEquals(sender, _search))
        {
            if (_results.Items.Count > 0)
                _results.SelectedIndex = Math.Max(_results.SelectedIndex - 1, 0);
            e.Handled = true;
        }
    }

    private void LaunchSelected()
    {
        int i = _results.SelectedIndex;
        if (i < 0 || i >= _filtered.Count) i = 0;
        if (_filtered.Count == 0) return;
        var item = _filtered[i];
        Hide();
        try
        {
            Process.Start(new ProcessStartInfo { FileName = item.Path, UseShellExecute = true });
            // v1.4: bounce hint for the dock (resolved target's exe name).
            try
            {
                string target = ShellIcons.ResolveLinkTarget(item.Path);
                string hint = Path.GetFileNameWithoutExtension(
                    string.IsNullOrWhiteSpace(target) ? item.Path : target);
                if (!string.IsNullOrWhiteSpace(hint)) AppLaunched?.Invoke(this, hint);
            }
            catch { }
        }
        catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // v1.4 Liquid Glass edge: specular top line + hairline rim around
        // the box (paint-only; the acrylic body and controls are unchanged).
        using var top = new Pen(Color.FromArgb(90, 255, 255, 255), 1f);
        e.Graphics.DrawLine(top, 0, 0, Width, 0);
        using var rim = new Pen(Color.FromArgb(60, 255, 255, 255), 1f);
        e.Graphics.DrawRectangle(rim, 0, 0, Width - 1, Height - 1);
    }

    public void ShowSearch()
    {
        _search.Text = string.Empty;
        ApplyFilter();
        Show();
        Activate();
        _search.Focus();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }
}
