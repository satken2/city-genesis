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
    [SerializeField] private float timeExaggeration = LocalConstants.VEHICLE_TIME_EXAGGERATION;
    [SerializeField] private float activeHoursPerDay = LocalConstants.VEHICLE_ACTIVE_HOURS_PER_DAY;
    [SerializeField] private int maxActiveVehicles = 300;
    [SerializeField] private float rangeScanInterval = .5f;
    [SerializeField] private float spawnFillRate = 1f;
    [SerializeField] private float minimumJourneyTime = 6f;
    [Header("走行")]
    [SerializeField] private float baseSpeed = 12f;
    [SerializeField] private float intersectionCurveTension = .4f;
    [SerializeField] private float intersectionSpeedFactor = .5f;
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
    private Transform container;
    private float scanTimer, densityTimer;
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
        for (int i = active.Count - 1; i >= 0; i--) Despawn(i);
        roads.Clear(); buckets.Clear(); bucketByKey.Clear();
        population.Reset(); scanTimer = 0; densityTimer = 0;
    }

    private void RefreshRoads(Vector3 center)
    {
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
        var previous = new Dictionary<long, Bucket>(bucketByKey);
        buckets.Clear(); bucketByKey.Clear();
        float total = 0;
        foreach (var road in roads.Values)
        foreach (int type in Constants.ROAD_VEHICLE_KEYS)
        {
            long key = Key(road.data.path_id, type);
            float target = Traffic(road.data, type) / Mathf.Max(1, activeHoursPerDay * 3600) * road.insideLength
                / Mathf.Max(.1f, baseSpeed * SpeedFactor(type)) * Mathf.Max(0, timeExaggeration);
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

    private static float SpawnWeight(Bucket b)
        => b.target <= 0 ? 0 : Mathf.Max(0, b.target - b.count) + Mathf.Max(0, b.target - b.average);

    private bool ReserveArrival(bool redistribute)
    {
        Vehicle selected = null;
        float best = float.NegativeInfinity;
        foreach (var v in active)
        {
            if (v.arrivalPending || v.fading || v.age < minimumJourneyTime) continue;
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
        selected.arrivalPending = true;
        if (bucketByKey.TryGetValue(Key(selected.pathId, selected.vehicleType), out var chosen)) chosen.pending++;
        return true;
    }

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
        int variant = VehicleModelResolver.PickVariantId(b.type, Random.Range(0, int.MaxValue));
        var v = new Vehicle { vehicleType = b.type, variantId = variant,
            speed = Mathf.Max(.1f, baseSpeed * SpeedFactor(b.type) * Random.Range(.85f, 1.15f)),
            go = Acquire(b.type, variant), spawnBlendRemaining = 15 };
        v.SetPath(road.data.path_id, road.line, forward, s);
        SetLane(v, road, true);
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
        for (int i = active.Count - 1; i >= 0; i--)
        {
            var v = active[i];
            var data = pathManager.GetPathData(v.pathId);
            if (!Drivable(data) || !DirectionAllowsEntry(data.travel_direction, v.forward)) { Despawn(i); continue; }
            float remaining = dt;
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
                if (!v.InIntersection && v.arrivalPending && v.arrivalDistance < 0 && v.age >= minimumJourneyTime)
                {
                    // 路肩寄せを次の交差点までに完了できる場合だけ開始する。短い道路では予約を持ち越す。
                    float clearance = Mathf.Min(4f, v.TotalLength * .15f);
                    float available = v.Remaining - clearance;
                    float entryGap = Mathf.Max(0, Mathf.Min(8f, v.TotalLength * .2f) - v.distanceSinceJunction);
                    if (available > entryGap + .001f)
                    {
                        v.arrivalDistance = Mathf.Min(Random.Range(15f, 45f), available);
                        v.arrivalBlendLength = Mathf.Min(15, v.arrivalDistance - entryGap);
                    }
                }
                remaining = v.Advance(remaining);
                if (!v.InIntersection && v.arrivalDistance >= 0 && v.arrivalDistance < .001f)
                {
                    v.BeginRoadsideExit(CityInfoStore.TrafficHand); continue;
                }
                if (v.InIntersection && v.connectorS >= v.connector.Length - .001f)
                {
                    v.connector = null; v.distanceSinceJunction = 0;
                    continue;
                }
                if (!v.InIntersection && v.Remaining < .001f)
                {
                    if (++transitions > 128) { invalid = true; break; }
                    if (!Transition(v)) v.BeginRoadsideExit(CityInfoStore.TrafficHand);
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
            if (!DirectionAllowsEntry(current.travel_direction, !v.forward) || !roads.TryGetValue(v.pathId, out next)) return false;
            // 中央1車線には折り返しの横幅がないため、終端で到着する。
            if (next.lanes.Center(0, CityInfoStore.TrafficHand) == 0) return false;
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
            next = candidates[chosen].road; nextForward = candidates[chosen].forward;
        }
        v.Sample(out Vector3 p0, out Vector3 t0);
        v.SetPath(next.data.path_id, next.line, nextForward);
        v.spawnBlendRemaining = 0;
        SetLane(v, next, false);
        v.Sample(out Vector3 p1, out Vector3 t1);
        v.connector = VehiclePolyline.Connector(p0, t0, p1, t1, intersectionCurveTension);
        v.connectorS = 0;
        v.connectorSpeed = v.speed * Mathf.Lerp(1, Mathf.Clamp(intersectionSpeedFactor, .1f, 1), Mathf.Clamp01(Vector3.Angle(t0, t1) / 90));
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
            remainder = Mathf.Clamp(remainder + (Desired - actual) * dt / 12f, -2, 2);
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
