using System.Collections.Generic;

/// <summary>
/// 天気の移り変わり。雲が増えて降り出し、弱まって晴れていく順に、隣り合う天気へ1歩ずつ進む。
/// 次の天気はメトロポリス法で選ぶ。1歩ごとの滞在の目標を「出現割合 ÷ 平均持続時間」にしているので、
/// 長く見たときに各天気でいる時間の割合が気候と季節の重み(WeatherClimate)に一致する。
/// </summary>
public static class WeatherChain
{
    private static readonly string[,] Pairs =
    {
        { "clear", "high_clouds" }, { "clear", "fair" }, { "clear", "fog" },
        { "high_clouds", "fair" }, { "high_clouds", "broken_clouds" }, { "high_clouds", "overcast" },
        { "fair", "broken_clouds" },
        { "broken_clouds", "overcast" }, { "broken_clouds", "rain_clouds" },
        { "overcast", "rain_clouds" }, { "overcast", "fog" }, { "overcast", "drizzle" }, { "overcast", "light_snow" },
        { "rain_clouds", "drizzle" }, { "rain_clouds", "rain" }, { "rain_clouds", "light_snow" }, { "rain_clouds", "snow" },
        { "fog", "drizzle" },
        { "drizzle", "rain" }, { "drizzle", "light_snow" },
        { "rain", "heavy_rain" }, { "rain", "snow" },
        { "light_snow", "snow" },
        { "snow", "blizzard" },
    };

    /// <summary>天気ごとの隣の天気。添字は WeatherPreset.All と対応する。</summary>
    public static readonly IReadOnlyList<int>[] Neighbors = BuildNeighbors();

    private static IReadOnlyList<int>[] BuildNeighbors()
    {
        var lists = new List<int>[WeatherPreset.All.Count];
        for (int i = 0; i < lists.Length; i++) lists[i] = new List<int>();
        for (int i = 0; i < Pairs.GetLength(0); i++)
        {
            int a = WeatherPreset.IndexOf(Pairs[i, 0]), b = WeatherPreset.IndexOf(Pairs[i, 1]);
            lists[a].Add(b);
            lists[b].Add(a);
        }
        return lists;
    }

    /// <summary>出現時間の割合に比例して1つ選ぶ。エポック明けやセッション開始など、途中から見始めるときに使う。</summary>
    public static int Sample(System.Random random, IReadOnlyList<int> weights)
    {
        int total = 0;
        for (int i = 0; i < weights.Count; i++) total += weights[i];
        int roll = random.Next(total);
        for (int i = 0; i < weights.Count; i++)
        {
            roll -= weights[i];
            if (roll < 0) return i;
        }
        return weights.Count - 1;
    }

    /// <summary>
    /// 今の天気が終わったときの次の天気。隣の天気を1つ候補に選び、目標との比の確率で受け入れる
    /// (受け入れなければ同じ天気が続く)。今の天気の重みが0(固定の解除直後や季節の変わり目)なら
    /// 重みのある隣へ必ず移り、隣がすべて0なら全体から選び直す。
    /// </summary>
    public static int Step(System.Random random, int current, IReadOnlyList<int> weights)
    {
        var near = Neighbors[current];
        if (weights[current] <= 0)
        {
            var open = new List<int>();
            foreach (int next in near) if (weights[next] > 0) open.Add(next);
            return open.Count > 0 ? open[random.Next(open.Count)] : Sample(random, weights);
        }
        int proposal = near[random.Next(near.Count)];
        if (weights[proposal] <= 0) return current;
        // 候補の選ばれやすさは隣の数に反比例するので、その偏りを受け入れ確率で打ち消す。
        double here = Target(current, weights) / near.Count;
        double there = Target(proposal, weights) / Neighbors[proposal].Count;
        return random.NextDouble() < there / here ? proposal : current;
    }

    /// <summary>1回の持続時間(秒)。平均の0.5〜1.5倍の一様乱数。</summary>
    public static float Dwell(System.Random random, int preset) =>
        WeatherPreset.All[preset].Duration * (0.5f + (float)random.NextDouble());

    /// <summary>重みのある天気どうしが隣接関係だけでつながっているか。つながっていないと届かない天気が生じる。</summary>
    public static bool Connected(IReadOnlyList<int> weights)
    {
        int start = -1, open = 0;
        for (int i = 0; i < weights.Count; i++)
            if (weights[i] > 0) { open++; if (start < 0) start = i; }
        if (start < 0) return false;
        var seen = new bool[weights.Count];
        var stack = new Stack<int>();
        stack.Push(start);
        seen[start] = true;
        int reached = 0;
        while (stack.Count > 0)
        {
            int here = stack.Pop();
            reached++;
            foreach (int next in Neighbors[here])
            {
                if (seen[next] || weights[next] <= 0) continue;
                seen[next] = true;
                stack.Push(next);
            }
        }
        return reached == open;
    }

    private static double Target(int preset, IReadOnlyList<int> weights) => weights[preset] / (double)WeatherPreset.All[preset].Duration;
}
