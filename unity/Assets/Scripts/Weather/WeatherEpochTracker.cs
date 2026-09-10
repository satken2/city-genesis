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

    public static int Next(System.Random random, int current, int count)
    {
        if (count < 2) return 0;
        int next = random.Next(count - 1);
        return next >= current ? next + 1 : next;
    }
}
