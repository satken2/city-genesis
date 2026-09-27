using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using GeneratedModels;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 入港船。稼働中のバルク港・タンカー埠頭・コンテナ埠頭・客船ターミナルごとにエポックに1回抽選し、当たった港へ
/// 外洋の無作為な地点から向かう航路の途中の無作為な位置に船を置く。船は入港の速さで減速しながら進み、
/// 岸壁の沖で止まってから横へ寄せて接岸し、実際の荷役・寄港に掛かる時間だけ留まる。仕様は公開 V 文書 §V.5。
/// </summary>
public sealed partial class AmbientVehicleManager
{
    private sealed class PortSpec
    {
        public int variant;
        public Spec ship;
        public float minSpeed, maxSpeed;            // 港外の航路の速さ [m/s]
        public float minStayHours, maxStayHours;    // 接岸している時間 [h]
    }

    /// <summary>1隻の入港。航路は出発点から岸壁の沖の停止点まで、その先は停止点から接岸位置へ真横に寄せる。</summary>
    private sealed class PortCall
    {
        public int agentId;
        public PortSpec spec;
        public SeaRoute.Hull hull;          // 接岸した船体の占有。Center が接岸位置
        public Vector2 runStart;            // 停止点の手前で岸壁と平行に進み始める点
        public Vector2 standOff;            // 岸壁の沖の停止点
        public Vector2[] route;             // 出発点から standOff まで
        public float[] along;               // route の累積距離
        public Vector2[] tangents;          // route の頂点ごとの進行方向
        public float routeLength, total;    // total = routeLength + 横寄せの距離
        public Vector2 berthDirection;      // 接岸したときの船首の向き(岸壁に平行)
        public float cruise;                // 港外の航路の速さ [m/s]
        public float stay;                  // 接岸している時間 [s]
        public float s;                     // 出発点からの進み [m]。total で接岸
        public float leavesAt = -1f;        // 停泊を終える時刻(Time.time)。接岸するまで負
    }

    private sealed class CityEnvelope { public CityEpoch city; }
    private sealed class CityEpoch { public int? epoch; }

    private const float PortCallChance = .6f;           // 稼働中の港に船が向かっている確率(港ごと・エポックごとに抽選)
    private const float PortDeceleration = .005f;        // 入港の減速度 [m/s²]。12ノットから約3.6kmかけて止まる
    private const float PortArrivalSpeed = .25f;         // 岸壁の沖で止まる直前の速さ [m/s]
    private const float BerthingDeceleration = .0005f;   // 横寄せの減速度 [m/s²]
    private const float BerthingSpeedMin = .05f, BerthingSpeedMax = .2f;   // タグボートで岸壁へ寄せる速さ [m/s]
    private const float FenderGap = 1.5f;                // 接岸した船側と岸壁の隙間(防舷材) [m]
    private const float StandOffMargin = 10f;            // 停止点を接岸位置から離す距離(船幅に足す) [m]
    private const float BerthGap = 15f;                  // 接岸した船どうしの最小の間隔 [m]
    private const float ApproachRun = 1f;                // 停止点の手前で岸壁と平行に進む距離(全長の倍率)
    private const float RouteMinClearance = 10f;         // 航路が岸・他の船から空ける最小の余裕(半幅に足す) [m]
    private const float RoutePreferredClearance = 80f;   // 航路が岸から空けたい余裕(半幅に足す) [m]
    private const float RouteCellMin = 40f;              // 航路の格子の最小間隔 [m]
    private const float RouteCellsMax = 400f;            // 航路の格子の一辺の点数の上限(大きなマップは間隔を広げる)
    private const float RouteMaxDistance = 400f;         // 岸までの距離を測る上限 [m]。出発点の条件(最大300m)より大きくする
    private const int PortFetchLimit = 500;
    private static readonly float[] BerthShifts = { 0f, .25f, -.25f, .5f, -.5f };   // 水際の中点から岸壁に沿ってずらす量(全長の倍率)

    private readonly HashSet<int> rolledPorts = new HashSet<int>();   // 今のエポックで抽選済みの港
    private int? portEpoch;             // 抽選したエポック
    private int shoreVersion;           // 処理済みの TerrainManager.ShoreDataVersion
    private int portSession;            // Clear で進む。取得中に都市が切り替わった結果を捨てる
    private bool portRefreshing;

    private bool HasPortCallState => portEpoch.HasValue || rolledPorts.Count > 0;

    private void ResetPortCalls()
    {
        rolledPorts.Clear();
        portEpoch = null;
        portSession++;
        // 次に海岸線・埋立地が揃う(新しい都市を読み終える)まで港を読まない。
        shoreVersion = TerrainManager.ShoreDataVersion;
    }

    /// <summary>海岸線・埋立地を取り直すたび(エポック・編集の再読込・都市の読み込み)に港を読み直す。</summary>
    private void UpdatePortCalls()
    {
        if (portRefreshing || shoreVersion == TerrainManager.ShoreDataVersion || !CoastlineStore.HasCoastline) return;
        StartCoroutine(RefreshPortCalls());
    }

    /// <summary>
    /// 港の一覧とエポックを読み、エポックが進んでいれば抽選をやり直す。同じエポックの中では稼働中でなくなった港の船だけを消し、
    /// まだ抽選していない港だけを抽選する(建物の編集などの再読込で船が入れ替わらない)。
    /// </summary>
    private IEnumerator RefreshPortCalls()
    {
        portRefreshing = true;
        int session = portSession;
        int version = TerrainManager.ShoreDataVersion;
        shoreVersion = version;
        try
        {
            int? epoch = null;
            yield return ApiClient.Get<CityEnvelope>("/city/", response => epoch = response?.city?.epoch, _ => { });
            bool failed = epoch == null;
            var ports = new List<(BuildingSummary building, PortSpec spec)>();
            foreach (PortSpec spec in PortSpecs)
            {
                if (failed) break;
                BuildingsListResponse page = null;
                string filter = Uri.EscapeDataString($"[variant][eq]{spec.variant}");
                yield return ApiClient.GetBulk<BuildingsListResponse>($"/buildings/?filter={filter}&limit={PortFetchLimit}",
                    value => page = value, _ => { }, timingLabel: "AmbientVehicleManager ports");
                if (page?.buildings == null) { failed = true; break; }
                foreach (BuildingSummary building in page.buildings)
                    if (building != null && building.status == Constants.STATUS_ACTIVE) ports.Add((building, spec));
            }
            // 取得中に海岸線を取り直した場合は、揃った状態で次の更新が読み直す。
            if (failed || session != portSession || version != TerrainManager.ShoreDataVersion) yield break;

            if (portEpoch != epoch)
            {
                RemoveVehicles(v => v.call != null);
                rolledPorts.Clear();
                portEpoch = epoch;
            }
            var active = new HashSet<int>();
            foreach (var port in ports) active.Add(port.building.agent_id);
            RemoveVehicles(v => v.call != null && !active.Contains(v.call.agentId));
            rolledPorts.RemoveWhere(id => !active.Contains(id));

            var hulls = new List<SeaRoute.Hull>();
            foreach (var v in vehicles) if (v.call != null) hulls.Add(v.call.hull);
            int existing = hulls.Count;
            var calls = new List<PortCall>();
            foreach (var (building, spec) in ports)
            {
                if (!rolledPorts.Add(building.agent_id) || Random.value >= PortCallChance) continue;
                PortCall call = PlanBerth(building, spec, hulls);
                if (call == null) continue;
                calls.Add(call);
                hulls.Add(call.hull);
            }
            if (calls.Count == 0) yield break;

            var rings = new Vector2[CoastlineStore.Rings.Count][];
            for (int i = 0; i < rings.Length; i++) rings[i] = CoastlineStore.Rings[i];
            var requests = new SeaRoute.Request[calls.Count];
            for (int i = 0; i < calls.Count; i++)
            {
                var blockers = new List<SeaRoute.Hull>(hulls);
                blockers.RemoveAt(existing + i);
                requests[i] = CreateRouteRequest(calls[i], blockers.ToArray(), rings);
            }
            float width = terrain.GetMapWidth(), height = terrain.GetMapHeight();
            float cell = Mathf.Max(RouteCellMin, Mathf.Max(width, height) / RouteCellsMax);
            // 距離場とダイクストラは格子の点数に比例して重いので、バックグラウンドで求める。
            var task = Task.Run(() =>
            {
                var grid = new SeaRoute.Grid(-width * .5f, -height * .5f, width * .5f, height * .5f, cell, RouteMaxDistance);
                var routes = new Vector2[requests.Length][];
                for (int i = 0; i < requests.Length; i++) routes[i] = SeaRoute.Plan(grid, requests[i]);
                return routes;
            });
            yield return Utils.WaitTask(task);
            if (!task.IsCompletedSuccessfully || session != portSession || portEpoch != epoch) yield break;
            for (int i = 0; i < calls.Count; i++)
                if (task.Result[i] != null) Launch(calls[i], task.Result[i]);
        }
        finally
        {
            portRefreshing = false;
        }
    }

    /// <summary>
    /// 港の最も長い水際に船を横付けする位置と、その手前で岸壁と平行に進む区間を決める。船体は水際の中点に置き、
    /// 陸に掛かる・他の船と重なる・平行に進む区間が取れないなら岸壁に沿ってずらす。置けなければ null。
    /// </summary>
    private PortCall PlanBerth(BuildingSummary building, PortSpec spec, List<SeaRoute.Hull> others)
    {
        PropRenderModel model = GetModel(spec.ship.model);
        if (model == null) return null;
        PortLayout.Waterline quay = default;
        float quayLength = 0f;
        foreach (PortLayout.Waterline waterline in PortBuildingMeshBuilder.ResolveWorldWaterlines(building))
        {
            float length = (waterline.Q - waterline.P).magnitude;
            if (length > quayLength) { quayLength = length; quay = waterline; }
        }
        if (quayLength < 1f) return null;

        float beam = model.bounds.size.x, shipLength = model.bounds.size.z;
        Vector2 along = (quay.Q - quay.P) / quayLength, normal = quay.Normal.normalized;
        Vector2 middle = (quay.P + quay.Q) * .5f + normal * (beam * .5f + FenderGap);
        float need = beam * .5f + RouteMinClearance;
        foreach (float shift in BerthShifts)
        {
            var hull = new SeaRoute.Hull { Center = middle + along * (shift * shipLength), Axis = along, HalfLength = shipLength * .5f, HalfWidth = beam * .5f };
            if (!IsOnWater(hull) || others.Exists(other => other.Overlaps(hull, BerthGap))) continue;
            Vector2 standOff = hull.Center + normal * (beam + StandOffMargin);
            if (!TryApproachRun(standOff, along, shipLength * ApproachRun, others, need, out Vector2 runStart)) continue;
            return new PortCall
            {
                agentId = building.agent_id,
                spec = spec,
                hull = hull,
                runStart = runStart,
                standOff = standOff,
                cruise = Random.Range(spec.minSpeed, spec.maxSpeed),
                stay = Random.Range(spec.minStayHours, spec.maxStayHours) * 3600f,
            };
        }
        return null;
    }

    /// <summary>
    /// 停止点の手前で岸壁と平行に進む区間の始点。岸壁の両向きのうち、岸と他の船から遠い向きを選ぶ
    /// (実船は岸壁に沿って行き足を落としてから止まる。正面から岸壁へ向かうと船首が岸に掛かる)。
    /// どちら向きも岸から need 以上離れられなければ false。
    /// </summary>
    private static bool TryApproachRun(Vector2 standOff, Vector2 along, float length, List<SeaRoute.Hull> others, float need, out Vector2 runStart)
    {
        runStart = standOff;
        float best = need;
        bool found = false;
        foreach (float sign in new[] { 1f, -1f })
        {
            Vector2 start = standOff - along * (sign * length);
            float clearance = RunClearance(start, standOff, others, need);
            if (clearance < best) continue;
            best = clearance;
            runStart = start;
            found = true;
        }
        return found;
    }

    private static bool IsOnWater(SeaRoute.Hull hull)
    {
        foreach (Vector2 p in hull.Outline(10f))
            if (CoastlineStore.IsLand(p.x, p.y)) return false;
        return true;
    }

    /// <summary>航路の条件。航路は平行に進む区間を通って停止点で終わる。</summary>
    private static SeaRoute.Request CreateRouteRequest(PortCall call, SeaRoute.Hull[] blockers, Vector2[][] rings)
    {
        float halfWidth = call.hull.HalfWidth, shipLength = call.hull.HalfLength * 2f;
        float need = halfWidth + RouteMinClearance;
        return new SeaRoute.Request
        {
            approach = new[] { call.runStart, call.standOff },
            minClearance = need,
            preferredClearance = halfWidth + RoutePreferredClearance,
            startClearance = shipLength * .6f + 150f,
            cornerTrim = shipLength * 2f,
            blockers = blockers,
            blockerMargin = need,
            rings = rings,
            seed = Random.Range(int.MinValue, int.MaxValue),
        };
    }

    /// <summary>線分上の岸までの距離の最小値。他の船(margin だけ広げる)に掛かるなら負の無限大。</summary>
    private static float RunClearance(Vector2 from, Vector2 to, List<SeaRoute.Hull> blockers, float margin)
    {
        float min = float.PositiveInfinity;
        for (int s = 0; s <= 8; s++)
        {
            Vector2 p = Vector2.Lerp(from, to, s / 8f);
            foreach (SeaRoute.Hull blocker in blockers)
                if (blocker.Contains(p, margin)) return float.NegativeInfinity;
            min = Mathf.Min(min, Clearance(new Vector3(p.x, 0f, p.y)));
        }
        return min;
    }

    /// <summary>航路が決まった入港船を、航路と横寄せを通した道のりの無作為な位置に置く。</summary>
    private void Launch(PortCall call, Vector2[] route)
    {
        PropRenderModel model = GetModel(call.spec.ship.model);
        if (model == null) return;
        int n = route.Length;
        call.route = route;
        call.along = new float[n];
        for (int i = 1; i < n; i++) call.along[i] = call.along[i - 1] + Vector2.Distance(route[i - 1], route[i]);
        call.tangents = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            Vector2 back = i > 0 ? (route[i] - route[i - 1]).normalized : Vector2.zero;
            Vector2 ahead = i < n - 1 ? (route[i + 1] - route[i]).normalized : Vector2.zero;
            Vector2 tangent = back + ahead;
            call.tangents[i] = tangent.sqrMagnitude > 1e-6f ? tangent.normalized : (ahead.sqrMagnitude > 0f ? ahead : back);
        }
        call.berthDirection = (call.standOff - call.runStart).normalized;
        call.routeLength = call.along[n - 1];
        call.total = call.routeLength + Vector2.Distance(call.standOff, call.hull.Center);
        call.s = Random.Range(0f, call.total);
        var v = new Vehicle { spec = call.spec.ship, model = model, trail = MakeTrail(call.spec.ship), call = call,
                              routeLength = float.PositiveInfinity, expiresAt = float.PositiveInfinity };
        PlacePortShip(v);
        vehicles.Add(v);
    }

    /// <summary>
    /// 航路では岸壁の沖の停止点までの残りで決まる一定の減速度で行き足を落とし、横寄せはさらに遅く寄せる。
    /// 接岸したら停泊時間だけ留まり、過ぎたら描画範囲の外にある時点で消す(目の前では消さない)。
    /// </summary>
    private void MovePortShip(Vehicle v, float dt, Vector3 camera)
    {
        PortCall call = v.call;
        if (call.s >= call.total)
        {
            if (call.leavesAt < 0f) call.leavesAt = Time.time + call.stay;
            else if (Time.time > call.leavesAt && (v.position - camera).sqrMagnitude > DrawDistance * DrawDistance) v.dead = true;
            return;
        }
        v.speed = call.s < call.routeLength
            ? Mathf.Clamp(Mathf.Sqrt(PortArrivalSpeed * PortArrivalSpeed + 2f * PortDeceleration * (call.routeLength - call.s)), PortArrivalSpeed, call.cruise)
            : Mathf.Clamp(Mathf.Sqrt(2f * BerthingDeceleration * (call.total - call.s)), BerthingSpeedMin, BerthingSpeedMax);
        call.s = Mathf.Min(call.total, call.s + v.speed * dt);
        PlacePortShip(v);
    }

    /// <summary>道のり s の位置と向き。航路は岸壁と平行に進んで停止点に着くので、横寄せの間は船首をその向きのまま保つ。</summary>
    private static void PlacePortShip(Vehicle v)
    {
        PortCall call = v.call;
        Vector2 position, direction;
        if (call.s < call.routeLength)
        {
            int i = Array.BinarySearch(call.along, call.s);
            if (i < 0) i = ~i - 1;
            i = Mathf.Clamp(i, 0, call.route.Length - 2);
            float span = call.along[i + 1] - call.along[i];
            float t = span > 1e-4f ? (call.s - call.along[i]) / span : 0f;
            position = Vector2.Lerp(call.route[i], call.route[i + 1], t);
            direction = Vector2.Lerp(call.tangents[i], call.tangents[i + 1], t);
        }
        else
        {
            float lateral = call.total - call.routeLength;
            float t = lateral > 1e-4f ? Mathf.Clamp01((call.s - call.routeLength) / lateral) : 1f;
            position = Vector2.Lerp(call.standOff, call.hull.Center, t);
            direction = call.berthDirection;
        }
        v.position = new Vector3(position.x, 0f, position.y);
        if (direction.sqrMagnitude > 1e-6f) v.heading = Mathf.Atan2(direction.x, direction.y) * Mathf.Rad2Deg;
    }
}
