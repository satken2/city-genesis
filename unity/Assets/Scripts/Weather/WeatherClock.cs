using UnityEngine;

/// <summary>
/// Rendering time advances from sunrise to sunset over about 10 minutes, independently of epochs. Skip nighttime, linking sunset
/// to the next sunrise through brief twilight; switch the sun from west to east while it is below the horizon and the scene is dark.
/// During epoch execution, set the sun and fade to black; after closing results, advance from sunrise into morning.
/// </summary>
public sealed class WeatherClock
{
    public const float DaySeconds = 600f;
    public const float TwilightSeconds = 20f;
    public const float SunsetSeconds = 1.5f;
    public const float FadeSeconds = 0.5f;
    public const float DuskSeconds = SunsetSeconds + FadeSeconds;
    public const float DawnSeconds = DuskSeconds;
    public const float MorningPhase = 0.12f;      // Position after fade-in or at session start, about 1.5 hours after sunrise.
    public const float MaxElevation = 55f;        // Noon sun elevation (degrees).
    public const float BelowHorizon = 0.02f;      // Below-horizon travel after sunset/before sunrise, as a fraction of daytime duration.
    public const float LowestElevation = -6f;     // Lowest below-horizon elevation (degrees).
    public const float TwilightDarkness = 0.55f;  // Twilight darkness between sunset and sunrise.

    public enum Stage { Day, Twilight, Dusk, Night, Dawn }

    public Stage Current { get; private set; } = Stage.Day;

    /// <summary>Sun-path position: 0=sunrise, 0.5=noon, 1=sunset; negative or above 1 means below the horizon.</summary>
    public float Phase { get; private set; } = MorningPhase;

    /// <summary>Ambient lighting: 0=day, 1=night. FadeAlpha produces a fully black screen.</summary>
    public float Darkness { get; private set; }

    /// <summary>Fullscreen black overlay independent of ambient light, fading only after sunset completes.</summary>
    public float FadeAlpha { get; private set; }

    /// <summary>Pause daytime progression for capture without stopping fade or sunrise effects.</summary>
    public bool Paused;

    private float progress, startPhase, startDarkness, startFadeAlpha;
    private bool dawnRequested;

    public float Elevation => ElevationAt(Phase);

    /// <summary>Sun bearing in clockwise degrees from north=0. Rises east, passes south, and sets west.</summary>
    public float Azimuth => 90f + 180f * Phase;

    /// <summary>Displayed time in hours, with sunrise at 6 and sunset at 18.</summary>
    public float Hours => 6f + 12f * Mathf.Clamp01(Phase);

    public bool IsDarkening => Current == Stage.Dusk || Current == Stage.Night;

    public static float ElevationAt(float phase)
    {
        if (phase < 0f) return LowestElevation * Mathf.Min(1f, -phase / BelowHorizon);
        if (phase > 1f) return LowestElevation * Mathf.Min(1f, (phase - 1f) / BelowHorizon);
        return MaxElevation * Mathf.Sin(Mathf.PI * phase);
    }

    /// <summary>Advance time. True only when fading ends and sunrise starts, allowing the caller to change weather then.</summary>
    public bool Advance(float seconds)
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
            case Stage.Dusk:
                progress += seconds / DuskSeconds;
                float t = Mathf.Clamp01(progress);
                float sunset = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t * DuskSeconds / SunsetSeconds));
                Phase = Mathf.Lerp(startPhase, 1f + BelowHorizon, sunset);
                Darkness = Mathf.Lerp(startDarkness, 1f, sunset);
                FadeAlpha = Mathf.Lerp(startFadeAlpha, 1f, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t * DuskSeconds - SunsetSeconds) / FadeSeconds)));
                if (progress >= 1f) { Current = Stage.Night; Phase = 1f + BelowHorizon; Darkness = 1f; }
                break;
            case Stage.Night:
                if (!dawnRequested) break;
                dawnRequested = false;
                Current = Stage.Dawn;
                progress = 0f;
                Phase = -BelowHorizon;
                return true;
            case Stage.Dawn:
                progress += seconds / DawnSeconds;
                float u = Mathf.Clamp01(progress);
                FadeAlpha = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(u * DawnSeconds / FadeSeconds));
                float sunrise = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((u * DawnSeconds - FadeSeconds) / SunsetSeconds));
                Phase = Mathf.Lerp(-BelowHorizon, MorningPhase, sunrise);
                Darkness = 1f - sunrise;
                if (progress >= 1f) { Current = Stage.Day; Phase = MorningPhase; Darkness = 0f; }
                break;
        }
        return false;
    }

    /// <summary>Start an epoch: fast-forward from the current position to sunset, then fade to black. Do nothing while already fading or dark.</summary>
    public void BeginNight()
    {
        if (IsDarkening) return;
        startPhase = Phase;
        startDarkness = Darkness;
        startFadeAlpha = FadeAlpha;
        progress = 0f;
        dawnRequested = false;
        Current = Stage.Dusk;
    }

    /// <summary>Recover after closing results or on failure. Do nothing unless darkened.</summary>
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

    /// <summary>Advance or rewind daytime for capture, only during the day and without crossing sunrise/sunset.</summary>
    public void Step(float hours)
    {
        if (Current != Stage.Day) return;
        Phase = Mathf.Clamp(Phase + hours / 12f, 0f, 0.999f);
    }
}
