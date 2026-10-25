using System.Drawing.Drawing2D;

namespace FrostDock;

/// <summary>
/// v1.4 Liquid Glass fake (shared paint helpers). True refraction needs GPU
/// shaders WinForms GDI+ cannot do, so the material is faked the same way
/// the dock pill already is: blurred-wallpaper surface + translucent tint
/// + bright specular top-edge highlight + soft inner shadow + 1px rim.
/// Paint-only: no control behaviour lives here.
/// </summary>
internal static class GlassStyle
{
    public static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0) { path.AddRectangle(r); return path; }
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Paints a glass card: the blurred-wallpaper region behind <paramref name="rect"/>
    /// (mapped from screen coords), a tint, a specular gradient at the top
    /// edge, and a 1px white rim. Falls back to flat tint when no blurred
    /// wallpaper is available.
    /// </summary>
    public static void PaintGlassCard(Graphics g, Rectangle rect, int radius, Color tint,
        Bitmap? blurred, Rectangle screenBounds, Point formLocation)
    {
        using var path = RoundedRect(rect, radius);
        var state = g.Save();
        g.SetClip(path);
        try
        {
            if (blurred != null)
            {
                try
                {
                    int sx = formLocation.X + rect.X, sy = formLocation.Y + rect.Y;
                    var src = new Rectangle(
                        (int)Math.Round((double)(sx - screenBounds.Left) * blurred.Width / Math.Max(1, screenBounds.Width)),
                        (int)Math.Round((double)(sy - screenBounds.Top) * blurred.Height / Math.Max(1, screenBounds.Height)),
                        Math.Max(1, (int)Math.Round((double)rect.Width * blurred.Width / Math.Max(1, screenBounds.Width))),
                        Math.Max(1, (int)Math.Round((double)rect.Height * blurred.Height / Math.Max(1, screenBounds.Height))));
                    g.DrawImage(blurred, rect, src, GraphicsUnit.Pixel);
                }
                catch { }
            }
            using var tintBrush = new SolidBrush(tint);
            g.FillRectangle(tintBrush, rect);
            // Specular top edge: bright at the very top, fading down.
            int glossH = Math.Min(14, rect.Height / 2);
            if (glossH > 0)
            {
                using var gloss = new LinearGradientBrush(
                    new Rectangle(rect.X, rect.Y, rect.Width, glossH),
                    Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255),
                    LinearGradientMode.Vertical);
                g.FillRectangle(gloss, new Rectangle(rect.X, rect.Y, rect.Width, glossH));
            }
            // Soft inner shadow along the bottom edge.
            using var shade = new LinearGradientBrush(
                new Rectangle(rect.X, rect.Bottom - 8, rect.Width, 8),
                Color.FromArgb(0, 0, 0, 0), Color.FromArgb(28, 0, 0, 0),
                LinearGradientMode.Vertical);
            g.FillRectangle(shade, new Rectangle(rect.X, rect.Bottom - 8, rect.Width, 8));
        }
        finally { g.Restore(state); }
        using var rim = new Pen(Color.FromArgb(71, 255, 255, 255), 1f);
        g.DrawPath(rim, path);
    }

    /// <summary>
    /// Glossy pill slider: glass track, white fill up to <paramref name="fraction"/>,
    /// and a shiny round knob with a specular dot. Pass fraction &lt; 0 for
    /// the disabled (no device) state: dim track, no fill, no knob.
    /// </summary>
    public static void PaintGlossySlider(Graphics g, Rectangle track, float fraction, bool enabled)
    {
        using var trackPath = RoundedRect(track, track.Height / 2);
        var state = g.Save();
        g.SetClip(trackPath);
        try
        {
            using var trackFill = new SolidBrush(enabled
                ? Color.FromArgb(110, 20, 20, 28) : Color.FromArgb(70, 30, 30, 38));
            g.FillRectangle(trackFill, track);
            if (enabled && fraction >= 0)
            {
                int fillW = (int)Math.Round(track.Width * Math.Clamp(fraction, 0f, 1f));
                if (fillW > 0)
                {
                    using var fill = new LinearGradientBrush(
                        new Rectangle(track.X, track.Y, fillW, track.Height),
                        Color.FromArgb(255, 255, 255, 255), Color.FromArgb(235, 220, 220, 220),
                        LinearGradientMode.Vertical);
                    g.FillRectangle(fill, new Rectangle(track.X, track.Y, fillW, track.Height));
                }
            }
            // Specular line along the track's top.
            using var gloss = new LinearGradientBrush(
                new Rectangle(track.X, track.Y, track.Width, Math.Max(2, track.Height / 3)),
                Color.FromArgb(60, 255, 255, 255), Color.FromArgb(0, 255, 255, 255),
                LinearGradientMode.Vertical);
            g.FillRectangle(gloss, new Rectangle(track.X, track.Y, track.Width, Math.Max(2, track.Height / 3)));
        }
        finally { g.Restore(state); }
        using var rim = new Pen(Color.FromArgb(71, 255, 255, 255), 1f);
        g.DrawPath(rim, trackPath);

        if (enabled && fraction >= 0)
        {
            int knobD = track.Height + 4;
            int kx = track.X + (int)Math.Round((track.Width - knobD) * Math.Clamp(fraction, 0f, 1f));
            int ky = track.Y - 2;
            // Knob shadow + white body + specular dot.
            using var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0));
            g.FillEllipse(shadow, new Rectangle(kx + 1, ky + 2, knobD, knobD));
            using var body = new LinearGradientBrush(
                new Rectangle(kx, ky, knobD, knobD),
                Color.White, Color.FromArgb(255, 215, 215, 215),
                LinearGradientMode.Vertical);
            g.FillEllipse(body, new Rectangle(kx, ky, knobD, knobD));
            using var dot = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
            g.FillEllipse(dot, new Rectangle(kx + knobD / 4, ky + knobD / 5, knobD / 4, knobD / 4));
        }
    }
}
