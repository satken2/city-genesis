using System;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;


/// <summary>確定した路線時計から編成を配置する。画面外の進行と停車も同じ時計で再現する。</summary>
public sealed class TransitVehicleManager : MonoBehaviour
{
    public static TransitVehicleManager Instance { get; private set; }
    private readonly Dictionary<GameObject, int> lineByVehicle = new();
    private readonly Dictionary<GameObject, Renderer[]> pickRenderers = new();
    private void Awake() { Instance = this; }
    private void OnDestroy() { if (Instance == this) Instance = null; }

    /// <summary>描画中の車体だけを選ぶ。建物・地形の背後の車両は通常表示から選択できない。</summary>
    public bool TryPick(Ray ray, out int lineId)
    {
        lineId = -1;
        float nearest = 5000;
        foreach (var go in visible.Values)
        {
            if (!go.activeInHierarchy || !lineByVehicle.TryGetValue(go, out int id)) continue;
            if (!pickRenderers.TryGetValue(go, out var renderers))
            { renderers = go.GetComponentsInChildren<Renderer>(); pickRenderers[go] = renderers; }
            foreach (var renderer in renderers)
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                // 車体が斜めを向いたときも、ワールドAABBの空いた角を選択範囲にしない。
                var localRay = new Ray(renderer.transform.InverseTransformPoint(ray.origin), renderer.transform.InverseTransformVector(ray.direction));
                if (!renderer.localBounds.IntersectRay(localRay, out float localDistance)) continue;
                Vector3 hit = renderer.transform.TransformPoint(localRay.GetPoint(localDistance));
                float distance = Vector3.Dot(hit-ray.origin, ray.direction);
                if (distance >= 0 && distance < nearest) { nearest = distance; lineId = id; }
            }
        }
        if (lineId < 0) return false;
        if (Physics.Raycast(ray, out _, Mathf.Max(0, nearest - .05f), ~0, QueryTriggerInteraction.Ignore)) return false;
        return true;
    }
    [SerializeField] private float renderRadius = 480;
    [SerializeField] private int maxVisibleCars = 300;
    private PathManager paths;
    private double clock;
    private float scanTimer;
    private int revision = -1, session = -1;
    private readonly List<Line> lines = new();
    private readonly HashSet<int> routePaths = new();
    private TransitRouteGeometry routeGeometry;
    private readonly Dictionary<string, GameObject> visible = new();
    private readonly Dictionary<string, Stack<GameObject>> pool = new();
    private readonly HashSet<string> used = new();
    private readonly Dictionary<GameObject,float> nominalLengths = new();
    private readonly Dictionary<int,(double distance,double time)> soundTrack = new(); // 走行音の速度を路線時計の距離差分から出すための編成別の前回値
    private sealed class Line : TransitRouteGeometry.Route
    {
        public TransitLineSummary data;
        public int type;
        public Vector3[] positions, directions;
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
            session = store.Session; clock = 0; revision = -1; lines.Clear(); routePaths.Clear(); routeGeometry?.Clear();
            HideUnused(true);
            soundTrack.Clear();
            VehicleAmbience.Clear(VehicleAmbience.Kind.Bus);
            VehicleAmbience.Clear(VehicleAmbience.Kind.Train);
        }
        if (GameStateManager.Instance == null || !GameStateManager.Instance.IsGameStarted())
        { HideUnused(true); return; }
        if (paths == null) paths = FindAnyObjectByType<PathManager>();
        if (paths == null || CameraCellChecker.Instance == null) return;
        clock += Time.deltaTime;
        if (revision != store.Revision) { revision = store.Revision; RebuildLines(store.Lines); }
        Vector3 center = CameraCellChecker.Instance.CurrentWorldPosition;
        scanTimer -= Time.deltaTime;
        if (scanTimer <= 0) { scanTimer = .5f; RefreshGeometry(center); }
        used.Clear();
        foreach (var line in lines) DrawLine(line, center);
        HideUnused(false);
    }

    private void RebuildLines(List<TransitLineSummary> source)
    {
        lines.Clear();
        routePaths.Clear();
        foreach (var data in source)
        {
            if (data.status != Constants.STATUS_ACTIVE || data.vehicle_count < Math.Max(1, data.cars_per_train)) continue;
            var line = new Line { data = data, motion = new TransitMotionClock(data.journeys), type = VehicleModelConfig.GetTransitVehicleType(data.path_variant) };
            line.VehicleType = line.type;
            if (line.motion.Duration <= 0 || line.motion.Length <= 0 || line.type == 0 || data.cars_per_train > maxVisibleCars) continue;
            line.positions = new Vector3[Math.Max(1,data.cars_per_train)]; line.directions = new Vector3[line.positions.Length];
            TransitRouteGeometry.BuildRuns(line);
            lines.Add(line);
            foreach (var run in line.runs) routePaths.Add(run.path);
        }
        scanTimer = 0;
    }

    private void RefreshGeometry(Vector3 center)
    {
        routeGeometry ??= new TransitRouteGeometry(paths);
        if (routePaths.Count == 0) { routeGeometry.Clear(); return; }
        float margin = 0;
        foreach (var l in lines) margin = Mathf.Max(margin, l.data.cars_per_train*Mathf.Max(1,l.data.length_per_vehicle));
        routeGeometry.Refresh(routePaths, paths.GetPathsNearCircle(center, renderRadius + margin));
    }

    private void DrawLine(Line line, Vector3 center)
    {
        int cars = Math.Max(1,line.data.cars_per_train), trains = line.data.vehicle_count/cars;
        float spacing = line.data.length_per_vehicle > 0 ? line.data.length_per_vehicle : VehicleModelResolver.GetVehicleLength(line.type);
        int stride = Math.Max(1,(int)Math.Ceiling((double)trains / Math.Max(1,maxVisibleCars/cars*4)));
        for (int train = 0; train < trains; train += stride)
        {
            // 編成の外観は路線IDと編成番号で固定し、往復・プール復帰でも全車を揃える。
            int variant = VehicleModelResolver.PickVariantId(line.type, unchecked(line.data.line_agent_id * 397 + train));
            double trainTime = clock + line.motion.Duration*train/trains;
            int step = line.motion.AtTime(trainTime, out double distance, out bool stopped);
            if (step < 0) continue;
            bool returning = line.motion.Returning[step];
            var positions = line.positions; var directions = line.directions;
            bool ready = true, near = false;
            for (int car = 0; car < cars; car++)
            {
                double offset = ((cars-1)*.5-car)*spacing;
                if (!routeGeometry.Sample(line, distance+offset, distance, line.motion.Steps[step].to_fraction > line.motion.Steps[step].from_fraction, out positions[car], out directions[car])) { ready = false; break; }
                if (line.type == Constants.VEHICLE_BUS && stopped) BusTurnaround(line,step,trainTime,ref positions[car],ref directions[car]);
                Vector3 delta = positions[car]-center; delta.y = 0;
                near |= delta.sqrMagnitude <= renderRadius*renderRadius;
            }
            if (!ready || !near || used.Count+cars > maxVisibleCars) continue;
            ReportSound(line, train, trainTime, distance, positions[cars/2]);
            for (int car = 0; car < cars; car++)
            {
                // 折り返しでは車体の前後を維持し、進行方向に対する編成順を逆転する。
                int identity = returning ? cars-1-car : car;
                bool middle = identity > 0 && identity < cars-1;
                string modelKey = line.type + ":" + variant + (middle ? ":middle" : ":lead");
                string key = line.data.line_agent_id+":"+train+":"+identity+":"+modelKey;
                used.Add(key);
                if (!visible.TryGetValue(key, out var go))
                {
                    if (pool.TryGetValue(modelKey,out var stack) && stack.Count > 0) go = stack.Pop();
                    else go = VehicleModelResolver.CreateTransitCar(line.type,middle,transform,variant);
                    go.name = modelKey; visible[key] = go; go.SetActive(true);
                }
                if (!nominalLengths.TryGetValue(go,out float fitted) || fitted != spacing)
                { VehicleModelResolver.FitTransitLength(go,spacing); nominalLengths[go]=spacing; }
                lineByVehicle[go] = line.data.line_agent_id;
                Vector3 forward = directions[car] * (returning && line.type != Constants.VEHICLE_BUS ? -1 : 1);
                if (identity == cars-1 && cars > 1 && line.type != Constants.VEHICLE_BUS) forward = -forward;
                go.transform.SetPositionAndRotation(positions[car],Quaternion.LookRotation(forward,Vector3.up));
            }
        }
    }

    /// <summary>描画した編成を走行音の発音体として通知する。速度は路線時計の距離差分で、周回の折り返しで飛ぶ値は上限で丸める。</summary>
    private void ReportSound(Line line, int train, double time, double distance, Vector3 position)
    {
        int id = unchecked(line.data.line_agent_id * 1000003 + train);
        float speed = 0;
        if (soundTrack.TryGetValue(id, out var last) && time > last.time)
            speed = (float)Math.Min(60, Math.Abs(distance - last.distance) / (time - last.time));
        soundTrack[id] = (distance, time);
        VehicleAmbience.Report(line.type == Constants.VEHICLE_BUS ? VehicleAmbience.Kind.Bus : VehicleAmbience.Kind.Train, id, position, speed);
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
