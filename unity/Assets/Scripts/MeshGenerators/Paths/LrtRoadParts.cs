using System.Collections.Generic;
using UnityEngine;
using Dreamteck.Splines;
using GeneratedModels;

/// <summary>
/// LRTの軌道位置・踏面高さ・停留所ホーム寸法の唯一の正と、道路上の併用軌道パーツの生成。
/// 軌道・車両・交差点のレールコネクタ・停留所ホームはすべてここの値を共有する。
/// LRTの線路は1本のパスで上下線を持つ左右対称の複線(DIR_BOTH固定)。
/// </summary>
public static class LrtRoadParts
{
    public static bool IsEmbedded(int variant) => variant == 222 || variant == 242 || variant == 303;
    public static bool IsLrt(int variant) => IsEmbedded(variant) || variant == 431;

    public const float EMBEDDED_RAIL_TOP = .025f;       // lrt_track の踏面(路面原点から)
    public const float DEDICATED_RAIL_TOP = .355f;      // 431 の track(411と共有)の踏面(TRACK_ABOVE_SPLINE から)
    public const float MALL_TRACK_OFFSET = 1.75f;       // 303 の軌道中心。431(幅7m・2線)と同じ軌道中心間隔3.5m
    public const float MALL_POLE_OFFSET = 5.4f;         // 303 の両側架線柱の足元。腕は近い方の軌道へ出る
    public const float VEHICLE_HALF_WIDTH = 1.27f;      // LRT車体(幅2.54m)の半幅
    public const float MALL_VEHICLE_ENVELOPE = MALL_TRACK_OFFSET + VEHICLE_HALF_WIDTH;  // 303 で車体が通る範囲の半幅

    public const float PLATFORM_EDGE_GAP = 1.35f;       // 軌道中心からホーム内縁まで(車体半幅+0.08m)
    public const float PLATFORM_ABOVE_RAIL = .30f;      // ホーム上面のレール踏面からの高さ
    public const float PLATFORM_END_MARGIN = 1f;        // 編成の両端からデッキ端までの余裕
    public const float PLATFORM_MERGE_GAP = 2f;         // 同じパス上でこれ未満の隙間しかないホームは1本に併合する
    public const float PLATFORM_MIN_LENGTH = 4f;        // 切断済み区間へ収めた後のデッキがこれ未満なら描かない
    public const float SHELTER_MIN_DECK_LENGTH = 8f;    // 上屋を置くデッキ長の下限
    public const float PLATFORM_POLE_CLEARANCE = .5f;   // 431 の架線柱をホーム外縁から離す距離
    public const float PLATFORM_TRACK_ZONE_MARGIN = 2f;     // 431 で架線柱を移し高欄を省く範囲の、スロープ端からの余裕
    public const float PLATFORM_EQUIPMENT_ZONE_MARGIN = 1f; // 303 で沿道設備を置かない範囲の、スロープ端からの余裕

    /// <summary>
    /// 軌道中心の横距離(正の値。左右対称の2軌道の片側)。
    /// 222/242 は双方向の最内車線中心、303 は定数、431 は TrackPath.GetTrackCenterOffset と同じ導出。
    /// </summary>
    public static float Offset(PathSummary path)
    {
        if (path.path_variant == 303) return MALL_TRACK_OFFSET;
        if (!IsEmbedded(path.path_variant))
        {
            TrackPath.GetTrackMasterParams(path, out float trackWidth, out int trackLanes, out _, out _, out _, out _, out _, out _);
            return Mathf.Abs(TrackPath.GetTrackCenterOffset(trackWidth, trackLanes, 0, TrackPath.GetTrackCenterWidth(path)));
        }
        RoadPath.GetPathMasterParams(path, out float width, out float sidewalk, out float side, out _,
            out int lanes, out _, out float center, out _, out _, out _, out _);
        // 併用軌道は一方通行への変更でも位置を変えず、双方向時の内側車線を使う。
        var layout = new RoadLaneLayout(width, sidewalk, side, center, lanes, Constants.DIR_BOTH);
        return Mathf.Abs(layout.Center(layout.Count - 1, Constants.TRAFFIC_HAND_LEFT));
    }

    /// <summary>全軌道の中心の横位置(始点→終点の進行方向右が正)を昇順で返す。</summary>
    public static List<float> TrackLaterals(PathSummary path)
    {
        if (IsEmbedded(path.path_variant))
        {
            float offset = Offset(path);
            return new List<float> { -offset, offset };
        }
        TrackPath.GetTrackMasterParams(path, out float width, out int lanes, out _, out _, out _, out _, out _, out _);
        int count = Mathf.Max(1, lanes);
        float centerWidth = TrackPath.GetTrackCenterWidth(path);
        var laterals = new List<float>(count);
        for (int i = 0; i < count; i++) laterals.Add(TrackPath.GetTrackCenterOffset(width, count, i, centerWidth));
        return laterals;
    }

    /// <summary>レール踏面のスプラインからの高さ。埋め込み軌道は0.125m、431 は0.475m。</summary>
    public static float RailTopAboveSpline(int variant) => IsEmbedded(variant)
        ? RoadPartHeights.BODY_ABOVE_SPLINE + EMBEDDED_RAIL_TOP
        : RoadPartHeights.TRACK_ABOVE_SPLINE + DEDICATED_RAIL_TOP;

    /// <summary>ホームの幅。222/242 は外側車線の車両と干渉しない安全地帯の幅。</summary>
    public static float PlatformWidth(int variant) => variant == 222 || variant == 242 ? 1.2f : 2f;

    /// <summary>デッキ両端の外側に置くスロープの長さ。</summary>
    public static float PlatformRampLength(int variant) => variant == 431 ? 6f : 3f;

    /// <summary>ホーム内縁の横距離(軌道の外側。中心線から)。</summary>
    public static float PlatformInnerOffset(PathSummary path) => Offset(path) + PLATFORM_EDGE_GAP;

    /// <summary>ホーム外縁の横距離(中心線から)。</summary>
    public static float PlatformOuterOffset(PathSummary path) => PlatformInnerOffset(path) + PlatformWidth(path.path_variant);

    /// <summary>ホーム上面のスプラインからの高さ。</summary>
    public static float PlatformSurfaceAboveSpline(int variant) => RailTopAboveSpline(variant) + PLATFORM_ABOVE_RAIL;

    /// <summary>編成長(両数×車長)に両端の余裕を足したデッキ長。</summary>
    public static float PlatformDeckLength(float consistLength) => consistLength + 2f * PLATFORM_END_MARGIN;

    /// <summary>
    /// 架線柱を置く距離(切断済みスプラインの始点から)。両端から min(間隔/2, 5m) 空けて間隔ごとに置く。
    /// 柱の描画と、303 の設備が柱の足元を避ける予約で同じ列を使う。
    /// </summary>
    public static IEnumerable<float> PoleDistances(float length, float spacing)
    {
        if (spacing <= 0f) yield break;
        float margin = Mathf.Min(spacing * .5f, 5f);
        for (float distance = margin; distance <= length - margin; distance += spacing) yield return distance;
    }

    /// <summary>
    /// 道路上の併用軌道(222/242/303)の軌道・架線・架線柱と、停留所のホーム(platforms。LrtPlatformParts.Resolve の結果)。
    /// 222/242 の中央柱と 303 の両側柱はホームと干渉しない位置なので、ホーム区間でも動かさない。
    /// </summary>
    public static void Generate(GameObject parent, SplineComputer spline, PathSummary path, List<LrtPlatformParts.Span> platforms = null)
    {
        if (!IsEmbedded(path.path_variant)) return;
        string prefix = $"Models/Paths/p{path.path_variant}";
        float spacing = MasterDataStore.Instance?.GetPathMaster(path.path_variant)?.wire_spacing ?? 0;
        foreach (float x in TrackLaterals(path))
        {
            RoadPath.CreateSplineMesh(parent, spline, prefix + "/lrt_track", 3, x,
                RoadPartHeights.BODY_ABOVE_SPLINE, stretchToWidth: false);
            if (spacing > 0)
                RoadPath.CreateSplineMesh(parent, spline, prefix + "/lrt_wire", 1, x,
                    RoadPartHeights.BODY_ABOVE_SPLINE, stretchToWidth: false);
        }
        LrtPlatformParts.Generate(parent, spline, path, platforms);
        if (spacing <= 0) return;
        var pole = ModelManager.Instance?.GetModel(prefix + "/lrt_pole", true);
        if (pole == null) return;
        foreach (float distance in PoleDistances(spline.CalculateLength(), spacing))
        {
            double percent = spline.Travel(0, distance, Spline.Direction.Forward);
            if (PathDrawContext.HasDetailRange && (percent < PathDrawContext.DetailFrom || percent > PathDrawContext.DetailTo)) continue;
            var sample = spline.Evaluate(percent);
            if (PathTunnelParts.IsTunnel(path, sample.position)) continue;
            Vector3 forward = Vector3.ProjectOnPlane(sample.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f) continue;
            var placed = Object.Instantiate(pole, parent.transform);
            placed.name = "LrtPole";
            placed.transform.position = sample.position + Vector3.up * RoadPartHeights.BODY_ABOVE_SPLINE;
            placed.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            foreach (var renderer in placed.GetComponentsInChildren<MeshRenderer>())
            {
                var materials = renderer.sharedMaterials;
                for (int m = 0; m < materials.Length; m++) materials[m] = RoadPath.GetOrCreateRoadMaterial(materials[m]);
                renderer.sharedMaterials = materials;
            }
        }
    }
}
