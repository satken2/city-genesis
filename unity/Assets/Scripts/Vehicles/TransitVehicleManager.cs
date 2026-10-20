using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;


/// <summary>Place trains using the established line clock, reproducing off-screen progress and dwell with the same clock.</summary>
public sealed partial class TransitVehicleManager : MonoBehaviour
{
    public static TransitVehicleManager Instance { get; private set; }
    private readonly Dictionary<GameObject, (int line, int train)> trainByVehicle = new();
    private readonly Dictionary<GameObject, Renderer[]> pickRenderers = new();
    private void Awake() { Instance = this; }
    private void OnDestroy() { if (Instance == this) Instance = null; }
    // Iterators disposed before their first step skip finally, so clear the handle here to allow prewarming after re-enabling.
    private void OnDisable() { RenderWorkScheduler.Stop(this, warmWorker); warmWorker = null; warmedRevision = -1; }

    /// <summary>Select only rendered bodies. Vehicles behind buildings or terrain cannot be selected in normal view.</summary>
    public bool TryPick(Ray ray, out int lineId)
    {
        if (!TryPickTrain(ray, out lineId, out _, out float nearest)) return false;
        if (Physics.Raycast(ray, out _, Mathf.Max(0, nearest - .05f), ~0, QueryTriggerInteraction.Ignore)) return false;
        return true;
    }

    /// <summary>Nearest rendered train hit by the ray. The caller checks occlusion by foreground buildings/terrain.</summary>
    public bool TryPickTrain(Ray ray, out int lineId, out int train, out float nearest)
    {
        lineId = -1; train = -1;
        nearest = 5000;
        foreach (var go in visible.Values)
        {
            if (!go.activeInHierarchy || !trainByVehicle.TryGetValue(go, out var owner)) continue;
            if (!pickRenderers.TryGetValue(go, out var renderers))
            { renderers = go.GetComponentsInChildren<Renderer>(); pickRenderers[go] = renderers; }
            foreach (var renderer in renderers)
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                // Even for angled bodies, do not make empty corners of the world AABB selectable.
                var localRay = new Ray(renderer.transform.InverseTransformPoint(ray.origin), renderer.transform.InverseTransformVector(ray.direction));
                if (!renderer.localBounds.IntersectRay(localRay, out float localDistance)) continue;
                Vector3 hit = renderer.transform.TransformPoint(localRay.GetPoint(localDistance));
                float distance = Vector3.Dot(hit-ray.origin, ray.direction);
                if (distance >= 0 && distance < nearest) { nearest = distance; (lineId, train) = owner; }
            }
        }
        return lineId >= 0;
    }
    [SerializeField] private float renderRadius = 480;
    [SerializeField] private int maxVisibleCars = 300;
    private PathManager paths;
    private double clock;
    private float scanTimer;
    private int revision = -1, session = -1;
    private readonly List<Line> lines = new();
    private readonly Dictionary<int, Line> lineById = new();
    private readonly HashSet<int> routePaths = new();
    private TransitRouteGeometry routeGeometry;
    private readonly Dictionary<string, GameObject> visible = new();
    private readonly Dictionary<string, Stack<GameObject>> pool = new();
    private readonly HashSet<string> used = new();
    private readonly Dictionary<GameObject,float> nominalLengths = new();
    private readonly Dictionary<int,(double distance,double time)> soundTrack = new(); // Previous values per train for deriving running-sound speed from line-clock distance changes.
    private int trainCreationsThisFrame, creationFrame = -1;
    private int warmedRevision = -1;
    private Coroutine warmWorker;
    private sealed class Line : TransitRouteGeometry.Route
    {
        public TransitLineSummary data;
        public int type;
        public Vector3[] positions, directions;
        public string[] keys, modelKeys;   // Working array where DrawLine places one train's vehicle keys, sized to car count.
        public VehicleModelConfig.TransitCarModel[] models;
        public Color32? livery;             // Line color for body stripes; unset retains original paint.
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindAnyObjectByType<TransitVehicleManager>() == null)
            new GameObject("TransitVehicleManager").AddComponent<TransitVehicleManager>();
    }

    private void Update()
    {
        var store = TransitDataStore.Ensure();
        if (session != store.Session)
        {
            StopDrive();
            RenderWorkScheduler.Stop(this, warmWorker); warmWorker = null; warmedRevision = -1;
            session = store.Session; clock = 0; revision = -1; lines.Clear(); lineById.Clear(); routePaths.Clear(); routeGeometry?.Clear();
            HideUnused(true);
            TransitLivery.Reset();
            soundTrack.Clear();
            VehicleAmbience.Clear(VehicleAmbience.Kind.Bus);
            VehicleAmbience.Clear(VehicleAmbience.Kind.Train);
        }
        if (GameStateManager.Instance == null || !GameStateManager.Instance.IsGameStarted())
        { HideUnused(true); return; }
        if (paths == null) paths = FindAnyObjectByType<PathManager>();
        if (paths == null || CameraCellChecker.Instance == null) return;
        driveDelta = Time.deltaTime * DriveMode.SpeedMultiplier * TrafficClock.Scale;
        clock += driveDelta;
        if (revision != store.Revision) { revision = store.Revision; RebuildLines(store.Lines); }
        if (warmWorker == null && warmedRevision != revision && lines.Count > 0 && ModelManager.Instance != null)
        {
            warmedRevision = revision;
            // Resident auxiliary work, excluded from initial-render completion waits.
            warmWorker = RenderWorkScheduler.Start(this, WarmPool(new List<Line>(lines)), "transitWarmup", () => false);
        }
        Vector3 center = CameraCellChecker.Instance.CurrentWorldPosition;
        if (DriveMode.TryGetLoadCenter(out var rideCenter)) center = rideCenter;
        scanTimer -= Time.deltaTime;
        if (scanTimer <= 0) { scanTimer = .5f; RefreshGeometry(center); }
        used.Clear();
        foreach (var line in lines) if (line.data.line_agent_id == DrivenLineId) DrawLine(line, center);
        foreach (var line in lines) if (line.data.line_agent_id != DrivenLineId) DrawLine(line, center);
        HideUnused(false);
    }

    private void RebuildLines(List<TransitLineSummary> source)
    {
        lines.Clear();
        lineById.Clear();
        routePaths.Clear();
        foreach (var data in source)
        {
            if (data.status != Constants.STATUS_ACTIVE || data.vehicle_count < Math.Max(1, data.cars_per_train)) continue;
            var line = new Line { data = data, motion = new TransitMotionClock(data.journeys), type = VehicleModelConfig.GetTransitVehicleType(data.path_variant),
                livery = TransitLivery.Parse(data.line_color) };
            line.VehicleType = line.type;
            if (line.motion.Duration <= 0 || line.motion.Length <= 0 || line.type == 0 || data.cars_per_train > maxVisibleCars) continue;
            line.ConsistLength = Math.Max(1,data.cars_per_train)*CarLength(line);
            line.positions = new Vector3[Math.Max(1,data.cars_per_train)]; line.directions = new Vector3[line.positions.Length];
            line.keys = new string[line.positions.Length]; line.modelKeys = new string[line.positions.Length];
            line.models = new VehicleModelConfig.TransitCarModel[line.positions.Length];
            TransitRouteGeometry.BuildRuns(line);
            lines.Add(line);
            lineById[data.line_agent_id] = line;
            foreach (var run in line.runs) routePaths.Add(run.path);
        }
        scanTimer = 0;
        var driven = lines.Find(line => line.data.line_agent_id == DrivenLineId);
        if (DrivenLineId >= 0 && (driven == null || drivenTrain >= Trains(driven))) StopDrive();
    }

    private static int Trains(Line line) => line.data.vehicle_count / Math.Max(1, line.data.cars_per_train);

    /// <summary>Train departure interval (s). Departures are staggered by effective headway, using the same formula as core transit_capacity.effective_headway.</summary>
    private static double Headway(Line line, int trains) => Math.Max(line.data.target_headway * 60.0, line.motion.Duration / trains);

    /// <summary>Train thinning: on lines with excessive trains, render only every stride-th train.</summary>
    private int Stride(Line line, int trains)
        => Math.Max(1,(int)Math.Ceiling((double)trains / Math.Max(1,maxVisibleCars/Math.Max(1,line.data.cars_per_train)*4)));

    /// <summary>Traffic-animation line clock (s), shared by train positions and station/stop passenger rendering.</summary>
    public double Clock => clock;

    /// <summary>Circuit clock, cars per train, and car length (m) for a rendered line. False for suspended lines or unbuildable trains.</summary>
    public bool TryGetLine(int lineId, out TransitMotionClock motion, out int cars, out float carLength)
    {
        motion = null; cars = 0; carLength = 0;
        if (!lineById.TryGetValue(lineId, out var line)) return false;
        motion = line.motion; cars = Math.Max(1, line.data.cars_per_train); carLength = CarLength(line);
        return true;
    }

    /// <summary>
    /// Circuit phase for each rendered train (s, from 0 inclusive to cycle exclusive). Phases beyond the circuit duration wait at the first stop.
    /// Timing and thinning match DrawLine, including delays for the train being ridden.
    /// </summary>
    public bool TryGetTrainPhases(int lineId, List<double> phases, out double cycle)
    {
        phases.Clear(); cycle = 0;
        if (!lineById.TryGetValue(lineId, out var line)) return false;
        int trains = Trains(line), stride = Stride(line, trains);
        double headway = Headway(line, trains);
        cycle = headway * trains;
        for (int train = 0; train < trains; train++)
        {
            bool driven = line.data.line_agent_id == DrivenLineId && train == drivenTrain;
            if (!driven && train % stride != 0) continue;
            double time = clock + headway*train + (driven ? driveOffset : 0);
            phases.Add(((time % cycle) + cycle) % cycle);
        }
        return phases.Count > 0;
    }

    /// <summary>
    /// Centers and directions of vehicles stopped at the stop at step end. Uses the same route geometry as running rendering,
    /// so stopping positions are available even without a train present then. False if route geometry is not loaded.
    /// </summary>
    public bool TrySampleStop(int lineId, int step, Vector3[] positions, Vector3[] directions)
    {
        if (routeGeometry == null || !lineById.TryGetValue(lineId, out var line) || positions.Length != Math.Max(1, line.data.cars_per_train)) return false;
        var s = line.motion.Steps[step];
        return PlaceCars(line, line.motion.Distances[step] + s.length_m, s.to_fraction > s.from_fraction, CarLength(line), positions, directions);
    }

    /// <summary>Vehicle centers/directions for a train centered at clock distance. Buses align each vehicle to its lane.</summary>
    private bool PlaceCars(Line line, double distance, bool forward, float spacing, Vector3[] positions, Vector3[] directions)
    {
        if (line.type != Constants.VEHICLE_BUS) return routeGeometry.SampleConsist(line,distance,forward,spacing,positions,directions);
        for (int car = 0; car < positions.Length; car++)
        {
            double offset = ((positions.Length-1)*.5-car)*spacing;
            if (!routeGeometry.SampleBus(line,distance+offset,distance,forward,spacing,out positions[car],out directions[car])) return false;
        }
        return true;
    }

    /// <summary>Nominal car length (m). Use model length for types without length in the line summary.</summary>
    private static float CarLength(Line line)
        => line.data.length_per_vehicle > 0 ? line.data.length_per_vehicle : VehicleModelResolver.GetVehicleLength(line.type);

    private void RefreshGeometry(Vector3 center)
    {
        routeGeometry ??= new TransitRouteGeometry(paths);
        if (routePaths.Count == 0) { routeGeometry.Clear(); return; }
        float margin = 0;
        foreach (var l in lines) margin = Mathf.Max(margin, l.data.cars_per_train*Mathf.Max(1,l.data.length_per_vehicle));
        routeGeometry.Refresh(routePaths, paths.GetPathsNearCircle(center, renderRadius + margin));
    }

    /// <summary>
    /// Place one line's trains. For appearances without existing bodies, create only one train per frame because Instantiate and LOD construction are expensive.
    /// Never thin the ridden train or partially visible trains, avoiding gaps and flicker.
    /// </summary>
    private void DrawLine(Line line, Vector3 center)
    {
        if (creationFrame != Time.frameCount) { creationFrame = Time.frameCount; trainCreationsThisFrame = 0; }
        int cars = Math.Max(1,line.data.cars_per_train), trains = Trains(line);
        if (line.keys == null || line.keys.Length != cars) { line.keys = new string[cars]; line.modelKeys = new string[cars]; }
        float spacing = CarLength(line);
        int stride = Stride(line, trains);
        double headway = Headway(line, trains);
        double cycle = headway * trains;
        for (int train = 0; train < trains; train++)
        {
            bool driven = line.data.line_agent_id == DrivenLineId && train == drivenTrain;
            if (!driven && train % stride != 0) continue;
            // Use the line's selected appearance for every train; only invalid selections fall back deterministically by line ID/train index. Keep all cars consistent across reversals and pool reuse.
            int variant = VehicleModelResolver.PickTransitVariantId(line.type, line.data.vehicle_model, unchecked(line.data.line_agent_id * 397 + train));
            double trainTime = clock + headway*train + (driven ? driveOffset : 0);
            double runTime = line.motion.Schedule(trainTime, cycle);
            int step = line.motion.AtTime(runTime, out double distance, out bool stopped);
            if (step < 0) continue;
            bool reversed = line.motion.Reversed[step];
            var positions = line.positions; var directions = line.directions;
            bool forward = line.motion.Steps[step].to_fraction > line.motion.Steps[step].from_fraction;
            bool bus = line.type == Constants.VEHICLE_BUS;
            if (!bus && !driven)
            {
                if (!routeGeometry.Sample(line,distance,distance,forward,out var anchor,out _)) continue;
                Vector3 delta = anchor-center; delta.y = 0;
                // Before vehicle-end searches, reject trains entirely out of range, retaining padding for trains whose front alone is visible.
                float reach = renderRadius + (cars*.5f+1)*spacing;
                if (delta.sqrMagnitude > reach*reach) continue;
            }
            bool ready = PlaceCars(line,distance,forward,spacing,positions,directions);
            bool near = false;
            for (int car = 0; ready && car < cars; car++)
            {
                if (bus && stopped) BusTurnaround(line,step,runTime,ref positions[car],ref directions[car]);
                Vector3 delta = positions[car]-center; delta.y = 0;
                near |= delta.sqrMagnitude <= renderRadius*renderRadius;
            }
            if (driven)
            {
                DriveWaiting = !ready;
                if (!ready)
                {
                    // Do not advance only the clock beyond unloaded connections. Retain the last train pose and view while waiting for loading.
                    driveOffset -= driveDelta;
                    string prefix = line.data.line_agent_id + ":" + train + ":";
                    foreach (var entry in visible) if (entry.Key.StartsWith(prefix, StringComparison.Ordinal)) used.Add(entry.Key);
                    continue;
                }
                // Place the view on the leading vehicle in travel direction, switching to the opposite end when departing after reversal.
                DrivePosition = positions[0]; DriveDirection = directions[0];
            }
            if (!ready || (!driven && !near) || used.Count+cars > maxVisibleCars) continue;
            bool anyVisible = false, needsCreation = false;
            for (int car = 0; car < cars; car++)
            {
                // On reversal, preserve body front/rear orientation and reverse train order relative to travel direction.
                int identity = reversed ? cars-1-car : car;
                var model = VehicleModelConfig.GetTransitCarModel(line.data.path_variant, variant, identity, cars);
                line.models[car] = model;
                string modelKey = line.type + ":" + model.Part;
                string key = line.data.line_agent_id+":"+train+":"+identity+":"+modelKey;
                line.modelKeys[car] = modelKey; line.keys[car] = key;
                if (visible.ContainsKey(key)) anyVisible = true;
                else if (!pool.TryGetValue(modelKey, out var stock) || stock.Count == 0) needsCreation = true;
            }
            if (!driven && needsCreation && !anyVisible && trainCreationsThisFrame > 0) continue;
            ReportSound(line, train, variant, trainTime, distance, positions[cars/2]);
            bool created = false;
            for (int car = 0; car < cars; car++)
            {
                var model = line.models[car];
                string modelKey = line.modelKeys[car], key = line.keys[car];
                used.Add(key);
                if (!visible.TryGetValue(key, out var go))
                {
                    if (pool.TryGetValue(modelKey,out var stack) && stack.Count > 0) go = stack.Pop();
                    else { go = VehicleModelResolver.CreateTransitCar(line.type,model.Part,transform,model.LiveryVariant); created = true; }
                    go.name = modelKey; visible[key] = go; go.SetActive(true);
                }
                if (!nominalLengths.TryGetValue(go,out float fitted) || fitted != spacing)
                { VehicleModelResolver.FitTransitLength(go,spacing); nominalLengths[go]=spacing; }
                trainByVehicle[go] = (line.data.line_agent_id, train);
                TransitLivery.Apply(go, line.livery);
                Vector3 bodyForward = directions[car] * (reversed && line.type != Constants.VEHICLE_BUS ? -1 : 1);
                if (model.ReverseBody) bodyForward = -bodyForward;
                go.transform.SetPositionAndRotation(positions[car],Quaternion.LookRotation(bodyForward,Vector3.up));
                if (driven && car == 0) DriveBody = go;
            }
            if (created) trainCreationsThisFrame++;
        }
    }

    /// <summary>
    /// Precreate one body for each actual model used by active lines, removing first-visible train instantiation
    /// from rendering frames. Skip appearances with pooled or visible bodies. Retry next Update if interrupted.
    /// </summary>
    private IEnumerator WarmPool(List<Line> snapshot)
    {
        bool completed = false;
        try
        {
            var variants = new HashSet<int>();
            var seen = new HashSet<string>();
            foreach (var line in snapshot)
            {
                int cars = Math.Max(1,line.data.cars_per_train), trains = line.data.vehicle_count/cars;
                variants.Clear();
                for (int train = 0; train < trains; train++)
                    variants.Add(VehicleModelResolver.PickTransitVariantId(line.type, line.data.vehicle_model, unchecked(line.data.line_agent_id * 397 + train)));
                foreach (int variant in variants)
                for (int identity = 0; identity < cars; identity++)
                {
                    var model = VehicleModelConfig.GetTransitCarModel(line.data.path_variant, variant, identity, cars);
                    string modelKey = line.type + ":" + model.Part;
                    if (!seen.Add(modelKey)) continue;
                    if (pool.TryGetValue(modelKey, out var stack) && stack.Count > 0) continue;
                    bool shown = false;
                    foreach (var go in visible.Values) if (go != null && go.name == modelKey) { shown = true; break; }
                    if (shown) continue;
                    var car = VehicleModelResolver.CreateTransitCar(line.type, model.Part, transform, model.LiveryVariant);
                    car.name = modelKey; car.SetActive(false);
                    if (stack == null) { stack = new Stack<GameObject>(); pool[modelKey] = stack; }
                    stack.Push(car);
                    yield return RenderWorkScheduler.Continue;
                }
            }
            completed = true;
        }
        finally
        {
            warmWorker = null;
            if (!completed) warmedRevision = -1;
        }
    }

    /// <summary>
    /// Register rendered trains as running-sound emitters, choosing sounds by train appearance.
    /// Speed comes from line-clock distance changes; clamp jumps at circuit wrapping to the upper limit.
    /// </summary>
    private void ReportSound(Line line, int train, int variant, double time, double distance, Vector3 position)
    {
        int id = unchecked(line.data.line_agent_id * 1000003 + train);
        float speed = 0;
        if (soundTrack.TryGetValue(id, out var last) && time > last.time)
            speed = (float)Math.Min(60, Math.Abs(distance - last.distance) / (time - last.time));
        soundTrack[id] = (distance, time);
        VehicleAmbience.Report(line.type == Constants.VEHICLE_BUS ? VehicleAmbience.Kind.Bus : VehicleAmbience.Kind.Train, id, position, speed,
            VehicleModelConfig.GetTransitSound(line.type, variant));
    }

    private void BusTurnaround(Line line,int index,double time,ref Vector3 position,ref Vector3 direction)
    {
        var step=line.motion.Steps[index];
        var next=line.motion.Steps[(index+1)%line.motion.Steps.Count];
        if (next.path_id!=step.path_id || (next.to_fraction-next.from_fraction)*(step.to_fraction-step.from_fraction)>=0) return;
        double phase=((time%line.motion.Duration)+line.motion.Duration)%line.motion.Duration;
        double elapsed=phase-line.motion.Times[index]-step.duration_seconds;
        double duration=Math.Min(5,step.dwell_seconds);
        if(duration<=0)return;
        float t=Mathf.Clamp01((float)((elapsed-(step.dwell_seconds-duration))/duration));
        Vector3 right=Vector3.Cross(Vector3.up,direction).normalized;
        float offset=routeGeometry.GetOffset(step.path_id)*(CityInfoStore.TrafficHand==Constants.TRAFFIC_HAND_LEFT?-1:1);
        float angle=t*Mathf.PI;
        if(Math.Abs(offset)<.001f) { direction=Quaternion.AngleAxis(t*180,Vector3.up)*direction; return; }
        position+=right*offset*(Mathf.Cos(angle)-1)+direction*Math.Abs(offset)*Mathf.Sin(angle);
        direction=(direction*Mathf.Cos(angle)-right*Mathf.Sign(offset)*Mathf.Sin(angle)).normalized;
    }

    private void HideUnused(bool all)
    {
        var remove = new List<string>();
        foreach(var entry in visible)
        {
            if(!all && used.Contains(entry.Key)) continue;
            var go=entry.Value; go.SetActive(false);
            if(!pool.TryGetValue(go.name,out var stack)) { stack=new Stack<GameObject>();pool[go.name]=stack; }
            stack.Push(go);remove.Add(entry.Key);
        }
        foreach(var key in remove)visible.Remove(key);
    }
}
