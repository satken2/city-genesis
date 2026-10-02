using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;


/// <summary>確定した路線時計から編成を配置する。画面外の進行と停車も同じ時計で再現する。</summary>
public sealed partial class TransitVehicleManager : MonoBehaviour
{
    public static TransitVehicleManager Instance { get; private set; }
    private readonly Dictionary<GameObject, int> lineByVehicle = new();
    private readonly Dictionary<GameObject, Renderer[]> pickRenderers = new();
    private void Awake() { Instance = this; }
    private void OnDestroy() { if (Instance == this) Instance = null; }
    // 初回ステップ前に破棄された iterator は finally を通らないため、ハンドルをここで戻して再有効化後の事前生成を止めない。
    private void OnDisable() { RenderWorkScheduler.Stop(this, warmWorker); warmWorker = null; warmedRevision = -1; }

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
    private int trainCreationsThisFrame, creationFrame = -1;
    private int warmedRevision = -1;
    private Coroutine warmWorker;
    private sealed class Line : TransitRouteGeometry.Route
    {
        public TransitLineSummary data;
        public int type;
        public Vector3[] positions, directions;
        public string[] keys, modelKeys;   // DrawLine が1編成ぶんの車両キーを置く作業領域(長さは編成両数)
        public VehicleModelConfig.TransitCarModel[] models;
        public Color32? livery;             // 車体の帯を塗る路線色(未設定なら元の塗装)
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
            session = store.Session; clock = 0; revision = -1; lines.Clear(); routePaths.Clear(); routeGeometry?.Clear();
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
            // 常駐の補助処理なので、初期描画の完了判定を待たせない。
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
            foreach (var run in line.runs) routePaths.Add(run.path);
        }
        scanTimer = 0;
        if (DrivenLineId >= 0 && !lines.Exists(line => line.data.line_agent_id == DrivenLineId)) StopDrive();
    }

    /// <summary>1両の公称車長(m)。路線サマリに車長が無い種別はモデルの長さを使う。</summary>
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
    /// 1路線の編成を配置する。まだ車体の無い外観の編成は、Instantiate と LOD 構築が重いので1フレームに1編成だけ作る。
    /// 乗車中の編成と一部が表示中の編成は間引かない(欠けや点滅を出さない)。
    /// </summary>
    private void DrawLine(Line line, Vector3 center)
    {
        if (creationFrame != Time.frameCount) { creationFrame = Time.frameCount; trainCreationsThisFrame = 0; }
        int cars = Math.Max(1,line.data.cars_per_train), trains = line.data.vehicle_count/cars;
        if (line.keys == null || line.keys.Length != cars) { line.keys = new string[cars]; line.modelKeys = new string[cars]; }
        float spacing = CarLength(line);
        int stride = Math.Max(1,(int)Math.Ceiling((double)trains / Math.Max(1,maxVisibleCars/cars*4)));
        // 編成は実効運行間隔(コアの transit_capacity.effective_headway と同じ式)ずつずらして発車する。
        double headway = Math.Max(line.data.target_headway * 60.0, line.motion.Duration / trains);
        double cycle = headway * trains;
        for (int train = 0; train < trains; train += stride)
        {
            // 路線の選択外観を全編成で使い、候補外のときだけ路線IDと編成番号で固定する。往復・プール復帰でも全車を揃える。
            int variant = VehicleModelResolver.PickTransitVariantId(line.type, line.data.vehicle_model, unchecked(line.data.line_agent_id * 397 + train));
            bool driven = line.data.line_agent_id == DrivenLineId && train == 0;
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
                // 車端探索の前に編成全体が範囲外の候補を除く。先頭だけが見える編成の余白を残す。
                float reach = renderRadius + (cars*.5f+1)*spacing;
                if (delta.sqrMagnitude > reach*reach) continue;
            }
            bool ready = bus || routeGeometry.SampleConsist(line,distance,forward,spacing,positions,directions);
            bool near = false;
            for (int car = 0; ready && car < cars; car++)
            {
                if (bus)
                {
                    double offset = ((cars-1)*.5-car)*spacing;
                    if (!routeGeometry.SampleBus(line,distance+offset,distance,forward,spacing,out positions[car],out directions[car])) { ready = false; break; }
                    if (stopped) BusTurnaround(line,step,runTime,ref positions[car],ref directions[car]);
                }
                Vector3 delta = positions[car]-center; delta.y = 0;
                near |= delta.sqrMagnitude <= renderRadius*renderRadius;
            }
            if (driven)
            {
                DriveWaiting = !ready;
                if (!ready)
                {
                    // 未ロードの接続を越えて時計だけ進めない。最後の編成と視点を保ってロードを待つ。
                    driveOffset -= driveDelta;
                    string prefix = line.data.line_agent_id + ":0:";
                    foreach (var entry in visible) if (entry.Key.StartsWith(prefix, StringComparison.Ordinal)) used.Add(entry.Key);
                    continue;
                }
                int cab = reversed ? cars - 1 : 0;
                DrivePosition = positions[cab]; DriveDirection = directions[cab];
            }
            if (!ready || (!driven && !near) || used.Count+cars > maxVisibleCars) continue;
            bool anyVisible = false, needsCreation = false;
            for (int car = 0; car < cars; car++)
            {
                // 折り返しでは車体の前後を維持し、進行方向に対する編成順を逆転する。
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
            ReportSound(line, train, trainTime, distance, positions[cars/2]);
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
                lineByVehicle[go] = line.data.line_agent_id;
                TransitLivery.Apply(go, line.livery);
                Vector3 bodyForward = directions[car] * (reversed && line.type != Constants.VEHICLE_BUS ? -1 : 1);
                if (model.ReverseBody) bodyForward = -bodyForward;
                go.transform.SetPositionAndRotation(positions[car],Quaternion.LookRotation(bodyForward,Vector3.up));
            }
            if (created) trainCreationsThisFrame++;
        }
    }

    /// <summary>
    /// 運行中の路線が使う実体モデルごとに車体を1両ずつ前もって作り、初めて見える編成の生成を
    /// 描画フレームから外す。在庫か表示中の車体がある外観は飛ばす。中断されたら次の Update でやり直す。
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
