/// <summary>同じエポックの再読込では何もしない。セッション境界では基準値を捨てる。</summary>
public sealed class WeatherEpochTracker
{
    public int? Epoch { get; private set; }
    public void Reset() => Epoch = null;

    /// <summary>進んだエポック数を返す。基準値が無い(セッション開始直後)・巻き戻った場合は0で、基準値だけ置き換える。</summary>
    public int Observe(int epoch)
    {
        if (epoch < 0) return 0;
        int advanced = Epoch.HasValue && epoch > Epoch.Value ? epoch - Epoch.Value : 0;
        Epoch = epoch;
        return advanced;
    }
}
