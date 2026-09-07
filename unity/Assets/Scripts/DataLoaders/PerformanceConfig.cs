using UnityEngine;

/// <summary>
/// パフォーマンス関連の設定値を一元管理するクラス。
/// フレーム予算やLOD切り替えの閾値など、実行時に変更可能なパラメータを提供する。
/// </summary>
public static class PerformanceConfig
{
    /// <summary>一つの生成器が共通予算を独占しないための協調中断間隔。</summary>
    public static float SliceBudgetMs => Mathf.Max(0.05f, Mathf.Min(0.5f, FrameBudgetMs - (float)RenderWorkScheduler.UsedMs));
    /// <summary>
    /// コルーチン内でのオブジェクト生成など、1フレームあたりの最大処理時間（ミリ秒）。
    /// 目標FPSに基づいて動的に計算される。
    /// 例: 目標60FPS → 16.67ms/フレームのうち他処理の余裕を差し引いた値をフレーム予算とする。
    /// </summary>
    public static float FrameBudgetMs
    {
        get
        {
            float targetFps = Application.targetFrameRate > 0
                ? Application.targetFrameRate
                : 60f;
            // フレーム時間のうちコルーチン処理に割り当てる割合。
            // 残りの時間はレンダリング・物理演算・入力処理などに使われる。
            const float budgetRatio = 0.25f;
            float frameTime = 1000f / targetFps;
            return Mathf.Max(2f, frameTime * budgetRatio);
        }
    }
}
