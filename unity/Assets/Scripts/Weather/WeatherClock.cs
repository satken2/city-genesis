using UnityEngine;

/// <summary>
/// Rendering time advances from sunrise to sunset over about 10 minutes, independently of epochs. Skip nighttime, linking sunset
/// to the next sunrise through brief twilight; switch the sun from west to east while it is below the horizon and the scene is dark.
/// </summary>
public sealed class WeatherClock
{
    public const float DaySeconds = 600f;
    public const float TwilightSeconds = 20f;
    public const float MorningPhase = 0.12f;      // Position at session start, about 1.5 hours after sunrise.
    public const float MaxElevation = 55f;        // Noon sun elevation (degrees).
    public const float BelowHorizon = 0.02f;      // Below-horizon travel after sunset/before sunrise, as a fraction of daytime duration.
    public const float LowestElevation = -6f;     // Lowest below-horizon elevation (degrees).
    public const float TwilightDarkness = 0.55f;  // Twilight darkness between sunset and sunrise.

    public enum Stage { Day, Twilight }

    public Stage Current { get; private set; } = Stage.Day;

    /// <summary>Sun-path position: 0=sunrise, 0.5=noon, 1=sunset; negative or above 1 means below the horizon.</summary>
    public float Phase { get; private set; } = MorningPhase;

    /// <summary>Ambient lighting: 0=day, 1=night.</summary>
    public float Darkness { get; private set; }

    /// <summary>Pause daytime progression for capture without stopping twilight.</summary>
    public bool Paused;

    private float progress;

    public float Elevation => ElevationAt(Phase);

    /// <summary>Sun bearing in clockwise degrees from north=0. Rises east, passes south, and sets west.</summary>
    public float Azimuth => 90f + 180f * Phase;

    /// <summary>Displayed time in hours, with sunrise at 6 and sunset at 18.</summary>
    public float Hours => 6f + 12f * Mathf.Clamp01(Phase);

    public static float ElevationAt(float phase)
    {
        if (phase < 0f) return LowestElevation * Mathf.Min(1f, -phase / BelowHorizon);
        if (phase > 1f) return LowestElevation * Mathf.Min(1f, (phase - 1f) / BelowHorizon);
        return MaxElevation * Mathf.Sin(Mathf.PI * phase);
    }

    public void Advance(float seconds)
    {
        switch (Current)
        {
            case Stage.Day:
                if (!Paused) Phase += seconds / DaySeconds;
                if (Phase >= 1f) { Current = Stage.Twilight; progress = 0f; }
                break;
            case Stage.Twilight:
                progress += seconds / TwilightSeconds;
                if (progress >= 1f) { Current = Stage.Day; Phase = 0f; Darkness = 0f; break; }
                Phase = progress < 0.5f ? 1f + BelowHorizon * progress * 2f : -BelowHorizon * (2f - progress * 2f);
                Darkness = TwilightDarkness * Mathf.Sin(Mathf.PI * progress);
                break;
        }
    }

    /// <summary>Set morning directly while the scene is hidden, at session start/end.</summary>
    public void StartMorning()
    {
        Current = Stage.Day;
        Phase = MorningPhase;
        Darkness = 0f;
    }

    /// <summary>Advance or rewind daytime for capture, only during the day and without crossing sunrise/sunset.</summary>
    public void Step(float hours)
    {
        if (Current != Stage.Day) return;
        Phase = Mathf.Clamp(Phase + hours / 12f, 0f, 0.999f);
    }
}
