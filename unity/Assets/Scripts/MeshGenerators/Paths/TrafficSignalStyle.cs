/// <summary>
/// 信号機の外観様式。様式ごとに柱・アーム・灯器のモデル(Models/Props/traffic_signal_{Key}_{pole|arm|head})を持つ。
/// 言語設定やマップで様式を切り替えられるよう様式単位でまとめているが、現在は日本式に固定している。
/// </summary>
public sealed class TrafficSignalStyle
{
    /// <summary>横型3灯(左から青・黄・赤)の灯器を片持ちアームで車線の上へ張り出す日本式。</summary>
    public static readonly TrafficSignalStyle Japan = new TrafficSignalStyle("jp", 0.9f);

    public static TrafficSignalStyle Current => Japan;

    public string Key { get; }

    /// <summary>柱の軸から灯器の吊り金具までの最短距離(m)。灯器の半幅と柱の取付バンドの半径に余裕を足した値で、灯器が柱に食い込まない。</summary>
    public float MinReach { get; }

    private TrafficSignalStyle(string key, float minReach)
    {
        Key = key;
        MinReach = minReach;
    }

    public string ModelPath(string part) => $"Models/Props/traffic_signal_{Key}_{part}";
}
