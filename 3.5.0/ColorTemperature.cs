using System.Drawing;

namespace GammaBrightnessTool;

/// <summary>
/// Converts a color-temperature value (kelvin) into an RGB color for
/// VISUAL FEEDBACK ONLY (slider fill, mode icon). It reuses the same
/// Tanner Helland math the gamma ramp uses, then boosts the saturation
/// of the three channel multipliers into a clearly visible color:
/// at 6600K the raw multipliers are all 1.0 (pure white), which would be
/// invisible against a light track, so the neutral point is remapped to a
/// mid-tone gray that reads as "neutral / no tint" on either theme.
/// Lower kelvin shifts toward warm orange, higher kelvin toward cool blue.
/// </summary>
public static class ColorTemperature
{
    /// <summary>
    /// Returns the display color for a kelvin value in [MIN, MAX].
    /// Saturation-boosted so it stays visible in both light and dark themes
    /// (never pure white, never pure black).
    /// </summary>
    public static Color FromKelvin(float kelvin)
    {
        float t = Math.Clamp(kelvin, GammaController.MIN_TEMPERATURE, GammaController.MAX_TEMPERATURE);
        float r = (float)GammaController.GetRedMultiplier(t);
        float g = (float)GammaController.GetGreenMultiplier(t);
        float b = (float)GammaController.GetBlueMultiplier(t);

        // Neutral gray anchor at 6600K (raw multipliers are all 1.0).
        // Light theme: mid gray (135) is visible on the light track (#C8C8C8).
        // Dark theme: a lighter gray (185) keeps the fill legible against
        // the dark track. The per-channel deviation is scaled outward from
        // the anchor.
        float anchor = ThemeManager.PopupIsDark ? 185f : 135f;
        // Warm/cool deviation is amplified so 1000K reads clearly orange
        // and 10000K clearly blue, not a subtle tint.
        const float boost = 150f;

        float dr = r - 1.0f;
        float dg = g - 1.0f;
        float db = b - 1.0f;
        int red = (int)Math.Clamp(anchor + dr * boost, 20, 255);
        int green = (int)Math.Clamp(anchor + dg * boost, 20, 255);
        int blue = (int)Math.Clamp(anchor + db * boost, 20, 255);

        return Color.FromArgb(255, red, green, blue);
    }
}
