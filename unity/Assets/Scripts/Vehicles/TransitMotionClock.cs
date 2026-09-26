using System;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>コアの等加速度区間を時間で参照する。フレーム積分誤差と停車の二重加算を避ける。</summary>
public sealed class TransitMotionClock
{
    public readonly List<TransitMotionStep> Steps = new();
    public readonly List<double> Times = new();
    public readonly List<double> Distances = new();
    public readonly List<int> JourneyIndices = new();
    /// <summary>各区間で編成の向きが先頭区間と逆か(コアの journeys.reversed)。運転台と車両の並びの反転に使う。</summary>
    public readonly List<bool> Reversed = new();
    public double Duration { get; private set; }
    public double Length { get; private set; }
    /// <summary>1周の継ぎ目(先頭の停留所)で折り返さずに走り続ける路線か(環状運転など)。</summary>
    public bool Closed { get; private set; }

    public TransitMotionClock(List<TransitJourney> journeys)
    {
        if (journeys == null) return;
        for (int j = 0; j < journeys.Count; j++)
        {
            if (journeys[j].steps == null) continue;
            foreach (var step in journeys[j].steps)
            {
                if (step.duration_seconds <= 0 || double.IsNaN(step.duration_seconds) || double.IsInfinity(step.duration_seconds)) continue;
                Times.Add(Duration); Distances.Add(Length); Steps.Add(step);
                JourneyIndices.Add(j); Reversed.Add(journeys[j].reversed);
                Duration += step.duration_seconds + Math.Max(0, step.dwell_seconds);
                Length += step.length_m;
            }
        }
        Closed = Steps.Count > 0 && journeys.Count > 0 && !journeys[0].turnback;
    }

    public int AtTime(double seconds, out double distance, out bool stopped)
    {
        distance = 0; stopped = false;
        if (Duration <= 0 || Steps.Count == 0) return -1;
        seconds = ((seconds % Duration) + Duration) % Duration;
        int index = Find(Times, seconds);
        var step = Steps[index];
        double t = Math.Min(step.duration_seconds, seconds - Times[index]);
        stopped = seconds - Times[index] >= step.duration_seconds;
        double acceleration = (step.end_speed_mps - step.start_speed_mps) / step.duration_seconds;
        distance = Distances[index] + Math.Clamp(step.start_speed_mps*t + .5*acceleration*t*t, 0, step.length_m);
        return index;
    }

    public int AtDistance(double distance) => Steps.Count == 0 ? -1 : Find(Distances, Math.Clamp(distance, 0, Length));

    private static int Find(List<double> values, double value)
    {
        int lo = 0, hi = values.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (values[mid] <= value) lo = mid; else hi = mid - 1;
        }
        return lo;
    }
}
