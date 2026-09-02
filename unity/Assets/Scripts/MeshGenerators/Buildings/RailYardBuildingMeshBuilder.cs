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
///   2. 集約・接続ゾーン … 各線路の端から三次ベジエ曲線で端子ノードの手前
///      （LEAD_LENGTH だけ内側）の1点へ収束させ、そこから端子まで1本の直線線路
///      （リード線）を敷く。曲線・リード上にも架線を張る。
///
/// パーツはすべてスケールなしの実寸配置（yard_beam のみ柱間へXスケール）。
/// タイル系パーツ（yard_track / yard_wire）はローカル+Zが線路方向で、メッシュ bounds で
/// 再基準化して敷き詰めるため原点位置に依存しない。曲線区間は短い直線タイルを
/// 弦近似で並べる（CURVE_STEP_RATIO で重ねて隙間を隠す）。
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

    /// <summary>曲線区間の最小の走行方向長さ(m)。</summary>
    private const float CURVE_MIN_RUN = 12f;

    /// <summary>横ずれ量1mあたりの曲線走行長(m)。大きいほど曲線が緩くなる。</summary>
    private const float CURVE_RUN_PER_LATERAL = 2.2f;

    /// <summary>曲線区間のタイル送り（タイル実寸長に対する比率）。1未満で重ねて弦近似の隙間を隠す。</summary>
    private const float CURVE_STEP_RATIO = 0.85f;

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
        public bool HasCurve;       // 集約曲線を持つか
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

        List<TrackRow> rows = new List<TrackRow>();
        for (int i = 0; i < trackCount; i++)
        {
            float v = vCenter + (i - (trackCount - 1) * 0.5f) * TRACK_SPACING;
            if (!TryGetChordRange(uv, v, out float uStart, out float uEnd))
            {
                continue;
            }
            uStart += END_MARGIN;
            uEnd -= END_MARGIN;

            // 集約曲線に必要な走行長（横ずれが大きい行ほど長い）
            float run = Mathf.Max(CURVE_MIN_RUN, Mathf.Abs(v - nodeUV.y) * CURVE_RUN_PER_LATERAL);
            float straightEnd = Mathf.Min(uEnd, leadStartU - run);
            if (straightEnd - uStart < MIN_STRAIGHT_LENGTH)
            {
                continue;
            }
            rows.Add(new TrackRow
            {
                V = v,
                StraightStart = uStart,
                StraightEnd = straightEnd,
                HasCurve = straightEnd < leadStartU - 0.5f,
            });
        }

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

        // タイル1枚を「位置 p から方向 dir へ敷く」ための行列（bounds 始端を p に合わせる）
        Matrix4x4 TileMatrix(Vector3 pos, Quaternion rot, float minZ) =>
            Matrix4x4.TRS(pos - rot * new Vector3(0f, 0f, minZ), rot, Vector3.one);

        // --- 1. 並行線路ゾーン（直線タイル+車止め） ---
        foreach (TrackRow row in rows)
        {
            int n = Mathf.Max(1, Mathf.FloorToInt((row.StraightEnd - row.StraightStart) / tileLength));
            for (int t = 0; t < n; t++)
            {
                Vector3 pos = ToLocal3(new Vector2(row.StraightStart + t * tileLength, row.V));
                Matrix4x4 m = TileMatrix(pos, forwardRot, tileMinZ);
                AddInstances(trackMesh, trackMats, m, combineInstances, materials);
                if (wireMesh != null)
                {
                    AddInstances(wireMesh, wireMats, TileMatrix(pos, forwardRot, wireMinZ), combineInstances, materials);
                }
            }
            if (stopMesh != null)
            {
                // 車止めは行き止まり端に、+Z（著作の奥行き）が線路の内側を向くように置く
                Vector3 pos = ToLocal3(new Vector2(row.StraightStart, row.V));
                AddInstances(stopMesh, stopMats, Matrix4x4.TRS(pos, forwardRot, Vector3.one), combineInstances, materials);
            }
        }

        // --- 2. 集約・接続ゾーン（ベジエ曲線+リード線） ---
        Vector2 curveTarget = new Vector2(leadStartU, nodeUV.y);
        foreach (TrackRow row in rows)
        {
            if (!row.HasCurve)
            {
                continue;
            }
            // 曲線の始点は直線タイルの実際の敷き終わり（タイル長で切り捨てた位置）に
            // 合わせる。StraightEnd をそのまま使うと最大1タイル分の隙間が空く
            int straightTiles = Mathf.Max(1, Mathf.FloorToInt((row.StraightEnd - row.StraightStart) / tileLength));
            Vector2 p0 = new Vector2(row.StraightStart + straightTiles * tileLength, row.V);
            float d = Mathf.Max(1f, curveTarget.x - p0.x);
            Vector2 p1 = p0 + Vector2.right * (d * 0.4f);
            Vector2 p2 = curveTarget - Vector2.right * (d * 0.4f);
            PlaceTilesAlongBezier(p0, p1, p2, curveTarget,
                trackMesh, trackMats, wireMesh, wireMats,
                tileLength, tileMinZ, wireMinZ,
                ToLocal3, ToDir3, combineInstances, materials);
        }

        // リード線（収束点→端子ノード）。端子の少し先まで敷いて、溶接された引き込み線路と馴染ませる
        {
            int n = Mathf.Max(1, Mathf.CeilToInt(LEAD_LENGTH / tileLength));
            for (int t = 0; t < n; t++)
            {
                float u0 = leadStartU + t * tileLength;
                Vector3 pos = ToLocal3(new Vector2(Mathf.Min(u0, nodeUV.x - 0.1f), nodeUV.y));
                Matrix4x4 m = TileMatrix(pos, forwardRot, tileMinZ);
                AddInstances(trackMesh, trackMats, m, combineInstances, materials);
                if (wireMesh != null)
                {
                    AddInstances(wireMesh, wireMats, TileMatrix(pos, forwardRot, wireMinZ), combineInstances, materials);
                }
            }
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

        // --- 5. 敷地装飾（フェンス・地面パネル）。モジュラー/prebuilt/港湾と共通の処理 ---
        Material floorMaterial = BuildingMeshUtils.ResolveFloorMaterialFromMaster(buildingData, prefix);
        Vector2[] parcelPolygon = BuildingMeshUtils.ResolveParcelContext(
            buildingData, useParcelAltitude: false, out _, out _);
        Vector2[] sitePolygon = (parcelPolygon != null && parcelPolygon.Length >= 3) ? parcelPolygon : footprint;
        ModularBodyBuilder.GenerateSiteDecorations(
            prefix, 0,
            parcelPolygon, sitePolygon, sitePolygon,
            floorMaterial,
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

    /// <summary>
    /// 三次ベジエ曲線（UV座標）に沿って線路・架線タイルを弦近似で敷く。
    /// 曲線を32分割した折れ線で弧長テーブルを作り、タイル実寸長×CURVE_STEP_RATIO の
    /// 歩幅で位置・接線を求めて直線タイルを並べる（重なりで弦近似の隙間を隠す）。
    /// </summary>
    private static void PlaceTilesAlongBezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3,
        Mesh trackMesh, Material[] trackMats, Mesh wireMesh, Material[] wireMats,
        float tileLength, float tileMinZ, float wireMinZ,
        System.Func<Vector2, Vector3> toLocal3, System.Func<Vector2, Vector3> toDir3,
        List<CombineInstance> combineInstances, List<Material> materials)
    {
        const int SAMPLES = 32;
        Vector2[] pts = new Vector2[SAMPLES + 1];
        float[] len = new float[SAMPLES + 1];
        for (int i = 0; i <= SAMPLES; i++)
        {
            float t = (float)i / SAMPLES;
            pts[i] = BezierPoint(p0, p1, p2, p3, t);
            len[i] = i == 0 ? 0f : len[i - 1] + (pts[i] - pts[i - 1]).magnitude;
        }
        float total = len[SAMPLES];
        if (total < 0.5f)
        {
            return;
        }

        float step = tileLength * CURVE_STEP_RATIO;
        int seg = 1;
        for (float s = 0f; s < total - 0.25f; s += step)
        {
            while (seg < SAMPLES && len[seg] < s)
            {
                seg++;
            }
            float segLen = Mathf.Max(1e-4f, len[seg] - len[seg - 1]);
            float f = (s - len[seg - 1]) / segLen;
            Vector2 at = Vector2.Lerp(pts[seg - 1], pts[seg], f);
            Vector2 dir = (pts[seg] - pts[seg - 1]) / segLen;

            Quaternion rot = Quaternion.LookRotation(toDir3(dir), Vector3.up);
            Vector3 pos = toLocal3(at);
            AddInstances(trackMesh, trackMats,
                Matrix4x4.TRS(pos - rot * new Vector3(0f, 0f, tileMinZ), rot, Vector3.one),
                combineInstances, materials);
            if (wireMesh != null)
            {
                AddInstances(wireMesh, wireMats,
                    Matrix4x4.TRS(pos - rot * new Vector3(0f, 0f, wireMinZ), rot, Vector3.one),
                    combineInstances, materials);
            }
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
