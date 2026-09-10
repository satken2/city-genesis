using UnityEngine;

/// <summary>経済・季節から独立した、撮影にも使える環境光の組み合わせ。</summary>
public sealed class WeatherPreset
{
    public readonly string Name;
    public readonly Color Zenith, Horizon, Sun, Cloud;
    public readonly float Elevation, Azimuth, Intensity, Coverage, Thickness, Haze, Shadow;

    private WeatherPreset(string name, string zenith, string horizon, string sun, string cloud,
        float elevation, float azimuth, float intensity, float coverage, float thickness, float haze, float shadow)
    {
        Name = name;
        Zenith = Parse(zenith); Horizon = Parse(horizon); Sun = Parse(sun); Cloud = Parse(cloud);
        Elevation = elevation; Azimuth = azimuth; Intensity = intensity;
        Coverage = coverage; Thickness = thickness; Haze = haze; Shadow = shadow;
    }

    private static Color Parse(string hex) { ColorUtility.TryParseHtmlString("#" + hex, out var c); return c; }

    public static readonly System.Collections.Generic.IReadOnlyList<WeatherPreset> All = System.Array.AsReadOnly(new[]
    {
        new WeatherPreset("快晴 / Clear", "2872BC", "C5DDEC", "FFF3DE", "FFFFFF", 52, 135, 1.35f, 0.03f, 0.25f, 0.10f, 0.85f),
        new WeatherPreset("薄雲 / High clouds", "4284C1", "D5DFE8", "FFF0D9", "FFF9EE", 44, 155, 1.15f, 0.30f, 0.22f, 0.18f, 0.70f),
        new WeatherPreset("積雲 / Fair weather", "327BB8", "CCDCE5", "FFF1D8", "FFFAF1", 48, 120, 1.30f, 0.48f, 0.75f, 0.15f, 0.80f),
        new WeatherPreset("晴れ間 / Broken clouds", "4A7EAA", "C7D4DD", "FFE8CA", "F7F3EA", 35, 205, 1.12f, 0.66f, 0.85f, 0.26f, 0.65f),
        new WeatherPreset("曇天 / Overcast", "8998A9", "CBD1D7", "E8EDF5", "DCE2E8", 55, 170, 0.38f, 0.96f, 0.70f, 0.37f, 0.22f),
        new WeatherPreset("雨雲 / Rain clouds", "546377", "9CAAB7", "C5D2E2", "AAB7C8", 32, 220, 0.22f, 0.90f, 1.25f, 0.55f, 0.15f),
        new WeatherPreset("朝霧 / Morning mist", "9DBBCB", "E0DFD0", "FFE7BE", "F2EDE0", 18, 85, 0.68f, 0.38f, 0.35f, 0.85f, 0.40f),
        new WeatherPreset("朝焼け / Sunrise", "637DB2", "F2C1A2", "FFD2A0", "F9D4BC", 12, 80, 1.00f, 0.42f, 0.55f, 0.35f, 0.70f),
        new WeatherPreset("夕陽 / Golden hour", "5484B1", "F4C591", "FFC17F", "FFE0B3", 16, 260, 1.25f, 0.28f, 0.65f, 0.25f, 0.80f),
        new WeatherPreset("夕焼け / Sunset", "696E9B", "ECA68D", "FFA477", "EDB7AB", 7, 275, 0.80f, 0.58f, 0.70f, 0.42f, 0.65f),
    });
}
