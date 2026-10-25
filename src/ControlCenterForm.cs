using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.NetworkInformation;

namespace FrostDock;

/// <summary>
/// v1.3 Control Center: a macOS-style frosted popover below the top bar's
/// right end — FULLY CUSTOM-PAINTED. Zero native WinForms controls inside:
/// no TrackBar, no Button, no Label, no traffic lights (a transient
/// popover never has window chrome). Everything is drawn in OnPaint and
/// hit-tested by hand:
/// - 16px rounded corners (Region), wallpaper-sampled frosted background
///   (same approach as the dock pill) + dark tint.
/// - Sliders: custom white-pill fill inside a dark translucent track.
///   Volume is real Core Audio (AudioVolume, now with an active-endpoint
///   fallback); brightness is real WMI laptop-panel brightness.
/// - Wi-Fi / Bluetooth: rounded translucent module cards
///   (Color.FromArgb(110, 40, 40, 48), 12px radius) showing live status
///   and opening Windows Settings on click — never a fake toggle.
/// HONESTY RULE: a row that cannot reach its backend draws dimmed with
/// "n/a" / "No panel" — never a fake working slider.
/// Closes on Esc, on deactivation (click outside), or on the glyph again.
/// </summary>
internal sealed class ControlCenterForm : Form
{
    private const int CornerRadius = 16;
    private const int CardColorA = 110;

    private int _volumeValue = -1; // -1 = unreachable (n/a)
    private int _brightValue = -1; // -1 = no panel
    private string _wifiStatus = "Unknown";
    private string _btStatus = "Unknown";
    private bool _dragVolume;
    private bool _dragBright;

    private Rectangle _volTrack = Rectangle.Empty;
    private Rectangle _brightTrack = Rectangle.Empty;
    private Rectangle _wifiCard = Rectangle.Empty;
    private Rectangle _btCard = Rectangle.Empty;

    public ControlCenterForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Width = 320;
        Height = 268;
        UpdateRegion();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
    }

    private void UpdateRegion()
    {
        using var path = RoundedRectPath(new Rectangle(0, 0, Width, Height), CornerRadius);
        Region?.Dispose();
        Region = new Region(path);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateRegion();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // No acrylic here: the frost comes from the wallpaper sample drawn
        // in OnPaint (same as the dock pill), so the popover matches it.
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
        Invalidate();
    }

    public void TogglePanel()
    {
        if (Visible) Hide();
        else ShowPanel();
    }

    private void RefreshValues()
    {
        _volumeValue = AudioVolume.TryGet(out float level) ? (int)Math.Round(level * 100) : -1;
        _brightValue = BrightnessControl.TryGet(out byte bright) ? bright : -1;

        try
        {
            bool hasWifi = false, connected = false;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                hasWifi = true;
                if (ni.OperationalStatus == OperationalStatus.Up) connected = true;
            }
            _wifiStatus = !hasWifi ? "No Wi-Fi adapter" : connected ? "Connected" : "Not connected";
        }
        catch { _wifiStatus = "Unknown"; }

        _btStatus = NativeMethods.BluetoothAdapterPresent() ? "Adapter found" : "No adapter found";
    }

    private static void OpenSettings(string uri)
    {
        try { Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true }); } catch { }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (_volumeValue >= 0 && _volTrack.Contains(e.Location))
        {
            _dragVolume = true;
            ApplySlider(e.Location, isVolume: true);
        }
        else if (_brightValue >= 0 && _brightTrack.Contains(e.Location))
        {
            _dragBright = true;
            ApplySlider(e.Location, isVolume: false);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragVolume) ApplySlider(e.Location, isVolume: true);
        else if (_dragBright) ApplySlider(e.Location, isVolume: false);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragVolume = false;
        _dragBright = false;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (_wifiCard.Contains(e.Location)) OpenSettings("ms-settings:network-wifi");
        else if (_btCard.Contains(e.Location)) OpenSettings("ms-settings:bluetooth");
    }

    private void ApplySlider(Point p, bool isVolume)
    {
        var track = isVolume ? _volTrack : _brightTrack;
        if (track.Width <= 0) return;
        int v = (int)Math.Round(Math.Clamp((double)(p.X - track.X) / track.Width, 0, 1) * 100);
        if (isVolume)
        {
            _volumeValue = v;
            AudioVolume.TrySet(v / 100f);
        }
        else
        {
            _brightValue = v;
            BrightnessControl.TrySet((byte)v);
        }
        Invalidate();
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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Frosted background: blurred wallpaper region behind this popup +
        // dark tint, clipped to the rounded outline.
        var full = new Rectangle(0, 0, Width, Height);
        using (var clip = RoundedRectPath(full, CornerRadius))
        {
            var state = g.Save();
            g.SetClip(clip);
            try
            {
                var blurred = WallpaperBlur.GetBlurredScreen();
                if (blurred != null)
                {
                    try
                    {
                        var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
                        int sx = Location.X, sy = Location.Y;
                        var src = new Rectangle(
                            (int)Math.Round((double)(sx - screen.Left) * blurred.Width / screen.Width),
                            (int)Math.Round((double)(sy - screen.Top) * blurred.Height / screen.Height),
                            Math.Max(1, (int)Math.Round((double)Width * blurred.Width / screen.Width)),
                            Math.Max(1, (int)Math.Round((double)Height * blurred.Height / screen.Height)));
                        g.DrawImage(blurred, full, src, GraphicsUnit.Pixel);
                    }
                    catch { }
                }
                using var tint = new SolidBrush(Color.FromArgb(150, 28, 28, 34));
                g.FillRectangle(tint, full);
            }
            finally { g.Restore(state); }
        }
        // Single hairline edge, no double ring.
        using (var edge = RoundedRectPath(full, CornerRadius))
        using (var pen = new Pen(Color.FromArgb(71, 255, 255, 255), 1f))
            g.DrawPath(pen, edge);

        using var titleFont = new Font("Segoe UI", 10f, FontStyle.Bold);
        using var labelFont = new Font("Segoe UI", 10f);
        using var smallFont = new Font("Segoe UI", 9f);
        using var white = new SolidBrush(Color.White);
        using var dim = new SolidBrush(Color.FromArgb(200, 220, 220, 220));

        g.DrawString("Control Center", titleFont, white, new PointF(16, 12));

        int y = 48;
        // Volume row.
        DrawRowLabel(g, "Volume", y, labelFont, white);
        _volTrack = new Rectangle(100, y + 4, 160, 22);
        DrawSlider(g, _volTrack, _volumeValue, _volumeValue < 0 ? "n/a" : _volumeValue + "%", labelFont, dim);
        y += 44;
        // Brightness row.
        DrawRowLabel(g, "Brightness", y, labelFont, white);
        _brightTrack = new Rectangle(100, y + 4, 160, 22);
        DrawSlider(g, _brightTrack, _brightValue, _brightValue < 0 ? "No panel" : _brightValue + "%", labelFont, dim);
        y += 44;
        // Wi-Fi card.
        DrawRowLabel(g, "Wi-Fi", y, labelFont, white);
        _wifiCard = new Rectangle(100, y - 2, 204, 46);
        DrawCard(g, _wifiCard, _wifiStatus, "Wi-Fi Settings ›", smallFont, white, dim);
        y += 62;
        // Bluetooth card.
        DrawRowLabel(g, "Bluetooth", y, labelFont, white);
        _btCard = new Rectangle(100, y - 2, 204, 46);
        DrawCard(g, _btCard, _btStatus, "Bluetooth Settings ›", smallFont, white, dim);
    }

    private static void DrawRowLabel(Graphics g, string text, int y, Font font, Brush brush)
    {
        g.DrawString(text, font, brush, new PointF(16, y + 2));
    }

    /// <summary>
    /// Custom pill slider: dark translucent track; white pill fill of
    /// proportional width; value text at the right. Disabled rows draw
    /// dimmed with the status text instead of a fill.
    /// </summary>
    private static void DrawSlider(Graphics g, Rectangle area, int value, string text, Font font, Brush dim)
    {
        // Track: dark translucent rounded bar, vertically centred in area.
        int trackH = 8;
        var track = new Rectangle(area.X, area.Y + (area.Height - trackH) / 2, area.Width, trackH);
        using (var path = RoundedRectPath(track, trackH / 2))
        using (var fill = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            g.FillPath(fill, path);
        if (value >= 0)
        {
            int fillW = (int)Math.Round(track.Width * Math.Clamp(value, 0, 100) / 100.0);
            if (fillW > 0)
            {
                var fillRect = new Rectangle(track.X, track.Y, Math.Max(fillW, trackH), trackH);
                using var path = RoundedRectPath(fillRect, trackH / 2);
                using var fill = new SolidBrush(Color.White);
                g.FillPath(fill, path);
            }
            using var white = new SolidBrush(Color.White);
            g.DrawString(value + "%", font, white, new PointF(area.Right + 8, area.Y + 2));
        }
        else
        {
            g.DrawString(text, font, dim, new PointF(area.Right + 8, area.Y + 2));
        }
    }

    private static void DrawCard(Graphics g, Rectangle rect, string status, string action, Font font, Brush white, Brush dim)
    {
        using var path = RoundedRectPath(rect, 12);
        using var fill = new SolidBrush(Color.FromArgb(CardColorA, 40, 40, 48));
        g.FillPath(fill, path);
        g.DrawString(status, font, white, new PointF(rect.X + 10, rect.Y + 4));
        g.DrawString(action, font, dim, new PointF(rect.X + 10, rect.Y + 24));
    }
}
