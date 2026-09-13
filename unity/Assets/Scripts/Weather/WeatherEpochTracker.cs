/// <summary>同じエポックの再読込では抽選しない。セッション境界では基準値を捨てる。</summary>
public sealed class WeatherEpochTracker
{
    public int? Epoch { get; private set; }
    public void Reset() => Epoch = null;

    public int Observe(int epoch)
    {
        if (epoch < 0) return 0;
        int advanced = Epoch.HasValue && epoch > Epoch.Value ? epoch - Epoch.Value : 0;
        Epoch = epoch;
        return advanced;
    }

    public static int Next(System.Random random)
    {
        int total = 0;
        foreach (var preset in WeatherPreset.All) total += preset.Weight;
        int roll = random.Next(total);
        for (int i = 0; i < WeatherPreset.All.Count; i++)
        {
            roll -= WeatherPreset.All[i].Weight;
            if (roll < 0) return i;
        }
        return WeatherPreset.All.Count - 1;
    }
}
