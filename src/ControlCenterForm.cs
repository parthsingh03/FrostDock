using System.Diagnostics;
using System.Net.NetworkInformation;

namespace FrostDock;

/// <summary>
/// v1.2 Control Center: a macOS-style frosted popup below the top bar's
/// right end, opened from the status cluster / Control Center glyph.
/// HONESTY RULE (from the brief): every control either really works or is
/// clearly an indicator + Settings link — never a dead switch.
/// - Volume slider: real, via Core Audio (AudioVolume). Disabled if the
///   endpoint cannot be reached.
/// - Brightness slider: real, via WMI laptop-panel brightness
///   (BrightnessControl). Disabled with a note on machines without one.
/// - Wi-Fi: live status from NetworkInterface + a button that opens
///   Windows Wi-Fi Settings (ms-settings:network-wifi).
/// - Bluetooth: live adapter-presence check (BluetoothFindFirstRadio) +
///   a button that opens Windows Bluetooth Settings (ms-settings:bluetooth).
/// Traffic lights top-left: red = close panel, yellow = hide panel (the
/// app keeps running), green = dimmed and inert (a fixed popup has no
/// maximize state — documented in the README).
/// Closes on Esc, on deactivation (click outside), or on the glyph again.
/// </summary>
internal sealed class ControlCenterForm : Form
{
    private readonly TrackBar _volume = new();
    private readonly Label _volumePct = new();
    private readonly TrackBar _brightness = new();
    private readonly Label _brightnessPct = new();
    private readonly Label _wifiStatus = new();
    private readonly Label _btStatus = new();

    public ControlCenterForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(34, 34, 40);
        KeyPreview = true;
        Width = 320;
        Height = 268;

        int y = 0;

        // Traffic lights: red close, yellow hide, green dimmed/inert.
        AddLight(14, Color.FromArgb(255, 95, 87), "Close", (_, _) => Hide());
        AddLight(30, Color.FromArgb(255, 189, 46), "Hide", (_, _) => Hide());
        var green = new Panel { BackColor = Color.Transparent, Size = new Size(12, 12), Location = new Point(46, 14) };
        green.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Color.FromArgb(90, 40, 205, 65));
            e.Graphics.FillEllipse(brush, 0, 0, 11, 11);
        };
        new ToolTip().SetToolTip(green, "No maximize state for this panel");
        Controls.Add(green);

        var title = new Label
        {
            Text = "Control Center",
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(70, 10)
        };
        Controls.Add(title);
        y = 44;

        // Volume row.
        AddRowLabel("Volume", y);
        _volume.SetBounds(100, y - 2, 160, 28);
        _volume.Minimum = 0; _volume.Maximum = 100; _volume.TickFrequency = 10;
        _volume.BackColor = Color.FromArgb(34, 34, 40);
        _volume.Scroll += (_, _) =>
        {
            AudioVolume.TrySet(_volume.Value / 100f);
            _volumePct.Text = _volume.Value + "%";
        };
        Controls.Add(_volume);
        _volumePct.AutoSize = true;
        _volumePct.ForeColor = Color.FromArgb(220, 220, 220);
        _volumePct.Location = new Point(268, y + 2);
        Controls.Add(_volumePct);
        y += 44;

        // Brightness row.
        AddRowLabel("Brightness", y);
        _brightness.SetBounds(100, y - 2, 160, 28);
        _brightness.Minimum = 0; _brightness.Maximum = 100; _brightness.TickFrequency = 10;
        _brightness.BackColor = Color.FromArgb(34, 34, 40);
        _brightness.Scroll += (_, _) =>
        {
            BrightnessControl.TrySet((byte)_brightness.Value);
            _brightnessPct.Text = _brightness.Value + "%";
        };
        Controls.Add(_brightness);
        _brightnessPct.AutoSize = true;
        _brightnessPct.ForeColor = Color.FromArgb(220, 220, 220);
        _brightnessPct.Location = new Point(268, y + 2);
        Controls.Add(_brightnessPct);
        y += 44;

        // Wi-Fi row: live status + Settings link (never a fake toggle).
        AddRowLabel("Wi-Fi", y);
        _wifiStatus.AutoSize = true;
        _wifiStatus.ForeColor = Color.FromArgb(220, 220, 220);
        _wifiStatus.Location = new Point(100, y + 2);
        Controls.Add(_wifiStatus);
        var wifiBtn = FlatButton("Wi-Fi Settings", 100, y + 24);
        wifiBtn.Click += (_, _) => OpenSettings("ms-settings:network-wifi");
        Controls.Add(wifiBtn);
        y += 62;

        // Bluetooth row: live adapter presence + Settings link.
        AddRowLabel("Bluetooth", y);
        _btStatus.AutoSize = true;
        _btStatus.ForeColor = Color.FromArgb(220, 220, 220);
        _btStatus.Location = new Point(100, y + 2);
        Controls.Add(_btStatus);
        var btBtn = FlatButton("Bluetooth Settings", 100, y + 24);
        btBtn.Click += (_, _) => OpenSettings("ms-settings:bluetooth");
        Controls.Add(btBtn);

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
    }

    private void AddLight(int x, Color color, string tip, EventHandler onClick)
    {
        var light = new Panel { BackColor = Color.Transparent, Size = new Size(12, 12), Location = new Point(x, 14) };
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

    private void AddRowLabel(string text, int y)
    {
        var label = new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 10f),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(16, y + 2)
        };
        Controls.Add(label);
    }

    private Button FlatButton(string text, int x, int y)
    {
        return new Button
        {
            Text = text,
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(60, 60, 68),
            FlatStyle = FlatStyle.Flat,
            AutoSize = true,
            Location = new Point(x, y)
        };
    }

    private static void OpenSettings(string uri)
    {
        try { Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true }); } catch { }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Acrylic blur: safe here because the blurred region is 100% of this
        // window's rectangle (same pattern as the top bar / search box).
        NativeMethods.EnableAcrylic(Handle, unchecked((int)0xE6282222));
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

    /// <summary>Refresh live values, then show below the top bar's right end.</summary>
    public void ShowPanel()
    {
        RefreshValues();
        var area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Right - Width - 12, 34);
        Show();
        Activate();
    }

    public void TogglePanel()
    {
        if (Visible) Hide();
        else ShowPanel();
    }

    private void RefreshValues()
    {
        // Volume (real Core Audio value; disable the row if unreachable).
        if (AudioVolume.TryGet(out float level))
        {
            _volume.Enabled = true;
            _volume.Value = (int)Math.Round(level * 100);
            _volumePct.Text = _volume.Value + "%";
        }
        else
        {
            _volume.Enabled = false;
            _volumePct.Text = "n/a";
        }

        // Brightness (real WMI value; disable the row if no laptop panel).
        if (BrightnessControl.TryGet(out byte bright))
        {
            _brightness.Enabled = true;
            _brightness.Value = bright;
            _brightnessPct.Text = bright + "%";
        }
        else
        {
            _brightness.Enabled = false;
            _brightnessPct.Text = "No panel";
        }

        // Wi-Fi: live adapter + connection state, never a toggle.
        try
        {
            bool hasWifi = false, connected = false;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                hasWifi = true;
                if (ni.OperationalStatus == OperationalStatus.Up) connected = true;
            }
            _wifiStatus.Text = !hasWifi ? "No Wi-Fi adapter" : connected ? "Connected" : "Not connected";
        }
        catch { _wifiStatus.Text = "Unknown"; }

        // Bluetooth: live adapter presence (a real radio check, not a toggle).
        _btStatus.Text = NativeMethods.BluetoothAdapterPresent() ? "Adapter found" : "No adapter found";
    }
}
