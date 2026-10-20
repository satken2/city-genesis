/// <summary>
/// Signal visual style. Each style has post/arm/head models (Models/Props/traffic_signal_{Key}_{pole|arm|head}).
/// Grouped for future language/map-based style selection; currently fixed to Japanese style.
/// </summary>
public sealed class TrafficSignalStyle
{
    /// <summary>Japanese style: horizontal green/yellow/red heads (left→right), cantilevered above lanes.</summary>
    public static readonly TrafficSignalStyle Japan = new TrafficSignalStyle("jp", 0.9f);

    public static TrafficSignalStyle Current => Japan;

    public string Key { get; }

    /// <summary>Minimum post-axis to head-hanger distance (m): head half-width + mounting-band radius + margin, preventing head/post overlap.</summary>
    public float MinReach { get; }

    private TrafficSignalStyle(string key, float minReach)
    {
        Key = key;
        MinReach = minReach;
    }

    public string ModelPath(string part) => $"Models/Props/traffic_signal_{Key}_{part}";
}
