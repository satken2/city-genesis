using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// 鉄道車両基地（MODEL_RAIL_YARD）のメッシュ生成器。
///
/// 車両基地は走行網の接続端子ノード（depot_node_id。鉄道車両基地・モノレール車庫では電力と共用の
/// 接続ノード）へ引き込み線路が溶接される施設で、敷地内の線路をその端子位置へ収束させて描画する。
/// フットプリントと端子ノード位置から次の2ゾーンをプロシージャルに組み立てる:
///
///   1. 並行線路ゾーン … 端子と反対側の敷地に、直線の線路（yard_track タイル）を
///      等間隔（TRACK_SPACING）で並べる。各線路の行き止まり端には車止め（yard_stop）、
///      上空には架線（yard_wire）と門型架線柱（yard_pole + yard_beam）を配置する。
///      余裕があれば先頭2線を検修庫（yard_shed）が覆う。
///   2. 集約・接続ゾーン … 接続点を通る中央線の左右に、最外側から延びる斜め集約線を置く。
///      各留置線は同じ側の集約線へ途中合流し、左右の集約線は中央線へ接続する。
/// タイルは長手方向だけ端数を調整し、曲線では頂点を中心線に沿って変形する。
/// 軌間と高さは実寸を維持する。yard_beam は柱間へXスケールする。
///
/// 端子ノードの位置は PathManager のノードキャッシュから引く。未ロード・テストシーン
/// では、バックエンドの初期配置（接道辺から最も遠い辺の中点。LRT車両基地は前面辺の中点。
/// crud_agents.py の _connection_node_position）を模した位置へフォールバックする。端子の移動が確定した
/// ときは BuildingManager.OnTerminalNodeMoved がこの建物を再生成する。
///
/// 端子へつながる線路が1本で複線のパス（LRT専用軌道431・まとめた複線）の基地は、端子の手前を同じ軌道間隔の
/// 複線にする。片側の軌道が集約線で、もう片側は渡り線で集約線へ合流する。端子へつながる線路が変わると
/// BuildingManager.OnDepotTerminalPathsChanged がこの建物を作り直す。
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

    /// <summary>複線の引き込み線の2本目で、端子から渡り線の始点までの直線長(m)。</summary>
    private const float SECOND_LEAD_STRAIGHT = 3f;
    /// <summary>2本目が集約線へ合流する渡り線の走行長(m)。軌道中心間隔3.5mで最小曲線半径が約17mになる。</summary>
    private const float SECOND_LEAD_RUN = 16f;

    /// <summary>集約線の横移動1mに対する走行長。隣接線の合流点は10m間隔になる。</summary>
    private const float LADDER_RUN_PER_LATERAL = 2f;
    /// <summary>留置線の曲げ始めから集約線との交点までの走行長。</summary>
    private const float ENTRY_APPROACH = 6f;
    /// <summary>集約線との交点から接線を揃えた合流点までの走行長。</summary>
    private const float ENTRY_OVERLAP = 4f;

    /// <summary>敷地境界のちょうど上を走る線路を内側と判定するための余裕(m)。</summary>
    private const float BOUNDARY_TOLERANCE = .01f;

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
        public int Side;            // 中央線=0、左右=-1/+1
        public Vector2 MergePoint;  // 同じ側の集約線が中央線へ接続する位置
    }

    /// <summary>基地が車両を送り出す走行網と端子へつながる線路から決まる、端子と引き込み線の描き方。</summary>
    private struct YardProfile
    {
        public bool TerminalOnFrontage; // 端子の初期位置が前面辺（頂点0→1）の中点
        public float LeadHalfSpacing;   // 端子へ入る複線の軌道中心の半間隔。単線は0
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
            float cy = Utils.GetGroundHeight(new Vector3(cx, 1000f, cz));
            centerPointWorld = new Vector3(cx, cy, cz);
        }

        // 端子ノードのローカルXZ位置（未ロード時はバックエンドの初期配置を模したフォールバック）。
        YardProfile profile = ResolveProfile(buildingData);
        Vector2 nodeLocal = ResolveTerminalNodeLocal(buildingData, centerPointWorld, footprint, profile.TerminalOnFrontage);

        // ローカル基底: U=敷地の初期接続辺へ向かう線路方向、V=その左手垂直。
        Vector2 centroid = Vector2.zero;
        for (int i = 0; i < footprint.Length; i++)
        {
            centroid += footprint[i];
        }
        centroid /= footprint.Length;

        // 端子の横移動で全線の向きが回転しないよう、敷地の初期接続辺を方向の基準にする。
        Vector2 u2 = ResolveTerminalNodeLocal(null, centerPointWorld, footprint, profile.TerminalOnFrontage) - centroid;
        if (u2.sqrMagnitude < 1e-4f)
        {
            // 端子が重心に一致する退化ケース。前面辺（頂点0→1）の法線方向を使う
            Vector2 front = (footprint[1] - footprint[0]).normalized;
            u2 = new Vector2(-front.y, front.x);
        }
        u2.Normalize();
        if (Vector2.Dot(nodeLocal - centroid, u2) < 0f) u2 = -u2;
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
        // 複線の引き込み線では、端子の片側の軌道位置へ構内の線路を集約する（単線は端子そのもの）。
        Vector2 trunkNode = nodeUV - new Vector2(0f, profile.LeadHalfSpacing);
        float leadStartU = trunkNode.x - LEAD_LENGTH;

        // 並行線路の行割り（敷地の横幅から本数を決め、中央寄せで並べる）
        float usable = (vMax - vMin) - SIDE_MARGIN * 2f;
        int trackCount = Mathf.Clamp(Mathf.FloorToInt(usable / TRACK_SPACING) + 1, 1, MAX_TRACKS);
        float vCenter = (vMin + vMax) * 0.5f;

        List<TrackRow> rows = PlanRows(uv, trunkNode, vCenter, trackCount);


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
            if (row.Side == 0) continue;
            PlaceTilesAlongBezier(p0, p0 + Vector2.right * (run * .4f),
                row.Target - row.Direction * ENTRY_OVERLAP, row.Target,
                trackMesh, trackMats, wireMesh, wireMats, tileLength, tileMinZ, wireMinZ,
                ToLocal3, ToDir3, combineInstances, materials, temporaryMeshes,
                row.Side != 0 && !row.IsSpine);
        }

        // 左右それぞれの外側線から中央線へ集約する。横位置を戻す接続曲線は作らない。
        foreach (int side in new[] { -1, 1 })
        {
            TrackRow first = rows.Find(row => row.Side == side && row.IsSpine);
            if (first.Side == 0) continue;
            Vector2 spineStart = first.Target;
            Vector2 spineEnd = first.MergePoint + new Vector2(-8f, side * 2f);
            Vector2 delta = spineEnd - spineStart;
            PlaceTilesAlongBezier(spineStart, spineStart + delta / 3f, spineStart + delta * (2f / 3f), spineEnd,
                trackMesh, trackMats, wireMesh, wireMats, tileLength, tileMinZ, wireMinZ,
                ToLocal3, ToDir3, combineInstances, materials, temporaryMeshes, false);
            PlaceTilesAlongBezier(spineEnd, spineEnd + first.Direction * 3f,
                first.MergePoint - Vector2.right * 3f, first.MergePoint,
                trackMesh, trackMats, wireMesh, wireMats, tileLength, tileMinZ, wireMinZ,
                ToLocal3, ToDir3, combineInstances, materials, temporaryMeshes, true);
        }
        Vector2 lead = new Vector2(leadStartU, trunkNode.y);
        PlaceTilesAlongBezier(lead, lead + Vector2.right * 2f, trunkNode - Vector2.right * 2f, trunkNode,
            trackMesh, trackMats, wireMesh, wireMats, tileLength, tileMinZ, wireMinZ,
            ToLocal3, ToDir3, combineInstances, materials, temporaryMeshes, false);

        // 複線の2本目: 端子から直線で入り、渡り線で集約線へ合流する（合流側の支持材は集約線の外縁で切る）。
        float secondMergeU = float.NaN;
        if (profile.LeadHalfSpacing > 0f
            && TryPlanSecondLead(uv, rows, nodeUV, trunkNode, out Vector2 secondEnd, out Vector2 secondStart, out Vector2 secondMerge))
        {
            PlaceTilesAlongBezier(secondStart, secondStart + Vector2.right, secondEnd - Vector2.right, secondEnd,
                trackMesh, trackMats, wireMesh, wireMats, tileLength, tileMinZ, wireMinZ,
                ToLocal3, ToDir3, combineInstances, materials, temporaryMeshes, false);
            Vector2 handle = Vector2.right * (SECOND_LEAD_RUN * .4f);
            PlaceTilesAlongBezier(secondStart, secondStart - handle, secondMerge + handle, secondMerge,
                trackMesh, trackMats, wireMesh, wireMats, tileLength, tileMinZ, wireMinZ,
                ToLocal3, ToDir3, combineInstances, materials, temporaryMeshes, true);
            secondMergeU = secondMerge.x;
        }

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
                && Mathf.Abs(rowA.V - rowB.V) <= TRACK_SPACING + .01f)
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
            // 2本目の渡り線は行の外を横切るので、その区間には柱を立てない。
            if (!float.IsNaN(secondMergeU)) uCommonEnd = Mathf.Min(uCommonEnd, secondMergeU - 2f);
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
            buildingData, out _, out _);
        Vector2[] sitePolygon = (parcelPolygon != null && parcelPolygon.Length >= 3) ? parcelPolygon : footprint;
        ModularBodyBuilder.GenerateSiteDecorations(
            prefix, 0,
            sitePolygon, sitePolygon, sitePolygon,
            null,
            centerPointWorld, deformToTerrain: isDynamic,
            combineInstances, materials, temporaryMeshes,
            fenceOpeningCenter: ResolveFenceOpening(sitePolygon,nodeLocal,u2), fenceOpeningRadius: 3f + profile.LeadHalfSpacing);

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
    /// 初期配置ロジック（接道辺=前面辺から最も遠い辺の中点。onFrontage なら前面辺の中点。
    /// crud_agents._connection_node_position）を模した位置へフォールバックする。
    /// ユーザーが端子を移動していない限り両者は一致する。
    /// </summary>
    private static Vector2 ResolveTerminalNodeLocal(BuildingSummary buildingData, Vector3 centerPointWorld, Vector2[] footprint, bool onFrontage)
    {
        int nodeId = buildingData == null ? 0 : buildingData.depot_node_id > 0 ? buildingData.depot_node_id : buildingData.infra_node_id;
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

        int n = footprint.Length;
        if (onFrontage) return (footprint[0] + footprint[1 % n]) * 0.5f;

        // フォールバック: 前面辺（頂点0→1）から最も遠い辺の中点
        Vector2 frontA = footprint[0];
        Vector2 frontB = footprint[1 % n];
        Vector2 best = (footprint[0] + footprint[n / 2]) * 0.5f;
        float bestDist = -1f;
        for (int i = 1; i < n; i++)
        {
            Vector2 mid = (footprint[i] + footprint[(i + 1) % n]) * 0.5f;
            float dist = PolylineUtils.DistanceToSegment(mid, frontA, frontB);
            if (dist > bestDist)
            {
                bestDist = dist;
                best = mid;
            }
        }
        return best;
    }

    /// <summary>
    /// 端子の位置と引き込み線の描き方を導く。端子の位置は、基地の供給する車両財（agent_master.supply）を
    /// 使う路線種別の走行網で決まり、溶接を要する軌道系と街路の両方を走る網（LRT）は端子が前面辺にある
    /// （バックエンドの transit_route.depot_terminal_on_frontage と同じ判定）。
    /// 引き込み線は、端子へ実際につながる線路パスの軌道数が2以上ならその軌道間隔の複線になる
    /// （同じ網に単線と複線の線路が併存するので、網からは決まらない）。つながる線路が無い間（未接続・未ロード）は
    /// 網の線路のうち軌道数の最も少ないもので描く（単線がある網は単線、複線の線路しか無いLRTは複線）。
    /// マスター未取得（テストシーン等）は鉄道車両基地と同じ描き方。
    /// </summary>
    private static YardProfile ResolveProfile(BuildingSummary building)
    {
        var profile = new YardProfile();
        MasterDataStore store = MasterDataStore.Instance;
        if (store == null || !store.GetAgentMasterDictionary().TryGetValue(building.variant, out var agent) || agent.supply == null)
            return profile;
        var paths = store.GetPathMasterDictionary();
        bool guided = false, road = false;
        float networkLead = float.MaxValue;
        foreach (var transit in store.GetTransitMasterDictionary().Values)
        {
            if (transit.vehicle != agent.supply) continue;
            foreach (List<int> variants in new[] { transit.route_path_variants, transit.stop_path_variants })
            {
                if (variants == null) continue;
                foreach (int variant in variants)
                {
                    if (!paths.TryGetValue(variant, out PathMasterItem path)) continue;
                    bool track = TrackIntersectionMeshGenerator.IsTrackType(path.type);
                    guided |= track;
                    road |= path.type == Constants.PATH_STREET || path.type == Constants.PATH_WALKWAY;
                    if (track) networkLead = Mathf.Min(networkLead, LeadHalfSpacing(path.width, path.lanes ?? 1));
                }
            }
        }
        profile.TerminalOnFrontage = guided && road;
        profile.LeadHalfSpacing = ConnectedLeadHalfSpacing(building) ?? (networkLead < float.MaxValue ? networkLead : 0f);
        return profile;
    }

    /// <summary>
    /// 引き込み線の軌道中心の半間隔（単線は0）。建物の version が変わらないまま端子へつながる線路が変わると
    /// 見た目が変わるので、BuildingManager が再利用キーに混ぜる。
    /// </summary>
    public static float ResolveLeadHalfSpacing(BuildingSummary building)
    {
        return ResolveProfile(building).LeadHalfSpacing;
    }

    /// <summary>端子へつながる線路パスから決まる引き込み線の半間隔。つながる線路が無ければ null。</summary>
    private static float? ConnectedLeadHalfSpacing(BuildingSummary building)
    {
        int nodeId = building.depot_node_id > 0 ? building.depot_node_id : building.infra_node_id;
        var connected = nodeId > 0 ? TrackPath.ConnectedPathsProvider?.Invoke(nodeId) : null;
        if (connected == null) return null;
        float? spacing = null;
        foreach (PathSummary path in connected)
        {
            if (!TrackIntersectionMeshGenerator.IsTrackType(path.path_type)) continue;
            TrackPath.GetTrackMasterParams(path, out float width, out int lanes, out _, out _, out _, out _, out _, out _);
            spacing = Mathf.Max(spacing ?? 0f, LeadHalfSpacing(width, lanes));
        }
        return spacing;
    }

    /// <summary>軌道数2以上の線路の軌道中心の半間隔。単線は0。</summary>
    private static float LeadHalfSpacing(float width, int lanes)
    {
        return lanes >= 2 ? Mathf.Abs(TrackPath.GetTrackCenterOffset(width, lanes, 0)) : 0f;
    }

    /// <summary>
    /// 複線の引き込み線の2本目を計画する。端子の反対側の軌道位置から SECOND_LEAD_STRAIGHT だけ直進し、
    /// SECOND_LEAD_RUN の渡り線で集約線の直線区間へ合流する。合流点が集約線の直線区間に無い、
    /// または線路が敷地に収まらない場合は false（集約線の1本だけで端子へ入る）。
    /// </summary>
    private static bool TryPlanSecondLead(Vector2[] uv, List<TrackRow> rows, Vector2 node, Vector2 trunkNode,
        out Vector2 end, out Vector2 start, out Vector2 merge)
    {
        end = node + (node - trunkNode);
        start = end - Vector2.right * SECOND_LEAD_STRAIGHT;
        merge = new Vector2(start.x - SECOND_LEAD_RUN, trunkNode.y);
        int trunk = rows.FindIndex(row => row.Side == 0);
        if (trunk < 0 || merge.x < rows[trunk].StraightStart) return false;
        Vector2 handle = Vector2.right * (SECOND_LEAD_RUN * .4f);
        for (int k = 0; k <= 64; k++)
        {
            float t = k / 64f;
            if (!PolygonUtils.Contains(uv, Vector2.Lerp(start, end, t), BOUNDARY_TOLERANCE)
                || !PolygonUtils.Contains(uv, PolylineUtils.CubicBezierPoint(start, start - handle, merge + handle, merge, t), BOUNDARY_TOLERANCE))
                return false;
        }
        return true;
    }

    /// <summary>
    /// 接続点を通る直線を先に確保し、左右の集約線へ留置線を追加する。
    /// 境界に収まらない行だけを省くため、端子の移動で施設全体を棄却しない。
    /// </summary>
    private static List<TrackRow> PlanRows(Vector2[] uv, Vector2 node, float center, int count)
    {
        var rows = new List<TrackRow>();
        Vector2 lead = node - Vector2.right * LEAD_LENGTH;
        if (!TryGetRowStart(uv, node.y, lead.x, out float mainStart)) return rows;
        if (lead.x - mainStart < .5f) return rows;
        rows.Add(new TrackRow { V=node.y, StraightStart=mainStart, StraightEnd=lead.x,
            Target=lead, Direction=Vector2.right, Side=0 });
        float minV=float.MaxValue, maxV=float.MinValue;
        foreach (Vector2 point in uv) { minV=Mathf.Min(minV,point.y); maxV=Mathf.Max(maxV,point.y); }
        for (int rank=1; rank<MAX_TRACKS && rows.Count<count; rank++)
        {
            foreach (int side in new[] { -1, 1 })
            {
                if (rows.Count>=count) break;
                float v=node.y+side*rank*TRACK_SPACING;
                if (v<minV+SIDE_MARGIN || v>maxV-SIDE_MARGIN) continue;
                // 左右が同じ一点へ同時合流する三方分岐を避ける。
                Vector2 merge=lead-Vector2.right*(side<0 ? 6f : 18f);
                Vector2 spineEnd=merge+new Vector2(-8f,side*2f);
                float distance=rank*TRACK_SPACING-ENTRY_OVERLAP/LADDER_RUN_PER_LATERAL;
                Vector2 target=spineEnd+new Vector2(-(distance-2f)*LADDER_RUN_PER_LATERAL,side*(distance-2f));
                Vector2 direction=new Vector2(LADDER_RUN_PER_LATERAL,-side).normalized;
                float end=target.x-ENTRY_OVERLAP-ENTRY_APPROACH;
                if (!TryGetRowStart(uv,v,end,out float start) || end-start<MIN_STRAIGHT_LENGTH) continue;
                if (merge.x<mainStart || merge.x>lead.x) continue;
                Vector2 p0=new Vector2(end,v);
                bool fits=true;
                for (int k=0; k<=64; k++)
                {
                    float t=k/64f;
                    if (!PolygonUtils.Contains(uv,PolylineUtils.CubicBezierPoint(p0,p0+Vector2.right*((target.x-end)*.4f),target-direction*ENTRY_OVERLAP,target,t),BOUNDARY_TOLERANCE)
                        || !PolygonUtils.Contains(uv,Vector2.Lerp(target,spineEnd,t),BOUNDARY_TOLERANCE)
                        || !PolygonUtils.Contains(uv,PolylineUtils.CubicBezierPoint(spineEnd,spineEnd+direction*3f,merge-Vector2.right*3f,merge,t),BOUNDARY_TOLERANCE))
                    { fits=false; break; }
                }
                if (fits) rows.Add(new TrackRow { V=v,StraightStart=start,StraightEnd=end,Target=target,
                    Direction=direction,Side=side,MergePoint=merge });
            }
        }
        foreach (int side in new[] { -1, 1 })
        {
            int outer=-1;
            for (int i=0; i<rows.Count; i++)
                if (rows[i].Side==side && (outer<0 || rows[i].Target.x<rows[outer].Target.x)) outer=i;
            if (outer>=0) { TrackRow row=rows[outer]; row.IsSpine=true; rows[outer]=row; }
        }
        rows.Sort((a,b)=>a.V.CompareTo(b.V));
        return rows;
    }

    /// <summary>行の終端を含む敷地内区間を選ぶ。凹区画の別の区間を横切らない。</summary>
    private static bool TryGetRowStart(Vector2[] polygon,float v,float end,out float start)
    {
        start=0f;
        // 境界上の水平辺は半開区間の交点計算から漏れるため、両側の微小オフセットも試す。
        foreach (float offset in new[] { 0f,-.001f,.001f })
        {
            float sampleV=v+offset;
            var hits=new List<float>();
            for (int i=0; i<polygon.Length; i++)
            {
                Vector2 a=polygon[i],b=polygon[(i+1)%polygon.Length];
                if ((a.y<=sampleV && b.y>sampleV)||(b.y<=sampleV && a.y>sampleV))
                    hits.Add(Mathf.Lerp(a.x,b.x,(sampleV-a.y)/(b.y-a.y)));
            }
            hits.Sort();
            for (int i=0; i+1<hits.Count; i+=2)
                if (end>=hits[i] && end<=hits[i+1]+.01f)
                {
                    start=hits[i]+END_MARGIN;
                    return true;
                }
        }
        return false;
    }

    /// <summary>端子から線路方向へ延びる引き込み線が、最初に敷地境界を横切る位置。</summary>
    private static Vector2 ResolveFenceOpening(Vector2[] polygon, Vector2 node, Vector2 direction)
    {
        float Cross(Vector2 a,Vector2 b) => a.x*b.y-a.y*b.x;
        float nearest=float.MaxValue;
        for (int i=0; i<polygon.Length; i++)
        {
            Vector2 a=polygon[i], edge=polygon[(i+1)%polygon.Length]-a;
            float denominator=Cross(direction,edge);
            if (Mathf.Abs(denominator)<.00001f) continue;
            float t=Cross(a-node,edge)/denominator;
            float u=Cross(a-node,direction)/denominator;
            if (t>=-.01f && u>=0f && u<=1f) nearest=Mathf.Min(nearest,Mathf.Max(0f,t));
        }
        return nearest<float.MaxValue ? node+direction*nearest : node;
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
            Vector2 point = PolylineUtils.CubicBezierPoint(p0, p1, p2, p3, i / (float)samples);
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
                Vector2 at = PolylineUtils.CubicBezierPoint(p0, p1, p2, p3, t);
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
                    // 細い単桁は通常線路の枕木幅を持たない。サブメッシュの実断面へ接続する。
                    if (edge > 0f && source.bounds.size.x < 2f)
                    {
                        edge = 0f;
                        Vector3[] authoredVertices = source.vertices;
                        foreach (int index in source.GetTriangles(sub))
                            edge = Mathf.Max(edge, Mathf.Abs(authoredVertices[index].x));
                    }
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
