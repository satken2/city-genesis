using System;
using UnityEngine;

/// <summary>
/// 一般車・歩行者・公共交通・索道・航空機・船舶の演出用時計と停止状態。描画用の時刻もこの停止状態に従う。
/// カメラの慣性やドラッグ速度の計算、再試行待ちを止めないため、Time.timeScale は使わず各 Manager が経過時間に Scale を掛ける。
/// 天候の変化、水面、火災は停止しない。
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
