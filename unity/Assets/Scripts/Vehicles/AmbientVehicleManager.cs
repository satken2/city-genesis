using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Sky/sea ambience: high-altitude aircraft and slow offshore ships, independent of the economic simulation.
/// Ships approaching active ports are handled in AmbientVehicleManager.PortCalls.cs.
/// Vehicles have no GameObject; state uses saved coordinates without FloatingOrigin displacement,
/// and renders each frame with Graphics.RenderMesh. See the public overview V_環境演出の乗り物.md.
/// </summary>
public sealed partial class AmbientVehicleManager : MonoBehaviour
{
    public static AmbientVehicleManager Instance { get; private set; }

    private sealed class Spec
    {
        public string model;
        public bool air;
        public float minSpeed, maxSpeed;          // [m/s]
        public float minAltitude, maxAltitude;    // Aircraft altitude [m].
        public float turnRate;                    // Ship turn speed [deg/s].
        public float length;                      // Ship length [m], the basis for required land clearance.
        public float weight;                      // Selection weights for ship types.
        public float minInterval, maxInterval;    // Aircraft spawn interval [s].
        public int maxAlive;                      // Maximum simultaneous aircraft count.
        public float trailLife, trailSpacing, trailStart, trailEnd, trailAlpha;   // Aircraft contrails; trailLife 0 disables them.
        public float wakeLife;                    // Ship wake lifetime [s].
    }

    private static readonly Spec[] AirSpecs =
    {
        new Spec { model = "ambient_airliner", air = true, minSpeed = 170, maxSpeed = 230, minAltitude = 1500, maxAltitude = 2300,
                   minInterval = 45, maxInterval = 110, maxAlive = 2, trailLife = 45, trailSpacing = 60, trailStart = 14, trailEnd = 70, trailAlpha = .55f },
        new Spec { model = "ambient_propeller", air = true, minSpeed = 45, maxSpeed = 60, minAltitude = 350, maxAltitude = 650,
                   minInterval = 70, maxInterval = 200, maxAlive = 1 },
        new Spec { model = "ambient_helicopter", air = true, minSpeed = 35, maxSpeed = 55, minAltitude = 150, maxAltitude = 300,
                   minInterval = 70, maxInterval = 200, maxAlive = 1 },
    };
    private static readonly Spec[] SeaSpecs =
    {
        new Spec { model = "ambient_container_ship", minSpeed = 4.5f, maxSpeed = 6, turnRate = 1.0f, length = 172, weight = 3, wakeLife = 150 },
        new Spec { model = "ambient_tanker", minSpeed = 3.5f, maxSpeed = 5, turnRate = .8f, length = 168, weight = 2, wakeLife = 150 },
        new Spec { model = "ambient_cruise_ship", minSpeed = 4.5f, maxSpeed = 6, turnRate = 1.0f, length = 160, weight = 1.5f, wakeLife = 150 },
        new Spec { model = "ambient_fishing_boat", minSpeed = 3, maxSpeed = 5, turnRate = 5f, length = 22, weight = 3.5f, wakeLife = 60 },
    };
    private static readonly Spec BulkCarrier = new Spec { model = "ambient_bulk_carrier", wakeLife = 150 };

    // Port-call ships by port type. Offshore route speed is 9-12 knots; berth durations follow real cargo-handling/port-call times. Fishing ports are excluded.
    private static readonly PortSpec[] PortSpecs =
    {
        new PortSpec { variant = 401, ship = BulkCarrier, minSpeed = 4.6f, maxSpeed = 5.4f, minStayHours = 36, maxStayHours = 84 },   // Bulk port.
        new PortSpec { variant = 402, ship = SeaSpecs[1], minSpeed = 4.6f, maxSpeed = 5.4f, minStayHours = 18, maxStayHours = 36 },   // Tanker berth.
        new PortSpec { variant = 403, ship = SeaSpecs[0], minSpeed = 5.6f, maxSpeed = 6.4f, minStayHours = 12, maxStayHours = 24 },   // Container berth.
        new PortSpec { variant = 1068, ship = SeaSpecs[2], minSpeed = 5.6f, maxSpeed = 6.4f, minStayHours = 8, maxStayHours = 11 },   // Passenger terminal.
    };

    private sealed class Vehicle
    {
        public Spec spec;
        public PropRenderModel model;
        public AmbientTrail trail;      // Aircraft contrails.
        public ShipWake wake;           // Ship wakes.
        public Vector3 position;        // Saved coordinates.
        public float heading;           // Heading [deg], 0=+Z, clockwise.
        public float targetHeading, speed, decisionTimer, travelled, routeLength;
        public float expiresAt;         // Ship expiry time (TrafficClock.Time). Remove after expiry once outside the view.
        public bool dead;               // Ships that ran aground or completed their port stay, removed on the next update.
        public PortCall call;           // Port-call route and berth position; null for ambient ships and aircraft.
        public DrawLod lod = DrawLod.Normal;   // Previous frame's rendering LOD, allowing separate promotion/demotion thresholds.
        public Vector3 Direction => new Vector3(Mathf.Sin(heading * Mathf.Deg2Rad), 0, Mathf.Cos(heading * Mathf.Deg2Rad));
    }

    // Maintain expected ship count as open-sea area x density through spawning with lifetimes, proportional to sea area and rare enough for occasional sightings.
    private const float ShipsPerKm2 = 1f / 40f;
    private const int MaxShips = 6;
    private const float ShipLifetimeMin = 420f, ShipLifetimeMax = 780f;   // [s]
    private const float ShipSpawnTick = 2f;
    private const int SeaSampleGrid = 40;             // Sea-area estimation grid subdivisions per side.
    private const float AirRouteHalfLength = 7000f;   // Distance extending before/after the focal point. Spawn/despawn beyond the far clip of 4500m.
    private const float AirFocusRadius = 1200f;       // Range for displacing the route focal point from the camera target.
    private const float DrawDistance = 6000f;
    private const float SeaMargin = 1500f;            // Allowed ship distance outside the map.
    private const float ShipSpawnMinCameraDistance = 600f;
    private const float DecisionInterval = 2.5f;
    private static readonly float[] HeadingDeltas = { 0, 10, -10, 20, -20, 35, -35, 50, -50, 70, -70, 90, -90, 120, -120, 150, -150, 180 };

    private readonly List<Vehicle> vehicles = new List<Vehicle>();
    private readonly Dictionary<string, PropRenderModel> models = new Dictionary<string, PropRenderModel>();
    private readonly Dictionary<PropRenderModel, ShipWake.Hull> wakeHulls = new Dictionary<PropRenderModel, ShipWake.Hull>();
    private readonly float[] airTimers = new float[AirSpecs.Length];
    private float shipTimer;
    private float expectedShips;        // Open-sea area x density, estimated per coastline generation.
    private int coastVersion = -1;
    private bool visible = true;
    private Material trailMaterial, wakeMaterial;
    private IpcClient ipc;
    private TerrainManager terrain;

    /// <summary>FloatingOrigin shifts root Transforms together, so this otherwise stationary GameObject's position is the cumulative offset.</summary>
    private Vector3 Offset => transform.position;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindAnyObjectByType<AmbientVehicleManager>() == null)
            new GameObject("AmbientVehicleManager").AddComponent<AmbientVehicleManager>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        for (int i = 0; i < airTimers.Length; i++) airTimers[i] = Random.Range(10f, AirSpecs[i].minInterval);
        AssetPackStore.CityChanging += ForgetModels;
    }

    /// <summary>Reload models on city switches because the city's asset pack may differ.</summary>
    private void ForgetModels()
    {
        Clear();
        foreach (var model in models.Values) model.Dispose();
        models.Clear();
        wakeHulls.Clear();
    }

    private void OnDestroy()
    {
        AssetPackStore.CityChanging -= ForgetModels;
        Clear();
        foreach (var model in models.Values) model.Dispose();
        models.Clear();
        wakeHulls.Clear();
        if (trailMaterial != null) Destroy(trailMaterial);
        if (wakeMaterial != null) Destroy(wakeMaterial);
        if (ipc != null) ipc.OnSessionReset -= Clear;
        if (Instance == this) Instance = null;
    }

    public void SetVisible(bool value)
    {
        visible = value;
        if (!visible) Clear();
    }

    /// <summary>Clear all vehicles and port-call selections so the previous city's ships and aircraft do not survive session switches.</summary>
    public void Clear()
    {
        RemoveVehicles(_ => true);
        ResetPortCalls();
    }

    private void RemoveVehicles(System.Predicate<Vehicle> match)
    {
        for (int i = vehicles.Count - 1; i >= 0; i--)
        {
            if (!match(vehicles[i])) continue;
            vehicles[i].trail?.Dispose();
            vehicles[i].wake?.Dispose();
            vehicles.RemoveAt(i);
        }
    }

    private void Update()
    {
        if (ipc != IpcClient.Instance)
        {
            if (ipc != null) ipc.OnSessionReset -= Clear;
            ipc = IpcClient.Instance;
            if (ipc != null) ipc.OnSessionReset += Clear;
        }
        if (!visible || GameStateManager.Instance == null || !GameStateManager.Instance.IsGameStarted())
        {
            if (vehicles.Count > 0 || HasPortCallState) Clear();
            shoreVersion = TerrainManager.ShoreDataVersion;
            return;
        }
        // Port-call ships persist until the epoch advances, including during coastline refetching.
        if (!CoastlineStore.IsLoaded)
        {
            RemoveVehicles(v => v.call == null);
            return;
        }
        if (terrain == null) terrain = FindAnyObjectByType<TerrainManager>();
        if (ModelManager.Instance == null || terrain == null) return;

        float dt = Time.deltaTime * TrafficClock.Scale;
        Vector3 camera = CameraSavedPosition();
        if (coastVersion != CoastlineStore.Version)
        {
            coastVersion = CoastlineStore.Version;
            RemoveVehicles(v => v.call == null);
            expectedShips = EstimateSeaAreaKm2() * ShipsPerKm2;
            SeedShips(camera);
        }
        UpdatePortCalls();
        SpawnAircraft(dt, camera);
        SpawnShips(dt, camera);
        for (int i = vehicles.Count - 1; i >= 0; i--)
        {
            var v = vehicles[i];
            if (v.spec.air) MoveAircraft(v, dt);
            else if (v.call != null) MovePortShip(v, dt, camera);
            else MoveShip(v, dt);
            if (v.dead || v.travelled >= v.routeLength)
            {
                v.trail?.Dispose();
                v.wake?.Dispose();
                vehicles.RemoveAt(i);
                continue;
            }
            v.trail?.Record(v.position, TrafficClock.Time);
            v.wake?.Record(v.position, v.Direction, TrafficClock.Time, dt);
        }
    }

    private void LateUpdate()
    {
        if (vehicles.Count == 0) return;
        Vector3 camera = CameraSavedPosition();
        Vector3 offset = Offset;
        var view = Camera.main;
        float tanHalfFov = DrawLodEvaluator.GetTanHalfFov(view);
        foreach (var v in vehicles)
        {
            if ((v.position - camera).sqrMagnitude > DrawDistance * DrawDistance) continue;
            // Models must face +Z. Place ship origins at sea level, representing the waterline.
            var matrix = Matrix4x4.TRS(v.position + offset, Quaternion.LookRotation(v.Direction, Vector3.up), Vector3.one);
            var bounds = Utils.TransformBounds(v.model.bounds, matrix);
            v.lod = view != null
                ? SelectLod(v, Vector3.Distance(view.transform.position, bounds.center), tanHalfFov, view.orthographic)
                : DrawLod.Normal;
            var parts = v.lod == DrawLod.Close ? v.model.close : v.lod == DrawLod.Far ? v.model.far : v.model.normal;
            foreach (var part in parts)
            {
                var rp = new RenderParams(part.material)
                {
                    worldBounds = bounds,
                    shadowCastingMode = v.spec.air || v.lod == DrawLod.Far ? ShadowCastingMode.Off : ShadowCastingMode.On,
                    receiveShadows = !v.spec.air,
                };
                Graphics.RenderMesh(rp, part.mesh, part.submesh, matrix * part.local);
            }
            if (v.trail != null)
            {
                if (trailMaterial == null) trailMaterial = CreateMaterial("AmbientTrail", "飛行機雲");
                v.trail.Draw(v.position, TrafficClock.Time, offset, trailMaterial);
            }
            if (v.wake != null)
            {
                if (wakeMaterial == null) wakeMaterial = CreateMaterial("ShipWake", "船の航跡");
                v.wake.Draw(v.position, v.Direction, TrafficClock.Time, offset, wakeMaterial);
            }
        }
    }

    /// <summary>
    /// Rendering LOD: switch only models with dedicated _close / _far variants, such as ships, using the same projected-size ratio as buildings.
    /// Aircraft without variants always use the unsuffixed model. Avoid automatic vertex reduction because it distorts silhouettes of hulls/airframes tens of meters long.
    /// </summary>
    private static DrawLod SelectLod(Vehicle v, float distance, float tanHalfFov, bool orthographic)
    {
        var model = v.model;
        if (!model.DedicatedClose && !model.DedicatedFar) return DrawLod.Normal;
        var lod = DrawLodEvaluator.EvaluateBySize(model.Radius, distance, tanHalfFov, v.lod, model.DedicatedClose, orthographic);
        return lod == DrawLod.Far && !model.DedicatedFar ? DrawLod.Normal : lod;
    }

    private static Material CreateMaterial(string shaderName, string label)
    {
        var shader = Resources.Load<Shader>("Shaders/" + shaderName);
        if (shader == null) { Debug.LogWarning($"AmbientVehicleManager: Shaders/{shaderName} が無い。{label}は描かない"); return null; }
        return new Material(shader) { name = shaderName };
    }

    private Vector3 CameraSavedPosition()
    {
        var checker = CameraCellChecker.Instance;
        Vector3 world = checker != null ? checker.CurrentWorldPosition : (Camera.main != null ? Camera.main.transform.position : Vector3.zero);
        world.y = 0;
        return world - Offset;
    }

    private PropRenderModel GetModel(string name)
    {
        if (!models.TryGetValue(name, out var model))
        {
            model = new PropRenderModel(name);
            models.Add(name, model);
        }
        return model.normal.Count > 0 ? model : null;
    }

    private AmbientTrail MakeTrail(Spec spec)
    {
        if (spec.trailLife <= 0) return null;
        return new AmbientTrail(spec.trailSpacing, spec.trailLife, spec.trailStart, spec.trailEnd, spec.trailAlpha);
    }

    /// <summary>Measure each model's hull waterline from its mesh only once.</summary>
    private ShipWake MakeWake(Spec spec, PropRenderModel model, float speed)
    {
        if (spec.wakeLife <= 0) return null;
        if (!wakeHulls.TryGetValue(model, out var hull)) wakeHulls.Add(model, hull = ShipWake.Hull.Measure(model));
        return new ShipWake(hull, spec.wakeLife, speed);
    }

    // ---------------------------------------------------------------- Aircraft

    /// <summary>A route is a straight line through a focal point near the camera target. Spawn/despawn beyond AirRouteHalfLength on either side.</summary>
    private void SpawnAircraft(float dt, Vector3 camera)
    {
        for (int i = 0; i < AirSpecs.Length; i++)
        {
            airTimers[i] -= dt;
            if (airTimers[i] > 0) continue;
            var spec = AirSpecs[i];
            airTimers[i] = Random.Range(spec.minInterval, spec.maxInterval);
            int alive = 0;
            foreach (var existing in vehicles) if (existing.spec == spec) alive++;
            if (alive >= spec.maxAlive) continue;
            var model = GetModel(spec.model);
            if (model == null) continue;
            Vector2 focusOffset = Random.insideUnitCircle * AirFocusRadius;
            var focus = new Vector3(camera.x + focusOffset.x, Random.Range(spec.minAltitude, spec.maxAltitude), camera.z + focusOffset.y);
            var v = new Vehicle { spec = spec, model = model, trail = MakeTrail(spec), heading = Random.Range(0f, 360f),
                                  speed = Random.Range(spec.minSpeed, spec.maxSpeed), routeLength = AirRouteHalfLength * 2 };
            v.position = focus - v.Direction * AirRouteHalfLength;
            vehicles.Add(v);
        }
    }

    private static void MoveAircraft(Vehicle v, float dt)
    {
        float step = v.speed * dt;
        v.position += v.Direction * step;
        v.travelled += step;
    }

    // ---------------------------------------------------------------- Ships

    /// <summary>
    /// Open-sea area [km²]: sample the map rectangle on a grid and multiply by the fraction classified as open sea.
    /// Compute once per coastline generation; do not call every frame because each sample scans all rings.
    /// </summary>
    private float EstimateSeaAreaKm2()
    {
        if (!CoastlineStore.HasCoastline) return 0f;
        float w = terrain.GetMapWidth(), h = terrain.GetMapHeight();
        int sea = 0;
        for (int i = 0; i < SeaSampleGrid; i++)
            for (int j = 0; j < SeaSampleGrid; j++)
            {
                var p = new Vector3(-w * .5f + w * (i + .5f) / SeaSampleGrid, 0, -h * .5f + h * (j + .5f) / SeaSampleGrid);
                if (IsOpenSea(p)) sea++;
            }
        return w * h * 1e-6f * sea / (SeaSampleGrid * SeaSampleGrid);
    }

    /// <summary>Start near steady-state counts immediately after city load, choosing a Poisson count and uniformly distributed remaining lifetimes.</summary>
    private void SeedShips(Vector3 camera)
    {
        int count = 0;
        float threshold = Mathf.Exp(-expectedShips), product = Random.value;
        while (product > threshold && count < MaxShips) { count++; product *= Random.value; }
        for (int i = 0; i < count; i++)
        {
            var v = TrySpawnShip(camera);
            if (v != null) v.expiresAt = TrafficClock.Time + Random.Range(0f, ShipLifetimeMax);
        }
    }

    /// <summary>
    /// Steady-state process with spawn rate = expected count / mean lifetime. Remove expired ships only outside the view, avoiding visible disappearance.
    /// </summary>
    private void SpawnShips(float dt, Vector3 camera)
    {
        shipTimer -= dt;
        if (shipTimer > 0) return;
        shipTimer = ShipSpawnTick;
        int ships = 0;
        foreach (var existing in vehicles)
        {
            if (existing.spec.air || existing.call != null) continue;
            ships++;
            if (TrafficClock.Time > existing.expiresAt && (existing.position - camera).sqrMagnitude > DrawDistance * DrawDistance)
                existing.dead = true;
        }
        if (ships >= MaxShips || expectedShips <= 0f) return;
        float rate = expectedShips / ((ShipLifetimeMin + ShipLifetimeMax) * .5f);
        if (Random.value < rate * ShipSpawnTick) TrySpawnShip(camera);
    }

    private Vehicle TrySpawnShip(Vector3 camera)
    {
        if (!CoastlineStore.HasCoastline) return null;
        var spec = PickShipSpec();
        var model = GetModel(spec.model);
        if (model == null) return null;
        float halfW = terrain.GetMapWidth() * .5f + 500f, halfH = terrain.GetMapHeight() * .5f + 500f;
        float need = spec.length * .6f + 150f;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            var p = new Vector3(Random.Range(-halfW, halfW), 0, Random.Range(-halfH, halfH));
            if ((p - camera).sqrMagnitude < ShipSpawnMinCameraDistance * ShipSpawnMinCameraDistance) continue;
            if (!IsOpenSea(p) || Clearance(p) < need) continue;
            var v = new Vehicle { spec = spec, model = model, position = p,
                                  speed = Random.Range(spec.minSpeed, spec.maxSpeed), routeLength = float.PositiveInfinity,
                                  heading = Random.Range(0f, 360f), expiresAt = TrafficClock.Time + Random.Range(ShipLifetimeMin, ShipLifetimeMax) };
            v.wake = MakeWake(spec, model, v.speed);
            v.heading = v.targetHeading = ChooseHeading(v);
            v.decisionTimer = DecisionInterval;
            vehicles.Add(v);
            return v;
        }
        return null;
    }

    private static Spec PickShipSpec()
    {
        float total = 0;
        foreach (var s in SeaSpecs) total += s.weight;
        float r = Random.Range(0f, total);
        foreach (var s in SeaSpecs) { r -= s.weight; if (r <= 0) return s; }
        return SeaSpecs[SeaSpecs.Length - 1];
    }

    private void MoveShip(Vehicle v, float dt)
    {
        v.decisionTimer -= dt;
        if (v.decisionTimer <= 0)
        {
            v.decisionTimer = DecisionInterval;
            // Distance to the coastline scans all edges, so check only at decision intervals rather than every frame.
            if (Clearance(v.position) < v.spec.length * .3f) { v.dead = true; return; }
            v.targetHeading = ChooseHeading(v);
        }
        v.heading = Mathf.MoveTowardsAngle(v.heading, v.targetHeading, v.spec.turnRate * dt);
        v.position += v.Direction * (v.speed * dt);
    }

    /// <summary>
    /// Score each candidate heading by minimum forward clearance (distance to coastline minus required clearance), favoring straight travel toward the widest water.
    /// Scale look-ahead distance by both length and speed to see a turning-radius distance ahead.
    /// </summary>
    private float ChooseHeading(Vehicle v)
    {
        float need = v.spec.length * .6f + 30f;
        float lookahead = Mathf.Max(300f, v.spec.length * 3f, v.speed / (v.spec.turnRate * Mathf.Deg2Rad) * 2f);
        float halfW = terrain.GetMapWidth() * .5f + SeaMargin, halfH = terrain.GetMapHeight() * .5f + SeaMargin;
        float best = float.NegativeInfinity, bestHeading = v.heading;
        foreach (float delta in HeadingDeltas)
        {
            float h = v.heading + delta;
            var dir = new Vector3(Mathf.Sin(h * Mathf.Deg2Rad), 0, Mathf.Cos(h * Mathf.Deg2Rad));
            float score = 300f;
            for (int k = 1; k <= 4; k++)
            {
                Vector3 p = v.position + dir * (lookahead * k / 4f);
                float margin = Clearance(p) - need;
                if (Mathf.Abs(p.x) > halfW || Mathf.Abs(p.z) > halfH) margin -= 400f;
                if (margin < score) score = margin;
            }
            score -= Mathf.Abs(delta) * .6f + Random.Range(0f, 12f);
            if (score > best) { best = score; bestHeading = h; }
        }
        return Mathf.Repeat(bestHeading, 360f);
    }

    /// <summary>Signed coastline distance: positive at sea, negative on land. Without a coastline, treat the whole area as sea.</summary>
    private static float Clearance(Vector3 saved) => -CoastlineStore.SignedDistance(saved.x, saved.z);

    /// <summary>
    /// Whether a point is open sea, outside every ring. Holes inside land rings are lakes, so exclude by containment count rather than parity.
    /// Within the map, also require elevation sufficiently below sea level to prevent grounding from coarse coastlines.
    /// </summary>
    private bool IsOpenSea(Vector3 saved)
    {
        Vector2 point = new Vector2(saved.x, saved.z);
        int depth = 0;
        foreach (var ring in CoastlineStore.Rings)
        {
            if (ring.Length < 3) continue;
            if (PolygonUtils.Contains(ring, point)) depth++;
        }
        if (depth != 0) return false;
        float halfW = terrain.GetMapWidth() * .5f, halfH = terrain.GetMapHeight() * .5f;
        if (Mathf.Abs(saved.x) < halfW && Mathf.Abs(saved.z) < halfH && Utils.GetGroundHeight(saved + Offset) > -2f) return false;
        return true;
    }
}
