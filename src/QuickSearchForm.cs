using System.Diagnostics;

namespace FrostDock;

/// <summary>
/// Quick-search-style search: a centred frosted panel with one search box.
/// Searches Start Menu shortcuts; Enter launches the top match.
/// Opened with Alt+Space / Ctrl+Space, closed with Esc.
/// </summary>
internal sealed class QuickSearchForm : Form
{
    private readonly TextBox _search = new();
    private readonly ListBox _results = new();
    private readonly List<SearchItem> _all = new();
    private List<SearchItem> _filtered = new();

    private sealed record SearchItem(string Name, string Path);

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

        _search.BorderStyle = BorderStyle.None;
        _search.BackColor = Color.FromArgb(34, 34, 40);
        _search.ForeColor = Color.White;
        _search.Font = new Font("Segoe UI", 20f);
        _search.PlaceholderText = "Quick Search";
        _search.SetBounds(24, 18, Width - 48, 44);
        _search.TextChanged += (_, _) => ApplyFilter();
        _search.KeyDown += Search_KeyDown;
        Controls.Add(_search);

        var line = new Panel { BackColor = Color.FromArgb(80, 255, 255, 255), Height = 1 };
        line.SetBounds(24, 72, Width - 48, 1);
        Controls.Add(line);

        _results.BorderStyle = BorderStyle.None;
        _results.BackColor = Color.FromArgb(34, 34, 40);
        _results.ForeColor = Color.FromArgb(235, 235, 235);
        _results.Font = new Font("Segoe UI", 12f);
        _results.ItemHeight = 30;
        _results.SetBounds(16, 84, Width - 32, Height - 100);
        _results.DoubleClick += (_, _) => LaunchSelected();
        _results.KeyDown += Search_KeyDown;
        Controls.Add(_results);

        LoadApps();
        ApplyFilter();
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
        try { Process.Start(new ProcessStartInfo { FileName = item.Path, UseShellExecute = true }); }
        catch { }
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
