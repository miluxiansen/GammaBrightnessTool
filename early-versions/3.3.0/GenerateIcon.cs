namespace GammaBrightnessTool;

/// <summary>
/// Command-line tool to generate the application icon file.
/// Run: dotnet run --project GammaBrightnessTool.csproj -- --generate-icon
/// </summary>
public static class GenerateIcon
{
    public static void Run()
    {
        string resourcesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Resources");
        resourcesDir = Path.GetFullPath(resourcesDir);
        
        if (!Directory.Exists(resourcesDir))
        {
            Directory.CreateDirectory(resourcesDir);
        }

        string iconPath = Path.Combine(resourcesDir, "AppIcon.ico");
        string pngPath = Path.Combine(resourcesDir, "lightbulb.png");
        
        // Use provided PNG if available, otherwise generate
        if (File.Exists(pngPath))
        {
            Console.WriteLine($"Converting PNG: {pngPath}");
            PngToIcoConverter.Convert(pngPath, iconPath);
        }
        else
        {
            Console.WriteLine("PNG not found, generating default icon...");
            using var icon = IconGenerator.CreateTaskbarIcon();
            IconGenerator.SaveIconToFile(icon, iconPath);
        }
        
        Console.WriteLine($"Icon generated: {iconPath}");
    }

    /// <summary>
    /// Exports the ORIGINAL tray sun glyph (the multi-size tray icon) as a
    /// PNG file into the Resources folder, so it can be reused as the
    /// brightness icon in the left-click popup (keeping tray and popup
    /// visuals consistent). Also writes a companion white-on-transparent
    /// version for dark taskbars.
    /// Run: dotnet run --project GammaBrightnessTool.csproj -- --export-tray-icon
    /// </summary>
    public static void ExportTrayIconPng()
    {
        string resourcesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Resources");
        resourcesDir = Path.GetFullPath(resourcesDir);
        Directory.CreateDirectory(resourcesDir);

        // 256px master render (black glyph) — the same DrawSun the tray
        // icon uses, so the exported art matches the current tray icon 1:1.
        string blackPath = Path.Combine(resourcesDir, "tray-sun-black.png");
        using (var bmp = new System.Drawing.Bitmap(256, 256, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            IconGenerator.DrawSun(g, 256, System.Drawing.Color.Black);
            bmp.Save(blackPath, System.Drawing.Imaging.ImageFormat.Png);
        }

        string whitePath = Path.Combine(resourcesDir, "tray-sun-white.png");
        using (var bmp = new System.Drawing.Bitmap(256, 256, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            IconGenerator.DrawSun(g, 256, System.Drawing.Color.White);
            bmp.Save(whitePath, System.Drawing.Imaging.ImageFormat.Png);
        }

        Console.WriteLine($"Exported: {blackPath}");
        Console.WriteLine($"Exported: {whitePath}");
    }
}
