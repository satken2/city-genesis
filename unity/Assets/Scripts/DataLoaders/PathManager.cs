using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Threading.Tasks;
using GeneratedModels;
using Dreamteck.Splines;
using NetTopologySuite.Algorithm;

/// <summary>
/// ノードとパスをまとめて管理する例。
/// セルごとに「ノード情報」「パス情報」を取得し、描画する。
/// </summary>
public partial class PathManager : BaseApiDataManager
{
    public int PendingTerrainRebuildCount => queuedTerrainFollowPathIds.Count;
    private readonly RenderObjectCache lodCache = new RenderObjectCache(128L * 1024 * 1024);
    private readonly HashSet<int> pendingPillarRebuildIds = new HashSet<int>();
    /// <summary>pendingPillarRebuildIds の最後の通知時点の pathBuildSerialCounter。</summary>
    private readonly Dictionary<int, long> pendingPillarRebuildSerials = new Dictionary<int, long>();
    /// <summary>pendingPillarRebuildIds のうち送電線の隣接通知を含むもの。遠景の送電線も作り直す必要があるので「橋脚だけ」扱いにしない。</summary>
    private readonly HashSet<int> pendingPowerRebuildIds = new HashSet<int>();
    private readonly Dictionary<int, int> geometryRevisions = new Dictionary<int, int>();
    private readonly Dictionary<int, (int version, int status)> terrainFollowCompletedPaths = new Dictionary<int, (int, int)>();
    private readonly HashSet<int> terrainFollowCompletedNodes = new HashSet<int>();
    private int terrainFollowNodeVersion;

    /// <summary>追従全体の完了・強制更新・セッション終了時に、中断再開用の記録を解放する。</summary>
    public void ClearTerrainFollowProgress()
    {
        terrainFollowCompletedPaths.Clear();
        terrainFollowCompletedNodes.Clear();
        terrainFollowNodeVersion = NodeDataVersion;
    }

    private void InvalidateTerrainFollowPath(PathSummary path)
    {
        if (path == null) return;
        QueueUndergroundPath(path.path_id);
        pathLabelPositions.Remove(path.path_id);
        anchorPolylineCache.Remove(path.path_id);
        if (terrainFollowCompletedPaths.Remove(path.path_id))
            RenderWorkScheduler.CountEvent("terrainFollow.pathInvalidated");
        terrainFollowCompletedNodes.Remove(path.start_node_id);
        terrainFollowCompletedNodes.Remove(path.end_node_id);
    }

    /// <summary>接地と交差点端部の依存範囲に余裕を持たせ、更新セルの周囲3×3だけ再処理可能にする。</summary>
    private void InvalidateTerrainFollowCell(Vector2Int cell)
    {
        for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++)
        {
            if (!pathIdsByCell.TryGetValue(cell + new Vector2Int(x, z), out var ids)) continue;
            foreach (int id in ids)
                if (pathDataCache.TryGetValue(id, out var path)) InvalidateTerrainFollowPath(path);
        }
    }

    private string PathRenderKey(PathSummary path, DrawLod lod, bool invalidate)
    {
        geometryRevisions.TryGetValue(path.path_id, out int revision);
        if (invalidate) geometryRevisions[path.path_id] = ++revision;
        return path.path_id + ":" + path.version + ":" + path.status + ":" + revision + ":" + lod + ":" + ConstructionPathBuilder.Progress(path);
    }

    private bool RestorePathLod(PathSummary path, string key, DrawLod lod, bool updateIntersections)
    {
        pathObjectsCache.TryGetValue(path.path_id, out GameObject old);
        GameObject next = old != null && old.GetComponent<RenderCacheIdentity>()?.key == key ? old : lodCache.Take(key);
        if (next != null && next != old && !IsCachedBuildTerrainCurrent(next, path))
        {
            // キーは地形署名を含まず、地形追従は表示中のGOしか見ないので、退避中に通過セルの地形が変わった版はここで捨てる。
            RenderWorkScheduler.CountEvent("path.cachedTerrainStale");
            Destroy(next);
            next = null;
        }
        if (next == null)
        {
            RenderWorkScheduler.CountEvent("path.buildRequested");
            return false;
        }
        RenderWorkScheduler.CountEvent(next == old ? "path.reuseActive" : "path.reuseCached");
        if (next != old)
        {
            DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Path,
                old != null ? DrawLodInfo.GetDrawLod(old, DrawLod.Normal) : (DrawLod?)null, lod);
            PathPresentation.Replace(next, old, lodCache);
        }
        SetPathObject(path.path_id, next);
        next.SetActive(true);
        // 退避中に非表示集合が変わっていることがあるので、生成時と同じくフィルタを引き直す。
        ObjectVisibilityFilter.ApplyToObject(next, InputObjectType.PATH, path.path_id);
        EnsureEndIntersectionsVisible(path);
        if (lod == DrawLod.Close) closePathIds.Add(path.path_id); else closePathIds.Remove(path.path_id);
        if (updateIntersections) RegisterPathData(path); else SavePathToCache(path);
        RegisterPathCells(path);
        next.GetComponent<VisualPathStats>()?.InitFrom(path);
        next.GetComponent<VisualModeObject>()?.SetStatus(path.status);
        UpdatePlannedIcon(next, path, path.status == Constants.STATUS_PENDING);
        ReevaluateAwaitingIntersectionsForPath(path);
        return true;
    }

    // ノード/ウェイポイントの表示切り替え用フラグ（公開プロパティ）
    private bool _showNodes = false;
    /// <summary>
    /// ノードの表示状態。setterで変更時に即座に既存オブジェクトへ反映するため、
    /// Updateでの毎フレームポーリングが不要。
    /// </summary>
    public bool ShowNodes
    {
        get => _showNodes;
        set
        {
            if (_showNodes == value) return;
            _showNodes = value;
            foreach (var obj in nodeObjectsCache.Values)
            {
                if (obj != null) obj.SetActive(value);
            }
        }
    }

    private bool _showWaypoints = false;
    /// <summary>
    /// ウェイポイントの表示状態。setterで変更時に即座に既存オブジェクトへ反映する。
    /// </summary>
    public bool ShowWaypoints
    {
        get => _showWaypoints;
        set
        {
            if (_showWaypoints == value) return;
            _showWaypoints = value;
            foreach (var kvp in pathObjectsCache)
            {
                GameObject pathObj = kvp.Value;
                if (pathObj == null) continue;

                if (value)
                {
                    bool hasWaypoint = false;
                    foreach (Transform child in pathObj.transform)
                    {
                        if (child.CompareTag(WAYPOINT_TAG))
                        {
                            hasWaypoint = true;
                            break;
                        }
                    }

                    if (!hasWaypoint && pathPointsCache.TryGetValue(kvp.Key, out List<Vector3> pts))
                    {
                        CreateWaypointsForPath(pathObj, pts);
                    }
                    else
                    {
                        foreach (Transform child in pathObj.transform)
                        {
                            if (child.CompareTag(WAYPOINT_TAG))
                                child.gameObject.SetActive(true);
                        }
                    }
                }
                else
                {
                    foreach (Transform child in pathObj.transform)
                    {
                        if (child.CompareTag(WAYPOINT_TAG))
                            child.gameObject.SetActive(false);
                    }
                }
            }
        }
    }

    // 命名規則の定数
    private const string NODE_PREFIX = "Node_";
    private const string PATH_PREFIX = "Path_";
    private const string WAYPOINT_NAME = "Waypoint";
    
    // タグ名の定数
    private const string NODE_TAG = "PathNode";
    private const string PATH_TAG = "PathObject";
    private const string WAYPOINT_TAG = "PathWaypoint";

    // Dynamic地形の1m格子の起伏を拾い、サンプル間の尾根による路面の貫通を抑える。
    public const float TERRAIN_SAMPLE_INTERVAL = 1f;



    // ==== マテリアルキャッシュ ====
    private static Material cachedNodeMaterial;
    private static Material cachedWaypointMaterial;
    private static Material cachedRoadMaterial;

    /// <summary>
    /// Resources.Loadによるマテリアル取得をキャッシュして呼び出しコストを削減する。
    /// </summary>
    private static Material GetNodeMaterial()
    {
        if (cachedNodeMaterial == null)
            cachedNodeMaterial = Resources.Load<Material>("NodeMaterial");
        return cachedNodeMaterial;
    }

    private static Material GetWaypointMaterial()
    {
        if (cachedWaypointMaterial == null)
            cachedWaypointMaterial = Resources.Load<Material>("WaypointMaterial");
        return cachedWaypointMaterial;
    }

    /// <summary>
    /// 遠景（Far）ティアの道路描画に使用する黒色マテリアル。
    /// </summary>
    private static Material GetRoadMaterial()
    {
        if (cachedRoadMaterial == null)
            cachedRoadMaterial = Resources.Load<Material>("Road");
        return cachedRoadMaterial;
    }

    private static Material farRoadDepthMaterial;
    private static Material nodeDepthMaterial;

    /// <summary>遠景の帯(Far道路・詳細区間外の残り帯)に使う、Road の DepthOffsetLit 版の共有マテリアル。</summary>
    private static Material GetFarRoadDepthMaterial() => GetDepthOffsetMaterial(GetRoadMaterial(), ref farRoadDepthMaterial);

    /// <summary>ノードの円柱に使う、NodeMaterial の DepthOffsetLit 版の共有マテリアル。</summary>
    private static Material GetNodeDepthMaterial() => GetDepthOffsetMaterial(GetNodeMaterial(), ref nodeDepthMaterial);

    /// <summary>
    /// シェーダーを Z-fighting 防止用の Custom/DepthOffsetLit に差し替えた共有マテリアルを1度だけ作る。
    /// Renderer.materials で個別に複製すると誰も解放せず蓄積するため、パス・ノード間で共有する。
    /// 色の上書きは VisualModeObject がマテリアル差し替えとプロパティブロックで行うので、共有しても他へ波及しない。
    /// シェーダーが無ければ元の共有マテリアルをそのまま使う。
    /// </summary>
    private static Material GetDepthOffsetMaterial(Material source, ref Material cache)
    {
        if (cache != null) return cache;
        if (source == null) return null;
        Shader shader = Shader.Find("Custom/DepthOffsetLit");
        if (shader == null)
        {
            Debug.LogWarning("PathManager: Custom/DepthOffsetLit shader not found");
            cache = source;
        }
        else cache = source.shader == shader ? source : new Material(source) { name = source.name + " (DepthOffset)", shader = shader };
        return cache;
    }

    // ==== Spatial Hash ====
    private SpatialHash<NodeData> nodeGrid;
    private SpatialHash<PathSegment> segmentGrid;
    [SerializeField]
    private float gridCellSize = 5f; // Inspectorでも変更可
    /// <summary>segmentGrid を作ったときのセル寸法。実行中に Inspector で変えても登録と削除の点列を一致させるため固定する。</summary>
    private float segmentGridCellSize = 5f;
    
    private Dictionary<int, NodeData> nodeDataCache = new Dictionary<int, NodeData>();
    private Dictionary<int, PathSummary> pathDataCache = new Dictionary<int, PathSummary>();
    private Dictionary<int, HashSet<int>> pathsByNodeIndex = new Dictionary<int, HashSet<int>>();
    private Dictionary<int, GameObject> nodeIntersectionMeshes = new Dictionary<int, GameObject>();
    private readonly Dictionary<int, string> nodeMarkingSignatures = new();
    /// <summary>ノードごとの線路のS字の受け持ちの署名(TrackTransitionSignature)。変わったら前後の線路と並走する線路も結合をやり直す。</summary>
    private readonly Dictionary<int, string> nodeTransitionSignatures = new();

    /// <summary>ノードを持たない線路(計画中・着工保留中・建設中)の端点。仮のつながりを位置で探す索引に入れる(Y は使わない)。</summary>
    private sealed class ProvisionalTrackEnd
    {
        public int PathId;
        public bool AtStart;
        public Vector3 Position;
    }
    private SpatialHash<ProvisionalTrackEnd> provisionalEndGrid;
    private readonly Dictionary<int, List<ProvisionalTrackEnd>> provisionalEndsByPath = new();

    /// <summary>敷設プレビューの線(負の path_id。RoadRenderer.PreviewTrackSink から登録される)と、その描画点列。</summary>
    private readonly List<PathSummary> previewTracks = new();
    private readonly Dictionary<int, List<Vector3>> previewTrackPoints = new();
    /// <summary>形状を編集中の計画。プレビューと元の計画が互いを相手にしないよう、仮の相手からも隣接候補からも外す。</summary>
    private int previewExcludedPathId;
    private ulong previewTrackSignature;

    private static readonly System.Reflection.MethodInfo MemberwiseCloneMethod =
        typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>
    /// nodeDataCache のセル逆引き。セル単位の同期・アンロードで、プールに残る全ノードを走査せずに
    /// 対象セルのノードGO・交差点メッシュだけを引く。nodeDataCache の追加・座標変更・削除と一緒に更新する。
    /// </summary>
    private readonly Dictionary<Vector2Int, HashSet<int>> nodeIdsByCell = new Dictionary<Vector2Int, HashSet<int>>();
    private readonly Dictionary<int, Vector2Int> nodeCellById = new Dictionary<int, Vector2Int>();

    /// <summary>
    /// pathObjectsCache にあるのに通過セル(pathCellsById)が未登録のパスID。セル逆引きで拾えないGOだけを
    /// 位置判定に回し、プール全体を走査しないために使う。
    /// </summary>
    private readonly HashSet<int> unregisteredPathObjectIds = new HashSet<int>();

    /// <summary>
    /// 交差点メッシュを最後に生成できたときの入力署名(ComputeNodeIntersectionSignature)。
    /// 署名が一致し交差点GOが生きていれば、UpdateNodeIntersectionMesh は計算をやり直さない。
    /// 生成に失敗したとき・交差点を消したとき・接続道路を削除したときは捨てる。
    /// </summary>
    private readonly Dictionary<int, long> nodeIntersectionSignatures = new Dictionary<int, long>();

    /// <summary>
    /// LODバッチ中に新規作成し、交差点計算をバッチの交差点フェーズへ回したノード。バッチが取り消されると
    /// 遅延集合ごと失われ、既存ノードの再受信では交差点を計算しないため、次に受信したときにここから回収する。
    /// </summary>
    private readonly HashSet<int> deferredNewNodeIntersectionIds = new HashSet<int>();

    // GameObject Cache
    private Dictionary<int, GameObject> nodeObjectsCache = new Dictionary<int, GameObject>();
    private Dictionary<int, GameObject> pathObjectsCache = new Dictionary<int, GameObject>();

    /// <summary>
    /// バックエンドから取得したパスのversionをキャッシュする。
    /// 差分更新時にバックエンドのversionと比較し、変更があった場合のみオブジェクトを再作成する。
    /// </summary>
    private Dictionary<int, int> pathVersionCache = new Dictionary<int, int>();

    // パスの接続点キャッシュ (PathID -> Point)
    // 始点側と終点側で分ける
    private Dictionary<int, Vector3> pathStartConnectionPoints = new Dictionary<int, Vector3>();
    private Dictionary<int, Vector3> pathEndConnectionPoints = new Dictionary<int, Vector3>();
    private readonly Dictionary<int, MotorwayMergeLayout> motorwayMerges = new Dictionary<int, MotorwayMergeLayout>();

    /// <summary>板を持たない合流ノード(本線をノードでつなぎ、ランプを本線の路面へ取り込む)。</summary>
    private readonly HashSet<int> slablessMergeNodes = new HashSet<int>();

    public MotorwayMergeLayout GetMotorwayMerge(int nodeId)
        => motorwayMerges.TryGetValue(nodeId, out var layout) ? layout : null;

    // パスの歩道セットバックキャッシュ (PathID -> SidewalkSetback)。
    // 交差相手に歩道がない交差点では、直線側の歩道だけを車道の切断面より手前で
    // 終わらせ（clip）、空いた区間に交差点側がエンドキャップを敷く。
    // 接続点と同じく、交差点メッシュ生成の結果（NodeMeshResult.SidewalkSetbacks）から更新され、
    // 道路メッシュ生成（PathMeshFactory.GeneratePathMesh）に渡される。
    private Dictionary<int, SidewalkSetback> pathStartSidewalkSetbacks = new Dictionary<int, SidewalkSetback>();
    private Dictionary<int, SidewalkSetback> pathEndSidewalkSetbacks = new Dictionary<int, SidewalkSetback>();

    // パスごとの登録済みセグメント（再生成時に古いセグメントを削除するため）
    private Dictionary<int, List<PathSegment>> pathSegmentsByPathId = new Dictionary<int, List<PathSegment>>();
    private Dictionary<Vector2Int, LodTier> cellPathLod = new Dictionary<Vector2Int, LodTier>();
    private Dictionary<Vector2Int, HashSet<int>> pathIdsByCell = new Dictionary<Vector2Int, HashSet<int>>();
    private Dictionary<int, HashSet<Vector2Int>> pathCellsById = new Dictionary<int, HashSet<Vector2Int>>();
    private Dictionary<int, List<Vector3>> pathPointsCache = new Dictionary<int, List<Vector3>>();
    private readonly Dictionary<int, Vector3> pathLabelPositions = new();

    /// <summary>
    /// カメラ移動によるLOD更新中は TerrainManager.OnTerrainTileReady がセルごとに発火する。
    /// ここで道路を即時再生成すると、同じ道路が地形セルの枚数ぶん再生成されてチラつくため、
    /// CellDataLoader が地形ロード完了後に行う changedCells ベースの一括更新へ集約する。
    /// </summary>
    private bool deferTerrainTilePathRebuilds = false;

    /// <summary>
    /// defer 中に受信して破棄した OnTerrainTileReady のセル。
    /// EndDeferredTerrainTilePathRebuilds で回収して呼び出し元(CellDataLoader)に返し、
    /// 追従更新の未処理集合(pendingFollowUpCells)へ合流させる。
    /// 通常は地形ロード完了後の一括追従で処理済みのセルばかりだが、
    /// バッチのキャンセルタイミングによる取りこぼしへの防御として記録する。
    /// </summary>
    private readonly HashSet<Vector2Int> deferredTerrainReadyCells = new HashSet<Vector2Int>();

    private bool isLodPathBatchActive = false;
    private HashSet<int> lodBatchRebuiltPathIds = new HashSet<int>();
    private HashSet<int> deferredIntersectionPathRebuildIds = new HashSet<int>();
    /// <summary>deferredIntersectionPathRebuildIds に積んだ時点の生成通し番号。バッチが完了せず終わったとき再生成キューへ移すのに使う。</summary>
    private readonly Dictionary<int, long> deferredIntersectionRebuildSerials = new Dictionary<int, long>();
    private HashSet<int> deferredLodIntersectionNodeIds = new HashSet<int>();
    /// <summary>
    /// 進行中のLODバッチが作り直す可能性のあるパス。フェーズ1で受信した全IDを入れ、フェーズ2で再生成不要と
    /// 分かったIDを外す。地形追従キューはバッチ中これらを取り出さない。
    /// </summary>
    private readonly HashSet<int> lodBatchPendingBuildIds = new HashSet<int>();
    /// <summary>LODロードの取消し世代。取消されたバッチの一括取得は通信・デコードを打ち切る(ApiClient.GetBulk の isCanceled)。</summary>
    private int lodFetchGeneration;

    /// <summary>
    /// LOD一括ロード(LoadMergedPathsAndNodesAndApplyCells)が進行中かどうか。
    /// 一括ロード中は TryRebuildPathOnce が「このバッチで生成済みの道路」を
    /// lodBatchRebuiltPathIds で重複再生成スキップするため、地形ロード完了後の
    /// 地形追従再生成(CellDataLoader.TerrainFollowUpCoroutine)がこの間に走ると、
    /// 地形適用前に生成された(高さ0や誤った高さの)道路の再生成が握りつぶされる。
    /// CellDataLoader はこのフラグを見て、一括ロードの完了を待ってから地形追従を行う。
    /// </summary>
    public bool IsLodBatchActive => isLodPathBatchActive;

    /// <summary>
    /// 交差点生成に必要な接続パスがまだ揃っていないノードIDの集合。
    /// セル境界などで接続パスが一部未ロードの場合に登録され、
    /// 後からパスが追加された際に自動的に交差点を再評価する。
    /// </summary>
    private HashSet<int> awaitingIntersectionNodes = new HashSet<int>();

    /// <summary>
    /// 交差点生成のために個別取得中のノードID。
    /// 同じノードの取得コルーチンが複数同時に走ると、完了順によって古い交差点更新が後から反映されるため、
    /// ノード単位で取得を1本に集約する。
    /// </summary>
    private HashSet<int> fetchingIntersectionNodeIds = new HashSet<int>();

    /// <summary>
    /// 1ノードの交差点メッシュ生成を許容する接続パス数の上限。
    /// 交差点ポリゴン計算（IntersectionPolygonUtil）は接続パスの全ペアで交差領域を取る
    /// O(N²)処理のため、バックエンドのデータ異常で1ノードに大量のパスが接続していると
    /// 計算がメインスレッドを占有しエディタごとフリーズする。正常な交差点は高々十数本
    /// なので、上限超過は異常データとみなして交差点メッシュの生成を諦める。
    /// </summary>
    private const int MAX_INTERSECTION_CONNECTED_PATHS = 32;

    /// <summary>
    /// 接続パス数の上限超過を警告済みのノードID。同一ノードへの再警告スパムを防ぐ。
    /// </summary>
    private HashSet<int> overloadedIntersectionNodeIds = new HashSet<int>();

    /// <summary>
    /// LoadLodCells で起動したコルーチンを追跡し、新たなカメラ移動時にキャンセルする。
    /// </summary>
    private List<Coroutine> activeLodCoroutines = new List<Coroutine>();

    [Header("計画中アイコン設定")]
    [Tooltip("計画中(status=3)の道路に表示する2Dアイコン")]
    public Sprite plannedPathIcon;
    [Tooltip("アイコンの大きさ")]
    public float plannedPathIconScale = 1f;
    [Tooltip("アイコンを地面からどれだけ浮かせるか")]
    public float plannedPathIconYOffset = 2f;

    /// <summary>
    /// セル跨ぎのパスを誤って非表示にしないため、アンロード判定時に
    /// 「残すべきセル」として参照するセル集合。
    /// LoadLodCells の先頭で設定され、UnloadCellsObjects で使用される。
    /// </summary>
    private HashSet<Vector2Int> cellsToKeepVisible = new HashSet<Vector2Int>();

    private void Awake()
    {
        InitializeUnderground();
        if (GetComponent<PathSpeedLimitOverlay>() == null) gameObject.AddComponent<PathSpeedLimitOverlay>();
        nodeGrid = new SpatialHash<NodeData>(gridCellSize);
        provisionalEndGrid = new SpatialHash<ProvisionalTrackEnd>(gridCellSize);
        segmentGridCellSize = Mathf.Max(gridCellSize, 0.0001f);
        segmentGrid = new SpatialHash<PathSegment>(segmentGridCellSize);

        TerrainManager.OnTerrainTileReady += OnTerrainTileReadyHandler;

        // 橋脚の干渉判定と近接線路の結合描画に、周辺パスの点列を供給する。
        // キャッシュ済みパスデータとセル空間インデックスが必要なためPathManagerが担う。
        PillarClearance.NeighborProvider = GetPillarObstacles;
        TrackAdjacency.NeighborProvider = GetAdjacentTrackNeighbors;
        RoadAdjacency.NeighborProvider = GetAdjacentRoadNeighbors;
        RoadAdjacency.EndJoinProvider = GetRoadEndJoin;
        TrackPath.ConnectedPathsProvider = GetTrackPathsAtNode;
        RoadRenderer.PreviewTrackSink = SetPreviewTracks;
        RoadMarkingRules.ConnectedPaths = GetPathsConnectedToNode;
        RoadMarkingRules.IsApproachEndpoint = (path, start, point) =>
        {
            var connections = start ? pathStartConnectionPoints : pathEndConnectionPoints;
            return connections.TryGetValue(path.path_id, out var cut) && Vector3.Distance(cut, point) < 1f;
        };
        PowerLinePath.ConnectedPaths = GetPathsConnectedToNode;
        PowerLineRailGantry.TrackPolyline = GetPowerLineTrackPolyline;
        TrackPath.InfrastructureConnectionProvider = IsInfrastructureConnection;
        TrackPath.LateralProfileProvider = GetTrackLateralProfile;
        LrtPlatformParts.PolylineProvider = GetLrtPlatformPolyline;
        BuildingRoadAccessIndex.Changed += OnBuildingRoadAccessChanged;
    }

    /// <summary>LRT停留所のホームの範囲を、車両と同じ未切断点列と切断位置で解決するための点列。</summary>
    private List<Vector3> GetLrtPlatformPolyline(int pathId, bool trimIntersection) =>
        TryGetPathPolyline(pathId, out var points, trimIntersection) ? points : null;

    private List<Vector3> GetPowerLineTrackPolyline(PathSummary path) => CalculatePathPoints(path,false);

    private void Start()
    {
        // 都市の通行方向(traffic_hand)を取得する。駅パスのプラットフォーム設置側
        // (platform_side の内側/外側)の左右解決に TrackPath が参照する。
        CityInfoStore.EnsureLoaded(this);
    }

    private void OnDestroy()
    {
        DisposeUnderground();
        BuildingRoadAccessIndex.Changed -= OnBuildingRoadAccessChanged;
        if (RoadMarkingRules.ConnectedPaths == (Func<int, List<PathSummary>>)GetPathsConnectedToNode)
        {
            RoadMarkingRules.ConnectedPaths = null;
            RoadMarkingRules.IsApproachEndpoint = null;
            RoadMarkingAtlas.Clear();
        }
        lodCache.Clear();
        if (PowerLinePath.ConnectedPaths == (Func<int, List<PathSummary>>)GetPathsConnectedToNode)
            PowerLinePath.ConnectedPaths = null;
        if (PowerLineRailGantry.TrackPolyline == (Func<PathSummary, List<Vector3>>)GetPowerLineTrackPolyline)
            PowerLineRailGantry.TrackPolyline = null;
        if (PillarClearance.NeighborProvider == (Func<PathSummary, List<PillarClearance.Corridor>>)GetPillarObstacles)
            PillarClearance.NeighborProvider = null;
        if (LrtPlatformParts.PolylineProvider == (Func<int, bool, List<Vector3>>)GetLrtPlatformPolyline)
            LrtPlatformParts.PolylineProvider = null;
        TerrainManager.OnTerrainTileReady -= OnTerrainTileReadyHandler;

        if (TrackAdjacency.NeighborProvider == (System.Func<PathSummary, List<TrackNeighborInfo>>)GetAdjacentTrackNeighbors)
        {
            TrackAdjacency.NeighborProvider = null;
            TrackPath.ConnectedPathsProvider = null;
            TrackPath.InfrastructureConnectionProvider = null;
            TrackPath.LateralProfileProvider = null;
        }
        if (RoadRenderer.PreviewTrackSink == (Action<List<PathSummary>, List<List<Vector3>>, int>)SetPreviewTracks)
        {
            RoadRenderer.PreviewTrackSink = null;
        }
        if (RoadAdjacency.NeighborProvider == (Func<PathSummary, List<RoadNeighborInfo>>)GetAdjacentRoadNeighbors)
        {
            RoadAdjacency.NeighborProvider = null;
            RoadAdjacency.EndJoinProvider = null;
        }
    }

    /// <summary>
    /// 施設の接続端子(インフラ施設の端子・車両基地の端子)か。施設内へ軌道や管が続くので、線路の車止めも交差点も置かない。
    /// ノードの種別ビットでは判定しない(電化線路のノードはどれも電力ビットを持つ)。端子は建物側の逆引きで解くので、
    /// 施設が未ロードの間は端子と分からない(建物→パスの順に読むので通常は先に揃う)。
    /// </summary>
    private bool IsInfrastructureConnection(int nodeId)
    {
        if (BuildingManager.Instance == null || nodeId <= 0) return false;
        return BuildingManager.Instance.TryGetInfraTerminalOwner(nodeId, out _) || BuildingManager.Instance.IsDepotTerminalNode(nodeId);
    }

    /// <summary>
    /// ノードに交差点(板・レールのつなぎ・端の切断)が要るか。交通のパス(インフラのパスと人員搬送を除く)が
    /// 2本以上つながり、施設の接続端子でないノードだけ。
    /// </summary>
    private bool NeedsIntersection(int nodeId, List<PathSummary> connectedPaths)
    {
        int transport = 0;
        foreach (var path in connectedPaths)
            if (!InfraNetworkUtil.IsInfraPathType(path.path_type) && path.path_type != Constants.PATH_CONVEYOR) transport++;
        return transport >= 2 && !IsInfrastructureConnection(nodeId);
    }

    /// <summary>
    /// 線路の軌道の横位置の形(TrackEndTransition のS字)。両端のノードにつながるパスから求める。
    /// 線路のメッシュ(TrackPath)・車両の走行描画・近接線路の結合判定が同じ形を使う。線路以外は null。
    /// </summary>
    public TrackLateralProfile GetTrackLateralProfile(PathSummary path)
    {
        if (path == null || !TrackIntersectionMeshGenerator.IsTrackType(path.path_type)) return null;
        PathSummary self = path;
        List<PathSummary> start = ResolveTrackEnd(ref self, true);
        List<PathSummary> end = ResolveTrackEnd(ref self, false);
        return TrackEndTransition.BuildProfile(self, start, end);
    }

    /// <summary>
    /// 線路の端でつながるパスの一覧(自分を含む)。実ノードがあればそのノードのつながり(仮の線路を含む。GetTrackPathsAtNode)。
    /// ノードを持たない端(計画中・建設中・プレビュー)は、竣工時の溶接と同じ規則で位置から探す。つながる先の実ノードがあれば
    /// その番号を、無ければ仮の線路どうしの合流点の仮の番号を、self の写しの端へ当てる。
    /// </summary>
    private List<PathSummary> ResolveTrackEnd(ref PathSummary self, bool atStart)
    {
        int nodeId = atStart ? self.start_node_id : self.end_node_id;
        if (nodeId > 0) return GetTrackPathsAtNode(nodeId);
        if (!TryGetTrackEndPosition(self, atStart, out Vector3 position)) return null;
        int selfId = self.path_id;
        NodeData node = FindWeldNode(position);
        if (node != null)
        {
            self = WithEndNode(self, atStart, node.node_id);
            var paths = GetTrackPathsAtNode(node.node_id);
            paths.RemoveAll(p => p.path_id == selfId);
            paths.Add(self);
            return paths;
        }
        int junction = ProvisionalJunctionId(position);
        self = WithEndNode(self, atStart, junction);
        var ends = new List<(PathSummary path, bool atStart)>();
        CollectProvisionalEnds(position, selfId, ends);
        if (ends.Count == 0) return null;
        var result = new List<PathSummary> { self };
        foreach (var (other, otherAtStart) in ends) result.Add(WithEndNode(other, otherAtStart, junction));
        return result;
    }

    /// <summary>
    /// ノードにつながるパス(実際のつながり)に、端点がノードの位置にある仮の線路(ノードを持たない計画中・着工保留中・建設中と
    /// 敷設プレビュー)を、そのノードにつながる写しとして加えたもの。線路の車止め・S字の判定に使う(TrackPath.ConnectedPathsProvider)。
    /// 仮の線路との交差点の板・レールの接続は作らない(竣工して溶接されたときに作られる)。
    /// </summary>
    private List<PathSummary> GetTrackPathsAtNode(int nodeId)
    {
        var result = GetPathsConnectedToNode(nodeId);
        if (nodeId <= 0) return result;
        NodeData node = GetNodeData(nodeId);
        if (node == null) return result;
        var ends = new List<(PathSummary path, bool atStart)>();
        CollectProvisionalEnds(node.GetAbsolutePosition(), int.MinValue, ends);
        foreach (var (path, atStart) in ends)
        {
            int id = path.path_id;
            if (!result.Exists(p => p.path_id == id)) result.Add(WithEndNode(path, atStart, nodeId));
        }
        return result;
    }

    /// <summary>端の位置(アンカーの水平位置と、地面+相対高さの絶対標高)。</summary>
    private static bool TryGetTrackEndPosition(PathSummary path, bool atStart, out Vector3 position)
    {
        position = default;
        var anchor = atStart ? path.GetStartPointList() : path.GetEndPointList();
        if (anchor == null || anchor.Count < 3) return false;
        var ground = new Vector3(anchor[0], 0f, anchor[2]);
        position = new Vector3(anchor[0], Utils.GetGroundHeight(ground) + anchor[1], anchor[2]);
        return true;
    }

    /// <summary>
    /// 位置に溶接される実ノード(線路がつながるノードのうち、竣工時の溶接と同じ距離・高さの差に収まる最寄りのもの)。
    /// コアの find_nearby_node(RAIL_NODE_SNAP_THRESHOLD と標高差)の鏡写し。
    /// </summary>
    private NodeData FindWeldNode(Vector3 position)
    {
        NodeData best = null;
        float bestDistance = float.MaxValue;
        foreach (NodeData node in QueryNearbyNodes(position, LocalConstants.PROVISIONAL_TRACK_WELD_DISTANCE))
        {
            Vector3 nodePosition = node.GetAbsolutePosition();
            float distance = Vector2.Distance(new Vector2(nodePosition.x, nodePosition.z), new Vector2(position.x, position.z));
            if (distance > LocalConstants.PROVISIONAL_TRACK_WELD_DISTANCE || distance >= bestDistance) continue;
            if (Mathf.Abs(nodePosition.y - position.y) >= LocalConstants.PROVISIONAL_TRACK_WELD_HEIGHT) continue;
            if (!GetPathsConnectedToNode(node.node_id).Exists(IsTrackTypePath)) continue;
            best = node;
            bestDistance = distance;
        }
        return best;
    }

    /// <summary>
    /// 位置にある仮の線路の端(ノードを持たない計画中・建設中の線路と敷設プレビュー)。excludePathId と、形状を編集中の計画は除く。
    /// </summary>
    private void CollectProvisionalEnds(Vector3 position, int excludePathId, List<(PathSummary path, bool atStart)> result)
    {
        float tolerance = LocalConstants.PROVISIONAL_TRACK_WELD_DISTANCE;
        bool Matches(PathSummary path, bool atStart)
        {
            if (!TryGetTrackEndPosition(path, atStart, out Vector3 end)) return false;
            return Vector2.Distance(new Vector2(end.x, end.z), new Vector2(position.x, position.z)) <= tolerance
                && Mathf.Abs(end.y - position.y) < LocalConstants.PROVISIONAL_TRACK_WELD_HEIGHT;
        }
        if (provisionalEndGrid != null)
        {
            foreach (var end in provisionalEndGrid.Query(new Vector3(position.x, 0f, position.z), tolerance))
            {
                if (end.PathId == excludePathId || (previewExcludedPathId > 0 && end.PathId == previewExcludedPathId)) continue;
                if (!pathDataCache.TryGetValue(end.PathId, out PathSummary path) || !Matches(path, end.AtStart)) continue;
                result.Add((path, end.AtStart));
            }
        }
        foreach (PathSummary preview in previewTracks)
        {
            if (preview.path_id == excludePathId) continue;
            for (int end = 0; end < 2; end++)
                if (Matches(preview, end == 0)) result.Add((preview, end == 0));
        }
    }

    /// <summary>実ノードの無い、仮の線路どうしの合流点の番号。位置から決まる負の値(実ノードの番号・未設定の -1 と重ならない)。</summary>
    private static int ProvisionalJunctionId(Vector3 position)
    {
        int hash = (Mathf.RoundToInt(position.x * 2f) * 73856093) ^ (Mathf.RoundToInt(position.z * 2f) * 19349663);
        return -2 - (hash & 0x3FFFFFFF);
    }

    /// <summary>パスの写しの片端に、つながるノードの番号を当てる(仮のつながりを、ノードで判定する既存の処理へ通すため)。</summary>
    private static PathSummary WithEndNode(PathSummary path, bool atStart, int nodeId)
    {
        var clone = (PathSummary)MemberwiseCloneMethod.Invoke(path, null);
        if (atStart) clone.start_node_id = nodeId;
        else clone.end_node_id = nodeId;
        return clone;
    }

    /// <summary>ノードを持たない線路(計画中・着工保留中・建設中)の端点を索引に入れる。それ以外の線路は索引から外す。</summary>
    private void IndexProvisionalTrackEnds(PathSummary path)
    {
        UnindexProvisionalTrackEnds(path.path_id);
        if (provisionalEndGrid == null || !IsTrackTypePath(path) || !TrackAdjacency.IsProvisional(path)) return;
        var ends = new List<ProvisionalTrackEnd>(2);
        for (int end = 0; end < 2; end++)
        {
            bool atStart = end == 0;
            if ((atStart ? path.start_node_id : path.end_node_id) > 0) continue;
            var anchor = atStart ? path.GetStartPointList() : path.GetEndPointList();
            if (anchor == null || anchor.Count < 3) continue;
            var item = new ProvisionalTrackEnd { PathId = path.path_id, AtStart = atStart, Position = new Vector3(anchor[0], 0f, anchor[2]) };
            provisionalEndGrid.Add(item, item.Position);
            ends.Add(item);
        }
        if (ends.Count > 0) provisionalEndsByPath[path.path_id] = ends;
    }

    private void UnindexProvisionalTrackEnds(int pathId)
    {
        if (!provisionalEndsByPath.TryGetValue(pathId, out var ends)) return;
        foreach (var end in ends) provisionalEndGrid?.Remove(end, end.Position);
        provisionalEndsByPath.Remove(pathId);
    }

    /// <summary>
    /// 敷設プレビューの線を仮の相手として登録する(RoadRenderer.PreviewTrackSink)。tracks が null なら消す。
    /// 形(点列・バリアント・向き・除外する計画)が前回と同じなら何もしない。変わったら、前後のプレビューの近くの線路を
    /// 作り直しの列に積む(S字のつながりと並走の縫い合わせを描き直す)。excludedPathId は形状を編集中の計画(無ければ0)。
    /// </summary>
    private void SetPreviewTracks(List<PathSummary> tracks, List<List<Vector3>> points, int excludedPathId)
    {
        ulong signature = PreviewTrackSignature(tracks, points, excludedPathId);
        if (signature == previewTrackSignature) return;
        var affected = new HashSet<int>();
        foreach (PathSummary old in previewTracks) affected.UnionWith(CollectNearbyTrackPathIds(old));
        previewTracks.Clear();
        previewTrackPoints.Clear();
        if (tracks != null && points != null)
        {
            for (int i = 0; i < tracks.Count && i < points.Count; i++)
            {
                if (tracks[i] == null || points[i] == null || points[i].Count < 2) continue;
                previewTracks.Add(tracks[i]);
                previewTrackPoints[tracks[i].path_id] = points[i];
            }
        }
        foreach (PathSummary track in previewTracks) affected.UnionWith(CollectNearbyTrackPathIds(track));
        if (previewExcludedPathId != excludedPathId)
        {
            foreach (int id in new[] { previewExcludedPathId, excludedPathId })
                if (id > 0 && pathDataCache.TryGetValue(id, out PathSummary edited))
                {
                    affected.Add(id);
                    affected.UnionWith(CollectNearbyTrackPathIds(edited));
                }
        }
        previewExcludedPathId = excludedPathId;
        previewTrackSignature = signature;
        foreach (int id in affected) QueueTrackNeighborRebuild(id);
        FlushPendingTrackNeighborRebuilds(null);
    }

    /// <summary>プレビューの線の形の署名(0.1m 単位)。0 は登録なし。</summary>
    private static ulong PreviewTrackSignature(List<PathSummary> tracks, List<List<Vector3>> points, int excludedPathId)
    {
        if (tracks == null || tracks.Count == 0) return 0;
        ulong h = SignatureMix((ulong)(uint)excludedPathId ^ 0x9E3779B97F4A7C15UL);
        for (int i = 0; i < tracks.Count; i++)
        {
            PathSummary track = tracks[i];
            if (track == null) continue;
            h = SignatureMix(h ^ (uint)track.path_id ^ ((ulong)(uint)track.path_variant << 32));
            h = SignatureMix(h ^ (uint)track.travel_direction ^ ((ulong)(uint)track.platform_side << 32));
            if (points == null || i >= points.Count || points[i] == null) continue;
            foreach (Vector3 point in points[i])
                h = SignatureMix(h ^ (uint)Mathf.RoundToInt(point.x * 10f) ^ ((ulong)(uint)Mathf.RoundToInt(point.z * 10f) << 32) ^ (ulong)(uint)Mathf.RoundToInt(point.y * 10f) << 16);
        }
        return h == 0 ? 1 : h;
    }

    /// <summary>ノードの線路のS字の受け持ち(パスごとの受け持ち長さ・全長・横ずれ)の署名。S字が無ければ空。</summary>
    private static string TrackTransitionSignature(int nodeId, List<PathSummary> connectedPaths)
    {
        var shifts = TrackEndTransition.ResolveNode(nodeId, connectedPaths);
        if (shifts.Count == 0) return "";
        var signature = new System.Text.StringBuilder("|S");
        var ids = new List<int>(shifts.Keys);
        ids.Sort();
        foreach (int id in ids)
        {
            var shift = shifts[id];
            signature.Append('|').Append(id).Append(':').Append(Mathf.RoundToInt(shift.Own * 100)).Append(':').Append(Mathf.RoundToInt(shift.Span * 100));
            foreach (float delta in shift.Delta) signature.Append(':').Append(Mathf.RoundToInt(delta * 1000));
        }
        return signature.ToString();
    }

    // ================================================================
    // 地形追従再構築の視野優先キュー
    // ================================================================

    /// <summary>
    /// 道路再構築の視野優先判定に使う水平半角(度)と、カメラ近傍の無条件視野内距離(m)。
    /// 建物側(BuildingManager)と同じ基準に揃える。
    /// </summary>
    private const float PATH_VIEW_HALF_ANGLE_DEG = 75f;
    private const float PATH_VIEW_NEAR_DISTANCE = 100f;

    /// <summary>視野外のパスに加算する処理順ペナルティ。視野内(距離のみ)より必ず後になる。</summary>
    private const float PATH_OUT_OF_VIEW_PENALTY = 100000f;

    /// <summary>
    /// 地形タイル更新イベント(OnTerrainTileReadyHandler)起因のパス再生成キュー。
    /// 1本の再生成はスプライン＋SplineMesh群の同期生成で数msかかるため、
    /// ハンドラ内で同期一括再生成せず、TerrainFollowRebuildWorker が
    /// 視野優先の順序・フレーム予算制で消化する。
    /// </summary>
    private readonly HashSet<int> queuedTerrainFollowPathIds = new HashSet<int>();
    /// <summary>queuedTerrainFollowPathIds のうち、投入理由が地形タイル到着だけの項目。地形が生成時と同じなら省略できる。</summary>
    private readonly HashSet<int> terrainOnlyQueuedPathIds = new HashSet<int>();
    /// <summary>queuedTerrainFollowPathIds のうち、投入理由が橋脚の周辺変化だけの項目。橋脚を描かない遠景なら省略できる。</summary>
    private readonly HashSet<int> queuedPillarOnlyIds = new HashSet<int>();
    /// <summary>
    /// 投入時点の生成通し番号(投入が重なったときは最大値)。GOの PathBuildStamp.buildSerial がこれより大きければ、
    /// 投入の原因を反映した後に作り直されているので再生成を省く。
    /// </summary>
    private readonly Dictionary<int, long> queuedTerrainFollowSerials = new Dictionary<int, long>();

    /// <summary>道路GOを実際に生成するたびに進める通し番号。セッションをまたいでも戻さない(単調増加が前提)。</summary>
    private long pathBuildSerialCounter;

    private enum TerrainFollowQueueReason { Terrain, Pillar, Other }

    /// <summary>
    /// 再生成キューの処理順(視野優先スコアの昇順)。投入のたびに並べ替えると件数の2乗になるため、
    /// 末尾に達したとき・古くなって一定フレーム経ったとき・カメラが大きく動いたときだけまとめて並べ替える。
    /// キューから消えたIDは取り出し時に飛ばす。
    /// </summary>
    private List<(float score, int id)> terrainFollowOrder = new List<(float score, int id)>();
    private List<(float score, int id)> terrainFollowOrderScratch = new List<(float score, int id)>();
    private readonly List<int> terrainFollowOrderIds = new List<int>();
    private int terrainFollowOrderCursor;
    private bool terrainFollowOrderStale = true;
    private int terrainFollowOrderFrame;
    private Vector3 terrainFollowOrderCamPos;
    private Vector3 terrainFollowOrderCamFwd = Vector3.forward;
    private const int TERRAIN_FOLLOW_REORDER_FRAMES = 10;
    private const float TERRAIN_FOLLOW_REORDER_MOVE = 50f;
    private const float TERRAIN_FOLLOW_REORDER_ANGLE = 20f;
    private static readonly Comparison<(float score, int id)> TerrainFollowOrderComparison =
        (a, b) => a.score != b.score ? a.score.CompareTo(b.score) : a.id.CompareTo(b.id);

    /// <summary>地形追従再生成キューを消化する常駐ワーカー。最初の投入時に起動する。</summary>
    private Coroutine terrainFollowRebuildWorker;

    /// <summary>
    /// 地形追従再生成キューへの唯一の投入口。changeSerial は原因を適用した後の pathBuildSerialCounter。
    /// 「地形だけ」「橋脚だけ」の印は新規投入のときだけ付け、別の理由の投入が重なったら外す(省略は常に安全側)。
    /// </summary>
    private void EnqueueTerrainFollowPath(int id, long changeSerial, TerrainFollowQueueReason reason)
    {
        bool added = queuedTerrainFollowPathIds.Add(id);
        if (added || !queuedTerrainFollowSerials.TryGetValue(id, out long queuedSerial) || queuedSerial < changeSerial)
            queuedTerrainFollowSerials[id] = changeSerial;
        if (added && reason == TerrainFollowQueueReason.Terrain) terrainOnlyQueuedPathIds.Add(id);
        else if (reason != TerrainFollowQueueReason.Terrain) terrainOnlyQueuedPathIds.Remove(id);
        if (added && reason == TerrainFollowQueueReason.Pillar) queuedPillarOnlyIds.Add(id);
        else if (reason != TerrainFollowQueueReason.Pillar) queuedPillarOnlyIds.Remove(id);
        terrainFollowOrderStale = true;
        EnsureTerrainFollowRebuildWorker();
    }

    private void ClearTerrainFollowQueue()
    {
        queuedTerrainFollowPathIds.Clear();
        terrainOnlyQueuedPathIds.Clear();
        queuedPillarOnlyIds.Clear();
        queuedTerrainFollowSerials.Clear();
        terrainFollowOrder.Clear();
        terrainFollowOrderCursor = 0;
        terrainFollowOrderStale = true;
    }

    // ================================================================
    // 描画LOD(パス1本ごとの詳細度。セルのデータティアとは独立)
    // ================================================================

    /// <summary>カメラ移動に応じてパスの描画LODを再判定する常駐ワーカー。最初のLODロードで起動する。</summary>
    private Coroutine pathDrawLodWorker;

    /// <summary>接写メッシュを持つパスIDの集合。DrawLodConfig.MAX_CLOSE_PATHS の母集団。</summary>
    private readonly HashSet<int> closePathIds = new HashSet<int>();

    /// <summary>描画LODを前回判定したときのカメラ位置。一定距離動いたときだけ再判定する。</summary>
    private Vector3 lastPathDrawLodCameraPos = new Vector3(float.NaN, 0f, 0f);

    /// <summary>
    /// 描画LOD判定用のアンカー点列のキャッシュ。source が pathDataCache の現在のインスタンスと
    /// 同一参照のときだけ有効とし、データ差し替え・地形タイル到着(InvalidateTerrainFollowPath)・
    /// アンロードで捨てる。アンカーのYは地形高さを含むため、地形更新前の値が残らないようにする。
    /// </summary>
    private sealed class AnchorEntry
    {
        public PathSummary source;
        public List<Vector3> anchors;
        public float length;
    }
    private readonly Dictionary<int, AnchorEntry> anchorPolylineCache = new Dictionary<int, AnchorEntry>();

    /// <summary>
    /// パスの望ましい描画LODと詳細描画区間を決める。
    /// データティア(tier)には依らず、アンカー点列(始点・経由点・終点)への最近接距離で
    /// Close/Normal/Far を決める(ヒステリシスは既存オブジェクトの描画LODを基準にする)。
    /// ルートのクリップ範囲は全長とし、長い道路の細分化は固定区間ワーカーに任せる。
    /// </summary>
    private DrawLod DeterminePathDrawLod(PathSummary path, LodTier tier, Vector3 camPos, out double detailFrom, out double detailTo)
    {
        detailFrom = 0.0;
        detailTo = 1.0;

        List<Vector3> anchors = GetAnchorPolyline(path, out float length);
        if (anchors.Count < 2) return DrawLod.Normal;

        DrawLod current = DrawLod.Normal;
        if (pathObjectsCache.TryGetValue(path.path_id, out GameObject existing) && existing != null)
        {
            current = DrawLodInfo.GetDrawLod(existing, DrawLod.Normal);
        }
        float minDist = DrawLodEvaluator.MinDistanceToPolyline(anchors, camPos);
        bool allowClose = closePathIds.Contains(path.path_id) || closePathIds.Count < DrawLodConfig.MAX_CLOSE_PATHS;
        DrawLod lod = DrawLodEvaluator.EvaluatePathByDistance(minDist, current, allowClose);
        if (lod != DrawLod.Close) return lod;

        // 路面本体を SplinePartLod の固定区間で描く道路は親を通常LODに固定し、Close/Normal の切替を区間に任せる
        // (親を作り直すと全区間・設備・橋脚を再生成するため)。区間化は交差点で切った描画長・工事中・トンネル分割で
        // 決まるので、生成済みなら実物を見る。遠景の帯は区間を持たないため、初回と同じく推定で決める。
        if (existing != null && current != DrawLod.Far) return HasBodySegments(existing) ? DrawLod.Normal : DrawLod.Close;
        return length >= 256f && IsSegmentedLongRoad(path) ? DrawLod.Normal : DrawLod.Close;
    }

    private static readonly List<SplinePartLod> splinePartLodBuffer = new List<SplinePartLod>();

    private static bool HasBodySegments(GameObject pathGO)
    {
        pathGO.GetComponentsInChildren(true, splinePartLodBuffer);
        bool found = splinePartLodBuffer.Exists(part => part.IsBody);
        splinePartLodBuffer.Clear();
        return found;
    }

    /// <summary>
    /// 未生成のパスについて、路面本体が SplinePartLod の固定区間に分かれる通常道路かを推定する。
    /// 条件は RoadPath.CreateSplineMesh が body を区間化する条件(base_height<=0・single_span でない・
    /// 詳細区間を抑止する角付き折れ線でない)に揃える。線路(model_type=track)は対象外。
    /// 外れても、生成後の再判定で実物(HasBodySegments)に合わせて1回作り直すだけで収束する。
    /// </summary>
    private static bool IsSegmentedLongRoad(PathSummary path)
    {
        if (PathMeshFactory.GetModelType(path) != "default") return false;
        if (PowerLinePath.Handles(path) || RoadPath.IsRopewayVariant(path.path_variant)) return false;
        if (path.status == Constants.STATUS_CONSTRUCTING) return false;
        var master = MasterDataStore.Instance?.GetPathMaster(path.path_variant);
        if ((master?.single_span ?? false) || (master?.base_height ?? 0f) > 0f) return false;
        return path.curved || (path.waypoints?.Count ?? 0) == 0;
    }

    /// <summary>
    /// アンカー点列と、その3D折れ線長を返す。返すリストはキャッシュと共有するので読み取り専用として扱う。
    /// pathDataCache に登録中のインスタンス以外(受信直後の新データなど)は保存せず毎回作る。
    /// </summary>
    private List<Vector3> GetAnchorPolyline(PathSummary path, out float length)
    {
        if (anchorPolylineCache.TryGetValue(path.path_id, out AnchorEntry entry) && ReferenceEquals(entry.source, path))
        {
            length = entry.length;
            return entry.anchors;
        }
        List<Vector3> anchors = BuildAnchorPolyline(path);
        length = 0f;
        for (int i = 1; i < anchors.Count; i++) length += Vector3.Distance(anchors[i-1], anchors[i]);
        if (pathDataCache.TryGetValue(path.path_id, out PathSummary cached) && ReferenceEquals(cached, path))
        {
            entry ??= new AnchorEntry();
            entry.source = path;
            entry.anchors = anchors;
            entry.length = length;
            anchorPolylineCache[path.path_id] = entry;
        }
        return anchors;
    }

    /// <summary>始点・経由点・終点をワールド座標で結んだアンカー点列(描画LOD判定用の粗い形状)。</summary>
    private List<Vector3> BuildAnchorPolyline(PathSummary path)
    {
        var result = new List<Vector3>();
        var startList = path.GetStartPointList();
        var endList = path.GetEndPointList();
        if (startList == null || startList.Count < 2 || endList == null || endList.Count < 2) return result;
        result.Add(GetPathPointPosition(startList));
        var waypoints = path.GetWaypointList();
        if (waypoints != null)
        {
            foreach (var wp in waypoints) result.Add(wp.GetAbsolutePosition());
        }
        result.Add(GetPathPointPosition(endList));
        return result;
    }

    /// <summary>生成済みパスの描画LODと詳細区間が、望ましい値と実質的に一致しているか。</summary>
    private static bool DrawLodMatches(GameObject pathGO, DrawLod lod, double detailFrom, double detailTo)
    {
        var info = pathGO != null ? pathGO.GetComponent<DrawLodInfo>() : null;
        if (info == null) return lod == DrawLod.Normal && detailFrom <= 0.0 && detailTo >= 1.0;
        if (info.drawLod != lod) return false;
        if (lod == DrawLod.Far) return true;
        return System.Math.Abs(info.detailFrom - detailFrom) <= DrawLodConfig.PATH_DETAIL_RANGE_REBUILD_TOLERANCE
            && System.Math.Abs(info.detailTo - detailTo) <= DrawLodConfig.PATH_DETAIL_RANGE_REBUILD_TOLERANCE;
    }

    private void EnsureTerrainFollowRebuildWorker()
    {
        if (terrainFollowRebuildWorker == null)
        {
            terrainFollowRebuildWorker = RenderWorkScheduler.Start(this, TerrainFollowRebuildWorker(), "PathManager", () => PendingTerrainRebuildCount > 0);
        }
    }

    /// <summary>
    /// カメラ移動に応じて、生成済みパスの描画LODを再判定する常駐ワーカー。
    /// 一定フレームごと・一定距離の移動があったときだけ全パスを走査し、望ましい描画LODと
    /// 食い違うパスを共通フレーム予算で切り替える。回転・FOVは距離LODを変えない。
    /// 再生成が終わるまで旧オブジェクトは表示されたまま残る。
    /// </summary>
    private LodViewState drawViewState;

    // 再判定の走査は共通予算のスライスに分けるため、辞書を直接列挙せずキーの写しを回す。
    private readonly List<int> drawLodScanIds = new List<int>();
    private readonly List<int> drawLodChangeIds = new List<int>();
    private readonly System.Diagnostics.Stopwatch drawLodScanWatch = new System.Diagnostics.Stopwatch();

    private IEnumerator PathDrawLodReevaluationWorker()
    {
        while (true)
        {
            for (int i = 0; i < DrawLodConfig.REEVALUATE_INTERVAL_FRAMES; i++) yield return null;
            if (pathObjectsCache.Count == 0 || isLodPathBatchActive) continue;

            Camera cam = Camera.main;
            if (cam == null) continue;
            if (!drawViewState.UpdatePosition(cam)) continue;
            Vector3 camPos = cam.transform.position;
            RenderWorkScheduler.CountEvent("path.lodScanned", pathObjectsCache.Count);

            drawLodScanIds.Clear();
            drawLodScanIds.AddRange(pathObjectsCache.Keys);
            drawLodChangeIds.Clear();
            bool aborted = false;
            drawLodScanWatch.Restart();
            for (int i = 0; i < drawLodScanIds.Count; i++)
            {
                if (drawLodScanWatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
                {
                    yield return RenderWorkScheduler.Continue;
                    drawLodScanWatch.Restart();
                    cam = Camera.main;
                    // バッチ中は各パスをバッチ側で判定するので走査を打ち切る。基準位置を捨てて次の周期で必ず走査し直す。
                    if (cam == null || isLodPathBatchActive)
                    {
                        aborted = true;
                        drawViewState = default;
                        break;
                    }
                    camPos = cam.transform.position;
                }
                int id = drawLodScanIds[i];
                if (!pathObjectsCache.TryGetValue(id, out GameObject obj) || obj == null || !obj.activeSelf) continue;
                if (!pathDataCache.TryGetValue(id, out PathSummary path)) continue;
                DrawLod desired = DeterminePathDrawLod(path, LodTier.Near, camPos, out double from, out double to);
                if (DrawLodMatches(obj, desired, from, to)) continue;
                drawLodChangeIds.Add(id);
            }
            drawLodScanIds.Clear();
            if (aborted)
            {
                drawLodChangeIds.Clear();
                continue;
            }
            for (int i = 0; i < drawLodChangeIds.Count; i++)
            {
                // バッチ中に作ると、交差点の切断点が確定する前の形がバッチの生成済みとして数えられ、
                // バッチ側の再生成が重複スキップされる。残りは捨てて次の周期で走査し直す。
                if (isLodPathBatchActive)
                {
                    drawViewState = default;
                    break;
                }
                // yield の間にデータが差し替わることがあるため、古いインスタンスで上書きしないよう毎回引き直す。
                if (!pathDataCache.TryGetValue(drawLodChangeIds[i], out PathSummary path)) continue;
                TryRebuildPathOnce(path, LodTier.Near, false, false, "描画LOD");
                yield return RenderWorkScheduler.Continue;
            }
            drawLodChangeIds.Clear();
        }
    }

    /// <summary>
    /// 詳細区間の外側に遠景の帯を敷く。詳細パーツを敷かない区間(PathDrawContext)を、
    /// 描画点列を割合で切り出した曲線どおりの帯で埋める。
    /// </summary>
    private void AddFarRemainderStrips(GameObject pathGO, List<Vector3> points, float width, double detailFrom, double detailTo)
    {
        void AddStrip(double from, double to, string name)
        {
            if (to - from <= 1e-4) return;
            List<Vector3> slice = PathDrawContext.SlicePolylineByFraction(points, from, to);
            Mesh mesh = PathMeshFactory.BuildStripMesh(slice, width);
            if (mesh == null) return;
            GameObject go = new GameObject(name);
            go.transform.SetParent(pathGO.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<IntersectionMeshOwner>().OwnedMesh = mesh;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            Material mat = GetFarRoadDepthMaterial();
            if (mat != null) mr.sharedMaterial = mat;
        }
        AddStrip(0.0, detailFrom, "FarRemainder_Start");
        AddStrip(detailTo, 1.0, "FarRemainder_End");
    }

    /// <summary>
    /// 近接線路の結合描画（TrackAdjacency）のための再生成待ちキュー。
    /// 線路のデータ変更（新規・version変化・削除）があると、その近くに並走する既存線路の
    /// 結合状態（道床の一体化・防音壁の省略・架線柱のコリドー全幅化）が変わるが、
    /// 既存線路自身の version は変わらないため差分更新では再生成されない。
    /// そこで変更のあった線路の近接線路をここに積み、LODバッチ中でなければ即座に、
    /// バッチ中なら EndLodPathBatch 時に（このバッチで生成済みのパスを除いて）
    /// queuedTerrainFollowPathIds へ流して TerrainFollowRebuildWorker に再生成させる。
    /// </summary>
    private readonly HashSet<int> pendingTrackNeighborRebuildIds = new HashSet<int>();

    /// <summary>
    /// パスの視野優先スコア(小さいほど優先)。パスが覆うセル群のうちカメラに最も近い
    /// セル矩形までの水平距離をベースに、視野外なら大きなペナルティを加算する。
    /// </summary>
    private float ScorePathForViewPriority(PathSummary path, Vector3 camPos, Vector3 camForwardHorizontal)
    {
        float cellSize = TerrainManager.CellSize;
        float best = float.MaxValue - 1f;
        foreach (var cell in GetPathCoveredCellsCached(path))
        {
            float cx = Mathf.Clamp(camPos.x, cell.x * cellSize, (cell.x + 1) * cellSize);
            float cz = Mathf.Clamp(camPos.z, cell.y * cellSize, (cell.y + 1) * cellSize);
            Vector3 closest = new Vector3(cx, 0f, cz);
            Vector3 to = closest - camPos;
            to.y = 0f;
            float dist = to.magnitude;
            if (!Utils.IsInHorizontalView(closest, camPos, camForwardHorizontal, PATH_VIEW_NEAR_DISTANCE, PATH_VIEW_HALF_ANGLE_DEG))
            {
                dist += PATH_OUT_OF_VIEW_PENALTY;
            }
            if (dist < best) best = dist;
        }
        return best;
    }

    /// <summary>
    /// ノードの視野優先スコア(小さいほど優先)。ノード位置の水平距離＋視野外ペナルティ。
    /// </summary>
    private float ScoreNodeForViewPriority(int nodeId, Vector3 camPos, Vector3 camForwardHorizontal)
    {
        if (!nodeDataCache.TryGetValue(nodeId, out NodeData node) || node?.position == null || node.position.Length < 2)
        {
            return float.MaxValue - 1f;
        }
        float x = node.position[0];
        float z = node.position.Length >= 3 ? node.position[2] : node.position[1];
        Vector3 pos = new Vector3(x, 0f, z);
        Vector3 to = pos - camPos;
        to.y = 0f;
        float dist = to.magnitude;
        if (!Utils.IsInHorizontalView(pos, camPos, camForwardHorizontal, PATH_VIEW_NEAR_DISTANCE, PATH_VIEW_HALF_ANGLE_DEG))
        {
            dist += PATH_OUT_OF_VIEW_PENALTY;
        }
        return dist;
    }

    private bool NeedsTerrainFollowReorder(Camera cam)
    {
        if (terrainFollowOrderCursor >= terrainFollowOrder.Count) return true;
        if (terrainFollowOrderStale && Time.frameCount - terrainFollowOrderFrame >= TERRAIN_FOLLOW_REORDER_FRAMES) return true;
        if (cam == null) return false;
        Vector3 moved = cam.transform.position - terrainFollowOrderCamPos;
        moved.y = 0f;
        if (moved.sqrMagnitude >= TERRAIN_FOLLOW_REORDER_MOVE * TERRAIN_FOLLOW_REORDER_MOVE) return true;
        return Vector3.Angle(Utils.GetCameraHorizontalForward(cam), terrainFollowOrderCamFwd) >= TERRAIN_FOLLOW_REORDER_ANGLE;
    }

    /// <summary>
    /// キューの写しに視野優先スコアを付けて並べ直す。スコア計算は共通予算で区切り、途中の投入・取り出しは
    /// 次の並べ替え(投入で立つ stale)と取り出し時の存在確認で吸収する。
    /// </summary>
    /// <param name="watch">呼び出し側のステップの予算計測。並べ替えの後、同じステップの残り予算で取り出しを続けられるようにする。</param>
    private IEnumerator RebuildTerrainFollowOrder(System.Diagnostics.Stopwatch watch)
    {
        terrainFollowOrderStale = false;
        terrainFollowOrderFrame = Time.frameCount;
        Camera cam = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
        Vector3 camFwd = cam != null ? Utils.GetCameraHorizontalForward(cam) : Vector3.forward;
        terrainFollowOrderCamPos = camPos;
        terrainFollowOrderCamFwd = camFwd;

        terrainFollowOrderIds.Clear();
        terrainFollowOrderIds.AddRange(queuedTerrainFollowPathIds);
        terrainFollowOrderScratch.Clear();
        for (int i = 0; i < terrainFollowOrderIds.Count; i++)
        {
            if (watch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
            {
                yield return RenderWorkScheduler.Continue;
                watch.Restart();
            }
            int id = terrainFollowOrderIds[i];
            // データを失った項目は最優先で取り除く。
            float score = pathDataCache.TryGetValue(id, out PathSummary p) ? ScorePathForViewPriority(p, camPos, camFwd) : float.MinValue;
            terrainFollowOrderScratch.Add((score, id));
        }
        terrainFollowOrderIds.Clear();
        terrainFollowOrderScratch.Sort(TerrainFollowOrderComparison);
        (terrainFollowOrder, terrainFollowOrderScratch) = (terrainFollowOrderScratch, terrainFollowOrder);
        terrainFollowOrderScratch.Clear();
        terrainFollowOrderCursor = 0;
    }

    /// <summary>
    /// 地形タイル更新・橋脚や近接線路の周辺変化によるパス再生成キューを消化する常駐ワーカー。
    /// フレーム予算内で「視野内かつカメラに近い」パスから順に再生成する。処理順は RebuildTerrainFollowOrder がまとめて決める。
    /// LODバッチ中は、そのバッチが作り直す予定のパスを取り出さずキューに残す(切断点の確定前に作ると、
    /// バッチ内の重複除外で確定後の再生成が行われなくなるため)。
    /// </summary>
    private IEnumerator TerrainFollowRebuildWorker()
    {
        var budgetWatch = new System.Diagnostics.Stopwatch();
        while (true)
        {
            if (queuedTerrainFollowPathIds.Count == 0)
            {
                if (terrainFollowOrder.Count > 0)
                {
                    terrainFollowOrder.Clear();
                    terrainFollowOrderCursor = 0;
                }
                yield return null;
                continue;
            }

            budgetWatch.Restart();
            if (NeedsTerrainFollowReorder(Camera.main)) yield return RebuildTerrainFollowOrder(budgetWatch);

            int processed = 0;
            int heldForBatch = 0;
            while (terrainFollowOrderCursor < terrainFollowOrder.Count && budgetWatch.Elapsed.TotalMilliseconds <= PerformanceConfig.SliceBudgetMs)
            {
                int bestId = terrainFollowOrder[terrainFollowOrderCursor++].id;
                if (!queuedTerrainFollowPathIds.Contains(bestId)) continue;
                if (isLodPathBatchActive && (lodBatchPendingBuildIds.Contains(bestId) || deferredIntersectionPathRebuildIds.Contains(bestId)))
                {
                    heldForBatch++;
                    continue;
                }

                processed++;
                queuedTerrainFollowPathIds.Remove(bestId);
                bool terrainOnly = terrainOnlyQueuedPathIds.Remove(bestId);
                bool pillarOnly = queuedPillarOnlyIds.Remove(bestId);
                queuedTerrainFollowSerials.TryGetValue(bestId, out long queuedSerial);
                queuedTerrainFollowSerials.Remove(bestId);
                if (!pathDataCache.TryGetValue(bestId, out PathSummary path)) continue;

                // ロード範囲外(表示対象セルを1つも通らない)のパスは再生成しない。
                // キュー投入からここまでの間にセルがアンロードされた場合に再生成すると、
                // 非表示のはずのパスがアクティブなGameObjectとして復活してしまう。
                // セルが再ロードされれば通常のLODロードが面倒を見る。
                bool inLoadedRange = false;
                foreach (var cell in GetPathCoveredCellsCached(path))
                {
                    if (cellsToKeepVisible.Contains(cell))
                    {
                        inLoadedRange = true;
                        break;
                    }
                }
                if (!inLoadedRange) continue;
                pathObjectsCache.TryGetValue(bestId, out GameObject currentGO);
                if (currentGO != null)
                {
                    PathBuildStamp stamp = currentGO.GetComponent<PathBuildStamp>();
                    if (stamp != null && stamp.buildSerial > queuedSerial)
                    {
                        RenderWorkScheduler.CountEvent("path.queueRebuildSkipped");
                        continue;
                    }
                }
                // 遠景は橋脚を描かない。revision は通知時に進めてあるので、次に通常LODで作るときは周辺の最新データで作られる。
                if (pillarOnly && (currentGO == null || (currentGO.GetComponent<ObjectIdInfo>()?.isFarObject ?? false)))
                {
                    RenderWorkScheduler.CountEvent("path.queueRebuildSkipped");
                    continue;
                }
                // 同じ版のタイルの再到着(LRUからの復元など)では、生成時と地形が変わっていない。
                if (terrainOnly && IsTerrainCurrent(path))
                {
                    RenderWorkScheduler.CountEvent("terrainFollow.pathTerrainUnchanged");
                    continue;
                }

                // 現在のティアで再生成する(キュー投入時からティアが変わっていることがある。
                // Farに落ちたパスへフル詳細メッシュを作る無駄を避ける)。
                LodTier tier = LodTier.Far;
                foreach (var cell in GetPathCoveredCellsCached(path))
                {
                    if (cellPathLod.TryGetValue(cell, out LodTier t) && t > tier) tier = t;
                }
                TryRebuildPathOnce(path, tier, updateIntersections: false, forceRebuild: true, reason: "地形追従(キュー)", batchMember: false);
            }

            // 残りがすべて進行中のバッチ待ちなら、並べ替えを空回しせずバッチの終了(または並べ替え間隔)まで待つ。
            if (processed == 0 && heldForBatch > 0 && terrainFollowOrderCursor >= terrainFollowOrder.Count)
            {
                for (int i = 0; i < TERRAIN_FOLLOW_REORDER_FRAMES && isLodPathBatchActive; i++) yield return null;
                continue;
            }
            yield return RenderWorkScheduler.Continue;
        }
    }

    public void BeginDeferredTerrainTilePathRebuilds()
    {
        deferTerrainTilePathRebuilds = true;
    }

    /// <summary>
    /// 地形タイル生成イベントによる道路再生成の抑制を解除し、抑制中に受信して破棄した
    /// イベントのセル一覧を返す(返却後、内部の記録はクリアされる)。
    /// </summary>
    public HashSet<Vector2Int> EndDeferredTerrainTilePathRebuilds()
    {
        deferTerrainTilePathRebuilds = false;
        var dropped = new HashSet<Vector2Int>(deferredTerrainReadyCells);
        deferredTerrainReadyCells.Clear();
        return dropped;
    }

    public override void ClearAllCells()
    {
        ClearUndergroundPaths();
        GetComponent<PathSpeedLimitOverlay>()?.Clear();
        RoadMarkingAtlas.Clear();
        ClearTerrainFollowProgress();
        Debug.Log("PathManager: 全ノード・道路データをクリア");
        closePathIds.Clear();
        lodCache.Clear();
        geometryRevisions.Clear();
        pendingBuildingAccessRebuilds.Clear();
        anchorPolylineCache.Clear();
        untrimmedPointsCache.Clear();
        connectionRevisions.Clear();
        DrawLodStats.ResetCounts(DrawLodStats.Category.Path);

        // 走行中のLODロードを止めてからクリアする。残しておくと、旧セッション宛の
        // リクエストのレスポンスがクリア後に届き、キャッシュと空間ハッシュを再登録してしまう。
        CancelActiveLodCoroutines();

        // ノード削除
        foreach (var obj in nodeObjectsCache.Values)
        {
            if (obj != null) Destroy(obj);
        }
        nodeObjectsCache.Clear();
        nodeIdsByCell.Clear();
        nodeCellById.Clear();

        // パス削除
        foreach (var obj in pathObjectsCache.Values)
        {
            if (obj != null) Destroy(obj);
        }
        pathObjectsCache.Clear();
        unregisteredPathObjectIds.Clear();

        // ウェイポイント削除 (パスの子要素として管理されるようになったため、個別の管理は不要だが、念のためタグ検索で残骸を消す)
        GameObject[] waypointObjects = GameObject.FindGameObjectsWithTag(WAYPOINT_TAG);
        foreach (var obj in waypointObjects)
        {
            Destroy(obj);
        }

        loadedCells.Clear();
        nodeDataCache.Clear();
        pathDataCache.Clear();
        ConstructionAmbience.Clear(ConstructionAmbience.Kind.Path);
        pendingPillarRebuildIds.Clear();
        pendingPillarRebuildSerials.Clear();
        pendingPowerRebuildIds.Clear();
        pathsByNodeIndex.Clear();
        pathVersionCache.Clear();

        // 空間ハッシュは吸着判定と道路編集モードの背景オーバーレイ（既存パス表示）の
        // 唯一の供給源。セルのアンロードは非表示化のみでここを消さないため、
        // 残すと存在しないパス・ノードへ吸着してしまう。
        nodeGrid?.Clear();
        segmentGrid?.Clear();
        provisionalEndGrid?.Clear();
        provisionalEndsByPath.Clear();
        // 背景オーバーレイはカメラ静止中 NodeDataVersion の変化でしか再構築されないため、
        // クリアを通知しないと旧セッションのパス線が残り続ける。
        NodeDataVersion++;

        ClearIntersectionMeshes();
        pathStartConnectionPoints.Clear();
        pathEndConnectionPoints.Clear();
        pathStartSidewalkSetbacks.Clear();
        pathEndSidewalkSetbacks.Clear();
        pathSegmentsByPathId.Clear();
        pathIdsByCell.Clear();
        pathCellsById.Clear();
        pathCellsVersion.Clear();
        pathPointsCache.Clear();
        pathLabelPositions.Clear();
        cellPathLod.Clear();
        awaitingIntersectionNodes.Clear();
        fetchingIntersectionNodeIds.Clear();
        overloadedIntersectionNodeIds.Clear();
        ClearTerrainFollowQueue();
        pendingTrackNeighborRebuildIds.Clear();
        pendingLrtPlatformRebuildSerials.Clear();
        cellsToKeepVisible.Clear();
        pendingUnloadCells.Clear();
        lodBatchPendingBuildIds.Clear();
        deferredTerrainReadyCells.Clear();
        deferTerrainTilePathRebuilds = false;
    }

    // ================================================================
    // LOD対応セルロード
    // ================================================================

    public override void LoadLodCells(LodCellSets cellSets, bool isForce)
    {
        LoadLodCells(cellSets, null, isForce);
    }

    public override void LoadLodCells(LodCellSets cellSets, HashSet<Vector2Int> tierChangedCells, bool isForce)
    {
        // 実行中の古いコルーチンをすべてキャンセル
        CancelActiveLodCoroutines();
        if (pathDrawLodWorker == null)
        {
            pathDrawLodWorker = RenderWorkScheduler.Start(this, PathDrawLodReevaluationWorker(), "PathManager", () => false);
        }

        var allPathCells = new HashSet<Vector2Int>(cellSets.farCells);
        var nearSet = new HashSet<Vector2Int>(cellSets.nearCells);
        var dynamicSet = new HashSet<Vector2Int>(cellSets.dynamicCells);

        // セル跨ぎのパスを誤って非表示にしないために、
        // アンロード判定時に参照する「残すべきセル」集合を設定する
        cellsToKeepVisible = new HashSet<Vector2Int>(allPathCells);

        var toUnload = new List<Vector2Int>();
        foreach (var cell in loadedCells)
        {
            if (!allPathCells.Contains(cell))
                toUnload.Add(cell);
        }

        // 常に cellSets.farCells の全セルを対象とする。
        // 未ロードセルは新規ロード、ロード済みセルは version ベースで差分更新される。
        var cellsToLoad = new List<Vector2Int>();
        var lodUpdates = new Dictionary<Vector2Int, LodTier>();
        foreach (var cell in cellSets.farCells)
        {
            LodTier desiredTier = LodTier.Far;
            if (dynamicSet.Contains(cell)) desiredTier = LodTier.Dynamic;
            else if (nearSet.Contains(cell)) desiredTier = LodTier.Near;

            cellsToLoad.Add(cell);
            RenderWorkScheduler.CountEvent(loadedCells.Contains(cell) ? "path.fetchExistingCells" : "path.fetchNewCells");
            lodUpdates[cell] = desiredTier;
        }

        if (cellsToLoad.Count > 0)
        {
            List<Vector2Int> changedCellsForLog = BuildChangedCellsForPathLog(cellsToLoad, toUnload);

            // LODティアが実際に変更されたセルのみを抽出する。
            // カメラ移動時はティア変更セルの道路をキャッシュから再描画するが、
            // バックエンドデータ変更時(ReloadAllData)はティア変更がないため
            // RebuildCachedPathsForCells に全セルを渡すとversion比較なしの全再描画が発生してしまう。
            List<Vector2Int> lodTierChangedCells = new List<Vector2Int>();
            foreach (var kvp in lodUpdates)
            {
                if (!cellPathLod.TryGetValue(kvp.Key, out LodTier oldTier) || oldTier != kvp.Value)
                {
                    lodTierChangedCells.Add(kvp.Key);
                }
            }

            activeLodCoroutines.Add(RenderWorkScheduler.Start(this, LoadMergedPathsAndNodesAndApplyCells(cellsToLoad, nearSet, dynamicSet, toUnload, lodUpdates, changedCellsForLog, lodTierChangedCells), "PathManager"));
        }
        else
        {
            ApplyLodCellChanges(cellsToLoad, toUnload, lodUpdates);
        }
    }

    /// <summary>
    /// 新しいセルの道路を生成してから不要セルを非表示にする。
    /// API待ちやメッシュ生成のあいだ既存道路を残すことで、セル跨ぎ時の空白フレームを防ぐ。
    /// </summary>
    private IEnumerator LoadMergedPathsAndNodesAndApplyCells(List<Vector2Int> cellsToLoad, HashSet<Vector2Int> nearSet, HashSet<Vector2Int> dynamicSet, List<Vector2Int> cellsToUnload, Dictionary<Vector2Int, LodTier> lodUpdates, List<Vector2Int> changedCellsForLog, List<Vector2Int> tierChangedCells)
    {
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] PathManager LoadMergedPathsAndNodesAndApplyCells START cells={cellsToLoad.Count}");

        bool succeeded = false;
        HashSet<int> rebuiltPathIds = new HashSet<int>();
        HashSet<int> targetPathIds = CollectAffectedPathIds(changedCellsForLog);
        HashSet<Vector2Int> changedCellSet = new HashSet<Vector2Int>(changedCellsForLog);

        BeginLodPathBatch();
        try
        {
            var swPhase = System.Diagnostics.Stopwatch.StartNew();
            yield return LoadMergedPathsAndNodes(cellsToLoad, nearSet, dynamicSet, success => succeeded = success, rebuiltPathIds, targetPathIds, changedCellSet);
            UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] PathManager  LoadMergedPathsAndNodes: {swPhase.ElapsedMilliseconds}ms");

            if (!succeeded)
            {
                // バッチ失敗時に deferred 情報が失われないよう退避して復元する。
                var preservedNodeIds = new HashSet<int>(deferredLodIntersectionNodeIds);
                var preservedPathIds = new HashSet<int>(deferredIntersectionPathRebuildIds);
                EndLodPathBatch();
                foreach (var id in preservedNodeIds) deferredLodIntersectionNodeIds.Add(id);
                foreach (var id in preservedPathIds) deferredIntersectionPathRebuildIds.Add(id);
                yield break;
            }

            // RebuildCachedPathsForCells にはティア変更セルのみを渡す。
            // 全セルを渡すとversion比較なしの全再描画が発生するため、
            // ティアが変わっていないセルの道路はスキップする。
            swPhase.Restart();
            yield return RebuildCachedPathsForCells(tierChangedCells, nearSet, dynamicSet, rebuiltPathIds);
            UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] PathManager  RebuildCachedPathsForCells: {swPhase.ElapsedMilliseconds}ms (tierChangedCells={tierChangedCells.Count})");

            swPhase.Restart();
            yield return UpdateDeferredLodIntersections();
            UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] PathManager  UpdateDeferredLodIntersections: {swPhase.ElapsedMilliseconds}ms");

            swPhase.Restart();
            yield return RebuildDeferredIntersectionPaths(nearSet, dynamicSet, rebuiltPathIds);
            UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] PathManager  RebuildDeferredIntersectionPaths: {swPhase.ElapsedMilliseconds}ms");

            ApplyLodCellChanges(cellsToLoad, cellsToUnload, lodUpdates);
            EndLodPathBatch();

            UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] PathManager LoadMergedPathsAndNodesAndApplyCells END: {swTotal.ElapsedMilliseconds}ms");
        }
        finally
        {
            // 中断・生成失敗でも次の地形追従を待ち状態に閉じ込めない。
            if (isLodPathBatchActive) EndLodPathBatch();
        }
    }

    private void BeginLodPathBatch()
    {
        isLodPathBatchActive = true;
        lodBatchRebuiltPathIds.Clear();
        lodBatchPendingBuildIds.Clear();
        deferredIntersectionPathRebuildIds.Clear();
        deferredIntersectionRebuildSerials.Clear();
        deferredLodIntersectionNodeIds.Clear();
    }

    /// <summary>
    /// バッチの終了時(完了・失敗・取消し)に、交差点の切断点が変わった道路を再生成キューへ移す。
    /// 取り消されたバッチや、切断点の確定前にバッチ内で生成済みとして重複除外された道路は、次のバッチでは
    /// 道路もノードも「変化なし」になり誰も作り直さないため。番号は切断点が変わった時点のものなので、
    /// その後に作り直した道路はワーカーが省く。
    /// </summary>
    private void RequeueUnfinishedIntersectionRebuilds()
    {
        foreach (var kvp in deferredIntersectionRebuildSerials)
            EnqueueTerrainFollowPath(kvp.Key, kvp.Value, TerrainFollowQueueReason.Other);
        deferredIntersectionRebuildSerials.Clear();
    }

    private void EndLodPathBatch()
    {
        isLodPathBatchActive = false;
        RequeueUnfinishedIntersectionRebuilds();
        // バッチ中に積まれた近接線路の再生成を開始する。このバッチで生成済みのパスは
        // フェーズ1のキャッシュ一括更新後に生成されており結合状態が既に正しいため除外する。
        FlushPendingTrackNeighborRebuilds(lodBatchRebuiltPathIds);
        lodBatchRebuiltPathIds.Clear();
        lodBatchPendingBuildIds.Clear();
        deferredIntersectionPathRebuildIds.Clear();
        deferredIntersectionRebuildSerials.Clear();
        deferredLodIntersectionNodeIds.Clear();
    }

    private void CancelActiveLodCoroutines()
    {
        lodFetchGeneration++;
        foreach (var c in activeLodCoroutines)
        {
            if (c != null) RenderWorkScheduler.Stop(this, c);
        }
        activeLodCoroutines.Clear();

        if (isLodPathBatchActive)
        {
            // LOD更新コルーチンはカメラ移動で途中停止されることがある。
            // 途中停止時に一括更新フラグや延期キューが残ると、次の更新で道路端再生成が誤って延期されたままになる。
            EndLodPathBatch();
        }
    }

    private void ApplyLodCellChanges(List<Vector2Int> cellsToLoad, List<Vector2Int> cellsToUnload, Dictionary<Vector2Int, LodTier> lodUpdates)
    {
        foreach (var cell in cellsToLoad)
        {
            loadedCells.Add(cell);
            pendingUnloadCells.Remove(cell);
        }

        foreach (var kvp in lodUpdates)
        {
            cellPathLod[kvp.Key] = kvp.Value;
        }

        UnloadCellsObjects(cellsToUnload);
        foreach (var cell in cellsToUnload)
        {
            loadedCells.Remove(cell);
            cellPathLod.Remove(cell);
        }
    }

    /// <summary>
    /// 複数セルのマージBBOXを使ってパスとノードを一括取得する。
    /// 従来のセルごとのAPIコール(最大49回)を、パス1回+ノード1回の計2回に削減する。
    /// </summary>
    private IEnumerator LoadMergedPathsAndNodes(List<Vector2Int> cells, HashSet<Vector2Int> nearSet, HashSet<Vector2Int> dynamicSet, Action<bool> onComplete = null, HashSet<int> rebuiltPathIds = null, HashSet<int> targetPathIds = null, HashSet<Vector2Int> changedCellsFilter = null)
    {
        // ノードもパスと同様に、遠景セルを含むすべての可視セルを範囲として一括取得する。
        // これにより、遠景セル内の道路端にあるノードの個別API取得が発生することを防ぐ。
        if (cells.Count > 0)
        {
            var swNode = System.Diagnostics.Stopwatch.StartNew();
            yield return LoadMergedNodes(cells);
            UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] PathManager  LoadMergedNodes: {swNode.ElapsedMilliseconds}ms");
        }

        // パスは全セルのマージBBOXで一括取得
        var swPath = System.Diagnostics.Stopwatch.StartNew();
        yield return LoadMergedPaths(cells, nearSet, dynamicSet, onComplete, rebuiltPathIds, targetPathIds, changedCellsFilter);
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] PathManager  LoadMergedPaths: {swPath.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// 複数セルをマージしたBBOXでノードを一括取得する。
    /// </summary>
    private IEnumerator LoadMergedNodes(List<Vector2Int> cells)
    {
        CalculateMergedBBox(cells, out float minX, out float minZ, out float maxX, out float maxZ);

        int generation = lodFetchGeneration;
        NodeGeometryResponse response = null;
        yield return ApiClient.GetBulk<NodeGeometryResponse>(
            $"/nodes?start_x={minX}&end_x={maxX}&start_z={minZ}&end_z={maxZ}",
            r => response = r,
            timingLabel: "PathManager nodes",
            isCanceled: () => generation != lodFetchGeneration);
        if (response == null)
        {
            yield break;
        }

        HashSet<int> receivedNodeIds = new HashSet<int>();

        int nodeCount = response?.nodes?.Count ?? 0;
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[PathManager] LoadMergedNodes: received {nodeCount} nodes.");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var swProcess = System.Diagnostics.Stopwatch.StartNew();
        if (response != null && response.nodes != null)
        {
            foreach (var n in response.nodes)
            {
                CreateOrUpdateNode(n);
                receivedNodeIds.Add(n.node_id);
                if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
                {
                    yield return RenderWorkScheduler.Continue;
                    stopwatch.Restart();
                }
            }
        }
        UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] PathManager  CreateOrUpdateNode loop ({nodeCount} nodes): {swProcess.ElapsedMilliseconds}ms");

        SyncNodesInCells(cells, receivedNodeIds);
    }

    /// <summary>
    /// 複数セルをマージしたBBOXでパスを一括取得する。
    /// 各パスの位置からLOD tierを判定し、Nearならフル詳細、Farなら簡易表示で処理する。
    /// </summary>
    private IEnumerator LoadMergedPaths(List<Vector2Int> cells, HashSet<Vector2Int> nearSet, HashSet<Vector2Int> dynamicSet, Action<bool> onComplete = null, HashSet<int> rebuiltPathIds = null, HashSet<int> targetPathIds = null, HashSet<Vector2Int> changedCellsFilter = null)
    {
        CalculateMergedBBox(cells, out float minX, out float minZ, out float maxX, out float maxZ);
        string bbox = $"{minX},{minZ},{maxX},{maxZ}";
        PagedPathListResponse response = new PagedPathListResponse { paths = new List<ConstructionPathSummary>() };
        int cursor = 0;
        int generation = lodFetchGeneration;
        Func<bool> isCanceled = () => generation != lodFetchGeneration;
        while (true)
        {
            PagedPathListResponse page = null;
            yield return ApiClient.GetBulk<PagedPathListResponse>(
                $"/paths/?bbox={bbox}&limit=500&cursor={cursor}", value => page = value,
                timingLabel: "PathManager page", isCanceled: isCanceled);
            if (page?.paths == null) { response = null; break; }
            RenderWorkScheduler.CountEvent("path.received", page.paths.Count);
            response.paths.AddRange(page.paths);
            if (!page.next_cursor.HasValue) break;
            if (page.next_cursor.Value <= cursor) { response = null; break; }
            cursor = page.next_cursor.Value;
            yield return null;
        }
        if (response == null)
        {
            onComplete?.Invoke(false);
            yield break;
        }

        HashSet<int> affectedNodeIds = new HashSet<int>();

        HashSet<int> receivedIds = new HashSet<int>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // プレイヤー周辺の主要道路（Dynamic / Near）と、遠景道路（Far）を分離して格納する。
        // 遠景道路は描画遅延が視覚的に気にならないため、非同期の別コルーチンでゆっくり生成する。
        List<PathSummary> highPriorityPaths = new List<PathSummary>();
        List<PathSummary> lowPriorityPaths = new List<PathSummary>();
        Dictionary<int, LodTier> batchPathTiers = new Dictionary<int, LodTier>();

        // フェーズ1: 全パスのデータキャッシュを先に更新する。
        // 交差点メッシュの計算には接続パスのデータが揃っている必要があるため、
        // 描画前に pathDataCache を一括更新する。予算で区切るので、途中のyieldで地形追従キューが
        // 同じパスを先に作らないよう、受信したIDをバッチ待ちに登録してから保存する。
        Dictionary<int, int> oldVersions = new Dictionary<int, int>();
        Dictionary<int, int> oldStatuses = new Dictionary<int, int>();
        var oldProgress = new Dictionary<int, float>();
        if (response?.paths != null)
        {
            foreach (var p in response.paths)
            {
                receivedIds.Add(p.path_id);
                if (isLodPathBatchActive) lodBatchPendingBuildIds.Add(p.path_id);
                if (pathVersionCache.TryGetValue(p.path_id, out int oldVer))
                {
                    oldVersions[p.path_id] = oldVer;
                }
                if (pathDataCache.TryGetValue(p.path_id, out PathSummary oldPath))
                {
                    oldStatuses[p.path_id] = oldPath.status;
                    oldProgress[p.path_id] = ConstructionPathBuilder.Progress(oldPath);
                }
                SavePathToCache(p);
                if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
                {
                    yield return RenderWorkScheduler.Continue;
                    stopwatch.Restart();
                }
            }
        }

        // フェーズ2: version が変わったパス（または新規）のみオブジェクトを再作成する。
        // 変化なしの continue も予算判定を通るよう、判定はループの先頭で行う。
        if (response?.paths != null)
        {
            foreach (var p in response.paths)
            {
                if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
                {
                    yield return RenderWorkScheduler.Continue;
                    stopwatch.Restart();
                }
                bool versionChanged = true;
                int? renderedStatus = pathObjectsCache.TryGetValue(p.path_id, out var renderedPath) && renderedPath != null
                    ? renderedPath.GetComponent<ObjectIdInfo>()?.status
                    : oldStatuses.TryGetValue(p.path_id, out int cachedStatus) ? cachedStatus : (int?)null;
                if (oldVersions.TryGetValue(p.path_id, out int cachedVersion) && cachedVersion == p.version && HasPathRepresentation(p)
                    && renderedStatus == p.status
                    && (p.status != Constants.STATUS_CONSTRUCTING || (oldProgress.TryGetValue(p.path_id, out float previousProgress)
                        && previousProgress == ConstructionPathBuilder.Progress(p))))
                {
                    versionChanged = false;
                }

                if (!versionChanged)
                {
                    lodBatchPendingBuildIds.Remove(p.path_id);
                    RenderWorkScheduler.CountEvent("path.dataUnchanged");
                    // 再生成対象外だが、非表示になっている既存オブジェクトは再表示する。
                    if (pathObjectsCache.TryGetValue(p.path_id, out GameObject existingObj) && existingObj != null && !existingObj.activeSelf)
                    {
                        existingObj.SetActive(true);
                        EnsureEndIntersectionsVisible(p);
                    }
                    continue;
                }

                rebuiltPathIds?.Add(p.path_id);

                // status変化検知 (oldStatusesはフェーズ1で上書き前に保存済み)
                if (oldStatuses.TryGetValue(p.path_id, out int oldStatus))
                {
                    if (oldStatus != p.status)
                    {
                        if (p.start_node_id > 0) affectedNodeIds.Add(p.start_node_id);
                        if (p.end_node_id > 0) affectedNodeIds.Add(p.end_node_id);
                        TriggerPathStatusEffect(p, oldStatus, p.status);
                    }
                }
                else
                {
                    if (p.start_node_id > 0) affectedNodeIds.Add(p.start_node_id);
                    if (p.end_node_id > 0) affectedNodeIds.Add(p.end_node_id);
                }

                // version が変わったパスは形状が変わった可能性があるため、
                // 先に通過セルキャッシュ(pathCellsById)を更新してからLOD tierを判定する。
                // DeterminePathLodTier はこのキャッシュを参照するため、順序を入れ替えてはいけない。
                RegisterPathCells(p);

                // 線路の結合描画: この線路の変更で近くに並走する線路の結合状態が
                // 変わり得るため、近接線路を再生成対象に積む（バッチ中は終了後に実行）。
                // 高速道路の本線とランプの結合描画も同じ理由で相手側を作り直す。
                EnqueueAdjacentTrackNeighborRebuilds(p);
                EnqueueAdjacentRoadMergeRebuilds(p, affectedNodeIds);
                NotifyDepotTerminals(p);

                LodTier tier = DeterminePathLodTier(p, nearSet, dynamicSet);

                if (isLodPathBatchActive)
                {
                    // LOD一括更新では、先に全対象道路のデータだけを登録してから交差点を確定する。
                    // 初期ロード時は交差点の接続点キャッシュがまだ無いため、道路メッシュを先に作ると
                    // 交差点形状と道路端が一致しない状態で固定されることがある。
                    if (p.start_node_id > 0) deferredLodIntersectionNodeIds.Add(p.start_node_id);
                    if (p.end_node_id > 0) deferredLodIntersectionNodeIds.Add(p.end_node_id);

                    batchPathTiers[p.path_id] = tier;
                    if (tier == LodTier.Far)
                    {
                        lowPriorityPaths.Add(p);
                    }
                    else
                    {
                        highPriorityPaths.Add(p);
                    }
                    continue;
                }

                // ここへ来るのは version が変わった(＝見た目が変わった)パスと新規パスだけなので、
                // 必ずメッシュを作り直す。forceRebuild を落とすと、CreateOrUpdatePath の
                // 「ステータスが同じなら既存オブジェクトを使い回す」分岐に入り、結合・分割・造り替えの
                // ように形状だけが変わった稼働中パスが古いメッシュのまま残る。
                TryRebuildPathOnce(p, tier, updateIntersections: true, forceRebuild: true, reason: "api");
            }
        }
        if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
        {
            yield return RenderWorkScheduler.Continue;
            stopwatch.Restart();
        }

        if (highPriorityPaths.Count > 0 || lowPriorityPaths.Count > 0)
        {
            // 通信はバッチ化されているため、未取得ノードは一括でフェッチする
            yield return UpdateDeferredLodIntersections();

            // 高優先度（Dynamic/Near）の道路のみを同期的にビルドする。
            // プレイヤーの至近距離にある道路を最優先で表示させるための処理。
            foreach (var path in highPriorityPaths)
            {
                LodTier tier = batchPathTiers.TryGetValue(path.path_id, out LodTier storedTier) ? storedTier : DeterminePathLodTier(path, nearSet, dynamicSet);
                // version 差分で選ばれたパスのみが積まれているため、"api" と同じく必ず作り直す。
                TryRebuildPathOnce(path, tier, updateIntersections: false, forceRebuild: true, reason: "api-high");

                if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
                {
                    yield return RenderWorkScheduler.Continue;
                    stopwatch.Restart();
                }
            }
        }

        SyncPathsInCells(cells, receivedIds, affectedNodeIds);

        // 主要な交差点メッシュの再生成（高優先度道路に接続するもの、または差分検知されたもの）
        foreach (int nodeId in affectedNodeIds)
        {
            if (isLodPathBatchActive)
            {
                yield return UpdateNodeIntersectionMeshAfterNodeReady(nodeId);
            }
            else
            {
                UpdateNodeIntersectionMesh(nodeId);
            }
            if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
            {
                yield return RenderWorkScheduler.Continue;
                stopwatch.Restart();
            }
        }

        // 目の前の詳細道路および主要交差点が構築完了した時点で、呼び出し元（CellDataLoader等）へ
        // ロード完了を通知し、全体のロード待ち時間を最小化する。
        onComplete?.Invoke(true);

        // 低優先度の遠景（Far）道路のビルドは、ロード完了完了通知の後に別コルーチンとして実行する。
        // これにより、コルーチン全体の完了を待つ必要がなくなり、地形と周辺道路の追従完了が一瞬で完了する。
        if (lowPriorityPaths.Count > 0)
        {
            Coroutine lowPriorityCoroutine = RenderWorkScheduler.Start(this, BuildLowPriorityPathsCoroutine(lowPriorityPaths, batchPathTiers, nearSet, dynamicSet), "PathManager");
            activeLodCoroutines.Add(lowPriorityCoroutine);
        }
    }

    /// <summary>
    /// 道路のステータス変化に応じたエフェクトを呼び出します。
    /// </summary>
    private void TriggerPathStatusEffect(PathSummary path, int oldStatus, int newStatus)
    {
        if (VisualEffectManager.Instance == null || path == null) return;

        var startPointList = path.GetStartPointList();
        var endPointList = path.GetEndPointList();
        if (startPointList == null || startPointList.Count < 2 || endPointList == null || endPointList.Count < 2) return;

        Vector3 startPos = GetPathPointPosition(startPointList);
        Vector3 endPos = GetPathPointPosition(endPointList);
        Vector3 centerPos = (startPos + endPos) * 0.5f;

        float groundHeight = Utils.GetGroundHeight(new Vector3(centerPos.x, 1000f, centerPos.z));
        centerPos.y = groundHeight;

        // 建設中 (STATUS_CONSTRUCTING) から アクティブ (STATUS_ACTIVE) または 完了 (STATUS_COMPLETED) への完成遷移
        if (oldStatus == Constants.STATUS_CONSTRUCTING && 
            (newStatus == Constants.STATUS_ACTIVE || newStatus == Constants.STATUS_COMPLETED))
        {
            VisualEffectManager.Instance.PlayConstructionComplete(centerPos);
        }
        // アクティブ (STATUS_ACTIVE) から 解体 (STATUS_DEMOLISHING) または 解体計画 (STATUS_SCHEDULED_DEMOLITION) への遷移
        else if (oldStatus == Constants.STATUS_ACTIVE && 
                 (newStatus == Constants.STATUS_DEMOLISHING || newStatus == Constants.STATUS_SCHEDULED_DEMOLITION))
        {
            VisualEffectManager.Instance.PlayDemolition(centerPos);
        }
    }

    /// <summary>
    /// 低優先度の遠景（Far ティア）道路を、フレーム予算（PerformanceConfig.SliceBudgetMs）の範囲内で
    /// フレーム分割を挟みながら、バックグラウンドで非同期に生成する。
    /// </summary>
    private IEnumerator BuildLowPriorityPathsCoroutine(List<PathSummary> paths, Dictionary<int, LodTier> batchPathTiers, HashSet<Vector2Int> nearSet, HashSet<Vector2Int> dynamicSet)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var path in paths)
        {
            if (path == null) continue;

            LodTier tier = batchPathTiers.TryGetValue(path.path_id, out LodTier storedTier) ? storedTier : DeterminePathLodTier(path, nearSet, dynamicSet);
            // version 差分で選ばれたパスのみが積まれているため、"api" と同じく必ず作り直す。
            TryRebuildPathOnce(path, tier, updateIntersections: false, forceRebuild: true, reason: "api-low");

            if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
            {
                yield return RenderWorkScheduler.Continue;
                stopwatch.Restart();
            }
        }
    }

    /// <summary>
    /// パスの始点・終点・ウェイポイントの位置からLOD tierを判定する。
    /// いずれかのアンカー点がDynamicセルに含まれる場合はDynamic（最高詳細）、
    /// 含まれなければNearセルをチェックし、それでもなければFar（簡易表示）とする。
    /// セル境界を跨ぐ道路が正しいtierで描画されるよう、
    /// 全アンカー点をサンプリングして最も詳細なtierを採用する。
    ///
    /// 通過セルは登録済みキャッシュ(pathCellsById)があればそれを使う。
    /// version が変わった(=形状が変わった可能性がある)パスについては、
    /// 先に RegisterPathCells でキャッシュを更新してから呼ぶこと。
    /// </summary>
    private LodTier DeterminePathLodTier(PathSummary path, HashSet<Vector2Int> nearCells, HashSet<Vector2Int> dynamicCells)
    {
        LodTier bestTier = LodTier.Far;

        foreach (var cell in GetPathCoveredCellsCached(path))
        {
            if (dynamicCells.Contains(cell)) return LodTier.Dynamic;
            if (nearCells.Contains(cell)) bestTier = LodTier.Near;
        }

        return bestTier;
    }

    /// <summary>
    /// 登録済みの通過セルキャッシュ(pathCellsById)があればそれを返し、なければ再計算する。
    /// GetPathCoveredCells はアンカー走査+セルサンプリングを伴うため、
    /// 全パスに対して毎回呼ぶとロード時間に効いてくる。
    /// 注意: version が変わった直後(RegisterPathCells で再登録される前)は
    /// キャッシュが旧形状のままである。形状変更直後の文脈では先に RegisterPathCells を呼ぶこと。
    /// </summary>
    private HashSet<Vector2Int> GetPathCoveredCellsCached(PathSummary path)
    {
        if (path != null && pathCellsById.TryGetValue(path.path_id, out HashSet<Vector2Int> cached))
        {
            return cached;
        }
        return GetPathCoveredCells(path);
    }

    private IEnumerator RebuildCachedPathsForCells(List<Vector2Int> changedCells, HashSet<Vector2Int> nearSet, HashSet<Vector2Int> dynamicSet, HashSet<int> alreadyRebuiltPathIds)
    {
        HashSet<int> pathIds = new HashSet<int>();
        foreach (var cell in changedCells)
        {
            if (!pathIdsByCell.TryGetValue(cell, out HashSet<int> ids)) continue;
            foreach (int id in ids)
            {
                if (alreadyRebuiltPathIds != null && alreadyRebuiltPathIds.Contains(id)) continue;
                pathIds.Add(id);
            }
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (int pathId in pathIds)
        {
            if (alreadyRebuiltPathIds != null && alreadyRebuiltPathIds.Contains(pathId))
            {
                // 既に再生成済みだが、非表示のまま残っている場合は再表示する。
                if (pathObjectsCache.TryGetValue(pathId, out GameObject existing) && existing != null && !existing.activeSelf)
                {
                    existing.SetActive(true);
                    if (pathDataCache.TryGetValue(pathId, out PathSummary shownPath)) EnsureEndIntersectionsVisible(shownPath);
                }
                continue;
            }

            if (!pathDataCache.TryGetValue(pathId, out PathSummary path)) continue;

            LodTier tier = DeterminePathLodTier(path, nearSet, dynamicSet);
            // セルへの再入場だけでは形状の版を進めず、既存LODと退避キャッシュを使う。
            // 地形・データ・交差点の変更は、それぞれの更新経路で強制再生成する。
            TryRebuildPathOnce(path, tier, updateIntersections: true, forceRebuild: false, reason: "lookup");

            if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
            {
                yield return RenderWorkScheduler.Continue;
                stopwatch.Restart();
            }
        }
    }

    private IEnumerator RebuildDeferredIntersectionPaths(HashSet<Vector2Int> nearSet, HashSet<Vector2Int> dynamicSet, HashSet<int> alreadyRebuiltPathIds)
    {
        if (deferredIntersectionPathRebuildIds.Count == 0) yield break;

        // yield の間に交差点の確定で追加されることがあるので、集合を直接列挙せず、未処理分がなくなるまで写しを回す。
        var visited = new HashSet<int>();
        var pathIds = new List<int>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            pathIds.Clear();
            foreach (int id in deferredIntersectionPathRebuildIds)
            {
                if (visited.Add(id)) pathIds.Add(id);
            }
            if (pathIds.Count == 0) yield break;
            foreach (int pathId in pathIds)
            {
                if (alreadyRebuiltPathIds != null && alreadyRebuiltPathIds.Contains(pathId)) continue;
                if (!pathDataCache.TryGetValue(pathId, out PathSummary path)) continue;

                LodTier tier = DeterminePathLodTier(path, nearSet, dynamicSet);
                if (TryRebuildPathOnce(path, tier, updateIntersections: false, forceRebuild: true, reason: "intersection"))
                {
                    alreadyRebuiltPathIds?.Add(pathId);
                }

                if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
                {
                    yield return RenderWorkScheduler.Continue;
                    stopwatch.Restart();
                }
            }
        }
    }

    private IEnumerator UpdateDeferredLodIntersections()
    {
        if (deferredLodIntersectionNodeIds.Count == 0) yield break;

        List<int> nodeIds = new List<int>(deferredLodIntersectionNodeIds);
        deferredLodIntersectionNodeIds.Clear();

        // キャッシュ（nodeDataCache）に存在せず、かつ現在通信取得中（fetchingIntersectionNodeIds）でもない
        // 未取得のノードIDを抽出し、バックエンドへ一括取得リクエストを送信する。
        // これにより、個別リクエスト（/nodes/{id}）が連続して走る通信オーバーヘッドを防止する。
        List<int> missingNodeIds = new List<int>();
        foreach (int nodeId in nodeIds)
        {
            if (!nodeDataCache.ContainsKey(nodeId) && !fetchingIntersectionNodeIds.Contains(nodeId))
            {
                missingNodeIds.Add(nodeId);
            }
        }

        if (missingNodeIds.Count > 0)
        {
            yield return FetchNodesBatchCoroutine(missingNodeIds);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (int nodeId in nodeIds)
        {
            yield return UpdateNodeIntersectionMeshAfterNodeReady(nodeId);
            if (stopwatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
            {
                yield return RenderWorkScheduler.Continue;
                stopwatch.Restart();
            }
        }
    }

    /// <summary>
    /// 未取得のノードIDリストを受け取り、バックエンドへカンマ区切りのパラメータ（ids）で一括リクエストを送信する。
    /// URLの長さ制限等を考慮し、最大100件ずつのバッチに分割して取得処理を行う。
    /// </summary>
    private IEnumerator FetchNodesBatchCoroutine(List<int> nodeIds)
    {
        const int batchSize = 100;
        for (int i = 0; i < nodeIds.Count; i += batchSize)
        {
            List<int> batch = nodeIds.GetRange(i, Math.Min(batchSize, nodeIds.Count - i));
            string idsParam = string.Join(",", batch);

            // 通信中の重複リクエストを防ぐため、フェッチ中フラグをセットする
            foreach (int nodeId in batch)
            {
                fetchingIntersectionNodeIds.Add(nodeId);
            }

            // バッチ取消し(RenderWorkScheduler.Stop の Dispose)でもフラグを外す。
            // 残ると後続バッチがこのノードの取得完了を永久に待ち、IsLodBatchActive が下りなくなる。
            try
            {
                NodeGeometryResponse response = null;
                yield return ApiClient.GetBulk<NodeGeometryResponse>(
                    $"/nodes?ids={idsParam}",
                    r => response = r,
                    timingLabel: "PathManager nodes-batch");

                if (response?.nodes != null)
                {
                    foreach (var n in response.nodes)
                    {
                        nodeDataCache[n.node_id] = n;
                        IndexNodeCell(n.node_id);
                    }
                }
            }
            finally
            {
                foreach (int nodeId in batch)
                {
                    fetchingIntersectionNodeIds.Remove(nodeId);
                }
            }
        }
    }

    private IEnumerator UpdateNodeIntersectionMeshAfterNodeReady(int nodeId)
    {
        if (!nodeDataCache.ContainsKey(nodeId))
        {
            // LOD一括更新では、この取得完了を待ってから道路端の再生成へ進む。
            // StartCoroutineだけを投げると、カメラ移動やAPI応答順によって交差点接続点の反映が次回更新へ漏れる。
            if (fetchingIntersectionNodeIds.Add(nodeId))
            {
                yield return FetchNodeAndUpdateIntersection(nodeId);
            }
            else
            {
                while (fetchingIntersectionNodeIds.Contains(nodeId))
                {
                    yield return null;
                }

                if (nodeDataCache.ContainsKey(nodeId))
                {
                    UpdateNodeIntersectionMesh(nodeId);
                }
            }
            yield break;
        }

        UpdateNodeIntersectionMesh(nodeId);
    }

    private bool HasPathRepresentation(PathSummary path)
    {
        if (pathObjectsCache.TryGetValue(path.path_id, out var obj) && obj != null) return true;
        return TunnelSectionUtil.IsUndergroundPath(path) || FarPathHasHiddenTunnel(path);
    }

    /// <param name="batchMember">偽なら、LODバッチ中でもバッチの重複除外に数えない。バッチの外から割り込む再生成
    /// (地形追従キュー)がバッチ自身の後段の再生成(切断点確定後)を握りつぶさないようにする。</param>
    private bool TryRebuildPathOnce(PathSummary path, LodTier tier, bool updateIntersections, bool forceRebuild, string reason, bool batchMember = true)
    {
        if (path == null) return false;
        RenderWorkScheduler.CountEvent(forceRebuild ? "path.forceRebuildRequested" : "path.lodRebuildRequested");
        if (forceRebuild && reason.StartsWith("地形追従", StringComparison.Ordinal))
            RenderWorkScheduler.CountEvent("path.terrainRebuildRequested");

        if (batchMember && isLodPathBatchActive && !lodBatchRebuiltPathIds.Add(path.path_id))
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"PathManager: 再描画 重複スキップ path{path.path_id} reason={reason}");
            // 重複スキップしても、非表示になっている既存オブジェクトは再表示する。
            if (pathObjectsCache.TryGetValue(path.path_id, out GameObject existing) && existing != null && !existing.activeSelf)
            {
                existing.SetActive(true);
                EnsureEndIntersectionsVisible(path);
            }
            return false;
        }

        // データティアが Near/Dynamic でも、カメラから遠く小さく見えるパスは帯(Far)で描く。
        Camera drawCam = Camera.main;
        Vector3 drawCamPos = drawCam != null ? drawCam.transform.position : Vector3.zero;
        DrawLod drawLod = DeterminePathDrawLod(path, tier, drawCamPos, out double detailFrom, out double detailTo);
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"PathManager: 再描画 path{path.path_id} tier={tier} draw={drawLod} range=({detailFrom:F2}〜{detailTo:F2}) reason={reason}");
        if (drawLod == DrawLod.Far)
            CreateOrUpdateFarPath(path, forceRebuild);
        else
            CreateOrUpdatePath(path, updateIntersections, forceRebuild, drawLod, detailFrom, detailTo);

        // API受信やキュー投入を描画完了として扱わない。中断時は同じ版でも再生成する。
        if (HasPathRepresentation(path)) pathVersionCache[path.path_id] = path.version;
        return true;
    }

    private List<Vector2Int> BuildChangedCellsForPathLog(List<Vector2Int> cellsToLoad, List<Vector2Int> cellsToUnload)
    {
        HashSet<Vector2Int> uniqueCells = new HashSet<Vector2Int>();
        if (cellsToLoad != null)
        {
            foreach (var cell in cellsToLoad) uniqueCells.Add(cell);
        }
        if (cellsToUnload != null)
        {
            foreach (var cell in cellsToUnload) uniqueCells.Add(cell);
        }

        List<Vector2Int> cells = new List<Vector2Int>(uniqueCells);
        cells.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        return cells;
    }

    private HashSet<int> CollectAffectedPathIds(List<Vector2Int> changedCells)
    {
        HashSet<int> pathIds = new HashSet<int>();
        if (changedCells == null) return pathIds;

        foreach (var cell in changedCells)
        {
            if (!pathIdsByCell.TryGetValue(cell, out HashSet<int> lookupPathIds)) continue;
            foreach (int pathId in lookupPathIds)
            {
                pathIds.Add(pathId);
            }
        }

        return pathIds;
    }

    /// <summary>
    /// 複数セルにまたがるパスの同期処理（差分更新用）。
    /// APIレスポンスに含まれないパスのうち、指定セル群に含まれるものを削除する。
    /// </summary>
    private void SyncPathsInCells(List<Vector2Int> cells, HashSet<int> receivedIds, HashSet<int> affectedNodeIds)
    {
        List<int> pathsToRemove = new List<int>();

        // 通過セル登録済みのパスはセル逆引きで候補を絞る。未登録のGOだけ従来の位置判定に回す。
        HashSet<int> candidateIds = new HashSet<int>();
        foreach (var cell in cells)
        {
            if (!pathIdsByCell.TryGetValue(cell, out HashSet<int> ids)) continue;
            foreach (int id in ids)
            {
                if (!receivedIds.Contains(id)) candidateIds.Add(id);
            }
        }
        foreach (int pathId in candidateIds)
        {
            if (pathObjectsCache.TryGetValue(pathId, out GameObject obj) && obj != null) pathsToRemove.Add(pathId);
        }
        foreach (int pathId in unregisteredPathObjectIds)
        {
            if (receivedIds.Contains(pathId) || candidateIds.Contains(pathId)) continue;
            if (pathObjectsCache.TryGetValue(pathId, out GameObject obj) && obj != null && IsPathInCells(pathId, obj, cells)) pathsToRemove.Add(pathId);
        }

        foreach (int pathId in pathsToRemove)
        {
            if (pathDataCache.TryGetValue(pathId, out PathSummary removedPath))
            {
                if (removedPath.start_node_id > 0) affectedNodeIds.Add(removedPath.start_node_id);
                if (removedPath.end_node_id > 0) affectedNodeIds.Add(removedPath.end_node_id);

                // 線路の結合描画: 撤去された線路と並走していた線路の結合を解く
                // （防音壁・架線柱の復活）ため、近接線路を再生成対象に積む。
                // 通過セルキャッシュを参照するため UnregisterPathCells より前に行うこと。
                EnqueueAdjacentTrackNeighborRebuilds(removedPath);
                EnqueueAdjacentRoadMergeRebuilds(removedPath, affectedNodeIds);
            }

            UnregisterPathDataById(pathId);
            if (removedPath != null) NotifyDepotTerminals(removedPath);
            UnregisterPathCells(pathId);
            UnregisterPathSegments(pathId);
            DropEndNodeIntersectionSignatures(removedPath);
            pathStartConnectionPoints.Remove(pathId);
            pathEndConnectionPoints.Remove(pathId);
            pathStartSidewalkSetbacks.Remove(pathId);
            pathEndSidewalkSetbacks.Remove(pathId);
            DropRoadEndJoins(pathId);
            pathVersionCache.Remove(pathId);

            if (pathObjectsCache.TryGetValue(pathId, out GameObject obj))
            {
                if (obj != null) DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Path, DrawLodInfo.GetDrawLod(obj, DrawLod.Normal), null);
                Destroy(obj);
                RemovePathObject(pathId);
            }
            closePathIds.Remove(pathId);
        }
    }

    /// <summary>
    /// ノードをデータ座標のセルへ索引する。ノードGO・交差点メッシュはノード座標のXZに置かれるので、
    /// transform を読まずデータの座標で求める(GetAbsolutePosition は地形高さを引くので使わない)。
    /// </summary>
    private void IndexNodeCell(int nodeId)
    {
        if (!nodeDataCache.TryGetValue(nodeId, out NodeData node) || node?.position == null || node.position.Length < 2)
        {
            UnindexNodeCell(nodeId);
            return;
        }
        Vector2Int cell = TerrainManager.WorldToCell(node.position[0], node.position.Length >= 3 ? node.position[2] : node.position[1]);
        if (nodeCellById.TryGetValue(nodeId, out Vector2Int indexed) && indexed == cell) return;
        UnindexNodeCell(nodeId);
        nodeCellById[nodeId] = cell;
        if (!nodeIdsByCell.TryGetValue(cell, out HashSet<int> ids))
        {
            ids = new HashSet<int>();
            nodeIdsByCell[cell] = ids;
        }
        ids.Add(nodeId);
    }

    private void UnindexNodeCell(int nodeId)
    {
        if (!nodeCellById.Remove(nodeId, out Vector2Int cell)) return;
        if (!nodeIdsByCell.TryGetValue(cell, out HashSet<int> ids)) return;
        ids.Remove(nodeId);
        if (ids.Count == 0) nodeIdsByCell.Remove(cell);
    }

    /// <summary>
    /// 複数セルにまたがるノードの同期処理（差分更新用）。
    /// APIレスポンスに含まれないノードのうち、指定セル群に含まれるものを削除する。
    /// </summary>
    private void SyncNodesInCells(List<Vector2Int> cells, HashSet<int> receivedNodeIds)
    {
        List<int> nodesToRemove = new List<int>();

        foreach (var cell in cells)
        {
            if (!nodeIdsByCell.TryGetValue(cell, out HashSet<int> ids)) continue;
            foreach (int nodeId in ids)
            {
                if (receivedNodeIds.Contains(nodeId)) continue;
                if (nodeObjectsCache.TryGetValue(nodeId, out GameObject obj) && obj != null) nodesToRemove.Add(nodeId);
            }
        }

        foreach (var nodeId in nodesToRemove)
        {
            if (nodeObjectsCache.TryGetValue(nodeId, out GameObject obj))
            {
                Destroy(obj);
                nodeObjectsCache.Remove(nodeId);
                // 空間ハッシュからも外す（残すと削除済みノードへ吸着してしまう）
                if (nodeDataCache.TryGetValue(nodeId, out NodeData removedNode))
                {
                    nodeGrid?.Remove(removedNode, removedNode.GetAbsolutePosition());
                }
                nodeDataCache.Remove(nodeId);
                UnindexNodeCell(nodeId);
                RemoveNodeIntersectionMesh(nodeId);
            }
        }
        if (nodesToRemove.Count > 0)
        {
            NodeDataVersion++;
        }
    }

    private void CreateOrUpdateFarPath(PathSummary path, bool forceRebuild = false)
    {
        SavePathToCache(path);
        EnsureEndIntersectionsVisible(path);

        // 地下パス、および丘を貫く非追従トンネルパスは遠景の簡易表示を行わない（非表示）。
        // 始点・終点を結ぶ直線ストリップは埋設区間を無視して地表を横切ってしまい、
        // トンネルなのに黒帯が地面に描かれてしまうため。遠距離では坑口・開削区間も
        // 視認できないので、非表示で問題ない。谷を渡るだけの非追従橋は直線ストリップを描く。
        // 既存オブジェクト（Near詳細）はプーリングの慣習に合わせて破棄せず非アクティブにして残す。
        if (TunnelSectionUtil.IsUndergroundPath(path) || FarPathHasHiddenTunnel(path))
        {
            if (pathObjectsCache.TryGetValue(path.path_id, out GameObject existingUnderground)
                && existingUnderground != null && existingUnderground.activeSelf)
            {
                existingUnderground.SetActive(false);
            }
            RegisterPathCells(path);
            return;
        }

        string renderKey = PathRenderKey(path, DrawLod.Far, forceRebuild);
        if (RestorePathLod(path, renderKey, DrawLod.Far, false)) return;
        geometryRevisions.TryGetValue(path.path_id, out int buildRevision);
        long buildSerial = ++pathBuildSerialCounter;
        forceRebuild = true;
        GameObject oldPath = null;
        if (pathObjectsCache.TryGetValue(path.path_id, out GameObject existingPath))
        {
            if (existingPath != null)
            {
                var existingIdInfo = existingPath.GetComponent<ObjectIdInfo>();
                int currentStatus = existingIdInfo != null ? existingIdInfo.status : 0;
                bool existingIsFar = existingIdInfo != null && existingIdInfo.isFarObject;
                // Near詳細オブジェクトの誤再利用を防ぐため、遠景オブジェクトであることを確認する。
                // forceRebuild（version が変わった＝形状が変わり得る）のときは使い回さず作り直す。
                if (!forceRebuild && currentStatus == path.status && existingIsFar)
                {
                    // プールから再利用: 非表示だったオブジェクトを再表示
                    if (!existingPath.activeSelf)
                        existingPath.SetActive(true);
                    SavePathToCache(path);
                    RegisterPathCells(path);
                    ReevaluateAwaitingIntersectionsForPath(path);
                    UpdatePlannedIcon(existingPath, path, path.status == Constants.STATUS_PENDING);
                    return;
                }
                oldPath = existingPath;
            }
            else
            {
                RemovePathObject(path.path_id);
            }
        }

        int status = path.status;
        GameObject pathGO = new GameObject($"{PATH_PREFIX}{path.path_id}");
        var idInfo = pathGO.AddComponent<ObjectIdInfo>();
        idInfo.id = path.path_id;
        idInfo.status = status;
        idInfo.isFarObject = true;
        Transform container = GetContainer("Paths");
        if (container != null) pathGO.transform.SetParent(container);
        
        pathGO.tag = PATH_TAG;

        var startPointList = path.GetStartPointList();
        var endPointList = path.GetEndPointList();

        if (startPointList == null || startPointList.Count < 2 || endPointList == null || endPointList.Count < 2)
        {
            Destroy(pathGO);
            return;
        }

        Vector3 startPos = GetPathPointPosition(startPointList);
        Vector3 endPos = GetPathPointPosition(endPointList);

        // 3D描画用に base_height を引く（CalculatePathPoints と同じシフト）。
        float farBaseHeight = GetPathBaseHeight(path.path_variant);
        startPos.y -= farBaseHeight;
        endPos.y -= farBaseHeight;

        float startRelY = (startPointList != null && startPointList.Count >= 3) ? startPointList[1] : 0f;
        float endRelY = (endPointList != null && endPointList.Count >= 3) ? endPointList[1] : 0f;

        // 地形追従しないパス（桁橋・非追従指定など）は端点も地形へ吸着させない。
        bool farVariantFollows = GetPathFollowTerrain(path);
        if (farVariantFollows && startRelY == farBaseHeight)
            startPos.y = Utils.GetGroundHeight(new Vector3(startPos.x, 1000f, startPos.z)) + RoadPartHeights.ROAD_SPLINE_OFFSET;
        if (farVariantFollows && endRelY == farBaseHeight)
            endPos.y = Utils.GetGroundHeight(new Vector3(endPos.x, 1000f, endPos.z)) + RoadPartHeights.ROAD_SPLINE_OFFSET;

        List<Vector3> points = new List<Vector3> { startPos, endPos };
        ApplyStationGrade(path, points);
        // 曲線・経由点付きのパスは端点だけの直線だと形が崩れるので、描画点列を間引いて曲線を保つ。
        bool hasWaypoints = (path.GetWaypointList()?.Count ?? 0) > 0;
        if (path.curved || hasWaypoints)
        {
            List<Vector3> curvePoints = CalculatePathPoints(path);
            if (curvePoints.Count >= 2)
            {
                points = DecimatePolyline(curvePoints, FAR_STRIP_SAMPLE_INTERVAL);
            }
        }
        float width = 6f;
        var masterData = MasterDataStore.Instance;
        if (masterData != null)
        {
            var pathMaster = masterData.GetPathMaster(path.path_variant);
            if (pathMaster != null) width = pathMaster.width;
        }

        bool constructing = status == Constants.STATUS_CONSTRUCTING && PathMeshFactory.GetModelType(path) != "none";
        bool powerLine = PowerLinePath.Handles(path);
        bool farPlatform = !constructing && FarPlatformPath.Handles(path);
        Mesh mesh = constructing || powerLine || farPlatform ? null : PathMeshFactory.BuildStripMesh(points, width);
        if (powerLine) PowerLinePath.GenerateInto(pathGO, path, CalculatePathPoints(path), DrawLod.Far);
        else if (constructing) ConstructionPathBuilder.GenerateFar(pathGO, path, points, width, GetRoadMaterial());
        else if (farPlatform) FarPlatformPath.GenerateInto(pathGO, path, points);
        if (mesh != null)
        {
            MeshFilter mf = pathGO.AddComponent<MeshFilter>();
            mf.mesh = mesh;
            MeshRenderer mr = pathGO.AddComponent<MeshRenderer>();
            Material mat = GetFarRoadDepthMaterial();
            if (mat != null) mr.sharedMaterial = mat;
        }

        var identity = pathGO.AddComponent<RenderCacheIdentity>();
        identity.key = renderKey;
        identity.mesh = mesh;

        var stats = pathGO.AddComponent<VisualPathStats>();
        stats.InitFrom(path);
        var visual = pathGO.AddComponent<VisualModeObject>();
        visual.objectType = InputObjectType.PATH;
        visual.SetStatus(status);
        visual.SetFilterHidden(ObjectVisibilityFilter.IsHidden(InputObjectType.PATH, path.path_id));

        SavePathToCache(path);
        RegisterPathCells(path);
        StampPathBuild(pathGO, path, buildRevision, buildSerial);
        UpdatePlannedIcon(pathGO, path, path.status == Constants.STATUS_PENDING);
        DrawLodInfo.Ensure(pathGO).drawLod = DrawLod.Far;
        closePathIds.Remove(path.path_id);
        DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Path,
            oldPath != null ? DrawLodInfo.GetDrawLod(oldPath, DrawLod.Normal) : (DrawLod?)null, DrawLod.Far);

        // 辞書は即座に新しいオブジェクトに更新する。
        // これにより、短時間に同じIDで再描画が呼ばれてもオブジェクトが増殖しない。
        SetPathObject(path.path_id, pathGO);

        // 固定区間パーツは非同期に揃うため、初回生成が完了してから旧表示と差し替える。
        if (oldPath != null)
        {
            PathPresentation.Replace(pathGO, oldPath, lodCache);
        }
        ReevaluateAwaitingIntersectionsForPath(path);
    }

    public List<PathSummary> GetAllPaths()
    {
        return pathDataCache.Values.ToList();
    }

    /// <summary>
    /// 公共交通パス(PATH_TRANSPORT)の描画用データを列挙する。
    /// TransitOverlayManager が「設置済みの公共交通路線を種別色の帯」で描画するために使う。
    /// path_type が PATH_TRANSPORT のキャッシュ済みパスについて、path_id・variant(=区間パスの
    /// バリアント=transit_master の transit_path_id)・地形追従済みの点列を返す。
    /// メッシュ生成器を持たない PATH_TRANSPORT も pathDataCache には存在するため、
    /// 通常のパスと同じ点列算出(CalculatePathPoints)がそのまま使える。
    /// </summary>
    public List<(int pathId, int variant, List<Vector3> points)> GetTransitPathsForOverlay()
    {
        var result = new List<(int, int, List<Vector3>)>();
        foreach (var kvp in pathDataCache)
        {
            var path = kvp.Value;
            if (path == null || path.path_type != Constants.PATH_TRANSPORT) continue;
            // STATUS_ACTIVE のみを対象にすると計画中区間が見えないため、
            // ステータスは問わずキャッシュ済みの全公共交通パスを描画対象にする。
            var points = CalculatePathPoints(path);
            if (points == null || points.Count < 2) continue;
            result.Add((path.path_id, path.path_variant, points));
        }
        return result;
    }

    /// <summary>
    /// 指定パスIDの地形追従済み点列(中心線)を返す。
    /// TransitOverlayManager が route_path_ids から下地(道路/線路)パスの実ジオメトリを
    /// 連結して路線を描くために使う。道路/線路は curved=true でベジエ曲線サンプリングされる。
    /// pathDataCache 未読込(=まだロードされていないセルのパス)なら false。
    /// </summary>
    /// <summary>
    /// 生成済みのパスへ、オブジェクト単位の表示フィルタ(ObjectVisibilityFilter)を反映し直す。
    /// 非表示集合が変わったときに呼ばれる。未ロードのパスは生成時に自分で引くため対象外。
    /// </summary>
    public void RefreshVisibilityFilter()
    {
        RefreshUndergroundVisibility();
        foreach (var pair in pathObjectsCache)
        {
            ObjectVisibilityFilter.ApplyToObject(pair.Value, InputObjectType.PATH, pair.Key);
        }
    }

    public bool TryGetPathPolyline(int pathId, out List<Vector3> points, bool trimIntersection = true)
    {
        points = null;
        if (!pathDataCache.TryGetValue(pathId, out PathSummary path) || path == null)
            return false;
        var pts = CalculatePathPoints(path, trimIntersection);
        if (pts == null || pts.Count < 2)
            return false;
        points = pts;
        return true;
    }

    /// <summary>
    /// 未切断の中心線を読み取り専用で返す。キャッシュ済みなら共有の点列をそのまま返すので複製しない
    /// (毎回描き直すオーバーレイが、点列を読むだけのためにリストを確保しないようにする)。書き換えないこと。
    /// </summary>
    public bool TryGetUntrimmedPolylineShared(int pathId, out IReadOnlyList<Vector3> points)
    {
        points = null;
        if (!pathDataCache.TryGetValue(pathId, out PathSummary path) || path == null)
            return false;
        List<Vector3> pts = GetUntrimmedPoints(path, out _);
        if (pts == null || pts.Count < 2)
            return false;
        points = pts;
        return true;
    }

    /// <summary>画面ラベルの中心。中心点を描画中心線へ射影し、高架・勾配の高さも保つ。</summary>
    public bool TryGetPathLabelPosition(int pathId, out Vector3 position)
    {
        if (pathLabelPositions.TryGetValue(pathId, out position)) return true;
        position = default;
        if (!pathDataCache.TryGetValue(pathId, out var path)) return false;
        var center = path.GetCenterPointArray();
        if (center == null || center.Length < 2) return false;
        // Far切替中には古い描画点列が残るため、無効化後は最新データから共通の形状取得を通す。
        if (!TryGetPathPolyline(pathId, out var points)) return false;
        Vector3 target = new Vector3(center[0], 0f, center[1]);
        float nearest = float.PositiveInfinity;
        for (int i = 1; i < points.Count; i++)
        {
            Vector3 projected = PolylineUtils.ClosestPointOnSegmentXZ(target, points[i - 1], points[i]);
            float distance = Utils.XZDistanceSquared(projected, target);
            if (distance >= nearest) continue;
            nearest = distance;
            position = projected;
        }
        if (float.IsPositiveInfinity(nearest)) return false;
        pathLabelPositions[pathId] = position;
        return true;
    }

    /// <summary>描画円の外接矩形にある道路候補をセル逆引きで返す。厳密な円交差は呼び出し側で判定する。</summary>
    public List<PathSummary> GetPathsNearCircle(Vector3 center, float radius)
    {
        float size = TerrainManager.CellSize;
        var ids = new HashSet<int>();
        var result = new List<PathSummary>();
        int minX = Mathf.FloorToInt((center.x - radius) / size), maxX = Mathf.FloorToInt((center.x + radius) / size);
        int minZ = Mathf.FloorToInt((center.z - radius) / size), maxZ = Mathf.FloorToInt((center.z + radius) / size);
        for (int x = minX; x <= maxX; x++)
        for (int z = minZ; z <= maxZ; z++)
        {
            if (!pathIdsByCell.TryGetValue(new Vector2Int(x, z), out var cellIds)) continue;
            foreach (int id in cellIds)
                if (ids.Add(id) && pathDataCache.TryGetValue(id, out var path)) result.Add(path);
        }
        return result;
    }

    public GameObject GetNodeObject(int nodeId)
    {
        if (nodeObjectsCache.TryGetValue(nodeId, out GameObject obj))
        {
            return obj;
        }
        return null;
    }

    /// <summary>
    /// ノードの交差点メッシュ GO(NodeMesh_*。子に Sidewalk / Rail / Signals を持つ)。無ければ null。
    /// GetNodeObject が返すノード円柱とは別物。
    /// </summary>
    public GameObject GetNodeIntersectionObject(int nodeId)
    {
        return nodeIntersectionMeshes.TryGetValue(nodeId, out GameObject obj) && obj != null ? obj : null;
    }

    /// <summary>
    /// 切断後の描画中心線(TryGetPathPolyline(trim=true) の結果)の版。中心線が変われば必ず値が変わる。
    /// キャッシュ対象外なら -1 で、呼び出し側は点列の比較にフォールバックすること。メインスレッド専用。
    /// </summary>
    public long GetPathGeometryRevision(int pathId)
    {
        if (!pathDataCache.TryGetValue(pathId, out PathSummary path)) return -1;
        PointsEntry entry = GetUntrimmedEntry(path);
        if (entry == null) return -1;
        connectionRevisions.TryGetValue(pathId, out int connection);
        return ((long)entry.revision << 32) | (uint)connection;
    }

    public GameObject GetPathObject(int pathId)
    {
        if (pathObjectsCache.TryGetValue(pathId, out GameObject obj))
        {
            return obj;
        }
        return null;
    }

    /// <summary>
    /// 指定したIDのパスデータを取得する。
    /// キャッシュに存在しない場合は null を返す。
    /// </summary>
    public PathSummary GetPathData(int pathId)
    {
        if (pathDataCache.TryGetValue(pathId, out PathSummary path))
        {
            return path;
        }
        return null;
    }

    /// <summary>
    /// 指定した道路が、targetPathIds内のいずれかの道路とノードを共有しているかを判定する。
    /// 地区編集において、選択道路との接続性を確認するために使用する。
    /// 両端のノードID（start_node_id, end_node_id）のいずれかが
    /// 対象道路のノードと一致すれば接続されているとみなす。
    /// </summary>
    public bool IsPathConnectedToSet(int pathId, HashSet<int> targetPathIds)
    {
        PathSummary path = GetPathData(pathId);
        if (path == null) return false;

        foreach (int id in targetPathIds)
        {
            PathSummary other = GetPathData(id);
            if (other == null) continue;
            if (path.start_node_id == other.start_node_id ||
                path.start_node_id == other.end_node_id ||
                path.end_node_id == other.start_node_id ||
                path.end_node_id == other.end_node_id)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 指定したzone_idに所属する全道路のIDセットを返す。
    /// ドラッグによる地区拡張時に、接続先候補となる既存地区の道路を取得するために使用する。
    /// </summary>
    public HashSet<int> GetZonePathIds(int zoneId)
    {
        var result = new HashSet<int>();
        foreach (var kvp in pathDataCache)
        {
            if (kvp.Value.zone_id == zoneId) result.Add(kvp.Key);
        }
        return result;
    }

    /// <summary>
    /// 道路の計画中アイコンを作成・更新・削除する。
    /// 保留中(status=11)の道路に対して、center_pointの位置（地形高さを考慮）に2Dアイコンを表示する。
    /// アイコンは道路オブジェクトの子として管理され、道路と同じタイミングで表示/非表示が切り替わる。
    /// </summary>
    private void UpdatePlannedIcon(GameObject pathGO, PathSummary path, bool isPlanned)
    {
        Transform iconTrans = pathGO.transform.Find("PlannedIcon");
        if (!isPlanned)
        {
            if (iconTrans != null)
                Destroy(iconTrans.gameObject);
            return;
        }

        if (plannedPathIcon == null)
            return;

        GameObject iconGO;
        if (iconTrans == null)
        {
            iconGO = new GameObject("PlannedIcon");
            iconGO.transform.SetParent(pathGO.transform, false);
            SpriteRenderer sr = iconGO.AddComponent<SpriteRenderer>();
            sr.sprite = plannedPathIcon;
            sr.sortingOrder = 10;
            iconGO.AddComponent<Billboard>();
        }
        else
        {
            iconGO = iconTrans.gameObject;
            if (!iconGO.activeSelf)
                iconGO.SetActive(true);
        }

        float[] cp = path.GetCenterPointArray();
        Vector3 iconPos;
        if (cp != null && cp.Length >= 2)
        {
            float groundHeight = Utils.GetGroundHeight(new Vector3(cp[0], 1000f, cp[1]));
            iconPos = new Vector3(cp[0], groundHeight + plannedPathIconYOffset, cp[1]);
        }
        else
        {
            var startList = path.GetStartPointList();
            var endList = path.GetEndPointList();
            if (startList != null && startList.Count >= 2 && endList != null && endList.Count >= 2)
            {
                Vector3 start = new Vector3(startList[0], 0, startList[1]);
                Vector3 end = new Vector3(endList[0], 0, endList[1]);
                Vector3 mid = (start + end) * 0.5f;
                float groundHeight = Utils.GetGroundHeight(new Vector3(mid.x, 1000f, mid.z));
                iconPos = new Vector3(mid.x, groundHeight + plannedPathIconYOffset, mid.z);
            }
            else
            {
                iconPos = pathGO.transform.position + Vector3.up * plannedPathIconYOffset;
            }
        }

        iconGO.transform.position = iconPos;
        iconGO.transform.localScale = Vector3.one * plannedPathIconScale;
    }

    protected override void UnloadCell(Vector2Int cellIndex)
    {
        UnloadCellsObjects(new List<Vector2Int> { cellIndex });
    }

    private readonly HashSet<Vector2Int> unloadCellSet = new HashSet<Vector2Int>();

    /// <summary>
    /// 非表示化を待つ離脱セル→登録世代。PathUnloadWorker がフレーム予算内で消化する。
    /// 常駐へ戻ったセルは ApplyLodCellChanges で外す。戻った後のバッチが適用前に取り消されても、
    /// 再び常駐外になったときに隠し残しを回収できるよう、cellsToKeepVisible に含まれる間は保留して残す。
    /// </summary>
    private readonly Dictionary<Vector2Int, int> pendingUnloadCells = new Dictionary<Vector2Int, int>();
    private int unloadGeneration;
    private Coroutine pathUnloadWorker;

    /// <summary>PathUnloadWorker が1セル分の対象IDを写し取る再利用リスト。yield を跨いで HashSet を列挙しないために使う。</summary>
    private readonly List<int> unloadScanPathIds = new List<int>();
    private readonly List<int> unloadScanNodeIds = new List<int>();
    private readonly List<int> unloadHiddenEndNodeIds = new List<int>();

    /// <summary>
    /// 指定セル群を分割アンロードの待ち行列へ積む。ノード・パス・交差点メッシュの非表示化(破棄せず残す)は
    /// PathUnloadWorker が予算内で行う(離脱セルの道路数百本を1ステップで SetActive(false) するとスパイクになるため)。
    /// 他のロード対象セル（cellsToKeepVisible）に跨っているパスは非表示にしない。
    /// これにより、セル境界を跨ぐ道路がカメラ移動時に一瞬消えるチラつきを防止する。
    /// 通過セル未登録のGOはセル索引から辿れないので、ここで同期に隠す(通常はほぼ空)。
    /// </summary>
    private void UnloadCellsObjects(List<Vector2Int> cells)
    {
        if (cells == null || cells.Count == 0) return;
        unloadCellSet.Clear();
        foreach (var cell in cells)
        {
            if (!cellsToKeepVisible.Contains(cell)) unloadCellSet.Add(cell);
        }
        if (unloadCellSet.Count == 0) return;

        foreach (var cell in unloadCellSet) pendingUnloadCells[cell] = ++unloadGeneration;
        if (pathUnloadWorker == null)
            pathUnloadWorker = RenderWorkScheduler.Start(this, PathUnloadWorker(), "PathManager", () => false);

        // 通過セル未登録のGOは辞書の列挙中に隠さず、集めてから隠す。
        List<Vector2Int> unloadCells = null;
        List<Vector2Int> keepCells = null;
        List<int> unregisteredToHide = null;
        foreach (int pathId in unregisteredPathObjectIds)
        {
            if (!pathObjectsCache.TryGetValue(pathId, out GameObject pathGO) || pathGO == null || !pathGO.activeSelf) continue;
            unloadCells ??= new List<Vector2Int>(unloadCellSet);
            keepCells ??= new List<Vector2Int>(cellsToKeepVisible);
            if (IsPathInCells(pathId, pathGO, unloadCells) && !IsPathInCells(pathId, pathGO, keepCells))
                (unregisteredToHide ??= new List<int>()).Add(pathId);
        }
        unloadCellSet.Clear();
        if (unregisteredToHide == null) return;
        List<int> hiddenEndNodeIds = null;
        foreach (int pathId in unregisteredToHide) HideUnloadedPath(pathId, pathObjectsCache[pathId], ref hiddenEndNodeIds);
        if (hiddenEndNodeIds != null)
        {
            foreach (int nodeId in hiddenEndNodeIds) HideIntersectionMeshIfOrphaned(nodeId);
        }
    }

    /// <summary>待ち行列のセルが、まだ非表示化してよい状態(同じ世代で登録中、かつ常駐外)か。</summary>
    private bool IsUnloadCellCurrent(Vector2Int cell, int generation)
        => pendingUnloadCells.TryGetValue(cell, out int current) && current == generation && !cellsToKeepVisible.Contains(cell);

    /// <summary>
    /// 離脱セルのノード・パス・交差点メッシュを非表示にする常駐ワーカー。1セルずつIDを写し取り、フレーム予算で分割する。
    /// 再開のたびに、セルが常駐へ戻っていないか・再登録されていないかを確かめ、変わっていればそのスナップショットを捨てる。
    /// パスは隠す直前に、その時点の cellsToKeepVisible に跨っていないかを確かめる(待機中に後続バッチが表示し直したものを隠さない)。
    /// </summary>
    private IEnumerator PathUnloadWorker()
    {
        var watch = new System.Diagnostics.Stopwatch();
        while (true)
        {
            Vector2Int cell = default;
            int generation = 0;
            bool found = false;
            foreach (var pair in pendingUnloadCells)
            {
                if (cellsToKeepVisible.Contains(pair.Key)) continue;
                cell = pair.Key;
                generation = pair.Value;
                found = true;
                break;
            }
            if (!found)
            {
                yield return null;
                continue;
            }

            unloadScanPathIds.Clear();
            unloadScanNodeIds.Clear();
            unloadHiddenEndNodeIds.Clear();
            if (pathIdsByCell.TryGetValue(cell, out HashSet<int> pathIds)) unloadScanPathIds.AddRange(pathIds);
            if (nodeIdsByCell.TryGetValue(cell, out HashSet<int> nodeIds)) unloadScanNodeIds.AddRange(nodeIds);
            List<int> hiddenEndNodeIds = unloadHiddenEndNodeIds;

            bool interrupted = false;
            int step = 0;
            watch.Restart();
            for (int phase = 0; phase < 3 && !interrupted; phase++)
            {
                List<int> ids = phase == 1 ? unloadScanPathIds : unloadScanNodeIds;
                for (int i = 0; i < ids.Count; i++)
                {
                    int id = ids[i];
                    if (phase == 0)
                    {
                        if (nodeObjectsCache.TryGetValue(id, out GameObject nodeGO) && nodeGO != null && nodeGO.activeSelf) nodeGO.SetActive(false);
                    }
                    else if (phase == 1)
                    {
                        if (pathObjectsCache.TryGetValue(id, out GameObject pathGO) && pathGO != null && pathGO.activeSelf
                            && !(pathCellsById.TryGetValue(id, out HashSet<Vector2Int> pathCells) && AnyCellIn(pathCells, cellsToKeepVisible)))
                            HideUnloadedPath(id, pathGO, ref hiddenEndNodeIds);
                    }
                    else
                    {
                        // 交差点メッシュは道路GOの子ではないため、接続道路がすべて隠れたときに一緒に隠す。
                        // 1本でも表示中なら残し、見えている道路の端から交差点を欠けさせない。
                        if (nodeIntersectionMeshes.TryGetValue(id, out GameObject meshGO) && meshGO != null && meshGO.activeSelf
                            && !HasVisibleConnectedPath(id))
                            meshGO.SetActive(false);
                    }
                    if ((++step & 15) == 0 && watch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
                    {
                        yield return RenderWorkScheduler.Continue;
                        watch.Restart();
                        if (!IsUnloadCellCurrent(cell, generation))
                        {
                            interrupted = true;
                            break;
                        }
                    }
                }
            }
            // 別セルにある交差点も、ここで最後の接続道路が隠れたなら隠す。中断時も、隠した道路の分は済ませる
            // (隠した道路は再走査で拾われないため)。
            foreach (int nodeId in unloadHiddenEndNodeIds) HideIntersectionMeshIfOrphaned(nodeId);
            unloadScanPathIds.Clear();
            unloadScanNodeIds.Clear();
            unloadHiddenEndNodeIds.Clear();
            if (!interrupted && IsUnloadCellCurrent(cell, generation))
                pendingUnloadCells.Remove(cell);
            yield return RenderWorkScheduler.Continue;
        }
    }

    private static bool AnyCellIn(HashSet<Vector2Int> cells, HashSet<Vector2Int> set)
    {
        foreach (var cell in cells)
        {
            if (set.Contains(cell)) return true;
        }
        return false;
    }

    private void HideUnloadedPath(int pathId, GameObject pathGO, ref List<int> hiddenEndNodeIds)
    {
        pathGO.SetActive(false);
        anchorPolylineCache.Remove(pathId);
        DropUntrimmedPoints(pathId);
        if (pathDataCache.TryGetValue(pathId, out PathSummary hiddenPath))
        {
            hiddenEndNodeIds ??= new List<int>();
            hiddenEndNodeIds.Add(hiddenPath.start_node_id);
            hiddenEndNodeIds.Add(hiddenPath.end_node_id);
        }
    }

    private void HideIntersectionMeshIfOrphaned(int nodeId)
    {
        if (nodeIntersectionMeshes.TryGetValue(nodeId, out GameObject meshGO) && meshGO != null && meshGO.activeSelf
            && !HasVisibleConnectedPath(nodeId))
            meshGO.SetActive(false);
    }

    /// <summary>ノードに接続する道路のうち、表示中(アクティブ)のGOを持つものが1本でもあるか。</summary>
    private bool HasVisibleConnectedPath(int nodeId)
    {
        if (!pathsByNodeIndex.TryGetValue(nodeId, out var pathIds)) return false;
        foreach (int pathId in pathIds)
        {
            if (pathObjectsCache.TryGetValue(pathId, out GameObject pathGO) && pathGO != null && pathGO.activeSelf) return true;
        }
        return false;
    }

    /// <summary>
    /// アンロードで隠した両端の交差点メッシュを再表示する。道路GOを表示へ戻す経路と、
    /// 道路を生成・復元する入口(CreateOrUpdatePath / CreateOrUpdateFarPath)から必ず呼ぶ。
    /// </summary>
    private void EnsureEndIntersectionsVisible(PathSummary path)
    {
        if (path == null) return;
        ShowIntersectionMesh(path.start_node_id);
        ShowIntersectionMesh(path.end_node_id);
    }

    private void ShowIntersectionMesh(int nodeId)
    {
        if (nodeIntersectionMeshes.TryGetValue(nodeId, out GameObject meshGO) && meshGO != null && !meshGO.activeSelf)
            meshGO.SetActive(true);
    }

    /// <summary>
    /// ノード生成処理
    /// </summary>
    /// <summary>
    /// ノードキャッシュ（nodeDataCache / nodeGrid）の実変更カウンタ。
    /// ノードの新規追加・位置/タイプの実変更・削除があった場合にのみインクリメントされる
    /// （カメラのセル跨ぎによる再取得は大半が無変更のため、無条件には増やさない）。
    /// RoadPlanManager が背景オーバーレイ（既存ノード表示）の再構築判定にポーリング比較で使う。
    /// </summary>
    public int NodeDataVersion { get; private set; }

    /// <summary>
    /// ノードの追加または更新処理を行う。
    /// ノードは作成後に位置が変化しない想定であるため、すでにキャッシュとオブジェクトが
    /// 存在する場合は処理をスキップしてパフォーマンスを向上させる。
    /// </summary>
    public void CreateOrUpdateNode(NodeData node)
    {
        bool positionChanged = false;
        if (nodeDataCache.TryGetValue(node.node_id, out NodeData cachedNode))
        {
            // 位置やタイプが変更された可能性があるため、これらが変更されていた場合はデータを更新する。
            positionChanged = Vector3.Distance(cachedNode.GetAbsolutePosition(), node.GetAbsolutePosition()) > 0.001f;
            if (positionChanged)
            {
                nodeGrid?.Remove(cachedNode, cachedNode.GetAbsolutePosition());

                // position 配列を更新する
                if (cachedNode.position != null && cachedNode.position.Length >= 3)
                {
                    cachedNode.position[0] = node.position[0];
                    cachedNode.position[1] = node.position[1];
                    cachedNode.position[2] = node.position[2];
                }
                else
                {
                    cachedNode.position = new float[] { node.position[0], node.position[1], node.position[2] };
                }

                nodeGrid?.Add(cachedNode, cachedNode.GetAbsolutePosition());
            }
            if (positionChanged || cachedNode.type != node.type || cachedNode.station_path_id != node.station_path_id)
            {
                NodeDataVersion++;
            }
            cachedNode.type = node.type;
            cachedNode.station_path_id = node.station_path_id;
            node = cachedNode;
        }
        else
        {
            nodeDataCache[node.node_id] = node;
            Vector3 pos = node.GetAbsolutePosition();
            nodeGrid?.Add(node, pos);
            NodeDataVersion++;
        }
        IndexNodeCell(node.node_id);

        GameObject existingNode = GetNodeObject(node.node_id);
        if (existingNode != null)
        {
            if (positionChanged)
            {
                Vector3 newAbsolutePos = node.GetAbsolutePosition();
                newAbsolutePos.y += 0.1f;
                existingNode.transform.position = newAbsolutePos;
            }
            
            if (existingNode.activeSelf != ShowNodes)
            {
                existingNode.SetActive(ShowNodes);
            }
            if (deferredNewNodeIntersectionIds.Contains(node.node_id)) UpdateOrDeferNodeIntersection(node.node_id);
            return;
        }

        Vector3 absolutePos = node.GetAbsolutePosition();
        absolutePos.y += 0.1f; 

        int status = Constants.STATUS_ACTIVE; 
        GameObject nodeGO = CreateDefaultNodeVisual(node.node_id, status, absolutePos);
        
        // インフラ施設の接続端子ノードには、ドラッグ移動用の NodeInputHandler を付与し、
        // ロールを設定する。端子ノードの識別は、建物エージェント側の infra_node_id から
        // BuildingManager が構築する逆引きインデックスで行う（ノードAPIは所属施設を
        // 公開しない片方向参照の設計。CellDataLoader のロード順は Building → Path のため、
        // 通常はノード到着時点で建物側の逆引きが揃っている）。
        if (InfraNetworkUtil.IsInfraMask(node.type)
            && BuildingManager.Instance != null
            && BuildingManager.Instance.TryGetInfraTerminalOwner(node.node_id, out _))
        {
            var handler = nodeGO.AddComponent<NodeInputHandler>();
            handler.nodeId = node.node_id;
            
            var idInfo = nodeGO.GetComponent<ObjectIdInfo>();
            if (idInfo != null)
            {
                idInfo.role = "infra_terminal";
            }
        }
        
        Transform container = GetContainer("Nodes");
        if (container != null) nodeGO.transform.SetParent(container);
        
        nodeGO.SetActive(ShowNodes);
        nodeObjectsCache[node.node_id] = nodeGO;

        UpdateOrDeferNodeIntersection(node.node_id);
    }

    /// <summary>
    /// 受信したノードの交差点を更新する。LODバッチ中は接続道路がまだ届いていないことがあるので、
    /// その場で計算せずバッチの交差点フェーズ(UpdateDeferredLodIntersections)へ回す。
    /// 取り消されたときは、再受信(deferredNewNodeIntersectionIds)と接続道路の到着(awaitingIntersectionNodes)で回収する。
    /// </summary>
    private void UpdateOrDeferNodeIntersection(int nodeId)
    {
        if (isLodPathBatchActive)
        {
            deferredLodIntersectionNodeIds.Add(nodeId);
            awaitingIntersectionNodes.Add(nodeId);
            deferredNewNodeIntersectionIds.Add(nodeId);
            return;
        }
        UpdateNodeIntersectionMesh(nodeId);
    }

    /// <summary>
    /// 指定されたIDのノードデータを取得します。
    /// </summary>
    public NodeData GetNodeData(int nodeId)
    {
        if (nodeDataCache.TryGetValue(nodeId, out NodeData node))
        {
            return node;
        }
        return null;
    }

    /// <summary>
    /// キャッシュされているノードの位置を更新し、空間インデックス（nodeGrid）を再構築する。
    /// ノードのドラッグ移動完了後に呼び出される。
    /// </summary>
    public void UpdateCachedNodePosition(int nodeId, Vector3 newPosition)
    {
        if (nodeDataCache.TryGetValue(nodeId, out NodeData node))
        {
            Vector3 oldPos = node.GetAbsolutePosition();
            nodeGrid?.Remove(node, oldPos);
            
            if (node.position != null && node.position.Length >= 3)
            {
                node.position[0] = newPosition.x;
                node.position[1] = newPosition.y;
                node.position[2] = newPosition.z;
            }
            else
            {
                node.position = new float[] { newPosition.x, newPosition.y, newPosition.z };
            }
            
            nodeGrid?.Add(node, node.GetAbsolutePosition());
            Debug.Log($"PathManager: キャッシュ内のノード {nodeId} の位置を更新しました: {newPosition}");
        }
    }

    private GameObject CreateDefaultNodeVisual(int nodeId, int status, Vector3 position)
    {
        GameObject nodeGO = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        nodeGO.name = $"{NODE_PREFIX}{nodeId}";
        nodeGO.tag = NODE_TAG;
        nodeGO.transform.position = position;

        var idInfo = nodeGO.AddComponent<ObjectIdInfo>();
        idInfo.id = nodeId;
        idInfo.status = status;
        
        nodeGO.transform.localScale = new Vector3(0.5f, 0.01f, 0.5f);
        
        Material nodeMat = GetNodeDepthMaterial();
        if (nodeMat != null) nodeGO.GetComponent<Renderer>().sharedMaterial = nodeMat;

        return nodeGO;
    }

    /// <summary>
    /// 指定ノードを端点とするキャッシュ済みパスの一覧を返す（pathsByNodeIndex による O(1) 逆引き）。
    /// 交差点メッシュ生成のほか、インフラノードの網種別判定（InfraNetworkUtil）でも使う。
    /// </summary>
    public List<PathSummary> GetPathsConnectedToNode(int nodeId)
    {
        List<PathSummary> connectedPaths = new List<PathSummary>();
        if (pathsByNodeIndex.TryGetValue(nodeId, out HashSet<int> pathIds))
        {
            foreach (int pathId in pathIds)
            {
                if (pathDataCache.TryGetValue(pathId, out PathSummary path))
                {
                    connectedPaths.Add(path);
                }
            }
        }
        return connectedPaths;
    }

    // ================================================================
    // 近接線路の結合描画（TrackAdjacency）のサポート
    // ================================================================

    /// <summary>結合描画の対象となるパス種別（線路・モノレール）か。</summary>
    private static bool IsTrackTypePath(PathSummary path)
    {
        return path != null && (path.path_type == Constants.PATH_TRACK || path.path_type == Constants.PATH_MONORAIL);
    }

    /// <summary>
    /// 指定線路の近接候補（通過セル＋隣接セルに登録されている、自分以外の同種線路）の
    /// パスID一覧を返す。並走判定そのものは行わない粗いセル単位の絞り込みで、
    /// 幾何的な判定は TrackAdjacency.Compute 側が行う。
    /// 通過セルの隣接セルも見るのは、セル境界を挟んで並走するペアを取りこぼさないため。
    /// </summary>
    private List<int> CollectNearbyTrackPathIds(PathSummary path)
    {
        var result = new List<int>();
        if (!IsTrackTypePath(path)) return result;

        var seen = new HashSet<int>();
        foreach (var cell in GetPathCoveredCellsCached(path))
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (!pathIdsByCell.TryGetValue(new Vector2Int(cell.x + dx, cell.y + dy), out HashSet<int> ids)) continue;
                    foreach (int id in ids)
                    {
                        if (id == path.path_id || !seen.Add(id)) continue;
                        if (!pathDataCache.TryGetValue(id, out PathSummary other)) continue;
                        // 線路とモノレールのように種別の異なるパスは結合しない。
                        // LRTの専用軌道は1本で複線を持つ別系統なので、同じ PATH_TRACK の通常鉄道とも結合しない。
                        if (other.path_type != path.path_type) continue;
                        if (LrtRoadParts.IsLrt(other.path_variant) != LrtRoadParts.IsLrt(path.path_variant)) continue;
                        result.Add(id);
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// TrackAdjacency.NeighborProvider の実体。指定線路の近接候補線路の描画点列と幅、
    /// 駅パスならプラットフォームの左右張り出し幅と淵ライン（島式ホームの結合判定用）、LRTの線路なら
    /// 停留所のホーム区間（主線路の架線柱をホームの外へ出すため）、つなぎ目のS字で外へ動く量を返す。
    /// 点列は生成済みならキャッシュ(pathPointsCache)を使い、未生成（同一ロード内で
    /// 相手がまだ描画されていない等）なら CalculatePathPoints で算出する。
    /// 完成前の線路(計画中・着工保留中・建設中)と敷設プレビューの線は仮の相手(TrackNeighborInfo.Provisional)として返す。
    /// 建設中の線路は施工済みの区間だけを返す(桁が架かる前に完成側の高欄を外さないため)。
    /// 形状を編集中の計画は、そのプレビューと二重にならないよう返さない。
    /// </summary>
    public List<TrackNeighborInfo> GetAdjacentTrackNeighbors(PathSummary path)
    {
        var result = new List<TrackNeighborInfo>();
        foreach (int id in CollectNearbyTrackPathIds(path))
        {
            if (previewExcludedPathId > 0 && id == previewExcludedPathId) continue;
            if (!pathDataCache.TryGetValue(id, out PathSummary other)) continue;

            List<Vector3> points;
            if (!pathPointsCache.TryGetValue(id, out points) || points == null || points.Count < 2)
            {
                points = CalculatePathPoints(other);
            }
            if (points == null || points.Count < 2) continue;
            if (other.status == Constants.STATUS_CONSTRUCTING)
            {
                points = PathDrawContext.SlicePolylineByFraction(points, 0f, ConstructionPathBuilder.BuiltFraction(other, points));
                if (points == null || points.Count < 2) continue;
            }
            result.Add(BuildTrackNeighborInfo(other, points));
        }
        // 敷設プレビューの線(自分自身と、同じ線の区間は除く)。プレビューはセル表に載らないので別に加える。
        bool lrt = LrtRoadParts.IsLrt(path.path_variant);
        foreach (PathSummary preview in previewTracks)
        {
            if (preview.path_id == path.path_id || preview.path_type != path.path_type || LrtRoadParts.IsLrt(preview.path_variant) != lrt) continue;
            if (previewTrackPoints.TryGetValue(preview.path_id, out var points)) result.Add(BuildTrackNeighborInfo(preview, points));
        }
        return result;
    }

    /// <summary>隣接候補1本分の情報(GetAdjacentTrackNeighbors)。</summary>
    private TrackNeighborInfo BuildTrackNeighborInfo(PathSummary other, List<Vector3> points)
    {
        // 駅パスのホーム張り出し幅を幾何フレーム（点列の start→end 方向）の左右へ解決する。
        // TrackPath 側が自線路のホームと同じ規約で扱えるようにするため、
        // 内側/外側→左右の解決（ResolvePlatformExtents）はここで済ませて渡す。
        float platformLeft = 0f;
        float platformRight = 0f;
        var otherMaster = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(other.path_variant) : null;
        if (otherMaster != null && otherMaster.platform_width.HasValue && otherMaster.platform_width.Value > 0f)
        {
            TrackPath.ResolvePlatformExtents(other, otherMaster.platform_width.Value, out platformLeft, out platformRight);
        }

        // LRTの停留所のホーム区間。相手が自分の描画で使うのと同じ索引・点列から、点列の3D累積距離で求める。
        List<Vector2> lrtZones = null;
        if (LrtRoadParts.IsLrt(other.path_variant))
        {
            var lrtSpans = LrtPlatformParts.Resolve(other, points, new VehiclePolyline(points).Length);
            if (lrtSpans.Count > 0)
                lrtZones = LrtPlatformParts.ZoneRanges(lrtSpans, LrtRoadParts.PlatformRampLength(other.path_variant), LrtRoadParts.PLATFORM_TRACK_ZONE_MARGIN);
        }

        // つなぎ目のS字で道床・ホームの外端が動く区間(結合判定で相手の広がりに含める)。
        TrackLateralProfile profile = GetTrackLateralProfile(other);
        float[] outerShifts = profile != null && !profile.IsUniform ? new TrackLateralSampler(profile, other, points).OuterShiftsAtPoints() : null;

        float width = GetRoadWidth(other.path_variant);
        return new TrackNeighborInfo
        {
            PathId = other.path_id,
            Points = points,
            Width = width,
            OuterShifts = outerShifts,
            Provisional = TrackAdjacency.IsProvisional(other),
            PlatformLeft = platformLeft,
            PlatformRight = platformRight,
            PlatformEdge = TrackPath.PlatformEdgeDistance(width, Mathf.Max(1, otherMaster?.lanes ?? 1), otherMaster?.center_width ?? 0f),
            LrtPlatformZones = lrtZones,
            LrtPlatformOuter = lrtZones != null ? LrtRoadParts.PlatformOuterOffset(other) : 0f,
        };
    }

    /// <summary>
    /// データ変更（新規・version変化・削除）のあった線路の近接線路を再生成待ちキューへ積む。
    /// 対象が線路でなければ何もしない。LODバッチ中でなければ即座にフラッシュする。
    /// 再生成(TryRebuildPathOnce)はデータを変更しないため、これが連鎖することはない。
    /// </summary>
    private void EnqueueAdjacentTrackNeighborRebuilds(PathSummary path)
    {
        if (!IsTrackTypePath(path)) return;

        foreach (int id in CollectNearbyTrackPathIds(path))
        {
            QueueTrackNeighborRebuild(id);
        }
        FlushPendingTrackNeighborRebuilds(null);
    }

    /// <summary>
    /// 近接線路の作り直しを積む。積む時点で描画の版を進め、視野外にあって後から表示される線路が
    /// 結合状態の古い退避中のGOで復元されないようにする(橋脚・送電線などの通知と同じ)。
    /// </summary>
    private void QueueTrackNeighborRebuild(int id)
    {
        if (!pendingTrackNeighborRebuildIds.Add(id)) return;
        geometryRevisions.TryGetValue(id, out int revision);
        geometryRevisions[id] = revision + 1;
    }

    /// <summary>
    /// 線路の登録・変更・削除のあと、両端のノードが車両基地の端子なら基地へ知らせる。
    /// 車両基地の引き込み線は端子へつながる線路の軌道数で単線・複線が決まるため
    /// (BuildingManager.OnDepotTerminalPathsChanged。変わらなければ作り直さない)。
    /// 削除では pathsByNodeIndex から外した後に呼ぶ。
    /// </summary>
    private static void NotifyDepotTerminals(PathSummary path)
    {
        BuildingManager buildings = BuildingManager.Instance;
        if (!IsTrackTypePath(path) || buildings == null) return;
        if (buildings.IsDepotTerminalNode(path.start_node_id)) buildings.OnDepotTerminalPathsChanged(path.start_node_id);
        if (buildings.IsDepotTerminalNode(path.end_node_id)) buildings.OnDepotTerminalPathsChanged(path.end_node_id);
    }

    /// <summary>
    /// 近接線路の再生成待ちキューを地形追従再生成キューへ流す。LODバッチ中は何もしない
    /// （バッチ終了時の EndLodPathBatch から、このバッチで生成済みのパスを除外して呼ばれる）。
    /// 実際の再生成は TerrainFollowRebuildWorker が視野優先・フレーム予算制で行う。
    /// </summary>
    private void FlushPendingTrackNeighborRebuilds(HashSet<int> excludeIds)
    {
        if (isLodPathBatchActive || pendingTrackNeighborRebuildIds.Count == 0) return;

        foreach (int id in pendingTrackNeighborRebuildIds)
        {
            if (excludeIds != null && excludeIds.Contains(id)) continue;
            EnqueueTerrainFollowPath(id, pathBuildSerialCounter, TerrainFollowQueueReason.Other);
        }
        pendingTrackNeighborRebuildIds.Clear();
    }

    // ================================================================
    // 高速道路の本線とランプの結合描画（RoadAdjacency）のサポート
    // ================================================================

    /// <summary>
    /// 指定した高速道路の近接候補(通過セル＋隣接セルに登録されている高速道路)のうち、
    /// ランプかどうかが wantRamps に一致するもの。幾何的な判定は RoadAdjacency.Compute が行う。
    /// </summary>
    private List<int> CollectNearbyRoadMergeIds(PathSummary path, bool wantRamps)
    {
        var result = new List<int>();
        if (!RoadAdjacency.IsMotorway(path)) return result;
        // 生成中の新データはまだ通過セルが登録されていないことがあるので、そのときだけ計算し直す。
        bool registered = pathDataCache.TryGetValue(path.path_id, out PathSummary current) && ReferenceEquals(current, path);
        var seen = new HashSet<int>();
        foreach (var cell in registered ? GetPathCoveredCellsCached(path) : GetPathCoveredCells(path))
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (!pathIdsByCell.TryGetValue(new Vector2Int(cell.x + dx, cell.y + dy), out HashSet<int> ids)) continue;
                    foreach (int id in ids)
                    {
                        if (id == path.path_id || !seen.Add(id)) continue;
                        if (!pathDataCache.TryGetValue(id, out PathSummary other)) continue;
                        if (RoadAdjacency.IsMotorway(other) && RoadAdjacency.IsRamp(other) == wantRamps) result.Add(id);
                    }
                }
        return result;
    }

    /// <summary>
    /// RoadAdjacency.NeighborProvider の実体。本線にはランプ、ランプには本線の、稼働中の近接候補を返す。
    /// 点列は切断前の描画点列で、ランプは本線へ寄せた後の高さを持つ(本線の点列はランプに依存しない)。
    /// 工事中の道路は完成した路面を描かないので結合相手に含めない。
    /// </summary>
    public List<RoadNeighborInfo> GetAdjacentRoadNeighbors(PathSummary path)
    {
        var result = new List<RoadNeighborInfo>();
        if (!RoadAdjacency.IsMotorway(path)) return result;
        foreach (int id in CollectNearbyRoadMergeIds(path, !RoadAdjacency.IsRamp(path)))
        {
            if (!pathDataCache.TryGetValue(id, out PathSummary other) || other.status != Constants.STATUS_ACTIVE) continue;
            var points = GetUntrimmedPoints(other, out _);
            if (points == null || points.Count < 2) continue;
            result.Add(new RoadNeighborInfo { PathId = id, Points = points, Width = GetRoadWidth(other.path_variant) });
        }
        return result;
    }

    /// <summary>
    /// データ変更(新規・version変化・削除)のあった高速道路の近くで、結合相手になり得る道路を作り直す。
    /// 本線の変更はランプの描画高さを変える。ランプの点列キャッシュと両端の交差点の入力署名は本線の署名を含むので
    /// (ComputeRoadMergeSignature)、ここではランプの両端のノードを affectedNodeIds へ積んで計算し直させる
    /// (呼び出し側が、変更したパスの登録・削除を終えてから交差点を更新する)。
    /// 通過セルキャッシュを参照するため UnregisterPathCells より前に呼ぶこと。
    /// </summary>
    private void EnqueueAdjacentRoadMergeRebuilds(PathSummary path, ICollection<int> affectedNodeIds)
    {
        if (!RoadAdjacency.IsMotorway(path)) return;
        bool ramp = RoadAdjacency.IsRamp(path);
        foreach (int id in CollectNearbyRoadMergeIds(path, !ramp))
        {
            if (!pathDataCache.TryGetValue(id, out PathSummary other)) continue;
            pendingTrackNeighborRebuildIds.Add(id);
            geometryRevisions.TryGetValue(id, out int revision);
            geometryRevisions[id] = revision + 1;
            if (ramp) continue;
            InvalidateTerrainFollowPath(other);
            if (other.start_node_id > 0) affectedNodeIds.Add(other.start_node_id);
            if (other.end_node_id > 0) affectedNodeIds.Add(other.end_node_id);
        }
        FlushPendingTrackNeighborRebuilds(null);
    }

    /// <summary>ノードで本線が交差点の板を挟まずに続く端(MotorwayMergeLayout)。キーは RoadEndKey。</summary>
    private readonly Dictionary<long, RoadEndJoin> roadEndJoins = new Dictionary<long, RoadEndJoin>();

    private static long RoadEndKey(int pathId, bool start) => ((long)pathId << 1) | (start ? 1L : 0L);

    private RoadEndJoin GetRoadEndJoin(PathSummary path, bool start)
        => path != null && roadEndJoins.TryGetValue(RoadEndKey(path.path_id, start), out var join) ? join : default;

    private void DropRoadEndJoins(int pathId)
    {
        roadEndJoins.Remove(RoadEndKey(pathId, true));
        roadEndJoins.Remove(RoadEndKey(pathId, false));
    }

    /// <summary>ノードの計算結果でその端の扱いを更新し、変わったら真を返す(道路の作り直しが要る)。</summary>
    private bool SetRoadEndJoin(PathSummary path, int nodeId, RoadEndJoin join)
    {
        long key = RoadEndKey(path.path_id, path.start_node_id == nodeId);
        roadEndJoins.TryGetValue(key, out var current);
        if (current.Continuous == join.Continuous && current.OwnsSupport == join.OwnsSupport) return false;
        if (join.Continuous) roadEndJoins[key] = join;
        else roadEndJoins.Remove(key);
        return true;
    }

    /// <param name="drawLod">描画LOD(Normal / Close)。Far は CreateOrUpdateFarPath が担当する。</param>
    /// <param name="detailFrom">詳細パーツを敷く区間の始点(全長に対する割合)。区間外は遠景の帯で埋める。</param>
    /// <param name="detailTo">詳細パーツを敷く区間の終点。</param>
    public void CreateOrUpdatePath(PathSummary path, bool updateIntersections = true, bool forceRebuild = false, DrawLod drawLod = DrawLod.Normal, double detailFrom = 0.0, double detailTo = 1.0)
    {
        EnsureEndIntersectionsVisible(path);
        if (drawLod == DrawLod.Far || drawLod == DrawLod.Hidden) drawLod = DrawLod.Normal;
        if (drawLod == DrawLod.Close && closePathIds.Count >= DrawLodConfig.MAX_CLOSE_PATHS && !closePathIds.Contains(path.path_id))
        {
            drawLod = DrawLod.Normal;
        }
        string renderKey = PathRenderKey(path, drawLod, forceRebuild);
        if (RestorePathLod(path, renderKey, drawLod, updateIntersections)) return;
        geometryRevisions.TryGetValue(path.path_id, out int buildRevision);
        long buildSerial = ++pathBuildSerialCounter;
        forceRebuild = true;
        GameObject oldPath = null;
        if (pathObjectsCache.TryGetValue(path.path_id, out GameObject existingPath))
        {
             if (existingPath != null && !forceRebuild)
             {
                  var existingIdInfo = existingPath.GetComponent<ObjectIdInfo>();
                  int currentStatus = existingIdInfo != null ? existingIdInfo.status : 0;
                  bool existingIsFar = existingIdInfo != null && existingIdInfo.isFarObject;
                  // Far簡易オブジェクトの誤再利用を防ぐため、遠景でないことを確認する。描画LOD・詳細区間も一致が条件。
                   if (currentStatus == path.status && !existingIsFar && pathDataCache.ContainsKey(path.path_id)
                       && DrawLodMatches(existingPath, drawLod, detailFrom, detailTo))
                  {
                      // プールから再利用: 非表示だったオブジェクトを再表示
                      if (!existingPath.activeSelf)
                          existingPath.SetActive(true);
                      SavePathToCache(path);
                      RegisterPathCells(path);
                      ReevaluateAwaitingIntersectionsForPath(path);

                      // 再利用時も VisualPathStats を更新して、zone_id などの変化を反映する
                      var stats = existingPath.GetComponent<VisualPathStats>();
                      if (stats != null)
                      {
                          stats.InitFrom(path);
                      }
                      UpdatePlannedIcon(existingPath, path, path.status == Constants.STATUS_PENDING);
                      return;
                  }
                 // ティア変更やステータス変更時は、新しいメッシュを生成してから古いものを破棄する。
                 // これにより、Destroy と Instantiate の間に生じる一瞬の消滅（ポッピング）を防ぐ。
                 oldPath = existingPath;
             }
             else if (existingPath != null)
             {
                 oldPath = existingPath;
             }
        }

        int status = path.status;
        GameObject pathGO = new GameObject($"{PATH_PREFIX}{path.path_id}");
        var idInfo = pathGO.AddComponent<ObjectIdInfo>();
        idInfo.id = path.path_id;
        idInfo.status = status;
        idInfo.isFarObject = false;
        Transform container = GetContainer("Paths");
        if (container != null) pathGO.transform.SetParent(container);

        pathGO.tag = PATH_TAG;

        List<Vector3> points = CalculatePathPoints(path);

        if (points.Count < 2)
        {
            Debug.LogWarning($"PathManager: パス {path.path_id} の点が不足しています。メッシュ生成をスキップします。");
            Destroy(pathGO);
            return;
        }

        MeshFilter mf = pathGO.AddComponent<MeshFilter>();

        // 地下パス（負の相対Yを持つ）や、実効非追従で丘に潜る区間を持つパスは、
        // トンネル区間（深さ > クリアランス高さ）のメッシュ生成をスキップし、
        // 描画区間ごとに分割して生成する。境界（坑口）には portal パーツを配置する。
        // 工事中は進捗に応じた土工・橋脚・完成区間を組み立てる。
        // ただし model_type=none（公共交通の路線等、完成時も描画しないパス）は工事中も非表示のまま。
        // 描画LODと詳細区間は PathDrawContext 経由で生成器へ渡す。詳細区間はパス全長の割合なので、
        // 区間分割する生成(トンネル・工事中)には適用しない(全長を同じ詳細度で描く)。
        PathMeshResult meshResult;
        bool usePartialRange = false;
        var genWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (PowerLinePath.Handles(path))
            {
                PathDrawContext.Begin(drawLod);
                meshResult = PathMeshFactory.GeneratePathMesh(path, points);
            }
            else if (status == Constants.STATUS_CONSTRUCTING && PathMeshFactory.GetModelType(path) != "none")
            {
                PathDrawContext.Begin(drawLod);
                meshResult = GenerateConstructingPathMesh(path, points);
            }
            else if (UsesTunnelMesh(path, points))
            {
                // トンネル区間を含むパスは描画区間分割の都合上、歩道セットバックは適用しない
                // （地下道路の交差点エンドキャップは現状スコープ外）。
                PathDrawContext.Begin(drawLod);
                meshResult = GenerateUndergroundPathMesh(path, points);
            }
            else
            {
                PathDrawContext.Begin(drawLod, detailFrom, detailTo);
                pathStartSidewalkSetbacks.TryGetValue(path.path_id, out SidewalkSetback startSetback);
                pathEndSidewalkSetbacks.TryGetValue(path.path_id, out SidewalkSetback endSetback);
                meshResult = PathMeshFactory.GeneratePathMesh(path, points, startSetback, endSetback);
                usePartialRange = PathDrawContext.HasDetailRange;
            }
        }
        finally
        {
            PathDrawContext.End();
        }
        DrawLodStats.AddGenerationMs(DrawLodStats.Category.Path, genWatch.Elapsed.TotalMilliseconds);

        var identity = pathGO.AddComponent<RenderCacheIdentity>();
        identity.key = renderKey;
        identity.mesh = meshResult?.Mesh;

        if (meshResult != null && meshResult.GeneratedObject != null)
        {
            meshResult.GeneratedObject.transform.SetParent(pathGO.transform, false);
        }

        var drawInfo = DrawLodInfo.Ensure(pathGO);
        drawInfo.drawLod = drawLod;
        drawInfo.detailFrom = usePartialRange ? (float)detailFrom : 0f;
        drawInfo.detailTo = usePartialRange ? (float)detailTo : 1f;
        if (usePartialRange)
        {
            AddFarRemainderStrips(pathGO, points, GetRoadWidth(path.path_variant), detailFrom, detailTo);
        }
        if (drawLod == DrawLod.Close) closePathIds.Add(path.path_id);
        else closePathIds.Remove(path.path_id);

        mf.mesh = meshResult?.Mesh;
        bool modulesCreated = meshResult?.ModulesCreated ?? false;

        if (meshResult != null && meshResult.Mesh != null)
        {
            // コライダーの物理データ生成（PhysXベイク）は処理負荷が高いため、別スレッドで非同期に実行する。
            RenderWorkScheduler.Start(this, SetColliderAsync(pathGO, meshResult.Mesh), "PathManager");
        }
        PathInputHandler inputHandler = pathGO.AddComponent<PathInputHandler>();
        inputHandler.pathId = path.path_id;

        UpdatePlannedIcon(pathGO, path, path.status == Constants.STATUS_PENDING);

        SetupVisuals(pathGO, path, status, modulesCreated);

        SetupWaypoints(pathGO, path.path_id, points);

        RegisterPathSegments(path, points);

        if (updateIntersections)
        {
            RegisterPathData(path);
        }
        else
        {
            SavePathToCache(path);
        }
        RegisterPathCells(path);
        StampPathBuild(pathGO, path, buildRevision, buildSerial);

        // 辞書は即座に新しいオブジェクトに更新する。
        // これにより、短時間に同じIDで再描画が呼ばれてもオブジェクトが増殖しない。
        SetPathObject(path.path_id, pathGO);
        DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Path,
            oldPath != null ? DrawLodInfo.GetDrawLod(oldPath, DrawLod.Normal) : (DrawLod?)null, drawLod);

        // 固定区間パーツは非同期に揃うため、初回生成が完了してから旧表示と差し替える。
        if (oldPath != null)
        {
            PathPresentation.Replace(pathGO, oldPath, lodCache);
        }
        ReevaluateAwaitingIntersectionsForPath(path);
    }

    /// <summary>遠景の帯の頂点間隔(m)。曲線の形を保ちつつ頂点を抑える。</summary>
    private const float FAR_STRIP_SAMPLE_INTERVAL = 15f;

    /// <summary>点列を距離間隔で間引く(始点・終点は必ず残す)。</summary>
    private static List<Vector3> DecimatePolyline(List<Vector3> points, float interval)
    {
        var result = new List<Vector3>();
        if (points == null || points.Count == 0) return result;
        result.Add(points[0]);
        float acc = 0f;
        for (int i = 1; i < points.Count - 1; i++)
        {
            acc += Vector3.Distance(points[i - 1], points[i]);
            if (acc >= interval)
            {
                result.Add(points[i]);
                acc = 0f;
            }
        }
        if (points.Count > 1) result.Add(points[points.Count - 1]);
        return result;
    }

    /// <summary>
    /// 地下パス（負の相対Yを持つパス）のメッシュを生成する。
    ///
    /// 点列をトンネル判定（深さ = 地形標高 − 道路Y がクリアランス高さを超えるか）で
    /// 「描画する連続区間」に分割し、区間ごとに通常のメッシュ生成器を呼ぶ。
    /// トンネル区間はメッシュを生成せず、描画⇔トンネルの境界（坑口）に
    /// portal パーツ（Models/Paths/p{variant}/portal、なければ p{type}/portal）を配置する。
    ///
    /// 深さは1本のパス内で連続的に変化するため、開削⇔トンネルの遷移は何度でも
    /// 起こり得る（坑口は閾値を跨ぐすべての境界に配置される）。
    /// コライダーは描画区間のストリップメッシュを結合したものになる
    /// （トンネル区間はクリック判定を持たない）。
    /// </summary>
    /// <summary>
    /// パスをトンネル分割メッシュ（開削区間＋坑口、トンネル区間は非描画）で描くべきか。
    /// 負の相対Yを持つ地下パスに加えて、実効非追従で丘に潜りトンネル区間が実在する
    /// パスも対象にする（谷を渡るだけの非追従橋は対象外＝通常メッシュで柱付き描画）。
    /// </summary>
    private static bool UsesTunnelMesh(PathSummary path, List<Vector3> points)
    {
        // 駅舎パスは生成器が各アンカーを「地形標高 + マスター height」へ持ち上げて桁を架けるため、
        // 点列(地表基準)での深さ判定は意味を持たない。区間分割すると分割数ぶん橋と建屋が
        // 二重生成されるので対象外にする。
        if (PathMeshFactory.GetModelType(path) == "station_house") return false;
        if (TunnelSectionUtil.IsUndergroundPath(path)) return true;
        if (GetPathFollowTerrain(path)) return false;
        float clearance = TunnelSectionUtil.GetClearanceHeight(path.path_variant);
        return TunnelSectionUtil.HasTunnelSection(points, clearance, pathFollowsTerrain: false);
    }

    /// <summary>
    /// Farティアで非表示にすべきトンネルパスか。端点を結ぶ直線がトンネル区間を持つ
    /// 非追従パス（丘を貫くパス）のみ非表示にし、谷を渡るだけの非追従橋は直線ストリップを描く。
    /// </summary>
    private bool FarPathHasHiddenTunnel(PathSummary path)
    {
        if (GetPathFollowTerrain(path)) return false;
        var startList = path.GetStartPointList();
        var endList = path.GetEndPointList();
        if (startList == null || endList == null) return false;
        var line = new List<Vector3> { GetPathPointPosition(startList), GetPathPointPosition(endList) };
        float clearance = TunnelSectionUtil.GetClearanceHeight(path.path_variant);
        return TunnelSectionUtil.HasTunnelSection(line, clearance, pathFollowsTerrain: false, sampleInterval: 20f);
    }

    private PathMeshResult GenerateUndergroundPathMesh(PathSummary path, List<Vector3> points)
    {
        float clearance = TunnelSectionUtil.GetClearanceHeight(path.path_variant);
        var portals = new List<TunnelSectionUtil.PortalInfo>();
        var runs = TunnelSectionUtil.SplitDrawableRuns(points, clearance, portals, GetPathFollowTerrain(path));

        GameObject container = new GameObject("UndergroundPath");
        var colliderCombine = new List<CombineInstance>();

        foreach (var run in runs)
        {
            PathMeshResult runResult = PathMeshFactory.GeneratePathMesh(path, run);
            if (runResult == null) continue;

            if (runResult.GeneratedObject != null)
            {
                runResult.GeneratedObject.transform.SetParent(container.transform, false);
            }

            if (runResult.Mesh != null)
            {
                colliderCombine.Add(new CombineInstance
                {
                    mesh = runResult.Mesh,
                    transform = Matrix4x4.identity,
                });
            }
        }

        PlaceTunnelPortals(container, path, portals);

        Mesh colliderMesh = null;
        if (colliderCombine.Count == 1)
        {
            colliderMesh = colliderCombine[0].mesh;
        }
        else if (colliderCombine.Count > 1)
        {
            colliderMesh = new Mesh();
            colliderMesh.CombineMeshes(colliderCombine.ToArray(), true, false);
            colliderMesh.RecalculateBounds();
        }

        UnityLog.If(LocalConstants.UNITY_LOG_ROAD,
            () => $"[PathManager] 地下パス {path.path_id}: 描画区間={runs.Count} 坑口={portals.Count} clearance={clearance}");

        // 全区間がトンネルでメッシュが無い場合も、ModulesCreated=true として
        // 呼び出し側のフォールバックマテリアル適用（黒帯表示）を抑制する。
        return new PathMeshResult(colliderMesh, true, container);
    }

    /// <summary>進捗別の土工・橋脚・路面と、完成時と共通の坑口を組み立てる。</summary>
    private PathMeshResult GenerateConstructingPathMesh(PathSummary path, List<Vector3> points)
    {
        var result = ConstructionPathBuilder.Generate(path, points, UsesTunnelMesh(path, points), GetPathFollowTerrain(path), out var portals);
        PlaceTunnelPortals(result.GeneratedObject, path, portals);
        return result;
    }

    /// <summary>
    /// 坑口（トンネル出入口）に portal パーツを配置する。
    /// プレハブはバリアント→タイプの順で検索し、なければ配置しない（オプショナルパーツ）。
    /// プレハブはローカル+Z がトンネル内部を向く前提で、境界点にY回転のみで配置する。
    /// </summary>
    private void PlaceTunnelPortals(GameObject container, PathSummary path, List<TunnelSectionUtil.PortalInfo> portals)
    {
        if (portals == null || portals.Count == 0 || ModelManager.Instance == null) return;

        int pathType = path.path_type;
        var master = MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized
            ? MasterDataStore.Instance.GetPathMaster(path.path_variant)
            : null;
        if (master != null) pathType = master.type;

        GameObject portalPrefab = ModelManager.Instance.GetModel($"Models/Paths/p{path.path_variant}/portal");
        if (portalPrefab == null)
        {
            portalPrefab = ModelManager.Instance.GetModel($"Models/Paths/p{pathType}/portal");
        }
        if (portalPrefab == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD,
                () => $"[PathManager] portal プレハブなし (variant={path.path_variant}, type={pathType})。坑口の配置をスキップ");
            return;
        }

        foreach (var portal in portals)
        {
            GameObject portalGO = Instantiate(portalPrefab, container.transform);
            portalGO.transform.position = portal.position;
            portalGO.transform.rotation = Quaternion.LookRotation(portal.direction, Vector3.up);
        }
    }

    /// <summary>
    /// 指定されたオブジェクトを数フレーム後に破棄する。
    /// 新しいオブジェクトのレンダリングが確定してから古いものを消すことで、
    /// 一瞬の消滅（ポッピング）を防ぐ。
    /// 辞書の更新は呼び出し元で即座に行うため、このコルーチンでは破棄のみを行う。
    /// </summary>
    private IEnumerator DestroyObjectAfterFrames(GameObject obj)
    {
        return Utils.DestroyObjectAfterFrames(this, obj);
    }

    private void ReevaluateAwaitingIntersectionsForPath(PathSummary path)
    {
        if (path == null) return;
        ReevaluateAwaitingIntersectionNode(path.start_node_id);
        ReevaluateAwaitingIntersectionNode(path.end_node_id);
    }

    private void ReevaluateAwaitingIntersectionNode(int nodeId)
    {
        if (nodeId <= 0 || !awaitingIntersectionNodes.Contains(nodeId)) return;

        if (isLodPathBatchActive)
        {
            // 一括更新中は交差点をその場で作ると、後続道路がまだキャッシュ登録前の状態で計算されることがある。
            // バッチ内の交差点更新フェーズへ合流させ、接続パスが揃った状態で再評価する。
            deferredLodIntersectionNodeIds.Add(nodeId);
            return;
        }

        UpdateNodeIntersectionMesh(nodeId);
    }

    /// <summary>
    /// 連続する制御点間を一定間隔で細分化する。
    /// followTerrain=true の場合は中間点のYを地形高さに追従させ、
    /// false の場合は両端点のYを線形補間して滑らかな傾斜を維持する。
    /// </summary>
    public static List<Vector3> SubdividePointsWithTerrain(List<Vector3> controlPoints, float interval = TERRAIN_SAMPLE_INTERVAL, bool followTerrain = true)
    {
        if (controlPoints == null || controlPoints.Count < 2) return controlPoints;

        List<Vector3> result = new List<Vector3>(controlPoints.Count * 3);

        for (int i = 0; i < controlPoints.Count; i++)
        {
            result.Add(controlPoints[i]);

            if (i < controlPoints.Count - 1)
            {
                Vector3 from = controlPoints[i];
                Vector3 to = controlPoints[i + 1];

                float dx = to.x - from.x;
                float dz = to.z - from.z;
                float horizontalDistance = Mathf.Sqrt(dx * dx + dz * dz);

                if (horizontalDistance > interval)
                {
                    int subdivisions = Mathf.FloorToInt(horizontalDistance / interval);
                    for (int s = 1; s <= subdivisions; s++)
                    {
                        float t = (float)s / (subdivisions + 1);
                        float x = Mathf.Lerp(from.x, to.x, t);
                        float z = Mathf.Lerp(from.z, to.z, t);

                        if (followTerrain)
                        {
                            float groundHeight = Utils.GetGroundHeight(new Vector3(x, 1000f, z));
                            result.Add(new Vector3(x, groundHeight + RoadPartHeights.ROAD_SPLINE_OFFSET, z));
                        }
                        else
                        {
                            float y = Mathf.Lerp(from.y, to.y, t);
                            result.Add(new Vector3(x, y, z));
                        }
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// パスバリアントの base_height（3D描画用の高さオフセット）を取得する。
    /// マスター未定義の場合は 0m（シフトなし）。
    /// 確定済みパスの描画点列と、道路エディタの敷設プレビュー（RoadRenderer）が共有する。
    /// </summary>
    public static float GetPathBaseHeight(int pathVariant)
    {
        if (MasterDataStore.Instance == null) return 0f;
        var master = MasterDataStore.Instance.GetPathMaster(pathVariant);
        if (master != null && master.base_height.HasValue) return master.base_height.Value;
        return 0f;
    }

    /// <summary>
    /// パスバリアントのスパンが地形追従するか（follow_terrain、既定true）を取得する。
    /// false のバリアント（桁橋など）は relY=0 のスパンでも地形に吸着させず、
    /// アンカー絶対標高のみからの補間で水平デッキを構成する。
    /// バックエンド geo_path_depth._build_segments_3d の follow_flags と同一の定義。
    /// </summary>
    public static bool GetPathFollowTerrain(int pathVariant)
    {
        if (MasterDataStore.Instance == null) return true;
        var master = MasterDataStore.Instance.GetPathMaster(pathVariant);
        if (master != null && master.follow_terrain.HasValue) return master.follow_terrain.Value;
        return true;
    }

    /// <summary>接続パスの設計標高を優先した、描画オフセットを含まないノード吸着位置。</summary>
    public Vector3 GetNodeSnapPosition(NodeData node)
    {
        Vector3 position = node.GetAbsolutePosition();
        position.y = Utils.GetGroundHeight(position) + node.GetRelativeHeight();
        foreach (var path in GetPathsConnectedToNode(node.node_id))
        {
            if (path.station_grade_profile == null || path.station_grade_profile.Count < 2) continue;
            position.y = Utils.GetGroundHeight(position) + path.GetRelativeHeightAtPoint(position.x, position.z);
            return position;
        }
        return position;
    }

    /// <summary>保存済み絶対標高を描画点列へ適用する。</summary>
    public static void ApplyStationGrade(PathSummary path, List<Vector3> points)
    {
        if (path?.station_grade_profile == null || path.station_grade_profile.Count < 2) return;
        var profile = Newtonsoft.Json.Linq.JArray.FromObject(path.station_grade_profile).ToObject<List<List<float>>>();
        float offset = RoadPartHeights.ROAD_SPLINE_OFFSET - GetPathBaseHeight(path.path_variant);
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 point = points[i];
            point.y = EvaluateStationGrade(profile, point) + offset;
            points[i] = point;
        }
    }

    /// <summary>基準線へのXZ射影を使い、曲線の内外・逆向きの線路でも同じ断面の高さを返す。</summary>
    public static float EvaluateStationGrade(List<List<float>> profile, Vector3 point)
    {
        float best = float.PositiveInfinity, height = 0f;
        for (int i = 1; i < profile.Count; i++)
        {
            var a = profile[i - 1]; var b = profile[i];
            Vector2 start = new Vector2(a[0], a[2]);
            Vector2 end = new Vector2(b[0], b[2]);
            float t = PolylineUtils.ProjectOnSegment(new Vector2(point.x, point.z), start, end);
            float distance = (new Vector2(point.x, point.z) - Vector2.Lerp(start, end, t)).sqrMagnitude;
            if (distance < best) { best = distance; height = Mathf.Lerp(a[1], b[1], t); }
        }
        return height;
    }

    /// <summary>
    /// パス個別の地形追従設定を優先し、未指定時はバリアント既定に従う。
    /// バックエンド geo_path_depth.path_follows_terrain と同じ解決順。
    /// </summary>
    public static bool GetPathFollowTerrain(PathSummary path)
    {
        if (path == null) return true;
        int own = path.GetFollowTerrain();
        if (own == 1) return true;
        if (own == 0) return false;
        return GetPathFollowTerrain(path.path_variant);
    }

    /// <summary>
    /// パスのアンカー点（始点・ウェイポイント・終点）と制御点から、
    /// 三次ベジエ曲線に沿って密集にサンプリングした点群を生成する。
    ///
    /// 各セグメントは三次ベジエ B(t) = (1-t)^3*P0 + 3(1-t)^2*t*P1 + 3(1-t)*t^2*P2 + t^3*P3 で表現される:
    ///   P0 = 区間始点の位置
    ///   P1 = 区間始点の出口制御点（control_out または start_control）
    ///   P2 = 区間終点の入口制御点（control_in または end_control）
    ///   P3 = 区間終点の位置
    ///
    /// 制御点が未指定の場合は、始点と終点を1/3・2/3に内分した位置に自動設定し、
    /// 直線的なベジエ（実質的に直線）になるようにする。
    /// これはバックエンドの PathEntity._auto_fill_controls と同じロジックである。
    ///
    /// サンプリングされた各点のY座標は地形標高に追従する。
    ///
    /// base_height: 3D描画上の都合で、パスの相対高さ(relY)から base_height を引いた高さで
    /// 描画する（バックエンドのロジック・交差判定は元の relY を使うため影響しない）。
    /// 例: base_height=20 で relY=20 の送電線は、3D上は高さ0m（地面）として描画され、
    /// プレハブモデル自体が20mの高さを持つことで送電線が地上を這らないように見せる。
    ///
    /// 戻り値は毎回呼び出し側が所有する新しいリストで、書き換えてよい。
    /// 未切断の点列は GetUntrimmedPoints が地形署名付きでキャッシュする。
    /// </summary>
    public List<Vector3> CalculatePathPoints(PathSummary path, bool trimIntersection = true)
    {
        List<Vector3> shared = GetUntrimmedPoints(path, out bool isShared);
        List<Vector3> points = isShared ? new List<Vector3>(shared) : shared;
        return trimIntersection ? TrimIntersectionPoints(path, points) : points;
    }

    /// <summary>
    /// 未切断の点列のキャッシュ。点列はパスデータと、通過セルの地形高さだけで決まるので、
    /// source(pathDataCache の現在のインスタンス)と地形署名が一致する間は再サンプリングしない。
    /// points は共有するので書き換えない(再計算時は新しいリストに差し替える)。
    /// revision は再計算のたびに進む全体通し番号で、GetPathGeometryRevision の上位ビットになる。
    /// </summary>
    private sealed class PointsEntry
    {
        public PathSummary source;
        public long terrainSignature;
        public List<Vector3> points;
        public int revision;
    }
    private readonly Dictionary<int, PointsEntry> untrimmedPointsCache = new Dictionary<int, PointsEntry>();
    // セッションをまたいでも戻さない。ClearAllCells の前後で版が衝突しないようにするため。
    private int pointsRevisionCounter;
    /// <summary>切断点・歩道セットバックが変わるたびに進む、パスごとの版(GetPathGeometryRevision の下位ビット)。</summary>
    private readonly Dictionary<int, int> connectionRevisions = new Dictionary<int, int>();

    /// <summary>
    /// 未切断の点列を返す。isShared=true のときはキャッシュと共有するリストなので書き換えないこと。
    /// キャッシュするのは pathDataCache に登録中で通過セルも登録済みのインスタンスだけで、
    /// プレビュー・索道スナップショット・受信直後の新データは毎回計算する(isShared=false)。
    /// </summary>
    private List<Vector3> GetUntrimmedPoints(PathSummary path, out bool isShared)
    {
        PointsEntry entry = GetUntrimmedEntry(path);
        isShared = entry != null;
        return isShared ? entry.points : ComputeUntrimmedPathPoints(path);
    }

    /// <summary>
    /// 検証済みのキャッシュ項目。外れていれば計算し直して保存する。キャッシュ対象外なら null。
    /// 地形署名が不明(TerrainManager が無い)ときは、高さの変化を検知できないのでキャッシュしない。
    /// ランプは寄せ先の本線の署名も混ぜる(本線の地形・データが変わると描画高さが変わるため)。
    /// </summary>
    private PointsEntry GetUntrimmedEntry(PathSummary path)
    {
        if (path == null || !pathDataCache.TryGetValue(path.path_id, out PathSummary cached) || !ReferenceEquals(cached, path)
            || !pathCellsById.ContainsKey(path.path_id)) return null;
        long signature = ComputeTerrainSignature(path, out bool known);
        if (!known)
        {
            DropUntrimmedPoints(path.path_id);
            return null;
        }
        signature = (long)SignatureMix((ulong)signature ^ (ulong)ComputeRoadMergeSignature(path));
        if (untrimmedPointsCache.TryGetValue(path.path_id, out PointsEntry entry)
            && ReferenceEquals(entry.source, path) && entry.terrainSignature == signature) return entry;
        entry ??= new PointsEntry();
        entry.source = path;
        entry.terrainSignature = signature;
        entry.points = ComputeUntrimmedPathPoints(path);
        entry.revision = ++pointsRevisionCounter;
        untrimmedPointsCache[path.path_id] = entry;
        return entry;
    }

    /// <summary>
    /// 点列キャッシュと切断点の版を一緒に捨てる。次の項目は新しい通し番号になるので、
    /// 切断点の版を 0 から数え直しても GetPathGeometryRevision の値は過去と衝突しない。
    /// </summary>
    private void DropUntrimmedPoints(int pathId)
    {
        untrimmedPointsCache.Remove(pathId);
        connectionRevisions.Remove(pathId);
    }

    private void BumpConnectionRevision(int pathId)
    {
        connectionRevisions.TryGetValue(pathId, out int revision);
        connectionRevisions[pathId] = revision + 1;
    }

    /// <summary>
    /// パスの通過セルの地形署名(TerrainManager.GetTerrainSignature)。TerrainManager が無い
    /// (Unity の null 判定で破棄済みを含む)ときは known=false で、呼び出し側は「不明」として省略しない。
    /// </summary>
    private long ComputeTerrainSignature(PathSummary path, out bool known)
    {
        TerrainManager terrain = Utils.CachedTerrainManager;
        if (terrain == null)
        {
            known = false;
            return 0;
        }
        known = true;
        return terrain.GetTerrainSignature(GetPathCoveredCellsCached(path));
    }

    /// <summary>交差点の角の埋めや歩道コネクタが地形をサンプルしうる、ノード周りの範囲(m)。接続道路の通過セルに加えて署名する。</summary>
    private const float NODE_SIGNATURE_TERRAIN_MARGIN = 150f;

    /// <summary>
    /// 交差点メッシュ生成(NodeMeshFactory.GenerateNodeMesh と GetIntersectionCutLimit)の入力署名。
    /// ノードの座標・種別・駅パス、左右通行、ノード周りと接続道路の地形、接続道路ごとの版・状態・工事進捗・
    /// 反対端の接続数を混ぜる。接続道路の項は順序に依存しない和で集める。マスター・モデルは読み込み後は変わらない。
    /// 地形署名が取れない、またはマスター未読込なら known=false で、呼び出し側は省略しない。
    /// </summary>
    private long ComputeNodeIntersectionSignature(int nodeId, NodeData node, List<PathSummary> connected, out bool known)
    {
        known = false;
        TerrainManager terrain = Utils.CachedTerrainManager;
        if (terrain == null || node?.position == null || node.position.Length < 2) return 0;
        if (MasterDataStore.Instance == null || !MasterDataStore.Instance.IsInitialized) return 0;

        float[] pos = node.position;
        ulong h = SignatureMix((ulong)(uint)nodeId ^ ((ulong)(uint)pos.Length << 32));
        for (int i = 0; i < pos.Length; i++) h = SignatureMix(h ^ (uint)BitConverter.SingleToInt32Bits(pos[i]));
        h = SignatureMix(h ^ (uint)node.type);
        h = SignatureMix(h ^ (node.station_path_id.HasValue ? (uint)node.station_path_id.Value : 0x9E3779B9UL << 32));
        h = SignatureMix(h ^ (uint)CityInfoStore.TrafficHand);
        float x = pos[0];
        float z = pos.Length >= 3 ? pos[2] : pos[1];
        var area = new Rect(x - NODE_SIGNATURE_TERRAIN_MARGIN, z - NODE_SIGNATURE_TERRAIN_MARGIN,
            NODE_SIGNATURE_TERRAIN_MARGIN * 2f, NODE_SIGNATURE_TERRAIN_MARGIN * 2f);
        h = SignatureMix(h ^ (ulong)terrain.GetTerrainSignature(area));

        ulong pathSum = 0;
        foreach (PathSummary p in connected)
        {
            int otherNode = p.start_node_id == nodeId ? p.end_node_id : p.start_node_id;
            bool otherIsJunction = pathsByNodeIndex.TryGetValue(otherNode, out var otherPaths) && otherPaths.Count > 1;
            ulong t = SignatureMix((ulong)(uint)p.path_id ^ ((ulong)(uint)p.version << 32));
            t = SignatureMix(t ^ (uint)p.status ^ ((ulong)(otherIsJunction ? 1u : 0u) << 32) ^ ((ulong)(p.start_node_id == nodeId ? 1u : 0u) << 33));
            t = SignatureMix(t ^ (uint)BitConverter.SingleToInt32Bits(ConstructionPathBuilder.Progress(p)));
            t = SignatureMix(t ^ (ulong)ComputeTerrainSignature(p, out _));
            t = SignatureMix(t ^ (ulong)ComputeRoadMergeSignature(p));
            pathSum += t;
        }
        known = true;
        return (long)SignatureMix(h ^ pathSum ^ (ulong)connected.Count);
    }

    /// <summary>
    /// ランプの描画高さが依存する、近くの稼働中の本線の集合・版・地形の署名。ランプ以外は0。
    /// ランプの点列キャッシュと両端の交差点の入力署名に混ぜ、本線の変化で作り直させる。
    /// </summary>
    private long ComputeRoadMergeSignature(PathSummary path)
    {
        if (!RoadAdjacency.IsRamp(path)) return 0;
        ulong sum = 0;
        foreach (int id in CollectNearbyRoadMergeIds(path, false))
        {
            if (!pathDataCache.TryGetValue(id, out PathSummary main) || main.status != Constants.STATUS_ACTIVE) continue;
            ulong t = SignatureMix((ulong)(uint)id ^ ((ulong)(uint)main.version << 32));
            sum += SignatureMix(t ^ (ulong)ComputeTerrainSignature(main, out _));
        }
        return (long)sum;
    }

    /// <summary>SplitMix64 の攪拌。署名の各項を混ぜるのに使う。</summary>
    private static ulong SignatureMix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    /// <summary>
    /// 表示中の道路GOが、現在のデータ・描画revision・地形のまま生成されたものか。
    /// 真なら地形追従の強制再生成を省ける。地形署名が不明なら常に偽(従来どおり作り直す)。
    /// </summary>
    private bool IsTerrainCurrent(PathSummary path)
    {
        if (path == null || !pathObjectsCache.TryGetValue(path.path_id, out GameObject go) || go == null || !go.activeSelf) return false;
        PathBuildStamp stamp = go.GetComponent<PathBuildStamp>();
        if (stamp == null || !stamp.terrainKnown || stamp.version != path.version || stamp.status != path.status
            || stamp.progress != ConstructionPathBuilder.Progress(path)) return false;
        geometryRevisions.TryGetValue(path.path_id, out int revision);
        if (revision != stamp.geometryRevision) return false;
        long signature = ComputeTerrainSignature(path, out bool known);
        return known && signature == stamp.terrainSignature;
    }

    /// <summary>退避キャッシュから取り出したGOが、現在の通過セルの地形署名で生成されたものか。署名が不明なら偽。</summary>
    private bool IsCachedBuildTerrainCurrent(GameObject go, PathSummary path)
    {
        PathBuildStamp stamp = go.GetComponent<PathBuildStamp>();
        if (stamp == null || !stamp.terrainKnown) return false;
        long signature = ComputeTerrainSignature(path, out bool known);
        return known && signature == stamp.terrainSignature;
    }

    /// <summary>
    /// 生成した道路GOに、生成時のデータと地形署名を記録する。SavePathToCache・RegisterPathCells の後に呼ぶ
    /// (このインスタンスの通過セルで署名するため)。生成は同期なので、終了時の地形は生成開始時と同じ。
    /// buildRevision は描画キーに使った revision(生成中に交差点経由の再帰生成で進むことがあるため、開始時に取る)。
    /// buildSerial も生成開始時に進めた通し番号で、生成中に積まれた再生成要求を省かないようにする。
    /// </summary>
    private void StampPathBuild(GameObject pathGO, PathSummary path, int buildRevision, long buildSerial)
    {
        var stamp = pathGO.AddComponent<PathBuildStamp>();
        stamp.buildSerial = buildSerial;
        stamp.terrainSignature = ComputeTerrainSignature(path, out stamp.terrainKnown);
        stamp.version = path.version;
        stamp.status = path.status;
        stamp.progress = ConstructionPathBuilder.Progress(path);
        stamp.geometryRevision = buildRevision;
    }

    /// <summary>
    /// 未切断の点列(CalculatePathPoints の本体)。毎回サンプリングし直し、キャッシュは見ない。
    /// 高速道路のランプは、本線と結合する区間の高さを本線の路面へ寄せる(RoadAdjacency。描画専用)。
    /// 寄せ先の本線の変化は点列キャッシュの署名(ComputeRoadMergeSignature)が検知する。
    /// </summary>
    private List<Vector3> ComputeUntrimmedPathPoints(PathSummary path)
    {
        List<Vector3> points = ComputeDesignedPathPoints(path);
        if (points == null || points.Count < 2 || !RoadAdjacency.IsRamp(path)) return points;
        var neighbors = GetAdjacentRoadNeighbors(path);
        if (neighbors.Count == 0) return points;
        var master = MasterDataStore.Instance?.GetPathMaster(path.path_variant);
        var corridor = RoadAdjacency.Compute(points, GetRoadWidth(path.path_variant), Mathf.Max(0f, master?.wall_width ?? 0f), true, neighbors);
        return RoadAdjacency.ApplyRampHeights(points, corridor);
    }

    /// <summary>保存された縦断(アンカー・制御点・駅や接続の設計縦断)どおりの未切断の点列。</summary>
    private List<Vector3> ComputeDesignedPathPoints(PathSummary path)
    {
        List<Vector3> points = new List<Vector3>();
        var startPointList = path.GetStartPointList();
        var endPointList = path.GetEndPointList();
        var waypointList = path.GetWaypointList();

        // base_height をマスターデータから取得する。
        // 3D描画のみに影響し、バックエンドロジックには影響しない。
        float baseHeight = GetPathBaseHeight(path.path_variant);

        // 地形追従フラグ（パス個別設定 > バリアント既定）。
        // false（桁橋・非追従指定など）ならrelY=0のスパンも地形に吸着させない。
        bool variantFollows = GetPathFollowTerrain(path);

        Vector3 startPos = Vector3.zero;
        bool hasStart = startPointList != null && startPointList.Count >= 2;
        if (hasStart)
        {
            startPos = GetPathPointPosition(startPointList);

            // 3D描画用に base_height を引く（バックエンドの relY はそのまま）。
            startPos.y -= baseHeight;
        }

        Vector3 endPos = Vector3.zero;
        bool hasEnd = endPointList != null && endPointList.Count >= 2;
        if (hasEnd)
        {
            endPos = GetPathPointPosition(endPointList);

            endPos.y -= baseHeight;
        }

        if (!hasStart || !hasEnd) return points;

        // curved=False の場合はアンカー点間を直線で結び、地形に沿うように細分化する。
        // ウェイポイントで方向が変わる場合は、角の部分が潰れないよう接続断面を計算し、
        // セグメントを接続断面どうしで区切る。
        if (!path.curved || (MasterDataStore.Instance?.GetPathMaster(path.path_variant)?.single_span ?? false))
        {
            List<Vector3> anchorPoints = new List<Vector3>();
            anchorPoints.Add(startPos);

            if (waypointList != null && waypointList.Count > 0)
            {
                float width = GetRoadWidth(path.path_variant);
                var gf = new NetTopologySuite.Geometries.GeometryFactory();

                Vector3 prevPos = startPos;

                for (int i = 0; i < waypointList.Count; i++)
                {
                    var wp = waypointList[i];
                    Vector3 wpPos = wp.GetAbsolutePosition();
                    wpPos.y -= baseHeight;
                    float wpRelY = (wp.position != null && wp.position.Length >= 3) ? wp.position[1] : 0f;
                    if (variantFollows && wpRelY == baseHeight)
                    {
                        float groundHeight = Utils.GetGroundHeight(new Vector3(wpPos.x, 1000f, wpPos.z));
                        wpPos.y = groundHeight + RoadPartHeights.ROAD_SPLINE_OFFSET;
                    }

                    Vector3 nextPos;
                    if (i + 1 < waypointList.Count)
                    {
                        nextPos = waypointList[i + 1].GetAbsolutePosition();
                        nextPos.y -= baseHeight;
                        float nextRelY = (waypointList[i + 1].position != null && waypointList[i + 1].position.Length >= 3) ? waypointList[i + 1].position[1] : 0f;
                        if (variantFollows && nextRelY == baseHeight)
                        {
                            float groundHeight = Utils.GetGroundHeight(new Vector3(nextPos.x, 1000f, nextPos.z));
                            nextPos.y = groundHeight + RoadPartHeights.ROAD_SPLINE_OFFSET;
                        }
                    }
                    else
                    {
                        nextPos = endPos;
                    }

                    Vector2 dirA = (new Vector2(wpPos.x, wpPos.z) - new Vector2(prevPos.x, prevPos.z)).normalized;
                    Vector2 dirB = (new Vector2(nextPos.x, nextPos.z) - new Vector2(wpPos.x, wpPos.z)).normalized;

                    if (Vector2.Dot(dirA, dirB) > 0.99f)
                    {
                        // ほぼ直線なのでウェイポイントをそのまま通過点として追加
                        anchorPoints.Add(wpPos);
                        prevPos = wpPos;
                        continue;
                    }

                    // 角の場合は後で RoadPath.cs で処理するため、ここではそのまま wpPos を追加する
                    anchorPoints.Add(wpPos);
                    prevPos = wpPos;
                }
            }

            anchorPoints.Add(endPos);
            bool singleSpan = MasterDataStore.Instance?.GetPathMaster(path.path_variant)?.single_span ?? false;
            if ((anchorPoints.Count == 2 || path.station_grade_profile?.Count >= 2) && !singleSpan)
            {
                bool followTerrain = GetPathFollowTerrain(path) && startPointList[1] == 0f && endPointList[1] == 0f;
                var sampled = SubdividePointsWithTerrain(anchorPoints, TERRAIN_SAMPLE_INTERVAL, followTerrain);
                ApplyStationGrade(path, sampled);
                return sampled;
            }
            // RoadPath.cs 等で角の処理を行ってから細分化するため、ここではそのまま返す
            ApplyStationGrade(path, anchorPoints);
            return anchorPoints;
        }

        float startRelY = (startPointList != null && startPointList.Count >= 3) ? startPointList[1] : 0f;
        float endRelY = (endPointList != null && endPointList.Count >= 3) ? endPointList[1] : 0f;

        // 端点のベジエ制御点(start_control / end_control)。敷設時にユーザーがドラッグで
        // 調整した値がバックエンドに保存されており、APIレスポンスに含まれる場合はそれを
        // 優先する。未指定(null)の場合はバックエンド _auto_fill_controls と同一式の
        // 自動計算にフォールバックする(各分岐内)。
        Vector3? startControlWorld = EndpointControlToWorld(path.GetStartControlList(), startPointList, baseHeight);
        Vector3? endControlWorld = EndpointControlToWorld(path.GetEndControlList(), endPointList, baseHeight);

        if (waypointList == null || waypointList.Count == 0)
        {
            // ウェイポイントなし: 始点→終点の単一セグメント
            // 制御点未指定時はバックエンドと同じ1/3・2/3内分(直線ベジエ)を使用する
            Vector3 p1 = startControlWorld ?? Vector3.Lerp(startPos, endPos, 1f / 3f);
            Vector3 p2 = endControlWorld ?? Vector3.Lerp(startPos, endPos, 2f / 3f);
            bool followTerrain = variantFollows && startRelY == baseHeight && endRelY == baseHeight;
            points = SampleBezierSegmentWithTerrain(startPos, p1, p2, endPos, followTerrain);
        }
        else
        {
            // アンカー点のリストを構築: [始点, ウェイポイント0, ..., ウェイポイントN, 終点]
            // 各アンカー点は (position, control_in, control_out) を持つ
            int anchorCount = waypointList.Count + 2;
            Vector3[] anchorPos = new Vector3[anchorCount];
            float[] anchorRelY = new float[anchorCount];
            Vector3?[] anchorControlIn = new Vector3?[anchorCount];
            Vector3?[] anchorControlOut = new Vector3?[anchorCount];

            anchorPos[0] = startPos;
            anchorRelY[0] = startRelY;
            anchorControlIn[0] = null;
            anchorControlOut[0] = startControlWorld;

            for (int i = 0; i < waypointList.Count; i++)
            {
                Vector3 wpPos = waypointList[i].GetAbsolutePosition();
                wpPos.y -= baseHeight;
                float wpRelY = (waypointList[i].position != null && waypointList[i].position.Length >= 3) ? waypointList[i].position[1] : 0f;
                wpPos.y += 0.01f;
                anchorPos[i + 1] = wpPos;
                anchorRelY[i + 1] = wpRelY;
                Vector3? ctrlIn = waypointList[i].GetControlInAbsolute();
                if (ctrlIn.HasValue) { var c = ctrlIn.Value; c.y -= baseHeight; anchorControlIn[i + 1] = c; }
                Vector3? ctrlOut = waypointList[i].GetControlOutAbsolute();
                if (ctrlOut.HasValue) { var c = ctrlOut.Value; c.y -= baseHeight; anchorControlOut[i + 1] = c; }
            }

            anchorPos[anchorCount - 1] = endPos;
            anchorRelY[anchorCount - 1] = endRelY;
            anchorControlIn[anchorCount - 1] = endControlWorld;
            anchorControlOut[anchorCount - 1] = null;

            // 制御点が未指定のアンカーについては、
            // デフォルト制御点を3次元Catmull-Romスプラインの接線ベクトルから計算する。
            // 接線ベクトル V_i = P_{i+1} - P_{i-1}、テンション=1/6。
            // 端点は片側の隣接アンカーとのベクトルを使用する。
            for (int seg = 0; seg < anchorCount - 1; seg++)
            {
                Vector3 segStart = anchorPos[seg];
                Vector3 segEnd = anchorPos[seg + 1];

                // P1: セグメント始点の出口制御点 (C_{seg, out})
                Vector3 p1;
                if (anchorControlOut[seg].HasValue)
                {
                    p1 = anchorControlOut[seg].Value;
                }
                else
                {
                    Vector3 tangent;
                    if (seg == 0)
                    {
                        tangent = anchorPos[1] - anchorPos[0];
                    }
                    else
                    {
                        tangent = anchorPos[Mathf.Min(seg + 1, anchorCount - 1)] - anchorPos[seg - 1];
                    }
                    p1 = segStart + tangent * (1f / 6f);
                }

                // P2: セグメント終点の入口制御点 (C_{seg+1, in})
                Vector3 p2;
                if (anchorControlIn[seg + 1].HasValue)
                {
                    p2 = anchorControlIn[seg + 1].Value;
                }
                else
                {
                    Vector3 tangent;
                    if (seg + 1 == anchorCount - 1)
                    {
                        tangent = anchorPos[anchorCount - 1] - anchorPos[anchorCount - 2];
                    }
                    else
                    {
                        tangent = anchorPos[seg + 2] - anchorPos[seg];
                    }
                    p2 = segEnd - tangent * (1f / 6f);
                }

                bool followTerrain = variantFollows && anchorRelY[seg] == baseHeight && anchorRelY[seg + 1] == baseHeight;
                List<Vector3> segPoints = SampleBezierSegmentWithTerrain(segStart, p1, p2, segEnd, followTerrain);

                // 最初のセグメントは全点を追加、以降は重複する始点を除いて追加
                int startIdx = (seg == 0) ? 0 : 1;
                for (int j = startIdx; j < segPoints.Count; j++)
                {
                    points.Add(segPoints[j]);
                }
            }
        }

        ApplyStationGrade(path, points);
        return points;
    }

    /// <summary>対向ノードにも交差点がある場合は両端へ切断距離を予約する。</summary>
    private float GetIntersectionCutLimit(PathSummary path, int nodeId, List<Vector3> points)
    {
        int otherNode = path.start_node_id == nodeId ? path.end_node_id : path.start_node_id;
        bool bothEnds = pathsByNodeIndex.TryGetValue(otherNode, out var connected) && connected.Count > 1;
        float limit = IntersectionPolygonUtil.Length(points) * (bothEnds ? 0.45f : 0.95f);
        bool singleSpan = MasterDataStore.Instance?.GetPathMaster(path.path_variant)?.single_span ?? false;
        if ((!path.curved || singleSpan) && points.Count > 1)
        {
            var waypoints = path.GetWaypointList();
            if (waypoints != null && waypoints.Count > 0)
            {
                int first = path.start_node_id == nodeId ? 0 : points.Count - 1;
                int next = path.start_node_id == nodeId ? 1 : points.Count - 2;
                limit = Mathf.Min(limit, Vector2.Distance(IntersectionPolygonUtil.XZ(points[first]), IntersectionPolygonUtil.XZ(points[next])) * 0.45f);
            }
        }
        return limit;
    }
    private List<Vector3> TrimIntersectionPoints(PathSummary path, List<Vector3> points)
    {
        Vector3? start = pathStartConnectionPoints.TryGetValue(path.path_id, out var a) ? a : (Vector3?)null;
        Vector3? end = pathEndConnectionPoints.TryGetValue(path.path_id, out var b) ? b : (Vector3?)null;
        return IntersectionPolygonUtil.Trim(points, start, end);
    }
    /// <summary>
    /// APIレスポンスの端点制御点 [x, 相対高さ, z] をUnity絶対座標へ変換する。
    /// 地形基準は制御点直下ではなく所属アンカー(始点/終点)直下の標高を使う。
    /// 制御点XZ直下の地形を使うとスパン途中の起伏が曲線のYに漏れ込み、
    /// 「非追従スパンはアンカー標高のみから補間する」というバックエンドの規則と
    /// 食い違うため(PathWaypoint.GetControlInAbsolute と同じ理由)。
    /// 制御点が未指定(スキーマ再生成前でフィールドが無い場合を含む)なら null を返す。
    /// </summary>
    private static Vector3? EndpointControlToWorld(List<float> control, List<float> anchor, float baseHeight)
    {
        if (control == null || control.Count < 3) return null;
        if (anchor == null || anchor.Count < 2) return null;
        float anchorX = anchor[0];
        float anchorZ = anchor[anchor.Count - 1];
        float groundY = Utils.GetGroundHeight(new Vector3(anchorX, 1000f, anchorZ));
        return new Vector3(control[0], groundY + control[1] - baseHeight, control[2]);
    }

    /// <summary>
    /// 三次ベジエセグメントを約5m間隔でサンプリングする。
    /// 始点と終点も結果に含む。
    /// followTerrain=true の場合は各点のY座標を地形標高に合わせ、
    /// false の場合は制御点のY座標をそのまま三次ベジエ補間して立体道路を維持する。
    /// </summary>
    private static List<Vector3> SampleBezierSegmentWithTerrain(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, bool followTerrain = true)
    {
        // ベジエ曲線の近似弧長を制御点間距離の総和で概算し、
        // 約5m間隔になるようサンプリング数を動的に決定する。
        float approxLength = Vector3.Distance(p0, p1) + Vector3.Distance(p1, p2) + Vector3.Distance(p2, p3);
        int samples = Mathf.Max(1, Mathf.FloorToInt(approxLength / TERRAIN_SAMPLE_INTERVAL));

        int totalPoints = samples + 2;
        List<Vector3> result = new List<Vector3>(totalPoints);
        result.Add(p0);
        for (int s = 1; s <= samples; s++)
        {
            float t = (float)s / (samples + 1);
            Vector3 sampled = PolylineUtils.CubicBezierPoint(p0, p1, p2, p3, t);

            if (followTerrain)
            {
                float groundHeight = Utils.GetGroundHeight(new Vector3(sampled.x, 1000f, sampled.z));
                result.Add(new Vector3(sampled.x, groundHeight + RoadPartHeights.ROAD_SPLINE_OFFSET, sampled.z));
            }
            else
            {
                result.Add(sampled);
            }
        }
        result.Add(p3);

        return result;
    }

    private void SetupVisuals(GameObject pathGO, PathSummary path, int status, bool modulesCreated)
    {
        VisualModeObject visualModeObject = pathGO.AddComponent<VisualModeObject>();
        visualModeObject.objectType = InputObjectType.PATH;
        visualModeObject.useChildRenderers = true;
        visualModeObject.ignoreSelfRenderer = true;
        visualModeObject.InitializeMaterials();

        var stats = pathGO.AddComponent<VisualPathStats>();
        stats.InitFrom(path);
        visualModeObject.SetStatus(status);
        // オブジェクト単位の表示フィルタは差分ロードで後から生成されたパスにも効かせる必要が
        // あるため、生成直後にここで初期状態を引く。
        visualModeObject.SetFilterHidden(ObjectVisibilityFilter.IsHidden(InputObjectType.PATH, path.path_id));
    }

    private void SetupWaypoints(GameObject pathGO, int pathId, List<Vector3> points)
    {
        pathLabelPositions.Remove(pathId);
        pathPointsCache[pathId] = points;

        if (!_showWaypoints) return;

        CreateWaypointsForPath(pathGO, points);
    }

    private void CreateWaypointsForPath(GameObject pathGO, List<Vector3> points)
    {
        foreach (var pt in points)
        {
            GameObject wpGO = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            wpGO.name = WAYPOINT_NAME;
            wpGO.tag = WAYPOINT_TAG;
            wpGO.transform.position = pt;
            wpGO.transform.localScale = new Vector3(0.4f, 0.01f, 0.4f);

            Material wpMat = GetWaypointMaterial();
            if (wpMat != null)
            {
                wpGO.GetComponent<Renderer>().material = wpMat;
            }

            wpGO.transform.SetParent(pathGO.transform);
            wpGO.SetActive(true);
        }
    }

    private void RegisterPathSegments(PathSummary path, List<Vector3> points)
    {
        if (points == null || points.Count < 2) return;

        // 古いセグメントを削除してから再登録
        UnregisterPathSegments(path.path_id);

        List<PathSegment> newSegments = new List<PathSegment>(points.Count - 1);
        for (int i = 0; i < points.Count - 1; i++)
        {
            PathSegment seg = new PathSegment
            {
                path = path,
                segmentIndex = i,
                a = points[i],
                b = points[i + 1]
            };
            ApplySegmentToGrid(seg, true);
            newSegments.Add(seg);
        }
        pathSegmentsByPathId[path.path_id] = newSegments;
    }

    private void UnregisterPathSegments(int pathId)
    {
        if (segmentGrid == null) return;

        if (pathSegmentsByPathId.TryGetValue(pathId, out List<PathSegment> oldSegments))
        {
            foreach (var seg in oldSegments) ApplySegmentToGrid(seg, false);
            pathSegmentsByPathId.Remove(pathId);
        }
    }

    /// <summary>
    /// 線分をセル寸法の半分間隔で分割した点が通るグリッドセルへ、セグメントを1件ずつ登録または削除する。
    /// SpatialHash.Remove は1回で1件しか消さないため、登録と削除は同じ点列・同じ重複省略で回す。
    /// 直前の点と同じセルは飛ばし、1セルに同じセグメントを重ねて登録しない。
    /// </summary>
    private void ApplySegmentToGrid(PathSegment segment, bool add)
    {
        if (segmentGrid == null) return;

        Vector3 start = segment.a;
        Vector3 end = segment.b;
        float cellSize = segmentGridCellSize;
        float interval = cellSize * 0.5f;
        int divisions = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(start, end) / interval));

        bool hasPrevious = false;
        int previousX = 0, previousZ = 0;
        for (int i = 0; i <= divisions; i++)
        {
            Vector3 point = Vector3.Lerp(start, end, (float)i / divisions);
            int cellX = Mathf.FloorToInt(point.x / cellSize);
            int cellZ = Mathf.FloorToInt(point.z / cellSize);
            if (hasPrevious && cellX == previousX && cellZ == previousZ) continue;
            hasPrevious = true;
            previousX = cellX;
            previousZ = cellZ;
            if (add) segmentGrid.Add(segment, point);
            else segmentGrid.Remove(segment, point);
        }
    }

    private void AddPathToNodeIndex(PathSummary path)
    {
        if (path == null) return;
        if (path.start_node_id > 0)
        {
            if (!pathsByNodeIndex.TryGetValue(path.start_node_id, out var startSet))
            {
                startSet = new HashSet<int>();
                pathsByNodeIndex[path.start_node_id] = startSet;
            }
            startSet.Add(path.path_id);
        }
        if (path.end_node_id > 0)
        {
            if (!pathsByNodeIndex.TryGetValue(path.end_node_id, out var endSet))
            {
                endSet = new HashSet<int>();
                pathsByNodeIndex[path.end_node_id] = endSet;
            }
            endSet.Add(path.path_id);
        }
    }

    private void RemovePathFromNodeIndex(int pathId, int startNodeId, int endNodeId)
    {
        if (startNodeId > 0 && pathsByNodeIndex.TryGetValue(startNodeId, out var startSet))
        {
            startSet.Remove(pathId);
            if (startSet.Count == 0) pathsByNodeIndex.Remove(startNodeId);
        }
        if (endNodeId > 0 && pathsByNodeIndex.TryGetValue(endNodeId, out var endSet))
        {
            endSet.Remove(pathId);
            if (endSet.Count == 0) pathsByNodeIndex.Remove(endNodeId);
        }
    }

    private static bool IsPillarObstacle(PathSummary path)
    {
        return path != null && (path.path_type == Constants.PATH_DEFAULT || path.path_type == Constants.PATH_MOTORWAY
            || path.path_type == Constants.PATH_STREET || path.path_type == Constants.PATH_WALKWAY || IsTrackTypePath(path));
    }

    /// <summary>曲線・セル境界と橋脚の張り出しを含めるため、通過セルの隣接セルまで候補を集める。</summary>
    private HashSet<int> CollectPillarNeighborIds(IEnumerable<Vector2Int> cells, int ownId)
    {
        var ids = new HashSet<int>();
        foreach (var cell in cells)
        for (int x = -1; x <= 1; x++)
        for (int z = -1; z <= 1; z++)
        {
            if (!pathIdsByCell.TryGetValue(cell + new Vector2Int(x, z), out var neighbors)) continue;
            foreach (int id in neighbors) if (id != ownId) ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// メッシュの有無やLODを参照せず、最新データから通行空間を作る。橋脚候補ごとには再計算しない。
    /// 相手の点列は未切断点列のキャッシュを共有で渡し、路面の持ち上げは Corridor.YOffset で表す。
    /// </summary>
    public List<PillarClearance.Corridor> GetPillarObstacles(PathSummary path)
    {
        var result = new List<PillarClearance.Corridor>();
        // 生成中の新データはまだ通過セルが登録されていないことがあるので、そのときだけ計算し直す。
        bool registered = pathDataCache.TryGetValue(path.path_id, out PathSummary current) && ReferenceEquals(current, path);
        foreach (int id in CollectPillarNeighborIds(registered ? GetPathCoveredCellsCached(path) : GetPathCoveredCells(path), path.path_id))
        {
            if (!pathDataCache.TryGetValue(id, out var other) || !IsPillarObstacle(other)) continue;
            var master = MasterDataStore.Instance?.GetPathMaster(other.path_variant);
            if (master?.model_type == "none") continue;
            // 切断前の中心線を使い、交差点内にも通行空間を残す。描画用点列キャッシュは旧版の可能性がある。
            var points = GetUntrimmedPoints(other, out _);
            if (points == null || points.Count < 2) continue;
            float platform = master?.platform_width ?? 0f;
            float left = 0f, right = 0f;
            if (IsTrackTypePath(other) && platform > 0f) TrackPath.ResolvePlatformExtents(other, platform, out left, out right);
            result.Add(new PillarClearance.Corridor
            {
                Points = points,
                YOffset = RoadPartHeights.BODY_ABOVE_SPLINE,
                HalfWidth = Mathf.Max(other.width, GetRoadWidth(other.path_variant)) * 0.5f + Mathf.Max(left, right),
                Height = TunnelSectionUtil.GetClearanceHeight(other.path_variant)
            });
        }
        return result;
    }

    private void EnqueuePillarNeighborRebuilds(PathSummary path, IEnumerable<Vector2Int> cells)
    {
        if (!IsPillarObstacle(path)) return;
        foreach (int id in CollectPillarNeighborIds(cells, path.path_id))
        {
            if (!pathDataCache.TryGetValue(id, out var neighbor)) continue;
            var master = MasterDataStore.Instance?.GetPathMaster(neighbor.path_variant);
            if (!(master?.height_editable ?? false)
                || ((master.pillar_spacing ?? 0f) <= 0f && !BrickArcadeGenerator.HasArcade(neighbor))) continue;
            bool added = pendingPillarRebuildIds.Add(id);
            pendingPillarRebuildSerials[id] = pathBuildSerialCounter;
            if (!added) continue;
            if (!PowerLinePath.Handles(neighbor)) pendingPowerRebuildIds.Remove(id);
            InvalidateTerrainFollowPath(neighbor);
            geometryRevisions.TryGetValue(id, out int revision);
            geometryRevisions[id] = revision + 1;
        }
    }

    /// <summary>LRT停留所のホーム範囲が変わったパスと、通知時点の生成通し番号。LODバッチ外の LateUpdate で再生成キューへ移す。</summary>
    private readonly Dictionary<int, long> pendingLrtPlatformRebuildSerials = new Dictionary<int, long>();

    /// <summary>
    /// LRT停留所のホーム範囲(LrtStopPlatforms の索引)が変わったパスを作り直す。描画キーの revision を進めて
    /// 退避中の古いGOを再利用させず、生成済みのGOがあるパスだけを地形追従再生成キューへ積む(未生成のパスは
    /// 次の生成で索引を読む)。通知より後に作り直したGOはワーカーが省く。
    /// 近接する線路も作り直す(結合コリドーの主線路は相手のホーム区間を避けて架線柱を立てるため)。
    /// </summary>
    public void RequestLrtPlatformRebuild(IEnumerable<int> pathIds)
    {
        var targets = new HashSet<int>();
        foreach (int id in pathIds)
        {
            targets.Add(id);
            if (pathDataCache.TryGetValue(id, out PathSummary path)) targets.UnionWith(CollectNearbyTrackPathIds(path));
        }
        foreach (int id in targets)
        {
            geometryRevisions.TryGetValue(id, out int revision);
            geometryRevisions[id] = revision + 1;
            if (pathDataCache.ContainsKey(id) && pathObjectsCache.TryGetValue(id, out GameObject go) && go != null)
                pendingLrtPlatformRebuildSerials[id] = pathBuildSerialCounter;
        }
    }

    private readonly Dictionary<int, long> pendingBuildingAccessRebuilds = new();

    private void OnBuildingRoadAccessChanged(IReadOnlyList<BuildingRoadAccessIndex.Opening> openings)
    {
        var changed = new HashSet<int>();
        foreach (var opening in openings)
            foreach (var position in BuildingRoadAccessIndex.Samples(opening))
                foreach (var path in GetPathsNearCircle(position, 64f))
                {
                    if (path.path_type != Constants.PATH_STREET || changed.Contains(path.path_id)) continue;
                    if (!TryGetUntrimmedPolylineShared(path.path_id, out var points) || points.Count < 2)
                    { changed.Add(path.path_id); continue; }
                    // セルは候補抽出だけに使う。間口の標本間隔も含む距離で絞り、大きなセル全体を再生成しない。
                    float reach = GetRoadWidth(path.path_variant) * .5f + RoadAccessPlan.MaxGap + 16f;
                    for (int i = 1; i < points.Count; i++)
                        if (Utils.XZDistanceSquared(position, PolylineUtils.ClosestPointOnSegmentXZ(position, points[i - 1], points[i])) <= reach * reach)
                        { changed.Add(path.path_id); break; }
                }
        foreach (int id in changed)
        {
            geometryRevisions.TryGetValue(id, out int revision);
            geometryRevisions[id] = revision + 1;
            pendingBuildingAccessRebuilds[id] = pathBuildSerialCounter;
        }
    }

    private void LateUpdate()
    {
        if (isLodPathBatchActive) return;
        foreach (var entry in pendingBuildingAccessRebuilds)
            if (pathDataCache.ContainsKey(entry.Key)) EnqueueTerrainFollowPath(entry.Key, entry.Value, TerrainFollowQueueReason.Other);
        pendingBuildingAccessRebuilds.Clear();
        if (pendingLrtPlatformRebuildSerials.Count > 0)
        {
            foreach (var kvp in pendingLrtPlatformRebuildSerials)
                EnqueueTerrainFollowPath(kvp.Key, kvp.Value, TerrainFollowQueueReason.Other);
            pendingLrtPlatformRebuildSerials.Clear();
        }
        if (pendingPillarRebuildIds.Count == 0) return;
        // 同一フレームの登録・削除完了を待つ。バッチ途中に描かれた橋脚も最新の周辺データで再評価する。
        // 通知より後に作られたGO(バッチのフェーズ2など)は、取り出し時に通し番号で省く。
        foreach (int id in pendingPillarRebuildIds)
        {
            long serial = pendingPillarRebuildSerials.TryGetValue(id, out long notified) ? notified : pathBuildSerialCounter;
            EnqueueTerrainFollowPath(id, serial,
                pendingPowerRebuildIds.Contains(id) ? TerrainFollowQueueReason.Other : TerrainFollowQueueReason.Pillar);
        }
        pendingPillarRebuildIds.Clear();
        pendingPillarRebuildSerials.Clear();
        pendingPowerRebuildIds.Clear();
    }

    private void SavePathToCache(PathSummary path)
    {
        if (path == null) return;
        bool changed = !pathDataCache.TryGetValue(path.path_id, out var oldPath)
            || oldPath.version != path.version || oldPath.status != path.status
            || oldPath.start_node_id != path.start_node_id || oldPath.end_node_id != path.end_node_id;
        if (changed)
        {
            if (oldPath != null)
            {
                EnqueuePowerNeighbors(oldPath);
                InvalidateTerrainFollowPath(oldPath);
                EnqueuePillarNeighborRebuilds(oldPath, GetPathCoveredCellsCached(oldPath));
            }
            InvalidateTerrainFollowPath(path);
        }
        // 進捗はversionを上げずに毎エポック進むので、施工先端に追従する表示と工事音は進捗差分でも更新する。
        bool progressChanged = oldPath != null && ConstructionPathBuilder.Progress(oldPath) != ConstructionPathBuilder.Progress(path);
        if (progressChanged) InvalidateTerrainFollowPath(path);
        if (oldPath != null) RemovePathFromNodeIndex(path.path_id, oldPath.start_node_id, oldPath.end_node_id);
        // 版が同じ再受信は形状が同じなので、アンカー点列を新しいインスタンスへ引き継ぐ。
        if (!changed && !progressChanged && !ReferenceEquals(oldPath, path)
            && anchorPolylineCache.TryGetValue(path.path_id, out AnchorEntry anchorEntry) && ReferenceEquals(anchorEntry.source, oldPath))
            anchorEntry.source = path;
        // 未切断の点列は進捗に依らないので、進捗だけの変化でも引き継ぐ。
        if (changed) DropUntrimmedPoints(path.path_id);
        else if (!ReferenceEquals(oldPath, path)
            && untrimmedPointsCache.TryGetValue(path.path_id, out PointsEntry pointsEntry) && ReferenceEquals(pointsEntry.source, oldPath))
            pointsEntry.source = path;
        pathDataCache[path.path_id] = path;
        if (changed) QueueUndergroundPath(path.path_id);
        AddPathToNodeIndex(path);
        IndexProvisionalTrackEnds(path);
        // 工事音の点列は新しい通過セルで地形署名を取るので、セル登録の後に同期する。
        if (changed) RegisterPathCells(path);
        if (changed || progressChanged) SyncConstructionSound(path);
        if (changed)
        {
            EnqueuePowerNeighbors(path);
            EnqueuePillarNeighborRebuilds(path, GetPathCoveredCellsCached(path));
        }
    }

    private void RemovePathFromCache(int pathId)
    {
        RemoveUndergroundPath(pathId);
        if (pathDataCache.TryGetValue(pathId, out var path))
        {
            EnqueuePowerNeighbors(path);
            InvalidateTerrainFollowPath(path);
            EnqueuePillarNeighborRebuilds(path, GetPathCoveredCellsCached(path));
            RemovePathFromNodeIndex(pathId, path.start_node_id, path.end_node_id);
            UnindexProvisionalTrackEnds(pathId);
            pathDataCache.Remove(pathId);
            DropUntrimmedPoints(pathId);
            ConstructionAmbience.Unregister(ConstructionAmbience.Kind.Path, pathId);
        }
    }

    private void EnqueuePowerNeighbors(PathSummary path)
    {
        if (!PowerLinePath.Handles(path) && !IsTrackTypePath(path)) return;
        var nodes = new HashSet<int> { path.start_node_id, path.end_node_id };
        if (IsTrackTypePath(path))
            foreach (int id in CollectNearbyTrackPathIds(path))
                if (pathDataCache.TryGetValue(id,out var rail))
                {
                    nodes.Add(rail.start_node_id);
                    nodes.Add(rail.end_node_id);
                }
        foreach (int node in nodes)
            foreach (var neighbor in GetPathsConnectedToNode(node))
            {
                if (neighbor.path_id == path.path_id || !PowerLinePath.Handles(neighbor)) continue;
                bool added = pendingPillarRebuildIds.Add(neighbor.path_id);
                pendingPillarRebuildSerials[neighbor.path_id] = pathBuildSerialCounter;
                pendingPowerRebuildIds.Add(neighbor.path_id);
                if (!added) continue;
                InvalidateTerrainFollowPath(neighbor);
                geometryRevisions.TryGetValue(neighbor.path_id, out int revision);
                geometryRevisions[neighbor.path_id] = revision + 1;
            }
    }

    /// <summary>
    /// 工事音の現場登録をパスデータに同期する。工事中だけ施工の先端(工事車両が並ぶ位置)の1点を登録し、それ以外は解除する。
    /// 道路全体から鳴らすと作業していない区間まで工事音が聞こえるため、車両の配置と同じ先端座標だけを発音位置にする。
    /// 座標は描画点列と同じバックエンド座標(FloatingOriginのずれを含まない)で渡す。
    /// </summary>
    private void SyncConstructionSound(PathSummary path)
    {
        List<Vector3> points = path.status == Constants.STATUS_CONSTRUCTING ? CalculatePathPoints(path) : null;
        if (points == null || points.Count < 2)
        {
            ConstructionAmbience.Unregister(ConstructionAmbience.Kind.Path, path.path_id);
            return;
        }
        var front = ConstructionPathBuilder.FrontPoint(path, points, UsesTunnelMesh(path, points));
        ConstructionAmbience.Register(ConstructionAmbience.Kind.Path, path.path_id, new[] { front });
    }

    private void RegisterPathData(PathSummary path)
    {
        if (path == null) return;
        SavePathToCache(path);
        if (isLodPathBatchActive)
        {
            if (path.start_node_id > 0) deferredLodIntersectionNodeIds.Add(path.start_node_id);
            if (path.end_node_id > 0) deferredLodIntersectionNodeIds.Add(path.end_node_id);
            return;
        }

        if (path.start_node_id > 0) UpdateNodeIntersectionMesh(path.start_node_id);
        if (path.end_node_id > 0) UpdateNodeIntersectionMesh(path.end_node_id);
    }

    private void UnregisterPathData(PathSummary path)
    {
        if (path == null) return;
        RemovePathFromCache(path.path_id);
        if (path.start_node_id > 0) UpdateNodeIntersectionMesh(path.start_node_id);
        if (path.end_node_id > 0) UpdateNodeIntersectionMesh(path.end_node_id);
    }

    /// <summary>
    /// 指定パスのローカルなキャッシュとGameObjectを即座に破棄する。
    ///
    /// 結合(merge)のようにバックエンドでパスが消える操作の直後に、差分リロードの
    /// 「レスポンスに含まれないパスを消す」判定(SyncPathsInCells)を待たずに確実に消すために使う。
    /// リロードは非同期のコルーチンで、途中でキャンセルされると削除判定まで到達しないことがある。
    /// </summary>
    public void RemovePathLocally(int pathId)
    {
        if (!pathObjectsCache.TryGetValue(pathId, out GameObject obj)) return;

        var mergeNodes = new HashSet<int>();
        if (pathDataCache.TryGetValue(pathId, out PathSummary removedPath))
        {
            // 撤去した線路と並走していた線路の結合を解くため、通過セルキャッシュを消す前に積む。
            EnqueueAdjacentTrackNeighborRebuilds(removedPath);
            EnqueueAdjacentRoadMergeRebuilds(removedPath, mergeNodes);
        }

        UnregisterPathDataById(pathId);
        if (removedPath != null) NotifyDepotTerminals(removedPath);
        UnregisterPathCells(pathId);
        UnregisterPathSegments(pathId);
        DropEndNodeIntersectionSignatures(removedPath);
        pathStartConnectionPoints.Remove(pathId);
        pathEndConnectionPoints.Remove(pathId);
        pathStartSidewalkSetbacks.Remove(pathId);
        pathEndSidewalkSetbacks.Remove(pathId);
        DropRoadEndJoins(pathId);
        pathVersionCache.Remove(pathId);

        if (obj != null)
        {
            DrawLodStats.OnDrawLodChanged(DrawLodStats.Category.Path, DrawLodInfo.GetDrawLod(obj, DrawLod.Normal), null);
            Destroy(obj);
        }
        RemovePathObject(pathId);
        closePathIds.Remove(pathId);
        // 撤去した本線へ寄せていたランプの端の交差点を、撤去後の状態で計算し直す。
        foreach (int node in mergeNodes)
        {
            if (isLodPathBatchActive) deferredLodIntersectionNodeIds.Add(node);
            else UpdateNodeIntersectionMesh(node);
        }
    }

    private void UnregisterPathDataById(int pathId)
    {
        UnregisterPathCells(pathId);
        pathPointsCache.Remove(pathId);
        pathLabelPositions.Remove(pathId);
        if (pathDataCache.TryGetValue(pathId, out var cachedPath))
        {
            UnregisterPathData(cachedPath);
        }
        else
        {
            RemovePathFromCache(pathId);
        }
    }

    /// <summary>
    /// 地形タイルのデータ適用が完了した時に呼ばれる。
    /// 該当セルおよび隣接セルと交差するパスを再生成キューへ積む(実際の再生成は
    /// TerrainFollowRebuildWorker が視野優先・フレーム予算制で行う)。
    /// 既存オブジェクトを先に破棄せず、新しいメッシュが作成できてから差し替える。
    ///
    /// pathSegmentsByPathId を用いて線分と矩形の交差判定を行うことで、
    /// セル内に始点・ウェイポイントがない「通過のみ」の道路も正しく再構築対象に含める。
    /// 登録線分は道路の中心線だけなので、道路幅や橋脚の分だけ隣のセルの地形に掛かる道路を
    /// 取りこぼさないよう、対象セルと上下左右の隣接セルを合わせた範囲で交差判定を行う。
    /// </summary>
    private void OnTerrainTileReadyHandler(Vector2Int cellIndex, Terrain terrain)
    {
        Utils.InvalidateHeightCache();
        // 通知抑制中も無効化する。取消されたロードで到着したタイルも取りこぼさない。
        InvalidateTerrainFollowCell(cellIndex);

        if (deferTerrainTilePathRebuilds)
        {
            // 破棄したイベントのセルを記録しておき、EndDeferred 時に呼び出し元へ返す。
            deferredTerrainReadyCells.Add(cellIndex);
            return;
        }

        // 地形変更セルとその隣接4セルのBBOXを取得
        List<Rect> targetRects = new List<Rect>(5);
        Vector2Int[] neighborOffsets = {
            new Vector2Int(0, 0),
            new Vector2Int(-1, 0), new Vector2Int(1, 0),
            new Vector2Int(0, -1), new Vector2Int(0, 1)
        };
        foreach (var offset in neighborOffsets)
        {
            Vector2Int checkCell = cellIndex + offset;
            CalculateCellBounds(checkCell, out float sx, out float ex, out float sz, out float ez);
            targetRects.Add(new Rect(sx, sz, ex - sx, ez - sz));
        }

        // 判定対象となるパスを pathIdsByCell (セルごとの通過パスIDセット) から絞り込む。
        // 全パスを走査する代わりに、該当5セルに登録されているパス候補のみを抽出することで、
        // 計算量を大幅に削減し、地形更新時のスパイクを防止する。
        HashSet<int> candidatePathIds = new HashSet<int>();
        foreach (var offset in neighborOffsets)
        {
            Vector2Int checkCell = cellIndex + offset;
            if (pathIdsByCell.TryGetValue(checkCell, out HashSet<int> pathIds))
            {
                foreach (int pathId in pathIds)
                {
                    candidatePathIds.Add(pathId);
                }
            }
        }

        HashSet<int> pathsToRebuild = new HashSet<int>();
        foreach (int pathId in candidatePathIds)
        {
            if (pathSegmentsByPathId.TryGetValue(pathId, out List<PathSegment> segments))
            {
                if (segments == null) continue;

                bool intersects = false;
                foreach (var seg in segments)
                {
                    foreach (var rect in targetRects)
                    {
                        if (SegmentIntersectsRect(seg.a, seg.b, rect))
                        {
                            intersects = true;
                            break;
                        }
                    }
                    if (intersects) break;
                }

                if (intersects)
                {
                    pathsToRebuild.Add(pathId);
                }
            }
        }

        // ここで同期一括再生成すると、交差パス数×(スプライン+SplineMesh群の生成コスト)が
        // 1フレームに集中してヒッチになる。再生成は視野優先・フレーム予算制のワーカーへ委ねる。
        foreach (int pathId in pathsToRebuild)
        {
            EnqueueTerrainFollowPath(pathId, pathBuildSerialCounter, TerrainFollowQueueReason.Terrain);
        }
    }

    /// <summary>
    /// 指定セル群を通る道路の形状を、現在の地形高さに合わせて再計算する。
    /// dynamic地形の更新後に CellDataLoader から呼び出される。
    /// バックエンドからの再取得は行わず、キャッシュされた PathSummary を使って地形追従でメッシュを再生成する。
    /// </summary>
    public IEnumerator RebuildPathShapesForCellsCoroutine(List<Vector2Int> cells)
    {
        // ノードの位置・接続種別が変わった場合は接続形状の再確認が必要。
        if (terrainFollowNodeVersion != NodeDataVersion) ClearTerrainFollowProgress();
        HashSet<int> pathIds = new HashSet<int>();
        foreach (var cell in cells)
        {
            if (!pathIdsByCell.TryGetValue(cell, out HashSet<int> ids)) continue;
            foreach (int id in ids) pathIds.Add(id);
        }

        // 「カメラに映っている道路から先に」地形へ馴染ませるため、視野優先スコアで昇順ソートする。
        // 1本の再生成が重く1フレームに1〜数本しか進まないため、処理順が体感品質を大きく左右する
        // (視野外の道路が先に処理されると、目の前の道路が地形に埋まったまま何十フレームも残る)。
        Camera cam = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
        Vector3 camFwd = cam != null ? Utils.GetCameraHorizontalForward(cam) : Vector3.forward;

        var orderedPathIds = new List<int>(pathIds);
        var pathScores = new Dictionary<int, float>(orderedPathIds.Count);
        foreach (int pathId in orderedPathIds)
        {
            pathScores[pathId] = pathDataCache.TryGetValue(pathId, out PathSummary p)
                ? ScorePathForViewPriority(p, camPos, camFwd)
                : float.MaxValue;
        }
        orderedPathIds.Sort((a, b) => pathScores[a].CompareTo(pathScores[b]));

        // 1件ごとに完了を記録して共通予算へ戻す。取消し後も記録済みの本線は再生成しない。
        // 地形が生成時と同じ道路は作り直さずに完了扱いにし、その判定は予算の範囲でまとめて進める。
        var skipWatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (int pathId in orderedPathIds)
        {
            if (pathDataCache.TryGetValue(pathId, out PathSummary path))
            {
                if (terrainFollowCompletedPaths.TryGetValue(pathId, out var completed)
                    && completed == (path.version, path.status) && HasPathRepresentation(path))
                {
                    RenderWorkScheduler.CountEvent("terrainFollow.pathResumeSkipped");
                    continue;
                }
                if (IsTerrainCurrent(path))
                {
                    terrainFollowCompletedPaths[pathId] = (path.version, path.status);
                    RenderWorkScheduler.CountEvent("terrainFollow.pathTerrainUnchanged");
                    if (skipWatch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
                    {
                        yield return RenderWorkScheduler.Continue;
                        skipWatch.Restart();
                    }
                    continue;
                }
                LodTier tier = LodTier.Far;
                foreach (var cell in GetPathCoveredCellsCached(path))
                {
                    // 簡易的なtier判定: 指定セル群に含まれるかどうかは関係なく、
                    // 現在のキャメラ範囲で判定するべきだが、ここでは既存のtierを維持する。
                    // 実際には CreateOrUpdatePath 内部で position から tier を再判定しないため、
                    // 現在の tier を引き継ぐ。
                    if (cellPathLod.TryGetValue(cell, out LodTier t))
                    {
                        if (t > tier) tier = t;
                    }
                }
                // 地形追従で形状を再計算する。forceRebuild=true で既存メッシュを破棄して再生成。
                // 本コルーチンは CellDataLoader.TerrainFollowUpCoroutine から直接 yield されるため、
                // 例外を漏らすと残りの道路・交差点・プロップ調整・FinishTerrainFollowUp
                // (deferred抑制とカメラロックの解除)まで巻き込んで中断し、都市全体の
                // 地形追従が恒久的に止まる。1本の失敗はそのパスだけに留めて続行する。
                try
                {
                    if (TryRebuildPathOnce(path, tier, updateIntersections: false, forceRebuild: true, reason: "地形追従のため")
                        && HasPathRepresentation(path))
                    {
                        terrainFollowCompletedPaths[pathId] = (path.version, path.status);
                        RenderWorkScheduler.CountEvent("terrainFollow.pathCompleted");
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"PathManager: 地形追従の再生成に失敗 path={pathId}: {e}");
                }
            }

            yield return RenderWorkScheduler.Continue;
            skipWatch.Restart();
        }

        // 本線を再開時に省略しても、未完了の交差点は全対象道路から回収する。
        HashSet<int> affectedNodeIds = new HashSet<int>();
        foreach (int pathId in pathIds)
        {
            if (pathDataCache.TryGetValue(pathId, out PathSummary path))
            {
                if (path.start_node_id > 0) affectedNodeIds.Add(path.start_node_id);
                if (path.end_node_id > 0) affectedNodeIds.Add(path.end_node_id);
            }
        }
        var orderedNodeIds = new List<int>(affectedNodeIds);
        var nodeScores = new Dictionary<int, float>(orderedNodeIds.Count);
        foreach (int nodeId in orderedNodeIds)
        {
            nodeScores[nodeId] = ScoreNodeForViewPriority(nodeId, camPos, camFwd);
        }
        orderedNodeIds.Sort((a, b) => nodeScores[a].CompareTo(nodeScores[b]));

        foreach (int nodeId in orderedNodeIds)
        {
            // パス再生成と同じ理由で、1ノードの失敗が追従全体を中断しないよう捕捉して続行する。
            if (terrainFollowCompletedNodes.Contains(nodeId))
            {
                RenderWorkScheduler.CountEvent("terrainFollow.nodeResumeSkipped");
                continue;
            }
            try
            {
                UpdateNodeIntersectionMesh(nodeId);
                if (nodeDataCache.ContainsKey(nodeId) && !awaitingIntersectionNodes.Contains(nodeId)
                    && !fetchingIntersectionNodeIds.Contains(nodeId))
                {
                    terrainFollowCompletedNodes.Add(nodeId);
                    RenderWorkScheduler.CountEvent("terrainFollow.nodeCompleted");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"PathManager: 地形追従の交差点更新に失敗 node={nodeId}: {e}");
            }

            yield return RenderWorkScheduler.Continue;
        }
    }

    /// <summary>
    /// 線分 AB と軸平行矩形 rect が交差するかを判定する。
    /// まず端点が矩形内にあるかチェックし、次に矩形の4辺との交差を判定する。
    /// </summary>
    private static bool SegmentIntersectsRect(Vector3 a, Vector3 b, Rect rect)
    {
        // 端点が矩形内にあるか
        if (rect.Contains(new Vector2(a.x, a.z)) || rect.Contains(new Vector2(b.x, b.z)))
            return true;

        Vector2 p1 = new Vector2(a.x, a.z);
        Vector2 p2 = new Vector2(b.x, b.z);

        // 地形タイル到着時に候補道路の全1m線分で呼ばれるため、4辺は配列にせず直接判定する。
        Vector2 c0 = new Vector2(rect.xMin, rect.yMin);
        Vector2 c1 = new Vector2(rect.xMax, rect.yMin);
        Vector2 c2 = new Vector2(rect.xMax, rect.yMax);
        Vector2 c3 = new Vector2(rect.xMin, rect.yMax);
        return SegmentsIntersect(p1, p2, c0, c1) || SegmentsIntersect(p1, p2, c1, c2)
            || SegmentsIntersect(p1, p2, c2, c3) || SegmentsIntersect(p1, p2, c3, c0);
    }

    /// <summary>
    /// 2つの2D線分が交差するかを判定する（外積ベース）。
    /// </summary>
    private static bool SegmentsIntersect(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2)
    {
        float d = (a2.x - a1.x) * (b2.y - b1.y) - (a2.y - a1.y) * (b2.x - b1.x);
        if (Mathf.Abs(d) < 1e-6f)
            return false;

        float t = ((b1.x - a1.x) * (b2.y - b1.y) - (b1.y - a1.y) * (b2.x - b1.x)) / d;
        float u = ((b1.x - a1.x) * (a2.y - a1.y) - (b1.y - a1.y) * (a2.x - a1.x)) / d;

        return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
    }

    private void UpdateNodeIntersectionMesh(int nodeId)
    {
        if (!nodeDataCache.TryGetValue(nodeId, out var nodeData))
        {
            Debug.LogWarning($"[PathManager] nodeData not found for nodeId={nodeId}, fetching node...");
            if (fetchingIntersectionNodeIds.Add(nodeId))
            {
                RenderWorkScheduler.Start(this, FetchNodeAndUpdateIntersection(nodeId), "PathManager");
            }
            return;
        }
        deferredNewNodeIntersectionIds.Remove(nodeId);

        List<PathSummary> connectedPaths = GetPathsConnectedToNode(nodeId);

        // 交差点は、交通のパス(インフラのパスと人員搬送を除く)が2本以上つながるノードだけに作る。
        // ノードの種別ビットでは判定しない(電化線路のノードは送電網を兼ねて電力ビットを持つ)。
        // インフラのパスは「垂直結合」(端点の相対YとノードのYの不一致)を許容する仕様で、生成器が接続から外すので
        // 切断点で端点の高さを上書きしない。施設の接続端子は施設内へ軌道や管が続くので作らない。
        if (!NeedsIntersection(nodeId, connectedPaths))
        {
            RemoveNodeIntersectionMesh(nodeId);
            awaitingIntersectionNodes.Remove(nodeId);
            return;
        }

        // 接続パス数の防衛的上限（MAX_INTERSECTION_CONNECTED_PATHS のコメント参照）。
        // 超過ノードは awaitingIntersectionNodes にも積まない（パス追加のたびに
        // 再評価しても成功する見込みがないため）。
        if (connectedPaths.Count > MAX_INTERSECTION_CONNECTED_PATHS)
        {
            if (overloadedIntersectionNodeIds.Add(nodeId))
            {
                Debug.LogError($"[PathManager] nodeId={nodeId} の接続パス数({connectedPaths.Count})が" +
                               $"上限({MAX_INTERSECTION_CONNECTED_PATHS})を超えています。" +
                               $"バックエンドのデータ異常が疑われるため交差点メッシュを生成しません。");
            }
            RemoveNodeIntersectionMesh(nodeId);
            awaitingIntersectionNodes.Remove(nodeId);
            return;
        }
        overloadedIntersectionNodeIds.Remove(nodeId);

        long signature = ComputeNodeIntersectionSignature(nodeId, nodeData, connectedPaths, out bool signatureKnown);
        if (signatureKnown && nodeIntersectionSignatures.TryGetValue(nodeId, out long builtSignature) && builtSignature == signature
            && ((nodeIntersectionMeshes.TryGetValue(nodeId, out GameObject unchangedMesh) && unchangedMesh != null) || slablessMergeNodes.Contains(nodeId)))
        {
            if (unchangedMesh != null && !unchangedMesh.activeSelf) unchangedMesh.SetActive(true);
            awaitingIntersectionNodes.Remove(nodeId);
            RenderWorkScheduler.CountEvent("intersection.inputUnchanged");
            return;
        }

        // 標示の署名は通行区分と接続パスの種別・状態・通行方向・向きを含む。線路端の車止め(TrackPath)もこれらとマスターだけで
        // 決まるので、変わったら切断点が同じでも接続パスを作り直す(下の markingsChanged)。
        // 線路のつなぎ目のS字(TrackEndTransition)の受け持ちも、変われば接続パスを作り直す。
        // S字の区間では道床・ホームの外端が動くので、その線路と並走する線路も結合をやり直す。
        string transitionSignature = TrackTransitionSignature(nodeId, connectedPaths);
        string markingSignature = RoadMarkingRules.JunctionSignature(nodeId, connectedPaths) + transitionSignature;
        bool markingsChanged = !nodeMarkingSignatures.TryGetValue(nodeId, out var oldMarkingSignature) || oldMarkingSignature != markingSignature;
        nodeTransitionSignatures.TryGetValue(nodeId, out var oldTransitionSignature);
        if ((oldTransitionSignature ?? "") != transitionSignature)
        {
            if (transitionSignature.Length > 0) nodeTransitionSignatures[nodeId] = transitionSignature;
            else nodeTransitionSignatures.Remove(nodeId);
            foreach (var path in connectedPaths) EnqueueAdjacentTrackNeighborRebuilds(path);
        }

        Vector3 absolutePos = nodeData.GetAbsolutePosition();
        // 切断前の点列を共用し、本線の勾配と切断断面の高さを一致させる。
        var originalPoints = new Dictionary<int, List<Vector3>>();
        foreach (var p in connectedPaths) originalPoints[p.path_id] = CalculatePathPoints(p, false);
        var gradedPath = connectedPaths.Find(p => p.station_grade_profile != null && p.station_grade_profile.Count >= 2);
        if (gradedPath != null)
        {
            var nodePoints = new List<Vector3> { absolutePos };
            ApplyStationGrade(gradedPath, nodePoints);
            absolutePos.y = nodePoints[0].y;
        }
        NodeMeshResult meshResult = NodeMeshFactory.GenerateNodeMesh(nodeId, absolutePos, connectedPaths, nodeData.GetRelativeHeight(), p => originalPoints[p.path_id], p => GetIntersectionCutLimit(p, nodeId, originalPoints[p.path_id]));
        // 高速道路の本線をノードでつなぎ、ランプを本線の路面へ取り込む合流(MotorwayMergeLayout)と、
        // 軌道の位置がそろう線路の直進接続(TrackContinuousJoin)は板を持たない。
        bool slabless = meshResult != null && meshResult.Mesh == null && (meshResult.MotorwayMerge != null || meshResult.TrackJoin != null);
        // 道床の無い線路だけのノードは板を持たず、レールのつなぎだけをノードの GO に付ける。
        bool railsOnly = meshResult != null && meshResult.Mesh == null && meshResult.RailsOnly;
        if (!slabless && !railsOnly && (meshResult == null || meshResult.Mesh == null))
        {
            if (connectedPaths.Count >= 2)
                Debug.LogWarning($"[PathManager] meshResult or mesh is null for nodeId={nodeId}, removing mesh.");
            RemoveNodeIntersectionMesh(nodeId);
            // 接続パスが揃っていない、または交差点形状が計算できない可能性があるため、
            // 後からパス追加時に再評価できるよう追跡する。
            awaitingIntersectionNodes.Add(nodeId);
            return;
        }

        // 交差点生成に成功したら追跡リストから外す。
        awaitingIntersectionNodes.Remove(nodeId);
        if (signatureKnown) nodeIntersectionSignatures[nodeId] = signature;
        else nodeIntersectionSignatures.Remove(nodeId);

        if (slabless)
        {
            if (nodeIntersectionMeshes.TryGetValue(nodeId, out var staleMesh))
            {
                if (staleMesh != null) Destroy(staleMesh);
                nodeIntersectionMeshes.Remove(nodeId);
            }
            slablessMergeNodes.Add(nodeId);
            nodeMarkingSignatures[nodeId] = markingSignature;
        }
        else
        {
            slablessMergeNodes.Remove(nodeId);
            UpdateNodeSlab(nodeId, absolutePos, meshResult, markingSignature);
        }
        if (meshResult.MotorwayMerge != null) motorwayMerges[nodeId] = meshResult.MotorwayMerge;
        else motorwayMerges.Remove(nodeId);

        // 本線・線路をノードでつなぐ端は、分離帯の端を丸めず、つなぎ目の橋脚を片方だけが立てる。
        var joinChanged = new HashSet<int>();
        int joinA = -1, joinB = -1;
        if (slabless && meshResult.MotorwayMerge != null) { joinA = meshResult.MotorwayMerge.MainA; joinB = meshResult.MotorwayMerge.MainB; }
        else if (slabless && meshResult.TrackJoin != null) { joinA = meshResult.TrackJoin.PathA; joinB = meshResult.TrackJoin.PathB; }
        foreach (var path in connectedPaths)
        {
            bool main = slabless && (path.path_id == joinA || path.path_id == joinB);
            var join = main ? new RoadEndJoin { Continuous = true, OwnsSupport = path.path_id == Mathf.Min(joinA, joinB) } : default;
            if (SetRoadEndJoin(path, nodeId, join)) joinChanged.Add(path.path_id);
        }
        foreach (var path in connectedPaths)
            if (joinChanged.Contains(path.path_id) && (meshResult.ConnectionPoints == null || !meshResult.ConnectionPoints.ContainsKey(path.path_id)))
                OnIntersectionCutChanged(path, rebuild: pathObjectsCache.ContainsKey(path.path_id));

        if (meshResult.ConnectionPoints != null)
        {
            foreach (var kvp in meshResult.ConnectionPoints)
            {
                int pathId = kvp.Key;
                Vector3 newPoint = kvp.Value;
                bool changed = markingsChanged || joinChanged.Contains(pathId);

                if (pathDataCache.TryGetValue(pathId, out PathSummary path))
                {
                    if (path.start_node_id == nodeId)
                    {
                        if (!pathStartConnectionPoints.TryGetValue(pathId, out Vector3 current) || Vector3.Distance(current, newPoint) > 0.01f)
                        {
                            pathStartConnectionPoints[pathId] = newPoint;
                            changed = true;
                        }
                        if (ApplySidewalkSetback(pathStartSidewalkSetbacks, pathId, meshResult.SidewalkSetbacks))
                        {
                            changed = true;
                        }
                    }
                    else if (path.end_node_id == nodeId)
                    {
                        if (!pathEndConnectionPoints.TryGetValue(pathId, out Vector3 current) || Vector3.Distance(current, newPoint) > 0.01f)
                        {
                            pathEndConnectionPoints[pathId] = newPoint;
                            changed = true;
                        }
                        if (ApplySidewalkSetback(pathEndSidewalkSetbacks, pathId, meshResult.SidewalkSetbacks))
                        {
                            changed = true;
                        }
                    }

                    if (changed) OnIntersectionCutChanged(path, rebuild: true);
                }
            }
        }
    }

    /// <summary>交差点の板と付帯パーツ(歩道・壁・レール・標示)を生成結果で置き換える。</summary>
    private void UpdateNodeSlab(int nodeId, Vector3 absolutePos, NodeMeshResult meshResult, string markingSignature)
    {
        if (!nodeIntersectionMeshes.TryGetValue(nodeId, out var meshGO))
        {
            meshGO = new GameObject($"NodeMesh_{nodeId}");
            meshGO.transform.position = absolutePos;
            meshGO.transform.rotation = Quaternion.identity;

            meshGO.AddComponent<MeshFilter>();
            meshGO.AddComponent<MeshRenderer>();

            var visualModeObject = meshGO.AddComponent<VisualModeObject>();
            visualModeObject.objectType = InputObjectType.PATH;
            visualModeObject.preventMaterialChange = true;
            visualModeObject.InitializeMaterials();

            nodeIntersectionMeshes[nodeId] = meshGO;
        }
        else
        {
            meshGO.transform.position = absolutePos;
            if (!meshGO.activeSelf) meshGO.SetActive(true);
        }

        MeshFilter mf = meshGO.GetComponent<MeshFilter>();
        var owner = meshGO.GetComponent<IntersectionMeshOwner>();
        if (owner == null) owner = meshGO.AddComponent<IntersectionMeshOwner>();
        if (owner.OwnedMesh != meshResult.Mesh) MeshBakeService.Release(owner.OwnedMesh);
        owner.OwnedMesh = meshResult.Mesh;
        mf.sharedMesh = meshResult.Mesh;

        MeshRenderer mr = meshGO.GetComponent<MeshRenderer>();
        if (meshResult.Material != null)
        {
            mr.material = meshResult.Material;
        }

        var markingSurface = meshGO.GetComponent<RoadMarkingSurface>();
        if (meshResult.Markings != null)
        {
            if (markingSurface == null) markingSurface = meshGO.AddComponent<RoadMarkingSurface>();
            markingSurface.Configure(meshResult.Markings);
        }
        else if (markingSurface != null) Destroy(markingSurface);
        meshGO.GetComponent<VisualModeObject>().InitializeMaterials();
        nodeMarkingSignatures[nodeId] = markingSignature;

        UpdateNodeSidewalkMesh(meshGO, meshResult);
        UpdateNodeRailMesh(meshGO, meshResult);
        TrafficSignalBuilder.Apply(meshGO, meshResult.TrafficSignals);
    }

    /// <summary>
    /// 交差点メッシュ生成結果の歩道セットバックをキャッシュへ反映する。
    /// 変化があった場合のみ true を返す（呼び出し側で道路を再生成するトリガーになる）。
    /// セットバックが無くなった場合（例: 隣の道路が歩道ありに変わった）はエントリを削除する。
    /// </summary>
    private static bool ApplySidewalkSetback(Dictionary<int, SidewalkSetback> store, int pathId, Dictionary<int, SidewalkSetback> newSetbacks)
    {
        SidewalkSetback newValue = default;
        if (newSetbacks != null)
        {
            newSetbacks.TryGetValue(pathId, out newValue);
        }
        store.TryGetValue(pathId, out SidewalkSetback current);
        bool changed = Mathf.Abs(current.LeftOutwardSetback - newValue.LeftOutwardSetback) > 0.01f
                    || Mathf.Abs(current.RightOutwardSetback - newValue.RightOutwardSetback) > 0.01f;
        if (!changed)
        {
            return false;
        }
        if (newValue.LeftOutwardSetback <= 0f && newValue.RightOutwardSetback <= 0f)
        {
            store.Remove(pathId);
        }
        else
        {
            store[pathId] = newValue;
        }
        return true;
    }



    private void UpdateNodeSidewalkMesh(GameObject nodeMeshObject, NodeMeshResult meshResult)
    {
        const string sidewalkChildName = "Sidewalk";
        Transform child = nodeMeshObject.transform.Find(sidewalkChildName);

        if (meshResult.SidewalkConnectors == null || meshResult.SidewalkConnectors.Count == 0)
        {
            // 歩道接続が不要な交差点では、過去に作った歩道接続オブジェクトを残さない。
            // 道路更新で歩道なし道路になった場合も、古い表示が残らないようにする。
            if (child != null)
            {
                Destroy(child.gameObject);
            }
            return;
        }

        GameObject sidewalkGO;
        if (child == null)
        {
            // 交差点歩道は NodeMesh の子としてまとめる。
            // 個々の角にはさらに子オブジェクトを作り、各 SplineMesh が独立して再構築できるようにする。
            sidewalkGO = new GameObject(sidewalkChildName);
            sidewalkGO.transform.SetParent(nodeMeshObject.transform, false);
        }
        else
        {
            sidewalkGO = child.gameObject;
            // 交差点の接続道路数や道路幅が変わると、必要な歩道スプライン数と形状も変わる。
            // 差分更新よりも、一度子を破棄して現在の計算結果から作り直す方が状態不整合を起こしにくい。
            for (int i = sidewalkGO.transform.childCount - 1; i >= 0; i--)
            {
                Destroy(sidewalkGO.transform.GetChild(i).gameObject);
            }
        }

        sidewalkGO.transform.localPosition = Vector3.zero;
        sidewalkGO.transform.localRotation = Quaternion.identity;
        sidewalkGO.transform.localScale = Vector3.one;

        for (int i = 0; i < meshResult.SidewalkConnectors.Count; i++)
        {
            // SidewalkConnectorSpec は幾何計算結果のみを持つ。
            // 実際の表示は通常道路と同じ sidewalk プレハブを SplineMesh に流し込んで作る
            // （RoadPath.CreateSidewalkConnectorObject に共通化されている）。
            RoadPath.CreateSidewalkConnectorObject(sidewalkGO.transform, meshResult.SidewalkConnectors[i], i);
        }
    }

    /// <summary>
    /// 線路ノード（分岐器・平面交差）のレールコネクタを NodeMesh の子として実体化する。
    /// UpdateNodeSidewalkMesh と同じ差分方針で、一度子を破棄して現在の計算結果から作り直す
    /// （接続線路の追加・削除や向きの変更でコネクタの本数・形状が変わるため）。
    /// 幾何計算は TrackIntersectionMeshGenerator、実体化は TrackPath.CreateRailConnectorObject が担う。
    /// </summary>
    private void UpdateNodeRailMesh(GameObject nodeMeshObject, NodeMeshResult meshResult)
    {
        const string railChildName = "Rail";
        Transform child = nodeMeshObject.transform.Find(railChildName);

        if (meshResult.RailConnectors == null || meshResult.RailConnectors.Count == 0)
        {
            // 線路が撤去された交差点などで古いレール表示を残さない。
            if (child != null)
            {
                Destroy(child.gameObject);
            }
            return;
        }

        GameObject railGO;
        if (child == null)
        {
            railGO = new GameObject(railChildName);
            railGO.transform.SetParent(nodeMeshObject.transform, false);
        }
        else
        {
            railGO = child.gameObject;
            for (int i = railGO.transform.childCount - 1; i >= 0; i--)
            {
                Destroy(railGO.transform.GetChild(i).gameObject);
            }
        }

        railGO.transform.localPosition = Vector3.zero;
        railGO.transform.localRotation = Quaternion.identity;
        railGO.transform.localScale = Vector3.one;

        for (int i = 0; i < meshResult.RailConnectors.Count; i++)
        {
            TrackPath.CreateRailConnectorObject(railGO.transform, meshResult.RailConnectors[i], i);
        }
    }

    private void RemoveNodeIntersectionMesh(int nodeId)
    {
        nodeMarkingSignatures.Remove(nodeId);
        nodeTransitionSignatures.Remove(nodeId);
        motorwayMerges.Remove(nodeId);
        slablessMergeNodes.Remove(nodeId);
        // 交差点が消えたときは切断も解除する。以前の切断点を残すと道路端だけが欠ける。
        foreach (var path in GetPathsConnectedToNode(nodeId))
        {
            bool changed = SetRoadEndJoin(path, nodeId, default);
            if (path.start_node_id == nodeId)
            {
                changed |= pathStartConnectionPoints.Remove(path.path_id);
                pathStartSidewalkSetbacks.Remove(path.path_id);
            }
            if (path.end_node_id == nodeId)
            {
                changed |= pathEndConnectionPoints.Remove(path.path_id);
                pathEndSidewalkSetbacks.Remove(path.path_id);
            }
            if (changed) OnIntersectionCutChanged(path, rebuild: pathObjectsCache.ContainsKey(path.path_id));
        }
        if (nodeIntersectionMeshes.TryGetValue(nodeId, out var meshGO))
        {
            Destroy(meshGO);
            nodeIntersectionMeshes.Remove(nodeId);
        }
        nodeIntersectionSignatures.Remove(nodeId);
        deferredNewNodeIntersectionIds.Remove(nodeId);
        // ノードが削除された場合、交差点生成待ちリストからも外す。
        awaitingIntersectionNodes.Remove(nodeId);
    }

    /// <summary>
    /// 交差点の切断点・セットバックが変わった道路の後始末。描画キーの revision を進めて、古い切断点で作った
    /// 表示中・退避中のGOを再利用しないようにする。rebuild なら作り直しを要求する。描画LOD(DeterminePathDrawLod)を
    /// 通すため直接生成せず、LODバッチ中はバッチの交差点フェーズへ、それ以外は再生成キューへ積む。
    /// </summary>
    private void OnIntersectionCutChanged(PathSummary path, bool rebuild)
    {
        BumpConnectionRevision(path.path_id);
        geometryRevisions.TryGetValue(path.path_id, out int revision);
        geometryRevisions[path.path_id] = revision + 1;
        if (!rebuild) return;
        if (isLodPathBatchActive)
        {
            deferredIntersectionPathRebuildIds.Add(path.path_id);
            deferredIntersectionRebuildSerials[path.path_id] = pathBuildSerialCounter;
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"PathManager: LOD一括更新 交差点由来の再生成を延期 path{path.path_id}");
            return;
        }
        EnqueueTerrainFollowPath(path.path_id, pathBuildSerialCounter, TerrainFollowQueueReason.Other);
    }

    /// <summary>道路を削除したとき、その両端の交差点を次の更新で必ず計算し直させる。</summary>
    private void DropEndNodeIntersectionSignatures(PathSummary path)
    {
        if (path == null) return;
        nodeIntersectionSignatures.Remove(path.start_node_id);
        nodeIntersectionSignatures.Remove(path.end_node_id);
    }

    private void ClearIntersectionMeshes()
    {
        motorwayMerges.Clear();
        slablessMergeNodes.Clear();
        roadEndJoins.Clear();
        nodeIntersectionSignatures.Clear();
        deferredNewNodeIntersectionIds.Clear();
        nodeMarkingSignatures.Clear();
        nodeTransitionSignatures.Clear();
        foreach (var meshGO in nodeIntersectionMeshes.Values)
        {
            if (meshGO != null)
            {
                Destroy(meshGO);
            }
        }
        nodeIntersectionMeshes.Clear();
    }

    private bool IsPathInCells(int pathId, GameObject pathObj, List<Vector2Int> cells)
    {
        if (pathCellsById.TryGetValue(pathId, out HashSet<Vector2Int> pathCells))
        {
            foreach (var cell in cells)
            {
                if (pathCells.Contains(cell)) return true;
            }
            return false;
        }

        if (pathDataCache.TryGetValue(pathId, out PathSummary pathData))
        {
            var startPointList = pathData.GetStartPointList();
            if (startPointList != null && startPointList.Count >= 2)
            {
                return IsPointInCells(GetPathPointPosition(startPointList), cells);
            }
            var waypointList = pathData.GetWaypointList();
            if (waypointList != null && waypointList.Count > 0)
            {
                return IsPointInCells(waypointList[0].GetAbsolutePosition(), cells);
            }
        }

        if (pathObj != null)
        {
            foreach (Transform child in pathObj.transform)
            {
                if (child.CompareTag(WAYPOINT_TAG))
                {
                    return IsPointInCells(child.position, cells);
                }
            }

            MeshFilter mf = pathObj.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && mf.sharedMesh.vertexCount > 0)
            {
                return IsPointInCells(pathObj.transform.TransformPoint(mf.sharedMesh.vertices[0]), cells);
            }
        }

        return false;
    }

    /// <summary>通過セルを計算したときの version。形状は version と一緒にしか変わらないので、同じ version の再登録は省く。</summary>
    private readonly Dictionary<int, int> pathCellsVersion = new Dictionary<int, int>();

    private void RegisterPathCells(PathSummary path)
    {
        if (path == null) return;
        if (pathCellsById.ContainsKey(path.path_id) && pathCellsVersion.TryGetValue(path.path_id, out int registeredVersion)
            && registeredVersion == path.version) return;

        UnregisterPathCells(path.path_id);
        HashSet<Vector2Int> cells = GetPathCoveredCells(path);
        pathCellsById[path.path_id] = cells;
        pathCellsVersion[path.path_id] = path.version;

        foreach (var cell in cells)
        {
            if (!pathIdsByCell.TryGetValue(cell, out HashSet<int> ids))
            {
                ids = new HashSet<int>();
                pathIdsByCell[cell] = ids;
            }
            ids.Add(path.path_id);
        }
        unregisteredPathObjectIds.Remove(path.path_id);
    }

    /// <summary>パスGOを登録する。通過セルが未登録なら unregisteredPathObjectIds で追跡する。</summary>
    private void SetPathObject(int pathId, GameObject pathGO)
    {
        pathObjectsCache[pathId] = pathGO;
        if (!pathCellsById.ContainsKey(pathId)) unregisteredPathObjectIds.Add(pathId);
    }

    private void RemovePathObject(int pathId)
    {
        pathObjectsCache.Remove(pathId);
        unregisteredPathObjectIds.Remove(pathId);
    }

    private void UnregisterPathCells(int pathId)
    {
        pathCellsVersion.Remove(pathId);
        if (pathObjectsCache.ContainsKey(pathId)) unregisteredPathObjectIds.Add(pathId);
        if (!pathCellsById.TryGetValue(pathId, out HashSet<Vector2Int> cells)) return;

        foreach (var cell in cells)
        {
            if (!pathIdsByCell.TryGetValue(cell, out HashSet<int> ids)) continue;
            ids.Remove(pathId);
            if (ids.Count == 0) pathIdsByCell.Remove(cell);
        }

        pathCellsById.Remove(pathId);
    }

    private HashSet<Vector2Int> GetPathCoveredCells(PathSummary path)
    {
        HashSet<Vector2Int> cells = new HashSet<Vector2Int>();
        List<Vector3> anchors = path.curved && !(MasterDataStore.Instance?.GetPathMaster(path.path_variant)?.single_span ?? false)
            ? IntersectionPolygonUtil.SamplePlan(path) ?? GetPathAnchorPositions(path) : GetPathAnchorPositions(path);
        if (anchors == null || anchors.Count == 0) return cells;

        float halfWidth = Mathf.Max(GetRoadWidth(path.path_variant), path.width) * 0.5f;
        for (int i = 0; i < anchors.Count; i++)
        {
            AddCellsAroundPoint(cells, anchors[i], halfWidth);
            if (i < anchors.Count - 1)
            {
                AddCellsAlongSegment(cells, anchors[i], anchors[i + 1], halfWidth);
            }
        }

        return cells;
    }

    private List<Vector3> GetPathAnchorPositions(PathSummary path)
    {
        List<Vector3> anchors = new List<Vector3>();

        var startPointList = path.GetStartPointList();
        if (startPointList != null && startPointList.Count >= 2)
        {
            anchors.Add(GetPathPointFlatPosition(startPointList));
        }

        var waypointList = path.GetWaypointList();
        if (waypointList != null)
        {
            foreach (var wp in waypointList)
            {
                if (wp.position != null && wp.position.Length >= 2)
                {
                    anchors.Add(wp.GetAbsolutePosition());
                }
            }
        }

        var endPointList = path.GetEndPointList();
        if (endPointList != null && endPointList.Count >= 2)
        {
            anchors.Add(GetPathPointFlatPosition(endPointList));
        }

        return anchors;
    }

    private Vector3 GetPathPointFlatPosition(List<float> point)
    {
        if (point == null || point.Count < 2) return Vector3.zero;
        float z = point.Count >= 3 ? point[2] : point[1];
        return new Vector3(point[0], 0f, z);
    }

    private void AddCellsAlongSegment(HashSet<Vector2Int> cells, Vector3 start, Vector3 end, float margin)
    {
        float cellSize = TerrainManager.CellSize;
        float distance = Vector2.Distance(new Vector2(start.x, start.z), new Vector2(end.x, end.z));
        int samples = Mathf.Max(1, Mathf.CeilToInt(distance / (cellSize * 0.5f)));

        for (int i = 0; i <= samples; i++)
        {
            float t = samples > 0 ? (float)i / samples : 0f;
            AddCellsAroundPoint(cells, Vector3.Lerp(start, end, t), margin);
        }
    }

    private void AddCellsAroundPoint(HashSet<Vector2Int> cells, Vector3 point, float margin)
    {
        float cellSize = TerrainManager.CellSize;
        int minX = Mathf.FloorToInt((point.x - margin) / cellSize);
        int maxX = Mathf.FloorToInt((point.x + margin) / cellSize);
        int minZ = Mathf.FloorToInt((point.z - margin) / cellSize);
        int maxZ = Mathf.FloorToInt((point.z + margin) / cellSize);

        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                cells.Add(new Vector2Int(x, z));
            }
        }
    }

    private bool IsPointInCells(Vector3 position, List<Vector2Int> cells)
    {
        foreach (var cell in cells)
        {
            CalculateCellBounds(cell, out float startX, out float endX, out float startZ, out float endZ);
            if (position.x >= startX && position.x < endX && 
                position.z >= startZ && position.z < endZ)
            {
                return true;
            }
        }
        return false;
    }

    public List<NodeData> QueryNearbyNodes(Vector3 pos, float radius)
    {
        return nodeGrid?.Query(pos, radius) ?? new List<NodeData>();
    }

    public List<PathSegment> QueryNearbySegments(Vector3 pos, float radius)
    {
        return segmentGrid?.Query(pos, radius) ?? new List<PathSegment>();
    }
    
    private Vector3 GetPathPointPosition(List<float> point)
    {
        if (point == null || point.Count < 2)
        {
            Debug.LogWarning("PathManager: 不正なパス座標データ");
            return Vector3.zero;
        }

        if (point.Count >= 3)
        {
            Vector3 scaledPos = new Vector3(point[0], point[1], point[2]);
                float groundHeight = Utils.GetGroundHeight(new Vector3(scaledPos.x, 1000f, scaledPos.z));
                return new Vector3(scaledPos.x, groundHeight + scaledPos.y + RoadPartHeights.ROAD_SPLINE_OFFSET, scaledPos.z);
        }
        else
        {
            Vector3 pos = new Vector3(point[0], 1000.0f, point[1]);
            pos.y = Utils.GetGroundHeight(pos) + RoadPartHeights.ROAD_SPLINE_OFFSET;
            return pos;
        }
    }

    public float GetRoadWidth(int pathSubtype)
    {
        const float defaultBackendWidth = 4.0f;

        if (pathSubtype == 0)
        {
            return defaultBackendWidth;
        }

        var masterStore = MasterDataStore.Instance;
        if (masterStore == null || !masterStore.IsInitialized)
        {
            return defaultBackendWidth;
        }

        var masters = masterStore.GetPathMasterDictionary();
        if (masters != null && masters.TryGetValue(pathSubtype, out var master))
        {
            return master.width;
        }

        return defaultBackendWidth;
    }

    public IEnumerator FetchPath(int pathId)
    {
        yield return ApiClient.Get<ConstructionPathSummary>(
            $"/paths/{pathId}",
            path =>
            {
                if (path != null)
                    CreateOrUpdatePath(path);
            },
            error => Debug.LogError($"FetchPath({pathId}) failed: {error}")
        );
    }

    private IEnumerator FetchNodeAndUpdateIntersection(int nodeId)
    {
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[PathManager] FetchNodeAndUpdateIntersection: fetching node {nodeId}");
        // 取消しではコールバックが呼ばれないので、フラグの解除は finally でも保証する。
        try
        {
            yield return ApiClient.Get<NodeData>(
                $"/nodes/{nodeId}",
                node =>
                {
                    fetchingIntersectionNodeIds.Remove(nodeId);
                    if (node != null)
                    {
                        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"[PathManager] FetchNodeAndUpdateIntersection: node {nodeId} fetched successfully.");
                        nodeDataCache[node.node_id] = node;
                        IndexNodeCell(node.node_id);
                        UpdateNodeIntersectionMesh(node.node_id);
                    }
                    else
                    {
                        Debug.LogWarning($"[PathManager] FetchNodeAndUpdateIntersection: node {nodeId} response was null.");
                        RemoveNodeIntersectionMesh(nodeId);
                    }
                },
                error =>
                {
                    fetchingIntersectionNodeIds.Remove(nodeId);
                    Debug.LogError($"[PathManager] FetchNodeAndUpdateIntersection: node {nodeId} fetch failed: {error}");
                    RemoveNodeIntersectionMesh(nodeId);
                }
            );
        }
        finally
        {
            fetchingIntersectionNodeIds.Remove(nodeId);
        }
    }

    /// <summary>
    /// メッシュコライダーの物理データを別スレッド（Physics.BakeMesh）で非同期に生成し、
    /// ベイク完了後にコライダーコンポーネントに割り当てる。
    /// これにより、メインスレッドでの物理ベイクによる処理落ち（カクつき）を低減する。
    /// </summary>
    private IEnumerator SetColliderAsync(GameObject targetGO, Mesh mesh)
    {
        if (mesh == null) yield break;

        Task bakeTask = MeshBakeService.Bake(mesh);
        yield return Utils.WaitTask(bakeTask);

        if (targetGO != null)
        {
            MeshCollider collider = targetGO.GetComponent<MeshCollider>();
            if (collider == null)
            {
                collider = targetGO.AddComponent<MeshCollider>();
            }
            collider.sharedMesh = mesh;
        }
    }

}

[Serializable]
public class NodeGeometryResponse
{
    public List<NodeData> nodes;
}

[System.Serializable]
public class NodeData
{
    public int node_id;
    public int? station_path_id;
    public int node_temp_id;
    public float[] position;

    /// <summary>
    /// ノードタイプのビットマスク（Constants.NODE_ROAD / NODE_RAIL / … と、インフラ財ごとの
    /// ビットの OR）。溶接可否は「このマスク & パスのマスク が非0」で判定する。インフラビットを
    /// 含むか（＝インフラノードか）は InfraNetworkUtil.IsInfraMask で判定する。
    /// なお、インフラ施設の「接続端子ノード」（バックエンドが施設設置時に自動生成する）
    /// かどうかは、ノード自身のプロパティ（type）だけでは判定できない。
    /// ノードAPIは所属施設（owner）を公開しない agent→node の片方向参照の設計であり、
    /// 端子ノードの識別は建物エージェント側の infra_node_id から BuildingManager が構築する
    /// 逆引きインデックス（TryGetInfraTerminalOwner）で行う。
    /// </summary>
    public int type;

    public Vector3 PositionAsVector3()
    {
        if (position.Length >= 3)
        {
            return new Vector3(position[0], position[1], position[2]);
        }
        else
        {
            float groundHeight = Utils.GetGroundHeight(new Vector3(position[0], 1000f, position[1]));
            return new Vector3(position[0], groundHeight, position[1]);
        }
    }
    
    public float GetRelativeHeight()
    {
        if (position.Length >= 3)
        {
            return position[1];
        }
        return 0f;
    }
    
    public Vector3 GetAbsolutePosition()
    {
        if (position.Length >= 3)
        {
            float groundHeight = Utils.GetGroundHeight(new Vector3(position[0], 1000f, position[2]));
            return new Vector3(position[0], groundHeight + position[1], position[2]);
        }
        else
        {
            return PositionAsVector3();
        }
    }
}

[System.Serializable]
public class PathWaypoint
{
    public float[] position;

    /// <summary>
    /// 入口制御点（三次ベジエのP2）。前のアンカー点からこのウェイポイントへ入る方向を制御する。
    /// バックエンド座標系 [x, relativeY, z] で格納される。
    /// </summary>
    public float[] control_in;

    /// <summary>
    /// 出口制御点（三次ベジエのP1）。このウェイポイントから次のアンカー点へ向かう方向を制御する。
    /// バックエンド座標系 [x, relativeY, z] で格納される。
    /// </summary>
    public float[] control_out;
    
    public Vector3 PositionAsVector3()
    {
        if (position.Length >= 3)
        {
            return new Vector3(position[0], position[1], position[2]);
        }
        else
        {
            float groundHeight = Utils.GetGroundHeight(new Vector3(position[0], 1000f, position[1]));
            return new Vector3(position[0], groundHeight, position[1]);
        }
    }
    
    public float GetRelativeHeight()
    {
        if (position.Length >= 3)
        {
            return position[1];
        }
        return 0f;
    }
    
    public Vector3 GetAbsolutePosition()
    {
        if (position.Length >= 3)
        {
            float groundHeight = Utils.GetGroundHeight(new Vector3(position[0], 1000f, position[2]));
            return new Vector3(position[0], groundHeight + position[1], position[2]);
        }
        else
        {
            return PositionAsVector3();
        }
    }

    /// <summary>
    /// 所属アンカー（このウェイポイント）直下の地形標高を返す。
    /// 制御点の絶対化は制御点XZ直下ではなく所属アンカーの地形を基準にする。
    /// 制御点XZ直下の地形を使うとスパン途中の地形（掘割・丘）が曲線に漏れ込み、
    /// 「非追従スパンはアンカー絶対標高のみから補間する」というバックエンド
    /// （geo_path_depth.sample_absolute_path_points）の規則と形状が食い違うため。
    /// </summary>
    private float GetAnchorGroundHeight()
    {
        float x = position[0];
        float z = position.Length >= 3 ? position[2] : position[1];
        return Utils.GetGroundHeight(new Vector3(x, 1000f, z));
    }

    /// <summary>
    /// 入口制御点の絶対座標を返す。
    /// バックエンド座標系 [x, relativeY, z] を Unity絶対座標に変換する
    /// （地形基準は所属アンカー。GetAnchorGroundHeight 参照）。
    /// control_in が未設定の場合は null を返す。
    /// </summary>
    public Vector3? GetControlInAbsolute()
    {
        if (control_in == null || control_in.Length < 3) return null;
        float groundHeight = GetAnchorGroundHeight();
        return new Vector3(control_in[0], groundHeight + control_in[1], control_in[2]);
    }

    /// <summary>
    /// 出口制御点の絶対座標を返す。
    /// バックエンド座標系 [x, relativeY, z] を Unity絶対座標に変換する
    /// （地形基準は所属アンカー。GetAnchorGroundHeight 参照）。
    /// control_out が未設定の場合は null を返す。
    /// </summary>
    public Vector3? GetControlOutAbsolute()
    {
        if (control_out == null || control_out.Length < 3) return null;
        float groundHeight = GetAnchorGroundHeight();
        return new Vector3(control_out[0], groundHeight + control_out[1], control_out[2]);
    }
}

public struct PathSegment
{
    public GeneratedModels.PathSummary path;
    public int segmentIndex;
    public Vector3 a;
    public Vector3 b;
}

[System.Serializable]
public class PathMasterItem
{
    public bool streetlight_type_allowed;
    public int default_streetlight_type;
    public bool fence_type_allowed;
    public int default_fence_type;
    public bool street_tree_type_allowed;
    public int default_street_tree_type;
    public bool bench_type_allowed;
    public int default_bench_type;
    public bool bin_type_allowed;
    public int default_bin_type;
    public bool planter_type_allowed;
    public int default_planter_type;
    public bool advert_type_allowed;
    public int default_advert_type;
    public bool utility_mode_allowed;
    public int default_utility_mode;

    public int name;
    public float width;
    public float? sidewalk_width;
    public float? side_width;
    public float? tree_spacing;
    public int? lanes;
    public float? wall_width;
    public float? center_width;
    public float? pillar_spacing;
    public float? max_span;
    public float? height;
    public bool? height_editable;
    public bool frontage_allowed;

    /// <summary>
    /// 海岸線の外(海上・海底)へ敷設してよいバリアントか(海底送電線・海底石油パイプライン)。
    /// 敷設中の海・湖の横断判定の対象外になる。バックエンドの海岸線検証の免除と同じ列。
    /// </summary>
    public bool over_water_allowed;

    /// <summary>
    /// インターチェンジのランプ用バリアントか。バックエンドはランプの連結成分を起点にIC/JCTを自動検出する
    /// (検出結果は InterchangeManager が表示する)。描画では、ランプでない高速道路を本線とみなし、
    /// ランプを本線の路面へ結合・取り込む(RoadAdjacency / MotorwayMergeLayout)。
    /// </summary>
    public bool ramp;

    /// <summary>
    /// 許容最小高さ(m)。負の値を持つバリアント（地下道路・地下鉄用線路など）だけが
    /// 地下（相対Y &lt; 0）に建設できる。null は 0（地下禁止）扱い。
    /// </summary>
    public float? min_height;
    public int type;
    public string display_name;
    public string description;

    /// <summary>
    /// インフラパス（PATH_ELECTRICITY/PATH_WATER）のバリアントが流せるインフラ財の製品IDリスト。
    /// 網種別の判定（送電線と導水管は別網）に使う。インフラ以外のバリアントは null。
    /// </summary>
    public List<int> infra_products;

    /// <summary>
    /// 線路（PATH_TRACK/PATH_MONORAIL）の架線柱の設置間隔(m)。
    /// null または 0 以下なら非電化として架線（wire）・架線柱（pole/beam）を描画しない。
    /// 線路以外のバリアントは null。
    /// </summary>
    public float? wire_spacing;

    /// <summary>
    /// 駅パスのプラットフォーム幅(m)。null または 0 以下ならホームなし（通常の線路・道路）。
    /// 駅バリアントの width は通常線路と同じ「線路コリドーの幅」であり、プラットフォームは
    /// この幅ぶん道床の外側へ片側（または両側）に張り出して描画される。単線の設置側はパスの
    /// platform_side（内側/外側/両側。左右の解決は city.traffic_hand 基準）で決まり、
    /// lanes が2以上の駅は両外側（TrackPath.ResolvePlatformExtents）。
    /// </summary>
    public float? platform_width;

    /// <summary>
    /// 駅舎パス(PATH_STATION_HOUSE)の両終端の専有エリア(建屋)の横幅(m)。
    /// 終端セグメントと直交方向の寸法。null は STATION_HOUSE_DEFAULT_WIDTH 扱い。
    /// 駅舎以外のバリアントは null。
    /// </summary>
    public float? house_width;

    /// <summary>
    /// 駅舎パス(PATH_STATION_HOUSE)の両終端の専有エリア(建屋)の奥行き(m)。
    /// 終端セグメントに沿う方向の寸法で、矩形は終端から**外側へ**この長さぶん広がる
    /// (内側の辺が終端の上に乗る)。null は STATION_HOUSE_DEFAULT_DEPTH 扱い。
    /// 駅舎以外のバリアントは null。
    /// </summary>
    public float? house_depth;
    public string house_placement;

    /// <summary>
    /// 駅舎パス(PATH_STATION_HOUSE)の専有エリア(建屋)の横方向オフセット(m)。
    /// 終端の外向き方向を向いて右が正(=建屋のローカル+X方向)で、両終端の建屋が
    /// 同じ向き関係でずれる。連絡橋の取り付き位置に対して建屋の造形が偏心している
    /// バリアントで使う。null・0 は横ずれなし。駅舎以外のバリアントは null。
    /// </summary>
    public float? house_offset;

    /// <summary>
    /// 3D描画用の高さオフセット(m)。パスの相対高さ(relY)から base_height を引いた高さで3Dメッシュを描画する。
    /// バックエンドのロジック・交差判定は relY をそのまま使うため base_height の影響を受けない。
    ///
    /// 例: 高圧送電線で base_height=20 の場合、relY=20 で敷いたパスは3D上は高さ0m（地面）として描画され、
    /// プレハブモデル自体が20mの高さを持つことで送電線が地上を這わずに適切な高さに見える。
    /// relY=25 にすれば3D上は5mの高さとして描画され、柱が5m分伸びる。
    /// null は 0m 扱い（シフトなし＝従来動作）。
    /// </summary>
    public float? base_height;

    /// <summary>
    /// body を地形追従で細分化配置せず、柱(アンカー点)間を1枚の body 引き延ばしで接ぐか。
    /// true の場合、body は MAX_EXTRUDE_TILE_LENGTH(2.5m) ごとにタイル化されず、
    /// アンカー点(始点・ウェイポイント・終点)間の直線スパンとして1枚引き伸ばして配置される。
    /// 高架橋や桁橋のように、路面が地形の起伏に関わらず支柱間で水平に架かる構造物で使う。
    /// null は false 扱い（従来の地形追従細分化配置）。
    /// </summary>
    public bool? single_span;

    /// <summary>
    /// メッシュ生成器の種別。PathMeshFactory がこの値で生成器を切り替える。
    ///   "default" → RoadPath
    ///   "track"   → TrackPath
    ///   "none"    → メッシュ生成なし（経路探索専用パス等）
    /// null・空文字の場合は "default" とみなす。
    /// </summary>
    public string model_type;

    /// <summary>
    /// カーブ（曲線）敷設を許可するか。false の場合、UI で直線/曲線トグルが非表示になり
    /// 直線のみになる。null は true（カーブ可）扱い。
    /// </summary>
    public bool? curve_allowed;

    /// <summary>
    /// 線形制約: 許容する最小曲線半径(m)。0 または null は制限なし。敷設中のローカル推定
    /// (RoadPlanManager.CheckAlignmentViolations)が赤表示・HUD・確定ボタンの先回りに使う
    /// (権威判定はバックエンド geo_path_alignment)。
    /// </summary>
    public float? min_curve_radius;

    /// <summary>線形制約: 許容する最大縦断勾配(%)。0 または null は制限なし。</summary>
    public float? max_grade;
    /// <summary>縦断勾配を評価する標高平滑化距離(m)。最小10mでバックエンドと共用する。</summary>
    public float? smooth_factor;

    /// <summary>
    /// 路面の見た目を代表するマテリアル名（`Assets/Resources/` 直下の .mat 名）。
    /// 交差点アスファルトが「舗装路どうしならアスファルト、未舗装路が絡めば土」のように
    /// 接続道路の路面に合わせて塗り分けるために参照する。
    /// null・空文字なら body プレハブのマテリアルから解決する（従来動作）。
    /// </summary>
    public string body_material;

    /// <summary>
    /// スパン（アンカー間区間）が地形追従するか。false のバリアント（桁橋など）は
    /// relY=0 のスパンでも地形に吸着せず、アンカー絶対標高のみからの補間で
    /// 水平デッキを構成する（掘割・谷の上を跨げる）。
    /// バックエンドの交差判定（絶対標高比較のトリガー）も同じフラグを参照する。
    /// null は true（従来の地形追従）扱い。
    /// </summary>
    public bool? follow_terrain;
}

[System.Serializable]
public class PathMasterResponse
{
    public Dictionary<int, PathMasterItem> paths;
}

[Serializable]
public sealed class PagedPathListResponse
{
    public List<ConstructionPathSummary> paths;
    public int? next_cursor;
}

/// <summary>
/// 道路GO(ルート)の生成時の状態。LODキャッシュへ退避・復元してもGOと一緒に動くので、
/// 表示中のGOがどの地形・どのデータで作られたかを判定できる(PathManager.IsTerrainCurrent)。
/// buildSerial は生成の通し番号で、再生成キューの項目より後に作られたGOかを判定する。
/// </summary>
public sealed class PathBuildStamp : MonoBehaviour
{
    public long buildSerial;
    public long terrainSignature;
    public bool terrainKnown;
    public int version;
    public int status;
    public float progress;
    public int geometryRevision;
}
