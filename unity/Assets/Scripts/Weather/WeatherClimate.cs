using System.Collections.Generic;

/// <summary>
/// 気候区分(マップごとに固定。GET /api/v1/city の climate)と季節から、天気ごとの出現時間の割合(%)を引く表。
/// 値は各気候の代表地点の月別平年値(降水日数・降雪日数・日照)を目安にしている。雨や雪の日でも一日中
/// 降り続くわけではないので、降っている時間の割合は降水日数の割合のおよそ半分とした。
/// 0 の天気はその気候・季節では自然には起きない(撮影用に手動で選ぶことはできる)。
/// </summary>
public static class WeatherClimate
{
    public const int Spring = 0, Summer = 1, Autumn = 2, Winter = 3;

    // 気候の並びは Constants.CLIMATE_TYPES と同じ。列は WeatherPreset.All の順
    // (快晴・薄雲・晴れ・晴れ間・曇り・雨雲・霧・霧雨・雨・大雨・小雪・雪・吹雪)で、各行の合計は100。
    private static readonly int[][][] Table =
    {
        new[]
        {
            new[] { 25, 19, 14, 7, 11, 5, 3, 7, 8, 1, 0, 0, 0 }, // 温暖湿潤(福岡・高松)の春
            new[] { 23, 11, 20, 8, 11, 7, 2, 5, 9, 4, 0, 0, 0 }, // 温暖湿潤の夏。梅雨と夕立・台風
            new[] { 34, 17, 14, 7, 9, 4, 3, 5, 5, 2, 0, 0, 0 },  // 温暖湿潤の秋
            new[] { 29, 16, 13, 7, 13, 6, 1, 5, 6, 0, 3, 1, 0 }, // 温暖湿潤の冬。まれに雪がちらつく
        },
        new[]
        {
            new[] { 20, 17, 16, 8, 12, 6, 3, 7, 8, 3, 0, 0, 0 }, // 亜熱帯(那覇・香港)の春
            new[] { 23, 9, 23, 10, 8, 6, 0, 3, 10, 8, 0, 0, 0 }, // 亜熱帯の夏。スコールと台風の大雨
            new[] { 34, 16, 16, 7, 8, 4, 1, 4, 6, 4, 0, 0, 0 },  // 亜熱帯の秋
            new[] { 25, 18, 15, 8, 13, 6, 1, 7, 6, 1, 0, 0, 0 }, // 亜熱帯の冬。雪は降らない
        },
        new[]
        {
            new[] { 28, 17, 14, 8, 11, 5, 3, 5, 7, 2, 0, 0, 0 }, // 湿潤大陸性(ニューヨーク)の春
            new[] { 32, 13, 20, 8, 8, 5, 1, 3, 6, 4, 0, 0, 0 },  // 湿潤大陸性の夏。雷雨
            new[] { 36, 17, 14, 7, 9, 4, 2, 4, 5, 2, 0, 0, 0 },  // 湿潤大陸性の秋
            new[] { 30, 14, 12, 7, 14, 7, 2, 3, 4, 1, 3, 2, 1 }, // 湿潤大陸性の冬。降水の3分の1ほどが雪
        },
        new[]
        {
            new[] { 42, 18, 13, 7, 8, 4, 1, 3, 3, 1, 0, 0, 0 },  // 地中海性(サントリーニ)の春
            new[] { 67, 18, 9, 2, 2, 1, 0, 0, 1, 0, 0, 0, 0 },   // 地中海性の夏。ほぼ雨が降らない
            new[] { 47, 17, 13, 6, 7, 3, 1, 2, 3, 1, 0, 0, 0 },  // 地中海性の秋
            new[] { 27, 14, 14, 8, 14, 8, 1, 4, 7, 3, 0, 0, 0 }, // 地中海性の冬。雨季
        },
        new[]
        {
            new[] { 53, 22, 13, 6, 4, 2, 0, 0, 0, 0, 0, 0, 0 },  // ステップ(ダカール)の春。乾季
            new[] { 23, 13, 22, 11, 12, 9, 0, 1, 5, 4, 0, 0, 0 }, // ステップの夏。雨季でスコールが多い
            new[] { 33, 16, 19, 10, 9, 6, 0, 1, 3, 3, 0, 0, 0 }, // ステップの秋。雨季の終わり
            new[] { 50, 24, 12, 6, 5, 2, 0, 1, 0, 0, 0, 0, 0 },  // ステップの冬。乾季
        },
        new[]
        {
            new[] { 55, 21, 11, 5, 4, 2, 0, 1, 1, 0, 0, 0, 0 },  // 砂漠(マナーマ)の春
            new[] { 68, 25, 4, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0 },   // 砂漠の夏。雨は降らない
            new[] { 61, 22, 8, 4, 3, 1, 0, 1, 0, 0, 0, 0, 0 },   // 砂漠の秋
            new[] { 50, 20, 12, 6, 6, 3, 0, 2, 1, 0, 0, 0, 0 },  // 砂漠の冬。弱い雨がまれに降る
        },
        new[]
        {
            new[] { 25, 16, 13, 7, 14, 7, 2, 4, 5, 0, 4, 3, 0 }, // 亜寒帯(札幌)の春。3〜4月は雪
            new[] { 25, 14, 18, 8, 12, 7, 3, 4, 6, 3, 0, 0, 0 }, // 亜寒帯の夏
            new[] { 25, 14, 11, 6, 14, 7, 2, 5, 8, 2, 4, 2, 0 }, // 亜寒帯の秋。11月は雪
            new[] { 18, 11, 10, 6, 20, 7, 1, 0, 0, 0, 12, 11, 4 }, // 亜寒帯の冬。降水はすべて雪
        },
    };

    private static readonly string[] Names =
        { "温暖湿潤 / Humid temperate", "亜熱帯 / Subtropical", "湿潤大陸性 / Humid continental", "地中海性 / Mediterranean", "ステップ / Steppe", "砂漠 / Desert", "亜寒帯 / Subarctic" };

    private static readonly string[] SeasonNames = { "春 / Spring", "夏 / Summer", "秋 / Autumn", "冬 / Winter" };

    public static int ClimateCount => Table.Length;

    /// <summary>未知の値(バックエンドが古い・マップ指定なし)は温暖湿潤として扱う。</summary>
    public static int Normalize(int climate) =>
        System.Array.IndexOf(Constants.CLIMATE_TYPES, climate) >= 0 ? climate : Constants.CLIMATE_TEMPERATE;

    /// <summary>都市の日付(1800年1月1日からの日数。1か月30日・1年360日の暦)の季節。北半球の暦月で分ける。</summary>
    public static int SeasonOf(int currentDate)
    {
        int month = ((currentDate % 360) + 360) % 360 / 30 + 1;
        if (month >= 3 && month <= 5) return Spring;
        if (month >= 6 && month <= 8) return Summer;
        if (month >= 9 && month <= 11) return Autumn;
        return Winter;
    }

    /// <summary>天気ごとの出現割合。添字は WeatherPreset.All と対応する。</summary>
    public static IReadOnlyList<int> Weights(int climate, int season) => Table[Row(climate)][season];

    public static string Name(int climate) => Names[Row(climate)];

    public static string SeasonName(int season) => SeasonNames[season];

    private static int Row(int climate) => System.Array.IndexOf(Constants.CLIMATE_TYPES, Normalize(climate));
}
