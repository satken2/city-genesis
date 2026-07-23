using System;
using UnityEngine;

/// <summary>
/// GET /api/v1/city から取得する都市共通設定の静的ストア。
/// 現状は traffic_hand（通行方向。0=左側通行 / 1=右側通行）のみを保持する。
///
/// traffic_hand はゲーム開始後に絶対に変化しない固定設定であり、
/// 駅パスのプラットフォーム設置側（platform_side の内側/外側→左右の解決。
/// TrackPath.ResolvePlatformExtents）が参照する。車両描画（VehicleManager）は
/// 従来どおり自前で取得しており、本ストアとは独立している。
///
/// 取得は PathManager.Start が EnsureLoaded を呼ぶことで一度だけ行われる
/// （パスのロードはゲームセッション開始後にしか走らないため、タイミングとして十分早い）。
/// 取得前・取得失敗時は既定値（左側通行）のまま動作する。
/// </summary>
public static class CityInfoStore
{
    /// <summary>都市の通行方向（Constants.TRAFFIC_HAND_LEFT / TRAFFIC_HAND_RIGHT）。</summary>
    public static int TrafficHand { get; private set; } = Constants.TRAFFIC_HAND_LEFT;

    private static bool fetchStarted;

    /// <summary>
    /// 未取得なら /city/ から都市設定を取得する（多重呼び出しは無視）。
    /// コルーチンの実行ホストとして MonoBehaviour が必要。
    /// </summary>
    public static void EnsureLoaded(MonoBehaviour host)
    {
        if (fetchStarted || host == null) return;
        fetchStarted = true;
        host.StartCoroutine(ApiClient.Get<CityEnvelope>("/city/",
            env => { if (env?.city != null) TrafficHand = env.city.traffic_hand; },
            err =>
            {
                // 失敗時は次の EnsureLoaded 呼び出しで再試行できるようフラグを戻す
                fetchStarted = false;
                Debug.LogWarning($"CityInfoStore: traffic_hand の取得に失敗(既定=左側通行を使用): {err}");
            }));
    }

    // GET /api/v1/city のレスポンス(必要フィールドのみ)。
    [Serializable]
    private class CityEnvelope { public CityInfo city; }
    [Serializable]
    private class CityInfo { public int traffic_hand; }
}
