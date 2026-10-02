using System;
using UnityEngine;

/// <summary>
/// 交通演出(一般車・歩行者・公共交通・索道・空と海の乗り物)の時計と一時停止。
/// Time.timeScale はカメラ操作(慣性・ドラッグ速度の除算)や再試行の待ち時間まで止めるので使わず、
/// 各 Manager が経過時間に Scale を掛ける。天気・時刻・水面・火災などは止めない。
/// </summary>
public static class TrafficClock
{
    public static bool Paused { get; private set; }
    public static float Scale => Paused ? 0f : 1f;
    public static event Action<bool> PausedChanged;
    private static float pausedAt, pausedTotal;

    /// <summary>止めている間は進まない Time.time。飛行機雲・航跡の寿命など、時刻で数える演出が使う。</summary>
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
