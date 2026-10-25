using System.Drawing.Drawing2D;

namespace FrostDock;

/// <summary>
/// v1.2 macOS/iOS-style app tiles (user's explicit ask, from a MyDockFinder
/// dock screenshot): every dock icon reads as a uniform rounded-square
/// "mobile app icon" — logo inside a full-bleed tile background — instead
/// of free-floating Windows logos of different shapes.
/// SMART RULE (from the brief): if the icon's corners are already opaque,
/// the icon IS square tile artwork (e.g. Settings' blue square) and is
/// drawn as-is with no extra tile — no tile-in-tile double framing. Only
/// freeform / transparent-corner icons get composited onto a tile.
/// Tile background is chosen for contrast with the icon itself: dark-dominant
/// icons get a white tile (Steam on white in the user's screenshot), bright
/// icons get a near-black tile (clock on black). All artwork is rendered by
/// our code from the user's own installed app icons — no Apple artwork.
/// </summary>
internal static class SquircleTiles
{
    /// <summary>
    /// Renders one app icon as a tile of the given pixel size. The result is
    /// always a full square tile (either the icon as-is, or icon-on-tile).
    /// Callers scale-draw it to the on-screen size; magnification scales the
    /// whole tile, never just the logo.
    /// </summary>
    public static Bitmap RenderTile(Bitmap icon, int tileSize = 96)
    {
        var tile = new Bitmap(tileSize, tileSize, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(tile))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            if (ShellIcons.IsFullBleedSquare(icon))
            {
                // Already tile artwork: draw as-is, edge to edge.
                g.DrawImage(icon, new Rectangle(0, 0, tileSize, tileSize));
                return tile;
            }

            Color bg = TileBackground(icon);
            int radius = tileSize * 22 / 100; // squircle-approx: ~22% corner radius
            var rect = new Rectangle(0, 0, tileSize, tileSize);
            using (var path = RoundedRectPath(rect, radius))
            {
                // Subtle vertical gradient so the tile has depth, not a flat swatch.
                Color top = Lighten(bg, 14);
                Color bottom = Darken(bg, 10);
                using var brush = new LinearGradientBrush(rect, top, bottom, LinearGradientMode.Vertical);
                g.FillPath(brush, path);
                // Hairline inner edge for separation against any background.
                using var edge = new Pen(Color.FromArgb(bg == Color.FromArgb(245, 245, 247) ? 30 : 60,
                    bg == Color.FromArgb(245, 245, 247) ? Color.Black : Color.White), 1f);
                g.DrawPath(edge, path);
            }

            // Logo inset to ~78% so the tile border reads (iOS-style padding
            // for non-square artwork). Centre it.
            int logo = tileSize * 78 / 100;
            int off = (tileSize - logo) / 2;
            g.DrawImage(icon, new Rectangle(off, off, logo, logo));
        }
        return tile;
    }

    /// <summary>Procedural Launchpad icon (macOS-style 3x3 grid tile). No artwork copied.</summary>
    public static Bitmap LaunchpadIcon(int size = 96)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int radius = size * 22 / 100;
        using (var path = RoundedRectPath(new Rectangle(0, 0, size, size), radius))
        using (var brush = new LinearGradientBrush(new Rectangle(0, 0, size, size),
            Color.FromArgb(255, 88, 88, 96), Color.FromArgb(255, 44, 44, 52), LinearGradientMode.Vertical))
        {
            g.FillPath(brush, path);
        }
        // 3x3 grid of white rounded squares.
        int cell = size * 20 / 100, gap = size * 7 / 100;
        int total = cell * 3 + gap * 2;
        int x0 = (size - total) / 2, y0 = (size - total) / 2;
        using var tile = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
            {
                var rect = new Rectangle(x0 + c * (cell + gap), y0 + r * (cell + gap), cell, cell);
                using var path = RoundedRectPath(rect, cell * 22 / 100);
                g.FillPath(tile, path);
            }
        return bmp;
    }

    /// <summary>
    /// Contrast background: dark-dominant icons sit on white, bright icons
    /// on near-black, so the logo is always readable on its tile.
    /// </summary>
    private static Color TileBackground(Bitmap icon)
    {
        try
        {
            long sumR = 0, sumG = 0, sumB = 0, count = 0;
            int w = icon.Width, h = icon.Height;
            // Sample every 4th pixel: fast, and plenty for a luminance call.
            for (int y = 0; y < h; y += 4)
            {
                for (int x = 0; x < w; x += 4)
                {
                    var px = icon.GetPixel(x, y);
                    if (px.A < 128) continue;
                    sumR += px.R; sumG += px.G; sumB += px.B; count++;
                }
            }
            if (count == 0) return Color.FromArgb(245, 245, 247);
            double lum = (0.2126 * sumR + 0.7152 * sumG + 0.0722 * sumB) / (count * 255.0);
            return lum < 0.5 ? Color.FromArgb(245, 245, 247) : Color.FromArgb(28, 28, 30);
        }
        catch { return Color.FromArgb(245, 245, 247); }
    }

    private static Color Lighten(Color c, int amount) => Color.FromArgb(
        Math.Min(255, c.R + amount), Math.Min(255, c.G + amount), Math.Min(255, c.B + amount));

    private static Color Darken(Color c, int amount) => Color.FromArgb(
        Math.Max(0, c.R - amount), Math.Max(0, c.G - amount), Math.Max(0, c.B - amount));

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
}
