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
}
