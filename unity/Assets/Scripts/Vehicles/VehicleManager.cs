using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// カメラ周辺の車を連続走行させる描画専用マネージャー。
/// 交通量から決まる全体枠と道路別の時間平均密度で発生・沿道到着を調整する。
/// 台数調整はノード遷移から独立しており、交差点の上では到着させない。
/// </summary>
public class VehicleManager : MonoBehaviour
{
    [SerializeField] private PathManager pathManager;
    [SerializeField] private CameraCellChecker cameraCellChecker;
    [Header("描画範囲")]
    [SerializeField] private float renderRadius = 400f;
    [SerializeField] private float despawnMargin = 80f;
    [Header("密度")]
    [SerializeField] private float renderFraction = 1f;
    [SerializeField] private float activeHoursPerDay = LocalConstants.VEHICLE_ACTIVE_HOURS_PER_DAY;
    [SerializeField] private int maxActiveVehicles = 300;
    [SerializeField] private float rangeScanInterval = .5f;
    [SerializeField] private float spawnFillRate = 1f;
    [SerializeField] private float minimumJourneyTime = 6f;
    [Header("走行")]
    [SerializeField] private float baseSpeed = 12f;
    [SerializeField] private float intersectionCurveTension = .4f;
    [SerializeField] private float heavyVehicleOuterLaneBias = .7f;
    [SerializeField] private float fadeDuration = .4f;
    [SerializeField] private bool showDebugOverlay;

    private readonly List<Vehicle> active = new List<Vehicle>();
    private readonly Dictionary<string, Queue<GameObject>> pool = new Dictionary<string, Queue<GameObject>>();
    private readonly Dictionary<int, Road> roads = new Dictionary<int, Road>();
    private readonly List<Bucket> buckets = new List<Bucket>();
    private readonly Dictionary<long, Bucket> bucketByKey = new Dictionary<long, Bucket>();
    private readonly List<(Road road, bool forward, float weight)> candidates = new List<(Road, bool, float)>();
    private readonly VehiclePopulationBudget population = new VehiclePopulationBudget();
    private ParcelManager parcelManager;
    private BuildingManager buildingManager;
    private Coroutine accessWorker;
    private bool initialFill = true;
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

    private void Update()
    {
        if (pathManager == null) return;
        if (GameStateManager.Instance != null && !GameStateManager.Instance.IsGameStarted())
        {
            if (active.Count > 0 || roads.Count > 0) ClearVehicles();
            return;
        }
        float dt = Time.deltaTime;
        if (dt <= 0) return;
        Vector3 center = cameraCellChecker != null ? cameraCellChecker.CurrentWorldPosition : (Camera.main != null ? Camera.main.transform.position : Vector3.zero);
        bool jumped = hasCenter && XZDistanceSquared(center, lastCenter) > renderRadius * renderRadius;
        if (jumped || lastTrafficHand != CityInfoStore.TrafficHand) ClearVehicles();
        lastTrafficHand = CityInfoStore.TrafficHand;
        lastCenter = center;
        hasCenter = true;
        scanTimer -= dt;
        if (scanTimer <= 0)
        {
            RefreshRoads(center);
            scanTimer = Mathf.Max(.1f, rangeScanInterval);
        }
        MoveVehicles(dt, center);
        densityTimer += dt;
        // 制御周期を固定し、フレームごとの確率の切り捨て・上限依存を避ける。
        while (densityTimer >= .25f)
        {
            densityTimer -= .25f;
            RegulateDensity(.25f);
        }
    }

    private void ClearVehicles()
    {
        motionGeneration++; motionInFlight=false; motionFetchTime=0; motion.Clear();
        for (int i = active.Count - 1; i >= 0; i--) Despawn(i);
        roads.Clear(); buckets.Clear(); bucketByKey.Clear();
        population.Reset(); scanTimer = 0; densityTimer = 0;
        if (accessWorker != null) StopCoroutine(accessWorker);
        accessWorker = null; entrances.Clear(); exitRanks.Clear(); accessCache.Clear(); initialFill = true; accessReady = false; accessGeneration++;
    }

    private void RefreshRoads(Vector3 center)
    {
        if (parcelManager == null) parcelManager = FindAnyObjectByType<ParcelManager>();
        if (buildingManager == null) buildingManager = FindAnyObjectByType<BuildingManager>();
        var nextRoads = new Dictionary<int, Road>();
        float radius = Mathf.Max(1, renderRadius + despawnMargin);
        foreach (var data in pathManager.GetPathsNearCircle(center, radius))
        {
            if (!Drivable(data)) continue;
            if (!pathManager.TryGetPathPolyline(data.path_id, out var points) || points.Count < 2) continue;
            var line = new VehiclePolyline(points);
            if (line.Length < .01f) continue;
            var intervals = line.InsideIntervals(center, radius);
            float length = 0;
            foreach (var interval in intervals) length += interval.y - interval.x;
            if (length <= 0) continue;
            RoadPath.GetPathMasterParams(data, out float width, out float sidewalk, out float side, out _, out int lanes,
                out _, out float divider, out _, out _, out _, out _);
            nextRoads[data.path_id] = new Road
            {
                data = data, line = line, intervals = intervals, insideLength = length, halfWidth = width * .5f,
                lanes = new RoadLaneLayout(width, sidewalk, side, divider, lanes, data.travel_direction)
            };
        }
        roads.Clear();
        foreach (var entry in nextRoads) roads.Add(entry.Key, entry.Value);
        FetchMotion();
        if (accessWorker == null) accessWorker = StartCoroutine(UpdateAccess(center));
        var previous = new Dictionary<long, Bucket>(bucketByKey);
        buckets.Clear(); bucketByKey.Clear();
        float total = 0;
        foreach (var road in roads.Values)
        foreach (int type in Constants.ROAD_VEHICLE_KEYS)
        {
            if (type == Constants.VEHICLE_BUS) continue;
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
            float progress = v.TotalLength > 0 ? v.s / v.TotalLength : 0;
            v.road = road.line; v.s = progress * road.line.Length;
            SetLane(v, road, false);
        }
    }

    // 周辺道路だけを走査し、区画・建物・中心線の版が変わった入口だけ再計算する。
    // 重い幾何処理と逆探索は車のUpdateから分離し、約1msごとに次フレームへ譲る。
    private IEnumerator UpdateAccess(Vector3 center)
    {
        yield return null;
        var snapshot = new Dictionary<int, Road>(roads);
        var usedParcels = new HashSet<int>();
        var next = new Dictionary<int, List<Entrance>>();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        int overall = CityInfoStore.TrafficHand;
        foreach (var road in snapshot.Values)
        {
            var entries = new List<Entrance>();
            int shapeHash = road.data.path_id;
            foreach (var point in road.line.points) shapeHash = unchecked(shapeHash * 31 + point.GetHashCode());
            shapeHash = unchecked(shapeHash * 31 + road.halfWidth.GetHashCode());
            shapeHash = unchecked(shapeHash * 31 + road.data.travel_direction);
            shapeHash = unchecked(shapeHash * 31 + road.data.start_node_id);
            shapeHash = unchecked(shapeHash * 31 + road.data.end_node_id);
            overall ^= shapeHash;
            float range = renderRadius + despawnMargin;
            if (XZDistanceSquared(road.line.points[0], center) > range * range) overall ^= road.data.path_id * 17;
            if (XZDistanceSquared(road.line.points[road.line.points.Length - 1], center) > range * range) overall ^= road.data.path_id * 37;
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
                    int building = 0;
                    foreach (int bid in buildingManager.GetBuildingIdsByParcel(id))
                    {
                        var data = buildingManager.GetBuildingSummary(bid);
                        if (data == null || !data.is_bd || data.status != Constants.STATUS_ACTIVE) continue;
                        signature = unchecked(signature * 31 + bid);
                        if (building == 0 || bid < building) building = bid;
                    }
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
                    if (timer.Elapsed.TotalMilliseconds >= 1) { yield return null; timer.Restart(); }
                }
            }
            next[road.data.path_id] = entries;
            if (timer.Elapsed.TotalMilliseconds >= 1) { yield return null; timer.Restart(); }
        }
        if (accessReady && overall == accessSignature) { accessWorker = null; yield break; }
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
            bool sink = XZDistanceSquared(end, center) > radius * radius;
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
            if (timer.Elapsed.TotalMilliseconds >= 1) { yield return null; timer.Restart(); }
        }
        while (queue.Count > 0)
        {
            long state = queue.Dequeue();
            foreach (long previous in reverse[state])
                if (!ranks.ContainsKey(previous)) { ranks[previous] = ranks[state] + 1; queue.Enqueue(previous); }
            if (timer.Elapsed.TotalMilliseconds >= 1) { yield return null; timer.Restart(); }
        }
        // 完成した表を一括交換する。途中の表を車に見せず、入口喪失は退出直前にも検査する。
        entrances = next; exitRanks = ranks; accessSignature = overall; accessReady = true; accessGeneration++;
        var stale = new List<int>();
        foreach (int id in accessCache.Keys)
            if (!usedParcels.Contains(id)) stale.Add(id);
        foreach (int id in stale) accessCache.Remove(id);
        accessWorker = null;
    }

    private void BuildEntrances(Road road, ParcelSummary parcel, int building, List<Entrance> result)
    {
        if (parcel.status != Constants.STATUS_ACTIVE) return;
        var polygon = GeneratedModelUtils.ToFloatJaggedArray(parcel.polygon);
        if (polygon == null || polygon.Length < 3) return;
        for (int i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
            if (a.Length < 2 || b.Length < 2) continue;
            Vector3 mid = new Vector3((a[0] + b[0]) * .5f, parcel.altitude, (a[1] + b[1]) * .5f);
            float s = road.line.Project(mid);
            road.line.Sample(s, true, out var point, out var tangent);
            Vector3 offset = mid - point; offset.y = 0;
            if (s < 3 || road.line.Length - s < 3 || Mathf.Abs(mid.y - point.y) > 2) continue;
            if (offset.magnitude < road.halfWidth - 1 || offset.magnitude > road.halfWidth + 4) continue;
            Vector3 edge = new Vector3(b[0] - a[0], 0, b[1] - a[1]).normalized;
            if (Mathf.Abs(Vector3.Dot(edge, tangent)) < .8f) continue;
            Vector3 destination = mid + offset.normalized * 3;
            if (!parcelManager.IsPointInParcel(parcel.parcel_id, destination)) continue;
            float side = Vector3.Dot(Vector3.Cross(Vector3.up, tangent), offset);
            bool forward = CityInfoStore.TrafficHand == Constants.TRAFFIC_HAND_LEFT ? side < 0 : side > 0;
            if (!DirectionAllowsEntry(road.data.travel_direction, forward)) continue;
            result.Add(new Entrance { parcel = parcel.parcel_id, building = building, s = s, forward = forward, destination = destination });
        }
    }

    private static bool FrontageAllowed(PathSummary road)
        => road != null && road.path_type == Constants.PATH_STREET && MasterDataStore.Instance != null
            && MasterDataStore.Instance.GetPathMasterDictionary().TryGetValue(road.path_variant, out var master) && master.frontage_allowed;

    private bool ValidEntrance(Entrance entry, int path)
    {
        if (parcelManager == null || buildingManager == null) return false;
        var parcel = parcelManager.GetParcelSummary(entry.parcel);
        var building = buildingManager.GetBuildingSummary(entry.building);
        var road = pathManager.GetPathData(path);
        return Drivable(road) && road.path_type == Constants.PATH_STREET
            && FrontageAllowed(road)
            && parcel != null && parcel.front_path_id == path && parcel.status == Constants.STATUS_ACTIVE
            && building != null && building.is_bd && building.parcel_id == entry.parcel && building.status == Constants.STATUS_ACTIVE
            && parcelManager.IsPointInParcel(entry.parcel, entry.destination);
    }

    private void RegulateDensity(float dt)
    {
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
        for (int i = 0; i < fill; i++)
        {
            if (active.Count >= maxActiveVehicles) break;
            Bucket chosen = PickSpawnBucket();
            if (chosen == null) break;
            Spawn(chosen);
            chosen.count++;
        }
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
        => b.target <= 0 || (!initialFill && !HasSpawnPoint(b.road)) ? 0
            : Mathf.Max(0, b.target - b.count) + Mathf.Max(0, b.target - b.average);

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
            if (v.arrivalPending || v.fading || v.age < minimumJourneyTime) continue;
            if (!exitRanks.ContainsKey(State(v.pathId, v.forward))) continue;
            float surplus = 0;
            if (bucketByKey.TryGetValue(Key(v.pathId, v.vehicleType), out var b))
            {
                surplus = b.average - b.target - b.pending;
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
    }
    private void BuildFollowingGaps()
    {
        gaps.Clear();
        var groups=new Dictionary<(int,bool,int),List<Vehicle>>();
        foreach(var v in active)
        {
            if(v.ExitingRoad || v.fading)continue;
            var key=(v.pathId,v.forward,v.lane);
            if(!groups.TryGetValue(key,out var list)){list=new();groups[key]=list;}
            list.Add(v);
        }
        foreach(var list in groups.Values)
        {
            list.Sort((a,b)=>Progress(a).CompareTo(Progress(b)));
            for(int i=0;i+1<list.Count;i++)gaps[list[i]]=Mathf.Max(0,Progress(list[i+1])-Progress(list[i])-4.5f);
        }
    }
    private static float Progress(Vehicle v)=>v.InIntersection ? -(v.connector.Length-v.connectorS) : v.forward?v.s:v.TotalLength-v.s;

    private void Spawn(Bucket b)
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
            if (options.Count == 0) return;
            var option = options[Random.Range(0, options.Count)]; s = option.s; forward = option.forward;
        }
        int variant = VehicleModelResolver.PickVariantId(b.type, Random.Range(0, int.MaxValue));
        var v = new Vehicle { vehicleType = b.type, variantId = variant,
            speed = Mathf.Max(.1f, MeanSpeed(b.road.data.path_id,b.type)),
            go = Acquire(b.type, variant), spawnBlendRemaining = 15 };
        v.SetPath(road.data.path_id, road.line, forward, s);
        SetLane(v, road, true);
        SetMotion(v);
        active.Add(v);
        Place(v);
    }

    private void SetLane(Vehicle v, Road road, bool choose)
    {
        if (choose)
        {
            bool heavy = v.vehicleType == Constants.VEHICLE_TRUCK || v.vehicleType == Constants.VEHICLE_BUS;
            v.lane = heavy && Random.value < heavyVehicleOuterLaneBias ? 0 : Random.Range(0, road.lanes.Count);
        }
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
            if (!v.ExitingRoad && !v.fading)
            {
                float distance = v.Remaining+(v.InIntersection?v.connector.Length-v.connectorS:0);
                float free=motion.TryGetValue(v.pathId,out var timing)?timing.FreeSpeed:v.motionFreeSpeed;
                float desired=RoadMotionControl.DesiredSpeed(distance,v.motionRemaining,free);
                v.speed=RoadMotionControl.FollowingSpeed(desired,v.speed,gaps.TryGetValue(v,out float gap)?gap:float.PositiveInfinity,dt);
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
                        v.arrivalDistance = Mathf.Max(0, (selected.s - v.s) * (v.forward ? 1 : -1));
                        v.arrivalBlendLength = Mathf.Min(15, Mathf.Max(.1f, v.arrivalDistance));
                        v.destinationParcel = selected.parcel; v.destinationBuilding = selected.building; v.destinationPoint = selected.destination;
                    }
                    else { v.arrivalDistance = -1; v.destinationParcel = 0; }
                }
                remaining = v.Advance(remaining);
                if (!v.InIntersection && v.arrivalDistance >= 0 && v.arrivalDistance < .001f)
                {
                    var entry = new Entrance { parcel = v.destinationParcel, building = v.destinationBuilding, destination = v.destinationPoint };
                    if (ValidEntrance(entry, v.pathId)) v.BeginParcelExit(v.destinationPoint);
                    else { v.arrivalDistance = -1; v.destinationParcel = 0; }
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
                        // 退出先のない行き止まりでは実体を残す。見えている高速道路上で消して帳尻を合わせない。
                        v.age += remaining; remaining = 0; break;
                    }
                    continue;
                }
                break;
            }
            Place(v);
            if (invalid || v.fadeElapsed >= Mathf.Max(.01f, fadeDuration) || (!v.ExitingRoad && XZDistanceSquared(v.go.transform.position, center) > radius * radius))
                Despawn(i);
        }
    }

    private bool Transition(Vehicle v)
    {
        PathSummary current = pathManager.GetPathData(v.pathId);
        if (current == null) return false;
        int node = v.AheadNodeId(current.start_node_id, current.end_node_id);
        candidates.Clear();
        float total = 0;
        foreach (var data in pathManager.GetPathsConnectedToNode(node))
        {
            if (data.path_id == v.pathId || !Drivable(data) || !roads.TryGetValue(data.path_id, out var road)) continue;
            bool forward = data.start_node_id == node;
            if (!forward && data.end_node_id != node) continue;
            if (!DirectionAllowsEntry(data.travel_direction, forward)) continue;
            float weight = Traffic(data, v.vehicleType);
            candidates.Add((road, forward, weight)); total += weight;
        }
        Road next; bool nextForward;
        if (candidates.Count == 0)
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
            next = candidates[chosen].road; nextForward = candidates[chosen].forward;
        }
        v.Sample(out Vector3 p0, out Vector3 t0);
        v.SetPath(next.data.path_id, next.line, nextForward);
        v.spawnBlendRemaining = 0; v.arrivalDistance = -1; v.destinationParcel = 0;
        SetLane(v, next, false);
        v.Sample(out Vector3 p1, out Vector3 t1);
        v.connector = VehiclePolyline.Connector(p0, t0, p1, t1, intersectionCurveTension);
        v.connectorS = 0;
        v.connectorSpeed = v.speed;
        SetMotion(v,v.connector.Length);
        return true;
    }

    private void Place(Vehicle v)
    {
        v.Sample(out Vector3 p, out Vector3 t);
        v.go.transform.SetPositionAndRotation(p, Quaternion.LookRotation(t, Vector3.up));
        float fade = v.fading ? 1 - v.fadeElapsed / Mathf.Max(.01f, fadeDuration) : v.ExitingRoad ? 1 : v.age / Mathf.Max(.01f, fadeDuration);
        v.go.transform.localScale = Vector3.one * Mathf.Clamp01(fade);
    }

    private GameObject Acquire(int type, int variant)
    {
        string key = type + ":" + variant;
        GameObject go = pool.TryGetValue(key, out var queue) && queue.Count > 0 ? queue.Dequeue() : VehicleModelResolver.CreateInstance(type, variant, container);
        go.transform.localScale = Vector3.one;
        go.SetActive(true);
        return go;
    }

    private void Despawn(int index)
    {
        var v = active[index];
        if (v.go != null)
        {
            v.go.SetActive(false);
            string key = v.vehicleType + ":" + v.variantId;
            if (!pool.TryGetValue(key, out var queue)) { queue = new Queue<GameObject>(); pool.Add(key, queue); }
            queue.Enqueue(v.go);
        }
        active.RemoveAt(index);
    }

    public static bool DirectionAllowsEntry(int direction, bool forward)
        => direction == Constants.DIR_BOTH || direction == (forward ? Constants.DIR_FORWARD : Constants.DIR_BACKWARD);
    private static bool Drivable(PathSummary p) => p != null && p.status == Constants.STATUS_ACTIVE &&
        (p.path_type == Constants.PATH_MOTORWAY || p.path_type == Constants.PATH_STREET);
    private static float Traffic(PathSummary p, int type)
        => p.path_traffic != null && p.path_traffic.TryGetValue(type.ToString(), out float value) && !float.IsNaN(value) && !float.IsInfinity(value) ? Mathf.Max(0, value) : 0;
    private static float SpeedFactor(int type) => type == Constants.VEHICLE_TRUCK || type == Constants.VEHICLE_BUS ? .8f : type == Constants.VEHICLE_COMMERCIAL ? .9f : 1;
    private static long Key(int path, int type) => ((long)path << 8) | (uint)type;
    private static float XZDistanceSquared(Vector3 a, Vector3 b) => (a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z);
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
