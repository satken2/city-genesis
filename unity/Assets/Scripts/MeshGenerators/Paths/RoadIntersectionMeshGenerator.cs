using UnityEngine;
using System;
using System.Collections.Generic;
using GeneratedModels;
using NetTopologySuite.Geometries;

/// <summary>
/// 道路ノードに作られる交差点メッシュを生成します。
/// 車道部分は各接続道路の幅から求めた凸包で作り、道路端点をその凸包の接続断面へ移動させます。
/// 歩道部分は隣り合う道路の歩道端を結ぶ短いスプラインとして返し、通常道路と同じ sidewalk プレハブを曲げて配置します。
/// </summary>
public class RoadIntersectionMeshGenerator : INodeMeshGenerator
{
    private const float TriangulationEpsilon = 1e-5f;
    // 歩道エンドキャップ（歩道なし道路が隣にある場合の丸め終端）の最小長。
    // これより短いキャップは形状が潰れて見えるため生成しない。
    private const float MinSidewalkEndCapLength = 0.3f;
    // エンドキャップのために直線歩道を車道切断面から後退させる量の、歩道幅に対する係数。
    // キャップは半円状に閉じるため、縦寸法はおおよそ歩道幅の半分あれば足りる。
    private const float SidewalkEndCapSetbackFactor = 0.5f;

    public NodeMeshResult Generate(int nodeId, Vector3 nodePosition, List<PathSummary> paths, float nodeRelativeY = 0f)
    {
        if (paths == null || paths.Count < 2)
        {
            return null;
        }

        try
        {
            var gf = new GeometryFactory();
            Vector2 nodePos2D = new Vector2(nodePosition.x, nodePosition.z);

            // 相対Y≠0のノード（高架同士の平面交差など）は地形をサンプリングせず、
            // ノード絶対Y（nodePosition.y）の水平面として交差点を生成する。
            // 地形基準で生成すると、空中の交差点が地面に描かれ、切断点の代入で
            // 道路端まで地面に引き下げられてしまう。
            bool horizontalPlane = nodeRelativeY != 0f;

            var roadResult = IntersectionPolygonUtil.ComputeIntersectionPolygon(
                nodeId,
                nodePos2D,
                paths,
                gf,
                ResolveRoadWidth
            );

            Mesh roadMesh = ConvertGeometryToMesh(roadResult.Geometry, nodePosition, horizontalPlane, "RoadIntersection");
            if (roadMesh == null)
            {
                return null;
            }

            Dictionary<int, Vector3> connectionPoints = BuildConnectionPoints(roadResult.ConnectionPoints, nodePosition, horizontalPlane);
            Material roadMaterial = LoadRoadMaterial(paths);

            List<SidewalkConnectorSpec> sidewalkConnectors = null;
            Dictionary<int, SidewalkSetback> sidewalkSetbacks = null;
            if (GetSidewalkPaths(paths).Count >= 1)
            {
                // 歩道は車道交差点のような一枚板ではなく、通常道路で使っている sidewalk プレハブを
                // 隣り合う道路の歩道端を結ぶ短いスプラインへ流し込む。これにより、歩道プレハブの形状、
                // 厚み、マテリアル、UV の扱いを通常道路と揃えられる。
                // 歩道が1本しかない場合でもエンドキャップ（丸め終端）を生成するため、1本から呼ぶ。
                sidewalkConnectors = BuildSidewalkConnectorSpecs(nodeId, nodePosition, horizontalPlane, paths, roadResult.ConnectionPoints, out sidewalkSetbacks);
            }

            // 線路（PATH_TRACK/PATH_MONORAIL）が2本以上接続するノードでは、
            // ポイント（分岐器）・平面交差の表現として、転回制約を満たす通行可能ペアにのみ
            // レールコネクタを敷く（TrackIntersectionMeshGenerator参照）。
            List<RailConnectorSpec> railConnectors = TrackIntersectionMeshGenerator.BuildRailConnectorSpecs(nodeId, nodePosition, horizontalPlane, paths, roadResult.ConnectionPoints);

            return new NodeMeshResult(roadMesh, roadMaterial, connectionPoints, sidewalkConnectors, railConnectors, sidewalkSetbacks);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RoadIntersectionMeshGenerator] ノード {nodeId} の交差点メッシュ生成に失敗: {ex}");
            return null;
        }
    }

    /// <summary>
    /// 交差点の高さの基準面（＝道路のスプライン点が置かれる平面）を求める。
    /// 地表の交差点では各地点の地形標高、高架ノードではノード絶対Yの水平面を基準にし、
    /// どちらも道路のスプライン点と同じ ROAD_SPLINE_OFFSET を加算する
    /// （道路の端点は PathManager.GetPathPointPosition で「地形+相対Y+ROAD_SPLINE_OFFSET」に
    /// 置かれるため、高架でもこのオフセットを揃える必要がある）。
    /// </summary>
    internal static float SampleBaseSurfaceY(float x, float z, Vector3 nodePosition, bool horizontalPlane)
    {
        float baseY = horizontalPlane
            ? nodePosition.y
            : HeightmapTextureManager.GetTerrainHeight(new Vector3(x, 1000f, z));
        return baseY + RoadPartHeights.ROAD_SPLINE_OFFSET;
    }

    private static Dictionary<int, Vector3> BuildConnectionPoints(Dictionary<int, Vector2> source, Vector3 nodePosition, bool horizontalPlane)
    {
        if (source == null || source.Count == 0)
        {
            return null;
        }

        Dictionary<int, Vector3> result = new Dictionary<int, Vector3>();
        foreach (var kvp in source)
        {
            // 接続点は PathManager によって道路のスプライン点（端点）へそのまま代入されるため、
            // 高さは「スプライン点の基準面」で返す。路面上面の高さ（+BODY_ABOVE_SPLINE）で返すと、
            // 道路の始端だけ他のスプライン点より一段高くなり、始端に不自然な盛り上がりができる。
            // 高さは接続点自身の位置の地形標高から取り直す（交差点中心との微妙な標高差を吸収する）。
            float y = SampleBaseSurfaceY(kvp.Value.x, kvp.Value.y, nodePosition, horizontalPlane);
            result[kvp.Key] = new Vector3(kvp.Value.x, y, kvp.Value.y);
        }
        return result;
    }

    private static float ResolveRoadWidth(PathSummary path)
    {
        // 描画側の道路幅はマスターデータを正とする。PathSummary.width はバックエンド由来の値であり、
        // 通常道路メッシュ生成もマスターデータを参照するため、交差点計算も同じ基準に揃える。
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(path.path_variant) : null;
        if (master != null && master.width > 0f)
        {
            return master.width;
        }
        return path.width > 0f ? path.width : 4.0f;
    }

    private static float ResolveSidewalkWidth(PathSummary path)
    {
        // sidewalk_width が 0 以下の道路は歩道を持たない道路として扱う。
        // マスターデータが読めない場合は、既存の RoadPath と同じ既定値を使う。
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(path.path_variant) : null;
        return master != null && master.sidewalk_width.HasValue ? Mathf.Max(0f, master.sidewalk_width.Value) : 0.5f;
    }

    private class SidewalkRoadEntry
    {
        // 交差点に接続している1本の道路について、歩道接続スプラインを作るために必要な情報を保持する。
        // Direction はノードから道路外側へ向かう単位ベクトルで、左右歩道端の判定と角度順ソートに使う。
        // 歩道を持たない道路もエントリに含める（歩道接続の「ブロッカー」として角度順の隣接判定に必要）。
        public int PathId;
        public Vector2 Direction;
        public Vector2 ConnectionPoint;
        public float RoadHalfWidth;
        public float SidewalkWidth;
        public int PathVariant;
        public int PathType;
        public float Angle;

        /// <summary>この道路の歩道を交差点の歩道パーツに参加させられるか（歩道あり＋接続断面が計算済み）。</summary>
        public bool CanSidewalk;
    }

    private static List<SidewalkConnectorSpec> BuildSidewalkConnectorSpecs(int nodeId, Vector3 nodePosition, bool horizontalPlane, List<PathSummary> paths, Dictionary<int, Vector2> connectionPoints, out Dictionary<int, SidewalkSetback> sidewalkSetbacks)
    {
        sidewalkSetbacks = null;
        if (connectionPoints == null || connectionPoints.Count == 0)
        {
            return null;
        }

        List<SidewalkRoadEntry> entries = BuildSidewalkRoadEntries(nodeId, paths, connectionPoints);
        if (entries.Count == 0)
        {
            return null;
        }

        List<SidewalkConnectorSpec> specs = new List<SidewalkConnectorSpec>();
        Dictionary<int, SidewalkSetback> setbacks = new Dictionary<int, SidewalkSetback>();

        if (entries.Count == 1)
        {
            // 有効な方向を持つ道路が1本しかない（他は方向が計算できず脱落した）場合、
            // その歩道は左右ともエンドキャップで終端する。
            if (entries[0].CanSidewalk)
            {
                AddEndCap(specs, setbacks, nodePosition, horizontalPlane, entries[0], isLeftSide: true);
                AddEndCap(specs, setbacks, nodePosition, horizontalPlane, entries[0], isLeftSide: false);
            }
            sidewalkSetbacks = setbacks.Count > 0 ? setbacks : null;
            return specs.Count > 0 ? specs : null;
        }

        entries.Sort((a, b) => a.Angle.CompareTo(b.Angle));

        // 全接続道路を角度順に並べ、隣り合う道路のペアごとにセクター（道路間の扇形領域）を処理する。
        // 両方の道路が歩道を持つ場合のみ歩道同士を接続し、片方でも歩道なし道路が挟まる場合は
        // 歩道を接続せず、交差点の少し手前で丸めて終端するエンドキャップを作る。
        // 歩道を持つ道路だけでペアを作ると、間に挟まる歩道なし道路（高架道路など）を飛ばして
        // 交差点を横断する歩道ができてしまう。
        for (int i = 0; i < entries.Count; i++)
        {
            SidewalkRoadEntry current = entries[i];
            SidewalkRoadEntry next = entries[(i + 1) % entries.Count];
            float sectorAngle = PositiveAngleDelta(current.Angle, next.Angle);
            if (sectorAngle <= 0.01f)
            {
                continue;
            }

            if (current.CanSidewalk && next.CanSidewalk)
            {
                AddSpecIfValid(specs, BuildSidewalkConnectorSpec(nodePosition, horizontalPlane, current, next, sectorAngle));
            }
            else
            {
                // このセクターに面している歩道端（current の左側・next の右側）を個別にキャップで終端する。
                if (current.CanSidewalk)
                {
                    AddEndCap(specs, setbacks, nodePosition, horizontalPlane, current, isLeftSide: true);
                }
                if (next.CanSidewalk)
                {
                    AddEndCap(specs, setbacks, nodePosition, horizontalPlane, next, isLeftSide: false);
                }
            }
        }

        sidewalkSetbacks = setbacks.Count > 0 ? setbacks : null;
        return specs.Count > 0 ? specs : null;
    }

    /// <summary>
    /// エンドキャップの spec を生成して追加し、成功した場合はその道路・その側の
    /// 歩道セットバック量（直線歩道を切断面から後退させる量）を記録する。
    /// </summary>
    private static void AddEndCap(List<SidewalkConnectorSpec> specs, Dictionary<int, SidewalkSetback> setbacks, Vector3 nodePosition, bool horizontalPlane, SidewalkRoadEntry entry, bool isLeftSide)
    {
        SidewalkConnectorSpec spec = BuildSidewalkEndCapSpec(nodePosition, horizontalPlane, entry, isLeftSide, out float setback);
        if (spec == null)
        {
            return;
        }
        specs.Add(spec);

        if (setback > 0f)
        {
            // 同じ道路が複数セクターで左右別々にキャップされ得るため、左右を独立に統合する。
            setbacks.TryGetValue(entry.PathId, out SidewalkSetback s);
            if (isLeftSide)
            {
                s.LeftOutwardSetback = Mathf.Max(s.LeftOutwardSetback, setback);
            }
            else
            {
                s.RightOutwardSetback = Mathf.Max(s.RightOutwardSetback, setback);
            }
            setbacks[entry.PathId] = s;
        }
    }

    private static void AddSpecIfValid(List<SidewalkConnectorSpec> specs, SidewalkConnectorSpec spec)
    {
        if (spec != null)
        {
            specs.Add(spec);
        }
    }

    private static List<SidewalkRoadEntry> BuildSidewalkRoadEntries(int nodeId, List<PathSummary> paths, Dictionary<int, Vector2> connectionPoints)
    {
        List<SidewalkRoadEntry> entries = new List<SidewalkRoadEntry>();
        foreach (var path in paths)
        {
            Vector2 nodePoint;
            Vector2 directionPoint;
            if (!TryGetPathDirectionFromNode(nodeId, path, out nodePoint, out directionPoint))
            {
                continue;
            }

            Vector2 direction = directionPoint - nodePoint;
            if (direction.sqrMagnitude < 1e-6f)
            {
                continue;
            }
            direction.Normalize();

            // 歩道を持たない道路や接続断面が計算できなかった道路もエントリには含める
            // （角度順の隣接判定で「歩道接続をブロックする道路」として必要）。
            // ただし歩道パーツの生成対象にはしない（CanSidewalk=false）。
            float sidewalkWidth = ResolveSidewalkWidth(path);
            bool hasConnection = connectionPoints.TryGetValue(path.path_id, out Vector2 connectionPoint);

            entries.Add(new SidewalkRoadEntry
            {
                PathId = path.path_id,
                Direction = direction,
                ConnectionPoint = hasConnection ? connectionPoint : Vector2.zero,
                RoadHalfWidth = ResolveRoadWidth(path) * 0.5f,
                SidewalkWidth = sidewalkWidth,
                PathVariant = path.path_variant,
                PathType = path.path_type,
                Angle = Mathf.Atan2(direction.y, direction.x),
                CanSidewalk = sidewalkWidth > 0f && hasConnection
            });
        }
        return entries;
    }

    /// <summary>
    /// 歩道が交差点内へ続かない道路の歩道端を、交差点の少し手前で丸めて終端するエンドキャップを作る。
    /// 隣（角度順で隣接するセクター側）の道路が歩道を持たない場合に使う。
    ///
    /// 直線側の歩道は setback の分だけ切断面より手前（道路外側）で clip され
    /// （RoadPath.CreateRoadParts が SidewalkSetbacks を消費）、その空いた帯
    /// [halfWidth - sidewalkWidth, halfWidth] の区間にこのキャップを敷く。
    /// キャップは帯の中心線をスプラインとし、切断面に向かって幅を先細りさせることで
    /// 丸まりながら収束する終端形状になる。交差点の内側へは入り込まない。
    /// </summary>
    /// <param name="isLeftSide">道路をノードから外向きに見たときの左側の歩道端か。</param>
    /// <param name="setback">直線側の歩道を切断面から後退させるべき量（メートル）。生成失敗時は 0。</param>
    private static SidewalkConnectorSpec BuildSidewalkEndCapSpec(Vector3 nodePosition, bool horizontalPlane, SidewalkRoadEntry entry, bool isLeftSide, out float setback)
    {
        setback = 0f;
        Vector2 lat = isLeftSide ? PerpendicularLeft(entry.Direction) : -PerpendicularLeft(entry.Direction);
        float sw = entry.SidewalkWidth;

        // 後退量は歩道幅の半分程度。交差点中心を越えて道路長を食い潰さないよう制限する。
        Vector2 toNode = new Vector2(nodePosition.x, nodePosition.z) - entry.ConnectionPoint;
        float maxLen = toNode.magnitude * 0.5f;
        float capLen = Mathf.Min(Mathf.Max(sw * SidewalkEndCapSetbackFactor, MinSidewalkEndCapLength), maxLen);
        if (capLen < MinSidewalkEndCapLength)
        {
            return null;
        }
        setback = capLen;

        // 歩道帯 [halfWidth - sw, halfWidth] の中心線の、切断面上の位置。
        // スプラインは縁石ではなく帯中心線に置く（実体化側はプレハブの bounds 中心を
        // スプラインへ載せるため、直線部の帯位置と一致させるには中心線が必要）。
        Vector2 bandCenterAtCut = entry.ConnectionPoint + lat * (entry.RoadHalfWidth - sw * 0.5f);

        // キャップは切断面から道路外側へ capLen の区間（＝clip で後退した直線歩道の跡地）を占める。
        Vector2 capOpen = bandCenterAtCut + entry.Direction * capLen; // 直線歩道の終端と突き合う全幅端
        Vector2 capClose = bandCenterAtCut;                           // 切断面上で閉じる
        List<Vector2> bandPoints = new List<Vector2>
        {
            capOpen,
            Vector2.Lerp(capOpen, capClose, 0.5f),
            capClose,
        };
        List<float> widths = new List<float> { sw, sw * 0.7f, 0.05f };

        // sidewalk プレハブは「縁石（道路中心側の面）が +X＝進行方向右側」（左側用パーツ）の規約。
        // 縁石が道路の内側（-lat）を、外側端が道路境界側（+lat）を向く進行方向を選ぶ。
        Vector2 requiredTravel = -PerpendicularLeft(lat);
        if (Vector2.Dot(requiredTravel, capClose - capOpen) < 0f)
        {
            bandPoints.Reverse();
            widths.Reverse();
        }

        List<Vector3> points = new List<Vector3>(bandPoints.Count);
        for (int i = 0; i < bandPoints.Count; i++)
        {
            points.Add(ToLocalTerrainPoint(bandPoints[i], nodePosition, horizontalPlane));
        }
        return new SidewalkConnectorSpec(points, widths, entry.PathVariant, entry.PathType);
    }

    internal static bool TryGetPathDirectionFromNode(int nodeId, PathSummary path, out Vector2 nodePoint, out Vector2 directionPoint)
    {
        nodePoint = Vector2.zero;
        directionPoint = Vector2.zero;

        List<float> start = path.GetStartPointList();
        List<float> end = path.GetEndPointList();
        if (start == null || start.Count < 3 || end == null || end.Count < 3)
        {
            return false;
        }

        if (path.start_node_id == nodeId)
        {
            // 始点側ノードに接続している場合、最初のウェイポイントが道路の進入方向を最もよく表す。
            // ウェイポイントがない直線道路では終点方向を使う。
            nodePoint = new Vector2(start[0], start[2]);
            var waypoints = path.GetWaypointList();
            if (waypoints != null && waypoints.Count > 0)
            {
                var waypoint = waypoints[0];
                directionPoint = new Vector2(waypoint.position[0], waypoint.position[2]);
            }
            else
            {
                directionPoint = new Vector2(end[0], end[2]);
            }
            return true;
        }

        if (path.end_node_id == nodeId)
        {
            // 終点側ノードに接続している場合は、最後のウェイポイントからノードへ入ってくる。
            // ノードから外側へ向かう方向を得るため、最後のウェイポイントまたは始点を参照する。
            nodePoint = new Vector2(end[0], end[2]);
            var waypoints = path.GetWaypointList();
            if (waypoints != null && waypoints.Count > 0)
            {
                var waypoint = waypoints[waypoints.Count - 1];
                directionPoint = new Vector2(waypoint.position[0], waypoint.position[2]);
            }
            else
            {
                directionPoint = new Vector2(start[0], start[2]);
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// 隣り合う2本の道路（角度順で current → next、間のセクターは current の左側・next の右側）の
    /// 歩道同士を結ぶコネクタの spec を作る。
    ///
    /// 交差点隅を「2道路の切断断面線の交点 C を中心とした同心円弧」として構成する。
    /// 各切断面上での歩道帯の外側端（＝道路境界）は C から見た半径 ρ の位置にあり、
    /// スプラインは帯の中心線（半径 ρ - 歩道幅/2）に置く。弧の半径ベクトルが両端で
    /// 切断面の横方向（lat）と一致するため、弧の接線＝直線部の進行方向となり C1 連続になる。
    /// 対称な直交交差点では ρ=0 に退化し、弧は「交差点隅の点 C を中心にした半径＝歩道幅の
    /// 四分円板」を掃く（これが期待される見た目）。
    /// スプライン点が帯中心線なので、実体化側（RoadPath.CreateSidewalkConnectorObject）が
    /// プレハブの bounds 中心をスプラインに載せると、直線部の歩道帯 [halfWidth-sw, halfWidth]
    /// と正確に連続する。
    /// </summary>
    private static SidewalkConnectorSpec BuildSidewalkConnectorSpec(Vector3 nodePosition, bool horizontalPlane, SidewalkRoadEntry current, SidewalkRoadEntry next, float sectorAngle)
    {
        // セクターに面する横方向（切断面に沿った、道路中心→このセクター側の単位ベクトル）
        Vector2 latStart = PerpendicularLeft(current.Direction);
        Vector2 latEnd = -PerpendicularLeft(next.Direction);

        // 各切断面上の歩道帯の外側端＝道路境界の点。直線部の歩道帯はここまで届いている。
        Vector2 startOuter = current.ConnectionPoint + latStart * current.RoadHalfWidth;
        Vector2 endOuter = next.ConnectionPoint + latEnd * next.RoadHalfWidth;

        float swStart = current.SidewalkWidth;
        float swEnd = next.SidewalkWidth;

        // 弧の掃引角。lat の向きは道路方向+90°/-90°なので、latStart→latEnd の回転角は
        // sectorAngle - π（凸コーナーでは負＝時計回り）。
        // セクターが平角に近い/超える場合（ほぼ一直線・reflex）は同心円弧が成立しないため直線帯にする。
        float sweep = sectorAngle - Mathf.PI;

        List<Vector2> bandPoints;
        List<float> widths;

        bool useArc = sweep < -0.05f;
        if (useArc)
        {
            // 隅の中心 C = 2つの切断断面線（ConnectionPoint を通り lat 方向の直線）の交点。
            // startOuter + latStart * s = endOuter + latEnd * u を解く。
            Vector2 w = endOuter - startOuter;
            float denom = Cross2(latStart, latEnd);
            float s = Mathf.Abs(denom) > 1e-4f ? Cross2(w, latEnd) / denom : float.NaN;
            if (float.IsNaN(s))
            {
                useArc = false;
                bandPoints = null;
                widths = null;
            }
            else
            {
                Vector2 c = startOuter + latStart * s;
                // C から見た帯外側端の符号付き半径（lat 方向成分）。対称交差点では 0 に退化し得る。
                float rhoStart = Vector2.Dot(startOuter - c, latStart);
                float rhoEnd = Vector2.Dot(endOuter - c, latEnd);

                // 鋭角セクター等で C が異常に遠い場合は数値誤差が大きくなるため直線帯へ逃がす。
                float saneRadius = (current.RoadHalfWidth + next.RoadHalfWidth + swStart + swEnd) * 4f;
                if (Mathf.Max(Mathf.Abs(rhoStart), Mathf.Abs(rhoEnd)) > saneRadius)
                {
                    useArc = false;
                    bandPoints = null;
                    widths = null;
                }
                else
                {
                    float angleStart = Mathf.Atan2(latStart.y, latStart.x);
                    float centerRadiusStart = rhoStart - swStart * 0.5f;
                    float centerRadiusEnd = rhoEnd - swEnd * 0.5f;
                    float arcLenEstimate = Mathf.Abs(sweep) * Mathf.Max(Mathf.Abs(centerRadiusStart), Mathf.Abs(centerRadiusEnd));
                    int steps = Mathf.Clamp(Mathf.CeilToInt(arcLenEstimate / 0.5f), 2, 32);

                    bandPoints = new List<Vector2>(steps + 1);
                    widths = new List<float>(steps + 1);
                    for (int i = 0; i <= steps; i++)
                    {
                        float t = (float)i / steps;
                        float ang = angleStart + sweep * t;
                        Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                        float swT = Mathf.Lerp(swStart, swEnd, t);
                        float rho = Mathf.Lerp(rhoStart, rhoEnd, t);
                        // 帯中心線 = 外側端の符号付き半径から歩道幅の半分だけ内側
                        bandPoints.Add(c + dir * (rho - swT * 0.5f));
                        widths.Add(swT);
                    }
                }
            }
        }
        else
        {
            bandPoints = null;
            widths = null;
        }

        if (!useArc || bandPoints == null)
        {
            // 直線帯フォールバック: 両切断面上の帯中心点を直線で結ぶ。
            Vector2 startCenter = startOuter - latStart * (swStart * 0.5f);
            Vector2 endCenter = endOuter - latEnd * (swEnd * 0.5f);
            if ((endCenter - startCenter).sqrMagnitude < 1e-4f)
            {
                return null;
            }
            bandPoints = new List<Vector2>
            {
                startCenter,
                Vector2.Lerp(startCenter, endCenter, 0.5f),
                endCenter,
            };
            widths = new List<float> { swStart, (swStart + swEnd) * 0.5f, swEnd };
        }

        // sidewalk プレハブは「縁石（道路中心側の面）が +X＝進行方向右側」（左側用パーツ）の規約。
        // 縁石が帯の内側（交差点中心側 = -latStart）を、外側端が道路境界側（+latStart）を
        // 向く進行方向になるよう、必要なら反転する。
        Vector2 requiredTravel = -PerpendicularLeft(latStart);
        if (Vector2.Dot(requiredTravel, bandPoints[1] - bandPoints[0]) < 0f)
        {
            bandPoints.Reverse();
            widths.Reverse();
        }

        List<Vector3> points = new List<Vector3>(bandPoints.Count);
        for (int i = 0; i < bandPoints.Count; i++)
        {
            points.Add(ToLocalTerrainPoint(bandPoints[i], nodePosition, horizontalPlane));
        }

        // 見た目の基準となる sidewalk プレハブは、接続する2本の道路のうち variant 番号が大きい方を使う。
        // 同じ交差点内に複数の歩道種別がある場合でも、角ごとに優先度の高い歩道表現へ合わせる。
        SidewalkRoadEntry prefabSource = current.PathVariant >= next.PathVariant ? current : next;
        return new SidewalkConnectorSpec(points, widths, prefabSource.PathVariant, prefabSource.PathType);
    }

    /// <summary>2Dベクトルの外積（z成分）。</summary>
    private static float Cross2(Vector2 a, Vector2 b)
    {
        return a.x * b.y - a.y * b.x;
    }

    private static Vector3 ToLocalTerrainPoint(Vector2 point, Vector3 nodePosition, bool horizontalPlane)
    {
        // 交差点オブジェクトの子として配置するため、スプライン点はノード基準のローカル座標に変換する。
        // 高さは基準面 + 歩道の相対高さで、通常道路の歩道の下端（スプライン + SIDEWALK_ABOVE_SPLINE）と
        // 段差なく揃える。
        float worldY = SampleBaseSurfaceY(point.x, point.y, nodePosition, horizontalPlane) + RoadPartHeights.SIDEWALK_ABOVE_SPLINE;
        return new Vector3(point.x - nodePosition.x, worldY - nodePosition.y, point.y - nodePosition.z);
    }

    private static Vector2 PerpendicularLeft(Vector2 direction)
    {
        return new Vector2(-direction.y, direction.x);
    }

    private static float PositiveAngleDelta(float from, float to)
    {
        // Mathf.DeltaAngle は度数法かつ -180〜180 度の範囲へ折り返すため、
        // 角度順に並べた道路間の反時計回りセクター幅を扱うここでは 0〜2π の正の差分を使う。
        float delta = to - from;
        while (delta < 0f) delta += Mathf.PI * 2f;
        while (delta >= Mathf.PI * 2f) delta -= Mathf.PI * 2f;
        return delta;
    }

    private static List<PathSummary> GetSidewalkPaths(List<PathSummary> paths)
    {
        List<PathSummary> result = new List<PathSummary>();
        foreach (var path in paths)
        {
            if (ResolveSidewalkWidth(path) > 0f)
            {
                result.Add(path);
            }
        }
        return result;
    }

    private static Material LoadRoadMaterial(List<PathSummary> paths)
    {
        // 交差点のアスファルトは、接続道路のうち代表バリアント（variant番号が最大の道路）の
        // body プレハブのマテリアルを使い、道路本体と見た目を揃える。
        // RoadPath と同じマテリアルキャッシュ（Resources同名マテリアルの再割当＋renderQueue調整）を
        // 経由することで、交差点内だけ舗装の色味や描画設定が変わるのを防ぐ。
        if (ModelManager.Instance != null && paths != null)
        {
            // 道路と線路が混在するノード（踏切）では道路側の舗装を優先する
            // （線路バリアントは番号が大きく、単純な最大variant選択だと踏切全体が
            //  道床（バラスト）の見た目になってしまう。踏切は道路の舗装が連続し、
            //  その上をレールコネクタが横切るのが正しい見た目）。
            // 同じカテゴリ（道路どうし・線路どうし）の中では従来どおり最大variantを代表にする。
            PathSummary selectedPath = null;
            foreach (var path in paths)
            {
                if (selectedPath == null)
                {
                    selectedPath = path;
                    continue;
                }
                bool pathIsTrack = TrackIntersectionMeshGenerator.IsTrackType(path.path_type);
                bool selectedIsTrack = TrackIntersectionMeshGenerator.IsTrackType(selectedPath.path_type);
                if (selectedIsTrack && !pathIsTrack)
                {
                    selectedPath = path;
                }
                else if (pathIsTrack == selectedIsTrack && path.path_variant > selectedPath.path_variant)
                {
                    selectedPath = path;
                }
            }

            if (selectedPath != null)
            {
                GameObject prefab = ModelManager.Instance.GetModel($"Models/Paths/p{selectedPath.path_variant}/body")
                    ?? ModelManager.Instance.GetModel($"Models/Paths/p{selectedPath.path_type}/body");
                MeshRenderer renderer = prefab != null ? prefab.GetComponent<MeshRenderer>() : null;
                if (renderer != null && renderer.sharedMaterial != null)
                {
                    Material bodyMaterial = RoadPath.GetOrCreateRoadMaterial(renderer.sharedMaterial);
                    if (bodyMaterial != null)
                    {
                        return bodyMaterial;
                    }
                }
            }
        }

        Material material = Resources.Load<Material>("PATH_ASPHALT");
        if (material == null)
        {
            material = Resources.Load<Material>("NodeMaterial");
        }
        return material;
    }

    private static Mesh ConvertGeometryToMesh(Geometry geometry, Vector3 center, bool horizontalPlane, string meshName)
    {
        if (geometry == null || geometry.IsEmpty)
        {
            return null;
        }

        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();

        AddGeometryToMesh(geometry, center, horizontalPlane, vertices, triangles);

        if (vertices.Count < 3 || triangles.Count < 3)
        {
            return null;
        }

        Mesh mesh = new Mesh();
        mesh.name = meshName;
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        return mesh;
    }

    private static void AddGeometryToMesh(Geometry geometry, Vector3 center, bool horizontalPlane, List<Vector3> vertices, List<int> triangles)
    {
        if (geometry is Polygon polygon)
        {
            AddPolygonToMesh(polygon, center, horizontalPlane, vertices, triangles);
            return;
        }

        if (geometry is MultiPolygon || geometry is GeometryCollection)
        {
            for (int i = 0; i < geometry.NumGeometries; i++)
            {
                AddGeometryToMesh(geometry.GetGeometryN(i), center, horizontalPlane, vertices, triangles);
            }
        }
    }

    private static void AddPolygonToMesh(Polygon polygon, Vector3 center, bool horizontalPlane, List<Vector3> vertices, List<int> triangles)
    {
        Coordinate[] coords = polygon.ExteriorRing.Coordinates;
        if (coords == null || coords.Length < 4)
        {
            return;
        }

        Vector2[] points = new Vector2[coords.Length - 1];
        for (int i = 0; i < coords.Length - 1; i++)
        {
            points[i] = new Vector2((float)coords[i].X, (float)coords[i].Y);
        }

        if (SignedArea(points) < 0f)
        {
            Array.Reverse(points);
        }

        int baseIndex = vertices.Count;
        for (int i = 0; i < points.Length; i++)
        {
            // アスファルト面は基準面 + BODY_ABOVE_SPLINE で、通常道路の路面上面と同一平面になる。
            // 道路は切断点で交差点の縁まで切り詰められているため、領域の重なりによる
            // Zファイティングは起きない。
            float worldY = SampleBaseSurfaceY(points[i].x, points[i].y, center, horizontalPlane) + RoadPartHeights.BODY_ABOVE_SPLINE;
            vertices.Add(new Vector3(points[i].x - center.x, worldY - center.y, points[i].y - center.z));
        }

        List<int> polygonTriangles = TriangulatePolygon(points);
        if (polygonTriangles.Count == 0)
        {
            polygonTriangles = GenerateTriangleFanIndices(points.Length);
        }

        // Unity の XZ 平面では、2D 上で反時計回りの三角形をそのまま置くと法線が下向きになる。
        // 描画面を上向きにするため、三角形単位で巻き順を反転してから登録する。
        for (int i = 0; i < polygonTriangles.Count; i += 3)
        {
            triangles.Add(baseIndex + polygonTriangles[i]);
            triangles.Add(baseIndex + polygonTriangles[i + 2]);
            triangles.Add(baseIndex + polygonTriangles[i + 1]);
        }
    }

    private static List<int> TriangulatePolygon(Vector2[] polygon)
    {
        List<int> triangles = new List<int>();
        if (polygon == null || polygon.Length < 3)
        {
            return triangles;
        }

        List<int> vertexIndices = new List<int>(polygon.Length);
        for (int i = 0; i < polygon.Length; i++)
        {
            vertexIndices.Add(i);
        }

        int iterationLimit = polygon.Length * polygon.Length;
        int iterations = 0;
        while (vertexIndices.Count > 3 && iterations < iterationLimit)
        {
            bool earFound = false;
            for (int i = 0; i < vertexIndices.Count; i++)
            {
                int prev = vertexIndices[(i - 1 + vertexIndices.Count) % vertexIndices.Count];
                int curr = vertexIndices[i];
                int next = vertexIndices[(i + 1) % vertexIndices.Count];

                if (Cross(polygon[prev], polygon[curr], polygon[next]) <= TriangulationEpsilon)
                {
                    continue;
                }

                bool containsVertex = false;
                for (int j = 0; j < vertexIndices.Count; j++)
                {
                    int candidate = vertexIndices[j];
                    if (candidate == prev || candidate == curr || candidate == next)
                    {
                        continue;
                    }
                    if (IsPointInTriangle(polygon[candidate], polygon[prev], polygon[curr], polygon[next]))
                    {
                        containsVertex = true;
                        break;
                    }
                }

                if (containsVertex)
                {
                    continue;
                }

                triangles.Add(prev);
                triangles.Add(curr);
                triangles.Add(next);
                vertexIndices.RemoveAt(i);
                earFound = true;
                break;
            }

            if (!earFound)
            {
                return new List<int>();
            }
            iterations++;
        }

        if (vertexIndices.Count == 3)
        {
            triangles.Add(vertexIndices[0]);
            triangles.Add(vertexIndices[1]);
            triangles.Add(vertexIndices[2]);
        }
        return triangles;
    }

    private static List<int> GenerateTriangleFanIndices(int vertexCount)
    {
        List<int> triangles = new List<int>();
        for (int i = 1; i < vertexCount - 1; i++)
        {
            triangles.Add(0);
            triangles.Add(i);
            triangles.Add(i + 1);
        }
        return triangles;
    }

    private static float SignedArea(Vector2[] polygon)
    {
        float area = 0f;
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector2 a = polygon[i];
            Vector2 b = polygon[(i + 1) % polygon.Length];
            area += a.x * b.y - b.x * a.y;
        }
        return area * 0.5f;
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 ab = b - a;
        Vector2 ac = c - a;
        return ab.x * ac.y - ab.y * ac.x;
    }

    private static bool IsPointInTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
    {
        float cross1 = Cross(a, b, point);
        float cross2 = Cross(b, c, point);
        float cross3 = Cross(c, a, point);
        bool hasNegative = cross1 < -TriangulationEpsilon || cross2 < -TriangulationEpsilon || cross3 < -TriangulationEpsilon;
        bool hasPositive = cross1 > TriangulationEpsilon || cross2 > TriangulationEpsilon || cross3 > TriangulationEpsilon;
        return !(hasNegative && hasPositive);
    }
}
