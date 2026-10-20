using System.Collections.Generic;

/// <summary>City climate settings received from Core. Arrays run from January through December.</summary>
public sealed class WeatherClimateSettings
{
    public string name;
    public WeatherClimateMonth[] months;
}

public sealed class WeatherClimateMonth
{
    public float temperature, precipitation, sunlight;
    public Dictionary<string, int> weather;
}

/// <summary>Extract monthly occurrence shares from saved city values. Core manages climate presets.</summary>
public static class WeatherClimate
{
    public const int Spring = 0, Summer = 1, Autumn = 2, Winter = 3;
    private static readonly string[] SeasonNames = { "春 / Spring", "夏 / Summer", "秋 / Autumn", "冬 / Winter" };

    public static int MonthOf(int currentDate) => ((currentDate % 360) + 360) % 360 / 30;

    public static int SeasonOf(int currentDate)
    {
        int month = MonthOf(currentDate) + 1;
        if (month >= 3 && month <= 5) return Spring;
        if (month >= 6 && month <= 8) return Summer;
        if (month >= 9 && month <= 11) return Autumn;
        return Winter;
    }

    public static string SeasonName(int season) => SeasonNames[season];

    public static IReadOnlyList<int> Weights(WeatherClimateSettings climate, int currentDate)
    {
        var values = climate.months[MonthOf(currentDate)].weather;
        var weights = new int[WeatherPreset.All.Count];
        for (int i = 0; i < weights.Length; i++) weights[i] = values[WeatherPreset.All[i].Id];
        return weights;
    }

    /// <summary>Render clear skies before fetching, then sample from the received city settings.</summary>
    public static IReadOnlyList<int> InitialWeights()
    {
        var weights = new int[WeatherPreset.All.Count];
        weights[WeatherPreset.IndexOf("clear")] = 100;
        return weights;
    }
}
