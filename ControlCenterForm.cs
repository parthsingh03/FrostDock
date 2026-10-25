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

    private int _volumeValue = -1; // -1 = unreachable (reason shown, not "n/a")
    private int _brightValue = -1; // -1 = no panel
    private string _wifiStatus = "Unknown";
    private string _btStatus = "Unknown";
    private bool _dragVolume;
    private bool _dragBright;

    private Rectangle _volTrack = Rectangle.Empty;
    private Rectangle _brightTrack = Rectangle.Empty;
    private Rectangle _wifiCard = Rectangle.Empty;
    private Rectangle _btCard = Rectangle.Empty;

    // v1.4 Now Playing (GSMTC; null = no active session).
    private NowPlayingInfo? _npInfo;
    private bool _npSupported = true;
    private Rectangle _npCard = Rectangle.Empty;
    private Rectangle _npPrevRect = Rectangle.Empty;
    private Rectangle _npPlayRect = Rectangle.Empty;
    private Rectangle _npNextRect = Rectangle.Empty;

    public ControlCenterForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Width = 320;
        Height = 340; // v1.4: +72 for the Now Playing card on top.
        UpdateRegion();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        // Live refresh when the session or its track changes (WinRT may
        // call back off the UI thread, so marshal with BeginInvoke).
        NowPlaying.Changed += (_, _) =>
        {
            if (IsHandleCreated && Visible)
                BeginInvoke(new Action(() => { _ = RefreshNowPlayingAsync(); }));
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _npInfo?.Art?.Dispose(); } catch { }
        }
        base.Dispose(disposing);
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
        _ = RefreshNowPlayingAsync();
        Invalidate();
    }

    private async Task RefreshNowPlayingAsync()
    {
        if (!NowPlaying.Supported)
        {
            _npSupported = false;
            _npInfo = null;
            Invalidate();
            return;
        }
        _npSupported = true;
        var info = await NowPlaying.GetCurrentAsync();
        try { _npInfo?.Art?.Dispose(); } catch { }
        _npInfo = info;
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
        // v1.4 Now Playing buttons: GSMTC session when supported, plain
        // media keys when GSMTC interop is unavailable (never fake info).
        if (_npPrevRect.Contains(e.Location))
        {
            if (_npSupported) _ = NowPlaying.PreviousAsync();
            else NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_PREV_TRACK);
            return;
        }
        if (_npPlayRect.Contains(e.Location))
        {
            if (_npSupported) _ = NowPlaying.ToggleAsync();
            else NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_PLAY_PAUSE);
            return;
        }
        if (_npNextRect.Contains(e.Location))
        {
            if (_npSupported) _ = NowPlaying.NextAsync();
            else NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_NEXT_TRACK);
            return;
        }
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
                // v1.4 Liquid Glass: specular gradient across the top edge.
                using var gloss = new LinearGradientBrush(
                    new Rectangle(0, 0, Width, 18),
                    Color.FromArgb(50, 255, 255, 255), Color.FromArgb(0, 255, 255, 255),
                    LinearGradientMode.Vertical);
                g.FillRectangle(gloss, new Rectangle(0, 0, Width, 18));
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

        // v1.4 Now Playing card (top): album art + title/artist/app, with
        // Prev / Play-Pause / Next. No session -> "Nothing playing".
        // GSMTC unavailable -> media-key buttons with a "Media keys" note.
        _npCard = new Rectangle(16, 40, Width - 32, 64);
        DrawNowPlayingCard(g, _npCard, smallFont, white, dim);

        int y = 116;
        // Volume row.
        DrawRowLabel(g, "Volume", y, labelFont, white);
        _volTrack = new Rectangle(100, y + 4, 160, 22);
        DrawSlider(g, _volTrack, _volumeValue, "", labelFont, dim);
        // v1.4: when unreachable, the REAL reason goes under the slider
        // (never a bare "n/a" — the old text was too narrow to read here).
        if (_volumeValue < 0)
        {
            string reason = string.IsNullOrWhiteSpace(AudioVolume.LastError)
                ? "No audio device found" : AudioVolume.LastError;
            g.DrawString(reason, smallFont, dim, new PointF(100, y + 26));
        }
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

    private void DrawNowPlayingCard(Graphics g, Rectangle card, Font smallFont, Brush white, Brush dim)
    {
        var blurred = WallpaperBlur.GetBlurredScreen();
        var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        GlassStyle.PaintGlassCard(g, card, 12, Color.FromArgb(110, 40, 40, 48), blurred, screen, Location);

        using var titleFont = new Font("Segoe UI", 10f, FontStyle.Bold);
        using var glyphFont = new Font("Segoe Fluent Icons", 13f);
        var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

        // Buttons (right side): 3 x 30px circles, 6px gaps.
        int btnD = 30;
        int bx = card.Right - 10 - btnD * 3 - 12;
        int by = card.Y + (card.Height - btnD) / 2;
        _npPrevRect = new Rectangle(bx, by, btnD, btnD);
        _npPlayRect = new Rectangle(bx + btnD + 6, by, btnD, btnD);
        _npNextRect = new Rectangle(bx + (btnD + 6) * 2, by, btnD, btnD);
        bool sessionLive = _npSupported && _npInfo != null;
        DrawMediaButton(g, _npPrevRect, "\uE892", glyphFont, sessionLive || !_npSupported);
        DrawMediaButton(g, _npPlayRect, _npInfo != null && _npInfo.IsPlaying ? "\uE769" : "\uE768", glyphFont, sessionLive || !_npSupported);
        DrawMediaButton(g, _npNextRect, "\uE893", glyphFont, sessionLive || !_npSupported);

        int textX = card.X + 10;
        int textW = bx - textX - 8;
        if (_npInfo?.Art != null)
        {
            g.DrawImage(_npInfo.Art, new Rectangle(textX, card.Y + 8, 48, 48));
            textX += 58;
            textW -= 58;
        }
        else if (_npSupported && _npInfo != null)
        {
            // No art: music-note placeholder box.
            using var box = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
            g.FillRectangle(box, new Rectangle(textX, card.Y + 8, 48, 48));
            g.DrawString("\uE8D6", glyphFont, white, new PointF(textX + 15, card.Y + 22));
            textX += 58;
            textW -= 58;
        }

        if (!_npSupported)
        {
            g.DrawString("Media keys", titleFont, white, new Rectangle(textX, card.Y + 10, textW, 20), fmt);
            g.DrawString("Playback app control (no track info)", smallFont, dim, new Rectangle(textX, card.Y + 32, textW, 20), fmt);
        }
        else if (_npInfo == null)
        {
            g.DrawString("Nothing playing", titleFont, white, new Rectangle(textX, card.Y + 10, textW, 20), fmt);
            g.DrawString("Play something in any app", smallFont, dim, new Rectangle(textX, card.Y + 32, textW, 20), fmt);
        }
        else
        {
            g.DrawString(_npInfo.Title, titleFont, white, new Rectangle(textX, card.Y + 8, textW, 20), fmt);
            string sub = string.IsNullOrWhiteSpace(_npInfo.Artist)
                ? _npInfo.AppName : _npInfo.Artist + " · " + _npInfo.AppName;
            g.DrawString(sub, smallFont, dim, new Rectangle(textX, card.Y + 30, textW, 20), fmt);
        }
    }

    private static void DrawMediaButton(Graphics g, Rectangle rect, string glyph, Font glyphFont, bool enabled)
    {
        using var fill = new SolidBrush(enabled
            ? Color.FromArgb(200, 255, 255, 255) : Color.FromArgb(70, 255, 255, 255));
        g.FillEllipse(fill, rect);
        using var fg = new SolidBrush(enabled ? Color.FromArgb(255, 30, 30, 34) : Color.FromArgb(140, 255, 255, 255));
        var size = g.MeasureString(glyph, glyphFont);
        g.DrawString(glyph, glyphFont, fg,
            new PointF(rect.X + (rect.Width - size.Width) / 2, rect.Y + (rect.Height - size.Height) / 2));
    }

    private static void DrawRowLabel(Graphics g, string text, int y, Font font, Brush brush)
    {
        g.DrawString(text, font, brush, new PointF(16, y + 2));
    }

    /// <summary>
    /// v1.4 Liquid Glass slider: glossy pill track + shiny knob (see
    /// GlassStyle); value text at the right. Disabled rows draw dimmed
    /// with the status text instead of a fill (volume's reason line is
    /// drawn by the caller, under the slider).
    /// </summary>
    private static void DrawSlider(Graphics g, Rectangle area, int value, string text, Font font, Brush dim)
    {
        var track = new Rectangle(area.X, area.Y + 3, area.Width, 16);
        GlassStyle.PaintGlossySlider(g, track, value >= 0 ? value / 100f : -1, value >= 0);
        if (value >= 0)
        {
            using var white = new SolidBrush(Color.White);
            g.DrawString(value + "%", font, white, new PointF(area.Right + 8, area.Y + 2));
        }
        else if (!string.IsNullOrEmpty(text))
        {
            g.DrawString(text, font, dim, new PointF(area.Right + 8, area.Y + 2));
        }
    }

    private void DrawCard(Graphics g, Rectangle rect, string status, string action, Font font, Brush white, Brush dim)
    {
        var blurred = WallpaperBlur.GetBlurredScreen();
        var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        GlassStyle.PaintGlassCard(g, rect, 12, Color.FromArgb(CardColorA, 40, 40, 48), blurred, screen, Location);
        g.DrawString(status, font, white, new PointF(rect.X + 10, rect.Y + 4));
        g.DrawString(action, font, dim, new PointF(rect.X + 10, rect.Y + 24));
    }
}
