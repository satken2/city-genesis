using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Random = UnityEngine.Random;
using GeneratedModels;

/// <summary>
/// Rendering-only manager for continuously moving cars near the camera.
/// Regulate spawning and roadside arrivals with the traffic-derived total cap and per-road time-averaged density.
/// Count regulation is independent of node transitions, and arrivals never occur on intersections.
/// </summary>
public partial class VehicleManager : MonoBehaviour
{
    [SerializeField] private PathManager pathManager;
    [SerializeField] private CameraCellChecker cameraCellChecker;
    [Header("描画範囲")]
    [SerializeField] private float renderRadius = 400f;
    [SerializeField] private float despawnMargin = 80f;
    [SerializeField] private float maxCenterLead = 300f;   // Maximum horizontal lead of the management center from the camera; nonpositive means unlimited.
    [Header("密度")]
    [SerializeField] private float renderFraction = 1f;
    [SerializeField] private float activeHoursPerDay = LocalConstants.VEHICLE_ACTIVE_HOURS_PER_DAY;
    [SerializeField] private int maxActiveVehicles = 300;
    [SerializeField] private float rangeScanInterval = .5f;
    [SerializeField] private float spawnFillRate = 1f;
    [SerializeField] private float minimumJourneyTime = 6f;
    [Header("負荷")]
    [SerializeField] private float spawnBudgetMs = 1f;          // Replenishment time budget per frame; carry excess work to the next frame.
    [SerializeField] private float deactivationBudgetMs = .3f;  // Time budget for SetActive(false) on pooled bodies.
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
    private readonly Dictionary<int, Road> populatedRoads = new Dictionary<int, Road>();
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
    private bool seeding;
    private int seedRemaining;
    private float seedRemainder, nextSeedRemainder;
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
    private const float SpawnClearance = 8f;   // Do not spawn in lanes without this much clearance ahead/behind; overlapping spawns would stop following cars immediately.
    private const float StuckSeconds = 10f;    // Remove cars immobile for this long past their deadline, resolving mutual waits across intersections.
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
        public long geometryRevision = -1;   // PathManager.GetPathGeometryRevision; -1 means unknown, requiring point-list comparison.
        public Vector3 scanCenter;
        public float scanRadius;
        public int spawnPointGeneration = -1;   // accessGeneration when hasSpawnPoint was calculated.
        public bool hasSpawnPoint;
    }

    private sealed class Bucket
    {
        public Road road;
        public int type, count, pending;
        public float target, average;
        public TrafficPopulationSeed seed;
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
        AssetPackStore.CityChanging += ForgetModels;
    }

    private void OnDestroy() => AssetPackStore.CityChanging -= ForgetModels;

    /// <summary>Discard pooled bodies on city switches because they were instantiated from the city's asset-pack models.</summary>
    private void ForgetModels()
    {
        ClearVehicles();
        foreach (var go in pooled) if (go != null) Destroy(go);
        pooled.Clear();
        pool.Clear();
    }

    /// <summary>
    /// Reset worker and request states stopped by GameObject deactivation. Iterators disposed before their first step skip finally,
    /// and request coroutines stop without completion callbacks; without this reset, scans/fetches would never resume after re-enabling.
    /// </summary>
    private void OnDisable()
    {
        RenderWorkScheduler.StopAll(this);
        roadWorker = null; accessWorker = null; poolWarmStarted = false;
        motionGeneration++; motionInFlight = false;
    }

    private void Update()
    {
        // Deactivate all bodies returned to the pool through the previous frame, including on early-return paths.
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
            // A periodic resident worker, excluded from initial-load waiting. Ambient vehicles are decoration,
            // so the scan uses only budget left over by city object generation.
            if (roadWorker == null) roadWorker = RenderWorkScheduler.Start(this, ScanRoads(center, true), "vehicleRoads", () => false, RenderWorkPriority.Low);
            scanTimer = Mathf.Max(.1f, rangeScanInterval);
        }
        if (seeding) FillNewCoverage();
        float trafficDt = dt * DriveMode.SpeedMultiplier * TrafficClock.Scale;
        // Reevaluate following gaps in small increments even during fast-forward to avoid jumping past leading cars.
        for (float left = trafficDt; left > .00001f; left -= Mathf.Min(left, 1f / 30f))
            MoveVehicles(Mathf.Min(left, 1f / 30f), center);
        densityTimer += trafficDt;
        regulatedThisFrame = false;
        // Use a fixed control interval to avoid frame-dependent probability truncation or caps.
        while (densityTimer >= .25f)
        {
            densityTimer -= .25f;
            RegulateDensity(.25f);
        }
        if (spawnBacklog > 0 && !regulatedThisFrame && !TrafficClock.Paused) DrainSpawnBacklog();
    }

    /// <summary>
    /// Management center for moving/parked vehicles and pedestrians: ride position while driving, target position while following a citizen; otherwise clamp the view target (y=0 intersection)
    /// within horizontal maxLead of the camera. At shallow pitch, the intersection can lie kilometers away, managing invisible distant areas,
    /// and small pitch changes make it jump. Do not change the loading center in CameraCellChecker.
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
        roads.Clear(); populatedRoads.Clear(); buckets.Clear(); bucketByKey.Clear();
        population.Reset(); scanTimer = 0; densityTimer = 0; spawnBacklog = 0; seedRemaining = 0; seedRemainder = 0;
        RenderWorkScheduler.Stop(this, roadWorker); roadWorker = null;
        RenderWorkScheduler.Stop(this, accessWorker); accessWorker = null;
        entrances.Clear(); exitRanks.Clear(); accessCache.Clear(); seeding = false; accessReady = false; accessGeneration++;
    }

    /// <summary>
    /// Large center movement: retain cars still inside the circle and let ordinary despawning remove outside cars.
    /// Retain roads for moving-car branch decisions until the new scan commits, stopping only spawns in the old area.
    /// Entrance caches validate against parcel/geometry signatures, so do not discard them on center changes.
    /// </summary>
    private void Recenter()
    {
        RenderWorkScheduler.Stop(this, roadWorker); roadWorker = null;
        RenderWorkScheduler.Stop(this, accessWorker); accessWorker = null;
        buckets.Clear(); bucketByKey.Clear();
        spawnBacklog = 0; scanTimer = 0; motionFetchTime = 0;
        seeding = false;
    }

    /// <summary>Rebuild the road set synchronously. Starting a ride must resolve local roads before placing the car.</summary>
    private void RefreshRoads(Vector3 center)
    {
        RenderWorkScheduler.Stop(this, roadWorker); roadWorker = null;
        var work = ScanRoads(center, false);
        while (work.MoveNext()) { }
    }

    /// <summary>
    /// Build a nearby-road snapshot and swap it atomically in CommitRoads when complete.
    /// Budgeted work yields under the shared budget, so refetch current path data before checking roads that span a yield.
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
    /// One-road snapshot. Reuse the previous Road when geometry revision (or exact points if unknown), data, and center match;
    /// reuse points if only geometry matches. Point lookup is expensive before terrain loads, so skip it when revisions match.
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
            // TransitVehicleManager renders buses/LRT along their lines, so do not spawn them as ambient traffic.
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

        // Replace polylines after terrain following or road edits, completing active intersection curves before updating.
        foreach (var v in active)
        {
            if (v.InIntersection || v.ExitingRoad || !roads.TryGetValue(v.pathId, out var road)) continue;
            if (v.road != road.line) { v.s = road.line.RemapFrom(v.road, v.s); v.road = road.line; }
            SetLane(v, road, false);
        }
        // During paused camera movement, prevent outside-circle entities from consuming the new area's population cap.
        float radius = Mathf.Max(1, renderRadius + despawnMargin);
        for (int i = active.Count - 1; i >= 0; i--)
        {
            var v = active[i];
            if (v != DrivenVehicle && v != FollowedVehicle && Utils.XZDistanceSquared(v.go.transform.position, center) > radius * radius)
                Despawn(i);
        }
        PrepareSeeds();
    }

    private void PrepareSeeds()
    {
        foreach (var b in buckets)
        {
            var road = b.road;
            var intervals = populatedRoads.TryGetValue(road.data.path_id, out var old) && Traffic(old.data, b.type) > 0
                ? road.line.UncoveredIntervals(road.intervals, old.scanCenter, old.scanRadius) : road.intervals;
            b.seed = new TrafficPopulationSeed(intervals, b.target / road.insideLength);
            b.count = 0;
        }
        foreach (var v in active)
            if (bucketByKey.TryGetValue(Key(v.pathId, v.vehicleType), out var b))
            {
                b.count++;
                if (!v.InIntersection && !v.ExitingRoad) b.seed.Observe(v.s);
            }
        float expected = seedRemainder;
        foreach (var b in buckets) expected += b.seed.Weight;
        seedRemaining = Mathf.FloorToInt(expected + .00001f);
        nextSeedRemainder = Mathf.Max(0, expected - seedRemaining);
        spawnBacklog = 0;
        seeding = true;
    }

    /// <summary>
    /// Initially populate newly managed sections with already-moving traffic. Spread only body creation across the frame budget,
    /// without waiting for fill rate, traffic time, or entrance loading. Do not advance the managed area until complete.
    /// </summary>
    private void FillNewCoverage()
    {
        spawnWatch.Restart();
        for (int attempted = 0; seedRemaining > 0 && active.Count < maxActiveVehicles; attempted++)
        {
            if (attempted > 0 && spawnWatch.Elapsed.TotalMilliseconds >= spawnBudgetMs) return;
            var b = PickBucket(true);
            if (b == null) break;
            bool spawned = false;
            for (int retry = 0; retry < 4 && !spawned; retry++) spawned = Spawn(b);
            if (spawned) { seedRemaining--; b.count++; b.seed.Consume(); b.average = b.count; }
            else b.seed.Skip();
        }
        populatedRoads.Clear();
        foreach (var entry in roads) populatedRoads.Add(entry.Key, entry.Value);
        seedRemainder = nextSeedRemainder;
        seeding = false;
    }

    // Scan only nearby roads, recalculating entrances only when parcel, building, or centerline revisions change.
    // Separate expensive geometry and reverse searches from vehicle Update, slicing them under RenderWorkScheduler's shared budget.
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
                        // API version alone cannot detect terrain following or intersection cuts.
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
            // Reverse graph whose states are directed roads. Share steps-to-exit instead of searching per car.
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
                // Never reverse on motorways. Connect reverse directions only at true street dead ends.
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
            // Swap completed tables atomically; never expose partial tables to cars. Revalidate entrances immediately before exiting too.
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

    // Share entrance geometry with pedestrians; cars interpret path-relative side using traffic side and travel direction.
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
            // Even when total counts match, schedule roadside arrivals for cars concentrated on roads such as loops and redistribute to deficient roads.
            ReserveArrival(true);
        }

        if (seeding) return;
        int missing = population.Desired - active.Count;
        int fill = Mathf.Min(missing, Mathf.Max(1, Mathf.CeilToInt(missing * (1 - Mathf.Exp(-Mathf.Max(0, spawnFillRate) * dt)))));
        // Stop at the time budget to spread body activation/creation across frames. Always attempt the first car so filling cannot stall.
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

    /// <summary>On frames without a control tick, replenish work cut short by RegulateDensity's budget. Recalculated on the next control tick.</summary>
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

    private Bucket PickSpawnBucket() => PickBucket(false);

    private Bucket PickBucket(bool seed)
    {
        float sum = 0;
        foreach (var b in buckets) sum += seed ? b.seed.Weight : SpawnWeight(b);
        if (sum <= 0) return null;
        float pick = Random.value * sum;
        Bucket last = null;
        foreach (var b in buckets)
        {
            float weight = seed ? b.seed.Weight : SpawnWeight(b);
            if (weight <= 0) continue;
            last = b; pick -= weight;
            if (pick <= 0) return b;
        }
        return last;
    }

    private float SpawnWeight(Bucket b)
        => b.target <= 0 || !HasSpawnPointCached(b.road) ? 0
            : Mathf.Max(0, b.target - b.count) + Mathf.Max(0, b.target - b.average);

    // Every entrances replacement advances accessGeneration; intervals change only when creating Road, so generation comparisons invalidate them.
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
                // Correct distribution only on roads exceeding tolerance, avoiding car replacement for every small transient excess.
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
    /// Find the gap to the immediate leader on the same road, direction, and lane. Cars inside intersections also count as leaders of their previous lanes.
    /// Cars on the approaching road then queue behind a car stopped inside the intersection by downstream congestion.
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
    /// Spawn one car, trying lanes from a random choice onward. If a same-lane car lies within SpawnClearance ahead/behind, try another lane.
    /// If all lanes are blocked, return false before acquiring a body; the caller does not count that slot.
    /// </summary>
    private bool Spawn(Bucket b)
    {
        var road = b.road;
        bool forward = road.data.travel_direction == Constants.DIR_FORWARD ||
            (road.data.travel_direction == Constants.DIR_BOTH && Random.value < .5f);
        float s;
        if (seeding) s = b.seed.Sample();
        else
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
            go = go, spawnBlendRemaining = seeding ? 0 : 15, age = seeding ? fadeDuration : 0 };
        v.SetPath(road.data.path_id, road.line, forward, s);
        v.lane = lane;
        SetLane(v, road, false);
        SetMotion(v);
        active.Add(v);
        Place(v);
        return true;
    }

    /// <summary>Choose a lane. Heavy vehicles choose the shoulder-side lane with probability heavyVehicleOuterLaneBias.</summary>
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
        v.laneOffset = road.lanes.VehicleCenter(v.lane, CityInfoStore.TrafficHand, road.data.path_type);
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
                        // Retain arrival-blend progress when republishing the entrance table selects the same entrance again.
                        // When changing entrances, transfer the current blend to spawn merging to keep lateral position continuous.
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
                        // Do not remove cars immediately at dead ends without exits or use them to balance counts. If immobile past the deadline, remove as blocked cars.
                        v.age += remaining; remaining = 0; break;
                    }
                    continue;
                }
                break;
            }
            Place(v);
            // Treat cars that remain stationary past their deadline as mutually blocked across an intersection and remove them.
            // Do not count pre-deadline stop-line waiting, preserving long queues with sparse traffic and isolated waiting cars.
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
            // At itinerary section end, enter fading and continue the loop; wait for roads scanning has not yet reached.
            if (!NextFollowStep(v, out next, out nextForward)) return v.fading;
        }
        else if (candidates.Count == 0)
        {
            if (current.path_type != Constants.PATH_STREET || !DirectionAllowsEntry(current.travel_direction, !v.forward) || !roads.TryGetValue(v.pathId, out next)) return false;
            // Do not despawn arbitrarily at ends without exits; use permitted street reversals.

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
        // Carry deadlines across roads. Unused delay at joints waits at the next intersection; lateness shortens the next wait to catch up.
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
    /// Acquire a pooled body. If the requested appearance is unavailable, substitute another pooled appearance of the same type and update variant
    /// because Instantiate costs several ms per car. Create only if no appearances are available.
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
    /// Immediately hide returned bodies with zero scale; spread SetActive(false) across the budget in ProcessPendingDeactivation.
    /// Zero scale matches the age=0 spawn appearance, so the visual result is unchanged.
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
            // Do not touch bodies reacquired while pending deactivation; they are already moving again.
            if (go != null && pooled.Contains(go) && go.activeSelf) go.SetActive(false);
        }
    }

    /// <summary>
    /// Precreate one body for every ambient-traffic appearance so first-use Instantiate does not coincide with replenishment during driving.
    /// If interrupted, restart from the beginning on the next Update, skipping stocked appearances.
    /// </summary>
    private IEnumerator WarmPool()
    {
        bool completed = false;
        try
        {
            foreach (int type in Constants.ROAD_VEHICLE_KEYS)
            {
                if (type == Constants.VEHICLE_BUS || type == Constants.VEHICLE_TRAM) continue;
                var variants = VehicleModelResolver.GetVariants(type);
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
/// Sum expected counts across all roads before rounding, carrying fractions every 12 seconds
/// instead of inflating low traffic to one car per road. Splitting roads does not change the total cap.
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
        // Lower caps immediately on decreases without waiting for the next window; apply integer increases immediately as well.
        Desired = Mathf.Clamp(Desired, Mathf.FloorToInt(Target), Mathf.CeilToInt(Target));
    }
    public void Advance(float dt)
    {
        elapsed += dt;
        while (elapsed >= 12) { elapsed -= 12; Sample(); }
    }
    public void Observe(int actual, float dt)
    {
        // Carry fractional-slot arrival/replenishment delay in vehicle-seconds to prevent upward bias when displaying 0.2 cars for long periods.
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
