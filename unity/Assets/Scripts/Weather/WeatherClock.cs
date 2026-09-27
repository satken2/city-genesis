using UnityEngine;

/// <summary>
/// 描画用の時刻。日の出から日没まで約10分で進み、エポックとは無関係に流れる。夜は描かず、日没のあとは
/// 薄明を短く挟んで次の日の出へつなぐ(太陽の方位が西から東へ飛ぶのを、太陽が地平線下にある暗い間に済ませる)。
/// エポック実行中は日を沈めて暗転し、結果画面を閉じると日の出から朝へ進める。
/// </summary>
public sealed class WeatherClock
{
    public const float DaySeconds = 600f;
    public const float TwilightSeconds = 20f;
    public const float SunsetSeconds = 1.5f;
    public const float FadeSeconds = 0.5f;
    public const float DuskSeconds = SunsetSeconds + FadeSeconds;
    public const float DawnSeconds = DuskSeconds;
    public const float MorningPhase = 0.12f;      // 暗転明け・セッション開始の位置(日の出から約1.5時間)
    public const float MaxElevation = 55f;        // 正午の太陽高度(度)
    public const float BelowHorizon = 0.02f;      // 日没後・日の出前に地平線下へ沈む量(昼の長さに対する割合)
    public const float LowestElevation = -6f;     // 地平線下へ最も沈んだときの高度(度)
    public const float TwilightDarkness = 0.55f;  // 日没と日の出の間の薄明の暗さ

    public enum Stage { Day, Twilight, Dusk, Night, Dawn }

    public Stage Current { get; private set; } = Stage.Day;

    /// <summary>太陽の通り道の位置。0=日の出、0.5=正午、1=日没。負と1超は地平線下。</summary>
    public float Phase { get; private set; } = MorningPhase;

    /// <summary>0=昼の環境光、1=夜の環境光。完全な黒画面はFadeAlphaで作る。</summary>
    public float Darkness { get; private set; }

    /// <summary>環境光と独立した全画面の黒幕。日没完了後にだけフェードする。</summary>
    public float FadeAlpha { get; private set; }

    /// <summary>昼の時刻の進行を止める(撮影用)。暗転と日の出の演出は止めない。</summary>
    public bool Paused;

    private float progress, startPhase, startDarkness, startFadeAlpha;
    private bool dawnRequested;

    public float Elevation => ElevationAt(Phase);

    /// <summary>太陽の方位(北=0の時計回りの度)。東から昇り、南を通って西へ沈む。</summary>
    public float Azimuth => 90f + 180f * Phase;

    /// <summary>表示用の時刻(時)。日の出を6時、日没を18時とする。</summary>
    public float Hours => 6f + 12f * Mathf.Clamp01(Phase);

    public bool IsDarkening => Current == Stage.Dusk || Current == Stage.Night;

    public static float ElevationAt(float phase)
    {
        if (phase < 0f) return LowestElevation * Mathf.Min(1f, -phase / BelowHorizon);
        if (phase > 1f) return LowestElevation * Mathf.Min(1f, (phase - 1f) / BelowHorizon);
        return MaxElevation * Mathf.Sin(Mathf.PI * phase);
    }

    /// <summary>時刻を進める。暗転が明けて日の出を始めた瞬間だけ true を返す(呼び出し側はそこで天気を切り替える)。</summary>
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

    /// <summary>エポック開始。今の位置から日没まで早送りし、そのまま暗転する。暗転へ向かう途中・暗転中なら何もしない。</summary>
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

    /// <summary>結果画面のクローズまたは失敗による復帰。暗転していなければ何もしない。</summary>
    public void RequestDawn()
    {
        if (IsDarkening) dawnRequested = true;
    }

    /// <summary>画面が見えないうち(セッション開始・終了)に朝へ直接置く。</summary>
    public void StartMorning()
    {
        Current = Stage.Day;
        Phase = MorningPhase;
        Darkness = 0f;
        FadeAlpha = 0f;
        dawnRequested = false;
    }

    /// <summary>撮影用に昼の時刻を進める・戻す。昼の間だけ効き、日の出と日没は越えない。</summary>
    public void Step(float hours)
    {
        if (Current != Stage.Day) return;
        Phase = Mathf.Clamp(Phase + hours / 12f, 0f, 0.999f);
    }
}
