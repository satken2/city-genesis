using System.Collections.Generic;

/// <summary>
/// Weather transitions primarily between neighboring states, while still reaching valid states when zero-probability weather disconnects the graph.
/// Select the next state with the Metropolis method, targeting occurrence share / mean duration per step,
/// so long-term time spent in each state matches the city's monthly WeatherClimate settings.
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

    /// <summary>Neighbors for each weather state, indexed like WeatherPreset.All.</summary>
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

    /// <summary>Select proportionally to time share, used when starting observation midway, such as after epochs or at session start.</summary>
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
    /// Choose the next state when current weather ends, proposing mostly neighbors and accepting by the target/proposal probability ratio.
    /// If the current state's weight is zero, reselect from monthly occurrence shares.
    /// </summary>
    public static int Step(System.Random random, int current, IReadOnlyList<int> weights)
    {
        var near = Neighbors[current];
        if (weights[current] <= 0) return Sample(random, weights);
        // Include a small proportion of proposals across all valid states so zero-probability weather cannot disconnect transitions.
        int proposal = random.NextDouble() < 0.1 ? random.Next(weights.Count) : near[random.Next(near.Count)];
        if (weights[proposal] <= 0) return current;
        double here = Target(current, weights) * ProposalProbability(current, proposal);
        double there = Target(proposal, weights) * ProposalProbability(proposal, current);
        return random.NextDouble() < there / here ? proposal : current;
    }

    private static double ProposalProbability(int from, int to)
    {
        double probability = 0.1 / WeatherPreset.All.Count;
        foreach (int neighbor in Neighbors[from])
            if (neighbor == to) probability += 0.9 / Neighbors[from].Count;
        return probability;
    }

    /// <summary>One duration (seconds), uniform from 0.5 to 1.5 times the mean.</summary>
    public static float Dwell(System.Random random, int preset) =>
        WeatherPreset.All[preset].Duration * (0.5f + (float)random.NextDouble());

    /// <summary>Whether positive-weight states connect through neighbor links alone; disconnection would leave some states unreachable.</summary>
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
