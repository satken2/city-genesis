using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Static store for city settings fetched from GET /api/v1/city.
/// Shares the city government's agent ID and the traffic side across consumers of the same city.
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
    public static int? CityAgentId { get; private set; }

    /// <summary>Fetch the opened city's settings from /city/.</summary>
    public static IEnumerator Load()
    {
        TrafficHand = Constants.TRAFFIC_HAND_LEFT;
        CityAgentId = null;
        yield return ApiClient.Get<CityEnvelope>("/city/",
            env =>
            {
                if (env?.city == null) return;
                TrafficHand = env.city.traffic_hand;
                CityAgentId = env.city.city_agent_id;
            },
            err => Debug.LogWarning($"CityInfoStore: 都市情報の取得に失敗: {err}"));
    }

    // GET /api/v1/city response (required fields only).
    [Serializable]
    private class CityEnvelope { public CityInfo city; }
    [Serializable]
    private class CityInfo { public int traffic_hand; public int? city_agent_id; }
}
