using System.Diagnostics;

namespace FrostDock;

/// <summary>
/// The FrostDock top bar: full-width, 30px tall. Left: FrostDock wordmark and the
/// active app's name (like the macOS menu bar). Right: battery and clock.
/// </summary>
internal sealed class TopBarForm : Form
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly Label _appLabel = new();
    private readonly Label _clockLabel = new();
    private readonly Label _batteryLabel = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    public TopBarForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(30, 30, 34);
        Opacity = 0.92;
        Height = 30;

        var area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        Width = area.Width;
        Location = new Point(area.Left, area.Top);

        var brand = new Label
        {
            Text = "  ◆ FrostDock",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            AutoSize = true,
            Top = 6,
            Left = 6
        };
        Controls.Add(brand);

        _appLabel.ForeColor = Color.FromArgb(220, 220, 220);
        _appLabel.Font = new Font("Segoe UI", 9.5f);
        _appLabel.AutoSize = true;
        _appLabel.Top = 6;
        _appLabel.Left = 120;
        Controls.Add(_appLabel);

        _clockLabel.ForeColor = Color.White;
        _clockLabel.Font = new Font("Segoe UI", 9.5f);
        _clockLabel.AutoSize = true;
        _clockLabel.Top = 6;
        Controls.Add(_clockLabel);

        _batteryLabel.ForeColor = Color.FromArgb(220, 220, 220);
        _batteryLabel.Font = new Font("Segoe UI", 9.5f);
        _batteryLabel.AutoSize = true;
        _batteryLabel.Top = 6;
        Controls.Add(_batteryLabel);

        _timer.Tick += (_, _) => RefreshInfo();
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

    private void RefreshInfo()
    {
        try
        {
            var now = DateTime.Now;
            _clockLabel.Text = now.ToString("ddd d MMM  HH:mm");
            _clockLabel.Left = Width - _clockLabel.Width - 12;

            var power = SystemInformation.PowerStatus;
            if (power.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery))
            {
                _batteryLabel.Text = string.Empty;
            }
            else
            {
                int pct = (int)Math.Round(power.BatteryLifePercent * 100);
                _batteryLabel.Text = power.PowerLineStatus == PowerLineStatus.Online ? $"⚡ {pct}%" : $"🔋 {pct}%";
            }
            _batteryLabel.Left = _clockLabel.Left - _batteryLabel.Width - 16;

            var fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
                if (pid != 0)
                {
                    using var p = Process.GetProcessById((int)pid);
                    string name = string.IsNullOrWhiteSpace(p.MainWindowTitle)
                        ? Friendly(p.ProcessName)
                        : $"{Friendly(p.ProcessName)}";
                    if (_appLabel.Text != name) _appLabel.Text = name;
                }
            }
        }
        catch { /* top bar must never crash the app */ }
    }

    private static string Friendly(string processName)
    {
        return processName switch
        {
            "explorer" => "File Explorer",
            "Code" => "Visual Studio Code",
            "msedge" => "Microsoft Edge",
            "chrome" => "Google Chrome",
            "WindowsTerminal" => "Terminal",
            _ => processName
        };
    }
}
