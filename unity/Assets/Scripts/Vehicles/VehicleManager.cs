using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// カメラ周辺の道路に、path_traffic(車種別交通量)と travel_direction(走行方向)に応じて
/// 車を走らせるクライアント側シミュレータ。サーバー永続オブジェクトではなく手続き的に生成する
/// 短命オブジェクトのため、BaseApiDataManager のセル単位ロードには載せず、PathManager が既に
/// ロード済みの pathDataCache を読んで独立に動く。
///
/// 挙動: 描画範囲(カメラ注視点+半径)の境界から交通量に応じてスポーン → 交差点で交差先道路の
/// 交通量に応じて確率的に分岐(travel_direction を尊重) → 範囲外でデスポーン。
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
    [Tooltip("path_traffic 値 → 1秒あたり期待スポーン数への変換係数。実データのスケールに応じて調整。")]
    [SerializeField] private float spawnRateK = 0.02f;
    [Tooltip("同時に存在できる車の最大数(全体クランプ)。")]
    [SerializeField] private int maxActiveVehicles = 300;
    [Tooltip("流入パスの再スキャン間隔[秒]。")]
    [SerializeField] private float entryScanInterval = 0.5f;

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

    // 流入パス(範囲境界を跨ぐ道路)のキャッシュ。
    private struct EntryPath
    {
        public int pathId;
        public bool forward;   // 進行向き(true=start→end)
        public float startS;   // スポーン始点距離[m]
    }
    private readonly List<EntryPath> entryPaths = new List<EntryPath>();
    private float entryScanTimer;

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

    private const float TrafficEpsilon = 0.01f; // 交通量0の道路も分岐候補に残すための微小重み。

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

        RefreshEntryPathsIfNeeded(dt, center);
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

    // ---- 流入パスのスキャン ------------------------------------------------

    private void RefreshEntryPathsIfNeeded(float dt, Vector3 center)
    {
        entryScanTimer -= dt;
        if (entryScanTimer > 0f) return;
        entryScanTimer = entryScanInterval;

        entryPaths.Clear();
        float r2 = renderRadius * renderRadius;

        foreach (PathSummary path in pathManager.GetAllPaths())
        {
            if (path == null) continue;
            if (!IsDrivableRoad(path) || path.status != Constants.STATUS_ACTIVE) continue;
            if (!pathManager.TryGetPathPolyline(path.path_id, out var pts) || pts.Count < 2) continue;

            bool startInside = XZDistSqr(pts[0], center) <= r2;
            bool endInside = XZDistSqr(pts[pts.Count - 1], center) <= r2;
            if (startInside == endInside) continue; // 両端が同じ側 → 境界を跨がない

            // 外側の端から内側へ入る向きでスポーンさせる。
            // start が外側 → forward(start→end)、end が外側 → backward(end→start)。
            bool forward = !startInside;
            if (!DirectionAllowsEntry(path.travel_direction, forward)) continue;

            float startS = ComputeBoundaryStartS(pts, center, forward);
            entryPaths.Add(new EntryPath { pathId = path.path_id, forward = forward, startS = startS });
        }
    }

    // 描画範囲の境界(半径=renderRadius)とパスの交点までの距離[m]を求め、そこをスポーン始点にする。
    // 隣接頂点の距離を線形補間して交点を近似するため、長い直線区間でも境界上にスポーンする。
    private float ComputeBoundaryStartS(List<Vector3> pts, Vector3 center, bool forward)
    {
        var cum = BuildCumulative(pts);
        float r = renderRadius;
        int n = pts.Count;

        if (forward)
        {
            // start(外側)→end。外側→内側に変わる区間 [i-1, i] の交点。
            for (int i = 1; i < n; i++)
            {
                float d0 = XZDist(pts[i - 1], center);
                float d1 = XZDist(pts[i], center);
                if (d1 <= r)
                {
                    float denom = d0 - d1;
                    float u = denom > 1e-4f ? Mathf.Clamp01((d0 - r) / denom) : 0f;
                    return cum[i - 1] + u * (cum[i] - cum[i - 1]);
                }
            }
            return cum[n - 1];
        }
        else
        {
            // end(外側)→start。ジオメトリ順で内側→外側に変わる区間 [i, i+1] の交点。
            for (int i = n - 2; i >= 0; i--)
            {
                float d0 = XZDist(pts[i + 1], center);
                float d1 = XZDist(pts[i], center);
                if (d1 <= r)
                {
                    float denom = d0 - d1;
                    float u = denom > 1e-4f ? Mathf.Clamp01((r - d1) / denom) : 0f;
                    return cum[i] + u * (cum[i + 1] - cum[i]);
                }
            }
            return cum[0];
        }
    }

    private static List<float> BuildCumulative(List<Vector3> pts)
    {
        var cum = new List<float>(pts.Count) { 0f };
        float acc = 0f;
        for (int i = 1; i < pts.Count; i++)
        {
            acc += Vector3.Distance(pts[i - 1], pts[i]);
            cum.Add(acc);
        }
        return cum;
    }

    // ---- スポーン ----------------------------------------------------------

    private void SpawnTick(float dt)
    {
        if (active.Count >= maxActiveVehicles) return;

        foreach (var entry in entryPaths)
        {
            PathSummary path = pathManager.GetPathData(entry.pathId);
            if (path == null) continue;

            foreach (int vt in Constants.ROAD_VEHICLE_KEYS)
            {
                float traffic = GetPathTraffic(path, vt);
                if (traffic <= 0f) continue;

                float p = traffic * spawnRateK * dt; // 期待スポーン数(この frame)
                if (UnityEngine.Random.value < p)
                {
                    if (SpawnVehicle(entry, vt) && active.Count >= maxActiveVehicles) return;
                }
            }
        }
    }

    private bool SpawnVehicle(EntryPath entry, int vehicleType)
    {
        if (!pathManager.TryGetPathPolyline(entry.pathId, out var pts) || pts.Count < 2) return false;

        int seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
        int variantId = VehicleModelResolver.PickVariantId(vehicleType, seed);

        var vehicle = new Vehicle
        {
            vehicleType = vehicleType,
            variantId = variantId,
            speed = baseSpeed * SpeedFactorFor(vehicleType) * UnityEngine.Random.Range(0.85f, 1.15f),
            go = AcquireInstance(vehicleType, variantId),
        };
        vehicle.SetPath(entry.pathId, pts, entry.forward, entry.startS);

        PlaceVehicle(vehicle);
        active.Add(vehicle);
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

    // ノード到達時に次のパスへ乗り換える。乗り換え先が無ければ false(=デスポーン)。
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

            float weight = GetPathTraffic(cand, v.vehicleType) + TrafficEpsilon;
            candidates.Add((cand, forward, weight));
            totalWeight += weight;
        }

        if (candidates.Count == 0) return false;

        // 交通量に応じた重み付きランダムで分岐先を選ぶ。
        float pick = UnityEngine.Random.value * totalWeight;
        foreach (var c in candidates)
        {
            pick -= c.weight;
            if (pick <= 0f)
            {
                if (!pathManager.TryGetPathPolyline(c.path.path_id, out var pts) || pts.Count < 2) return false;
                v.SetPath(c.path.path_id, pts, c.forward);
                return true;
            }
        }

        // 数値誤差で選ばれなかった場合は最後の候補に入る。
        var last = candidates[candidates.Count - 1];
        if (!pathManager.TryGetPathPolyline(last.path.path_id, out var lastPts) || lastPts.Count < 2) return false;
        v.SetPath(last.path.path_id, lastPts, last.forward);
        return true;
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
        GUI.Label(new Rect(10, 10, 300, 20), $"Vehicles: {active.Count} / {maxActiveVehicles}  entries: {entryPaths.Count}  hand: {(trafficHand == 0 ? "L" : "R")}");
    }

    // GET /api/v1/city のレスポンス(必要フィールドのみ)。
    [Serializable]
    private class CityEnvelope { public CityInfo city; }
    [Serializable]
    private class CityInfo { public int traffic_hand; }
}
