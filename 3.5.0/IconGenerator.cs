using System.Text;

namespace GammaBrightnessTool;

/// <summary>
/// Generates application icons dynamically using GDI+.
/// Creates taskbar and tray icons with transparent backgrounds.
/// </summary>
public static class IconGenerator
{
    /// <summary>
    /// Creates a lightbulb icon for the taskbar (black outline, transparent background).
    /// The returned Icon is a managed clone whose Dispose() destroys the native
    /// HICON (avoids leaking one GDI icon handle per call).
    /// </summary>
    public static Icon CreateTaskbarIcon(int size = 256)
    {
        if (size <= 0) size = 256;

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

        // Clone: the clone owns its own native handle and releases it on
        // Dispose(); the FromHandle wrapper is transient.
        using (var icon = Icon.FromHandle(bitmap.GetHicon()))
        {
            return (Icon)icon.Clone();
        }
    }

    /// <summary>
    /// Creates a sun icon for the system tray (simple line drawing, transparent background).
    /// Uses thicker lines for better visibility at small sizes.
    /// </summary>
    public static Icon CreateTrayIcon(int size = 256)
    {
        if (size <= 0) size = 256;

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

        using (var icon = Icon.FromHandle(bitmap.GetHicon()))
        {
            return (Icon)icon.Clone();
        }
    }

    /// <summary>
    /// Creates a multi-resolution tray icon containing the pre-rendered
    /// per-size PNGs from Resources\tray-icons (16..256, one frame per
    /// size, each designed for its own pixel size). Windows picks the
    /// frame closest to the actual display size, so the icon is rendered
    /// 1:1 instead of being bitmap-scaled - this keeps the strokes crisp
    /// at every size.
    /// </summary>
    public static Icon CreateMultiSizeTrayIcon()
    {
        // Tray art: the user-supplied per-size sun PNGs. The dark glyphs
        // are used on LIGHT taskbars and the white glyphs on DARK taskbars
        // (the taskbar follows the OS theme only, not the in-app theme).
        bool dark = ThemeManager.SystemIsDark;
        string color = dark ? "white" : "black";

        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };

        var entries = new List<(byte w, byte h, byte colors, byte reserved, ushort planes, ushort bitCount, uint bytesInRes, uint offset)>();
        var blobs = new List<byte[]>();
        int dataStart = 6 + sizes.Length * 16;

        foreach (int size in sizes)
        {
            string resName = $"tray-sun-{color}-{size}.png";
            using var png = LoadEmbeddedPng(resName);
            if (png == null)
            {
                // Fallback to the classic drawn glyph if a resource is gone.
                return CreateMultiSizeTrayIconDrawn();
            }

            using var pngStream = new MemoryStream();
            png.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
            var pngBytes = pngStream.ToArray();

            byte dim = (byte)(size >= 256 ? 0 : size);
            entries.Add((dim, dim, 0, 0, 1, 32, (uint)pngBytes.Length, (uint)(dataStart + blobs.Sum(b => b.Length))));
            blobs.Add(pngBytes);
        }

        using var iconStream = new MemoryStream();
        using (var iconWriter = new BinaryWriter(iconStream, Encoding.UTF8, leaveOpen: true))
        {
            iconWriter.Write((ushort)0);
            iconWriter.Write((ushort)1);
            iconWriter.Write((ushort)entries.Count);
            foreach (var e in entries)
            {
                iconWriter.Write(e.w);
                iconWriter.Write(e.h);
                iconWriter.Write(e.colors);
                iconWriter.Write(e.reserved);
                iconWriter.Write(e.planes);
                iconWriter.Write(e.bitCount);
                iconWriter.Write(e.bytesInRes);
                iconWriter.Write(e.offset);
            }
            foreach (var blob in blobs)
            {
                iconWriter.Write(blob);
            }
            iconWriter.Flush();
        }

        iconStream.Position = 0;
        return new Icon(iconStream);
    }

    /// <summary>
    /// The original drawn-glyph tray icon (black/white sun), kept as the
    /// fallback when the embedded Brightness.png is missing.
    /// </summary>
    private static Icon CreateMultiSizeTrayIconDrawn()
    {
        int[] sizes = { 16, 20, 24, 28, 32, 40, 48, 64, 96, 128, 256 };

        var entries = new List<(byte w, byte h, byte colors, byte reserved, ushort planes, ushort bitCount, uint bytesInRes, uint offset)>();
        var blobs = new List<byte[]>();
        int dataStart = 6 + sizes.Length * 16;

        foreach (int size in sizes)
        {
            using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                DrawSun(g, size, ThemeManager.SystemIsDark ? Color.White : Color.Black);
            }

            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
            var pngBytes = pngStream.ToArray();

            byte dim = (byte)(size >= 256 ? 0 : size);
            entries.Add((dim, dim, 0, 0, 1, 32, (uint)pngBytes.Length, (uint)(dataStart + blobs.Sum(b => b.Length))));
            blobs.Add(pngBytes);
        }

        using var iconStream = new MemoryStream();
        using (var iconWriter = new BinaryWriter(iconStream, Encoding.UTF8, leaveOpen: true))
        {
            iconWriter.Write((ushort)0);
            iconWriter.Write((ushort)1);
            iconWriter.Write((ushort)entries.Count);
            foreach (var e in entries)
            {
                iconWriter.Write(e.w);
                iconWriter.Write(e.h);
                iconWriter.Write(e.colors);
                iconWriter.Write(e.reserved);
                iconWriter.Write(e.planes);
                iconWriter.Write(e.bitCount);
                iconWriter.Write(e.bytesInRes);
                iconWriter.Write(e.offset);
            }
            foreach (var blob in blobs)
            {
                iconWriter.Write(blob);
            }
            iconWriter.Flush();
        }

        iconStream.Position = 0;
        return new Icon(iconStream);
    }

    /// <summary>
    /// Loads an embedded PNG resource by file name suffix (any resource
    /// whose dotted name ends with ".&lt;fileName&gt;"). Returns null when
    /// missing.
    /// </summary>
    private static Bitmap? LoadEmbeddedPng(string fileName)
    {
        var asm = typeof(IconGenerator).Assembly;
        var match = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
        if (match == null) return null;
        using var stream = asm.GetManifestResourceStream(match);
        if (stream == null) return null;
        return new Bitmap(stream);
    }

    /// <summary>
    /// Draws the sun glyph (circle + 8 rays) into the given Graphics.
    /// Shared by the single-size and multi-size icon generators.
    /// </summary>
    internal static void DrawSun(Graphics g, int size, Color color)
    {
        float scale = size / 256f;
        float centerX = size / 2f;
        float centerY = size / 2f;

        using var sunPen = new Pen(color, 12 * scale);
        sunPen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
        sunPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
        sunPen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;

        float centerRadius = 55 * scale;
        var centerRect = new RectangleF(
            centerX - centerRadius,
            centerY - centerRadius,
            centerRadius * 2,
            centerRadius * 2);
        g.DrawEllipse(sunPen, centerRect);

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
