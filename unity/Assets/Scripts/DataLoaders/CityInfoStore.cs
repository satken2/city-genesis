using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Static store for city settings fetched from GET /api/v1/city.
/// Currently stores only traffic_hand (traffic side: 0=left-hand / 1=right-hand).
///
/// traffic_hand is fixed per city and never changes during play.
/// Station path platform placement uses it to resolve platform_side inner/outer to left/right
/// in TrackPath.ResolvePlatformExtents. Vehicle rendering (VehicleManager) also
/// reads the same value rather than fetching city settings independently.
///
/// IpcStateCoordinator calls Load whenever a different city (session_id) is opened, before drawing it.
/// Use the default (left-hand traffic) if fetching fails.
/// </summary>
public static class CityInfoStore
{
    /// <summary>City traffic side (Constants.TRAFFIC_HAND_LEFT / TRAFFIC_HAND_RIGHT).</summary>
    public static int TrafficHand { get; private set; } = Constants.TRAFFIC_HAND_LEFT;

    /// <summary>Fetch the opened city's settings from /city/.</summary>
    public static IEnumerator Load()
    {
        TrafficHand = Constants.TRAFFIC_HAND_LEFT;
        yield return ApiClient.Get<CityEnvelope>("/city/",
            env => { if (env?.city != null) TrafficHand = env.city.traffic_hand; },
            err => Debug.LogWarning($"CityInfoStore: traffic_hand の取得に失敗(既定=左側通行を使用): {err}"));
    }

    // GET /api/v1/city response (required fields only).
    [Serializable]
    private class CityEnvelope { public CityInfo city; }
    [Serializable]
    private class CityInfo { public int traffic_hand; }
}
