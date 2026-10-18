using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Random = UnityEngine.Random;
using GeneratedModels;

/// <summary>
/// カメラ周辺の車を連続走行させる描画専用マネージャー。
/// 交通量から決まる全体枠と道路別の時間平均密度で発生・沿道到着を調整する。
/// 台数調整はノード遷移から独立しており、交差点の上では到着させない。
/// </summary>
public partial class VehicleManager : MonoBehaviour
{
    [SerializeField] private PathManager pathManager;
    [SerializeField] private CameraCellChecker cameraCellChecker;
    [Header("描画範囲")]
    [SerializeField] private float renderRadius = 400f;
    [SerializeField] private float despawnMargin = 80f;
    [SerializeField] private float maxCenterLead = 300f;   // 管理中心をカメラから水平にこの距離以内へ収める。0以下で無制限
    [Header("密度")]
    [SerializeField] private float renderFraction = 1f;
    [SerializeField] private float activeHoursPerDay = LocalConstants.VEHICLE_ACTIVE_HOURS_PER_DAY;
    [SerializeField] private int maxActiveVehicles = 300;
    [SerializeField] private float rangeScanInterval = .5f;
    [SerializeField] private float spawnFillRate = 1f;
    [SerializeField] private float minimumJourneyTime = 6f;
    [Header("負荷")]
    [SerializeField] private float spawnBudgetMs = 1f;          // 1フレームの補充に使う時間。超えた分は次フレームへ繰り越す
    [SerializeField] private float deactivationBudgetMs = .3f;  // プールへ戻した車体の SetActive(false) に使う時間
    [Header("走行")]
    [SerializeField] private float baseSpeed = 12f;
    [SerializeField] private float intersectionCurveTension = .4f;
    [SerializeField] private float heavyVehicleOuterLaneBias = .7f;
    [SerializeField] private float fadeDuration = .4f;
    [SerializeField] private bool showDebugOverlay;

    private readonly List<Vehicle> active = new List<Vehicle>();
    private readonly Dictionary<long, Queue<GameObject>> pool = new Dictionary<long, Queue<GameObject>>();
    private readonly HashSet<GameObject> pooled = new HashSet<GameObject>();
    private readonly List<GameObject> pendingDeactivation = new List<GameObject>();
    private readonly List<long> substituteKeys = new List<long>();
    private readonly Dictionary<int, Road> roads = new Dictionary<int, Road>();
    private readonly List<Bucket> buckets = new List<Bucket>();
    private readonly Dictionary<long, Bucket> bucketByKey = new Dictionary<long, Bucket>();
    private readonly List<(Road road, bool forward, float weight)> candidates = new List<(Road, bool, float)>();
    private readonly VehiclePopulationBudget population = new VehiclePopulationBudget();
    private ParcelManager parcelManager;
    private BuildingManager buildingManager;
    private Coroutine accessWorker, roadWorker;
    private bool poolWarmStarted;
    private int spawnBacklog;
    private bool regulatedThisFrame;
    private readonly Stopwatch spawnWatch = new Stopwatch();
    private bool initialFill = true, pendingInitialFill;
    private int accessSignature, accessGeneration;
    private bool accessReady;
    private Dictionary<int, List<Entrance>> entrances = new Dictionary<int, List<Entrance>>();
    private Dictionary<long, int> exitRanks = new Dictionary<long, int>();
    private readonly Dictionary<int, AccessCache> accessCache = new Dictionary<int, AccessCache>();
    private sealed class Entrance
    {
        public int parcel, building;
        public float s;
        public bool forward;
        public Vector3 destination;
    }
    private sealed class AccessCache
    {
        public int signature;
        public List<Entrance> entries;
    }
    private static long State(int path, bool forward) => ((long)path << 1) | (forward ? 1L : 0L);
    private Transform container;
    private float scanTimer, densityTimer;
    private float motionFetchTime;
    private bool motionInFlight;
    private int motionGeneration;
    private readonly Dictionary<int,RoadMotionData> motion = new();
    private readonly Dictionary<Vehicle,float> gaps = new();
    private readonly Dictionary<(int,bool,int),List<(Vehicle v,float progress)>> followGroups = new();
    private readonly Stack<List<(Vehicle v,float progress)>> followListPool = new();
    private static readonly System.Comparison<(Vehicle v,float progress)> ByProgress = (a,b)=>a.progress.CompareTo(b.progress);
    private const float SpawnClearance = 8f;   // 発生位置の前後にこの距離の空きが無い車線には出さない。重なって出ると後続が即停止するため
    private const float StuckSeconds = 10f;    // 締切を過ぎてこの時間まったく動けない車を消す。交差点を挟んだ待ち合いをほどく
    private Vector3 lastCenter;
    private bool hasCenter;
    private int densityTick;
    private int lastTrafficHand;

    private sealed class Road
    {
        public PathSummary data;
        public VehiclePolyline line;
        public RoadLaneLayout lanes;
        public List<Vector2> intervals;
        public float insideLength;
        public float halfWidth;
        public long geometryRevision = -1;   // PathManager.GetPathGeometryRevision。-1 は版不明で点列比較に頼る
        public Vector3 scanCenter;
        public float scanRadius;
        public int spawnPointGeneration = -1;   // hasSpawnPoint を計算したときの accessGeneration
        public bool hasSpawnPoint;
    }

    private sealed class Bucket
    {
        public Road road;
        public int type, count, pending;
        public float target, average;
    }

    private void Awake()
    {
        if (pathManager == null) pathManager = FindAnyObjectByType<PathManager>();
        if (cameraCellChecker == null) cameraCellChecker = CameraCellChecker.Instance ?? FindAnyObjectByType<CameraCellChecker>();
        container = new GameObject("VehicleContainer").transform;
        container.SetParent(transform, false);
        lastTrafficHand = CityInfoStore.TrafficHand;
        parcelManager = FindAnyObjectByType<ParcelManager>();
        buildingManager = FindAnyObjectByType<BuildingManager>();
    }

    private void Start() => CityInfoStore.EnsureLoaded(this);

    /// <summary>
    /// GO の無効化で止まったワーカーと通信の状態を戻す。初回ステップ前に破棄された iterator は finally を通らず、
    /// 通信コルーチンも完了コールバックを呼ばずに止まるため、ここで戻さないと再有効化後に走査・取得が二度と始まらない。
    /// </summary>
    private void OnDisable()
    {
        RenderWorkScheduler.StopAll(this);
        roadWorker = null; accessWorker = null; poolWarmStarted = false;
        motionGeneration++; motionInFlight = false;
    }

    private void Update()
    {
        // 前フレームまでにプールへ戻した車体を、早期 return する経路でも取りこぼさず非アクティブ化する。
        ProcessPendingDeactivation();
        if (pathManager == null) return;
        if (GameStateManager.Instance != null && !GameStateManager.Instance.IsGameStarted())
        {
            if (active.Count > 0 || roads.Count > 0 || roadWorker != null || accessWorker != null) ClearVehicles();
            return;
        }
        if (!poolWarmStarted && ModelManager.Instance != null)
        {
            poolWarmStarted = true;
            RenderWorkScheduler.Start(this, WarmPool(), "vehicleWarmup", () => false);
        }
        float dt = Time.deltaTime;
        if (dt <= 0) return;
        Vector3 center = ResolveTrafficCenter(cameraCellChecker, maxCenterLead);
        bool jumped = hasCenter && Utils.XZDistanceSquared(center, lastCenter) > renderRadius * renderRadius;
        if (lastTrafficHand != CityInfoStore.TrafficHand) ClearVehicles();
        else if (jumped) Recenter();
        lastTrafficHand = CityInfoStore.TrafficHand;
        lastCenter = center;
        hasCenter = true;
        scanTimer -= dt;
        if (scanTimer <= 0)
        {
            // 常駐の周期ワーカーなので初期ロード待ちの判定には含めない。
            if (roadWorker == null) roadWorker = RenderWorkScheduler.Start(this, ScanRoads(center, true), "vehicleRoads", () => false);
            scanTimer = Mathf.Max(.1f, rangeScanInterval);
        }
        float trafficDt = dt * DriveMode.SpeedMultiplier * TrafficClock.Scale;
        // 早送りでも追従間隔を小刻みに再評価し、前車を一度に飛び越えない。
        for (float left = trafficDt; left > .00001f; left -= Mathf.Min(left, 1f / 30f))
            MoveVehicles(Mathf.Min(left, 1f / 30f), center);
        densityTimer += trafficDt;
        regulatedThisFrame = false;
        // 制御周期を固定し、フレームごとの確率の切り捨て・上限依存を避ける。
        while (densityTimer >= .25f)
        {
            densityTimer -= .25f;
            RegulateDensity(.25f);
        }
        if (spawnBacklog > 0 && !regulatedThisFrame && !TrafficClock.Paused) DrainSpawnBacklog();
    }

    /// <summary>
    /// 周辺交通(走行車・路駐・歩行者)の管理中心。乗車中は乗車位置、市民の追従中は追従対象の位置。それ以外は注視点(y=0 平面との交点)を
    /// カメラ位置から水平 maxLead 以内へ寄せる。浅い俯角では交点が数km先へ飛び、見えない遠方を管理し続けるうえ、
    /// ピッチを少し変えただけで中心が大きく跳ぶため。ロード中心(CameraCellChecker)自体は変えない。
    /// </summary>
    public static Vector3 ResolveTrafficCenter(CameraCellChecker checker, float maxLead)
    {
        if (DriveMode.TryGetLoadCenter(out var ride)) return ride;
        if (CitizenFollow.TryGetLoadCenter(out var follow)) return follow;
        var camera = Camera.main;
        Vector3 center = checker != null ? checker.CurrentWorldPosition : (camera != null ? camera.transform.position : Vector3.zero);
        if (camera == null || maxLead <= 0) return center;
        Vector3 eye = camera.transform.position;
        float dx = center.x - eye.x, dz = center.z - eye.z;
        float distance = Mathf.Sqrt(dx * dx + dz * dz);
        if (distance <= maxLead) return center;
        float scale = maxLead / distance;
        return new Vector3(eye.x + dx * scale, center.y, eye.z + dz * scale);
    }

    private void ClearVehicles()
    {
        motionGeneration++; motionInFlight=false; motionFetchTime=0; motion.Clear();
        for (int i = active.Count - 1; i >= 0; i--) Despawn(i);
        roads.Clear(); buckets.Clear(); bucketByKey.Clear();
        population.Reset(); scanTimer = 0; densityTimer = 0; spawnBacklog = 0;
        RenderWorkScheduler.Stop(this, roadWorker); roadWorker = null;
        RenderWorkScheduler.Stop(this, accessWorker); accessWorker = null;
        entrances.Clear(); exitRanks.Clear(); accessCache.Clear(); initialFill = true; pendingInitialFill = false; accessReady = false; accessGeneration++;
    }

    /// <summary>
    /// 管理中心の大移動。円内に残る車はそのまま走らせ、円外の車は通常のデスポーンに任せる。
    /// roads は新しい走査が確定するまで走行中の分岐に使うので残し、古い区域への発生だけを止める。
    /// 入口キャッシュは区画と形状の署名で有効性を判定しているため、中心が変わっても捨てない。
    /// </summary>
    private void Recenter()
    {
        RenderWorkScheduler.Stop(this, roadWorker); roadWorker = null;
        RenderWorkScheduler.Stop(this, accessWorker); accessWorker = null;
        buckets.Clear(); bucketByKey.Clear();
        spawnBacklog = 0; scanTimer = 0; motionFetchTime = 0;
        pendingInitialFill = true;
    }

    /// <summary>同期で道路集合を作り直す。乗車開始は、その場で道路を得てから車を置く必要がある。</summary>
    private void RefreshRoads(Vector3 center)
    {
        RenderWorkScheduler.Stop(this, roadWorker); roadWorker = null;
        var work = ScanRoads(center, false);
        while (work.MoveNext()) { }
    }

    /// <summary>
    /// 周辺道路のスナップショットを作り、完成してから CommitRoads で一括交換する。
    /// budgeted では共通予算で分割するので、yield を跨いだ道路は最新のパスデータを取り直してから判定する。
    /// </summary>
    private IEnumerator ScanRoads(Vector3 center, bool budgeted)
    {
        try
        {
            if (pathManager == null) yield break;
            if (parcelManager == null) parcelManager = FindAnyObjectByType<ParcelManager>();
            if (buildingManager == null) buildingManager = FindAnyObjectByType<BuildingManager>();
            var next = new Dictionary<int, Road>();
            float radius = Mathf.Max(1, renderRadius + despawnMargin);
            var nearby = pathManager.GetPathsNearCircle(center, radius);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            foreach (var candidate in nearby)
            {
                if (pathManager == null) yield break;
                var data = pathManager.GetPathData(candidate.path_id);
                if (Drivable(data))
                {
                    var road = BuildRoad(data, center, radius);
                    if (road != null) next[data.path_id] = road;
                }
                if (budgeted && timer.Elapsed.TotalMilliseconds >= PerformanceConfig.SliceBudgetMs)
                {
                    yield return RenderWorkScheduler.Continue;
                    timer.Restart();
                }
            }
            CommitRoads(next, center);
        }
        finally
        {
            if (budgeted) roadWorker = null;
        }
    }

    /// <summary>
    /// 道路1本のスナップショット。形状の版(不明なら点列の完全一致)・データ・中心が同じなら前回の Road を返し、
    /// 形状だけ同じなら点列を使い回す。点列の取得は地形未ロード時に重いので、版が一致すれば呼ばない。
    /// </summary>
    private Road BuildRoad(PathSummary data, Vector3 center, float radius)
    {
        int id = data.path_id;
        roads.TryGetValue(id, out var prev);
        long revision = pathManager.GetPathGeometryRevision(id);
        VehiclePolyline line;
        if (prev != null && revision != -1 && revision == prev.geometryRevision && prev.data == data) line = prev.line;
        else
        {
            if (!pathManager.TryGetPathPolyline(id, out var points) || points.Count < 2) return null;
            line = prev != null && VehiclePolyline.SameSource(prev.line, points) ? prev.line : new VehiclePolyline(points);
        }
        if (line.Length < .01f) return null;
        if (prev != null && prev.line == line && prev.data == data && prev.geometryRevision == revision
            && prev.scanCenter == center && prev.scanRadius == radius) return prev;
        var intervals = line.InsideIntervals(center, radius);
        float length = 0;
        foreach (var interval in intervals) length += interval.y - interval.x;
        if (length <= 0) return null;
        var road = new Road { data = data, line = line, intervals = intervals, insideLength = length,
            geometryRevision = revision, scanCenter = center, scanRadius = radius };
        if (prev != null && prev.data == data) { road.lanes = prev.lanes; road.halfWidth = prev.halfWidth; }
        else
        {
            RoadPath.GetPathMasterParams(data, out float width, out float sidewalk, out float side, out _, out int lanes,
                out _, out float divider, out _, out _, out _, out _);
            road.halfWidth = width * .5f;
            road.lanes = new RoadLaneLayout(width, sidewalk, side, divider, lanes, data.travel_direction);
        }
        return road;
    }

    private void CommitRoads(Dictionary<int, Road> next, Vector3 center)
    {
        roads.Clear();
        foreach (var entry in next) roads.Add(entry.Key, entry.Value);
        FetchMotion();
        if (accessWorker == null) accessWorker = RenderWorkScheduler.Start(this, UpdateAccess(center), "vehicleAccess", () => false);
        var previous = new Dictionary<long, Bucket>(bucketByKey);
        buckets.Clear(); bucketByKey.Clear();
        float total = 0;
        foreach (var road in roads.Values)
        foreach (int type in Constants.ROAD_VEHICLE_KEYS)
        {
            // 路線車両(バス・LRT)は TransitVehicleManager が路線に沿って描くため、環境交通としては出さない。
            if (type == Constants.VEHICLE_BUS || type == Constants.VEHICLE_TRAM) continue;
            long key = Key(road.data.path_id, type);
            float target = Traffic(road.data, type) / Mathf.Max(1, activeHoursPerDay * 3600) * road.insideLength
                / MeanSpeed(road.data.path_id,type) * Mathf.Clamp01(renderFraction);
            var bucket = new Bucket { road = road, type = type, target = target,
                average = previous.TryGetValue(key, out var old) ? old.average : 0 };
            buckets.Add(bucket); bucketByKey.Add(key, bucket); total += target;
        }
        float scale = total > maxActiveVehicles ? Mathf.Max(0, maxActiveVehicles) / total : 1;
        foreach (var bucket in buckets) bucket.target *= scale;
        population.SetTarget(Mathf.Min(total, Mathf.Max(0, maxActiveVehicles)));

        // 地形追従・道路編集後の点列へ交換する。交差点通過中の曲線は完走してから更新する。
        foreach (var v in active)
        {
            if (v.InIntersection || v.ExitingRoad || !roads.TryGetValue(v.pathId, out var road)) continue;
            if (v.road != road.line) { v.s = road.line.RemapFrom(v.road, v.s); v.road = road.line; }
            SetLane(v, road, false);
        }
        // 新しい区域の目標台数が決まってから初期充填へ戻す。先に戻すと古い目標で即座に打ち切られる。
        if (pendingInitialFill) { initialFill = true; pendingInitialFill = false; }
    }

    // 周辺道路だけを走査し、区画・建物・中心線の版が変わった入口だけ再計算する。
    // 重い幾何処理と逆探索は車のUpdateから分離し、共通予算(RenderWorkScheduler)の中で分割する。
    private IEnumerator UpdateAccess(Vector3 center)
    {
        try
        {
            var snapshot = new Dictionary<int, Road>(roads);
            var usedParcels = new HashSet<int>();
            var next = new Dictionary<int, List<Entrance>>();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            int overall = CityInfoStore.TrafficHand;
            foreach (var road in snapshot.Values)
            {
                var entries = new List<Entrance>();
                int shapeHash = unchecked(road.data.path_id * 486187739 + road.line.ShapeHash);
                shapeHash = unchecked(shapeHash * 31 + road.halfWidth.GetHashCode());
                shapeHash = unchecked(shapeHash * 31 + road.data.travel_direction);
                shapeHash = unchecked(shapeHash * 31 + road.data.start_node_id);
                shapeHash = unchecked(shapeHash * 31 + road.data.end_node_id);
                overall ^= shapeHash;
                float range = renderRadius + despawnMargin;
                if (Utils.XZDistanceSquared(road.line.points[0], center) > range * range) overall ^= road.data.path_id * 17;
                if (Utils.XZDistanceSquared(road.line.points[road.line.points.Length - 1], center) > range * range) overall ^= road.data.path_id * 37;
                bool allowed = FrontageAllowed(road.data);
                if (allowed) overall ^= road.data.path_id * 79;
                if (allowed && parcelManager != null && buildingManager != null)
                {
                    foreach (int id in new List<int>(parcelManager.GetParcelIdsByFrontPath(road.data.path_id)))
                    {
                        usedParcels.Add(id);
                        var parcel = parcelManager.GetParcelSummary(id);
                        if (parcel == null) continue;
                        int signature = parcel.version ^ shapeHash ^ CityInfoStore.TrafficHand;
                        int building = RoadsideAccess.ActiveBuilding(buildingManager, id, ref signature);
                        // 地形追従や交差点切断はAPIのversionだけでは検出できない。
                        overall ^= signature;
                        if (!accessCache.TryGetValue(id, out var cached) || cached.signature != signature)
                        {
                            cached = new AccessCache { signature = signature, entries = new List<Entrance>() };
                            if (building != 0) BuildEntrances(road, parcel, building, cached.entries);
                            accessCache[id] = cached;
                        }
                        foreach (var entry in cached.entries)
                            foreach (var interval in road.intervals)
                                if (entry.s >= interval.x && entry.s <= interval.y)
                                { entries.Add(entry); overall ^= entry.parcel * 101; break; }
                        if (timer.Elapsed.TotalMilliseconds >= PerformanceConfig.SliceBudgetMs) { yield return RenderWorkScheduler.Continue; timer.Restart(); }
                    }
                }
                next[road.data.path_id] = entries;
                if (timer.Elapsed.TotalMilliseconds >= PerformanceConfig.SliceBudgetMs) { yield return RenderWorkScheduler.Continue; timer.Restart(); }
            }
            if (accessReady && overall == accessSignature) yield break;
            // 走行方向付き道路を状態とする逆グラフ。車ごとの探索は行わず、退出までの段数を共有する。
            var outgoing = new Dictionary<int, List<long>>();
            var reverse = new Dictionary<long, List<long>>();
            var ranks = new Dictionary<long, int>();
            var queue = new Queue<long>();
            float radius = renderRadius + despawnMargin;
            foreach (var road in snapshot.Values)
            foreach (bool forward in new[] { false, true })
            {
                if (!DirectionAllowsEntry(road.data.travel_direction, forward)) continue;
                long state = State(road.data.path_id, forward);
                int start = forward ? road.data.start_node_id : road.data.end_node_id;
                if (!outgoing.TryGetValue(start, out var list)) outgoing[start] = list = new List<long>();
                list.Add(state);
                reverse[state] = new List<long>();
                Vector3 end = road.line.points[forward ? road.line.points.Length - 1 : 0];
                bool sink = Utils.XZDistanceSquared(end, center) > radius * radius;
                foreach (var entry in next[road.data.path_id]) if (entry.forward == forward) sink = true;
                if (sink) { ranks[state] = 0; queue.Enqueue(state); }
            }
            foreach (var pair in reverse)
            {
                int id = (int)(pair.Key >> 1); bool forward = (pair.Key & 1) != 0;
                var road = snapshot[id];
                int end = forward ? road.data.end_node_id : road.data.start_node_id;
                bool connected = false;
                if (outgoing.TryGetValue(end, out var list))
                    foreach (long target in list)
                        if ((target >> 1) != id) { reverse[target].Add(pair.Key); connected = true; }
                // 高速道路上で折り返さない。街路の本当の行き止まりだけ逆方向へつなぐ。
                long opposite = State(id, !forward);
                if (!connected && road.data.path_type == Constants.PATH_STREET && reverse.ContainsKey(opposite))
                    reverse[opposite].Add(pair.Key);
                if (timer.Elapsed.TotalMilliseconds >= PerformanceConfig.SliceBudgetMs) { yield return RenderWorkScheduler.Continue; timer.Restart(); }
            }
            while (queue.Count > 0)
            {
                long state = queue.Dequeue();
                foreach (long previous in reverse[state])
                    if (!ranks.ContainsKey(previous)) { ranks[previous] = ranks[state] + 1; queue.Enqueue(previous); }
                if (timer.Elapsed.TotalMilliseconds >= PerformanceConfig.SliceBudgetMs) { yield return RenderWorkScheduler.Continue; timer.Restart(); }
            }
            // 完成した表を一括交換する。途中の表を車に見せず、入口喪失は退出直前にも検査する。
            entrances = next; exitRanks = ranks; accessSignature = overall; accessReady = true; accessGeneration++;
            var stale = new List<int>();
            foreach (int id in accessCache.Keys)
                if (!usedParcels.Contains(id)) stale.Add(id);
            foreach (int id in stale) accessCache.Remove(id);
        }
        finally
        {
            accessWorker = null;
        }
    }

    // 入口の幾何は歩行者と共有し、車はパス幾何の左右を通行側と走行方向へ読み替える。
    private void BuildEntrances(Road road, ParcelSummary parcel, int building, List<Entrance> result)
    {
        var candidates = new List<RoadsideAccess.Candidate>();
        RoadsideAccess.BuildCandidates(road.line, road.halfWidth, parcel, building, parcelManager, candidates);
        foreach (var c in candidates)
        {
            bool forward = CityInfoStore.TrafficHand == Constants.TRAFFIC_HAND_LEFT ? c.side < 0 : c.side > 0;
            if (!DirectionAllowsEntry(road.data.travel_direction, forward)) continue;
            result.Add(new Entrance { parcel = c.parcel, building = c.building, s = c.s, forward = forward, destination = c.destination });
        }
    }

    private static bool FrontageAllowed(PathSummary road) => RoadsideAccess.FrontageAllowed(road);

    private bool ValidEntrance(Entrance entry, int path)
    {
        var road = pathManager.GetPathData(path);
        return Drivable(road) && road.path_type == Constants.PATH_STREET && FrontageAllowed(road)
            && RoadsideAccess.EntranceValid(parcelManager, buildingManager, entry.parcel, entry.building, path, entry.destination);
    }

    private void RegulateDensity(float dt)
    {
        regulatedThisFrame = true;
        population.Observe(active.Count, dt);
        population.Advance(dt);
        foreach (var b in buckets) { b.count = 0; b.pending = 0; }
        int pending = 0;
        foreach (var v in active)
        {
            if (v.arrivalPending || v.fading) pending++;
            if (!bucketByKey.TryGetValue(Key(v.pathId, v.vehicleType), out var b)) continue;
            b.count++;
            if (v.arrivalPending || v.fading) b.pending++;
        }
        float alpha = 1 - Mathf.Exp(-dt / 8f);
        foreach (var b in buckets) b.average = Mathf.Lerp(b.average, b.count, alpha);
        int excess = active.Count - pending - population.Desired;
        if (excess > 0)
        {
            for (int i = 0; i < excess; i++) if (!ReserveArrival(false)) break;
        }
        else if (active.Count == population.Desired && pending == 0 && ++densityTick % 8 == 0)
        {
            // 総数が合っていても環状路などへ偏った車を沿道到着させ、不足道路へ再配置する。
            ReserveArrival(true);
        }

        if (population.Desired > 0 && active.Count >= population.Desired) initialFill = false;
        int missing = population.Desired - active.Count;
        int fill = Mathf.Min(missing, Mathf.Max(1, Mathf.CeilToInt(missing * (1 - Mathf.Exp(-Mathf.Max(0, spawnFillRate) * dt)))));
        // 車体の有効化・生成が1フレームに集中しないよう時間予算で打ち切る。1台目は必ず試みるので充填は止まらない。
        spawnBacklog = 0;
        spawnWatch.Restart();
        for (int i = 0; i < fill; i++)
        {
            if (active.Count >= maxActiveVehicles) break;
            if (i > 0 && spawnWatch.Elapsed.TotalMilliseconds >= spawnBudgetMs) { spawnBacklog = fill - i; break; }
            Bucket chosen = PickSpawnBucket();
            if (chosen == null) break;
            if (!Spawn(chosen)) continue;
            chosen.count++;
        }
    }

    /// <summary>制御周期の無いフレームで、前回の RegulateDensity が予算で打ち切った分を補充する。次の制御周期で再計算される。</summary>
    private void DrainSpawnBacklog()
    {
        spawnWatch.Restart();
        while (spawnBacklog > 0 && active.Count < population.Desired && active.Count < maxActiveVehicles)
        {
            if (spawnWatch.Elapsed.TotalMilliseconds >= spawnBudgetMs) return;
            Bucket chosen = PickSpawnBucket();
            if (chosen == null) { spawnBacklog = 0; return; }
            spawnBacklog--;
            if (!Spawn(chosen)) continue;
            chosen.count++;
        }
        spawnBacklog = 0;
    }

    private Bucket PickSpawnBucket()
    {
        float sum = 0;
        foreach (var b in buckets) sum += SpawnWeight(b);
        if (sum <= 0) return null;
        float pick = Random.value * sum;
        Bucket last = null;
        foreach (var b in buckets)
        {
            float weight = SpawnWeight(b);
            if (weight <= 0) continue;
            last = b; pick -= weight;
            if (pick <= 0) return b;
        }
        return last;
    }

    private float SpawnWeight(Bucket b)
        => b.target <= 0 || (!initialFill && !HasSpawnPointCached(b.road)) ? 0
            : Mathf.Max(0, b.target - b.count) + Mathf.Max(0, b.target - b.average);

    // entrances の交換は必ず accessGeneration を進め、intervals は Road 生成時にしか決まらないので、世代比較で無効化できる。
    private bool HasSpawnPointCached(Road road)
    {
        if (road.spawnPointGeneration != accessGeneration)
        {
            road.hasSpawnPoint = HasSpawnPoint(road);
            road.spawnPointGeneration = accessGeneration;
        }
        return road.hasSpawnPoint;
    }

    private bool HasSpawnPoint(Road road)
    {
        if (entrances.TryGetValue(road.data.path_id, out var list) && list.Count > 0) return true;
        foreach (var interval in road.intervals)
            if ((interval.x > .1f && DirectionAllowsEntry(road.data.travel_direction, true)) ||
                (interval.y < road.line.Length - .1f && DirectionAllowsEntry(road.data.travel_direction, false))) return true;
        return false;
    }

    private bool ReserveArrival(bool redistribute)
    {
        Vehicle selected = null;
        float best = float.NegativeInfinity;
        foreach (var v in active)
        {
            if (v == DrivenVehicle || v == FollowedVehicle || v.arrivalPending || v.fading || v.age < minimumJourneyTime) continue;
            if (!exitRanks.ContainsKey(State(v.pathId, v.forward))) continue;
            float surplus = 0;
            if (bucketByKey.TryGetValue(Key(v.pathId, v.vehicleType), out var b))
            {
                surplus = b.average - b.target - b.pending;
                // 偏りの調整は許容幅を超えた道路だけにし、小さな瞬間的超過のたびに車を入れ替えない。
                if (redistribute && surplus < Mathf.Max(.5f, b.target * .25f)) continue;
            }
            else if (redistribute) continue;
            float score = surplus + Random.value * .1f;
            if (score > best) { selected = v; best = score; }
        }
        if (selected == null) return false;
        selected.arrivalPending = true; selected.searchedPath = -1;
        if (bucketByKey.TryGetValue(Key(selected.pathId, selected.vehicleType), out var chosen)) chosen.pending++;
        return true;
    }

    private void FetchMotion()
    {
        if (motionInFlight || Time.unscaledTime < motionFetchTime || roads.Count == 0) return;
        motionInFlight=true; motionFetchTime=Time.unscaledTime+2;
        int generation=motionGeneration;
        var ids=new List<int>(roads.Keys);
        if(ids.Count>4096)ids.RemoveRange(4096,ids.Count-4096);
        StartCoroutine(ApiClient.Post<RoadMotionResponse>("/paths/motion",ids,response=>
        {
            if(generation!=motionGeneration)return;
            motion.Clear();
            if(response?.paths!=null)foreach(var item in response.paths)if(item.Valid)motion[item.path_id]=item;
            if(response != null && response.active_hours > 0) activeHoursPerDay=response.active_hours;
            motionInFlight=false;
        },(_,__)=>{if(generation==motionGeneration)motionInFlight=false;}));
    }
    private float MeanSpeed(int path,int type) => motion.TryGetValue(path,out var data) ? data.MeanSpeed : Mathf.Max(.1f,baseSpeed*SpeedFactor(type));
    private void SetMotion(Vehicle v,float connectorLength=0)
    {
        float mean=MeanSpeed(v.pathId,v.vehicleType);
        v.motionRemaining=(v.Remaining+connectorLength)/mean;
        v.motionFreeSpeed=motion.TryGetValue(v.pathId,out var data)?data.FreeSpeed:mean;
        var path=pathManager.GetPathData(v.pathId);
        v.holdAtEnd=path!=null && pathManager.GetPathsConnectedToNode(v.AheadNodeId(path.start_node_id,path.end_node_id)).Count!=2;
    }
    /// <summary>
    /// 同じ道路・向き・車線の中で、すぐ前の車との車間を求める。交差点の中の車は、入る前の車線の先頭の車としても数える。
    /// 先の道路が詰まって交差点の中で止まった車の後ろに、手前の道路の車が並ぶ。
    /// </summary>
    private void BuildFollowingGaps()
    {
        gaps.Clear();
        foreach(var list in followGroups.Values){list.Clear();followListPool.Push(list);}
        followGroups.Clear();
        foreach(var v in active)
        {
            if(v.ExitingRoad || v.fading)continue;
            AddFollower((v.pathId,v.forward,v.lane),v,Progress(v));
            if(v.InIntersection && v.entryPath>=0)AddFollower((v.entryPath,v.entryForward,v.entryLane),v,v.entryLength+v.connectorS);
        }
        foreach(var list in followGroups.Values)
        {
            list.Sort(ByProgress);
            for(int i=0;i+1<list.Count;i++)
            {
                float gap=Mathf.Max(0,list[i+1].progress-list[i].progress-RoadMotionControl.CarLength);
                gaps[list[i].v]=gaps.TryGetValue(list[i].v,out float other)?Mathf.Min(other,gap):gap;
            }
        }
    }
    private void AddFollower((int,bool,int) key,Vehicle v,float progress)
    {
        if(!followGroups.TryGetValue(key,out var list)){list=followListPool.Count>0?followListPool.Pop():new();followGroups[key]=list;}
        list.Add((v,progress));
    }
    private static float Progress(Vehicle v)=>v.InIntersection ? -(v.connector.Length-v.connectorS) : v.forward?v.s:v.TotalLength-v.s;

    /// <summary>
    /// 1台を発生させる。抽選した車線から順に試し、前後 SpawnClearance 以内に同じ車線の車がいれば別の車線へ回す。
    /// 全車線が塞がっていれば車体を取り出す前に false を返す(呼び出し側はその枠を数えない)。
    /// </summary>
    private bool Spawn(Bucket b)
    {
        var road = b.road;
        bool forward = road.data.travel_direction == Constants.DIR_FORWARD ||
            (road.data.travel_direction == Constants.DIR_BOTH && Random.value < .5f);
        float pick = Random.value * road.insideLength, s = road.intervals[0].x;
        foreach (var interval in road.intervals)
        {
            s = interval.x + Mathf.Min(pick, interval.y - interval.x);
            pick -= interval.y - interval.x;
            if (pick <= 0) break;
        }
        if (!initialFill)
        {
            var options = new List<(float s, bool forward)>();
            if (entrances.TryGetValue(road.data.path_id, out var list))
                foreach (var entry in list)
                    if (ValidEntrance(entry, road.data.path_id)) options.Add((entry.s, entry.forward));
            foreach (var interval in road.intervals)
            {
                if (interval.x > .1f && DirectionAllowsEntry(road.data.travel_direction, true)) options.Add((interval.x + Mathf.Min(5, (interval.y - interval.x) * .25f), true));
                if (interval.y < road.line.Length - .1f && DirectionAllowsEntry(road.data.travel_direction, false)) options.Add((interval.y - Mathf.Min(5, (interval.y - interval.x) * .25f), false));
            }
            if (options.Count == 0) return false;
            var option = options[Random.Range(0, options.Count)]; s = option.s; forward = option.forward;
        }
        s = Mathf.Clamp(s, 0, road.line.Length);
        float progress = forward ? s : road.line.Length - s;
        int laneCount = Mathf.Max(1, road.lanes.Count), first = ChooseLane(b.type, road), lane = -1;
        for (int k = 0; k < laneCount && lane < 0; k++)
        {
            int candidate = (first + k) % laneCount;
            if (LaneClear(road.data.path_id, forward, candidate, progress)) lane = candidate;
        }
        if (lane < 0) return false;
        int variant = VehicleModelResolver.PickVariantId(b.type, Random.Range(0, int.MaxValue));
        var go = Acquire(b.type, ref variant);
        var v = new Vehicle { vehicleType = b.type, variantId = variant,
            speed = Mathf.Max(.1f, MeanSpeed(b.road.data.path_id,b.type)),
            go = go, spawnBlendRemaining = 15 };
        v.SetPath(road.data.path_id, road.line, forward, s);
        v.lane = lane;
        SetLane(v, road, false);
        SetMotion(v);
        active.Add(v);
        Place(v);
        return true;
    }

    /// <summary>車線の抽選。大型車は heavyVehicleOuterLaneBias の確率で路肩側の車線を選ぶ。</summary>
    private int ChooseLane(int type, Road road)
    {
        bool heavy = type == Constants.VEHICLE_TRUCK || type == Constants.VEHICLE_BUS;
        return heavy && Random.value < heavyVehicleOuterLaneBias ? 0 : Random.Range(0, road.lanes.Count);
    }

    private bool LaneClear(int path, bool forward, int lane, float progress)
    {
        foreach (var o in active)
            if (o.pathId == path && o.forward == forward && o.lane == lane && !o.ExitingRoad && !o.fading
                && Mathf.Abs(Progress(o) - progress) < SpawnClearance) return false;
        return true;
    }

    private void SetLane(Vehicle v, Road road, bool choose)
    {
        if (choose) v.lane = ChooseLane(v.vehicleType, road);
        v.lane = Mathf.Clamp(v.lane, 0, road.lanes.Count - 1);
        v.laneOffset = road.lanes.Center(v.lane, CityInfoStore.TrafficHand);
        v.shoulderOffset = road.lanes.Outer * (CityInfoStore.TrafficHand == Constants.TRAFFIC_HAND_LEFT ? -1 : 1);
        v.roadHalfWidth = Mathf.Max(road.halfWidth, road.lanes.Outer);
    }

    private void MoveVehicles(float dt, Vector3 center)
    {
        float radius = Mathf.Max(1, renderRadius + despawnMargin);
        BuildFollowingGaps();
        for (int i = active.Count - 1; i >= 0; i--)
        {
            var v = active[i];
            var data = pathManager.GetPathData(v.pathId);
            if (!Drivable(data) || !DirectionAllowsEntry(data.travel_direction, v.forward)) { Despawn(i); continue; }
            float remaining = dt;
            Vector3 before = v.go.transform.position;
            if (!v.ExitingRoad && !v.fading)
            {
                float free=motion.TryGetValue(v.pathId,out var timing)?timing.FreeSpeed:v.motionFreeSpeed;
                float gap=gaps.TryGetValue(v,out float ahead)?ahead:float.PositiveInfinity;
                if(v.holdAtEnd && !v.InIntersection)gap=Mathf.Min(gap,RoadMotionControl.StopLineGap(v.Remaining,v.motionRemaining,free));
                v.speed=RoadMotionControl.FollowingSpeed(free,v.speed,gap,dt);
                v.connectorSpeed=v.speed;
                v.motionRemaining-=dt;
            }
            bool invalid = false;
            int transitions = 0;
            while (remaining > .00001f)
            {
                if (v.fading) { v.fadeElapsed += remaining; remaining = 0; break; }
                if (v.ExitingRoad)
                {
                    remaining = v.Advance(remaining);
                    if (v.ExitComplete) { v.fading = true; continue; }
                    break;
                }
                if (!v.InIntersection && v.arrivalPending &&
                    (v.accessGeneration != accessGeneration || v.searchedPath != v.pathId))
                {
                    v.accessGeneration = accessGeneration; v.searchedPath = v.pathId;
                    float previousBlend = v.ArrivalBlend, previousDistance = v.arrivalDistance;
                    bool hadTarget = v.arrivalDistance >= 0;
                    int previousParcel = v.destinationParcel;
                    Entrance selected = null;
                    if (entrances.TryGetValue(v.pathId, out var entries))
                        foreach (var entry in entries)
                        {
                            float distance = (entry.s - v.s) * (v.forward ? 1 : -1);
                            if (entry.forward != v.forward || distance < -.001f || !ValidEntrance(entry, v.pathId)) continue;
                            if (selected == null || distance < (selected.s - v.s) * (v.forward ? 1 : -1)) selected = entry;
                        }
                    if (selected != null)
                    {
                        float d = Mathf.Max(0, (selected.s - v.s) * (v.forward ? 1 : -1));
                        // 入口表の再公開で同じ入口を選び直した場合は寄せの進み具合を保つ。
                        // 別の入口へ替わる場合は、現在の寄せ量を発生時の寄せへ移して横位置を連続させる。
                        bool same = hadTarget && selected.parcel == previousParcel && Mathf.Abs(d - previousDistance) < .5f;
                        if (!same)
                        {
                            if (previousBlend > 0) v.spawnBlendRemaining = Mathf.Max(v.spawnBlendRemaining, previousBlend * 15f);
                            v.arrivalBlendLength = Mathf.Min(15, Mathf.Max(.1f, d));
                        }
                        v.arrivalDistance = d;
                        v.destinationParcel = selected.parcel; v.destinationBuilding = selected.building; v.destinationPoint = selected.destination;
                    }
                    else v.ReleaseArrival();
                }
                remaining = v.Advance(remaining);
                if (!v.InIntersection && v.arrivalDistance >= 0 && v.arrivalDistance < .001f)
                {
                    var entry = new Entrance { parcel = v.destinationParcel, building = v.destinationBuilding, destination = v.destinationPoint };
                    if (v == FollowedVehicle || ValidEntrance(entry, v.pathId)) v.BeginParcelExit(v.destinationPoint);
                    else v.ReleaseArrival();
                    continue;
                }
                if (v.InIntersection && v.connectorS >= v.connector.Length - .001f)
                {
                    v.connector = null; v.distanceSinceJunction = 0;
                    continue;
                }
                if (!v.InIntersection && v.Remaining < .001f)
                {
                    if (++transitions > 128) { invalid = true; break; }
                    if (!Transition(v))
                    {
                        // 退出先のない行き止まりでもその場では消さず、台数の帳尻合わせに使わない。動けないまま締切を過ぎれば詰まった車として消える。
                        v.age += remaining; remaining = 0; break;
                    }
                    continue;
                }
                break;
            }
            Place(v);
            // 締切を過ぎても位置が変わらない車は、交差点を挟んだ待ち合いで動けなくなったとみなして消す。
            // 締切前の停止線での待ちは数えないので、間引きで次の車までが長い待ち行列や1台だけの待ちは消えない。
            if (!v.ExitingRoad && !v.fading)
            {
                bool stuck = v.motionRemaining < 0 && Utils.XZDistanceSquared(before, v.go.transform.position) < 1e-6f;
                v.stuckSeconds = stuck ? v.stuckSeconds + dt : 0;
                if (v.stuckSeconds >= StuckSeconds && v != DrivenVehicle && v != FollowedVehicle) v.fading = true;
            }
            if (invalid || v.fadeElapsed >= Mathf.Max(.01f, fadeDuration) || (v != DrivenVehicle && v != FollowedVehicle && !v.ExitingRoad && Utils.XZDistanceSquared(v.go.transform.position, center) > radius * radius))
                Despawn(i);
        }
    }

    private bool Transition(Vehicle v)
    {
        PathSummary current = pathManager.GetPathData(v.pathId);
        if (current == null) return false;
        int node = v.AheadNodeId(current.start_node_id, current.end_node_id);
        float total = v == FollowedVehicle ? 0 : CollectCandidates(v, node);
        Road next; bool nextForward;
        if (v == FollowedVehicle)
        {
            // 行程の区間の終わりでは消えて(fading)ループを続け、走査がまだ届いていない道路は届くまで待つ。
            if (!NextFollowStep(v, out next, out nextForward)) return v.fading;
        }
        else if (candidates.Count == 0)
        {
            if (current.path_type != Constants.PATH_STREET || !DirectionAllowsEntry(current.travel_direction, !v.forward) || !roads.TryGetValue(v.pathId, out next)) return false;
            // 退出先のない末端でも任意デスポーンはせず、街路で許可された折り返しを使う。

            nextForward = !v.forward;
        }
        else
        {
            int chosen = total <= 0 ? Random.Range(0, candidates.Count) : candidates.Count - 1;
            if (total > 0)
            {
                float pick = Random.value * total;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i].weight <= 0) continue;
                    pick -= candidates[i].weight;
                    if (pick <= 0) { chosen = i; break; }
                }
            }
            if (v.arrivalPending)
            {
                int best = int.MaxValue;
                for (int i = 0; i < candidates.Count; i++)
                    if (exitRanks.TryGetValue(State(candidates[i].road.data.path_id, candidates[i].forward), out int rank) && rank < best)
                    { best = rank; chosen = i; }
            }
            if (v == DrivenVehicle) chosen = ChooseDriveCandidate(v);
            next = candidates[chosen].road; nextForward = candidates[chosen].forward;
        }
        if (v == DrivenVehicle) RequestedDrivePath = -1;
        if (next.data.path_id != v.pathId) { v.previousPath2 = v.previousPath; v.previousPath = v.pathId; }
        v.Sample(out Vector3 p0, out Vector3 t0);
        v.entryPath = v.pathId; v.entryForward = v.forward; v.entryLane = v.lane; v.entryLength = v.TotalLength;
        var merge = pathManager.GetMotorwayMerge(node);
        if (merge != null) v.lane = merge.TargetLane(current.path_id, next.data.path_id, v.lane, CityInfoStore.TrafficHand);
        v.SetPath(next.data.path_id, next.line, nextForward);
        v.spawnBlendRemaining = 0; v.arrivalDistance = -1; v.destinationParcel = 0;
        SetLane(v, next, false);
        v.Sample(out Vector3 p1, out Vector3 t1);
        v.connector = VehiclePolyline.Connector(p0, t0, p1, t1, intersectionCurveTension);
        v.connectorS = 0;
        v.connectorSpeed = v.speed;
        // 締切は道路をまたいで通しで数える。つなぎ目で使わなかった遅れは次の交差点で待ち、遅れた分は次の待ちを縮めて取り戻す。
        float carried = v.motionRemaining;
        SetMotion(v,v.connector.Length);
        v.motionRemaining += carried;
        if (v == FollowedVehicle) ApplyFollowArrival(v, next);
        return true;
    }

    private void Place(Vehicle v)
    {
        v.Sample(out Vector3 p, out Vector3 t);
        v.go.transform.SetPositionAndRotation(p, Quaternion.LookRotation(t, Vector3.up));
        VehicleAmbience.Report(VehicleAmbience.Kind.Car, AmbienceId(v.go), p, v.speed);
        float fade = v.fading ? 1 - v.fadeElapsed / Mathf.Max(.01f, fadeDuration) : v.ExitingRoad ? 1 : v.age / Mathf.Max(.01f, fadeDuration);
        v.go.transform.localScale = Vector3.one * Mathf.Clamp01(fade);
    }

    private static long PoolKey(int type, int variant) => ((long)type << 32) | (uint)variant;
    private static long AmbienceId(GameObject go) => unchecked((long)EntityId.ToULong(go.GetEntityId()));

    /// <summary>
    /// プールから車体を取り出す。指定外観の在庫が無ければ同じ車種の在庫で代替し、variant を実際の外観へ書き換える
    /// (Instantiate は1台数msかかるため)。どの外観も無いときだけ生成する。
    /// </summary>
    private GameObject Acquire(int type, ref int variant)
    {
        GameObject go = Dequeue(PoolKey(type, variant));
        if (go == null)
        {
            substituteKeys.Clear();
            foreach (var entry in pool)
                if ((int)(entry.Key >> 32) == type && entry.Value.Count > 0) substituteKeys.Add(entry.Key);
            for (int i = substituteKeys.Count; go == null && i > 0; i--)
            {
                int pick = Random.Range(0, substituteKeys.Count);
                go = Dequeue(substituteKeys[pick]);
                if (go != null) variant = (int)(uint)substituteKeys[pick];
                else substituteKeys.RemoveAt(pick);
            }
        }
        if (go == null) go = VehicleModelResolver.CreateInstance(type, variant, container);
        pooled.Remove(go);
        go.transform.localScale = Vector3.one;
        if (!go.activeSelf) go.SetActive(true);
        return go;
    }

    private GameObject Dequeue(long key)
    {
        if (!pool.TryGetValue(key, out var queue)) return null;
        while (queue.Count > 0)
        {
            var go = queue.Dequeue();
            if (go != null) return go;
            pooled.Remove(go);
        }
        return null;
    }

    private void Enqueue(long key, GameObject go)
    {
        if (!pool.TryGetValue(key, out var queue)) { queue = new Queue<GameObject>(); pool.Add(key, queue); }
        queue.Enqueue(go);
        pooled.Add(go);
    }

    /// <summary>
    /// 車体はスケール0で即座に隠してプールへ戻し、SetActive(false) は ProcessPendingDeactivation で予算内に分散する。
    /// スケール0は発生直後(age=0)と同じ表示状態なので、見た目は変わらない。
    /// </summary>
    private void Despawn(int index)
    {
        var v = active[index];
        if (v == DrivenVehicle) DrivenVehicle = null;
        if (v == WatchedVehicle) WatchedVehicle = null;
        if (v == FollowedVehicle) EndFollowRun();
        if (v.go != null)
        {
            VehicleAmbience.Remove(VehicleAmbience.Kind.Car, AmbienceId(v.go));
            v.go.transform.localScale = Vector3.zero;
            Enqueue(PoolKey(v.vehicleType, v.variantId), v.go);
            pendingDeactivation.Add(v.go);
        }
        active.RemoveAt(index);
    }

    private void ProcessPendingDeactivation()
    {
        if (pendingDeactivation.Count == 0) return;
        spawnWatch.Restart();
        for (int done = 0; pendingDeactivation.Count > 0; done++)
        {
            if (done >= 4 && spawnWatch.Elapsed.TotalMilliseconds >= deactivationBudgetMs) break;
            int last = pendingDeactivation.Count - 1;
            var go = pendingDeactivation[last];
            pendingDeactivation.RemoveAt(last);
            // 待機中に再び取り出された車体は走行中なので触らない。
            if (go != null && pooled.Contains(go) && go.activeSelf) go.SetActive(false);
        }
    }

    /// <summary>
    /// 環境交通の全外観を1台ずつ前もって生成し、初めて出る外観の Instantiate が走行中の補充に重ならないようにする。
    /// 中断されたら次の Update で最初からやり直す(在庫のある外観は飛ばす)。
    /// </summary>
    private IEnumerator WarmPool()
    {
        bool completed = false;
        try
        {
            foreach (int type in Constants.ROAD_VEHICLE_KEYS)
            {
                if (type == Constants.VEHICLE_BUS || type == Constants.VEHICLE_TRAM) continue;
                var variants = VehicleModelConfig.GetVariants(type);
                int count = Mathf.Max(1, variants.Count);
                for (int i = 0; i < count; i++)
                {
                    int variant = variants.Count > 0 ? variants[i] : 0;
                    long key = PoolKey(type, variant);
                    if (pool.TryGetValue(key, out var queue) && queue.Count > 0) continue;
                    Enqueue(key, VehicleModelResolver.CreateInstance(type, variant, container));
                    yield return RenderWorkScheduler.Continue;
                }
            }
            completed = true;
        }
        finally
        {
            if (!completed) poolWarmStarted = false;
        }
    }

    public static bool DirectionAllowsEntry(int direction, bool forward)
        => direction == Constants.DIR_BOTH || direction == (forward ? Constants.DIR_FORWARD : Constants.DIR_BACKWARD);
    private static bool Drivable(PathSummary p) => p != null && p.status == Constants.STATUS_ACTIVE &&
        (p.path_type == Constants.PATH_MOTORWAY || p.path_type == Constants.PATH_STREET);
    private static float Traffic(PathSummary p, int type)
        => p.path_traffic != null && p.path_traffic.TryGetValue(type.ToString(), out float value) && !float.IsNaN(value) && !float.IsInfinity(value) ? Mathf.Max(0, value) : 0;
    private static float SpeedFactor(int type) => type == Constants.VEHICLE_TRUCK || type == Constants.VEHICLE_BUS ? .8f : type == Constants.VEHICLE_COMMERCIAL ? .9f : 1;
    private static long Key(int path, int type) => ((long)path << 8) | (uint)type;
    private void OnGUI()
    {
        if (showDebugOverlay) GUI.Label(new Rect(10, 10, 600, 20), $"Vehicles: {active.Count} / {population.Desired} (mean {population.Target:F2}) roads: {roads.Count}");
    }
}

/// <summary>
/// 全道路の期待台数を合算してから整数化する。端数を12秒ごとに繰り越し、
/// 低交通量を道路ごとの最低1台へ膨らませない。道路分割で総枠は変化しない。
/// </summary>
public sealed class VehiclePopulationBudget
{
    public float Target { get; private set; }
    public int Desired { get; private set; }
    private float remainder, elapsed;
    private bool initialized;
    public void Reset() { Target = 0; Desired = 0; remainder = 0; elapsed = 0; initialized = false; }
    public void SetTarget(float target)
    {
        Target = Mathf.Max(0, target);
        if (Target == 0) remainder = 0;
        if (!initialized) { initialized = true; Sample(); }
        // 減少時は次の時間窓を待たず上限を下げる。増加時も整数部分までは即座に追従する。
        Desired = Mathf.Clamp(Desired, Mathf.FloorToInt(Target), Mathf.CeilToInt(Target));
    }
    public void Advance(float dt)
    {
        elapsed += dt;
        while (elapsed >= 12) { elapsed -= 12; Sample(); }
    }
    public void Observe(int actual, float dt)
    {
        // 端数枠の到着・補充の遅れを台秒で繰り越す。0.2台を長時間描くときの上振れを防ぐ。
        if (Target - Mathf.Floor(Target) > .00001f)
            remainder += (Desired - actual) * dt / 12f;
    }
    private void Sample()
    {
        int whole = Mathf.FloorToInt(Target);
        remainder += Target - whole;
        int extra = Target > whole && remainder >= .99999f ? 1 : 0;
        remainder -= extra;
        Desired = whole + extra;
    }
}
