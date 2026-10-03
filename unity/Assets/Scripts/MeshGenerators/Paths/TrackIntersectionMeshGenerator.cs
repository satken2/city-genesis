using UnityEngine;
using System;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>交差点の板なしで2本の線路をノードまでつなぐ直進接続(TrackIntersectionMeshGenerator.TryContinuousJoin)。</summary>
public sealed class TrackContinuousJoin
{
    public int PathA;
    public int PathB;
}

/// <summary>
/// 線路（PATH_TRACK）・モノレール（PATH_MONORAIL）ノードのポイント（分岐器）・平面交差の描画を担う。
///
/// 交差点の共通外周（道床）・切断点の計算とパス端の切り詰めは道路と共通で
/// RoadIntersectionMeshGenerator が行う（ハルのマテリアルも body プレハブ由来のため、
/// 線路ノードでは自動的に道床の見た目になる）。本クラスは「どの軌道端の間にレールを敷くか」の
/// 判定と、コネクタ形状（RailConnectorSpec）の計算だけを追加で行う。
///
/// レールはノードでの「パスAからパスBへ進む動き」ごとに、次をすべて満たすときに1本敷く。
///   - RailsConnect でレールがつながる組である
///   - 偏向角（Aの到着方向とBの発進方向のなす角。直進=0°、折り返し=180°）が閾値以下。
///     バックエンドの転回制約（transit_route のエッジベースDijkstra + geo_paths.path_end_tangent +
///     parameters.yaml の RAIL_MAX_TURN_ANGLE_DEG）の鏡写しで、道路上のLRTは右左折できる
///   - A がノードへ到着できる向き、B がノードから出発できる向きである（travel_direction）。
///     駅のホームは停車する列車が向きによらず走るので、物理的なレールとしては両方向とみなす
/// つなぐのは「A でノードへ到着する向きの軌道の端」と「B でノードから出発する向きの軌道の端」で、
/// パスと進む向きから軌道は1つに決まる（TrackPath.TrackIndexForTravel）。同じ軌道端の組は1本にまとめる。
/// 軌道数3以上の線路を含む組だけは向きを見ず、走行フレームの横位置順に全軌道端を対応付ける。
/// これにより「見た目に敷かれているレール」と「列車が走れる動き」が一致し、Y字合流の折り返し側や
/// ダイヤモンドクロッシングの90°転回側、一方通行で入れない向きにはレールが描かれない。
/// 線路端の車止め（TrackPath）は同じ規則で使われない軌道の端に置く（ResolveConnectedTrackEnds）。
///
/// 転回判定は制御点優先の論理端点接線、描画は切断断面の接線と勾配を使う。
/// 軌道の位置が違う線路の直進接続(TrackEndTransition のS字)では、切断位置での軌道の横位置と向きをS字に合わせる。
/// 軌道の位置と幅がそろう2本の直進接続は、交差点の板を作らず両方をノードまで描く(TryContinuousJoin)。
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
    /// ノードで2本のパスのレールがつながり得る組か(転回角・通行方向は問わない)。LRTの走行網(222/242/303/431)どうしは
    /// パス種別が違ってもつながり、LRTと通常鉄道、線路とモノレールはつながらない。
    /// </summary>
    public static bool RailsConnect(int typeA, int variantA, int typeB, int variantB)
    {
        bool lrtA = LrtRoadParts.IsLrt(variantA), lrtB = LrtRoadParts.IsLrt(variantB);
        if (lrtA || lrtB) return lrtA && lrtB;
        return typeA == typeB;
    }

    /// <summary>ノードに接続する1本の軌道を持つパス。</summary>
    private class TrackEntry
    {
        public PathSummary Path;
        public bool NodeIsStart;
        /// <summary>ノードから線路の内部へ向かう論理端点接線（転回判定用）。</summary>
        public Vector2 LogicalDirection;
        /// <summary>軌道中心の横位置（幾何フレーム=始点→終点の右が正の昇順）。添字が軌道番号。</summary>
        public List<float> Laterals;
        public bool Platform;
        /// <summary>ノードから線路の内部へ向かう切断断面の接線（コネクタの形状用）。</summary>
        public Vector2 Direction;
        public float Slope;
        public Vector2 ConnectionPoint;
        /// <summary>切断点での各軌道の横位置の、ノードから離れる向きの変化率(S字の区間だけ0以外)。</summary>
        public float[] LateralSlopes;
    }

    /// <summary>レールでつなぐ2つの軌道端。A・B は entries の添字で、A &lt; B に揃える。</summary>
    private struct Link
    {
        public int A, TrackA, B, TrackB;
    }

    /// <summary>
    /// 通行可能な軌道端どうしを結ぶレールコネクタの幾何情報を計算する。
    /// 線路が2本未満のノードや、つながる軌道端が無いノードでは null を返す。
    /// コネクタは entries の並び（paths の順）で先の線路から後の線路へ向かう。
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

        List<TrackEntry> entries = CollectEntries(nodeId, paths);
        entries.RemoveAll(entry => !connectionPoints.ContainsKey(entry.Path.path_id));
        if (entries.Count < 2)
        {
            return null;
        }
        var shifts = TrackEndTransition.ResolveNode(nodeId, paths);
        Vector2 nodePoint = new Vector2(nodePosition.x, nodePosition.z);
        foreach (TrackEntry entry in entries)
        {
            entry.ConnectionPoint = connectionPoints[entry.Path.path_id];
            entry.Direction = entry.LogicalDirection;
            float distance = Vector2.Distance(entry.ConnectionPoint, nodePoint);
            if (sections != null && sections.TryGetValue(entry.Path.path_id, out var section))
            {
                entry.Direction = section.Direction;
                entry.Slope = section.Tangent.y / Mathf.Max(1e-5f, IntersectionPolygonUtil.XZ(section.Tangent).magnitude);
                distance = section.Distance;
            }
            // S字の区間では、切断位置での軌道の横位置と向きを本線(TrackPath)と同じ式で求める。
            entry.LateralSlopes = new float[entry.Laterals.Count];
            if (!shifts.TryGetValue(entry.Path.path_id, out var shift) || shift.Delta.Length != entry.Laterals.Count) continue;
            for (int i = 0; i < entry.Laterals.Count; i++)
            {
                entry.Laterals[i] += shift.Shift(i, distance);
                entry.LateralSlopes[i] = shift.Delta[i] * shift.WeightSlope(distance);
            }
        }

        List<RailConnectorSpec> specs = new List<RailConnectorSpec>();
        foreach (Link link in ResolveLinks(entries))
        {
            AddConnector(specs, entries[link.A], link.TrackA, entries[link.B], link.TrackB, nodePosition, horizontalPlane);
        }
        return specs.Count > 0 ? specs : null;
    }

    /// <summary>
    /// ノードでレールがつながる軌道端（パスIDと軌道番号）の集合を、交差点のコネクタと同じ規則で返す。
    /// 線路端の車止めは、ここに含まれない軌道の端に置く。切断点を使わないので交差点メッシュの生成前でも求まる。
    /// </summary>
    public static HashSet<(int pathId, int track)> ResolveConnectedTrackEnds(int nodeId, List<PathSummary> paths)
    {
        var ends = new HashSet<(int pathId, int track)>();
        if (paths == null) return ends;
        List<TrackEntry> entries = CollectEntries(nodeId, paths);
        foreach (Link link in ResolveLinks(entries))
        {
            ends.Add((entries[link.A].Path.path_id, link.TrackA));
            ends.Add((entries[link.B].Path.path_id, link.TrackB));
        }
        return ends;
    }

    /// <summary>ノードに接続する軌道を持つパス（線路・モノレール・道路上のLRT）を集める。</summary>
    private static List<TrackEntry> CollectEntries(int nodeId, List<PathSummary> paths)
    {
        var entries = new List<TrackEntry>();
        foreach (PathSummary path in paths)
        {
            if (!IsTrackType(path.path_type) && !LrtRoadParts.IsEmbedded(path.path_variant)) continue;
            if (!RoadIntersectionMeshGenerator.TryGetPathDirectionFromNode(nodeId, path, out Vector2 nodePoint, out Vector2 directionPoint)) continue;
            entries.Add(new TrackEntry
            {
                Path = path,
                NodeIsStart = path.start_node_id == nodeId,
                LogicalDirection = (directionPoint - nodePoint).normalized,
                Laterals = LrtRoadParts.TrackLaterals(path),
                Platform = (MasterDataStore.Instance?.GetPathMaster(path.path_variant)?.platform_width ?? 0f) > 0f,
            });
        }
        return entries;
    }

    /// <summary>
    /// ノードでの「entries[a] から entries[b] へ進む動き」をすべて調べ、レールでつなぐ軌道端の組を返す
    /// （判定規則はクラスコメント参照）。
    /// </summary>
    private static List<Link> ResolveLinks(List<TrackEntry> entries)
    {
        var links = new List<Link>();
        // 偏向角の閾値。cos で比較する（偏向角 ≤ 閾値 ⇔ cos(偏向角) ≥ cos(閾値)）。
        float cosLimit = Mathf.Cos(LocalConstants.RAIL_MAX_TURN_ANGLE_DEG * Mathf.Deg2Rad);
        for (int a = 0; a < entries.Count; a++)
        {
            for (int b = 0; b < entries.Count; b++)
            {
                if (a == b) continue;
                TrackEntry from = entries[a];
                TrackEntry to = entries[b];
                if (!RailsConnect(from.Path.path_type, from.Path.path_variant, to.Path.path_type, to.Path.path_variant)) continue;

                // 転回角の制限は案内軌条系どうしだけ。道路上のLRTはバックエンドと同じく右左折できる。
                // 接続できる組は両方LRTか両方LRT以外なので、片方を見れば組の区分がわかる。
                bool turnRestricted = !LrtRoadParts.IsLrt(from.Path.path_variant)
                    || (IsTrackType(from.Path.path_type) && IsTrackType(to.Path.path_type));
                if (turnRestricted && Vector2.Dot(-from.LogicalDirection, to.LogicalDirection) < cosLimit) continue;

                if (from.Laterals.Count > 2 || to.Laterals.Count > 2)
                {
                    if (a < b) AddLateralOrderLinks(links, from, a, to, b);
                    continue;
                }

                // ノードが終点側なら幾何の順方向に到着し、始点側なら順方向に出発する。
                bool arriveForward = !from.NodeIsStart;
                bool departForward = to.NodeIsStart;
                if (!CanRun(from, arriveForward) || !CanRun(to, departForward)) continue;
                AddLink(links, a, TrackPath.TrackIndexForTravel(from.Laterals.Count, arriveForward),
                    b, TrackPath.TrackIndexForTravel(to.Laterals.Count, departForward));
            }
        }
        return links;
    }

    /// <summary>レールを張る判定で、パスを指定の向き（幾何の始点→終点が forward）に走れるか。</summary>
    private static bool CanRun(TrackEntry entry, bool forward)
    {
        return entry.Platform || VehicleManager.DirectionAllowsEntry(entry.Path.travel_direction, forward);
    }

    /// <summary>
    /// 軌道数3以上の線路を含む組の対応付け。走行フレーム（from から to へ進む向きの右が正）での
    /// 横位置順を保つ単調対応で、軌道数の少ない側で合流させて全軌道端を結ぶ。
    /// </summary>
    private static void AddLateralOrderLinks(List<Link> links, TrackEntry from, int a, TrackEntry to, int b)
    {
        // 横位置は幾何フレームなので、ノードが始点側の到着線路・終点側の出発線路は走行フレームで左右が反転する。
        List<int> orderFrom = SortByTravelLateral(from, from.NodeIsStart ? -1f : 1f);
        List<int> orderTo = SortByTravelLateral(to, to.NodeIsStart ? 1f : -1f);
        int count = Mathf.Max(orderFrom.Count, orderTo.Count);
        for (int k = 0; k < count; k++)
        {
            float t = count == 1 ? 0f : (float)k / (count - 1);
            AddLink(links, a, orderFrom[Mathf.RoundToInt(t * (orderFrom.Count - 1))],
                b, orderTo[Mathf.RoundToInt(t * (orderTo.Count - 1))]);
        }
    }

    /// <summary>軌道番号を、走行フレームの横位置（幾何フレームの横位置 × sign）の昇順に並べる。</summary>
    private static List<int> SortByTravelLateral(TrackEntry entry, float sign)
    {
        var order = new List<int>(entry.Laterals.Count);
        for (int i = 0; i < entry.Laterals.Count; i++) order.Add(i);
        order.Sort((x, y) => (entry.Laterals[x] * sign).CompareTo(entry.Laterals[y] * sign));
        return order;
    }

    /// <summary>軌道端の組を追加する。逆向きの動きで同じ組が出ても1本にまとめる。</summary>
    private static void AddLink(List<Link> links, int a, int trackA, int b, int trackB)
    {
        if (a > b)
        {
            (a, b) = (b, a);
            (trackA, trackB) = (trackB, trackA);
        }
        foreach (Link link in links)
        {
            if (link.A == a && link.TrackA == trackA && link.B == b && link.TrackB == trackB) return;
        }
        links.Add(new Link { A = a, TrackA = trackA, B = b, TrackB = trackB });
    }

    /// <summary>
    /// 軌道端 (from, trackFrom) から (to, trackTo) へのレールコネクタを追加する。
    /// 両側の切断点の軌道中心を、端点接線をハンドルにした三次ベジエで滑らかに結ぶ。
    /// </summary>
    private static void AddConnector(List<RailConnectorSpec> specs, TrackEntry from, int trackFrom, TrackEntry to, int trackTo, Vector3 nodePosition, bool horizontalPlane)
    {
        // from からノードへ入り、ノードから to へ抜ける向き。S字の区間では軌道の向きが中心線から傾く。
        Vector2 travelIn = -TrackDirection(from, trackFrom);
        Vector2 travelOut = TrackDirection(to, trackTo);
        Vector2 start = TrackEndPoint(from, trackFrom);
        Vector2 end = TrackEndPoint(to, trackTo);

        float dist = Vector2.Distance(start, end);
        if (dist < MIN_CONNECTOR_LENGTH) return;

        Vector2 p1 = start + travelIn * (dist / 3f);
        Vector2 p2 = end - travelOut * (dist / 3f);

        int sampleCount = Mathf.Clamp(Mathf.CeilToInt(dist / SAMPLE_INTERVAL) + 1, 4, 24);
        List<Vector3> points = new List<Vector3>(sampleCount);
        for (int s = 0; s < sampleCount; s++)
        {
            float t = (float)s / (sampleCount - 1);
            Vector2 pt = PolylineUtils.CubicBezierPoint(start, p1, p2, end, t);
            points.Add(ToLocalTrackPoint(pt, nodePosition, horizontalPlane));
        }

        // 見た目の基準となるレールプレハブは、歩道コネクタと同じく variant 番号が大きい方に合わせる。
        int fromVariant = from.Path.path_variant;
        int toVariant = to.Path.path_variant;
        TrackEntry prefabSource = fromVariant >= toVariant ? from : to;
        bool embedded = LrtRoadParts.IsEmbedded(fromVariant) || LrtRoadParts.IsEmbedded(toVariant);
        if (embedded) prefabSource = LrtRoadParts.IsEmbedded(fromVariant) ? from : to;

        specs.Add(new RailConnectorSpec(points, prefabSource.Path.path_variant, prefabSource.Path.path_type) {
            StartDirection = travelIn, EndDirection = travelOut, StartSlope = -from.Slope, EndSlope = to.Slope,
            EmbeddedLrt = embedded,
            HasWire = HasOverheadWire(fromVariant) && HasOverheadWire(toVariant),
            StartRailBase = embedded ? LrtRoadParts.RailTopAboveSpline(fromVariant) - LrtRoadParts.EMBEDDED_RAIL_TOP : RoadPartHeights.BODY_ABOVE_SPLINE,
            EndRailBase = embedded ? LrtRoadParts.RailTopAboveSpline(toVariant) - LrtRoadParts.EMBEDDED_RAIL_TOP : RoadPartHeights.BODY_ABOVE_SPLINE,
            StartWireOffset = LrtRoadParts.IsEmbedded(fromVariant) ? 0 : -.28f,
            EndWireOffset = LrtRoadParts.IsEmbedded(toVariant) ? 0 : -.28f
        });
    }

    private static bool HasOverheadWire(int variant)
    {
        var spacing = MasterDataStore.Instance?.GetPathMaster(variant)?.wire_spacing;
        return spacing.HasValue && spacing.Value > 0f;
    }

    /// <summary>
    /// 切断点での軌道中心の位置（XZ）。軌道の横位置は本線と同じ幾何フレームの値
    /// （TrackPath.GetTrackCenterOffset / LrtRoadParts.TrackLaterals）なので、切断点で本線の軌道と正確に一致する。
    /// </summary>
    private static Vector2 TrackEndPoint(TrackEntry entry, int track)
    {
        Vector2 geometricForward = entry.NodeIsStart ? entry.Direction : -entry.Direction;
        return entry.ConnectionPoint + RightOf(geometricForward) * entry.Laterals[track];
    }

    /// <summary>切断点での軌道 track の、ノードから線路の内部へ向かう向き(S字の横位置の変化を含む)。</summary>
    private static Vector2 TrackDirection(TrackEntry entry, int track)
    {
        float slope = entry.LateralSlopes != null && track < entry.LateralSlopes.Length ? entry.LateralSlopes[track] : 0f;
        if (Mathf.Abs(slope) < 1e-6f) return entry.Direction;
        Vector2 geometricForward = entry.NodeIsStart ? entry.Direction : -entry.Direction;
        return (entry.Direction + RightOf(geometricForward) * slope).normalized;
    }

    /// <summary>
    /// 交差点の板なしで2本の線路をノードまでつなぐかを判定する。交差点の対象のパスがちょうど2本の線路で、
    /// レールがつながる組で、中心線の折れ角が TRACK_JOIN_MAX_BEND_DEG 以下で、ノードでの軌道の横位置と幅
    /// (S字のずれを含む)が一致するとき。切断しないので、架線・桁・ホームがノードで途切れない。
    /// connectionPoints は各パスのノード側の端点(切断位置=端点)。条件を満たさなければ null を返す。
    /// </summary>
    public static TrackContinuousJoin TryContinuousJoin(int nodeId, List<PathSummary> paths, Func<PathSummary, List<Vector3>> pointResolver,
        Dictionary<int, TrackEndTransition.EndShift> shifts, out Dictionary<int, Vector3> connectionPoints)
    {
        connectionPoints = null;
        if (paths == null || paths.Count != 2) return null;
        PathSummary a = paths[0], b = paths[1];
        if (!IsTrackType(a.path_type) || !IsTrackType(b.path_type)
            || !RailsConnect(a.path_type, a.path_variant, b.path_type, b.path_variant)) return null;
        bool aStart = a.start_node_id == nodeId, bStart = b.start_node_id == nodeId;
        if (aStart == (a.end_node_id == nodeId) || bStart == (b.end_node_id == nodeId)) return null;

        if (!TryNodeEnd(a, aStart, pointResolver, out Vector3 nodeA, out Vector2 outA)
            || !TryNodeEnd(b, bStart, pointResolver, out Vector3 nodeB, out Vector2 outB)) return null;
        if (Vector2.Dot(outA, outB) > -Mathf.Cos(LocalConstants.TRACK_JOIN_MAX_BEND_DEG * Mathf.Deg2Rad)) return null;

        List<float> lateralsA = LrtRoadParts.TrackLaterals(a), lateralsB = LrtRoadParts.TrackLaterals(b);
        int count = lateralsA.Count;
        if (count != lateralsB.Count) return null;
        shifts.TryGetValue(a.path_id, out var shiftA);
        shifts.TryGetValue(b.path_id, out var shiftB);
        bool aligned = aStart != bStart;
        for (int i = 0; i < count; i++)
        {
            int j = aligned ? i : count - 1 - i;
            float lateralA = lateralsA[i] + (shiftA != null ? shiftA.Shift(i, 0f) : 0f);
            float lateralB = lateralsB[j] + (shiftB != null ? shiftB.Shift(j, 0f) : 0f);
            if (Mathf.Abs(lateralA - (aligned ? lateralB : -lateralB)) > LocalConstants.TRACK_JOIN_LATERAL_TOLERANCE) return null;
        }
        float widthA = MasterWidth(a) + (shiftA != null ? 2f * shiftA.OuterShift(0f) : 0f);
        float widthB = MasterWidth(b) + (shiftB != null ? 2f * shiftB.OuterShift(0f) : 0f);
        if (Mathf.Abs(widthA - widthB) > LocalConstants.TRACK_JOIN_LATERAL_TOLERANCE) return null;

        connectionPoints = new Dictionary<int, Vector3> { { a.path_id, nodeA }, { b.path_id, nodeB } };
        return new TrackContinuousJoin { PathA = a.path_id, PathB = b.path_id };
    }

    /// <summary>パスのノード側の端点と、そこからパスの内部へ向かう中心線の向き(XZ)。</summary>
    private static bool TryNodeEnd(PathSummary path, bool nodeIsStart, Func<PathSummary, List<Vector3>> pointResolver, out Vector3 nodeEnd, out Vector2 outward)
    {
        nodeEnd = default;
        outward = default;
        var source = pointResolver != null ? pointResolver(path) : IntersectionPolygonUtil.SamplePlan(path);
        if (source == null || source.Count < 2) return false;
        var points = new List<Vector3>(source);
        if (!nodeIsStart) points.Reverse();
        nodeEnd = points[0];
        outward = IntersectionPolygonUtil.AtDistance(points, 0f).Direction;
        return outward.sqrMagnitude > 1e-6f;
    }

    private static float MasterWidth(PathSummary path)
    {
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(path.path_variant) : null;
        return master != null && master.width > 0f ? master.width : path.width;
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
