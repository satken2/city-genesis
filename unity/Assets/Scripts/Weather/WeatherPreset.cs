using UnityEngine;

/// <summary>
/// 天気1種類ぶんの空・雲・光・降水の見た目。太陽の向きと夕焼けの色は時刻(WeatherClock)が決めるので、
/// ここにあるのは太陽が十分高い昼間の見た目である。出現割合は気候と季節(WeatherClimate)が決める。
/// </summary>
public sealed class WeatherPreset
{
    public readonly string Name;
    public readonly string Id;
    public readonly int Rain, Snow;
    public readonly Color Zenith, Horizon, Sun, Cloud;
    public readonly float Intensity, Coverage, Thickness, Haze, Shadow;
    public readonly float Duration; // 1回の持続時間の平均(秒)。実際の長さはこの0.5〜1.5倍の一様乱数

    private WeatherPreset(string id, string name, int rain, int snow, string zenith, string horizon, string sun, string cloud,
        float intensity, float coverage, float thickness, float haze, float shadow, float duration)
    {
        Id = id; Name = name; Rain = rain; Snow = snow;
        Zenith = Parse(zenith); Horizon = Parse(horizon); Sun = Parse(sun); Cloud = Parse(cloud);
        Intensity = intensity; Coverage = coverage; Thickness = thickness; Haze = haze; Shadow = shadow;
        Duration = duration;
    }

    private static Color Parse(string hex) { ColorUtility.TryParseHtmlString("#" + hex, out var c); return c; }

    public static readonly System.Collections.Generic.IReadOnlyList<WeatherPreset> All = System.Array.AsReadOnly(new[]
    {
        new WeatherPreset("clear", "快晴 / Clear", 0, 0, "2872BC", "C5DDEC", "FFF3DE", "FFFFFF", 1.35f, 0.03f, 0.25f, 0.10f, 0.85f, 300),
        new WeatherPreset("high_clouds", "薄雲 / High clouds", 0, 0, "4284C1", "D5DFE8", "FFF0D9", "FFF9EE", 1.15f, 0.30f, 0.22f, 0.18f, 0.70f, 200),
        new WeatherPreset("fair", "晴れ / Fair", 0, 0, "327BB8", "CCDCE5", "FFF1D8", "FFFAF1", 1.30f, 0.48f, 0.75f, 0.15f, 0.80f, 240),
        new WeatherPreset("broken_clouds", "晴れ間 / Broken clouds", 0, 0, "4A7EAA", "C7D4DD", "FFE8CA", "F7F3EA", 1.12f, 0.66f, 0.85f, 0.26f, 0.65f, 180),
        new WeatherPreset("overcast", "曇り / Overcast", 0, 0, "8998A9", "CBD1D7", "E8EDF5", "DCE2E8", 0.38f, 0.96f, 0.70f, 0.37f, 0.22f, 240),
        new WeatherPreset("rain_clouds", "雨雲 / Rain clouds", 0, 0, "546377", "9CAAB7", "C5D2E2", "AAB7C8", 0.22f, 0.90f, 1.25f, 0.55f, 0.15f, 150),
        new WeatherPreset("fog", "霧 / Fog", 0, 0, "9DBBCB", "D9E1E4", "EAF0F3", "EDF0F2", 0.68f, 0.38f, 0.35f, 0.85f, 0.30f, 150),
        new WeatherPreset("drizzle", "霧雨 / Drizzle", 1, 0, "8A9DAD", "C4D1D8", "DCE7EF", "CDD9DF", 0.36f, 0.94f, 0.65f, 0.70f, 0.18f, 180),
        new WeatherPreset("rain", "雨 / Rain", 2, 0, "647B90", "ABBBC7", "CCDDEA", "AEBFCD", 0.30f, 0.97f, 1.00f, 0.57f, 0.16f, 180),
        new WeatherPreset("heavy_rain", "大雨 / Heavy rain", 3, 0, "485A70", "8599AA", "B6C9DC", "8E9FB3", 0.18f, 1.00f, 1.40f, 0.77f, 0.10f, 90),
        new WeatherPreset("light_snow", "小雪 / Light snow", 0, 1, "9AA6B4", "D3DAE0", "E6ECF4", "D8DEE5", 0.42f, 0.94f, 0.70f, 0.62f, 0.20f, 180),
        new WeatherPreset("snow", "雪 / Snow", 0, 2, "8894A3", "C4CCD4", "DCE4EE", "C7CFD8", 0.32f, 0.98f, 0.95f, 0.74f, 0.14f, 210),
        new WeatherPreset("blizzard", "吹雪 / Blizzard", 0, 3, "A9B2BC", "DDE2E6", "D2DAE3", "C9D0D7", 0.24f, 1.00f, 1.30f, 0.93f, 0.08f, 120),
    });

    /// <summary>IDの一覧上の位置。未知のIDは -1。</summary>
    public static int IndexOf(string id)
    {
        for (int i = 0; i < All.Count; i++)
            if (All[i].Id == id) return i;
        return -1;
    }
}
