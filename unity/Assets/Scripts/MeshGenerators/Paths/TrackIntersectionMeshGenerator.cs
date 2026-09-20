using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// 線路（PATH_TRACK）・モノレール（PATH_MONORAIL）ノードのポイント（分岐器）・平面交差の描画を担う。
///
/// 交差点の共通外周（道床）・切断点の計算とパス端の切り詰めは道路と共通で
/// RoadIntersectionMeshGenerator が行う（ハルのマテリアルも body プレハブ由来のため、
/// 線路ノードでは自動的に道床の見た目になる）。本クラスは「どの線路ペアの間にレールを敷くか」の
/// 判定と、コネクタ形状（RailConnectorSpec）の計算だけを追加で行う。
///
/// レールを敷くペアの判定は、バックエンドの転回制約（transit_route のエッジベースDijkstra +
/// geo_paths.path_end_tangent + parameters.yaml の RAIL_MAX_TURN_ANGLE_DEG）と同じ幾何ルールを
/// 鏡写しにする:
///   ノードでパスAからパスBへ進むときの偏向角（Aの到着方向とBの発進方向のなす角。
///   直進=0°、折り返し=180°）が閾値以下のペアにのみレールを敷く。
/// これにより「見た目に敷かれているレール」と「経路探索で通れる方向」が一致し、
/// Y字合流の折り返し側やダイヤモンドクロッシングの90°転回側にはレールが描かれない。
///
/// 転回判定は制御点優先の論理端点接線、描画は切断断面の接線と勾配を使う。
///
/// 道路と線路が混在するノード（踏切）でも、線路ペアが通行可能な向きならレールが
/// アスファルトハルの上に敷かれ、踏切らしい見た目になる。
/// </summary>
public static class TrackIntersectionMeshGenerator
{
    /// <summary>コネクタのベジエサンプリング間隔（m）。</summary>
    private const float SAMPLE_INTERVAL = 1.0f;

    /// <summary>これより短いコネクタは形状が潰れるため生成しない（m）。</summary>
    private const float MIN_CONNECTOR_LENGTH = 0.05f;

    /// <summary>
    /// ノードでの転回に制約がある（剛体的な軌道上を走る）パス種別か。
    /// バックエンドの PATH_TYPES_TURN_RESTRICTED（consts.py）と同じ集合を保つこと。
    /// </summary>
    public static bool IsTrackType(int pathType)
    {
        return pathType == Constants.PATH_TRACK || pathType == Constants.PATH_MONORAIL;
    }

    /// <summary>
    /// ノードに接続する1本の線路について、コネクタ計算に必要な情報。
    /// Direction はノードから線路の内部へ向かうXZ単位ベクトル（=端点接線の外向き）。
    /// </summary>
    private class TrackEntry
    {
        public int PathId;
        public Vector2 Direction;
        public Vector2 LogicalDirection;
        public float Slope;
        public Vector2 ConnectionPoint;
        public bool NodeIsStart;
        public int Lanes;
        public float Width;
        public int PathVariant;
        public int PathType;
        public PathSummary Path;
    }

    /// <summary>
    /// 通行可能な線路ペアの切断点どうしを結ぶレールコネクタの幾何情報を計算する。
    /// 線路が2本未満のノードや、通行可能なペアが無いノードでは null を返す。
    /// </summary>
    /// <param name="connectionPoints">
    /// RoadIntersectionMeshGenerator が計算した、pathId → 交差点ハル縁の切断点（XZ平面）。
    /// </param>
    public static List<RailConnectorSpec> BuildRailConnectorSpecs(int nodeId, Vector3 nodePosition, bool horizontalPlane, List<PathSummary> paths, Dictionary<int, Vector2> connectionPoints, Dictionary<int, PathEndSection> sections = null)
    {
        if (paths == null || connectionPoints == null)
        {
            return null;
        }

        List<TrackEntry> entries = new List<TrackEntry>();
        foreach (var path in paths)
        {
            if (!IsTrackType(path.path_type) && !LrtRoadParts.IsEmbedded(path.path_variant)) continue;
            if (!connectionPoints.TryGetValue(path.path_id, out Vector2 cut)) continue;
            if (!RoadIntersectionMeshGenerator.TryGetPathDirectionFromNode(nodeId, path, out Vector2 nodePoint, out Vector2 directionPoint)) continue;

            Vector2 direction = directionPoint - nodePoint;
            if (direction.sqrMagnitude < 1e-6f) continue;
            direction.Normalize();
            if (sections != null && sections.TryGetValue(path.path_id, out var section)) direction = section.Direction;

            TrackPath.GetTrackMasterParams(path, out float width, out int lanes, out _, out _, out _, out _, out _, out _);

            entries.Add(new TrackEntry
            {
                PathId = path.path_id,
                Direction = direction,
                LogicalDirection = (directionPoint - nodePoint).normalized,
                Slope = sections != null && sections.TryGetValue(path.path_id, out var slopeSection) ? slopeSection.Tangent.y / Mathf.Max(1e-5f, IntersectionPolygonUtil.XZ(slopeSection.Tangent).magnitude) : 0f,
                ConnectionPoint = cut,
                NodeIsStart = path.start_node_id == nodeId,
                Lanes = Mathf.Max(1, lanes),
                Width = width,
                PathVariant = path.path_variant,
                PathType = path.path_type,
                Path = path,
            });
        }

        if (entries.Count < 2)
        {
            return null;
        }

        // 偏向角の閾値。cos で比較する（偏向角 ≤ 閾値 ⇔ cos(偏向角) ≥ cos(閾値)）。
        float cosLimit = Mathf.Cos(LocalConstants.RAIL_MAX_TURN_ANGLE_DEG * Mathf.Deg2Rad);

        List<RailConnectorSpec> specs = new List<RailConnectorSpec>();
        for (int i = 0; i < entries.Count; i++)
        {
            for (int j = i + 1; j < entries.Count; j++)
            {
                TrackEntry a = entries[i];
                TrackEntry b = entries[j];
                bool lrtPair = LrtRoadParts.IsLrt(a.PathVariant) && LrtRoadParts.IsLrt(b.PathVariant);
                if (!lrtPair && (a.PathType != b.PathType || LrtRoadParts.IsLrt(a.PathVariant) || LrtRoadParts.IsLrt(b.PathVariant))) continue;

                // A→Bの偏向角: 到着方向(-a.Direction)と発進方向(b.Direction)のなす角。
                // B→Aも同じ角度になる（対称）ため、順序なしペアごとに1回だけ判定すればよい。
                float cosDeflection = Vector2.Dot(-a.LogicalDirection, b.LogicalDirection);
                // 転回角の制限は案内軌条系どうしだけ。道路上のLRTはバックエンドと同じく右左折できる。
                if ((!lrtPair || (IsTrackType(a.PathType) && IsTrackType(b.PathType))) && cosDeflection < cosLimit) continue;

                AddConnectorsForPair(specs, a, b, nodePosition, horizontalPlane);
            }
        }

        return specs.Count > 0 ? specs : null;
    }

    /// <summary>
    /// 通行可能と判定された線路ペア(A, B)の間に、軌道ごとのレールコネクタを追加する。
    /// 軌道の対応付けは、走行フレーム（A→Bへ進む向きの右手方向）での横位置の順序で行う
    /// （複線の直進では右側軌道どうし・左側軌道どうしが繋がる）。
    /// 軌道数が異なるペアは横位置順を保つ多対一対応で、実際の全軌道端へ接続する。
    /// </summary>
    private static void AddConnectorsForPair(List<RailConnectorSpec> specs, TrackEntry a, TrackEntry b, Vector3 nodePosition, bool horizontalPlane)
    {
        // 走行方向: Aから来てノードを通過し、Bへ抜ける向き。
        Vector2 travelA = -a.Direction;
        Vector2 travelB = b.Direction;

        // 各線路の軌道横位置を「走行フレームの右方向を正」とする符号付き距離で求める。
        // 軌道オフセットはパス自身のスプライン進行方向（始点→終点）基準なので、
        // ノードが始点側か終点側かで走行フレームに対する符号が反転する。
        //   ノード=終点: パスの進行方向 ≒ 走行方向（そのまま）
        //   ノード=始点: パスの進行方向 ≒ 走行方向の逆（符号反転）
        float signA = a.NodeIsStart ? -1f : 1f;
        float signB = b.NodeIsStart ? 1f : -1f;

        List<float> lateralsA = BuildSortedLaterals(a, signA);
        List<float> lateralsB = BuildSortedLaterals(b, signB);

        // 横位置の順序を保つ単調対応で全軌道端を接続する。軌道数の少ない側で合流する。
        int connectorCount = Mathf.Max(lateralsA.Count, lateralsB.Count);
        var mappedA = new List<float>();
        var mappedB = new List<float>();
        for (int k = 0; k < connectorCount; k++)
        {
            float t = connectorCount == 1 ? 0f : (float)k / (connectorCount - 1);
            mappedA.Add(lateralsA[Mathf.RoundToInt(t * (lateralsA.Count - 1))]);
            mappedB.Add(lateralsB[Mathf.RoundToInt(t * (lateralsB.Count - 1))]);
        }
        lateralsA = mappedA;
        lateralsB = mappedB;
        Vector2 rightA = RightOf(travelA);
        Vector2 rightB = RightOf(travelB);

        // 見た目の基準となるレールプレハブは、歩道コネクタと同じく variant 番号が大きい方に合わせる。
        TrackEntry prefabSource = a.PathVariant >= b.PathVariant ? a : b;
        bool embedded = LrtRoadParts.IsEmbedded(a.PathVariant) || LrtRoadParts.IsEmbedded(b.PathVariant);
        if (embedded) prefabSource = LrtRoadParts.IsEmbedded(a.PathVariant) ? a : b;

        for (int k = 0; k < connectorCount; k++)
        {
            Vector2 start = a.ConnectionPoint + rightA * lateralsA[k];
            Vector2 end = b.ConnectionPoint + rightB * lateralsB[k];

            float dist = Vector2.Distance(start, end);
            if (dist < MIN_CONNECTOR_LENGTH) continue;

            // 端点接線をハンドルにした三次ベジエで、切断点どうしを滑らかに結ぶ。
            Vector2 p1 = start + travelA * (dist / 3f);
            Vector2 p2 = end - travelB * (dist / 3f);

            int sampleCount = Mathf.Clamp(Mathf.CeilToInt(dist / SAMPLE_INTERVAL) + 1, 4, 24);
            List<Vector3> points = new List<Vector3>(sampleCount);
            for (int s = 0; s < sampleCount; s++)
            {
                float t = (float)s / (sampleCount - 1);
                Vector2 pt = CubicBezier(start, p1, p2, end, t);
                points.Add(ToLocalTrackPoint(pt, nodePosition, horizontalPlane));
            }

            specs.Add(new RailConnectorSpec(points, prefabSource.PathVariant, prefabSource.PathType) {
                StartDirection = travelA, EndDirection = travelB, StartSlope = -a.Slope, EndSlope = b.Slope,
                EmbeddedLrt = embedded,
                StartRailBase = embedded && !LrtRoadParts.IsEmbedded(a.PathVariant) ? RoadPartHeights.TRACK_ABOVE_SPLINE + .355f - LrtRoadParts.RailTop : RoadPartHeights.BODY_ABOVE_SPLINE,
                EndRailBase = embedded && !LrtRoadParts.IsEmbedded(b.PathVariant) ? RoadPartHeights.TRACK_ABOVE_SPLINE + .355f - LrtRoadParts.RailTop : RoadPartHeights.BODY_ABOVE_SPLINE,
                StartWireOffset = LrtRoadParts.IsEmbedded(a.PathVariant) ? 0 : -.28f,
                EndWireOffset = LrtRoadParts.IsEmbedded(b.PathVariant) ? 0 : -.28f
            });
        }
    }

    /// <summary>
    /// 線路の全軌道の横位置（走行フレームの符号付き距離）を昇順で返す。
    /// 軌道オフセットの導出は TrackPath.GetTrackCenterOffset と同一（本線の軌道位置と一致させる）。
    /// </summary>
    private static List<float> BuildSortedLaterals(TrackEntry entry, float sign)
    {
        if (LrtRoadParts.IsEmbedded(entry.PathVariant))
        {
            float offset = LrtRoadParts.Offset(entry.Path);
            return entry.PathVariant == 303 ? new List<float> { 0 } : new List<float> { -offset, offset };
        }
        List<float> laterals = new List<float>(entry.Lanes);
        for (int i = 0; i < entry.Lanes; i++)
        {
            laterals.Add(TrackPath.GetTrackCenterOffset(entry.Width, entry.Lanes, i) * sign);
        }
        laterals.Sort();
        return laterals;
    }

    private static Vector2 CubicBezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1f - t;
        return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
    }

    /// <summary>
    /// XZ平面の2Dベクトルに対する、進行方向右手のベクトルを返す。
    /// （3Dの Cross(up, forward) = right と同じ向き。Vector2 は (x, z) を保持している）
    /// </summary>
    private static Vector2 RightOf(Vector2 direction)
    {
        return new Vector2(direction.y, -direction.x);
    }

    /// <summary>
    /// XZ点をノード基準のローカル座標に変換する。
    /// 高さは基準面 + TRACK_ABOVE_SPLINE で、本線の軌道パーツの下端と面一に揃える。
    /// </summary>
    private static Vector3 ToLocalTrackPoint(Vector2 point, Vector3 nodePosition, bool horizontalPlane)
    {
        float worldY = RoadIntersectionMeshGenerator.SampleBaseSurfaceY(point.x, point.y, nodePosition, horizontalPlane) + RoadPartHeights.TRACK_ABOVE_SPLINE;
        return new Vector3(point.x - nodePosition.x, worldY - nodePosition.y, point.y - nodePosition.z);
    }
}
