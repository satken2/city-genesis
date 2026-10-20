/// <summary>Ignore reloads within the same epoch. Discard the baseline at session boundaries.</summary>
public sealed class WeatherEpochTracker
{
    public int? Epoch { get; private set; }
    public void Reset() => Epoch = null;

    /// <summary>Return epochs advanced. With no baseline after session start, or on rollback, return 0 and only replace the baseline.</summary>
    public int Observe(int epoch)
    {
        if (epoch < 0) return 0;
        int advanced = Epoch.HasValue && epoch > Epoch.Value ? epoch - Epoch.Value : 0;
        Epoch = epoch;
        return advanced;
    }
}
