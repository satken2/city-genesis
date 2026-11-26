using UnityEngine;

/// <summary>
/// Rendering time doubles as the epoch countdown: one cycle runs from morning to the low evening sun, and the next epoch
/// starts there (the UI sets the cycle length and whether time is stopped). Night is never shown during play; the sun
/// sets only after an epoch's calculation finishes, so the city stays viewable while it runs: the evening holds during the
/// calculation, sunset and darkness follow it, and closing the results brings sunrise and the next cycle's morning.
/// </summary>
public sealed class WeatherClock
{
    public const float DefaultCycleSeconds = 600f;
    public const float EveningPhase = 0.97f;      // End of a cycle: low evening sun (about 5 degrees) that still lights the city.
    public const float RushSeconds = 1f;          // Time to reach the evening when an epoch starts before the cycle ends.
    public const float SunsetSeconds = 1f;
    public const float SunriseSeconds = 1.5f;
    public const float FadeSeconds = 0.5f;
    public const float DuskSeconds = SunsetSeconds + FadeSeconds;
    public const float DawnSeconds = SunriseSeconds + FadeSeconds;
    public const float MorningPhase = 0.12f;      // Start of a cycle, about 1.5 hours after sunrise.
    public const float MaxElevation = 55f;        // Noon sun elevation (degrees).
    public const float BelowHorizon = 0.02f;      // Below-horizon travel after sunset/before sunrise, as a fraction of daytime duration.
    public const float LowestElevation = -6f;     // Lowest below-horizon elevation (degrees).

    public enum Stage { Day, Dusk, Night, Dawn }

    public Stage Current { get; private set; } = Stage.Day;

    /// <summary>Sun-path position: 0=sunrise, 0.5=noon, 1=sunset; negative or above 1 means below the horizon.</summary>
    public float Phase { get; private set; } = MorningPhase;

    /// <summary>Ambient lighting: 0=day, 1=night.</summary>
    public float Darkness { get; private set; }

    public float FadeAlpha { get; private set; }
    public bool IsPresenting => Current == Stage.Dusk || Current == Stage.Night || Current == Stage.Dawn;
    public bool IsDarkening => Current == Stage.Dusk || Current == Stage.Night;

    /// <summary>Real seconds from morning to evening. The sun's speed follows it, so changing it keeps the current position.</summary>
    public float CycleSeconds { get; set; } = DefaultCycleSeconds;

    /// <summary>
    /// The player stopped time from the UI. Starts true so nothing runs before the UI says so.
    /// Separate from the traffic pause: vehicles and pedestrians keep moving.
    /// </summary>
    public bool Stopped { get; set; } = true;

    /// <summary>Time freeze for capture. Holds the daytime; epoch presentations still run to the end.</summary>
    public bool Paused;

    private float progress, startPhase, startDarkness, startFadeAlpha;
    private bool dawnRequested, executing;

    public float PhasePerSecond => (EveningPhase - MorningPhase) / Mathf.Max(1f, CycleSeconds);

    /// <summary>Real seconds until the evening; 0 outside the day or while the epoch runs.</summary>
    public float RemainingSeconds => Current == Stage.Day && !executing ? Mathf.Max(0f, EveningPhase - Phase) / PhasePerSecond : 0f;

    /// <summary>Whether the countdown is decreasing now (the UI counts down locally only while this holds).</summary>
    public bool Running => Current == Stage.Day && !executing && !Stopped && !Paused && Phase < EveningPhase;

    /// <summary>The cycle has ended and the day waits in the evening for the next epoch.</summary>
    public bool AtEvening => Current == Stage.Day && !executing && Phase >= EveningPhase;

    public float Elevation => ElevationAt(Phase);

    /// <summary>Sun bearing in clockwise degrees from north=0. Rises east, passes south, and sets west.</summary>
    public float Azimuth => AzimuthAt(Phase);

    /// <summary>Displayed time in hours, with sunrise at 6 and sunset at 18.</summary>
    public float Hours => 6f + 12f * Mathf.Clamp01(Phase);

    public static float AzimuthAt(float phase) => 90f + 180f * phase;

    public static float ElevationAt(float phase)
    {
        if (phase < 0f) return LowestElevation * Mathf.Min(1f, -phase / BelowHorizon);
        if (phase > 1f) return LowestElevation * Mathf.Min(1f, (phase - 1f) / BelowHorizon);
        return MaxElevation * Mathf.Sin(Mathf.PI * phase);
    }

    /// <summary>
    /// epochExecuting is true from the epoch's start until its results close. The day then moves to the evening and holds there.
    /// Returns true only as sunrise begins: the boundary for switching weather before the sky brightens.
    /// </summary>
    public bool Advance(float seconds, bool epochExecuting)
    {
        executing = epochExecuting;
        switch (Current)
        {
            case Stage.Day:
                if (executing) Phase = Mathf.MoveTowards(Phase, EveningPhase, seconds * (EveningPhase - MorningPhase) / RushSeconds);
                else if (!Stopped && !Paused) Phase = Mathf.Min(EveningPhase, Phase + seconds * PhasePerSecond);
                break;
            case Stage.Dusk:
                progress += seconds;
                float sunset = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(progress / SunsetSeconds));
                Phase = Mathf.Lerp(startPhase, 1f + BelowHorizon, sunset);
                Darkness = Mathf.Lerp(startDarkness, 1f, sunset);
                FadeAlpha = Mathf.Lerp(startFadeAlpha, 1f,
                    Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((progress - SunsetSeconds) / FadeSeconds)));
                if (progress >= DuskSeconds) { Current = Stage.Night; Phase = 1f + BelowHorizon; Darkness = FadeAlpha = 1f; }
                break;
            case Stage.Night:
                if (!dawnRequested) break;
                dawnRequested = false;
                Current = Stage.Dawn;
                progress = 0f;
                Phase = -BelowHorizon;
                return true;
            case Stage.Dawn:
                progress += seconds;
                FadeAlpha = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(progress / FadeSeconds));
                float sunrise = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((progress - FadeSeconds) / SunriseSeconds));
                Phase = Mathf.Lerp(-BelowHorizon, MorningPhase, sunrise);
                Darkness = 1f - sunrise;
                if (progress >= DawnSeconds) { Current = Stage.Day; Phase = MorningPhase; Darkness = FadeAlpha = 0f; }
                break;
        }
        return false;
    }

    public void BeginNight()
    {
        dawnRequested = false;
        if (IsDarkening) return;
        // A sun still below the horizon during sunrise moves to the west so the sunset does not cross the daytime sky.
        startPhase = Phase < 0f ? 1f + BelowHorizon : Phase;
        startDarkness = Darkness;
        startFadeAlpha = FadeAlpha;
        progress = 0f;
        Current = Stage.Dusk;
    }

    public void RequestDawn()
    {
        if (IsDarkening) dawnRequested = true;
    }

    /// <summary>Set morning directly while the scene is hidden, at session start/end.</summary>
    public void StartMorning()
    {
        Current = Stage.Day;
        Phase = MorningPhase;
        Darkness = 0f;
        FadeAlpha = 0f;
        dawnRequested = false;
    }

    /// <summary>Advance or rewind daytime for capture, only during the day and without passing the evening.</summary>
    public void Step(float hours)
    {
        if (Current != Stage.Day) return;
        Phase = Mathf.Clamp(Phase + hours / 12f, 0f, EveningPhase);
    }
}
