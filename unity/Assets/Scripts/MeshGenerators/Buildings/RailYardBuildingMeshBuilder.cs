using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// 鉄道車両基地（MODEL_RAIL_YARD）のメッシュ生成器。
///
/// 車両基地は走行網の接続端子ノード（depot_node_id。電化前提の共用接続ノード）へ
/// 引き込み線路が溶接される施設で、敷地内の線路をその端子位置へ収束させて描画する。
/// フットプリントと端子ノード位置から次の2ゾーンをプロシージャルに組み立てる:
///
///   1. 並行線路ゾーン … 端子と反対側の敷地に、直線の線路（yard_track タイル）を
///      等間隔（TRACK_SPACING）で並べる。各線路の行き止まり端には車止め（yard_stop）、
///      上空には架線（yard_wire）と門型架線柱（yard_pole + yard_beam）を配置する。
///      余裕があれば先頭2線を検修庫（yard_shed）が覆う。
///   2. 集約・接続ゾーン … 最外側の線路を斜めの集約線として延ばし、
///      各留置線をその途中へ合流させる。集約線と端子の接続線は各1本だけ描く。
/// タイルは長手方向だけ端数を調整し、曲線では頂点を中心線に沿って変形する。
/// 軌間と高さは実寸を維持する。yard_beam は柱間へXスケールする。
///
/// 端子ノードの位置は PathManager のノードキャッシュから引く。未ロード・テストシーン
/// では、バックエンドの初期配置（接道辺から最も遠い辺の中点。crud_agents.py の
/// _connection_node_position）を模した位置へフォールバックする。端子の移動が確定した
/// ときは BuildingManager.OnTerminalNodeMoved がこの建物を再生成する。
///
/// 高さはすべてローカルY=0の水平面（center_point 位置の地形標高に接地。傾斜地の
/// 起伏やノードの相対高さには追従しない）。
///
/// 詳細仕様は genesis-docs/docs/geometries/F9_車両基地のメッシュ生成.md を参照。
/// </summary>
public class RailYardBuildingMeshBuilder : IBuildingMeshGenerator
{
    /// <summary>並行線路の中心線間隔(m)。パスの線路（width=5.0）と同じ値。</summary>
    private const float TRACK_SPACING = 5f;

    /// <summary>敷地境界から最外側の線路中心までの側方マージン(m)。架線柱の設置余地を含む。</summary>
    private const float SIDE_MARGIN = 3.5f;

    /// <summary>敷地境界から線路端までの端部マージン(m)。</summary>
    private const float END_MARGIN = 2f;

    /// <summary>並行線路の本数上限。巨大な敷地でドローコールが膨らむのを防ぐ。</summary>
    private const int MAX_TRACKS = 10;

    /// <summary>端子ノード手前の直線リード線の長さ(m)。曲線はこの始点へ収束する。</summary>
    private const float LEAD_LENGTH = 6f;

    /// <summary>集約線の横移動1mに対する走行長。隣接線の合流点は10m間隔になる。</summary>
    private const float LADDER_RUN_PER_LATERAL = 2f;
    /// <summary>留置線の曲げ始めから集約線との交点までの走行長。</summary>
    private const float ENTRY_APPROACH = 6f;
    /// <summary>集約線との交点から接線を揃えた合流点までの走行長。</summary>
    private const float ENTRY_OVERLAP = 4f;

    /// <summary>直線ゾーンとして成立する最小長(m)。これを切る行は線路を敷かない。</summary>
    private const float MIN_STRAIGHT_LENGTH = 6f;

    /// <summary>架線柱（門型）の設置間隔(m)。</summary>
    private const float POLE_SPACING = 30f;

    /// <summary>最外側の線路中心から架線柱までの外側オフセット(m)。</summary>
    private const float POLE_OFFSET = 2.0f;

    /// <summary>yard_beam の著作幅(m)。柱間へこの値との比でXスケールされる。</summary>
    private const float BEAM_AUTHORED_WIDTH = 6f;

    /// <summary>検修庫（yard_shed）の著作サイズ（幅・全長）(m)。著作ジョブ build_a1060.py と揃える。</summary>
    private const float SHED_WIDTH = 11.2f;
    private const float SHED_LENGTH = 24f;

    /// <summary>コライダーの押し出し高さ(m)。架線柱の天端相当。</summary>
    private const float COLLIDER_HEIGHT = 7f;

    /// <summary>1本の線路（1行）の敷設計画。座標は U(線路方向)/V(横方向) の2次元。</summary>
    private struct TrackRow
    {
        public float V;             // 行の横方向オフセット
        public float StraightStart; // 直線区間の始端（行き止まり側）
        public float StraightEnd;   // 直線区間の終端（=曲線の始点）
        public Vector2 Target;      // 共通の斜め集約線上の合流点
        public Vector2 Direction;   // 合流先の集約線の接線
        public bool IsSpine;        // 集約線の起点となる最外側の行
    }

    public BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic = false)
    {
        if (buildingData == null || buildingData.polygonArray == null || buildingData.polygonArray.Length < 3)
        {
            Debug.LogWarning($"[RailYardBMB] 底面ポリゴンが不正です (Building ID: {buildingData?.agent_id})。白い箱にフォールバックします。");
            return BuildingMeshBuilder.GenerateSimpleBuilding(null, 1, LocalConstants.FLOOR_HEIGHT);
        }

        Vector2[] footprint = BuildingMeshUtils.GetFootprint(buildingData);
        string prefix = BuildingMeshUtils.ResolveModelNaming(buildingData).Prefix;

        // center_point のワールド座標と地形高さ。PORT/PREBUILT と同じく中心位置の地形標高に接地する。
        Vector3 centerPointWorld = Vector3.zero;
        if (buildingData.centerPointArray != null && buildingData.centerPointArray.Length >= 2)
        {
            float cx = buildingData.centerPointArray[0];
            float cz = buildingData.centerPointArray[1];
            float cy = HeightmapTextureManager.GetTerrainHeight(new Vector3(cx, 1000f, cz));
            centerPointWorld = new Vector3(cx, cy, cz);
        }

        // 端子ノードのローカルXZ位置（未ロード時はバックエンドの初期配置を模したフォールバック）。
        Vector2 nodeLocal = ResolveTerminalNodeLocal(buildingData, centerPointWorld, footprint);

        // ローカル基底: U=線路方向（重心→端子）、V=その左手垂直。
        Vector2 centroid = Vector2.zero;
        for (int i = 0; i < footprint.Length; i++)
        {
            centroid += footprint[i];
        }
        centroid /= footprint.Length;

        Vector2 u2 = nodeLocal - centroid;
        if (u2.sqrMagnitude < 1e-4f)
        {
            // 端子が重心に一致する退化ケース。前面辺（頂点0→1）の法線方向を使う
            Vector2 front = (footprint[1] - footprint[0]).normalized;
            u2 = new Vector2(-front.y, front.x);
        }
        u2.Normalize();
        Vector2 v2 = new Vector2(-u2.y, u2.x);

        // フットプリントを UV 座標へ射影
        Vector2[] uv = new Vector2[footprint.Length];
        float vMin = float.MaxValue, vMax = float.MinValue;
        for (int i = 0; i < footprint.Length; i++)
        {
            Vector2 d = footprint[i] - centroid;
            uv[i] = new Vector2(Vector2.Dot(d, u2), Vector2.Dot(d, v2));
            vMin = Mathf.Min(vMin, uv[i].y);
            vMax = Mathf.Max(vMax, uv[i].y);
        }
        Vector2 nodeUV = new Vector2(Vector2.Dot(nodeLocal - centroid, u2), Vector2.Dot(nodeLocal - centroid, v2));
        float leadStartU = nodeUV.x - LEAD_LENGTH;

        // 並行線路の行割り（敷地の横幅から本数を決め、中央寄せで並べる）
        float usable = (vMax - vMin) - SIDE_MARGIN * 2f;
        int trackCount = Mathf.Clamp(Mathf.FloorToInt(usable / TRACK_SPACING) + 1, 1, MAX_TRACKS);
        float vCenter = (vMin + vMax) * 0.5f;

        List<TrackRow> rows = PlanRows(uv, nodeUV, vCenter, trackCount);
        while (rows.Count == 0 && trackCount > 1)
            rows = PlanRows(uv, nodeUV, vCenter, --trackCount);

        if (rows.Count == 0)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                $"[RailYardBMB] agent={buildingData.agent_id} 線路を1本も敷けない敷地形状。白い箱にフォールバック");
            return BuildingMeshBuilder.GenerateSimpleBuilding(footprint, 1, LocalConstants.FLOOR_HEIGHT);
        }

        List<CombineInstance> combineInstances = new List<CombineInstance>();
        List<Material> materials = new List<Material>();
        List<Mesh> temporaryMeshes = new List<Mesh>();

        // パーツのロード（yard_track が無い建物は線路を描けないので白い箱へ）
        Mesh trackMesh = LoadPartMesh(prefix, "yard_track", out Material[] trackMats, temporaryMeshes);
        if (trackMesh == null)
        {
            foreach (Mesh temp in temporaryMeshes) Object.Destroy(temp);
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                $"[RailYardBMB] agent={buildingData.agent_id} yard_track パーツ未配置。白い箱にフォールバック");
            return BuildingMeshBuilder.GenerateSimpleBuilding(footprint, 1, LocalConstants.FLOOR_HEIGHT);
        }
        Mesh wireMesh = LoadPartMesh(prefix, "yard_wire", out Material[] wireMats, temporaryMeshes);
        Mesh poleMesh = LoadPartMesh(prefix, "yard_pole", out Material[] poleMats, temporaryMeshes);
        Mesh beamMesh = LoadPartMesh(prefix, "yard_beam", out Material[] beamMats, temporaryMeshes);
        Mesh stopMesh = LoadPartMesh(prefix, "yard_stop", out Material[] stopMats, temporaryMeshes);
        Mesh shedMesh = LoadPartMesh(prefix, "yard_shed", out Material[] shedMats, temporaryMeshes);

        // タイルの実寸（ローカル+Zが線路方向。bounds で再基準化するため原点位置に依存しない）
        float tileLength = Mathf.Max(trackMesh.bounds.size.z, 0.5f);
        float tileMinZ = trackMesh.bounds.min.z;
        float wireMinZ = wireMesh != null ? wireMesh.bounds.min.z : 0f;

        // 座標変換ヘルパー: UV → ローカルXZ / ワールド方向
        Vector3 ToLocal3(Vector2 p) => new Vector3(
            centroid.x + u2.x * p.x + v2.x * p.y, 0f,
            centroid.y + u2.y * p.x + v2.y * p.y);
        Vector3 ToDir3(Vector2 d) => new Vector3(
            u2.x * d.x + v2.x * d.y, 0f,
            u2.y * d.x + v2.y * d.y).normalized;

        Quaternion forwardRot = Quaternion.LookRotation(ToDir3(Vector2.right), Vector3.up);

        // 直線・曲線を別々に敷き、共有区間は合流先の行だけが描画する。
        foreach (TrackRow row in rows)
        {
            int n = Mathf.Max(1, Mathf.RoundToInt((row.StraightEnd - row.StraightStart) / tileLength));
            float step = (row.StraightEnd - row.StraightStart) / n;
            for (int t = 0; t < n; t++)
            {
                Vector3 pos = ToLocal3(new Vector2(row.StraightStart + t * step, row.V));
                Matrix4x4 m = Matrix4x4.TRS(pos, forwardRot, new Vector3(1f, 1f, step / tileLength))
                    * Matrix4x4.Translate(new Vector3(0f, 0f, -tileMinZ));
                AddInstances(trackMesh, trackMats, m, combineInstances, materials);
                if (wireMesh != null)
                    AddInstances(wireMesh, wireMats,
                        Matrix4x4.TRS(pos, forwardRot, new Vector3(1f, 1f, step / wireMesh.bounds.size.z))
                        * Matrix4x4.Translate(new Vector3(0f, 0f, -wireMinZ)), combineInstances, materials);
            }
            if (stopMesh != null)
                AddInstances(stopMesh, stopMats,
                    Matrix4x4.TRS(ToLocal3(new Vector2(row.StraightStart, row.V)), forwardRot, Vector3.one),
                    combineInstances, materials);

            Vector2 p0 = new Vector2(row.StraightEnd, row.V);
            float run = row.Target.x - p0.x;
            PlaceTilesAlongBezier(p0, p0 + Vector2.right * (run * .4f),
                row.Target - row.Direction * ENTRY_OVERLAP, row.Target,
                trackMesh, trackMats, wireMesh, wireMats, tileLength, tileMinZ, wireMinZ,
                ToLocal3, ToDir3, combineInstances, materials, temporaryMeshes,
                !row.IsSpine);
        }

        // 最外側線から延びる集約線を1本だけ敷き、最後に共通の引き込み線で端子へ接続する。
        TrackRow first = rows.Find(row => row.IsSpine);
        Vector2 spineStart = first.Target;
        Vector2 spineEnd = spineStart;
        foreach (TrackRow row in rows)
            if (row.Target.x > spineEnd.x) spineEnd = row.Target;
        Vector2 delta = spineEnd - spineStart;
        PlaceTilesAlongBezier(spineStart, spineStart + delta / 3f, spineStart + delta * (2f / 3f), spineEnd,
            trackMesh, trackMats, wireMesh, wireMats, tileLength, tileMinZ, wireMinZ,
            ToLocal3, ToDir3, combineInstances, materials, temporaryMeshes, false);
        Vector2 lead = new Vector2(leadStartU, nodeUV.y);
        float exitRun = lead.x - spineEnd.x;
        PlaceTilesAlongBezier(spineEnd, spineEnd + first.Direction * (exitRun * .35f),
            lead - Vector2.right * (exitRun * .35f), lead,
            trackMesh, trackMats, wireMesh, wireMats, tileLength, tileMinZ, wireMinZ,
            ToLocal3, ToDir3, combineInstances, materials, temporaryMeshes, false);
        PlaceTilesAlongBezier(lead, lead + Vector2.right * 2f, nodeUV - Vector2.right * 2f, nodeUV,
            trackMesh, trackMats, wireMesh, wireMats, tileLength, tileMinZ, wireMinZ,
            ToLocal3, ToDir3, combineInstances, materials, temporaryMeshes, false);

        // --- 3. 検修庫: 先頭2線の行き止まり側を覆う（直線ゾーンに十分な長さがある場合のみ） ---
        // 架線柱より先に位置を確定し、庫の区間には門型柱を立てない（梁が庫を貫通するため）
        float shedUStart = float.NaN, shedUEnd = float.NaN;
        if (shedMesh != null && rows.Count >= 2)
        {
            TrackRow rowA = rows[0];
            TrackRow rowB = rows[1];
            float uStart = Mathf.Max(rowA.StraightStart, rowB.StraightStart) + 1.5f;
            float uEnd = uStart + SHED_LENGTH;
            if (uEnd + 2f < Mathf.Min(rowA.StraightEnd, rowB.StraightEnd)
                && Mathf.Abs(rowA.V - rowB.V) < SHED_WIDTH)
            {
                Vector3 pos = ToLocal3(new Vector2((uStart + uEnd) * 0.5f, (rowA.V + rowB.V) * 0.5f));
                AddInstances(shedMesh, shedMats, Matrix4x4.TRS(pos, forwardRot, Vector3.one), combineInstances, materials);
                shedUStart = uStart;
                shedUEnd = uEnd;
            }
        }

        // --- 4. 架線柱（門型）: 直線ゾーンの共通区間に等間隔（検修庫の区間は避ける） ---
        if (poleMesh != null && rows.Count > 0)
        {
            float vTrackMin = float.MaxValue, vTrackMax = float.MinValue;
            float uCommonStart = float.MinValue, uCommonEnd = float.MaxValue;
            foreach (TrackRow row in rows)
            {
                vTrackMin = Mathf.Min(vTrackMin, row.V);
                vTrackMax = Mathf.Max(vTrackMax, row.V);
                uCommonStart = Mathf.Max(uCommonStart, row.StraightStart);
                uCommonEnd = Mathf.Min(uCommonEnd, row.StraightEnd);
            }
            float vLeft = vTrackMin - POLE_OFFSET;
            float vRight = vTrackMax + POLE_OFFSET;
            // yard_pole は「右側用」（軌道中心側が-X）。基準回転では -X が +V を向くため、
            // +V側（vRight）は基準回転のまま、-V側（vLeft）はヨー180°で軌道中心へ向ける
            Quaternion rightRot = forwardRot;
            Quaternion leftRot = forwardRot * Quaternion.Euler(0f, 180f, 0f);
            for (float uPole = uCommonStart + 4f; uPole <= uCommonEnd - 2f; uPole += POLE_SPACING)
            {
                if (!float.IsNaN(shedUStart) && uPole > shedUStart - 1f && uPole < shedUEnd + 1f)
                {
                    continue;
                }
                AddInstances(poleMesh, poleMats,
                    Matrix4x4.TRS(ToLocal3(new Vector2(uPole, vRight)), rightRot, Vector3.one),
                    combineInstances, materials);
                AddInstances(poleMesh, poleMats,
                    Matrix4x4.TRS(ToLocal3(new Vector2(uPole, vLeft)), leftRot, Vector3.one),
                    combineInstances, materials);
                if (beamMesh != null)
                {
                    // 門型梁は著作幅 6m を柱間へXスケール（著作のX方向はローカル±X=横方向）
                    float span = vRight - vLeft;
                    Vector3 center = ToLocal3(new Vector2(uPole, (vLeft + vRight) * 0.5f));
                    AddInstances(beamMesh, beamMats,
                        Matrix4x4.TRS(center, forwardRot, new Vector3(span / BEAM_AUTHORED_WIDTH, 1f, 1f)),
                        combineInstances, materials);
                }
            }
        }

        // 線路直下の道床はyard_trackが持つ。線路のない余白には敷地全面パネルを張らない。
        Vector2[] parcelPolygon = BuildingMeshUtils.ResolveParcelContext(
            buildingData, useParcelAltitude: false, out _, out _);
        Vector2[] sitePolygon = (parcelPolygon != null && parcelPolygon.Length >= 3) ? parcelPolygon : footprint;
        ModularBodyBuilder.GenerateSiteDecorations(
            prefix, 0,
            parcelPolygon, sitePolygon, sitePolygon,
            null,
            centerPointWorld, deformToTerrain: isDynamic,
            combineInstances, materials, temporaryMeshes);

        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
            $"[RailYardBMB] agent={buildingData.agent_id} prefix={prefix} tracks={rows.Count} " +
            $"node=({nodeUV.x:F1},{nodeUV.y:F1}) instances={combineInstances.Count}");

        // --- 6. 結合・コライダー ---
        Mesh finalMesh = new Mesh();
        finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        finalMesh.CombineMeshes(combineInstances.ToArray(), false, true);
        finalMesh.RecalculateNormals();
        finalMesh.RecalculateBounds();

        foreach (Mesh temp in temporaryMeshes)
        {
            Object.Destroy(temp);
        }

        Mesh colliderMesh = BuildingMeshBuilder.GenerateColliderMesh(footprint, COLLIDER_HEIGHT);
        var result = new BuildingMeshResult(finalMesh, materials.ToArray(), colliderMesh);
        return BuildingMeshBuilder.MergeSubMeshes(result);
    }

    /// <summary>
    /// 端子ノード（depot_node_id / 共用時は infra_node_id と同一）のローカルXZ位置を解決する。
    /// PathManager のノードキャッシュに無い場合（未ロード・テストシーン）は、バックエンドの
    /// 初期配置ロジック（接道辺=前面辺から最も遠い辺の中点。crud_agents._connection_node_position）
    /// を模した位置へフォールバックする。ユーザーが端子を移動していない限り両者は一致する。
    /// </summary>
    private static Vector2 ResolveTerminalNodeLocal(BuildingSummary buildingData, Vector3 centerPointWorld, Vector2[] footprint)
    {
        int nodeId = buildingData.depot_node_id > 0 ? buildingData.depot_node_id : buildingData.infra_node_id;
        if (nodeId > 0)
        {
            PathManager pathManager = Object.FindAnyObjectByType<PathManager>();
            NodeData node = pathManager != null ? pathManager.GetNodeData(nodeId) : null;
            if (node != null)
            {
                Vector3 abs = node.GetAbsolutePosition();
                return new Vector2(abs.x - centerPointWorld.x, abs.z - centerPointWorld.z);
            }
        }

        // フォールバック: 前面辺（頂点0→1）から最も遠い辺の中点
        int n = footprint.Length;
        Vector2 frontA = footprint[0];
        Vector2 frontB = footprint[1 % n];
        Vector2 best = (footprint[0] + footprint[n / 2]) * 0.5f;
        float bestDist = -1f;
        for (int i = 1; i < n; i++)
        {
            Vector2 mid = (footprint[i] + footprint[(i + 1) % n]) * 0.5f;
            float dist = DistancePointToSegment(mid, frontA, frontB);
            if (dist > bestDist)
            {
                bestDist = dist;
                best = mid;
            }
        }
        return best;
    }

    private static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-6f)
        {
            return (p - a).magnitude;
        }
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return (p - (a + ab * t)).magnitude;
    }

    /// <summary>
    /// UV座標のフットプリントを、V=<paramref name="v"/> の直線（U方向）で切ったときの
    /// U範囲（弦）を返す。交点が2つ未満なら false。
    /// 凹形状では最小〜最大の交点を採る（弦の途中で敷地外へ出る区画では線路が
    /// はみ出しうるが、敷地装飾のフェンス内には収まるため許容する）。
    /// </summary>
    private static bool TryGetChordRange(Vector2[] uv, float v, out float uStart, out float uEnd)
    {
        uStart = float.MaxValue;
        uEnd = float.MinValue;
        int hits = 0;
        int n = uv.Length;
        for (int i = 0; i < n; i++)
        {
            Vector2 a = uv[i];
            Vector2 b = uv[(i + 1) % n];
            if ((a.y <= v && b.y > v) || (b.y <= v && a.y > v))
            {
                float t = (v - a.y) / (b.y - a.y);
                float u = Mathf.Lerp(a.x, b.x, t);
                uStart = Mathf.Min(uStart, u);
                uEnd = Mathf.Max(uEnd, u);
                hits++;
            }
        }
        return hits >= 2 && uEnd - uStart > 1f;
    }

    /// <summary>最外側から反対側へ横切る集約線を基準に、留置線の終端を求める。</summary>
    private static List<TrackRow> PlanRows(Vector2[] uv, Vector2 node, float center, int count)
    {
        var rows = new List<TrackRow>();
        float side = count == 1 ? 0f : node.y <= center ? 1f : -1f;
        float outerV = center + side * (count - 1) * TRACK_SPACING * .5f;
        float oppositeV = center - side * (count - 1) * TRACK_SPACING * .5f;
        Vector2 direction = new Vector2(LADDER_RUN_PER_LATERAL, -side).normalized;
        float lastV = oppositeV - side * ENTRY_OVERLAP / LADDER_RUN_PER_LATERAL;
        float exitRun = Mathf.Max(18f, Mathf.Abs(lastV - node.y) * 2f);
        float lastU = node.x - LEAD_LENGTH - exitRun;
        float firstU = lastU - (count - 1) * TRACK_SPACING * LADDER_RUN_PER_LATERAL;
        for (int i = 0; i < count; i++)
        {
            float v = outerV - side * i * TRACK_SPACING;
            Vector2 target = new Vector2(firstU + i * TRACK_SPACING * LADDER_RUN_PER_LATERAL,
                v - side * ENTRY_OVERLAP / LADDER_RUN_PER_LATERAL);
            float end = target.x - ENTRY_OVERLAP - ENTRY_APPROACH;
            if (!TryGetChordRange(uv, v, out float start, out float limit)) return new List<TrackRow>();
            start += END_MARGIN;
            if (end > limit - END_MARGIN || end - start < MIN_STRAIGHT_LENGTH) return new List<TrackRow>();
            Vector2 p0 = new Vector2(end, v);
            for (int k = 0; k <= 32; k++)
            {
                Vector2 point = BezierPoint(p0, p0 + Vector2.right * ((target.x-end)*.4f),
                    target - direction * ENTRY_OVERLAP, target, k/32f);
                if (!ContainsPoint(uv, point)) return new List<TrackRow>();
            }
            rows.Add(new TrackRow { V=v, StraightStart=start, StraightEnd=end, Target=target,
                Direction=direction, IsSpine=i==0 });
        }
        // 接続点を敷地内で移動した場合も、共有線の中心が敷地から出る計画は採用しない。
        Vector2 spineStart=rows[0].Target, spineEnd=rows[rows.Count-1].Target;
        Vector2 lead=new Vector2(node.x-LEAD_LENGTH,node.y);
        for (int k=0; k<=64; k++)
        {
            float t=k/64f;
            if (!ContainsPoint(uv,Vector2.Lerp(spineStart,spineEnd,t))
                || !ContainsPoint(uv,BezierPoint(spineEnd,spineEnd+direction*(exitRun*.35f),
                    lead-Vector2.right*(exitRun*.35f),lead,t))
                || !ContainsPoint(uv,Vector2.Lerp(lead,node,t))) return new List<TrackRow>();
        }
        rows.Sort((a,b)=>a.V.CompareTo(b.V));
        return rows;
    }

    private static bool ContainsPoint(Vector2[] polygon, Vector2 point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            Vector2 a = polygon[i], b = polygon[j];
            if (DistancePointToSegment(point, a, b) < .01f) return true;
            if ((a.y > point.y) != (b.y > point.y)
                && point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
        }
        return inside;
    }

    /// <summary>弧長に沿ってタイル頂点を曲げる。隣接タイルの境界は同じ位置・接線を使う。</summary>
    private static void PlaceTilesAlongBezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3,
        Mesh trackMesh, Material[] trackMats, Mesh wireMesh, Material[] wireMats,
        float tileLength, float tileMinZ, float wireMinZ,
        System.Func<Vector2, Vector3> toLocal3, System.Func<Vector2, Vector3> toDir3,
        List<CombineInstance> combineInstances, List<Material> materials, List<Mesh> temporaryMeshes,
        bool joinsTrack)
    {
        const int samples = 96;
        float[] lengths = new float[samples + 1];
        Vector2 prev = p0;
        for (int i = 1; i <= samples; i++)
        {
            Vector2 point = BezierPoint(p0, p1, p2, p3, i / (float)samples);
            lengths[i] = lengths[i - 1] + Vector2.Distance(prev, point);
            prev = point;
        }
        float total = lengths[samples];
        if (total < .01f) return;
        int tiles = Mathf.Max(1, Mathf.RoundToInt(total / tileLength));
        float step = total / tiles;
        float Parameter(float distance)
        {
            int i = System.Array.BinarySearch(lengths, Mathf.Clamp(distance, 0f, total));
            if (i >= 0) return i / (float)samples;
            i = Mathf.Clamp(~i, 1, samples);
            return (i - 1 + (distance - lengths[i - 1]) / (lengths[i] - lengths[i - 1])) / samples;
        }
        void Bend(Mesh source, Material[] mats, float minZ, int tile, bool isTrack)
        {
            if (source == null) return;
            Mesh mesh = Object.Instantiate(source);
            Vector3[] vertices = source.vertices;
            for (int v = 0; v < vertices.Length; v++)
            {
                Vector3 local = vertices[v];
                float t = Parameter((tile + (local.z - minZ) / source.bounds.size.z) * step);
                Vector2 at = BezierPoint(p0, p1, p2, p3, t);
                Vector2 tangent = 3f * (1f - t) * (1f - t) * (p1 - p0)
                    + 6f * (1f - t) * t * (p2 - p1) + 3f * t * t * (p3 - p2);
                Vector3 right = Vector3.Cross(Vector3.up, toDir3(tangent));
                vertices[v] = toLocal3(at) + right * local.x + Vector3.up * local.y;
            }
            mesh.vertices = vertices;
            // 合流先の支持材の外縁で切り、分岐側の道床・枕木を隙間なく接続する。
            if (joinsTrack && isTrack)
            {
                var clippedVertices = new List<Vector3>(vertices);
                var uvs = new List<Vector2>(source.uv);
                while (uvs.Count < vertices.Length) uvs.Add(Vector2.zero);
                Vector2 tangent = (p3 - p2).normalized;
                Vector2 outward = new Vector2(-tangent.y, tangent.x);
                outward *= Mathf.Sign(Vector2.Dot(p0 - p3, outward));
                Vector3 normal = toDir3(outward);
                Vector3 origin = toLocal3(p3);
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    string name = mats[sub] != null ? mats[sub].name : "";
                    float edge = name.StartsWith("PATH_ROADBED") ? 1.55f
                        : (name.StartsWith("PATH_CONCRETE") || name.StartsWith("PATH_RAIL_METAL")) ? 1.3f : 0f;
                    if (edge == 0f) continue;
                    int[] indices = source.GetTriangles(sub);
                    var triangles = new List<int>();
                    for (int f = 0; f < indices.Length; f += 3)
                    {
                        var polygon = new List<int>(4);
                        for (int k = 0; k < 3; k++)
                        {
                            int a = indices[f + k], b = indices[f + (k + 1) % 3];
                            float da = Vector3.Dot(vertices[a] - origin, normal) - edge;
                            float db = Vector3.Dot(vertices[b] - origin, normal) - edge;
                            if (da >= 0f) polygon.Add(a);
                            if ((da >= 0f) == (db >= 0f)) continue;
                            float ratio = da / (da - db);
                            polygon.Add(clippedVertices.Count);
                            clippedVertices.Add(Vector3.Lerp(vertices[a], vertices[b], ratio));
                            uvs.Add(Vector2.Lerp(uvs[a], uvs[b], ratio));
                        }
                        for (int k = 1; k + 1 < polygon.Count; k++)
                        {
                            triangles.Add(polygon[0]); triangles.Add(polygon[k]); triangles.Add(polygon[k + 1]);
                        }
                    }
                    mesh.SetVertices(clippedVertices);
                    mesh.SetUVs(0, uvs);
                    mesh.SetTriangles(triangles, sub);
                }
            }
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            temporaryMeshes.Add(mesh);
            AddInstances(mesh, mats, Matrix4x4.identity, combineInstances, materials);
        }
        for (int tile = 0; tile < tiles; tile++)
        {
            Bend(trackMesh, trackMats, tileMinZ, tile, true);
            Bend(wireMesh, wireMats, wireMinZ, tile, false);
        }
    }

    private static Vector2 BezierPoint(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float it = 1f - t;
        return it * it * it * p0 + 3f * it * it * t * p1 + 3f * it * t * t * p2 + t * t * t * p3;
    }

    /// <summary>
    /// パーツプレハブを結合メッシュとしてロードする。複数オブジェクト構成のFBXでも
    /// 欠けないよう <see cref="BuildingMeshUtils.CombineModuleMeshes"/> を使う。
    /// パーツが無い場合は null（オプショナルパーツはスキップされる）。
    /// </summary>
    private static Mesh LoadPartMesh(string prefix, string partName, out Material[] partMaterials, List<Mesh> temporaryMeshes)
    {
        partMaterials = null;
        GameObject prefab = BuildingMeshUtils.GetPartNoPosition(prefix, partName, 0, true);
        if (prefab == null)
        {
            return null;
        }
        Mesh mesh = BuildingMeshUtils.CombineModuleMeshes(prefab, out partMaterials);
        if (mesh != null)
        {
            temporaryMeshes.Add(mesh);
        }
        return mesh;
    }

    /// <summary>
    /// メッシュの全サブメッシュを指定の変換行列で CombineInstance リストへ追加する。
    /// メッシュ実体は共有し、行列だけを変えて多数配置する（PlacePortCranes と同方式）。
    /// </summary>
    private static void AddInstances(Mesh mesh, Material[] subMeshMaterials, Matrix4x4 matrix,
        List<CombineInstance> combineInstances, List<Material> materials)
    {
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            combineInstances.Add(new CombineInstance
            {
                mesh = mesh,
                subMeshIndex = s,
                transform = matrix
            });
            Material mat = (subMeshMaterials != null && subMeshMaterials.Length > 0)
                ? subMeshMaterials[Mathf.Min(s, subMeshMaterials.Length - 1)]
                : null;
            materials.Add(mat);
        }
    }
}
