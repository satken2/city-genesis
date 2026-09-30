using UnityEngine;
using System.Collections.Generic;
using Dreamteck.Splines;
using GeneratedModels;

/// <summary>
/// 駅舎パス(PATH_STATION_HOUSE, model_type="station_house")のメッシュ生成器。
///
/// 駅舎はウェイポイント付き折れ線のパスで、描画は次の2要素で構成される:
///   1. 通路: passage_modeで上空・地上・地下を選ぶ。地下は描画・帯コライダーなし。各アンカー点を「そのXZ直下の地形標高」に置き、アンカー間を
///      直線で結ぶ(地形追従の細分化はしない)。**デッキの地上高・橋脚は body プレハブの
///      造形が持つ**(原点=地面。実寸配置で幅も引き伸ばさない)。
///   2. 建屋: 選択した終端の専有エリア矩形に駅舎モデル(Models/Stations/{variant}/body)を置き、
///      矩形の範囲に地面パネル(floor パーツのマテリアル)を張る。矩形は終端から外側へ
///      広がる(outside)。insideは外側辺が線路外縁に合う位置へ移す。house_offsetは共通。**モデルの原点は
///      矩形の内側の辺の中点(modelOrigin。スライドが無ければパスの端点そのもの)に置く**
///      (奥行きの広がりも横ずれもプレハブの造形が持つため)。桁もこの位置まで伸ばす。
///
/// 専有エリア矩形の位置はバックエンド(geo_station_house.py)と同一の決定的アルゴリズムで
/// 算出する(ComputeEndFootprints)。バックエンド側を変更する場合は必ずここも揃えること。
///
/// クリック判定は他のパスと同じ PathObject(パス詳細)として受ける(駅=駅舎パスそのもので、
/// 専用の駅詳細ウィンドウは無い。駅セクションの表示はUI側のパス詳細パネルが担う)。
/// 橋の帯コライダーメッシュは PathMeshResult.Mesh として返し、PathManager が pathGO へ
/// 通常どおりコライダー・タグ・ハンドラを装着する。建屋はコライダーが子オブジェクト側に
/// あるため、pathGO と同じ三点セット(PathObject タグ・PathInputHandler・ObjectIdInfo)を
/// 建屋側にも付与する(InputHandler はヒットしたコライダーの GameObject 自身を見るため)。
/// </summary>
public class StationHousePath : IPathMeshGenerator
{
    private const string PATH_TAG = "PathObject";

    // 建屋プレハブ欠落時の仮箱の高さ(m)。仮箱のみスケール可(プレハブは実寸規約)。
    private const float FALLBACK_HOUSE_HEIGHT = 8f;

    // 建屋プレハブ欠落の警告を variant ごとに1回だけ出すためのデデュープ。
    private static readonly HashSet<int> missingHousePrefabWarnedVariants = new HashSet<int>();

    /// <summary>
    /// 専有エリア矩形(建屋フットプリント)。center/direction は XZ 平面(Vector2.y = z)。
    ///
    /// center は house_offset ぶん横へずらした後の矩形中心(=土地を占有する位置)。
    /// modelOrigin は矩形の内側の辺の中点で、**建屋プレハブの原点をここに置く**。
    /// 線路を避けるスライドが無ければ modelOrigin は駅舎パスの端点そのものであり、
    /// スライドした場合は連絡橋もこの位置まで伸びる。プレハブは奥行き・横ずれとも
    /// 造形が持つ前提なので、コード側で中心へ寄せたり house_offset を足したりしない。
    /// </summary>
    public struct StationHouseFootprint
    {
        public bool atEnd;
        public bool inside;
        public Vector2 center;
        public Vector2 modelOrigin;
        public Vector2 direction;
        public float rotationDeg;
        public float width;
        public float depth;
    }

    public PathMeshResult Generate(PathSummary pathData, List<Vector3> points, SidewalkSetback startSidewalkSetback = default, SidewalkSetback endSidewalkSetback = default)
    {
        if (points == null || points.Count < 2)
        {
            Debug.LogWarning($"[StationHousePath] ポイント不足 ({points?.Count ?? 0}件)。生成スキップ。");
            return new PathMeshResult(null, false);
        }

        RoadPath.GetPathMasterParams(pathData, out float width, out _, out _, out _, out _, out _, out _,
            out _, out _, out _, out bool singleSpan);

        GameObject container = new GameObject($"StationHouse_{pathData.path_id}");

        List<StationHouseFootprint> footprints = ComputeEndFootprints(pathData);

        // 地下通路には地上の描画・クリック面を作らず、建屋から駅を選択する。
        Mesh bridgeColliderMesh = null;
        if (pathData.passage_mode != "underground")
        {
            List<Vector3> anchors = BuildBridgeAnchorPoints(pathData, points, footprints);
            SplineComputer spline = RoadPath.BuildSpline(container, anchors, false);
            bool ground = pathData.passage_mode == "ground";
            string part = ground ? "passage_ground" : "passage_elevated";
            string bodyPath = $"Models/Paths/p{pathData.path_variant}/{part}";
            string bodyFallbackPath = $"Models/Paths/p{Constants.PATH_STATION_HOUSE}/{part}";

            // 既存の専用通路は対応する方式で使い、他の組み合わせは共通パーツへ解決する。
            bool existingBody = ground
                ? pathData.path_variant == 2004 || pathData.path_variant == 2007
                : pathData.path_variant == 2002 || pathData.path_variant == 2005;
            if (existingBody) bodyPath = $"Models/Paths/p{pathData.path_variant}/body";
            RoadPath.CreateSplineMesh(container, spline, bodyPath, width,
                0f, RoadPartHeights.STATION_BRIDGE_BASE_ABOVE_SPLINE, false, bodyFallbackPath,
                stretchToWidth: false, singleSpan: singleSpan, maxTileLength: 0f, normalizeBodySurface: false);

            bridgeColliderMesh = RoadPath.BuildColliderMeshFromSpline(spline, width);
            if (bridgeColliderMesh != null)
            {
                OffsetMeshY(bridgeColliderMesh, GetBodyTopHeight(bodyPath, bodyFallbackPath));
                bridgeColliderMesh.name = $"StationHouseBridgeCollider_{pathData.path_id}";
            }
        }

        // ---- 選択した終端の建屋 ----
        foreach (var footprint in footprints)
        {
            CreateHouseObject(container.transform, pathData, footprint);
        }

        return new PathMeshResult(bridgeColliderMesh, true, container);
    }

    // =====================================================================
    // 連絡橋
    // =====================================================================

    /// <summary>
    /// 連絡橋のスプラインアンカー点を構築する。各アンカー(始点・ウェイポイント・終点)を
    /// そのXZ直下の地形標高に置き、アンカー間は直線で結ぶ(地形追従の細分化は行わない)。
    /// デッキの地上高は body プレハブの造形が持つため、ここでは持ち上げない。
    /// アンカーが取得できない場合(縮退データ)は渡された点列を同じ規則で接地して使う。
    ///
    /// 専有エリアが線路を避けて外側へスライドした場合は、橋も建屋の取り付き位置
    /// (footprint.modelOrigin)まで延長する。insideは同じ取り付きへ通路端を移す。
    /// </summary>
    private static List<Vector3> BuildBridgeAnchorPoints(PathSummary pathData, List<Vector3> points, List<StationHouseFootprint> footprints)
    {
        var anchors = new List<Vector3>();

        var startList = pathData.GetStartPointList();
        if (startList != null && startList.Count >= 2)
        {
            anchors.Add(MakeDeckPoint(startList[0], startList[startList.Count - 1]));
        }

        var waypointList = pathData.GetWaypointList();
        if (waypointList != null)
        {
            foreach (var wp in waypointList)
            {
                if (wp.position == null || wp.position.Length < 2) continue;
                anchors.Add(MakeDeckPoint(wp.position[0], wp.position[wp.position.Length - 1]));
            }
        }

        var endList = pathData.GetEndPointList();
        if (endList != null && endList.Count >= 2)
        {
            anchors.Add(MakeDeckPoint(endList[0], endList[endList.Count - 1]));
        }

        if (anchors.Count < 2)
        {
            anchors.Clear();
            foreach (var p in points)
            {
                anchors.Add(MakeDeckPoint(p.x, p.z));
            }
        }

        // 片側だけでも端の識別を保つ。内側配置では通路を建屋の取り付きまで短縮する。
        if (anchors.Count >= 2 && footprints != null)
        {
            foreach (var footprint in footprints)
            {
                int index = footprint.atEnd ? anchors.Count - 1 : 0;
                Vector2 origin = footprint.modelOrigin;
                Vector3 point = MakeDeckPoint(origin.x, origin.y);
                if (footprint.inside) anchors[index] = point;
                else if (!IsSameXZ(anchors[index], origin))
                {
                    if (footprint.atEnd) anchors.Add(point);
                    else anchors.Insert(0, point);
                }
            }
        }
        return anchors;
    }

    private static bool IsSameXZ(Vector3 anchor, Vector2 xz)
    {
        return Mathf.Abs(anchor.x - xz.x) < 1e-3f && Mathf.Abs(anchor.z - xz.y) < 1e-3f;
    }

    private static Vector3 MakeDeckPoint(float x, float z)
    {
        float groundY = Utils.GetGroundHeight(new Vector3(x, 1000f, z));
        return new Vector3(x, groundY + RoadPartHeights.ROAD_SPLINE_OFFSET, z);
    }

    /// <summary>
    /// body プレハブのメッシュ天端(ローカルY最大値)を返す。桁の高さはプレハブ形状が
    /// 持つため、コライダーをデッキ位置へ持ち上げる量として使う。プレハブが無ければ0
    /// (フォールバックの白ストリップはスプライン面に描かれる)。
    /// </summary>
    private static float GetBodyTopHeight(string bodyPath, string fallbackPath)
    {
        if (ModelManager.Instance == null) return 0f;

        GameObject prefab = ModelManager.Instance.GetModel(bodyPath, suppressWarning: true)
            ?? ModelManager.Instance.GetModel(fallbackPath, suppressWarning: true);
        var mf = prefab != null ? prefab.GetComponent<MeshFilter>() : null;
        if (mf == null || mf.sharedMesh == null) return 0f;
        return Mathf.Max(0f, mf.sharedMesh.bounds.max.y);
    }

    /// <summary>メッシュの全頂点をY方向へ平行移動する。</summary>
    private static void OffsetMeshY(Mesh mesh, float offsetY)
    {
        if (mesh == null || Mathf.Approximately(offsetY, 0f)) return;

        Vector3[] vertices = mesh.vertices;
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].y += offsetY;
        }
        mesh.vertices = vertices;
        mesh.RecalculateBounds();
    }

    // =====================================================================
    // 建屋
    // =====================================================================

    /// <summary>
    /// 建屋を配置する。プレハブは Models/Stations/{variant}/body → Models/Stations/body の
    /// 順に解決し、スケールは一切かけない(建物パーツ実寸規約)。無ければ仮箱
    /// (house_width × FALLBACK_HOUSE_HEIGHT × house_depth。仮箱のみスケール可)で代替する。
    ///
    /// 配置位置は**プレハブが modelOrigin(矩形の内側の辺の中点=スライドが無ければ
    /// 駅舎パスの端点そのもの)、仮箱が矩形そのものの center**。プレハブは奥行き方向の
    /// 広がりも横ずれも造形が持つ前提なので原点をそのまま置き、仮箱は矩形の寸法を
    /// 可視化する代替表示なので矩形の位置に置く。
    /// Y はそれぞれの位置の地形標高に接地する。専有エリアの地面パネルは矩形の範囲に張る。
    /// </summary>
    private static void CreateHouseObject(Transform parent, PathSummary pathData, StationHouseFootprint footprint)
    {
        CreateFootprintFloor(parent, pathData, footprint);

        GameObject prefab = ResolveStationPart(pathData.path_variant, "body");

        GameObject go;
        Vector2 position;
        if (prefab != null)
        {
            go = Object.Instantiate(prefab, parent);
            position = footprint.modelOrigin;
        }
        else
        {
            WarnMissingHousePrefabOnce(pathData.path_variant);
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.SetParent(parent, false);
            go.transform.localScale = new Vector3(footprint.width, FALLBACK_HOUSE_HEIGHT, footprint.depth);
            position = footprint.center;
        }
        go.name = "House";

        float groundY = Utils.GetGroundHeight(new Vector3(position.x, 1000f, position.y));
        go.transform.localPosition = new Vector3(position.x, groundY, position.y);
        go.transform.localRotation = Quaternion.Euler(0f, footprint.rotationDeg, 0f);
        go.SetActive(true);

        EnsureHouseCollider(go);

        // コライダーを持つ全オブジェクトにパス詳細クリック用の三点セットを付ける
        // (InputHandler はヒットしたコライダーの GameObject 自身のタグ・ハンドラを見るため)。
        foreach (var col in go.GetComponentsInChildren<Collider>())
        {
            SetupPathClick(col.gameObject, pathData);
        }
    }

    /// <summary>
    /// 駅舎パーツを Models/Stations/{variant}/{partName} → Models/Stations/{partName} の順に
    /// 解決する。バリアント専用フォルダに置けばそれが優先され、無ければ全駅舎共通の
    /// Stations 直下のパーツを使う。どちらにも無ければ null。
    /// </summary>
    private static GameObject ResolveStationPart(int pathVariant, string partName)
    {
        if (ModelManager.Instance == null) return null;

        return ModelManager.Instance.GetModel($"Models/Stations/{pathVariant}/{partName}", suppressWarning: true)
            ?? ModelManager.Instance.GetModel($"Models/Stations/{partName}", suppressWarning: true);
    }

    /// <summary>
    /// 専有エリア矩形に地面パネルを張る。建物の地面パネル
    /// (F_建物メッシュの生成 §8.1)と同じ扱いで、floor パーツはマテリアルの供給元にすぎず、
    /// 面そのものはポリゴンから生成する。**floor パーツがどこにも無ければ描画しない**
    /// (既定マテリアルへはフォールバックしない=不要なスラブを敷かない)。
    /// 面は常に地形へ追従させる(専有エリアは数十mの矩形で、水平に張ると傾斜地で埋没・浮きが出る)。
    /// </summary>
    private static void CreateFootprintFloor(Transform parent, PathSummary pathData, StationHouseFootprint footprint)
    {
        GameObject floorPart = ResolveStationPart(pathData.path_variant, "floor");
        if (floorPart == null) return;

        Material[] materials = BuildingMeshUtils.GetModuleMaterials(floorPart);
        if (materials == null || materials.Length == 0 || materials[0] == null) return;

        // 矩形の4隅を「中心を原点とする回転なしのローカルXZ」で組み立てる
        // (ModularBodyBuilder の地面パネルと同じ座標規約)。頂点順はCCW
        // (耳切り分割とパネルの巻き順反転がCCW入力を前提とするため。CWで渡すと法線が下を向く)。
        Vector2 dir = footprint.direction;
        Vector2 halfDepth = dir * (footprint.depth / 2f);
        Vector2 halfWidth = RightAxis(dir) * (footprint.width / 2f);
        var polygon = new Vector2[]
        {
            halfWidth - halfDepth,
            halfWidth + halfDepth,
            -halfWidth + halfDepth,
            -halfWidth - halfDepth,
        };

        float groundY = Utils.GetGroundHeight(new Vector3(footprint.center.x, 1000f, footprint.center.y));
        Vector3 centerWorld = new Vector3(footprint.center.x, groundY, footprint.center.y);

        Mesh mesh = ModularBodyBuilder.GenerateTerrainConformingFloorMesh(polygon, centerWorld);
        if (mesh == null) return;
        mesh.name = $"StationHouseFloor_{pathData.path_id}";

        GameObject go = new GameObject("Floor");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = centerWorld;
        go.AddComponent<MeshFilter>().mesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = materials[0];
    }

    /// <summary>
    /// クリック判定用のコライダーを保証する。プレハブ側にコライダーが無い場合、
    /// レンダラーの合成バウンズから BoxCollider を付与する(Y軸回転のみの前提の近似で十分)。
    /// </summary>
    private static void EnsureHouseCollider(GameObject go)
    {
        if (go.GetComponentInChildren<Collider>() != null) return;

        var renderers = go.GetComponentsInChildren<Renderer>();
        var box = go.AddComponent<BoxCollider>();
        if (renderers.Length == 0) return;

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }
        box.center = go.transform.InverseTransformPoint(bounds.center);
        box.size = go.transform.InverseTransformVector(bounds.size);
    }

    /// <summary>
    /// クリックでパス詳細を開くための三点セット(PathObject タグ・PathInputHandler・
    /// ObjectIdInfo)を付与する。PathManager が pathGO に付けるものと同じ構成。
    /// </summary>
    private static void SetupPathClick(GameObject go, PathSummary pathData)
    {
        go.tag = PATH_TAG;

        var idInfo = go.GetComponent<ObjectIdInfo>();
        if (idInfo == null) idInfo = go.AddComponent<ObjectIdInfo>();
        idInfo.id = pathData.path_id;
        idInfo.status = pathData.status;
        idInfo.isFarObject = false;

        var handler = go.GetComponent<PathInputHandler>();
        if (handler == null) handler = go.AddComponent<PathInputHandler>();
        handler.pathId = pathData.path_id;
    }

    private static void WarnMissingHousePrefabOnce(int pathVariant)
    {
        if (missingHousePrefabWarnedVariants.Add(pathVariant))
        {
            Debug.LogWarning($"[StationHousePath] Prefab not found: Models/Stations/{pathVariant}/body (仮箱で代替表示します)");
        }
    }

    // =====================================================================
    // 専有エリア算出 (geo_station_house.py の移植)
    // =====================================================================

    /// <summary>
    /// 駅舎パスの選択した終端の専有エリア(建屋フットプリント)を返す。start側・end側の順で最大2件。
    ///
    /// バックエンド geo_station_house.station_house_end_rects と同一のアルゴリズム:
    /// 終端の外向き方向を s 軸とする1次元の区間回避問題として、周辺の軌道系パスの
    /// 占有区間を避ける最小の内側の辺の位置 s_in を決定的に解く。矩形は終端から
    /// 外側へ広がる(内側の辺が終端の上に乗る)ため、中心は s_in + 奥行き半分の位置で、
    /// さらに house_offset ぶん横(外向き方向を向いて右が正)へずれる。
    /// 敷設プレビューの仮 PathSummary(path_id=0)でも動作する(自パス除外は path_id が
    /// 一致するときだけスキップ)。
    /// </summary>
    public static List<StationHouseFootprint> ComputeEndFootprints(PathSummary pathData)
    {
        var result = new List<StationHouseFootprint>();

        GetHouseFootprintParams(pathData.path_variant, out float houseWidth, out float houseDepth, out float houseOffset);
        List<Vector2> anchors = PathAnchorPoints2D(pathData);
        if (anchors.Count < 2) return result;

        var pathManager = Object.FindAnyObjectByType<PathManager>();

        for (int endIndex = 0; endIndex < 2; endIndex++)
        {
            bool atEnd = endIndex == 1;
            string ends = pathData.house_ends ?? "both";
            if (ends != "both" && ends != (atEnd ? "end" : "start")) continue;
            if (!TryGetEndpointOutwardDir(anchors, atEnd, out Vector2 dir)) continue;
            Vector2 tip = atEnd ? anchors[anchors.Count - 1] : anchors[0];

            List<Vector2> intervals = CollectOccupiedIntervals(pathManager, tip, dir, houseWidth, houseOffset, pathData.path_id);
            bool inside = MasterDataStore.Instance?.GetPathMaster(pathData.path_variant)?.house_placement == "inside";
            float sIn = ResolveHousePosition(intervals, houseDepth, inside);
            Vector2 innerCenter = tip + dir * sIn;

            result.Add(new StationHouseFootprint
            {
                atEnd = atEnd,
                inside = inside,
                center = innerCenter + dir * (houseDepth / 2f) + RightAxis(dir) * houseOffset,
                modelOrigin = innerCenter,
                direction = dir,
                rotationDeg = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg,
                width = houseWidth,
                depth = houseDepth,
            });
        }
        return result;
    }

    /// <summary>
    /// 外向き方向 dir に対する右手方向(XZ平面)。rotationDeg で回した建屋のローカル+X軸に一致する。
    /// house_offset の正方向であり、矩形の横幅の張り出し方向でもある。
    /// </summary>
    private static Vector2 RightAxis(Vector2 dir) => new Vector2(dir.y, -dir.x);

    /// <summary>
    /// 駅舎バリアントの専有エリアのパラメータ (width, depth, offset) を返す(m)。
    /// width は終端セグメントと直交方向、depth は終端セグメントに沿う方向の寸法、
    /// offset は矩形を横方向へずらす量(外向き方向を向いて右が正)。
    /// マスターに寸法が無いときは既定値、オフセットが無いときは0にフォールバックする
    /// (バックエンド get_house_footprint_params と同一)。
    /// </summary>
    public static void GetHouseFootprintParams(int pathVariant, out float width, out float depth, out float offset)
    {
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(pathVariant) : null;
        width = (master != null && master.house_width.HasValue && master.house_width.Value > 0f)
            ? master.house_width.Value : Constants.STATION_HOUSE_DEFAULT_WIDTH;
        depth = (master != null && master.house_depth.HasValue && master.house_depth.Value > 0f)
            ? master.house_depth.Value : Constants.STATION_HOUSE_DEFAULT_DEPTH;
        offset = (master != null && master.house_offset.HasValue) ? master.house_offset.Value : 0f;
    }

    /// <summary>パスのアンカー点(始点・ウェイポイント・終点)を XZ の2Dリストで返す。</summary>
    private static List<Vector2> PathAnchorPoints2D(PathSummary pathData)
    {
        var points = new List<Vector2>();

        var startList = pathData.GetStartPointList();
        if (startList != null && startList.Count >= 2)
        {
            points.Add(new Vector2(startList[0], startList[startList.Count - 1]));
        }
        var waypointList = pathData.GetWaypointList();
        if (waypointList != null)
        {
            foreach (var wp in waypointList)
            {
                if (wp.position == null || wp.position.Length < 2) continue;
                points.Add(new Vector2(wp.position[0], wp.position[wp.position.Length - 1]));
            }
        }
        var endList = pathData.GetEndPointList();
        if (endList != null && endList.Count >= 2)
        {
            points.Add(new Vector2(endList[0], endList[endList.Count - 1]));
        }
        return points;
    }

    /// <summary>
    /// 終端の外向き単位ベクトルを返す。縮退(全点一致)なら false。
    /// 終端に一致する点が連続する場合は、距離が出る点まで遡って方向を決める。
    /// </summary>
    private static bool TryGetEndpointOutwardDir(List<Vector2> anchors, bool atEnd, out Vector2 dir)
    {
        dir = Vector2.zero;
        if (anchors.Count < 2) return false;

        Vector2 tip = atEnd ? anchors[anchors.Count - 1] : anchors[0];
        for (int i = 1; i < anchors.Count; i++)
        {
            Vector2 baseP = atEnd ? anchors[anchors.Count - 1 - i] : anchors[i];
            Vector2 d = tip - baseP;
            float norm = d.magnitude;
            if (norm > 1e-6f)
            {
                dir = d / norm;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 線路パスの中心線から左右それぞれへの占有半幅を返す(m)。線路コリドーの半幅に、
    /// プラットフォームの張り出し幅(TrackPath.ResolvePlatformExtents で解決)を
    /// 足した値で、バックエンドの敷地ポリゴンと同じ形を表す。左右は幾何の進行方向
    /// (始点→終点)基準(バックエンド _rail_side_extents と同一)。
    /// 単線の駅のホームは片側にしか無いため、両側に platform_width を見込むと
    /// ホームの無い側で常にホーム1本ぶんの無駄な隙間が空く(軌道数2の駅は両側にある)。
    /// </summary>
    private static void RailSideExtents(PathSummary rail, out float extLeft, out float extRight)
    {
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(rail.path_variant) : null;
        float half = ((master != null && master.width > 0f) ? master.width : 5f) / 2f;
        float platformWidth = (master != null && master.platform_width.HasValue) ? master.platform_width.Value : 0f;
        TrackPath.ResolvePlatformExtents(rail, platformWidth, out float platformLeft, out float platformRight);
        extLeft = half + platformLeft;
        extRight = half + platformRight;
    }

    /// <summary>
    /// 終端周辺の軌道系パスが s 軸(終端の外向き方向)上に占有する区間のリストを返す。
    /// 終端 tip を原点、direction を +s、その右手を +lat とする座標系で、専有エリアの
    /// 横幅の帯に掛かる線路セグメントを s 軸へ射影し、線路の占有帯のぶん広げた区間
    /// (x=s_min, y=s_max) として収集する
    /// (バックエンド _occupied_intervals と同一。セグメント供給は PathManager の空間ハッシュ)。
    ///
    /// 線路の占有帯は中心線から左右非対称(ホームのある側だけ広い)で、その広がりは
    /// 線路セグメントの右方向を s 軸・lat 軸へ射影した成分 (cs, cr) に応じて
    /// 各軸へ `占有半幅 × |成分|` として分配される。横方向コリドーは houseOffset ぶん
    /// ずれた矩形の位置を中心に取る。
    /// </summary>
    private static List<Vector2> CollectOccupiedIntervals(PathManager pathManager, Vector2 tip, Vector2 direction, float houseWidth, float houseOffset, int excludePathId)
    {
        var intervals = new List<Vector2>();
        if (pathManager == null) return intervals;

        Vector2 right = RightAxis(direction);
        float hw = houseWidth / 2f;
        float margin = Constants.STATION_HOUSE_TRACK_CLEARANCE_MARGIN;
        var segments = pathManager.QueryNearbySegments(
            new Vector3(tip.x, 0f, tip.y), Constants.STATION_HOUSE_TRACK_SEARCH_RADIUS);

        foreach (var seg in segments)
        {
            var rail = seg.path;
            if (rail == null) continue;
            if (rail.path_type != Constants.PATH_TRACK && rail.path_type != Constants.PATH_MONORAIL) continue;
            if (excludePathId != 0 && rail.path_id == excludePathId) continue;

            Vector2 u = new Vector2(seg.b.x - seg.a.x, seg.b.z - seg.a.z);
            if (u.magnitude < 1e-9f) continue;
            u.Normalize();

            RailSideExtents(rail, out float extLeft, out float extRight);
            // 線路セグメントの右方向。占有帯は左へ extLeft、右へ extRight 広がる。
            Vector2 railRight = new Vector2(u.y, -u.x);
            float cs = railRight.x * direction.x + railRight.y * direction.y;
            float cr = railRight.x * right.x + railRight.y * right.y;
            float extSPlus = (cs >= 0f ? extRight : extLeft) * Mathf.Abs(cs) + margin;
            float extSMinus = (cs >= 0f ? extLeft : extRight) * Mathf.Abs(cs) + margin;
            float extLatPlus = (cr >= 0f ? extRight : extLeft) * Mathf.Abs(cr) + margin;
            float extLatMinus = (cr >= 0f ? extLeft : extRight) * Mathf.Abs(cr) + margin;

            float la = (seg.a.x - tip.x) * right.x + (seg.a.z - tip.y) * right.y - houseOffset;
            float lb = (seg.b.x - tip.x) * right.x + (seg.b.z - tip.y) * right.y - houseOffset;
            float sa = (seg.a.x - tip.x) * direction.x + (seg.a.z - tip.y) * direction.y;
            float sb = (seg.b.x - tip.x) * direction.x + (seg.b.z - tip.y) * direction.y;

            // 占有帯が専有エリアの横幅に掛かる中心線の範囲 lat ∈ [lo, hi] へクリップする。
            float lo = -hw - extLatPlus;
            float hi = hw + extLatMinus;
            if ((la < lo && lb < lo) || (la > hi && lb > hi)) continue;

            float t0 = 0f, t1 = 1f;
            float dl = lb - la;
            if (Mathf.Abs(dl) > 1e-9f)
            {
                float tLo = (lo - la) / dl;
                float tHi = (hi - la) / dl;
                t0 = Mathf.Max(t0, Mathf.Min(tLo, tHi));
                t1 = Mathf.Min(t1, Mathf.Max(tLo, tHi));
            }
            if (t0 > t1) continue;

            float s0 = sa + (sb - sa) * t0;
            float s1 = sa + (sb - sa) * t1;
            intervals.Add(new Vector2(Mathf.Min(s0, s1) - extSMinus, Mathf.Max(s0, s1) + extSPlus));
        }
        return intervals;
    }

    /// <summary>区間リストを昇順マージする(バックエンド _merge_intervals と同一)。</summary>
    private static List<Vector2> MergeIntervals(List<Vector2> intervals)
    {
        var merged = new List<Vector2>();
        if (intervals.Count == 0) return merged;

        intervals.Sort((x, y) => x.x != y.x ? x.x.CompareTo(y.x) : x.y.CompareTo(y.y));
        merged.Add(intervals[0]);
        for (int i = 1; i < intervals.Count; i++)
        {
            Vector2 last = merged[merged.Count - 1];
            if (intervals[i].x <= last.y)
            {
                last.y = Mathf.Max(last.y, intervals[i].y);
                merged[merged.Count - 1] = last;
            }
            else
            {
                merged.Add(intervals[i]);
            }
        }
        return merged;
    }

    /// <summary>
    /// 専有エリアの内側の辺の位置 s_in を、線路占有区間を避ける最小値で返す。
    /// 矩形は s 軸上で [s_in, s_in + houseDepth] を占める(s_in=0 が「内側の辺が終端の上に
    /// 乗る」初期位置)。候補は「終端そのもの(s_in=0)」と「各占有区間の終端」で、
    /// 区間と重ならない最小の候補を採用する(バックエンド _resolve_slide_distance と同一)。
    /// </summary>
    public static float ResolveHousePosition(List<Vector2> intervals, float depth, bool inside)
    {
        if (!inside) return ResolveSlideDistance(intervals, depth);
        var merged = MergeIntervals(intervals);
        if (merged.Count == 0) return -depth;
        Vector2 nearest = merged[0];
        float distance = Mathf.Max(nearest.x, -nearest.y, 0f);
        foreach (var interval in merged)
        {
            float candidate = Mathf.Max(interval.x, -interval.y, 0f);
            if (candidate < distance || (candidate == distance && interval.y > nearest.y))
            {
                nearest = interval;
                distance = candidate;
            }
        }
        return nearest.y - Constants.STATION_HOUSE_TRACK_CLEARANCE_MARGIN - depth;
    }

    private static float ResolveSlideDistance(List<Vector2> intervals, float houseDepth)
    {
        List<Vector2> merged = MergeIntervals(intervals);

        var candidates = new List<float> { 0f };
        foreach (var interval in merged)
        {
            candidates.Add(interval.y);
        }
        candidates.Sort();

        foreach (float cand in candidates)
        {
            if (cand < 0f) continue;
            bool free = true;
            float lo = cand, hi = cand + houseDepth;
            foreach (var interval in merged)
            {
                if (lo < interval.y && hi > interval.x)
                {
                    free = false;
                    break;
                }
            }
            if (free) return cand;
        }
        return 0f;
    }
}
