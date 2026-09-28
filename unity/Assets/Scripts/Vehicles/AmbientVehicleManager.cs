using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 空と海の環境演出。高高度を横切る航空機と、沖合をゆっくり進む船舶を経済シミュレーションと無関係に流す。
/// 稼働中の港へ向かう入港船は AmbientVehicleManager.PortCalls.cs。
/// 乗り物は GameObject を持たず、保存座標(FloatingOrigin のずれを含まない)で状態を持ち、
/// 毎フレーム Graphics.RenderMesh で描く。仕様は公開 V 文書(V_環境演出の乗り物.md)。
/// </summary>
public sealed partial class AmbientVehicleManager : MonoBehaviour
{
    public static AmbientVehicleManager Instance { get; private set; }

    private sealed class Spec
    {
        public string model;
        public bool air;
        public float minSpeed, maxSpeed;          // [m/s]
        public float minAltitude, maxAltitude;    // 航空機の高度 [m]
        public float turnRate;                    // 船の旋回速度 [deg/s]
        public float length;                      // 船の全長 [m]。陸地との必要余裕の基準
        public float weight;                      // 船種の抽選重み
        public float minInterval, maxInterval;    // 航空機の発生間隔 [s]
        public int maxAlive;                      // 航空機の同時存在数
        public float trailLife, trailSpacing, trailStart, trailEnd, trailAlpha;   // trailLife 0 = 帯なし
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
        new Spec { model = "ambient_container_ship", minSpeed = 4.5f, maxSpeed = 6, turnRate = 1.0f, length = 172, weight = 3,
                   trailLife = 80, trailSpacing = 40, trailStart = 15.5f, trailEnd = 77, trailAlpha = .3f },
        new Spec { model = "ambient_tanker", minSpeed = 3.5f, maxSpeed = 5, turnRate = .8f, length = 168, weight = 2,
                   trailLife = 90, trailSpacing = 40, trailStart = 15.5f, trailEnd = 64, trailAlpha = .3f },
        new Spec { model = "ambient_cruise_ship", minSpeed = 4.5f, maxSpeed = 6, turnRate = 1.0f, length = 160, weight = 1.5f,
                   trailLife = 80, trailSpacing = 40, trailStart = 14, trailEnd = 62, trailAlpha = .3f },
        new Spec { model = "ambient_fishing_boat", minSpeed = 3, maxSpeed = 5, turnRate = 5f, length = 22, weight = 3.5f,
                   trailLife = 30, trailSpacing = 5, trailStart = 2.5f, trailEnd = 9, trailAlpha = .35f },
    };
    private static readonly Spec BulkCarrier = new Spec { model = "ambient_bulk_carrier",
        trailLife = 80, trailSpacing = 40, trailStart = 15, trailEnd = 76, trailAlpha = .3f };

    // 港の種類ごとの入港船。速度は港外の航路の速さ(9〜12ノット)、停泊時間は実際の港での荷役・寄港の時間。漁港は対象外。
    private static readonly PortSpec[] PortSpecs =
    {
        new PortSpec { variant = 401, ship = BulkCarrier, minSpeed = 4.6f, maxSpeed = 5.4f, minStayHours = 36, maxStayHours = 84 },   // バルク港
        new PortSpec { variant = 402, ship = SeaSpecs[1], minSpeed = 4.6f, maxSpeed = 5.4f, minStayHours = 18, maxStayHours = 36 },   // タンカー埠頭
        new PortSpec { variant = 403, ship = SeaSpecs[0], minSpeed = 5.6f, maxSpeed = 6.4f, minStayHours = 12, maxStayHours = 24 },   // コンテナ埠頭
        new PortSpec { variant = 1068, ship = SeaSpecs[2], minSpeed = 5.6f, maxSpeed = 6.4f, minStayHours = 8, maxStayHours = 11 },   // 客船ターミナル
    };

    private sealed class Vehicle
    {
        public Spec spec;
        public PropRenderModel model;
        public AmbientTrail trail;
        public Vector3 position;        // 保存座標
        public float heading;           // 進行方位 [deg]。0=+Z、時計回り
        public float targetHeading, speed, decisionTimer, travelled, routeLength;
        public float expiresAt;         // 船の寿命(Time.time)。過ぎたら画面外に出た時点で消す
        public bool dead;               // 岸に乗り上げた船・停泊を終えた入港船。次の更新で消す
        public PortCall call;           // 入港船の航路と接岸位置。環境演出の船と航空機は null
        public DrawLod lod = DrawLod.Normal;   // 前のフレームの描画LOD(昇格と降格のしきい値を分けるため)
        public Vector3 Direction => new Vector3(Mathf.Sin(heading * Mathf.Deg2Rad), 0, Mathf.Cos(heading * Mathf.Deg2Rad));
    }

    // 船は「外洋の面積×密度」の期待値を寿命付きの発生で維持する。海の広さに比例し、たまに見かける程度の希少さにする。
    private const float ShipsPerKm2 = 1f / 40f;
    private const int MaxShips = 6;
    private const float ShipLifetimeMin = 420f, ShipLifetimeMax = 780f;   // [s]
    private const float ShipSpawnTick = 2f;
    private const int SeaSampleGrid = 40;             // 海面積の見積もり格子(片側の分割数)
    private const float AirRouteHalfLength = 7000f;   // 焦点の前後に伸ばす距離。遠景クリップ(4500m)の外で発生・消滅する
    private const float AirFocusRadius = 1200f;       // 航路の焦点をカメラ注視点からずらす範囲
    private const float DrawDistance = 6000f;
    private const float SeaMargin = 1500f;            // 船がマップ外へ出てよい距離
    private const float ShipSpawnMinCameraDistance = 600f;
    private const float DecisionInterval = 2.5f;
    private const float WakeHeight = .3f;
    private static readonly float[] HeadingDeltas = { 0, 10, -10, 20, -20, 35, -35, 50, -50, 70, -70, 90, -90, 120, -120, 150, -150, 180 };

    private readonly List<Vehicle> vehicles = new List<Vehicle>();
    private readonly Dictionary<string, PropRenderModel> models = new Dictionary<string, PropRenderModel>();
    private readonly float[] airTimers = new float[AirSpecs.Length];
    private float shipTimer;
    private float expectedShips;        // 外洋面積×密度。海岸線の世代ごとに見積もる
    private int coastVersion = -1;
    private bool visible = true;
    private Material trailMaterial;
    private IpcClient ipc;
    private TerrainManager terrain;

    /// <summary>FloatingOrigin はルートの Transform を一括でずらすので、動かさないこの GameObject の位置がそのまま累積オフセットになる。</summary>
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
    }

    private void OnDestroy()
    {
        Clear();
        foreach (var model in models.Values) model.Dispose();
        models.Clear();
        if (trailMaterial != null) Destroy(trailMaterial);
        if (ipc != null) ipc.OnSessionReset -= Clear;
        if (Instance == this) Instance = null;
    }

    public void SetVisible(bool value)
    {
        visible = value;
        if (!visible) Clear();
    }

    /// <summary>全ての乗り物と入港船の抽選を消す。セッション切り替えで前の都市の船や飛行機が残らないようにする。</summary>
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
        // 入港船はエポックが進むまで残すので、海岸線の取り直しの間も消さない。
        if (!CoastlineStore.IsLoaded)
        {
            RemoveVehicles(v => v.call == null);
            return;
        }
        if (terrain == null) terrain = FindAnyObjectByType<TerrainManager>();
        if (ModelManager.Instance == null || terrain == null) return;

        float dt = Time.deltaTime;
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
                vehicles.RemoveAt(i);
                continue;
            }
            // 航跡は海面板(Y=0)と重ならないよう少し浮かせる。
            v.trail?.Record(v.spec.air ? v.position : v.position + Vector3.up * WakeHeight, Time.time);
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
                if (trailMaterial == null) trailMaterial = CreateTrailMaterial();
                v.trail.Draw(v.spec.air ? v.position : v.position + Vector3.up * WakeHeight, Time.time, offset, trailMaterial);
            }
        }
    }

    /// <summary>
    /// 描画LOD。専用の接写・遠景モデル(`_close` / `_far`)を持つもの(船)だけを、建物と同じ投影比の判定で切り替える。
    /// 専用モデルの無いもの(航空機)は常に無印で描く。自動の頂点集約は数十m〜の機体・船体の輪郭を崩すため使わない。
    /// </summary>
    private static DrawLod SelectLod(Vehicle v, float distance, float tanHalfFov, bool orthographic)
    {
        var model = v.model;
        if (!model.DedicatedClose && !model.DedicatedFar) return DrawLod.Normal;
        var lod = DrawLodEvaluator.EvaluateBySize(model.Radius, distance, tanHalfFov, v.lod, model.DedicatedClose, orthographic);
        return lod == DrawLod.Far && !model.DedicatedFar ? DrawLod.Normal : lod;
    }

    private static Material CreateTrailMaterial()
    {
        var shader = Resources.Load<Shader>("Shaders/AmbientTrail");
        if (shader == null) { Debug.LogWarning("AmbientVehicleManager: Shaders/AmbientTrail が無い。飛行機雲・航跡は描かない"); return null; }
        return new Material(shader) { name = "AmbientTrail" };
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

    // ---------------------------------------------------------------- 航空機

    /// <summary>航路はカメラ注視点付近の焦点を通る直線。焦点の前後 AirRouteHalfLength の外で発生・消滅する。</summary>
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

    // ---------------------------------------------------------------- 船舶

    /// <summary>
    /// 外洋の面積 [km²]。マップ矩形を格子でサンプリングし、外洋と判定された点の割合を掛ける。
    /// 海岸線の世代ごとに一度だけ計算する(点ごとに全リングを走査するため毎フレームは呼ばない)。
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

    /// <summary>都市の読み込み直後から定常状態に近い数にする。ポアソン分布で数を決め、残り寿命は一様にばらす。</summary>
    private void SeedShips(Vector3 camera)
    {
        int count = 0;
        float threshold = Mathf.Exp(-expectedShips), product = Random.value;
        while (product > threshold && count < MaxShips) { count++; product *= Random.value; }
        for (int i = 0; i < count; i++)
        {
            var v = TrySpawnShip(camera);
            if (v != null) v.expiresAt = Time.time + Random.Range(0f, ShipLifetimeMax);
        }
    }

    /// <summary>
    /// 発生率 = 期待値 ÷ 平均寿命 の定常過程。寿命の尽きた船は描画範囲の外に出た時点で消す(目の前で消えない)。
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
            if (Time.time > existing.expiresAt && (existing.position - camera).sqrMagnitude > DrawDistance * DrawDistance)
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
            var v = new Vehicle { spec = spec, model = model, trail = MakeTrail(spec), position = p,
                                  speed = Random.Range(spec.minSpeed, spec.maxSpeed), routeLength = float.PositiveInfinity,
                                  heading = Random.Range(0f, 360f), expiresAt = Time.time + Random.Range(ShipLifetimeMin, ShipLifetimeMax) };
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
            // 海岸線までの距離は全辺走査なので、毎フレームではなく判断の周期でだけ見る。
            if (Clearance(v.position) < v.spec.length * .3f) { v.dead = true; return; }
            v.targetHeading = ChooseHeading(v);
        }
        v.heading = Mathf.MoveTowardsAngle(v.heading, v.targetHeading, v.spec.turnRate * dt);
        v.position += v.Direction * (v.speed * dt);
    }

    /// <summary>
    /// 候補方位ごとに前方の余裕(海岸線までの距離−必要余裕)の最小値を採点し、直進を優先しつつ最も広い水面へ向かう。
    /// 旋回半径ぶん先を見るため、先読み距離は全長と速度の両方に比例させる。
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

    /// <summary>海岸線までの距離。海で正、陸で負。海岸線が無ければ全域を海とみなす。</summary>
    private static float Clearance(Vector3 saved) => -CoastlineStore.SignedDistance(saved.x, saved.z);

    /// <summary>
    /// 外洋(どのリングにも含まれない点)かどうか。陸地リングの内側の穴は湖なので、偶奇判定ではなく包含数で除外する。
    /// マップ内なら標高が海面より十分低いことも要求する(海岸線の粗さで岸に乗るのを防ぐ)。
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
