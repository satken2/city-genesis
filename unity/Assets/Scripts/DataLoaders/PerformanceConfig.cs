using UnityEngine;

/// <summary>
/// Centralized performance settings.
/// Provide runtime-adjustable parameters such as frame budgets and LOD switching thresholds.
/// </summary>
public static class PerformanceConfig
{
    /// <summary>Budget for parking completed render objects. Use only part of PC memory, capped even on large machines.</summary>
    private static int cacheMemoryMiB = -1;
    private static int CacheMemoryMiB => cacheMemoryMiB >= 0 ? cacheMemoryMiB : cacheMemoryMiB = SystemInfo.systemMemorySize;
    public static long BuildingCacheBytes => CacheBudget(CacheMemoryMiB, 16, 256, 2048);
    public static long RenderCacheBytes => CacheBudget(CacheMemoryMiB, 12, 512, 3072);

    private static long CacheBudget(int memoryMiB, int divisor, int minimumMiB, int maximumMiB)
        => (long)System.Math.Max(minimumMiB, System.Math.Min(maximumMiB, memoryMiB / divisor)) * 1024 * 1024;

    /// <summary>Cooperative yield interval preventing one generator from monopolizing the shared budget.</summary>
    public static float SliceBudgetMs => Mathf.Max(0.05f, Mathf.Min(0.5f, FrameBudgetMs - (float)RenderWorkScheduler.UsedMs));
    /// <summary>
    /// Maximum per-frame processing time (milliseconds) for operations such as coroutine object generation.
    /// A fixed share of the actual frame time, never less than that share of the target frame time.
    /// When frames run slower than the target (heavy rendering during loading), generation keeps the same share of each frame
    /// instead of a budget sized for the target rate, which would leave loading throughput falling with the frame rate.
    /// </summary>
    public static float FrameBudgetMs
    {
        get
        {
            float targetFps = Application.targetFrameRate > 0
                ? Application.targetFrameRate
                : 60f;
            // Fraction of frame time allocated to coroutine work.
            // Remaining time is used for rendering, physics, input, etc.
            const float budgetRatio = 0.25f;
            float frameTime = Mathf.Max(1000f / targetFps, Time.smoothDeltaTime * 1000f);
            return Mathf.Max(2f, frameTime * budgetRatio);
        }
    }
}
