using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using GeneratedModels;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// Port-call ships. Once per epoch, sample each active bulk port, tanker berth, container berth, and passenger terminal. For selected ports,
/// place a ship at a random point along a route from a random open-sea origin. It approaches while decelerating,
/// stops offshore, moves sideways to berth, and stays for realistic cargo-handling or port-call durations.
/// </summary>
public sealed partial class AmbientVehicleManager
{
    private sealed class PortSpec
    {
        public int variant;
        public Spec ship;
        public float minSpeed, maxSpeed;            // Offshore route speed [m/s].
        public float minStayHours, maxStayHours;    // Berth duration [h].
    }

    /// <summary>One port call: route from origin to an offshore stand-off point, then move directly sideways to the berth.</summary>
    private sealed class PortCall
    {
        public int agentId;
        public PortSpec spec;
        public SeaRoute.Hull hull;          // Berthed hull occupancy. Center is the berth position.
        public Vector2 runStart;            // Point where travel parallel to the quay begins before the stand-off point.
        public Vector2 standOff;            // Stand-off stopping point offshore from the quay.
        public Vector2[] route;             // From the origin to standOff.
        public float[] along;               // Cumulative route distances.
        public Vector2[] tangents;          // Travel direction at each route vertex.
        public float routeLength, total;    // total = routeLength + sideways berthing distance.
        public Vector2 berthDirection;      // Bow direction when berthed, parallel to the quay.
        public float cruise;                // Offshore route speed [m/s].
        public float stay;                  // Berth duration [s].
        public float s;                     // Progress from origin [m]; berthed at total.
        public float leavesAt = -1f;        // Berth-departure time (TrafficClock.Time), negative until berthed.
    }

    private sealed class CityEnvelope { public CityEpoch city; }
    private sealed class CityEpoch { public int? epoch; }

    private const float PortCallChance = .6f;           // Probability of a ship approaching an active port, sampled per port and epoch.
    private const float PortDeceleration = .005f;        // Approach deceleration [m/s²], stopping from 12 knots over about 3.6km.
    private const float PortArrivalSpeed = .25f;         // Speed just before the offshore stand-off point [m/s].
    private const float BerthingDeceleration = .0005f;   // Sideways berthing deceleration [m/s²].
    private const float BerthingSpeedMin = .05f, BerthingSpeedMax = .2f;   // Tug-assisted sideways approach speed [m/s].
    private const float FenderGap = 1.5f;                // Gap between the berthed ship's side and the quay, representing fenders [m].
    private const float StandOffMargin = 10f;            // Extra stand-off distance added to ship width [m].
    private const float BerthGap = 15f;                  // Minimum spacing between berthed ships [m].
    private const float ApproachRun = 1f;                // Parallel approach length before the stand-off point, as a multiple of ship length.
    private const float RouteMinClearance = 10f;         // Minimum route clearance from land/other ships, added to half-width [m].
    private const float RoutePreferredClearance = 80f;   // Desired route clearance from land, added to half-width [m].
    private const float RouteCellMin = 40f;              // Minimum route-grid spacing [m].
    private const float RouteCellsMax = 400f;            // Maximum route-grid points per side; larger maps use wider spacing.
    private const float RouteMaxDistance = 400f;         // Maximum measured shore distance [m], greater than the origin condition's maximum of 300m.
    private const int PortFetchLimit = 500;
    private static readonly float[] BerthShifts = { 0f, .25f, -.25f, .5f, -.5f };   // Shift along the quay from the waterfront midpoint, as a multiple of ship length.

    private readonly HashSet<int> rolledPorts = new HashSet<int>();   // Ports already sampled this epoch.
    private int? portEpoch;             // Epoch last sampled.
    private int shoreVersion;           // Last processed TerrainManager.ShoreDataVersion.
    private int portSession;            // Incremented by Clear to discard fetch results if the city changes in flight.
    private bool portRefreshing;

    private bool HasPortCallState => portEpoch.HasValue || rolledPorts.Count > 0;

    private void ResetPortCalls()
    {
        rolledPorts.Clear();
        portEpoch = null;
        portSession++;
        // Do not fetch ports until coastlines and reclaimed land are ready again after the new city loads.
        shoreVersion = TerrainManager.ShoreDataVersion;
    }

    /// <summary>Reload ports whenever coastline/reclaimed-land data is refetched: epochs, edit reloads, or city loading.</summary>
    private void UpdatePortCalls()
    {
        if (portRefreshing || shoreVersion == TerrainManager.ShoreDataVersion || !CoastlineStore.HasCoastline) return;
        StartCoroutine(RefreshPortCalls());
    }

    /// <summary>
    /// Fetch ports and epoch, resampling if the epoch advanced. Within the same epoch, remove only ships whose ports became inactive,
    /// and sample only previously unsampled ports, avoiding ship replacement on reloads such as building edits.
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
            // If coastline data is refreshed during fetching, the next update refetches once all data is ready.
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
            // Distance-field and Dijkstra costs scale with grid-point count, so calculate in the background.
            var task = BackgroundWork.Run(() =>
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
    /// Choose a berth along the port's longest waterfront and a parallel approach section. Place the hull at the waterfront midpoint,
    /// shifting along the quay if it overlaps land/ships or lacks parallel approach space. null if placement is impossible.
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

        // Treat the bounding box as the hull; ship models must not have parts protruding beyond the hull.
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
    /// Start of the parallel approach before the stand-off point. Of the two quay directions, choose the one farther from land and ships;
    /// real ships slow alongside the quay before stopping, while approaching it head-on would put the bow over land.
    /// False if neither direction can maintain shore clearance of at least need.
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

    /// <summary>Route constraints. The route follows the parallel approach section and ends at the stand-off point.</summary>
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

    /// <summary>Minimum shore distance along a segment; negative infinity if it intersects another ship expanded by margin.</summary>
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

    /// <summary>Place a routed port-call ship at a random distance along the route and sideways berthing movement.</summary>
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
        var v = new Vehicle { spec = call.spec.ship, model = model, call = call,
                              routeLength = float.PositiveInfinity, expiresAt = float.PositiveInfinity };
        PrewarmWake(v);
        PlacePortShip(v);
        vehicles.Add(v);
    }

    /// <summary>
    /// Reconstruct wake history by moving from one wake lifetime back along the journey to the current position, so ships placed mid-route
    /// do not appear without a wake. Traverse the backward distances in reverse order so the last record matches the current position.
    /// </summary>
    private void PrewarmWake(Vehicle v)
    {
        const float Step = 1f;   // [s]
        PortCall call = v.call;
        var history = new List<float> { call.s };
        for (float t = 0f; t < v.spec.wakeLife && history[history.Count - 1] > 0f; t += Step)
        {
            float s = history[history.Count - 1];
            history.Add(Mathf.Max(0f, s - PortSpeed(call, s) * Step));
        }
        float target = call.s;
        v.wake = MakeWake(v.spec, v.model, PortSpeed(call, history[history.Count - 1]));
        for (int i = history.Count - 1; v.wake != null && i >= 0; i--)
        {
            call.s = history[i];
            PlacePortShip(v);
            v.wake.Record(v.position, v.Direction, TrafficClock.Time - i * Step, Step);
        }
        call.s = target;
    }

    /// <summary>Remain for the berth duration after docking, then remove only once outside the view, never visibly.</summary>
    private void MovePortShip(Vehicle v, float dt, Vector3 camera)
    {
        PortCall call = v.call;
        if (call.s >= call.total)
        {
            if (call.leavesAt < 0f) call.leavesAt = TrafficClock.Time + call.stay;
            else if (TrafficClock.Time > call.leavesAt && (v.position - camera).sqrMagnitude > DrawDistance * DrawDistance) v.dead = true;
            return;
        }
        v.speed = PortSpeed(call, call.s);
        call.s = Mathf.Min(call.total, call.s + v.speed * dt);
        PlacePortShip(v);
    }

    /// <summary>Speed at distance s: constant approach deceleration determined by remaining distance to the stand-off point, then slower sideways berthing.</summary>
    private static float PortSpeed(PortCall call, float s) => s < call.routeLength
        ? Mathf.Clamp(Mathf.Sqrt(PortArrivalSpeed * PortArrivalSpeed + 2f * PortDeceleration * (call.routeLength - s)), PortArrivalSpeed, call.cruise)
        : Mathf.Clamp(Mathf.Sqrt(2f * BerthingDeceleration * (call.total - s)), BerthingSpeedMin, BerthingSpeedMax);

    /// <summary>Position and orientation at distance s. The route arrives parallel to the quay, so retain that heading during sideways movement.</summary>
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
