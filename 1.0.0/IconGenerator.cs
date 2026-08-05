namespace GammaBrightnessTool;

/// <summary>
/// Generates application icons dynamically using GDI+.
/// Creates taskbar and tray icons with transparent backgrounds.
/// </summary>
public static class IconGenerator
{
    /// <summary>
    /// Creates a lightbulb icon for the taskbar (black outline, transparent background).
    /// </summary>
    public static Icon CreateTaskbarIcon(int size = 256)
    {
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float scale = size / 256f;
        float centerX = size / 2f;
        float centerY = size / 2f;

        // Lightbulb body (circle/ellipse)
        float bulbRadius = 80 * scale;
        float bulbTop = 60 * scale;
        var bulbRect = new RectangleF(
            centerX - bulbRadius,
            bulbTop,
            bulbRadius * 2,
            bulbRadius * 2.2f);

        // Draw bulb outline
        using var bulbPen = new Pen(Color.Black, 8 * scale);
        bulbPen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
        g.DrawEllipse(bulbPen, bulbRect);

        // Filament lines inside bulb
        using var filamentPen = new Pen(Color.Black, 4 * scale);
        float filamentY = bulbTop + bulbRadius * 1.2f;
        g.DrawLine(filamentPen, centerX - 20 * scale, filamentY, centerX - 10 * scale, filamentY - 30 * scale);
        g.DrawLine(filamentPen, centerX + 20 * scale, filamentY, centerX + 10 * scale, filamentY - 30 * scale);
        g.DrawLine(filamentPen, centerX - 10 * scale, filamentY - 30 * scale, centerX + 10 * scale, filamentY - 30 * scale);

        // Bulb base (screw thread)
        float baseWidth = 40 * scale;
        float baseHeight = 35 * scale;
        float baseTop = bulbTop + bulbRadius * 2.0f;
        var baseRect = new RectangleF(centerX - baseWidth / 2, baseTop, baseWidth, baseHeight);
        g.DrawRectangle(bulbPen, baseRect.X, baseRect.Y, baseRect.Width, baseRect.Height);

        // Base threads
        float threadY1 = baseTop + 10 * scale;
        float threadY2 = baseTop + 20 * scale;
        g.DrawLine(filamentPen, centerX - baseWidth / 2, threadY1, centerX + baseWidth / 2, threadY1);
        g.DrawLine(filamentPen, centerX - baseWidth / 2, threadY2, centerX + baseWidth / 2, threadY2);

        // Bottom contact
        float contactWidth = 25 * scale;
        float contactHeight = 12 * scale;
        var contactRect = new RectangleF(centerX - contactWidth / 2, baseTop + baseHeight, contactWidth, contactHeight);
        g.DrawRectangle(bulbPen, contactRect.X, contactRect.Y, contactRect.Width, contactRect.Height);

        return Icon.FromHandle(bitmap.GetHicon());
    }

    /// <summary>
    /// Creates a sun icon for the system tray (simple line drawing, transparent background).
    /// Uses thicker lines for better visibility at small sizes.
    /// </summary>
    public static Icon CreateTrayIcon(int size = 256)
    {
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float scale = size / 256f;
        float centerX = size / 2f;
        float centerY = size / 2f;

        // Thicker lines for tray visibility
        using var sunPen = new Pen(Color.Black, 12 * scale);
        sunPen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
        sunPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
        sunPen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;

        // Sun center circle - larger for visibility, fill more of the icon area
        float centerRadius = 55 * scale;
        var centerRect = new RectangleF(
            centerX - centerRadius,
            centerY - centerRadius,
            centerRadius * 2,
            centerRadius * 2);
        g.DrawEllipse(sunPen, centerRect);

        // Sun rays - extend closer to the edge
        int rayCount = 8;
        float innerRadius = 65 * scale;
        float outerRadius = 115 * scale;

        for (int i = 0; i < rayCount; i++)
        {
            float angle = (float)(i * 2 * Math.PI / rayCount - Math.PI / 2);
            float x1 = centerX + (float)Math.Cos(angle) * innerRadius;
            float y1 = centerY + (float)Math.Sin(angle) * innerRadius;
            float x2 = centerX + (float)Math.Cos(angle) * outerRadius;
            float y2 = centerY + (float)Math.Sin(angle) * outerRadius;
            g.DrawLine(sunPen, x1, y1, x2, y2);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    /// <summary>
    /// Saves an icon to a .ico file for use as application icon.
    /// </summary>
    public static void SaveIconToFile(Icon icon, string path)
    {
        using var stream = new FileStream(path, FileMode.Create);
        icon.Save(stream);
    }
}
