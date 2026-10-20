using System;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>Sample core constant-acceleration sections by time, avoiding frame-integration errors and duplicate dwell-time accounting.</summary>
public sealed class TransitMotionClock
{
    public readonly List<TransitMotionStep> Steps = new();
    public readonly List<double> Times = new();
    public readonly List<double> Distances = new();
    public readonly List<int> JourneyIndices = new();
    /// <summary>Whether each section's train faces opposite the first section, from core journeys.reversed, used to reverse cab and vehicle order.</summary>
    public readonly List<bool> Reversed = new();
    /// <summary>Stop arrivals in circuit order. Dwell follows the arriving section's final step.</summary>
    public readonly List<StopArrival> Arrivals = new();
    public double Duration { get; private set; }
    public double Length { get; private set; }
    /// <summary>Whether the line continues through the circuit seam at the first stop without reversing, as in circular service.</summary>
    public bool Closed { get; private set; }

    /// <summary>Step is the arriving step index, Arrival the circuit-clock arrival time (s), and Dwell the core's dwell time (s).</summary>
    public readonly struct StopArrival
    {
        public readonly int Stop, Step;
        public readonly double Arrival, Dwell;
        public StopArrival(int stop, int step, double arrival, double dwell) { Stop = stop; Step = step; Arrival = arrival; Dwell = dwell; }
    }

    public TransitMotionClock(List<TransitJourney> journeys)
    {
        if (journeys == null) return;
        for (int j = 0; j < journeys.Count; j++)
        {
            if (journeys[j].steps == null) continue;
            int last = -1;
            foreach (var step in journeys[j].steps)
            {
                if (step.duration_seconds <= 0 || double.IsNaN(step.duration_seconds) || double.IsInfinity(step.duration_seconds)) continue;
                last = Steps.Count;
                Times.Add(Duration); Distances.Add(Length); Steps.Add(step);
                JourneyIndices.Add(j); Reversed.Add(journeys[j].reversed);
                Duration += step.duration_seconds + Math.Max(0, step.dwell_seconds);
                Length += step.length_m;
            }
            if (last >= 0)
                Arrivals.Add(new StopArrival(journeys[j].to_stop_id, last, Times[last] + Steps[last].duration_seconds, Math.Max(0, Steps[last].dwell_seconds)));
        }
        Closed = Steps.Count > 0 && journeys.Count > 0 && !journeys[0].turnback;
    }

    /// <summary>
    /// Time from arrival to departure (s). The final arrival at the first stop also waits for the remainder of cycle, matching Schedule.
    /// </summary>
    public double Stay(StopArrival arrival, double cycle)
        => arrival.Arrival + arrival.Dwell >= Duration - 1e-6 ? Math.Max(arrival.Dwell, cycle - arrival.Arrival) : arrival.Dwell;

    /// <summary>
    /// Current service for arrival: while dwelling use that service; otherwise use the next service's arrival/departure and the previous departure time (s).
    /// phases contains each train's phase within cycle (TransitVehicleManager.TryGetTrainPhases) and must be nonempty.
    /// </summary>
    public void Service(StopArrival arrival, List<double> phases, double cycle, double now,
        out double arriving, out double departing, out double previous, out bool dwelling)
    {
        double stay = Stay(arrival, cycle);
        double dwellSince = double.MaxValue, nextIn = double.MaxValue, departedAgo = double.MaxValue;
        foreach (double phase in phases)
        {
            double since = Mod(phase - arrival.Arrival, cycle);
            if (since < stay) dwellSince = Math.Min(dwellSince, since);
            nextIn = Math.Min(nextIn, cycle - since);
            departedAgo = Math.Min(departedAgo, Mod(since - stay, cycle));
        }
        dwelling = dwellSince < double.MaxValue;
        arriving = dwelling ? now - dwellSince : now + nextIn;
        departing = arriving + stay;
        previous = Math.Min(now - departedAgo, arriving);
    }

    /// <summary>Wrap to [0, cycle).</summary>
    public static double Mod(double value, double cycle) => (value % cycle + cycle) % cycle;

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

    /// <summary>
    /// Map time for trains departing every cycle seconds onto the circuit clock. After completing one circuit (Duration), wait at the first stop
    /// for the remainder of cycle, matching the core's effective headway rule. Continue running if cycle is no longer than the circuit.
    /// </summary>
    public double Schedule(double seconds, double cycle)
    {
        if (Duration <= 0 || cycle <= Duration) return seconds;
        double phase = ((seconds % cycle) + cycle) % cycle;
        // While waiting, remain at the end of the final section's dwell at the first stop.
        return phase < Duration ? phase : Duration * (1 - 1e-9);
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
