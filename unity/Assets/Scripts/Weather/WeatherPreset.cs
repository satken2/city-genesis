using UnityEngine;

/// <summary>経済・季節から独立した、撮影にも使える環境光の組み合わせ。</summary>
public sealed class WeatherPreset
{
    public readonly string Name;
    public readonly string Id;
    public readonly int Weight;
    public readonly int Rain;
    public readonly Color Zenith, Horizon, Sun, Cloud;
    public readonly float Elevation, Azimuth, Intensity, Coverage, Thickness, Haze, Shadow;

    private WeatherPreset(string id, string name, int weight, int rain, string zenith, string horizon, string sun, string cloud,
        float elevation, float azimuth, float intensity, float coverage, float thickness, float haze, float shadow)
    {
        Id = id; Name = name; Weight = weight; Rain = rain;
        Zenith = Parse(zenith); Horizon = Parse(horizon); Sun = Parse(sun); Cloud = Parse(cloud);
        Elevation = elevation; Azimuth = azimuth; Intensity = intensity;
        Coverage = coverage; Thickness = thickness; Haze = haze; Shadow = shadow;
    }

    private static Color Parse(string hex) { ColorUtility.TryParseHtmlString("#" + hex, out var c); return c; }

    public static readonly System.Collections.Generic.IReadOnlyList<WeatherPreset> All = System.Array.AsReadOnly(new[]
    {
        new WeatherPreset("clear", "快晴 / Clear", 27, 0, "2872BC", "C5DDEC", "FFF3DE", "FFFFFF", 52, 135, 1.35f, 0.03f, 0.25f, 0.10f, 0.85f),
        new WeatherPreset("high_clouds", "薄雲 / High clouds", 15, 0, "4284C1", "D5DFE8", "FFF0D9", "FFF9EE", 44, 155, 1.15f, 0.30f, 0.22f, 0.18f, 0.70f),
        new WeatherPreset("fair", "晴れ / Fair", 12, 0, "327BB8", "CCDCE5", "FFF1D8", "FFFAF1", 48, 120, 1.30f, 0.48f, 0.75f, 0.15f, 0.80f),
        new WeatherPreset("broken_clouds", "晴れ間 / Broken clouds", 6, 0, "4A7EAA", "C7D4DD", "FFE8CA", "F7F3EA", 35, 205, 1.12f, 0.66f, 0.85f, 0.26f, 0.65f),
        new WeatherPreset("overcast", "曇り / Overcast", 10, 0, "8998A9", "CBD1D7", "E8EDF5", "DCE2E8", 55, 170, 0.38f, 0.96f, 0.70f, 0.37f, 0.22f),
        new WeatherPreset("rain_clouds", "雨雲 / Rain clouds", 5, 0, "546377", "9CAAB7", "C5D2E2", "AAB7C8", 32, 220, 0.22f, 0.90f, 1.25f, 0.55f, 0.15f),
        new WeatherPreset("fog", "霧 / Fog", 2, 0, "9DBBCB", "D9E1E4", "EAF0F3", "EDF0F2", 42, 135, 0.68f, 0.38f, 0.35f, 0.85f, 0.30f),
        new WeatherPreset("sunset", "夕焼け / Sunset", 8, 0, "696E9B", "ECA68D", "FFA477", "EDB7AB", 12, 275, 1.00f, 0.48f, 0.65f, 0.32f, 0.70f),
        new WeatherPreset("drizzle", "霧雨 / Drizzle", 7, 1, "8A9DAD", "C4D1D8", "DCE7EF", "CDD9DF", 45, 160, 0.36f, 0.94f, 0.65f, 0.70f, 0.18f),
        new WeatherPreset("rain", "雨 / Rain", 7, 2, "647B90", "ABBBC7", "CCDDEA", "AEBFCD", 40, 190, 0.30f, 0.97f, 1.00f, 0.57f, 0.16f),
        new WeatherPreset("heavy_rain", "大雨 / Heavy rain", 1, 3, "485A70", "8599AA", "B6C9DC", "8E9FB3", 38, 210, 0.18f, 1.00f, 1.40f, 0.77f, 0.10f),
    });
}
