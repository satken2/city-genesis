using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 計画中パスが干渉する区画・建物・既存パスのハイライト表示を司る。
/// world:highlight_objects / world:clear_highlights を受けた IpcEventHandler から駆動される。
///
/// - 区画: ParcelManager の2Dオーバーレイに分類色（blocking=赤/removable=橙/shavable=黄、
///   exempt=なし）で塗りつぶしを設定する。
/// - 建物・既存パス: 半透明の占有箱(OccupancyVolumeUtil)で示す。
/// - 川: 地表レベルで横切っている川の範囲を、同じ警告色の占有箱(RiverStore.AddOccupancyBox)で示す。
///
/// ハイライトは発信元(source。敷設="path_build" / 詳細パネル="path_detail")ごとに独立
/// レイヤーで保持し、毎回全ソースを合成して描画する。これにより、あるウィンドウの
/// クリアが別ウィンドウのハイライトを消さない。
///
/// バックエンドの compute_path_interference が権威。敷設中のローカル推定表示
/// (RoadPlanManager 側)とは別レイヤーで、こちらが確定結果を上書きする。
/// </summary>
public class HighlightManager
{
    // 区画の分類色（塗りつぶし。半透明）
    private static readonly Color BlockingParcelColor = new Color(1.0f, 0.20f, 0.20f, 0.55f); // 赤: 着工不可(買収要)
    private static readonly Color RemovableParcelColor = new Color(1.0f, 0.55f, 0.10f, 0.50f); // 橙: 削除で着工可
    private static readonly Color ShavableParcelColor = new Color(1.0f, 0.85f, 0.20f, 0.50f); // 黄: 部分収用で存続
    // exempt はハイライトしない（無変更で着工できるため）

    private OverlayMeshRenderer occupancyOverlay;

    // 発信元ごとの干渉ペイロード。合成描画の入力。
    private readonly Dictionary<string, PathInterferencePayload> bySource =
        new Dictionary<string, PathInterferencePayload>();

    private ParcelManager parcelManager;
    private PathManager pathManager;

    /// <summary>指定ソースの干渉ハイライトを設定し、全ソースを合成して描画し直す。</summary>
    public void Apply(string source, PathInterferencePayload interference)
    {
        if (string.IsNullOrEmpty(source)) source = "default";
        if (interference == null || interference.IsEmpty) bySource.Remove(source);
        else bySource[source] = interference;
        if (source == "path_build")
            Object.FindAnyObjectByType<RoadPlanManager>()?.SetAuthoritativeCollision(interference != null && interference.CollidingPathIds.Count > 0);
        Recompose();
    }

    /// <summary>指定ソースの干渉ハイライトを解除し、残りのソースで描画し直す。</summary>
    public void Clear(string source)
    {
        if (string.IsNullOrEmpty(source)) source = "default";
        if (source == "path_build") Object.FindAnyObjectByType<RoadPlanManager>()?.SetAuthoritativeCollision(false);
        if (bySource.Remove(source)) Recompose();
    }

    /// <summary>全ソースの干渉を合成し、区画・建物・パスへハイライトを塗り直す。</summary>
    private void Recompose()
    {
        // 全ソースの区画分類色を合成（blocking > removable > shavable の優先で上書き）
        var parcelColors = new Dictionary<int, Color>();
        var buildingIds = new HashSet<int>();
        var pathIds = new HashSet<int>();
        var riverIds = new HashSet<string>();
        foreach (var interference in bySource.Values)
        {
            foreach (int id in interference.ShavableParcels) parcelColors[id] = ShavableParcelColor;
            foreach (int id in interference.RemovableParcels) parcelColors[id] = RemovableParcelColor;
            foreach (int id in interference.BlockingParcels) parcelColors[id] = BlockingParcelColor;
            foreach (int id in interference.BuildingIds) buildingIds.Add(id);
            foreach (int id in interference.CollidingPathIds) pathIds.Add(id);
            foreach (string id in interference.CollidingRiverIds) riverIds.Add(id);
        }

        GetParcelManager()?.SetInterferenceParcels(parcelColors);

        var triangles = new List<Vector3>();
        if (BuildingManager.Instance != null)
            foreach (int id in buildingIds)
                OccupancyVolumeUtil.AddBuilding(triangles, BuildingManager.Instance.GetBuildingSummary(id));
        var pm = GetPathManager();
        if (pm != null)
            foreach (int id in pathIds)
                if (id >= 0) OccupancyVolumeUtil.AddPath(triangles, pm, id);
        // 川は権威判定側がどの区間で横切ったかを返さないため、川の範囲全体を占有箱で示す。
        // 敷設中はローカル判定(RoadPlanManager)が横断付近だけの箱を重ねて描く。
        foreach (string id in riverIds)
            RiverStore.AddOccupancyBox(triangles, RiverStore.FindById(id), null, 0f);
        if (occupancyOverlay == null && triangles.Count > 0)
        {
            var go = new GameObject("InterferenceOccupancy");
            occupancyOverlay = go.AddComponent<OverlayMeshRenderer>();
            var material = new Material(Shader.Find("Custom/OverlayLine"));
            material.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.LessEqual);
            occupancyOverlay.Initialize(material);
            Object.Destroy(material);
        }
        if (occupancyOverlay != null)
        {
            occupancyOverlay.BeginFrame();
            occupancyOverlay.AddPreTriangulated(triangles, OccupancyVolumeUtil.WarningColor);
            occupancyOverlay.EndFrame(Camera.main);
            occupancyOverlay.gameObject.SetActive(triangles.Count > 0);
        }
    }

    private ParcelManager GetParcelManager()
    {
        if (parcelManager == null) parcelManager = Object.FindAnyObjectByType<ParcelManager>();
        return parcelManager;
    }

    private PathManager GetPathManager()
    {
        if (pathManager == null) pathManager = Object.FindAnyObjectByType<PathManager>();
        return pathManager;
    }
}
