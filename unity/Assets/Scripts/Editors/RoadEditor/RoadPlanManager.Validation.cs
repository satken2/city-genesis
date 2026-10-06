using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;
using ElevationSegment = RoadShapeGeometry.ElevationSegment;

public partial class RoadPlanManager
{
    private List<ElevationSegment> cachedEditingElevationSegments;
    private List<(int segIndex, PathSegment otherSeg)> cachedElevationViolations;
    // 編集中パスが地表レベルで横切っている川と、その違反サンプル点(絶対座標)のローカル判定キャッシュ。
    private List<(RiverRecord river, List<Vector3> points)> cachedRiverViolations;
    // 編集中パスが地表レベルで海・湖の上を通っている区間(中心線上の路面の小区間。絶対座標)のローカル判定キャッシュ。
    private List<(Vector3 a, Vector3 b)> cachedSeaLakeViolations;
    // 川・海・湖の横断判定でパス中心線を標本化する間隔(m)。バックエンドは1m間隔だが、ドラッグ中の
    // リアルタイム判定なので粗くし、川幅(最小でも十数m)に対して取りこぼしが無い程度に留める。
    private const float RiverCrossingSampleStep = 2f;
    // 川の占有箱を出す範囲: 違反サンプル点からこの半径(m)以内のセルだけを赤く示す。
    private const float RiverOccupancyBoxRadius = 40f;
    private List<Vector3> cachedCollisionVolume;
    private OccupancyVolumeUtil.BuildingVolumes cachedBuildingVolume;
    // 敷設中パスと干渉する建物のフットプリント（ワールドXZポリゴン）のローカル推定キャッシュ。
    private List<List<Vector3>> cachedBuildingInterference;

    // 既存パスの立体交差判定用セグメントキャッシュ。
    // 編集中は既存パスが変化しないため、一度構築したセグメントを再利用する。
    private Dictionary<int, List<ElevationSegment>> existingElevSegCache = new Dictionary<int, List<ElevationSegment>>();

    // 建物干渉のコリドーBBOX絞り込みに使う追加マージン。
    private const float BuildingInterferenceMargin = 2f;

    // =====================================================================
    // 立体交差バリデーション
    // =====================================================================

    /// <summary>
    /// path_variant に対応するマスターから height（上空占有高さ）を取得する。
    /// マスターに定義がない場合は 0.0m とする（地面に張り付く道路）。
    /// </summary>
    private float GetPathHeightFromMaster(int variant)
    {
        if (MasterDataStore.Instance == null) return 0f;
        try
        {
            var master = MasterDataStore.Instance.GetPathMaster(variant);
            if (master != null && master.height.HasValue)
                return master.height.Value;
        }
        catch
        {
            // マスター未定義の場合は無視
        }
        return 0f;
    }

    /// <summary>
    /// 現在の PathSubtype の許容最小高さ(m)をマスターデータから取得する。
    /// 負の値を持つバリアント（地下道路・地下鉄用線路など）だけが地下（相対Y&lt;0）に
    /// 建設できる。マスター未定義時は 0（地下禁止）。バックエンドの
    /// _validate_path_height_range と同じ制約のエディタ側ガード。
    /// </summary>
    private float GetMinHeightFromMaster()
    {
        if (MasterDataStore.Instance == null) return 0f;
        try
        {
            var master = MasterDataStore.Instance.GetPathMaster(PathSubtype);
            if (master != null && master.min_height.HasValue)
                return master.min_height.Value;
        }
        catch
        {
            // マスター未定義の場合は 0 とする
        }
        return 0f;
    }

    /// <summary>
    /// 現在の PathSubtype が標高編集可能（height_editable=true）かどうかを
    /// マスターデータから判定する。マスターデータ未取得時は false とする。
    /// 標高編集不可のバリアント（街路など）では標高の設定・変更を禁止するために使用する。
    /// </summary>
    public bool IsHeightEditable()
    {
        if (MasterDataStore.Instance == null) return false;
        try
        {
            var master = MasterDataStore.Instance.GetPathMaster(PathSubtype);
            return master != null && master.height_editable.HasValue && master.height_editable.Value;
        }
        catch
        {
            // マスター未定義の場合は編集不可とする
        }
        return false;
    }

    /// <summary>
    /// 編集中パスの点群をベジエサンプリングして、立体交差判定用の
    /// ElevationSegment リストを生成する。
    /// 描画と同じ区間・制御点を使い、表示用の浮かせ量を除いて判定する。
    /// </summary>
    private List<ElevationSegment> BuildEditingElevationSegments()
    {
        if (cachedEditingElevationSegments != null) return cachedEditingElevationSegments;
        var spans = new List<RoadShapeGeometry.Span>();
        bool placing = phase == RoadEditorPhase.Placing && points.Count > 0;
        for (int i = 0; i < points.Count - 1 + (placing ? 1 : 0); i++)
            spans.Add(GetShapeSpan(i, placing));
        return cachedEditingElevationSegments = RoadShapeGeometry.SampleElevationSegments(spans, bezierSamplesPerSegment + 1);
    }

    /// <summary>
    /// 既存パスの形状データから、立体交差判定用の ElevationSegment リストを生成する。
    /// 保存座標の制御点は所属アンカー直下の地盤高を基準に絶対座標へ戻す。
    /// </summary>
    private List<ElevationSegment> BuildExistingElevationSegments(PathSummary path)
    {
        List<ElevationSegment> segments = new List<ElevationSegment>();
        var startList = GeneratedModelUtils.ToFloatList(path.start_point);
        var endList = GeneratedModelUtils.ToFloatList(path.end_point);
        if (startList == null || endList == null || startList.Count < 2 || endList.Count < 2) return segments;

        Vector3 sp = new Vector3(startList[0], startList.Count > 2 ? startList[1] : 0f, startList[startList.Count - 1]);
        Vector3 ep = new Vector3(endList[0], endList.Count > 2 ? endList[1] : 0f, endList[endList.Count - 1]);

        var waypoints = GeneratedModelUtils.ToWaypointList(path.waypoints) ?? new List<PathWaypoint>();

        List<Vector3> relPoints = new List<Vector3>();
        List<Vector3?> relControlOut = new List<Vector3?>();
        List<Vector3?> relControlIn = new List<Vector3?>();

        relPoints.Add(sp);
        var sc = GeneratedModelUtils.ToFloatList(path.start_control);
        relControlOut.Add(sc != null && sc.Count >= 3 ? new Vector3(sc[0], sc[1], sc[2]) : (Vector3?)null);
        relControlIn.Add(null);

        foreach (var wp in waypoints)
        {
            if (wp.position == null || wp.position.Length < 2) continue;
            Vector3 pos = new Vector3(wp.position[0], wp.position.Length > 2 ? wp.position[1] : 0f, wp.position[wp.position.Length - 1]);
            relPoints.Add(pos);

            if (wp.control_in != null && wp.control_in.Length >= 2)
                relControlIn.Add(new Vector3(wp.control_in[0], wp.control_in.Length > 2 ? wp.control_in[1] : 0f, wp.control_in[wp.control_in.Length - 1]));
            else
                relControlIn.Add(null);

            if (wp.control_out != null && wp.control_out.Length >= 2)
                relControlOut.Add(new Vector3(wp.control_out[0], wp.control_out.Length > 2 ? wp.control_out[1] : 0f, wp.control_out[wp.control_out.Length - 1]));
            else
                relControlOut.Add(null);
        }

        relPoints.Add(ep);
        var ec = GeneratedModelUtils.ToFloatList(path.end_control);
        relControlIn.Add(ec != null && ec.Count >= 3 ? new Vector3(ec[0], ec[1], ec[2]) : (Vector3?)null);
        relControlOut.Add(null);

        var result = SampleElevationSegments(relPoints, relControlOut, relControlIn, path.curved, PathManager.GetPathFollowTerrain(path), PathManager.GetPathBaseHeight(path.path_variant));
        if (path.station_grade_profile != null && path.station_grade_profile.Count >= 2)
        {
            for (int i = 0; i < result.Count; i++)
            {
                var seg = result[i];
                seg.absYA = Utils.GetGroundHeight(seg.a) + path.GetRelativeHeightAtPoint(seg.a.x, seg.a.z);
                seg.absYB = Utils.GetGroundHeight(seg.b) + path.GetRelativeHeightAtPoint(seg.b.x, seg.b.z);
                seg.follows = false;
                result[i] = seg;
            }
        }
        return result;
    }

    // 非追従区間の絶対標高はアンカー基準で補間する。途中の地形で橋・トンネルを曲げない。
    private List<ElevationSegment> SampleElevationSegments(List<Vector3> anchors,
        List<Vector3?> controlsOut, List<Vector3?> controlsIn, bool curved, bool followsTerrain, float groundRelativeHeight)
    {
        var spans = new List<RoadShapeGeometry.Span>();
        PathBuildPoint RestorePoint(int index)
        {
            Vector3 position = anchors[index];
            position.y += Utils.GetGroundHeight(position);
            var point = new PathBuildPoint(position, PathPointRole.Waypoint);
            if (controlsOut[index].HasValue)
            {
                var c = controlsOut[index].Value;
                point.control_out = RestoreControl(c.x, c.y, c.z, position);
            }
            if (controlsIn[index].HasValue)
            {
                var c = controlsIn[index].Value;
                point.control_in = RestoreControl(c.x, c.y, c.z, position);
            }
            return point;
        }
        for (int i = 0; i < anchors.Count - 1; i++)
            spans.Add(RoadShapeGeometry.CreateSpan(RestorePoint(i), RestorePoint(i + 1), curved, followsTerrain, groundRelativeHeight));
        return RoadShapeGeometry.SampleElevationSegments(spans, bezierSamplesPerSegment + 1);
    }

    /// <summary>
    /// 2つの線分（XZ平面投影）が交差するかを判定し、交差地点の
    /// 両線分上のパラメータ t を out で返す。
    /// 交差しない場合は false を返す。
    /// </summary>
    private static bool SegmentsIntersectXZ(Vector3 a1, Vector3 a2, Vector3 b1, Vector3 b2, out float tA, out float tB)
    {
        return RoadShapeGeometry.SegmentsIntersectXZ(a1, a2, b1, b2, out tA, out tB);
    }

    /// <summary>
    /// セグメント列に非追従スパン（高さ編集されたスパン、または follow_terrain=false
    /// バリアントのスパン）が含まれるかを返す。ElevationSegment.follows は
    /// バリアントフラグと relY≈0 の両方を織り込み済みのため、その否定を見るだけでよい。
    /// バックエンド geo_path_depth.path_has_non_follow_span と同義。
    /// </summary>
    private static bool HasNonFollowSegment(List<ElevationSegment> segments)
    {
        foreach (var seg in segments)
        {
            if (!seg.follows) return true;
        }
        return false;
    }

    /// <summary>
    /// 編集中パスと既存パスセグメントの間で立体交差（高さ方向の衝突）を検出する。
    ///
    /// 判定ルール:
    /// - XZ平面で2つのセグメントが交差する場合、交差地点での両パスの
    ///   高さ範囲 [Y, Y + height] が重なるかをチェックする。
    /// - ただし、交差地点における両パスの高さの差が許容誤差以下の場合は
    ///   「同じ高さの交差点・踏切」として許可する（相対比較1cm／絶対比較0.5m）。
    /// - 追従×追従のペアは地形高さを無視し、すべてのパスは仮想的にフラットな
    ///   平面上にあるとみなして相対高さで判定する。
    /// - 非追従スパン（高さ編集されたスパン・follow_terrain=false の橋バリアント）が
    ///   絡むペアは、バックエンドの validate_path_elevation_intersections と同様に
    ///   絶対標高（地形標高＋相対高さ）で判定する。丘の下を通るトンネルと丘の上の
    ///   地表道路、掘割を跨ぐ橋と掘割底の道路を正しく立体交差として許可するため。
    ///   地形追従セグメント（follows）は交差点位置の地形標高を使う。
    /// - 自分自身（既存パス編集中の場合）や新規パスの自己交差も判定の対象とする。
    /// </summary>
    private List<(int segIndex, PathSegment otherSeg)> CheckElevationIntersections()
    {
        if (cachedElevationViolations != null)
            return cachedElevationViolations;

        var violations = new List<(int segIndex, PathSegment otherSeg)>();
        if (pathManager == null || points.Count < 2 || PathType == Constants.PATH_STATION_HOUSE)
        {
            cachedElevationViolations = violations;
            return cachedElevationViolations;
        }

        float myHeight = GetPathHeightFromMaster(PathSubtype);
        List<ElevationSegment> mySegments = BuildEditingElevationSegments();
        if (mySegments.Count == 0)
        {
            cachedElevationViolations = violations;
            return cachedElevationViolations;
        }

        // 編集中パスが非追従スパン（高さ編集されたスパン、または follow_terrain=false
        // バリアントのスパン）を持つか。非追従スパンの補間relYは交点の物理量に
        // ならないため、絡むペアは絶対標高で判定する（バックエンドの
        // path_has_non_follow_span と同一の判定）。
        bool editingNonFollow = HasNonFollowSegment(mySegments);

        var freezeProbe = System.Diagnostics.Stopwatch.StartNew(); // [フリーズ調査] 調査後に削除
        int freezeProbeBuilt = 0; // [フリーズ調査] 調査後に削除
        HashSet<int> candidatePathIds = new HashSet<int>();
        for (int i = 0; i < mySegments.Count; i++)
        {
            var seg = mySegments[i];
            Vector3 mid = (seg.a + seg.b) * 0.5f;
            float radius = Vector3.Distance(seg.a, seg.b) * 0.5f + 5.0f;

            var nearby = pathManager.QueryNearbySegments(new Vector3(mid.x, 0, mid.z), radius);
            if (nearby != null)
            {
                foreach (var other in nearby)
                {
                    if (other.path != null)
                        candidatePathIds.Add(other.path.path_id);
                }
            }
        }

        // --- 既存パスとの交差判定 ---
        foreach (int otherPathId in candidatePathIds)
        {
            if (otherPathId == pathId) continue;

            PathSummary exPath = pathManager.GetPathData(otherPathId);
            if (exPath == null || exPath.path_type == Constants.PATH_STATION_HOUSE) continue;

            // 既存パスのセグメントは編集中に変化しないためキャッシュする
            if (!existingElevSegCache.TryGetValue(otherPathId, out List<ElevationSegment> exSegments))
            {
                exSegments = BuildExistingElevationSegments(exPath);
                existingElevSegCache[otherPathId] = exSegments;
                freezeProbeBuilt++; // [フリーズ調査] 調査後に削除
            }

            float otherHeight = GetPathHeightFromMaster(exPath.path_variant);

            if (myHeight <= 0f && otherHeight <= 0f) continue;

            // 非追従スパンが絡むペアは絶対標高（地形標高＋相対高さ）で比較する
            bool useAbsolute = editingNonFollow || HasNonFollowSegment(exSegments);

            for (int i = 0; i < mySegments.Count; i++)
            {
                for (int j = 0; j < exSegments.Count; j++)
                {
                    var mySeg = mySegments[i];
                    var exSeg = exSegments[j];

                    if (RoadShapeGeometry.ElevationOverlaps(mySeg, myHeight, exSeg, otherHeight, useAbsolute))
                    {
                        // 描画用に絶対座標に変換したダミーのPathSegmentを作成
                        Vector3 aAbs = new Vector3(exSeg.a.x, exSeg.absYA, exSeg.a.z);
                        Vector3 bAbs = new Vector3(exSeg.b.x, exSeg.absYB, exSeg.b.z);
                        PathSegment dummyOther = new PathSegment { a = aAbs, b = bAbs, path = exPath };
                        violations.Add((i, dummyOther));
                    }
                }
            }
        }

        // --- 自己交差判定 ---
        // 非追従スパンを持つパスは絶対標高（地形標高＋相対高さ）で判定する
        for (int i = 0; i < mySegments.Count; i++)
        {
            for (int j = i + 2; j < mySegments.Count; j++) // 隣接セグメントは無視
            {
                var mySeg1 = mySegments[i];
                var mySeg2 = mySegments[j];

                if (RoadShapeGeometry.ElevationOverlaps(mySeg1, myHeight, mySeg2, myHeight, editingNonFollow))
                {
                    Vector3 aAbs = new Vector3(mySeg2.a.x, mySeg2.absYA, mySeg2.a.z);
                    Vector3 bAbs = new Vector3(mySeg2.b.x, mySeg2.absYB, mySeg2.b.z);
                    PathSegment dummyOther = new PathSegment { a = aAbs, b = bAbs };
                    violations.Add((i, dummyOther));
                }
            }
        }

        // [フリーズ調査] 既存パスの形状を新しく作ったフレームだけ出す(毎フレーム出さない)。調査後に削除
        if (freezeProbeBuilt > 0) Debug.Log($"[フリーズ調査] 立体交差チェック {freezeProbe.Elapsed.TotalMilliseconds:F1}ms (候補パス{candidatePathIds.Count}件・形状を新規作成{freezeProbeBuilt}件)");

        cachedElevationViolations = violations;
        return cachedElevationViolations;
    }

    /// <summary>
    /// 衝突相手のパス区間の幅と占有高さ、地表レベルで横切っている川の範囲(横断付近)、
    /// および地表レベルで海・湖の上を通っている道路区間を半透明の占有筒・箱で示す。
    /// パス同士の衝突は相手側の区間だけに被せ、敷設中のプレビューメッシュを覆わない
    /// (自己交差では後ろ側の区間が相手になる)。
    /// </summary>
    private void DrawElevationIntersections(Camera cam)
    {
        if (overlayRenderer == null) return;

        if (cachedCollisionVolume == null)
        {
            cachedCollisionVolume = new List<Vector3>();
            var drawn = new HashSet<(Vector3, Vector3)>();
            foreach (var (_, other) in CheckElevationIntersections())
            {
                if (!drawn.Add((other.a, other.b))) continue;
                int variant = other.path != null ? other.path.path_variant : PathSubtype;
                OccupancyVolumeUtil.AddTube(cachedCollisionVolume, new List<Vector3> { other.a, other.b },
                    pathManager.GetRoadWidth(variant), GetPathHeightFromMaster(variant));
            }
            // 地表レベルで横切っている川は、横断付近の範囲を同じ警告色の占有箱で示す
            foreach (var (river, violationPoints) in CheckRiverCrossings())
            {
                RiverStore.AddOccupancyBox(cachedCollisionVolume, river, violationPoints, RiverOccupancyBoxRadius);
            }
            // 海・湖は川と違って範囲の格子を持たないため、水の上を通っている道路区間そのものを占有筒で示す
            foreach (var (a, b) in CheckSeaLakeCrossings())
            {
                OccupancyVolumeUtil.AddTube(cachedCollisionVolume, new List<Vector3> { a, b },
                    pathManager.GetRoadWidth(PathSubtype), GetPathHeightFromMaster(PathSubtype));
            }
        }
        overlayRenderer.AddPreTriangulated(cachedCollisionVolume, OccupancyVolumeUtil.WarningColor);
    }

    /// <summary>
    /// 編集中パスが川の範囲(RiverStore)を地表レベルで横切っていないかをローカル判定する。
    ///
    /// バックエンド geo_rivers.validate_path_river_crossings と同じ規則:
    /// 川の範囲内の各サンプル点で「路面絶対標高 ≥ 水面標高 + WATER_BRIDGE_MIN_CLEARANCE(橋)」
    /// または「地形標高 − 路面標高 > バリアントのクリアランス高さ(完全埋設トンネル)」の
    /// いずれかを要求する。地形追従スパンは路面＝川床(水面より下)なので必ず違反になる。
    /// インフラパス(電線・導水管・パイプライン)は地表を占有しないため対象外。
    ///
    /// 標本化は BuildEditingElevationSegments の各セグメント(直線スパンはスパン全体、曲線は
    /// ベジエ分割片)を RiverCrossingSampleStep 間隔で分割し、追従セグメントは地形高さ、
    /// 非追従セグメントは両端の絶対標高の線形補間を路面標高とする。
    /// 戻り値は違反した川ごとの違反サンプル点(絶対座標。占有箱の範囲指定に使う)。
    /// </summary>
    private List<(RiverRecord river, List<Vector3> points)> CheckRiverCrossings()
    {
        if (cachedRiverViolations != null)
            return cachedRiverViolations;

        var violations = new List<(RiverRecord river, List<Vector3> points)>();
        if (!RiverStore.HasRivers || points.Count < 2 || InfraNetworkUtil.IsInfraPathType(PathType))
        {
            cachedRiverViolations = violations;
            return cachedRiverViolations;
        }

        float clearance = GetPathHeightFromMaster(PathSubtype);
        var byRiver = new Dictionary<RiverRecord, List<Vector3>>();
        foreach (var seg in BuildEditingElevationSegments())
        {
            float dx = seg.b.x - seg.a.x;
            float dz = seg.b.z - seg.a.z;
            float length = Mathf.Sqrt(dx * dx + dz * dz);
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / RiverCrossingSampleStep));
            for (int k = 0; k <= steps; k++)
            {
                float t = (float)k / steps;
                float x = seg.a.x + dx * t;
                float z = seg.a.z + dz * t;
                var river = RiverStore.FindAt(x, z);
                if (river == null) continue;
                float terrainY = Utils.GetGroundHeight(new Vector3(x, 0f, z));
                float roadY = seg.HeightAt(t, true, terrainY);
                if (terrainY - roadY > clearance) continue; // 完全埋設トンネル
                float waterY = river.WaterLevelAt(x, z);
                if (roadY >= waterY + Constants.WATER_BRIDGE_MIN_CLEARANCE) continue; // 橋
                if (!byRiver.TryGetValue(river, out var list))
                {
                    list = new List<Vector3>();
                    byRiver[river] = list;
                }
                list.Add(new Vector3(x, roadY, z));
            }
        }
        foreach (var kv in byRiver) violations.Add((kv.Key, kv.Value));

        cachedRiverViolations = violations;
        return cachedRiverViolations;
    }

    /// <summary>
    /// 編集中パスが海・湖の上を地表レベルで通っていないかをローカル判定する。
    ///
    /// バックエンド geo_sea_lake.validate_path_sea_lake_crossings と同じ規則:
    /// 水の上の各サンプル点で「路面絶対標高 ≥ 水面標高 + WATER_BRIDGE_MIN_CLEARANCE(橋)」または
    /// 「地形標高 − 路面標高 > バリアントのクリアランス高さ(完全埋設トンネル)」のいずれかを要求する。
    /// 水の上とは、都市範囲内で陸地(CoastlineStore)の外にあり、地形が水面より低い点。水面標高は
    /// 湖の footprint(WaterSurfaceStore)内ならその水位、それ以外は海面の0。海岸線は頂点を間引いた形で
    /// 水際から数メートルずれるため、地形の条件が無いと乾いた岸を通る道路まで違反になる。
    /// 川の範囲は CheckRiverCrossings が扱う。インフラパスと海上敷設を許すバリアント(over_water_allowed)は対象外。
    ///
    /// 標本化は CheckRiverCrossings と同じ。戻り値は違反サンプル点を中心とする標本間隔ぶんの路面の小区間
    /// (絶対座標)で、占有箱の描画に使う。
    /// </summary>
    private List<(Vector3 a, Vector3 b)> CheckSeaLakeCrossings()
    {
        if (cachedSeaLakeViolations != null)
            return cachedSeaLakeViolations;

        var violations = new List<(Vector3 a, Vector3 b)>();
        cachedSeaLakeViolations = violations;
        if (!CoastlineStore.HasCoastline || points.Count < 2 || InfraNetworkUtil.IsInfraPathType(PathType))
            return violations;
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(PathSubtype) : null;
        if (master != null && master.over_water_allowed)
            return violations;
        if (terrainManager == null) terrainManager = FindAnyObjectByType<TerrainManager>();
        // 都市範囲が分からないうちは海と都市外を区別できないので判定しない(権威はバックエンド)
        if (terrainManager == null || !terrainManager.HasTerrainSettings)
            return violations;
        float halfW = terrainManager.GetMapWidth() * 0.5f;
        float halfH = terrainManager.GetMapHeight() * 0.5f;

        Vector3 RoadPoint(ElevationSegment seg, float t)
        {
            float x = Mathf.Lerp(seg.a.x, seg.b.x, t);
            float z = Mathf.Lerp(seg.a.z, seg.b.z, t);
            float y = seg.HeightAt(t, true, Utils.GetGroundHeight(new Vector3(x, 0f, z)));
            return new Vector3(x, y, z);
        }

        float clearance = GetPathHeightFromMaster(PathSubtype);
        foreach (var seg in BuildEditingElevationSegments())
        {
            float dx = seg.b.x - seg.a.x;
            float dz = seg.b.z - seg.a.z;
            float length = Mathf.Sqrt(dx * dx + dz * dz);
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / RiverCrossingSampleStep));
            for (int k = 0; k <= steps; k++)
            {
                float t = (float)k / steps;
                float x = seg.a.x + dx * t;
                float z = seg.a.z + dz * t;
                if (Mathf.Abs(x) > halfW || Mathf.Abs(z) > halfH) continue;
                var lake = WaterSurfaceStore.FindAt(x, z);
                float waterY = lake != null ? lake.Level : 0f;
                float terrainY = Utils.GetGroundHeight(new Vector3(x, 0f, z));
                if (terrainY >= waterY) continue;
                if (RiverStore.FindAt(x, z) != null || CoastlineStore.IsLand(x, z)) continue;
                float roadY = seg.HeightAt(t, true, terrainY);
                if (terrainY - roadY > clearance) continue; // 完全埋設トンネル
                if (roadY >= waterY + Constants.WATER_BRIDGE_MIN_CLEARANCE) continue; // 橋
                float half = 0.5f / steps;
                violations.Add((RoadPoint(seg, Mathf.Max(0f, t - half)), RoadPoint(seg, Mathf.Min(1f, t + half))));
            }
        }
        return violations;
    }

    /// <summary>
    /// 敷設中パスの敷地コリドー(中心線をパス幅で膨らませた帯)と重なる建物を
    /// フロント側でローカル推定し、そのフットプリントを返す。
    ///
    /// バックエンドへ問い合わせず BuildingManager の空間インデックス + 建物フットプリント
    /// (BuildingSummary.polygonArray は center_point 相対のローカルXZ座標。
    /// center_point を加算してワールドXZへ変換してから判定する)で幾何判定するため、
    /// ドラッグ追従でリアルタイムに使える。区画の所有と収用可否は判別せず、幾何的な重なりの
    /// 推定にとどまる(権威判定は確定時のバックエンド問い合わせ)。編集中はキャッシュする。
    /// </summary>
    private List<List<Vector3>> CheckBuildingInterference()
    {
        if (cachedBuildingInterference != null)
            return cachedBuildingInterference;

        var result = new List<List<Vector3>>();
        cachedBuildingInterference = result;
        cachedBuildingVolume = new OccupancyVolumeUtil.BuildingVolumes();

        if (BuildingManager.Instance == null || pathManager == null) return result;

        if (points.Count == 0) return result;
        var centerline = SamplePreviewSpans(GetRenderPoints(), 0, points.Count - 1,
            phase == RoadEditorPhase.Placing, false);
        if (centerline.Count < 2) return result;

        float halfWidth = pathManager.GetRoadWidth(PathSubtype) * 0.5f;
        if (halfWidth <= 0f) return result;

        // コリドーのBBOX(中心線のXZ範囲をhalfWidth+マージンで膨らませる)で候補を絞る。
        float minX = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var p in centerline)
        {
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.z < minZ) minZ = p.z;
            if (p.z > maxZ) maxZ = p.z;
        }
        float margin = halfWidth + BuildingInterferenceMargin;
        var candidates = BuildingManager.Instance.QueryBuildingIdsInBounds(
            minX - margin, minZ - margin, maxX + margin, maxZ + margin);

        // インフラを流せるバリアント(送電線・導水管・電化線路など)の敷設中は、同一網の
        // インフラ需給施設を干渉対象から除外する。接続端子ノードは施設の立地区画内に
        // あるため、端子への引き込みでパスが施設建物と重なるのは正常な配線形状であり、
        // 警告する意味がない。バックエンド権威判定(compute_path_interference の
        // building_ids)も同じ基準で除外するため、ローカル推定と権威ハイライトが揃う。
        bool drawingInfraCarrier = InfraNetworkUtil.IsInfraPathType(PathType)
            || InfraNetworkUtil.GetPathInfraProducts(PathSubtype) != null;

        foreach (int buildingId in candidates)
        {
            var summary = BuildingManager.Instance.GetBuildingSummary(buildingId);
            float[][] poly = summary?.polygonArray;
            if (poly == null || poly.Length < 3) continue;

            // 同一網のインフラ施設かどうかは、施設の接続端子ノードの網ビットと敷設中
            // バリアントの網互換(IsNodeSnappable と同じビット積)で判定する。
            // 端子ノードが未ロードの場合は除外しない(安全側=従来通りハイライト表示)。
            if (drawingInfraCarrier && summary.infra_node_id > 0)
            {
                var terminalNode = pathManager.GetNodeData(summary.infra_node_id);
                if (terminalNode != null
                    && InfraNetworkUtil.IsNodeSnappable(terminalNode, PathSubtype, pathManager))
                {
                    continue;
                }
            }

            // 公共施設の敷地は市有地で、インフラパスは区画を残したまま通れる(コアの
            // classify_parcel_overlaps と同じ免除)。取り壊されない建物なので干渉として示さない。
            if (summary.public_facility && InfraNetworkUtil.IsInfraPathType(PathType)) continue;

            // polygonArray は center_point 相対(ミニマップ・メッシュ生成と同じ規約)のため、
            // center_point を加算してワールドXZへ変換する。center_point 欠損時は位置を
            // 確定できないので判定対象から外す(相対座標のまま扱うと地図中心に誤検出が出る)。
            float[] cp = summary.centerPointArray;
            if (cp == null || cp.Length < 2) continue;

            var footprint = new List<Vector2>(poly.Length);
            foreach (var v in poly)
            {
                if (v != null && v.Length >= 2) footprint.Add(new Vector2(v[0] + cp[0], v[1] + cp[1]));
            }
            if (footprint.Count < 3) continue;

            float buildingBottom = Utils.GetGroundHeight(new Vector3(cp[0], 0f, cp[1]));
            float buildingTop = buildingBottom + (summary.height ?? 0f);
            bool overlaps = false;
            for (int i = 1; i < centerline.Count && !overlaps; i++)
            {
                var a = centerline[i - 1];
                var b = centerline[i];
                if (Mathf.Min(a.y, b.y) >= buildingTop || Mathf.Max(a.y, b.y) + GetPathHeightFromMaster(PathSubtype) <= buildingBottom) continue;
                overlaps = CorridorOverlapsPolygon(new List<Vector3> { a, b }, halfWidth, footprint);
            }
            if (overlaps)
            {
                var worldPoly = new List<Vector3>(footprint.Count);
                foreach (var f in footprint)
                {
                    worldPoly.Add(new Vector3(f.x, 0f, f.y));
                }
                result.Add(worldPoly);
                cachedBuildingVolume.Add(summary);
            }
        }

        return result;
    }

    /// <summary>
    /// 中心線をhalfWidthで膨らませたコリドーが、指定フットプリント(XZポリゴン)と重なるか。
    /// (a)フットプリント頂点が中心線から halfWidth 以内、(b)中心線頂点がフットプリント内、
    /// (c)中心線セグメントとフットプリント辺が交差、のいずれかで重なりとみなす近似。
    /// </summary>
    private static bool CorridorOverlapsPolygon(List<Vector3> centerline, float halfWidth, List<Vector2> footprint)
    {
        float halfWidthSq = halfWidth * halfWidth;

        // (a) フットプリント頂点が中心線コリドー内(いずれかのセグメントから halfWidth 以内)
        for (int fi = 0; fi < footprint.Count; fi++)
        {
            Vector2 fp = footprint[fi];
            for (int ci = 0; ci < centerline.Count - 1; ci++)
            {
                Vector2 a = new Vector2(centerline[ci].x, centerline[ci].z);
                Vector2 b = new Vector2(centerline[ci + 1].x, centerline[ci + 1].z);
                if (PolylineUtils.DistanceToSegmentSq(fp, a, b) <= halfWidthSq) return true;
            }
        }

        // (b) 中心線頂点がフットプリント内部
        for (int ci = 0; ci < centerline.Count; ci++)
        {
            if (PolygonUtils.Contains(footprint, new Vector2(centerline[ci].x, centerline[ci].z))) return true;
        }

        // (c) 中心線セグメントとフットプリント辺の交差
        for (int ci = 0; ci < centerline.Count - 1; ci++)
        {
            Vector3 a = centerline[ci];
            Vector3 b = centerline[ci + 1];
            for (int fi = 0; fi < footprint.Count; fi++)
            {
                Vector2 f0 = footprint[fi];
                Vector2 f1 = footprint[(fi + 1) % footprint.Count];
                if (SegmentsIntersectXZ(a, b,
                        new Vector3(f0.x, 0f, f0.y), new Vector3(f1.x, 0f, f1.y),
                        out _, out _))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// ローカル推定した干渉建物の占有箱を、公共施設は赤・民間は橙の半透明で表示する。
    /// バックエンドの権威結果が来ると HighlightManager 側の表示が重なる。
    /// </summary>
    private void DrawBuildingInterference(Camera cam)
    {
        if (overlayRenderer == null) return;

        CheckBuildingInterference();
        cachedBuildingVolume?.Draw(overlayRenderer);
    }
}
