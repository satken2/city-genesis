using System;
using UnityEngine;

/// <summary>
/// Clock and pause state for traffic animation: ordinary cars, pedestrians, public transport, ropeways, aircraft, and ships.
/// Avoid Time.timeScale because it also stops camera inertia, drag-speed division, and retry waits;
/// each Manager instead multiplies elapsed time by Scale. Weather, time of day, water, and fires keep running.
/// </summary>
public static class TrafficClock
{
    public static bool Paused { get; private set; }
    public static float Scale => Paused ? 0f : 1f;
    public static event Action<bool> PausedChanged;
    private static float pausedAt, pausedTotal;

    /// <summary>Time.time that stops while paused, used for timed effects such as contrail and wake lifetimes.</summary>
    public static float Time => (Paused ? pausedAt : UnityEngine.Time.time) - pausedTotal;

    public static void SetPaused(bool paused)
    {
        if (Paused == paused) return;
        if (paused) pausedAt = UnityEngine.Time.time;
        else pausedTotal += UnityEngine.Time.time - pausedAt;
        Paused = paused;
        PausedChanged?.Invoke(paused);
    }

    public static void Toggle() => SetPaused(!Paused);
}
