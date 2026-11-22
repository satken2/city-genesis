using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using GeneratedModels;

/// <summary>
/// Rendering-only manager moving pedestrians along sidewalks and shoulders near the camera.
/// Counts follow VehicleManager: use a total cap from pedestrian traffic and per-road time-averaged density
/// to regulate spawning and roadside arrivals. Do not remove at path ends.
/// No following, signals, or collision checks; pedestrians pass through each other and vehicles.
/// Entities have no GameObjects; each frame their placement and gait phase go to the Pedestrian shader, which moves the limbs.
/// </summary>
public sealed partial class PedestrianManager : MonoBehaviour
{
    [SerializeField] private PathManager pathManager;
    [SerializeField] private CameraCellChecker cameraCellChecker;
    [Header("描画範囲")]
    [SerializeField] private float renderRadius = 250f;
    [SerializeField] private float despawnMargin = 50f;
    [SerializeField] private float maxCenterLead = 180f;   // Maximum horizontal lead of the management center from the camera; nonpositive means unlimited.
    [Header("密度")]
    [SerializeField] private float renderFraction = 1f;   // Display multiplier relative to pedestrian traffic: 1=actual, 2/4=increased.
    [SerializeField] private int maxActivePedestrians = 1000;
    [SerializeField] private float rangeScanInterval = .5f;
    [SerializeField] private float spawnFillRate = 1f;
    [SerializeField] private float minimumJourneyTime = 6f;
    [Header("歩行")]
    [SerializeField] private float walkSpeedJitter = .25f;
    [SerializeField] private float bandMargin = .3f;
    [SerializeField] private float shoulderWidth = 1f;
    [SerializeField] private float connectorTension = .3f;
    [SerializeField] private float fadeDuration = .4f;
    [SerializeField] private float stepLength = .65f;   // Step length (m). One gait cycle of the shader is two steps.
    [SerializeField] private bool showDebugOverlay;

    /// <summary>Average walking speed (m/s): the city's PEDESTRIAN_SPEED, given in km/h.</summary>
    public static float WalkSpeed => MasterDataStore.Instance.GeneralParams.PEDESTRIAN_SPEED / 3.6f;

    private readonly List<Pedestrian> active = new List<Pedestrian>();
    private readonly Dictionary<int, Walk> walks = new Dictionary<int, Walk>();
    private readonly Dictionary<int, Walk> populatedWalks = new Dictionary<int, Walk>();
    private readonly List<Bucket> buckets = new List<Bucket>();
    private readonly Dictionary<int, Bucket> bucketByPath = new Dictionary<int, Bucket>();
    private readonly List<(Walk walk, bool forward, float weight)> candidates = new List<(Walk, bool, float)>();
    private readonly VehiclePopulationBudget population = new VehiclePopulationBudget();
    private ParcelManager parcelManager;
    private BuildingManager buildingManager;
    private Coroutine accessWorker, walkWorker;
    private bool seeding;
    private int seedRemaining;
    private float seedRemainder;
    private int accessSignature, accessGeneration;
    private bool accessReady, exitPossible;
    private Dictionary<int, List<Entrance>> entrances = new Dictionary<int, List<Entrance>>();
    private readonly Dictionary<int, AccessCache> accessCache = new Dictionary<int, AccessCache>();
    private float scanTimer, densityTimer;
    private Vector3 lastCenter;
    private bool hasCenter;
    private int densityTick;
    private readonly List<Model> models = new List<Model>();
    private bool modelsProbed;
    private Shader figureShader;
    private MaterialPropertyBlock figureProperties;
    private const int MaxModelVariants = 16;
    private static readonly int FiguresId = Shader.PropertyToID("_Pedestrians");
    private static readonly int StepLengthId = Shader.PropertyToID("_StepLength");
    private static readonly int PartLocalId = Shader.PropertyToID("_PartLocal");
    private static readonly int PartNormalId = Shader.PropertyToID("_PartNormal");

    private sealed class Walk
    {
        public PathSummary data;
        public VehiclePolyline line;
        public List<Vector2> intervals;
        public float insideLength, halfWidth;
        public float inner, outer, surface;   // Walking-band inner/outer offsets from the centerline and height.
        public bool centered;                 // Pedestrian-only roads without LRT tracks use one full-width band instead of separate sides.
        public long geometryRevision = -1;    // PathManager.GetPathGeometryRevision; -1 means unknown, requiring point-list comparison.
        public Vector3 scanCenter;
        public float scanRadius;
        public int spawnPointGeneration = -1; // accessGeneration when hasSpawnPoint was calculated.
        public bool hasSpawnPoint;
    }
    private sealed class Bucket
    {
        public Walk walk;
        public int count, pending;
        public float target, average;
        public TrafficPopulationSeed seed;
    }
    private sealed class Entrance
    {
        public int parcel, building, side;
        public float s;
        public Vector3 destination;
    }
    private sealed class AccessCache
    {
        public int signature;
        public List<Entrance> entries;
    }
    /// <summary>One figure as the Pedestrian shader reads it; the layout must match its PedestrianInstance.</summary>
    private struct Figure
    {
        public Vector4 positionScale;   // Foot position and uniform scale.
        public Vector4 headingGait;     // Heading x, heading z, gait phase in cycles, 1 while walking or 0 while standing.
    }
    private sealed class Model
    {
        public PropRenderModel source;   // Null for the box used without FBX assets.
        public readonly List<PropRenderModel.Part> parts = new List<PropRenderModel.Part>();   // Materials use the Pedestrian shader and are owned here.
        public readonly List<Figure> figures = new List<Figure>();
        public GraphicsBuffer buffer;

        public void Dispose()
        {
            foreach (var part in parts) UnityEngine.Object.Destroy(part.material);
            parts.Clear();
            source?.Dispose();
            buffer?.Dispose(); buffer = null;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindAnyObjectByType<PedestrianManager>() == null)
            new GameObject("PedestrianManager").AddComponent<PedestrianManager>();
    }

    private void Awake()
    {
        if (pathManager == null) pathManager = FindAnyObjectByType<PathManager>();
        if (cameraCellChecker == null) cameraCellChecker = CameraCellChecker.Instance ?? FindAnyObjectByType<CameraCellChecker>();
        parcelManager = FindAnyObjectByType<ParcelManager>();
        buildingManager = FindAnyObjectByType<BuildingManager>();
        AssetPackStore.CityChanging += ForgetModels;
    }

    private void Update()
    {
        if (pathManager == null) { pathManager = FindAnyObjectByType<PathManager>(); if (pathManager == null) return; }
        if (GameStateManager.Instance != null && !GameStateManager.Instance.IsGameStarted())
        {
            if (active.Count > 0 || walks.Count > 0 || walkWorker != null || accessWorker != null) ClearPedestrians();
            return;
        }
        float dt = Time.deltaTime;
        if (dt <= 0) return;
        Vector3 center = VehicleManager.ResolveTrafficCenter(cameraCellChecker, maxCenterLead);
        if (hasCenter && Utils.XZDistanceSquared(center, lastCenter) > renderRadius * renderRadius) Recenter();
        lastCenter = center; hasCenter = true;
        scanTimer -= dt;
        if (scanTimer <= 0)
        {
            // A periodic resident worker, excluded from initial-load waiting. Ambient pedestrians are decoration,
            // so the scan uses only budget left over by city object generation.
            if (walkWorker == null) walkWorker = RenderWorkScheduler.Start(this, ScanWalks(center), "pedestrianWalks", () => false, RenderWorkPriority.Low);
            scanTimer = Mathf.Max(.1f, rangeScanInterval);
        }
        if (TrafficClock.Paused) return;
        MovePedestrians(dt, center);
        UpdateTransit(dt);
        densityTimer += dt;
        // Use a fixed control interval to avoid frame-dependent probability truncation or caps.
        while (densityTimer >= .25f)
        {
            densityTimer -= .25f;
            RegulateDensity(.25f);
        }
    }

    // An iterator disposed before its first step skips finally, so clear its handle here to allow scans after re-enabling.
    private void OnDisable()
    {
        RenderWorkScheduler.StopAll(this);
        walkWorker = null; accessWorker = null;
    }

    private void OnDestroy()
    {
        AssetPackStore.CityChanging -= ForgetModels;
        foreach (var model in models) model.Dispose();
        models.Clear();
    }

    /// <summary>Reload models on city switches because the city's asset pack may differ.</summary>
    private void ForgetModels()
    {
        ClearPedestrians();
        foreach (var model in models) model.Dispose();
        models.Clear();
        modelsProbed = false;
    }

    private void ClearPedestrians()
    {
        if (FollowedPedestrian != null) EndFollowRun();
        active.Clear();
        walks.Clear(); populatedWalks.Clear(); buckets.Clear(); bucketByPath.Clear();
        population.Reset(); scanTimer = 0; densityTimer = 0; seedRemaining = 0; seedRemainder = 0;
        RenderWorkScheduler.Stop(this, walkWorker); walkWorker = null;
        RenderWorkScheduler.Stop(this, accessWorker); accessWorker = null;
        entrances.Clear(); accessCache.Clear(); seeding = false; accessReady = false; accessGeneration++;
        ClearTransit();
    }

    /// <summary>
    /// Large movement of the management center: keep pedestrians inside the circle walking and let normal despawning handle those outside.
    /// Retain walks for branching until the new scan commits, stopping only spawns in the old area. Retain entrance caches, which use signatures.
    /// </summary>
    private void Recenter()
    {
        RenderWorkScheduler.Stop(this, walkWorker); walkWorker = null;
        RenderWorkScheduler.Stop(this, accessWorker); accessWorker = null;
        buckets.Clear(); bucketByPath.Clear();
        scanTimer = 0;
        seeding = false;
    }

    /// <summary>
    /// Derive walking bands from road dimensions: sidewalk top if present, otherwise the shoulder-width strip at the road edge,
    /// or the full width on pedestrian-only roads. LRT pedestrian roads (303) have two bands outside the double-track vehicle envelope.
    /// Height is sidewalk top or road surface, following RoadPartHeights conventions.
    /// </summary>
    public static void ResolveBand(int pathType, int pathVariant, float width, float sidewalk, float margin, float shoulder,
        out float inner, out float outer, out float surface, out bool centered)
    {
        bool walkway = pathType == Constants.PATH_WALKWAY;
        centered = walkway && !LrtRoadParts.IsEmbedded(pathVariant);
        if (walkway)
        {
            inner = centered ? 0 : LrtRoadParts.MALL_VEHICLE_ENVELOPE + margin;
            outer = Mathf.Max(inner, width * .5f - margin);
            surface = RoadPartHeights.BODY_ABOVE_SPLINE;
        }
        else if (sidewalk > 0)
        {
            inner = Mathf.Max(0, width * .5f - sidewalk + margin);
            outer = Mathf.Max(inner, width * .5f - margin);
            surface = RoadPartHeights.SIDEWALK_ABOVE_SPLINE + LocalConstants.PEDESTRIAN_SIDEWALK_SURFACE;
        }
        else
        {
            inner = Mathf.Max(0, width * .5f - shoulder);
            outer = Mathf.Max(inner, width * .5f - margin);
            surface = RoadPartHeights.BODY_ABOVE_SPLINE;
        }
    }

    /// <summary>
    /// Build nearby walking-band snapshots in shared-budget slices, then swap them together in CommitWalks when complete.
    /// For roads spanning a yield, refetch current path data before evaluating them.
    /// </summary>
    private IEnumerator ScanWalks(Vector3 center)
    {
        try
        {
            if (pathManager == null) yield break;
            if (parcelManager == null) parcelManager = FindAnyObjectByType<ParcelManager>();
            if (buildingManager == null) buildingManager = FindAnyObjectByType<BuildingManager>();
            var next = new Dictionary<int, Walk>();
            float radius = Mathf.Max(1, renderRadius + despawnMargin);
            var nearby = pathManager.GetPathsNearCircle(center, radius);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            foreach (var candidate in nearby)
            {
                if (pathManager == null) yield break;
                var data = pathManager.GetPathData(candidate.path_id);
                if (Walkable(data))
                {
                    var walk = BuildWalk(data, center, radius);
                    if (walk != null) next[data.path_id] = walk;
                }
                if (timer.Elapsed.TotalMilliseconds >= PerformanceConfig.SliceBudgetMs)
                {
                    yield return RenderWorkScheduler.Continue;
                    timer.Restart();
                }
            }
            CommitWalks(next, center);
        }
        finally
        {
            walkWorker = null;
        }
    }

    /// <summary>
    /// Walking band for one road. Reuse the previous Walk when geometry revision (or exact points if unknown), data, and center match;
    /// reuse points if only geometry matches. Point lookup is expensive before terrain loads, so skip it when revisions match.
    /// </summary>
    private Walk BuildWalk(PathSummary data, Vector3 center, float radius)
    {
        int id = data.path_id;
        walks.TryGetValue(id, out var prev);
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
        var walk = new Walk { data = data, line = line, intervals = intervals, insideLength = length,
            geometryRevision = revision, scanCenter = center, scanRadius = radius };
        if (prev != null && prev.data == data)
        {
            walk.halfWidth = prev.halfWidth; walk.inner = prev.inner; walk.outer = prev.outer;
            walk.surface = prev.surface; walk.centered = prev.centered;
        }
        else
        {
            RoadPath.GetPathMasterParams(data, out float width, out float sidewalk, out _, out _, out _, out _, out _, out _, out _, out _, out _);
            walk.halfWidth = width * .5f;
            ResolveBand(data.path_type, data.path_variant, width, sidewalk, bandMargin, shoulderWidth, out walk.inner, out walk.outer, out walk.surface, out walk.centered);
            walk.inner = Mathf.Min(walk.outer, walk.inner + RoadEquipmentGenerator.SidewalkHedgeWidth(data, sidewalk));
        }
        return walk;
    }

    private void CommitWalks(Dictionary<int, Walk> next, Vector3 center)
    {
        walks.Clear();
        exitPossible = false;
        foreach (var entry in next)
        {
            walks.Add(entry.Key, entry.Value);
            foreach (var interval in entry.Value.intervals)
                if (interval.x > .1f || interval.y < entry.Value.line.Length - .1f) exitPossible = true;
        }
        if (accessWorker == null) accessWorker = RenderWorkScheduler.Start(this, UpdateAccess(center), "pedestrianAccess", () => false);
        var previous = new Dictionary<int, Bucket>(bucketByPath);
        buckets.Clear(); bucketByPath.Clear();
        float total = 0;
        // Expected people = daily traffic / active seconds x length within circle / walking speed x display multiplier. Do not round up per road.
        foreach (var walk in walks.Values)
        {
            float target = Traffic(walk.data) / Mathf.Max(1, CityDataCache.ActiveHoursPerDay * 3600) * walk.insideLength
                / Mathf.Max(.1f, WalkSpeed) * Mathf.Max(0, renderFraction);
            var bucket = new Bucket { walk = walk, target = target,
                average = previous.TryGetValue(walk.data.path_id, out var old) ? old.average : 0 };
            buckets.Add(bucket); bucketByPath.Add(walk.data.path_id, bucket); total += target;
        }
        float scale = total > maxActivePedestrians ? Mathf.Max(0, maxActivePedestrians) / total : 1;
        foreach (var bucket in buckets) bucket.target *= scale;
        population.SetTarget(Mathf.Min(total, Mathf.Max(0, maxActivePedestrians)));
        displayScale = Mathf.Max(0, renderFraction) * scale;

        // Replace polylines after terrain following or road edits. Complete connector polylines before adopting the update.
        foreach (var p in active)
        {
            if (p.InConnector || !walks.TryGetValue(p.pathId, out var walk)) continue;
            if (p.road != walk.line) { p.s = walk.line.RemapFrom(p.road, p.s); p.road = walk.line; }
            PlaceOnBand(p, walk, false);
        }
        // Even while paused, remove entities outside the circle to free the population cap for the new area.
        float radius = Mathf.Max(1, renderRadius + despawnMargin);
        for (int i = active.Count - 1; i >= 0; i--)
        {
            var p = active[i];
            p.Sample(out var position, out _);
            if (p != FollowedPedestrian && Utils.XZDistanceSquared(position, center) > radius * radius) Despawn(i);
        }
        PrepareSeeds();
        FillNewCoverage();
        ScanTransit(center);
    }

    private void PrepareSeeds()
    {
        foreach (var b in buckets)
        {
            var walk = b.walk;
            var intervals = populatedWalks.TryGetValue(walk.data.path_id, out var old) && Traffic(old.data) > 0
                ? walk.line.UncoveredIntervals(walk.intervals, old.scanCenter, old.scanRadius) : walk.intervals;
            b.seed = new TrafficPopulationSeed(intervals, b.target / walk.insideLength);
            b.count = 0;
        }
        foreach (var p in active)
            if (bucketByPath.TryGetValue(p.pathId, out var b))
            {
                b.count++;
                if (!p.InConnector) b.seed.Observe(p.s);
            }
        float expected = seedRemainder;
        foreach (var b in buckets) expected += b.seed.Weight;
        seedRemaining = Mathf.FloorToInt(expected + .00001f);
        seedRemainder = Mathf.Max(0, expected - seedRemaining);
        seeding = true;
    }

    // Pedestrians have no rendering GameObjects, so fill new-section densities when the scan commits.
    private void FillNewCoverage()
    {
        while (seedRemaining > 0 && active.Count < maxActivePedestrians)
        {
            var b = PickBucket(true);
            if (b == null) break;
            Spawn(b);
            seedRemaining--; b.count++; b.seed.Consume(); b.average = b.count;
        }
        populatedWalks.Clear();
        foreach (var entry in walks) populatedWalks.Add(entry.Key, entry.Value);
        seeding = false;
    }

    // Scan only nearby roads, recalculating entrances only when parcel, building, or centerline revisions change.
    // Unlike cars, pedestrians have no reverse exit lookup table; they use traffic-weighted random walks
    // and, when scheduled to arrive, enter the first entrance ahead. Split work within the shared budget.
    private IEnumerator UpdateAccess(Vector3 center)
    {
        try
        {
            var snapshot = new Dictionary<int, Walk>(walks);
            var usedParcels = new HashSet<int>();
            var next = new Dictionary<int, List<Entrance>>();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            int overall = 0;
            foreach (var walk in snapshot.Values)
            {
                var entries = new List<Entrance>();
                int shapeHash = unchecked(walk.data.path_id * 486187739 + walk.line.ShapeHash);
                shapeHash = unchecked(shapeHash * 31 + walk.halfWidth.GetHashCode());
                overall ^= shapeHash;
                bool allowed = RoadsideAccess.FrontageAllowed(walk.data, true);
                if (allowed) overall ^= walk.data.path_id * 79;
                if (allowed && parcelManager != null && buildingManager != null)
                {
                    foreach (int id in new List<int>(parcelManager.GetParcelIdsByFrontPath(walk.data.path_id)))
                    {
                        usedParcels.Add(id);
                        var parcel = parcelManager.GetParcelSummary(id);
                        if (parcel == null) continue;
                        int signature = parcel.version ^ shapeHash;
                        int building = RoadsideAccess.ActiveBuilding(buildingManager, id, ref signature);
                        overall ^= signature;
                        if (!accessCache.TryGetValue(id, out var cached) || cached.signature != signature)
                        {
                            cached = new AccessCache { signature = signature, entries = new List<Entrance>() };
                            if (building != 0) BuildEntrances(walk, parcel, building, cached.entries);
                            accessCache[id] = cached;
                        }
                        foreach (var entry in cached.entries)
                            foreach (var interval in walk.intervals)
                                if (entry.s >= interval.x && entry.s <= interval.y)
                                { entries.Add(entry); overall ^= entry.parcel * 101; break; }
                        if (timer.Elapsed.TotalMilliseconds >= PerformanceConfig.SliceBudgetMs) { yield return RenderWorkScheduler.Continue; timer.Restart(); }
                    }
                }
                next[walk.data.path_id] = entries;
                if (timer.Elapsed.TotalMilliseconds >= PerformanceConfig.SliceBudgetMs) { yield return RenderWorkScheduler.Continue; timer.Restart(); }
            }
            if (accessReady && overall == accessSignature) yield break;
            entrances = next; accessSignature = overall; accessReady = true; accessGeneration++;
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

    private void BuildEntrances(Walk walk, ParcelSummary parcel, int building, List<Entrance> result)
    {
        var found = new List<RoadsideAccess.Candidate>();
        RoadsideAccess.BuildCandidates(walk.line, walk.halfWidth, parcel, building, parcelManager, found);
        foreach (var c in found)
            result.Add(new Entrance { parcel = c.parcel, building = c.building, s = c.s, side = c.side, destination = c.destination });
    }

    private bool ValidEntrance(Entrance entry, int path)
    {
        var data = pathManager.GetPathData(path);
        return Walkable(data) && RoadsideAccess.FrontageAllowed(data, true)
            && RoadsideAccess.EntranceValid(parcelManager, buildingManager, entry.parcel, entry.building, path, entry.destination);
    }

    private void RegulateDensity(float dt)
    {
        population.Observe(active.Count, dt);
        population.Advance(dt);
        foreach (var b in buckets) { b.count = 0; b.pending = 0; }
        int pending = 0;
        foreach (var p in active)
        {
            if (p.arrivalPending || p.fading) pending++;
            if (!bucketByPath.TryGetValue(p.pathId, out var b)) continue;
            b.count++;
            if (p.arrivalPending || p.fading) b.pending++;
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
            // Even when total counts match, make pedestrians on overpopulated roads arrive and redistribute to deficient roads.
            ReserveArrival(true);
        }
        int missing = population.Desired - active.Count;
        int fill = Mathf.Min(missing, Mathf.Max(1, Mathf.CeilToInt(missing * (1 - Mathf.Exp(-Mathf.Max(0, spawnFillRate) * dt)))));
        for (int i = 0; i < fill; i++)
        {
            if (active.Count >= maxActivePedestrians) break;
            Bucket chosen = PickSpawnBucket();
            if (chosen == null) break;
            Spawn(chosen);
            chosen.count++;
        }
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
        => b.target <= 0 || !HasSpawnPointCached(b.walk) ? 0
            : Mathf.Max(0, b.target - b.count) + Mathf.Max(0, b.target - b.average);

    // Every entrances replacement advances accessGeneration; intervals change only when creating Walk, so generation comparisons invalidate them.
    private bool HasSpawnPointCached(Walk walk)
    {
        if (walk.spawnPointGeneration != accessGeneration)
        {
            walk.hasSpawnPoint = HasSpawnPoint(walk);
            walk.spawnPointGeneration = accessGeneration;
        }
        return walk.hasSpawnPoint;
    }

    private bool HasSpawnPoint(Walk walk)
    {
        if (entrances.TryGetValue(walk.data.path_id, out var list) && list.Count > 0) return true;
        foreach (var interval in walk.intervals)
            if (interval.x > .1f || interval.y < walk.line.Length - .1f) return true;
        return false;
    }

    private bool ReserveArrival(bool redistribute)
    {
        // Closed networks without entrances or exits beyond the management circle have no arrival destinations; retain pedestrians and stop replenishment.
        bool anyEntrance = exitPossible;
        if (!anyEntrance) foreach (var list in entrances.Values) if (list.Count > 0) { anyEntrance = true; break; }
        if (!anyEntrance) return false;
        Pedestrian selected = null;
        float best = float.NegativeInfinity;
        foreach (var p in active)
        {
            if (p == FollowedPedestrian || p.arrivalPending || p.fading || p.Entering || p.age < minimumJourneyTime) continue;
            float surplus = 0;
            if (bucketByPath.TryGetValue(p.pathId, out var b))
            {
                surplus = b.average - b.target - b.pending;
                if (redistribute && surplus < Mathf.Max(.5f, b.target * .25f)) continue;
            }
            else if (redistribute) continue;
            float score = surplus + Random.value * .1f;
            if (score > best) { selected = p; best = score; }
        }
        if (selected == null) return false;
        selected.arrivalPending = true; selected.searchedPath = -1;
        if (bucketByPath.TryGetValue(selected.pathId, out var chosen)) chosen.pending++;
        return true;
    }

    private void Spawn(Bucket b)
    {
        var walk = b.walk;
        bool forward = Random.value < .5f;
        float s;
        Entrance origin = null;
        if (seeding) s = b.seed.Sample();
        else
        {
            // After initial placement, replenish only at building entrances or incoming management-circle boundaries, never midway along roads.
            var options = new List<(float s, bool forward, Entrance entry)>();
            if (entrances.TryGetValue(walk.data.path_id, out var list))
                foreach (var entry in list)
                    if (ValidEntrance(entry, walk.data.path_id)) options.Add((entry.s, Random.value < .5f, entry));
            foreach (var interval in walk.intervals)
            {
                if (interval.x > .1f) options.Add((interval.x + Mathf.Min(5, (interval.y - interval.x) * .25f), true, null));
                if (interval.y < walk.line.Length - .1f) options.Add((interval.y - Mathf.Min(5, (interval.y - interval.x) * .25f), false, null));
            }
            if (options.Count == 0) return;
            var option = options[Random.Range(0, options.Count)]; s = option.s; forward = option.forward; origin = option.entry;
        }
        var p = new Pedestrian
        {
            variant = Random.Range(0, MaxModelVariants),
            speed = Mathf.Max(.1f, WalkSpeed * (1 + Random.Range(-walkSpeedJitter, walkSpeedJitter))),
            walked = Random.value * stepLength * 2,
            age = seeding ? fadeDuration : 0,
            moving = seeding,
        };
        p.SetPath(walk.data.path_id, walk.line, forward, s);
        PlaceOnBand(p, walk, true);
        if (origin != null) Leave(p, walk, origin.side, origin.destination);
        active.Add(p);
    }

    /// <summary>Place on sidewalk side relative to path geometry, walking out from origin: site, station building, or vehicle door.</summary>
    private void Leave(Pedestrian p, Walk walk, int side, Vector3 origin)
    {
        if (!walk.centered) { p.side = side; p.lateral = side * Random.Range(walk.inner, walk.outer); }
        p.BeginLeaving(origin);
    }

    /// <summary>Choose lateral position within the walking band. choose=false repositions after road updates, retaining side and offset.</summary>
    private void PlaceOnBand(Pedestrian p, Walk walk, bool choose)
    {
        p.surface = walk.surface;
        if (walk.centered)
        {
            p.side = 0;
            if (choose) p.lateral = Random.Range(-walk.outer, walk.outer);
            else p.lateral = Mathf.Clamp(p.lateral, -walk.outer, walk.outer);
            return;
        }
        if (choose || p.side == 0) { p.side = Random.value < .5f ? -1 : 1; p.lateral = p.side * Random.Range(walk.inner, walk.outer); }
        else p.lateral = p.side * Mathf.Clamp(Mathf.Abs(p.lateral), walk.inner, walk.outer);
    }

    private void MovePedestrians(float dt, Vector3 center)
    {
        float radius = Mathf.Max(1, renderRadius + despawnMargin);
        for (int i = active.Count - 1; i >= 0; i--)
        {
            var p = active[i];
            var data = pathManager.GetPathData(p.pathId);
            if (!Walkable(data)) { Despawn(i); continue; }
            float remaining = dt;
            bool invalid = false;
            int transitions = 0;
            float walkedBefore = p.walked;
            int pathBefore = p.pathId;
            float sBefore = p.s;
            while (remaining > .00001f)
            {
                if (p.fading) { p.fadeElapsed += remaining; remaining = 0; break; }
                if (!p.InConnector && p.arrivalPending && (p.accessGeneration != accessGeneration || p.searchedPath != p.pathId))
                {
                    p.accessGeneration = accessGeneration; p.searchedPath = p.pathId;
                    // Select the nearest entrance ahead, preferring the same sidewalk side; if none exists,
                    // cross the road to the opposite entrance, without crosswalk or signal modeling.
                    Entrance selected = null; bool selectedSameSide = false;
                    if (entrances.TryGetValue(p.pathId, out var entries))
                        foreach (var entry in entries)
                        {
                            float distance = (entry.s - p.s) * (p.forward ? 1 : -1);
                            if (distance < -.001f || !ValidEntrance(entry, p.pathId)) continue;
                            bool sameSide = p.side == 0 || entry.side == p.side;
                            if (selected != null && selectedSameSide && !sameSide) continue;
                            if (selected == null || (sameSide && !selectedSameSide) || distance < (selected.s - p.s) * (p.forward ? 1 : -1))
                            { selected = entry; selectedSameSide = sameSide; }
                        }
                    if (selected != null)
                    {
                        p.arrivalDistance = Mathf.Max(0, (selected.s - p.s) * (p.forward ? 1 : -1));
                        p.destinationParcel = selected.parcel; p.destinationBuilding = selected.building; p.destinationPoint = selected.destination;
                    }
                    else { p.arrivalDistance = -1; p.destinationParcel = 0; }
                }
                remaining = p.Advance(remaining);
                if (!p.InConnector && p.arrivalDistance >= 0 && p.arrivalDistance < .001f)
                {
                    var entry = new Entrance { parcel = p.destinationParcel, building = p.destinationBuilding, destination = p.destinationPoint };
                    if (p == FollowedPedestrian || ValidEntrance(entry, p.pathId)) p.BeginEntering(p.destinationPoint);
                    else { p.arrivalDistance = -1; p.destinationParcel = 0; }
                    continue;
                }
                if (p.ConnectorComplete)
                {
                    if (p.Entering) { p.fading = true; continue; }
                    p.EndConnector();
                    continue;
                }
                if (!p.InConnector && p.Remaining < .001f)
                {
                    if (++transitions > 128) { invalid = true; break; }
                    if (!Transition(p)) { p.age += remaining; remaining = 0; break; }
                    continue;
                }
                break;
            }
            p.moving = p.walked > walkedBefore;
            if (invalid || p.fadeElapsed >= Mathf.Max(.01f, fadeDuration)) { Despawn(i); continue; }
            if (PassGate(p, pathBefore, sBefore)) { active.RemoveAt(i); continue; }
            if (!p.Entering && p != FollowedPedestrian)
            {
                p.Sample(out Vector3 position, out _);
                if (Utils.XZDistanceSquared(position, center) > radius * radius) Despawn(i);
            }
        }
    }

    /// <summary>
    /// Transfer a pedestrian reaching a path end to a connected road, preserving the physical sidewalk side and crossing diagonally if needed.
    /// No signal/crosswalk modeling. Turn back in place if there are no candidates.
    /// </summary>
    private bool Transition(Pedestrian p)
    {
        PathSummary current = pathManager.GetPathData(p.pathId);
        if (current == null) return false;
        if (p == FollowedPedestrian) return FollowTransition(p);
        int node = p.forward ? current.end_node_id : current.start_node_id;
        candidates.Clear();
        float total = 0;
        foreach (var data in pathManager.GetPathsConnectedToNode(node))
        {
            if (data.path_id == p.pathId || !Walkable(data) || !walks.TryGetValue(data.path_id, out var walk)) continue;
            bool forward = data.start_node_id == node;
            if (!forward && data.end_node_id != node) continue;
            float weight = Traffic(data);
            candidates.Add((walk, forward, weight)); total += weight;
        }
        if (candidates.Count == 0)
        {
            // Turn back at dead ends and rerun entrance lookup because the direction changed.
            p.forward = !p.forward; p.searchedPath = -1; p.arrivalDistance = -1;
            return true;
        }
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
        MoveToWalk(p, candidates[chosen].walk, candidates[chosen].forward);
        return true;
    }

    /// <summary>Transfer to the next road's walking band, preserving travel-relative side in the new path frame and crossing the intersection along a connector polyline.</summary>
    private void MoveToWalk(Pedestrian p, Walk next, bool nextForward)
    {
        if (next.data.path_id != p.pathId) { p.previousPath2 = p.previousPath; p.previousPath = p.pathId; }
        p.Sample(out Vector3 p0, out Vector3 t0);
        // Preserve travel-relative side (+1=right) when mapping to the next path's geometric frame.
        int travelSide = p.side == 0 ? (Random.value < .5f ? -1 : 1) : p.side * (p.forward ? 1 : -1);
        p.SetPath(next.data.path_id, next.line, nextForward);
        p.arrivalDistance = -1; p.destinationParcel = 0;
        p.surface = next.surface;
        if (next.centered) { p.side = 0; p.lateral = Random.Range(-next.outer, next.outer); }
        else { p.side = travelSide * (nextForward ? 1 : -1); p.lateral = p.side * Random.Range(next.inner, next.outer); }
        p.Sample(out Vector3 p1, out Vector3 t1);
        p.connector = VehiclePolyline.Connector(p0, t0, p1, t1, connectorTension);
        p.connectorS = 0;
    }

    private void Despawn(int index)
    {
        if (active[index] == FollowedPedestrian) EndFollowRun();
        active.RemoveAt(index);
    }

    private void LateUpdate()
    {
        if (active.Count == 0 && transitStops.Count == 0) return;
        EnsureModels();
        if (models.Count == 0) return;
        foreach (var model in models) model.figures.Clear();
        float fadeTime = Mathf.Max(.01f, fadeDuration);
        foreach (var p in active)
        {
            p.Sample(out Vector3 position, out Vector3 tangent);
            AddFigure(position, tangent, p.variant, p.walked, p.moving, Mathf.Clamp01(p.fading ? 1 - p.fadeElapsed / fadeTime : p.age / fadeTime));
        }
        AddTransitFigures();
        float radius = renderRadius + despawnMargin;
        var bounds = new Bounds(lastCenter, new Vector3(radius * 2, 2000, radius * 2));
        figureProperties ??= new MaterialPropertyBlock();
        figureProperties.SetFloat(StepLengthId, stepLength);
        foreach (var model in models)
        {
            int count = model.figures.Count;
            if (count == 0) continue;
            if (model.buffer == null || model.buffer.count < count)
            {
                model.buffer?.Dispose();
                model.buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.NextPowerOfTwo(Mathf.Max(64, count)), 32);
            }
            model.buffer.SetData(model.figures);
            figureProperties.SetBuffer(FiguresId, model.buffer);
            foreach (var part in model.parts)
            {
                figureProperties.SetMatrix(PartLocalId, part.local);
                figureProperties.SetMatrix(PartNormalId, part.local.inverse.transpose);
                var rp = new RenderParams(part.material) { worldBounds = bounds, matProps = figureProperties,
                    shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
                Graphics.RenderMeshPrimitives(rp, part.mesh, part.submesh, count);
            }
        }
    }

    /// <summary>Queue one pedestrian for this frame. The gait phase follows distance walked, so faster walkers step faster; a stopped figure stands upright.</summary>
    private void AddFigure(Vector3 position, Vector3 heading, int variant, float walked, bool moving, float scale)
    {
        heading.y = 0;
        heading = heading.sqrMagnitude < .0001f ? Vector3.forward : heading.normalized;
        float phase = stepLength > 0 ? Mathf.Repeat(walked / (stepLength * 2), 1) : 0;
        models[variant % models.Count].figures.Add(new Figure { positionScale = new Vector4(position.x, position.y, position.z, scale),
            headingGait = new Vector4(heading.x, heading.z, phase, moving ? 1 : 0) });
    }

    /// <summary>
    /// Load Models/Props/pedestrian_{n} from 0 upward; if no models exist, use humanoid boxes so rendering works without FBX assets.
    /// Defer model checks on frames before ModelManager exists.
    /// The Pedestrian shader moves the limbs from joint data in the models' UV channels; a model without it glides in its authored pose.
    /// </summary>
    private void EnsureModels()
    {
        if (modelsProbed || ModelManager.Instance == null) return;
        modelsProbed = true;
        if (figureShader == null) figureShader = Resources.Load<Shader>("Shaders/Pedestrian");
        for (int i = 0; i < MaxModelVariants; i++)
        {
            string name = $"pedestrian_{i}";
            if (ModelManager.Instance.GetModel($"Models/Props/{name}", true) == null) break;
            var source = new PropRenderModel(name);
            if (source.normal.Count == 0) { source.Dispose(); break; }
            var model = new Model { source = source };
            foreach (var part in source.normal)
            {
                var color = part.material.HasProperty("_BaseColor") ? part.material.GetColor("_BaseColor") : part.material.color;
                model.parts.Add(new PropRenderModel.Part { mesh = part.mesh, submesh = part.submesh, local = part.local,
                    material = FigureMaterial(part.material.name, color,
                        part.material.HasProperty("_Smoothness") ? part.material.GetFloat("_Smoothness") : .15f,
                        part.material.HasProperty("_Metallic") ? part.material.GetFloat("_Metallic") : 0f) });
            }
            models.Add(model);
        }
        if (models.Count > 0) return;
        var box = new Model();
        box.parts.Add(new PropRenderModel.Part { mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx"), submesh = 0,
            material = FigureMaterial("PedestrianBoxMaterial", new Color(.85f, .75f, .6f), .15f, 0f),
            local = Matrix4x4.TRS(new Vector3(0, .85f, 0), Quaternion.identity, new Vector3(.4f, 1.7f, .3f)) });
        models.Add(box);
    }

    private Material FigureMaterial(string name, Color color, float smoothness, float metallic)
    {
        var material = new Material(figureShader) { name = name };
        material.SetColor("_BaseColor", color);
        material.SetFloat("_Smoothness", smoothness);
        material.SetFloat("_Metallic", metallic);
        return material;
    }

    private static bool Walkable(PathSummary p) => p != null && p.status == Constants.STATUS_ACTIVE &&
        (p.path_type == Constants.PATH_STREET || p.path_type == Constants.PATH_WALKWAY);
    private static float Traffic(PathSummary p)
        => p.path_traffic != null && p.path_traffic.TryGetValue(Constants.VEHICLE_PEDESTRIAN.ToString(), out float value)
            && !float.IsNaN(value) && !float.IsInfinity(value) ? Mathf.Max(0, value) : 0;

    private void OnGUI()
    {
        if (showDebugOverlay) GUI.Label(new Rect(10, 30, 600, 20), $"Pedestrians: {active.Count} / {population.Desired} (mean {population.Target:F2}) walks: {walks.Count}");
    }
}
