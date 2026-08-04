using UnityEngine;
using System.Collections.Generic;
using Dreamteck.Splines;
using GeneratedModels;

/// <summary>
/// 駅舎パス(PATH_STATION_HOUSE, model_type="station_house")のメッシュ生成器。
///
/// 駅舎はウェイポイント付き折れ線のパスで、描画は次の2要素で構成される:
///   1. 連絡橋: 線に沿った桁。各アンカー点を「地形標高 + マスター height(橋デッキの地上高)」へ
///      持ち上げ、アンカー間を水平スパンで結ぶ(single_span と同じ構成)。柱(pillar)で支える。
///   2. 建屋: 両終端の専有エリア矩形に駅舎モデル(Models/Stations/{variant})を置く。
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

    // マスターに height が無い場合の橋デッキの地上高(m)。
    private const float DEFAULT_DECK_HEIGHT = 5f;

    // 建屋プレハブ欠落時の仮箱の高さ(m)。仮箱のみスケール可(プレハブは実寸規約)。
    private const float FALLBACK_HOUSE_HEIGHT = 8f;

    // 建屋プレハブ欠落の警告を variant ごとに1回だけ出すためのデデュープ。
    private static readonly HashSet<int> missingHousePrefabWarnedVariants = new HashSet<int>();

    /// <summary>
    /// 専有エリア矩形(建屋フットプリント)。center/direction は XZ 平面(Vector2.y = z)。
    /// </summary>
    public struct StationHouseFootprint
    {
        public Vector2 center;
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
            out float? pillarSpacing, out _, out _, out _);
        float deckHeight = GetDeckHeight(pathData.path_variant);

        GameObject container = new GameObject($"StationHouse_{pathData.path_id}");

        // ---- 連絡橋(桁) ----
        List<Vector3> anchors = BuildBridgeAnchorPoints(pathData, points, deckHeight);
        SplineComputer spline = RoadPath.BuildSpline(container, anchors, false);

        string variantBase = $"Models/Paths/p{pathData.path_variant}";
        string typeBase = $"Models/Paths/p{pathData.path_type}";

        GameObject bridgeBody = RoadPath.CreateSplineMesh(container, spline, $"{variantBase}/body", width,
            0f, RoadPartHeights.BODY_ABOVE_SPLINE, false, $"{typeBase}/body", singleSpan: true);
        if (bridgeBody == null)
        {
            bridgeBody = RoadPath.CreateFallbackRoadBody(container, spline, width);
        }

        if (pillarSpacing.HasValue && pillarSpacing.Value > 0f)
        {
            RoadPath.GeneratePillars(container, spline, pillarSpacing.Value, 0f, pathData, variantBase, typeBase);
        }

        // 橋の帯のクリック用コライダーメッシュ。他のパスと同様に PathMeshResult.Mesh として
        // 返し、PathManager が pathGO 側にコライダー・タグ・ハンドラを装着する。
        Mesh bridgeColliderMesh = RoadPath.BuildColliderMeshFromSpline(spline, width);
        if (bridgeColliderMesh != null)
        {
            bridgeColliderMesh.name = $"StationHouseBridgeCollider_{pathData.path_id}";
        }

        // ---- 両終端の建屋 ----
        foreach (var footprint in ComputeEndFootprints(pathData))
        {
            CreateHouseObject(container.transform, pathData, footprint);
        }

        return new PathMeshResult(bridgeColliderMesh, true, container);
    }

    // =====================================================================
    // 連絡橋
    // =====================================================================

    /// <summary>マスターの height(橋デッキの地上高)を返す。未定義は既定値。</summary>
    private static float GetDeckHeight(int pathVariant)
    {
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(pathVariant) : null;
        if (master != null && master.height.HasValue && master.height.Value > 0f)
        {
            return master.height.Value;
        }
        return DEFAULT_DECK_HEIGHT;
    }

    /// <summary>
    /// 連絡橋のスプラインアンカー点を構築する。各アンカー(始点・ウェイポイント・終点)を
    /// そのXZ直下の地形標高 + deckHeight へ持ち上げ、アンカー間は水平スパンで結ぶ
    /// (地形追従の細分化は行わない。single_span の桁橋と同じ考え方)。
    /// アンカーが取得できない場合(縮退データ)は渡された点列を同じ規則で持ち上げて使う。
    /// </summary>
    private static List<Vector3> BuildBridgeAnchorPoints(PathSummary pathData, List<Vector3> points, float deckHeight)
    {
        var anchors = new List<Vector3>();

        var startList = pathData.GetStartPointList();
        if (startList != null && startList.Count >= 2)
        {
            anchors.Add(MakeDeckPoint(startList[0], startList[startList.Count - 1], deckHeight));
        }

        var waypointList = pathData.GetWaypointList();
        if (waypointList != null)
        {
            foreach (var wp in waypointList)
            {
                if (wp.position == null || wp.position.Length < 2) continue;
                anchors.Add(MakeDeckPoint(wp.position[0], wp.position[wp.position.Length - 1], deckHeight));
            }
        }

        var endList = pathData.GetEndPointList();
        if (endList != null && endList.Count >= 2)
        {
            anchors.Add(MakeDeckPoint(endList[0], endList[endList.Count - 1], deckHeight));
        }

        if (anchors.Count < 2)
        {
            anchors.Clear();
            foreach (var p in points)
            {
                anchors.Add(MakeDeckPoint(p.x, p.z, deckHeight));
            }
        }
        return anchors;
    }

    private static Vector3 MakeDeckPoint(float x, float z, float deckHeight)
    {
        float groundY = HeightmapTextureManager.GetTerrainHeight(new Vector3(x, 1000f, z));
        return new Vector3(x, groundY + deckHeight, z);
    }

    // =====================================================================
    // 建屋
    // =====================================================================

    /// <summary>
    /// 建屋を専有エリア矩形の中心に配置する。プレハブは
    /// Models/Stations/{variant} → Models/Stations/default の順に解決し、
    /// スケールは一切かけない(建物パーツ実寸規約)。無ければ仮箱
    /// (house_width × FALLBACK_HOUSE_HEIGHT × house_depth。仮箱のみスケール可)で代替する。
    /// Y は矩形中心の地形標高に接地する。
    /// </summary>
    private static void CreateHouseObject(Transform parent, PathSummary pathData, StationHouseFootprint footprint)
    {
        GameObject prefab = null;
        if (ModelManager.Instance != null)
        {
            prefab = ModelManager.Instance.GetModel($"Models/Stations/{pathData.path_variant}", suppressWarning: true)
                ?? ModelManager.Instance.GetModel("Models/Stations/default", suppressWarning: true);
        }

        GameObject go;
        if (prefab != null)
        {
            go = Object.Instantiate(prefab, parent);
        }
        else
        {
            WarnMissingHousePrefabOnce(pathData.path_variant);
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.SetParent(parent, false);
            go.transform.localScale = new Vector3(footprint.width, FALLBACK_HOUSE_HEIGHT, footprint.depth);
        }
        go.name = "House";

        float groundY = HeightmapTextureManager.GetTerrainHeight(new Vector3(footprint.center.x, 1000f, footprint.center.y));
        go.transform.localPosition = new Vector3(footprint.center.x, groundY, footprint.center.y);
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
            Debug.LogWarning($"[StationHousePath] Prefab not found: Models/Stations/{pathVariant} (仮箱で代替表示します)");
        }
    }

    // =====================================================================
    // 専有エリア算出 (geo_station_house.py の移植)
    // =====================================================================

    /// <summary>
    /// 駅舎パスの両終端の専有エリア(建屋フットプリント)を返す。start側・end側の順で最大2件。
    ///
    /// バックエンド geo_station_house.station_house_end_rects と同一のアルゴリズム:
    /// 終端の外向き方向を s 軸とする1次元の区間回避問題として、周辺の軌道系パスの
    /// 占有区間を避ける最小の中心位置 s0 を決定的に解く。
    /// 敷設プレビューの仮 PathSummary(path_id=0)でも動作する(自パス除外は path_id が
    /// 一致するときだけスキップ)。
    /// </summary>
    public static List<StationHouseFootprint> ComputeEndFootprints(PathSummary pathData)
    {
        var result = new List<StationHouseFootprint>();

        GetHouseDimensions(pathData.path_variant, out float houseWidth, out float houseDepth);
        List<Vector2> anchors = PathAnchorPoints2D(pathData);
        if (anchors.Count < 2) return result;

        var pathManager = Object.FindAnyObjectByType<PathManager>();

        for (int endIndex = 0; endIndex < 2; endIndex++)
        {
            bool atEnd = endIndex == 1;
            if (!TryGetEndpointOutwardDir(anchors, atEnd, out Vector2 dir)) continue;
            Vector2 tip = atEnd ? anchors[anchors.Count - 1] : anchors[0];

            List<Vector2> intervals = CollectOccupiedIntervals(pathManager, tip, dir, houseWidth, pathData.path_id);
            float s0 = ResolveSlideDistance(intervals, houseDepth);

            result.Add(new StationHouseFootprint
            {
                center = tip + dir * s0,
                direction = dir,
                rotationDeg = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg,
                width = houseWidth,
                depth = houseDepth,
            });
        }
        return result;
    }

    /// <summary>
    /// 駅舎バリアントの専有エリア寸法 (width, depth) を返す(m)。
    /// width は終端セグメントと直交方向、depth は終端セグメントに沿う方向。
    /// マスターに寸法が無いときは既定値にフォールバックする(バックエンド get_house_dimensions と同一)。
    /// </summary>
    public static void GetHouseDimensions(int pathVariant, out float width, out float depth)
    {
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(pathVariant) : null;
        width = (master != null && master.house_width.HasValue && master.house_width.Value > 0f)
            ? master.house_width.Value : Constants.STATION_HOUSE_DEFAULT_WIDTH;
        depth = (master != null && master.house_depth.HasValue && master.house_depth.Value > 0f)
            ? master.house_depth.Value : Constants.STATION_HOUSE_DEFAULT_DEPTH;
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
    /// 線路パスの中心線からの占有半幅(クリアランス込み)を返す。
    /// 線路コリドーの半幅にプラットフォーム幅(存在すれば)と余白を足した保守的な値。
    /// platform_side による非対称は無視して両側に platform_width を見込む
    /// (バックエンド _rail_clearance と同一の近似。決定性を優先する)。
    /// </summary>
    private static float RailClearance(int railVariant)
    {
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(railVariant) : null;
        float half = ((master != null && master.width > 0f) ? master.width : 5f) / 2f;
        if (master != null && master.platform_width.HasValue && master.platform_width.Value > 0f)
        {
            half += master.platform_width.Value;
        }
        return half + Constants.STATION_HOUSE_TRACK_CLEARANCE_MARGIN;
    }

    /// <summary>
    /// 終端周辺の軌道系パスが s 軸(終端の外向き方向)上に占有する区間のリストを返す。
    /// 終端 tip を原点、direction を +s とする座標系で、横方向距離が
    /// 「専有エリア半幅 + 線路クリアランス」以内にある線路セグメントを s 軸へ射影し、
    /// クリアランスぶん広げた区間 (x=s_min, y=s_max) として収集する
    /// (バックエンド _occupied_intervals と同一。セグメント供給は PathManager の空間ハッシュ)。
    /// </summary>
    private static List<Vector2> CollectOccupiedIntervals(PathManager pathManager, Vector2 tip, Vector2 direction, float houseWidth, int excludePathId)
    {
        var intervals = new List<Vector2>();
        if (pathManager == null) return intervals;

        Vector2 perp = new Vector2(-direction.y, direction.x);
        var segments = pathManager.QueryNearbySegments(
            new Vector3(tip.x, 0f, tip.y), Constants.STATION_HOUSE_TRACK_SEARCH_RADIUS);

        foreach (var seg in segments)
        {
            var rail = seg.path;
            if (rail == null) continue;
            if (rail.path_type != Constants.PATH_TRACK && rail.path_type != Constants.PATH_MONORAIL) continue;
            if (excludePathId != 0 && rail.path_id == excludePathId) continue;

            float clearance = RailClearance(rail.path_variant);
            float latLimit = houseWidth / 2f + clearance;

            float la = (seg.a.x - tip.x) * perp.x + (seg.a.z - tip.y) * perp.y;
            float lb = (seg.b.x - tip.x) * perp.x + (seg.b.z - tip.y) * perp.y;
            float sa = (seg.a.x - tip.x) * direction.x + (seg.a.z - tip.y) * direction.y;
            float sb = (seg.b.x - tip.x) * direction.x + (seg.b.z - tip.y) * direction.y;

            // 横方向コリドー |lat| <= latLimit にセグメントをクリップする。
            if (la > latLimit && lb > latLimit) continue;
            if (la < -latLimit && lb < -latLimit) continue;

            float t0 = 0f, t1 = 1f;
            float dl = lb - la;
            if (Mathf.Abs(dl) > 1e-9f)
            {
                for (int boundIndex = 0; boundIndex < 2; boundIndex++)
                {
                    float bound = boundIndex == 0 ? latLimit : -latLimit;
                    float sign = boundIndex == 0 ? 1f : -1f;
                    float tCross = (bound - la) / dl;
                    if (sign * la > sign * bound) t0 = Mathf.Max(t0, tCross); // a が境界の外側
                    if (sign * lb > sign * bound) t1 = Mathf.Min(t1, tCross); // b が境界の外側
                }
            }
            if (t0 > t1) continue;

            float s0 = sa + (sb - sa) * t0;
            float s1 = sa + (sb - sa) * t1;
            intervals.Add(new Vector2(Mathf.Min(s0, s1) - clearance, Mathf.Max(s0, s1) + clearance));
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
    /// 専有エリア(奥行き houseDepth)の中心 s0 を、線路占有区間を避ける最小値で返す。
    /// 候補は「終端そのもの(s0=0)」と「各占有区間の終端 + 奥行き半分」で、
    /// 区間と重ならない最小の候補を採用する(バックエンド _resolve_slide_distance と同一)。
    /// </summary>
    private static float ResolveSlideDistance(List<Vector2> intervals, float houseDepth)
    {
        List<Vector2> merged = MergeIntervals(intervals);
        float half = houseDepth / 2f;

        var candidates = new List<float> { 0f };
        foreach (var interval in merged)
        {
            candidates.Add(interval.y + half);
        }
        candidates.Sort();

        foreach (float cand in candidates)
        {
            if (cand < 0f) continue;
            bool free = true;
            float lo = cand - half, hi = cand + half;
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
