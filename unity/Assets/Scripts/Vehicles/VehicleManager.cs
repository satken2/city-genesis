using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// カメラ周辺の道路に、path_traffic(車種別交通量、1日あたりの通過トリップ数)と
/// travel_direction(走行方向)に応じて車を走らせるクライアント側シミュレータ。
/// サーバー永続オブジェクトではなく手続き的に生成する短命オブジェクトのため、
/// BaseApiDataManager のセル単位ロードには載せず、PathManager が既に
/// ロード済みの pathDataCache を読んで独立に動く。
///
/// 挙動: 描画範囲(カメラ注視点+半径)内の各パスに「目標台数 = 物理的な同時存在台数 × 演出倍率」を
/// 定め、不足分をパス上のランダム地点(沿道の建物から出てきた車)へ確率的に補充する。
/// 物理台数は 流量[台/s](交通量[台/日]÷活動秒数) × 区間通過時間[s] で求まる期待値で、
/// 実スケールではほぼ0台のため timeExaggeration で時間を圧縮して見た目の賑わいを作る。
/// 交差点では交差先道路の交通量に応じて確率的に分岐(travel_direction を尊重)し、乗り換え先が
/// 目標超過なら超過度に応じた確率で「沿道の建物に到着した」とみなしてデスポーンする。
/// これにより各パスの車密度が path_traffic に比例した水準へ自己調整され、交通量の少ない道路に
/// 車が滞留し続けることがない。範囲外へ出た車もデスポーンする。
/// 信号待ち・当たり判定は考慮しない。当面は単純な箱で描画(VehicleModelResolver 参照)。
/// </summary>
public class VehicleManager : MonoBehaviour
{
    [Header("参照(未設定なら実行時に自動解決)")]
    [SerializeField] private PathManager pathManager;
    [SerializeField] private CameraCellChecker cameraCellChecker;

    [Header("描画範囲")]
    [Tooltip("カメラ注視点からこの半径[m]の円内に車を走らせる。既定 400m ≒ Dynamic LOD 相当。")]
    [SerializeField] private float renderRadius = 400f;
    [Tooltip("この半径[m]を超えたらデスポーン(renderRadius より少し外側)。")]
    [SerializeField] private float despawnMargin = 80f;

    [Header("スポーン/密度")]
    [Tooltip("時間圧縮の演出倍率。1=物理的に正確な同時存在台数(交通量[台/日]を活動時間で流したときに範囲内に居る期待台数)で、実スケールではほぼ0台になるため、この倍率で賑わいを誇張する。")]
    [SerializeField] private float timeExaggeration = LocalConstants.VEHICLE_TIME_EXAGGERATION;
    [Tooltip("交通が流れる1日の活動時間[h]。バックエンドの PATH_ACTIVE_HOURS と同じ値を保つ(LocalConstants 参照)。")]
    [SerializeField] private float activeHoursPerDay = LocalConstants.VEHICLE_ACTIVE_HOURS_PER_DAY;
    [Tooltip("目標台数への不足分を1秒あたりこの割合で補充する(値が大きいほど速く適正密度に達する)。")]
    [SerializeField] private float spawnFillRate = 1.0f;
    [Tooltip("目標超過パスへ乗り換えた車が沿道到着(途中降車)する確率の強さ。0=無効。")]
    [SerializeField] private float arrivalDespawnStrength = 1.0f;
    [Tooltip("同時に存在できる車の最大数(全体クランプ)。")]
    [SerializeField] private int maxActiveVehicles = 300;
    [Tooltip("範囲内パスの再スキャン間隔[秒]。")]
    [SerializeField] private float rangeScanInterval = 0.5f;

    [Header("走行")]
    [Tooltip("基準速度[m/s]。車種係数と個体ばらつきが乗る。")]
    [SerializeField] private float baseSpeed = 12f;
    [Tooltip("中心線からの横オフセット[m](車線中心に寄せる)。")]
    [SerializeField] private float laneOffset = 2.5f;

    [Header("デバッグ")]
    [SerializeField] private bool showDebugOverlay = false;

    // 0=左側通行 / 1=右側通行。GET /api/v1/city の traffic_hand から取得(既定=左)。
    private int trafficHand = 0;

    private readonly List<Vehicle> active = new List<Vehicle>();
    // プール: キー "{type}:{variant}" → 非アクティブな GameObject キュー。
    private readonly Dictionary<string, Queue<GameObject>> pool = new Dictionary<string, Queue<GameObject>>();
    private Transform container;

    // 範囲内パス(描画円と交わる走行可能道路)のキャッシュ。sIn/sOut は円内区間の始点距離[m]。
    private struct RangePath
    {
        public int pathId;
        public float sIn;
        public float sOut;
    }
    private readonly List<RangePath> rangePaths = new List<RangePath>();
    // 範囲内パスのID集合。交通量0で目標を持たないパスも含む(途中降車判定の対象識別に使う)。
    private readonly HashSet<int> rangePathIds = new HashSet<int>();
    // (パス, 車種) → 目標台数。スキャンごとに再構築。キーは PathTypeKey。
    private readonly Dictionary<long, float> targetByPathType = new Dictionary<long, float>();
    // (パス, 車種) → 現在の走行台数。スポーン/乗り換え/デスポーンで増減し、スキャンとは独立に維持。
    private readonly Dictionary<long, int> countByPathType = new Dictionary<long, int>();
    private float rangeScanTimer;

    // path_traffic をリフレクションで読むためのフィールド情報(スキーマ未更新でも落ちないため)。
    private static readonly FieldInfo TrafficField = typeof(PathSummary).GetField("path_traffic");

    // 車種別の速度係数(トラック/バスは遅め)。
    private static readonly Dictionary<int, float> SpeedFactor = new()
    {
        { Constants.VEHICLE_STANDARD, 1.0f },
        { Constants.VEHICLE_TRUCK, 0.8f },
        { Constants.VEHICLE_BUS, 0.8f },
        { Constants.VEHICLE_COMMERCIAL, 0.9f },
    };

    private void Awake()
    {
        if (pathManager == null) pathManager = FindAnyObjectByType<PathManager>();
        if (cameraCellChecker == null) cameraCellChecker = CameraCellChecker.Instance ?? FindAnyObjectByType<CameraCellChecker>();
        container = new GameObject("VehicleContainer").transform;
        container.SetParent(transform, false);
    }

    private void Start()
    {
        // 通行方向を city 設定から取得(取得失敗時は既定=左側通行のまま)。
        StartCoroutine(ApiClient.Get<CityEnvelope>("/city/",
            env => { if (env?.city != null) trafficHand = env.city.traffic_hand; },
            err => Debug.LogWarning($"VehicleManager: traffic_hand の取得に失敗(既定=左側通行を使用): {err}")));
    }

    private void Update()
    {
        if (pathManager == null) return;

        // ゲーム開始前は走らせない(CameraCellChecker と同じ方針)。
        if (GameStateManager.Instance != null && !GameStateManager.Instance.IsGameStarted()) return;

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        Vector3 center = GetRangeCenter();

        RefreshRangePathsIfNeeded(dt, center);
        SpawnTick(dt);
        MoveAndDespawn(dt, center);
    }

    private Vector3 GetRangeCenter()
    {
        if (cameraCellChecker != null) return cameraCellChecker.CurrentWorldPosition;
        Camera cam = Camera.main;
        if (cam != null)
        {
            var pos = cam.transform.position; pos.y = 0f; return pos;
        }
        return Vector3.zero;
    }

    // ---- 範囲内パスのスキャン ----------------------------------------------

    private void RefreshRangePathsIfNeeded(float dt, Vector3 center)
    {
        rangeScanTimer -= dt;
        if (rangeScanTimer > 0f) return;
        rangeScanTimer = rangeScanInterval;

        rangePaths.Clear();
        rangePathIds.Clear();
        targetByPathType.Clear();

        foreach (PathSummary path in pathManager.GetAllPaths())
        {
            if (path == null) continue;
            if (!IsDrivableRoad(path) || path.status != Constants.STATUS_ACTIVE) continue;
            if (!pathManager.TryGetPathPolyline(path.path_id, out var pts) || pts.Count < 2) continue;
            if (!TryComputeInsideInterval(pts, center, out float sIn, out float sOut)) continue;

            rangePathIds.Add(path.path_id);

            // 目標台数 = 物理的な同時存在台数 × 演出倍率。円外にはみ出す部分は目標に含めない。
            // 物理台数 = 流量[台/s](=交通量[台/日]÷活動秒数) × 区間通過時間[s](=範囲内の長さ÷車速)。
            // 実スケールでは1台未満になるため timeExaggeration で時間を圧縮して賑わいを出す。
            float insideLen = sOut - sIn;
            float activeSeconds = Mathf.Max(1f, activeHoursPerDay * 3600f);
            bool hasTarget = false;
            foreach (int vt in Constants.ROAD_VEHICLE_KEYS)
            {
                float traffic = GetPathTraffic(path, vt);
                if (traffic <= 0f) continue;
                float vehicleSpeed = Mathf.Max(0.1f, baseSpeed * SpeedFactorFor(vt));
                float physicalCount = (traffic / activeSeconds) * (insideLen / vehicleSpeed);
                targetByPathType[PathTypeKey(path.path_id, vt)] = physicalCount * timeExaggeration;
                hasTarget = true;
            }

            // 全車種で交通量0のパスは目標を持たないためスポーン対象から外す(範囲内判定には残す)。
            if (hasTarget) rangePaths.Add(new RangePath { pathId = path.path_id, sIn = sIn, sOut = sOut });
        }
    }

    // パス中心線のうち描画円(半径=renderRadius)の内側にある区間 [sIn, sOut] を求める。
    // 円との交点は隣接頂点の距離を線形補間して近似する。パスが円を複数回出入りする場合は
    // 「最初の進入点〜最後の退出点」で1区間に丸める(稀なケースであり演出用途では許容)。
    private bool TryComputeInsideInterval(List<Vector3> pts, Vector3 center, out float sIn, out float sOut)
    {
        float r = renderRadius;
        float acc = 0f;
        float dPrev = XZDist(pts[0], center);
        bool inside = dPrev <= r;
        sIn = inside ? 0f : -1f;
        sOut = -1f;

        for (int i = 1; i < pts.Count; i++)
        {
            float segLen = Vector3.Distance(pts[i - 1], pts[i]);
            float dCur = XZDist(pts[i], center);

            if (!inside && dCur <= r)
            {
                float denom = dPrev - dCur;
                float u = denom > 1e-4f ? Mathf.Clamp01((dPrev - r) / denom) : 0f;
                if (sIn < 0f) sIn = acc + u * segLen;
                inside = true;
            }
            else if (inside && dCur > r)
            {
                float denom = dCur - dPrev;
                float u = denom > 1e-4f ? Mathf.Clamp01((r - dPrev) / denom) : 0f;
                sOut = acc + u * segLen;
                inside = false;
            }

            acc += segLen;
            dPrev = dCur;
        }
        if (inside) sOut = acc;

        // 1m未満の掠り(円周をわずかに横切るだけ)はスポーン区間として扱わない。
        return sIn >= 0f && sOut - sIn > 1f;
    }

    // ---- スポーン ----------------------------------------------------------

    private void SpawnTick(float dt)
    {
        if (active.Count >= maxActiveVehicles) return;

        foreach (var rp in rangePaths)
        {
            foreach (int vt in Constants.ROAD_VEHICLE_KEYS)
            {
                if (!targetByPathType.TryGetValue(PathTypeKey(rp.pathId, vt), out float target)) continue;
                int actual = countByPathType.TryGetValue(PathTypeKey(rp.pathId, vt), out int c) ? c : 0;
                float deficit = target - actual;
                if (deficit <= 0f) continue;

                // 不足分を spawnFillRate[1/s] で埋める確率スポーン(フレームレート非依存のポアソン過程)。
                float p = deficit * spawnFillRate * dt;
                if (UnityEngine.Random.value < p)
                {
                    if (SpawnVehicle(rp, vt) && active.Count >= maxActiveVehicles) return;
                }
            }
        }
    }

    private bool SpawnVehicle(RangePath rp, int vehicleType)
    {
        PathSummary path = pathManager.GetPathData(rp.pathId);
        if (path == null) return false;
        if (!pathManager.TryGetPathPolyline(rp.pathId, out var pts) || pts.Count < 2) return false;

        // 走行向きは travel_direction が許す向き。双方向ならランダム。
        bool forward;
        if (path.travel_direction == Constants.DIR_FORWARD) forward = true;
        else if (path.travel_direction == Constants.DIR_BACKWARD) forward = false;
        else forward = UnityEngine.Random.value < 0.5f;

        int seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
        int variantId = VehicleModelResolver.PickVariantId(vehicleType, seed);

        var vehicle = new Vehicle
        {
            vehicleType = vehicleType,
            variantId = variantId,
            speed = baseSpeed * SpeedFactorFor(vehicleType) * UnityEngine.Random.Range(0.85f, 1.15f),
            go = AcquireInstance(vehicleType, variantId),
        };
        // スポーン位置は範囲内区間のランダムな地点(沿道の建物から出てきた車を表す)。
        vehicle.SetPath(rp.pathId, pts, forward, UnityEngine.Random.Range(rp.sIn, rp.sOut));

        PlaceVehicle(vehicle);
        active.Add(vehicle);
        IncrementCount(rp.pathId, vehicleType);
        return true;
    }

    // ---- 移動 & デスポーン -------------------------------------------------

    private void MoveAndDespawn(float dt, Vector3 center)
    {
        float despawnR2 = (renderRadius + despawnMargin) * (renderRadius + despawnMargin);

        for (int i = active.Count - 1; i >= 0; i--)
        {
            Vehicle v = active[i];
            bool reachedNode = v.Advance(dt);

            if (reachedNode && !TransitionAtNode(v))
            {
                Despawn(v, i);
                continue;
            }

            PlaceVehicle(v);

            if (XZDistSqr(v.go.transform.position, center) > despawnR2)
            {
                Despawn(v, i);
            }
        }
    }

    // 車を現在の s に基づいて配置(横オフセット + 向き)。
    private void PlaceVehicle(Vehicle v)
    {
        v.Sample(out Vector3 pos, out Vector3 tangent);

        // 進行方向に対する右ベクトル。左側通行(0)は左へ、右側通行(1)は右へ寄せる。
        Vector3 right = Vector3.Cross(Vector3.up, tangent).normalized;
        float sign = trafficHand == 0 ? -1f : 1f;
        pos += right * (sign * laneOffset);

        v.go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(tangent, Vector3.up));
    }

    // ノード到達時に次のパスへ乗り換える。false を返すとデスポーン
    // (行き止まり・乗り換え先なし、または途中降車=沿道の建物への到着)。
    private bool TransitionAtNode(Vehicle v)
    {
        PathSummary current = pathManager.GetPathData(v.pathId);
        if (current == null) return false;

        int nodeId = v.AheadNodeId(current.start_node_id, current.end_node_id);
        if (nodeId <= 0) return false;

        // 候補: ノードに接続する走行可能な道路。現パスは除外(Uターン回避)。
        List<PathSummary> connected = pathManager.GetPathsConnectedToNode(nodeId);
        float totalWeight = 0f;
        var candidates = new List<(PathSummary path, bool forward, float weight)>();

        foreach (PathSummary cand in connected)
        {
            if (cand == null || cand.path_id == v.pathId) continue;
            if (!IsDrivableRoad(cand) || cand.status != Constants.STATUS_ACTIVE) continue;

            // どちら向きに入るか: ノードが start 側なら forward、end 側なら backward。
            bool forward;
            if (cand.start_node_id == nodeId) forward = true;
            else if (cand.end_node_id == nodeId) forward = false;
            else continue;

            if (!DirectionAllowsEntry(cand.travel_direction, forward)) continue;

            float weight = GetPathTraffic(cand, v.vehicleType);
            candidates.Add((cand, forward, weight));
            totalWeight += weight;
        }

        if (candidates.Count == 0) return false;

        // 交通量のある候補があれば交通量比例の重み付きランダム、全候補が交通量0なら均等で選ぶ。
        // 交通量0の道路に微小重みで常時混ぜると袋小路への流入が累積するため、0候補は
        // 「他に行き先が無いとき」だけの逃げ道とする。
        (PathSummary path, bool forward, float weight) chosen = candidates[candidates.Count - 1];
        if (totalWeight <= 0f)
        {
            chosen = candidates[UnityEngine.Random.Range(0, candidates.Count)];
        }
        else
        {
            float pick = UnityEngine.Random.value * totalWeight;
            foreach (var c in candidates)
            {
                pick -= c.weight;
                if (pick <= 0f) { chosen = c; break; }
            }
        }

        // 途中降車: 乗り換え先が範囲内かつ目標台数を超過するなら、超過度に応じた確率で
        // 「沿道の建物に到着した」とみなしてデスポーンする。目標を持たない(交通量0の)パスは
        // 目標0として扱われ、arrivalDespawnStrength=1 ならほぼ確実に降車する。
        // この自己調整により、下流ほど交通量が減る道路では実際に車が減っていく。
        if (rangePathIds.Contains(chosen.path.path_id))
        {
            long key = PathTypeKey(chosen.path.path_id, v.vehicleType);
            float target = targetByPathType.TryGetValue(key, out float t) ? t : 0f;
            int actual = countByPathType.TryGetValue(key, out int c2) ? c2 : 0;
            if (actual + 1 > target)
            {
                float pArrive = arrivalDespawnStrength * (1f - target / (actual + 1));
                if (UnityEngine.Random.value < pArrive) return false;
            }
        }

        if (!pathManager.TryGetPathPolyline(chosen.path.path_id, out var pts) || pts.Count < 2) return false;
        DecrementCount(v.pathId, v.vehicleType);
        v.SetPath(chosen.path.path_id, pts, chosen.forward);
        IncrementCount(v.pathId, v.vehicleType);
        return true;
    }

    // ---- 台数カウンタ ------------------------------------------------------

    // (パスID, 車種) を1つのキーに畳む。車種IDは Constants.VEHICLE_* で 256 未満。
    private static long PathTypeKey(int pathId, int vehicleType) => ((long)pathId << 8) | (uint)vehicleType;

    private void IncrementCount(int pathId, int vehicleType)
    {
        long key = PathTypeKey(pathId, vehicleType);
        countByPathType[key] = countByPathType.TryGetValue(key, out int c) ? c + 1 : 1;
    }

    private void DecrementCount(int pathId, int vehicleType)
    {
        long key = PathTypeKey(pathId, vehicleType);
        if (!countByPathType.TryGetValue(key, out int c)) return;
        if (c <= 1) countByPathType.Remove(key);
        else countByPathType[key] = c - 1;
    }

    // ---- プール ------------------------------------------------------------

    private GameObject AcquireInstance(int vehicleType, int variantId)
    {
        string key = PoolKey(vehicleType, variantId);
        if (pool.TryGetValue(key, out var queue) && queue.Count > 0)
        {
            GameObject reused = queue.Dequeue();
            reused.SetActive(true);
            return reused;
        }
        GameObject created = VehicleModelResolver.CreateInstance(vehicleType, variantId, container);
        created.SetActive(true);
        return created;
    }

    private void Despawn(Vehicle v, int index)
    {
        if (v.go != null)
        {
            v.go.SetActive(false);
            string key = PoolKey(v.vehicleType, v.variantId);
            if (!pool.TryGetValue(key, out var queue))
            {
                queue = new Queue<GameObject>();
                pool[key] = queue;
            }
            queue.Enqueue(v.go);
        }
        DecrementCount(v.pathId, v.vehicleType);
        active.RemoveAt(index);
    }

    private static string PoolKey(int vehicleType, int variantId) => $"{vehicleType}:{variantId}";

    // ---- ヘルパー ----------------------------------------------------------

    private static bool IsDrivableRoad(PathSummary path)
        => path.path_type == Constants.PATH_MOTORWAY || path.path_type == Constants.PATH_STREET;

    // ノードから入る向き(forward)が travel_direction で許可されるか。
    // forward(start→end) は BOTH/FORWARD で可、backward(end→start) は BOTH/BACKWARD で可。
    private static bool DirectionAllowsEntry(int travelDirection, bool forward)
    {
        if (travelDirection == Constants.DIR_BOTH) return true;
        return forward ? travelDirection == Constants.DIR_FORWARD
                       : travelDirection == Constants.DIR_BACKWARD;
    }

    private float SpeedFactorFor(int vehicleType)
        => SpeedFactor.TryGetValue(vehicleType, out float f) ? f : 1.0f;

    // PathSummary から車種別交通量を読む。スキーマに path_traffic が未追加なら
    // utilization(集計値)を VEHICLE_STANDARD の代替として用いる(暫定 degrade)。
    private static float GetPathTraffic(PathSummary path, int vehicleType)
    {
        if (TrafficField != null)
        {
            object val = TrafficField.GetValue(path);
            if (val is Dictionary<string, float> df)
            {
                return df.TryGetValue(vehicleType.ToString(), out float f) ? f : 0f;
            }
            if (val is System.Collections.IDictionary idict)
            {
                string k = vehicleType.ToString();
                if (idict.Contains(k) && idict[k] != null) return Convert.ToSingle(idict[k]);
                return 0f;
            }
        }
        // フォールバック: 集計 utilization を標準乗用車のみに割り当てる。
        return vehicleType == Constants.VEHICLE_STANDARD ? path.utilization : 0f;
    }

    private static float XZDistSqr(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

    private static float XZDist(Vector3 a, Vector3 b) => Mathf.Sqrt(XZDistSqr(a, b));

    private void OnGUI()
    {
        if (!showDebugOverlay) return;
        GUI.Label(new Rect(10, 10, 360, 20), $"Vehicles: {active.Count} / {maxActiveVehicles}  rangePaths: {rangePaths.Count}  hand: {(trafficHand == 0 ? "L" : "R")}");
    }

    // GET /api/v1/city のレスポンス(必要フィールドのみ)。
    [Serializable]
    private class CityEnvelope { public CityInfo city; }
    [Serializable]
    private class CityInfo { public int traffic_hand; }
}
