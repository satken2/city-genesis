using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
    /// Station/stop passenger animation. Convert each stop's daily boardings from line summaries to boardings per second by dividing by active time,
/// and assume equal alightings: return trips generally use the same stop, making daily boardings and alightings approximately equal.
/// Thin counts using the same display multiplier/cap ratio as pedestrians. Entities do not correspond to citizens or entities in other scenes.
/// - Platforms (stations/LRT): waiting count is boardings per second x time since previous departure, derived only from the line clock without state.
///   During the first part of a stop, alighting passengers walk from doors to the platform entrance where the station passage crosses it,
///   or to the train end if absent, then disappear. Waiting passengers walk to doors and disappear.
/// - Bus stops: passing pedestrians queue beside the shelter with probability boardings / road pedestrian traffic, then walk to stopped-bus doors and disappear.
///   Alighting passengers continue as ordinary pedestrians.
/// - Station buildings: the entrance is the perpendicular projection from the building end to the nearest pedestrian road, matching core station exits. Passing pedestrians
///   enter and disappear with probability station boardings / road pedestrian traffic; passengers leave for the sidewalk at the same rate as station boardings.
/// Do not render underground stops.
/// </summary>
public sealed partial class PedestrianManager
{
    private const float SlotSpacing = .9f;                // Spacing between waiting positions (m).
    private const float DoorSpacing = 5f;                 // Door count per vehicle is rounded vehicle length divided by this value.
    private const float BusHalfWidth = 1.25f;             // Distance to the bus side where doors are placed (m).
    private const float PlatformBackset = .5f;            // Minimum distance from platform edge to waiting passengers (m).
    private const float AlightSpan = .35f;                // Initial fraction of dwell time allocated to alighting.
    private const float BoardFrom = .3f, BoardTo = .7f;   // Boarding-start time range as a fraction of dwell time.
    private const float AlighterLife = 180f;              // Maximum alighting-passenger rendering duration (s).

    private float displayScale = 1;   // Displayed population fraction relative to traffic: display multiplier x cap thinning.
    private int transitGeneration;
    private float gateClock;          // Pedestrian clock counting station-building exit intervals (s).
    private readonly Dictionary<long, TransitStop> transitStops = new Dictionary<long, TransitStop>();
    private readonly Dictionary<long, Gate> houseGates = new Dictionary<long, Gate>();
    private readonly Dictionary<int, List<Gate>> gatesByPath = new Dictionary<int, List<Gate>>();
    private readonly Dictionary<int, StopLine> stopLines = new Dictionary<int, StopLine>();
    private readonly List<double> trainPhases = new List<double>();

    /// <summary>One stop on one line. Only bus stops store queues; platform passengers are derived from time on demand.</summary>
    private sealed class TransitStop
    {
        public int line, node, path, signature, seen;
        public bool bus, seeded;
        public float rate;                                  // Boardings per second, in displayed people.
        public readonly List<StopVisit> visits = new List<StopVisit>();
        public Slot[] slots;                                // Bus-stop waiting positions, nearest to the shelter first.
        public Gate gate;
        public readonly List<Rider> riders = new List<Rider>();
        public readonly List<(double time, Vector3 door)> alighting = new List<(double, Vector3)>();
    }

    /// <summary>
    /// One arrival at a stop. A line visiting the same stop twice per circuit has two entries with boardings split equally.
    /// Positions derive from the stopped train's vehicle layout (TransitVehicleManager.TrySampleStop).
    /// </summary>
    private sealed class StopVisit
    {
        public TransitMotionClock.StopArrival arrival;
        public Vector3[] doors, exits;
        public Slot[] slots;                                // Platform waiting positions, filled in order.
        public int[] order, slotDoor, slotExit, doorExit;
        public double lastArrival = double.NegativeInfinity;   // Arrival time for which bus-stop alighting passengers were spawned.
    }

    private struct Slot { public Vector3 position, facing; }

    /// <summary>
    /// Bus-stop queue member. Walk from from to the waiting position; on boarding, walk from boardStart to door and disappear.
    /// Times use the line clock; remove after leave, the bus departure time.
    /// </summary>
    private sealed class Rider
    {
        public Vector3 from, slot, facing, boardFrom, door;
        public double start, appear, boardStart, leave;
        public float speed, boardSpeed, walked;
        public int variant, index;
        public bool boarding;
    }

    /// <summary>
    /// Sidewalk entrance entered by passing pedestrians with probability. Station passengers walk to inside and disappear; bus-stop passengers join stop's queue.
    /// Station-building exits spawn at rate people/s onto sidewalk side.
    /// </summary>
    private sealed class Gate
    {
        public int path, side, seen;
        public float s, probability, rate, next = -1;
        public Vector3 inside;
        public TransitStop stop;
    }

    private sealed class StopLine { public long revision; public VehiclePolyline line; public int seen; }

    /// <summary>
    /// Platform dimensions: edge is the distance from vehicle/track center to the door line at the platform edge; band is the waiting area beyond that edge.
    /// Island platforms lie inside the tracks; others outside vehicles. Single-track stations fix the side to the platform projection (fixedSide).
    /// </summary>
    private struct Platform
    {
        public float edge, surface, nearBand, farBand;
        public bool island;
        public int fixedSide;
    }

    private void ClearTransit()
    {
        transitStops.Clear(); houseGates.Clear(); gatesByPath.Clear(); stopLines.Clear();
    }

    /// <summary>
    /// Recollect stops and station entrances in the management area. Rebuild positions only when underlying path, stopping step, or train length changes;
    /// discard out-of-range stops together with their queues. Station boardings include platforms outside the area.
    /// </summary>
    private void ScanTransit(Vector3 center)
    {
        int generation = ++transitGeneration;
        var transit = TransitVehicleManager.Instance;
        var store = TransitDataStore.Instance;
        float radius = renderRadius + despawnMargin;
        float dailyToRate = displayScale / Mathf.Max(1, activeHoursPerDay * 3600);   // Convert daily counts to displayed people per second.
        var houseRidership = new Dictionary<int, float>();
        if (transit != null && store != null)
            foreach (var data in store.Lines)
            {
                if (data?.stop_points == null || !transit.TryGetLine(data.line_agent_id, out var motion, out int cars, out float carLength)) continue;
                bool bus = VehicleModelConfig.GetTransitVehicleType(data.path_variant) == Constants.VEHICLE_BUS;
                foreach (var point in data.stop_points)
                {
                    if (point?.station_path_id == null || point.fraction == null || !(point.ridership > 0)) continue;
                    var underlay = pathManager.GetPathData(point.station_path_id.Value);
                    if (underlay == null || underlay.status != Constants.STATUS_ACTIVE) continue;
                    float ridership = point.ridership.Value;
                    if (underlay.house_path_id is int house)
                        houseRidership[house] = (houseRidership.TryGetValue(house, out float sum) ? sum : 0) + ridership;
                    var node = pathManager.GetNodeData(point.node_id);
                    if (node == null || Utils.XZDistanceSquared(node.PositionAsVector3(), center) > radius * radius) continue;
                    var line = StopPolyline(underlay.path_id, generation);
                    if (line == null) continue;
                    line.Sample(point.fraction.Value * line.Length, true, out var position, out _);
                    if (PathTunnelParts.IsTunnel(underlay, position)) continue;
                    long key = ((long)data.line_agent_id << 32) | (uint)point.node_id;
                    if (!transitStops.TryGetValue(key, out var stop))
                        transitStops[key] = stop = new TransitStop { line = data.line_agent_id, node = point.node_id, bus = bus };
                    int signature = StopSignature(stop, motion, underlay, cars, carLength, line);
                    if (stop.signature != signature || stop.visits.Count == 0)
                    {
                        stop.signature = signature;
                        if (!BuildStop(stop, transit, motion, underlay, cars, carLength, line)) { stop.visits.Clear(); continue; }
                    }
                    else RefreshArrivals(stop, motion);
                    stop.rate = ridership * dailyToRate;
                    if (stop.gate != null) stop.gate.probability = JoinProbability(ridership, underlay);
                    if (stop.bus && !stop.seeded) SeedBusStop(stop, transit, motion);
                    stop.seen = generation;
                }
            }

        foreach (var data in pathManager.GetPathsNearCircle(center, radius))
        {
            if (data.path_type != Constants.PATH_STATION_HOUSE || data.status != Constants.STATUS_ACTIVE
                || !houseRidership.TryGetValue(data.path_id, out float ridership)) continue;
            var anchors = StationHousePath.PathAnchorPoints2D(data);
            var footprints = StationHousePath.ComputeEndFootprints(data);
            if (anchors.Count < 2) continue;
            foreach (var footprint in footprints)
            {
                Vector2 tip = footprint.atEnd ? anchors[anchors.Count - 1] : anchors[0];
                if (!NearestWalk(new Vector3(tip.x, 0, tip.y), out var walk, out float s)) continue;
                long key = ((long)data.path_id << 1) | (footprint.atEnd ? 1L : 0L);
                if (!houseGates.TryGetValue(key, out var gate)) houseGates[key] = gate = new Gate();
                walk.line.Sample(s, true, out var axis, out var tangent);
                var inside = new Vector3(footprint.center.x, 0, footprint.center.y);
                inside.y = Utils.GetGroundHeight(new Vector3(inside.x, 1000f, inside.z));
                gate.path = walk.data.path_id; gate.s = s; gate.inside = inside;
                gate.side = Vector3.Dot(inside - axis, Vector3.Cross(Vector3.up, tangent)) < 0 ? -1 : 1;
                gate.probability = JoinProbability(ridership / footprints.Count, walk.data);
                gate.rate = ridership / footprints.Count * dailyToRate;
                gate.seen = generation;
            }
        }

        var stale = new List<long>();
        foreach (var entry in transitStops) if (entry.Value.seen != generation) stale.Add(entry.Key);
        foreach (long key in stale) transitStops.Remove(key);
        stale.Clear();
        foreach (var entry in houseGates) if (entry.Value.seen != generation) stale.Add(entry.Key);
        foreach (long key in stale) houseGates.Remove(key);
        var unused = new List<int>();
        foreach (var entry in stopLines) if (entry.Value.seen != generation) unused.Add(entry.Key);
        foreach (int id in unused) stopLines.Remove(id);
        gatesByPath.Clear();
        foreach (var stop in transitStops.Values) if (stop.gate != null) IndexGate(stop.gate);
        foreach (var gate in houseGates.Values) IndexGate(gate);
    }

    private void IndexGate(Gate gate)
    {
        if (!gatesByPath.TryGetValue(gate.path, out var list)) gatesByPath[gate.path] = list = new List<Gate>();
        list.Add(gate);
    }

    /// <summary>Uncut centerline beneath a stop. Multiply stopping fractions by this polyline's length, matching TransitRouteGeometry.</summary>
    private VehiclePolyline StopPolyline(int pathId, int generation)
    {
        long revision = pathManager.GetPathGeometryRevision(pathId);
        if (stopLines.TryGetValue(pathId, out var cached) && revision != -1 && cached.revision == revision)
        {
            cached.seen = generation;
            return cached.line;
        }
        if (!pathManager.TryGetPathPolyline(pathId, out var points, false) || points.Count < 2) return null;
        var line = new VehiclePolyline(points);
        stopLines[pathId] = new StopLine { revision = revision, line = line, seen = generation };
        return line;
    }

    /// <summary>Revision of inputs determining stop position. Line lists refetch every few seconds; do not rebuild unchanged contents.</summary>
    private int StopSignature(TransitStop stop, TransitMotionClock motion, PathSummary underlay, int cars, float carLength, VehiclePolyline line)
    {
        unchecked
        {
            int hash = line.ShapeHash * 31 + underlay.GetHashCode();
            hash = (hash * 31 + cars) * 31 + carLength.GetHashCode();
            foreach (var arrival in motion.Arrivals)
            {
                if (arrival.Stop != stop.node) continue;
                var step = motion.Steps[arrival.Step];
                hash = ((hash * 31 + arrival.Step) * 31 + step.path_id) * 31 + step.from_fraction.GetHashCode();
                hash = hash * 31 + step.to_fraction.GetHashCode();
            }
            if (underlay.house_path_id is int house) hash = hash * 31 + (pathManager.GetPathData(house)?.GetHashCode() ?? 0);
            if (stop.bus && walks.TryGetValue(underlay.path_id, out var walk)) hash = hash * 31 + walk.line.ShapeHash;
            return hash;
        }
    }

    /// <summary>Keep positions unchanged and update only arrival times to the latest line clock.</summary>
    private static void RefreshArrivals(TransitStop stop, TransitMotionClock motion)
    {
        int index = 0;
        foreach (var arrival in motion.Arrivals)
            if (arrival.Stop == stop.node && index < stop.visits.Count) stop.visits[index++].arrival = arrival;
    }

    /// <summary>Determine doors, waiting positions, and entrances per arrival. False without an underlying platform or loaded route geometry.</summary>
    private bool BuildStop(TransitStop stop, TransitVehicleManager transit, TransitMotionClock motion, PathSummary underlay,
        int cars, float carLength, VehiclePolyline line)
    {
        stop.visits.Clear();
        stop.path = underlay.path_id;
        if (!ResolvePlatform(stop.bus, underlay, out var platform)) return false;
        var positions = new Vector3[cars];
        var directions = new Vector3[cars];
        foreach (var arrival in motion.Arrivals)
        {
            if (arrival.Stop != stop.node) continue;
            if (!transit.TrySampleStop(stop.line, arrival.Step, positions, directions)) return false;
            stop.visits.Add(BuildVisit(stop, stop.visits.Count, arrival, platform, underlay, line, positions, directions, carLength));
        }
        return stop.visits.Count > 0 && (!stop.bus || BuildBusStop(stop, underlay));
    }

    private static bool ResolvePlatform(bool bus, PathSummary underlay, out Platform platform)
    {
        platform = new Platform { nearBand = PlatformBackset };
        if (bus)
        {
            platform.edge = BusHalfWidth;
            return true;
        }
        if (LrtRoadParts.IsLrt(underlay.path_variant))
        {
            platform.edge = LrtRoadParts.PLATFORM_EDGE_GAP;
            platform.surface = LrtRoadParts.PlatformSurfaceAboveSpline(underlay.path_variant);
            platform.farBand = Mathf.Max(platform.nearBand, LrtRoadParts.PlatformWidth(underlay.path_variant) * .5f - .3f);
            return true;
        }
        TrackPath.GetTrackMasterParams(underlay, out float width, out int lanes, out _, out _, out _, out _, out _, out float platformWidth);
        if (platformWidth <= 0) return false;
        float centerWidth = TrackPath.GetTrackCenterWidth(underlay);
        platform.island = TrackPath.IsIslandPlatform(underlay);
        // Limit waiting passengers to half the platform width. Island tracks share halves, and opposing single-track platforms may merge into a narrower slab.
        float deck = platform.island ? 2 * TrackPath.IslandEdgeDistance(width, lanes, centerWidth)
            : width * .5f + platformWidth - TrackPath.PlatformEdgeDistance(width, lanes, centerWidth);
        TrackPath.ResolvePlatformExtents(underlay, platformWidth, out _, out float right);
        platform.fixedSide = lanes >= 2 ? 0 : right > 0 ? 1 : -1;
        platform.edge = TrackPath.PLATFORM_EDGE_OFFSET;
        platform.surface = RoadPartHeights.PLATFORM_SURFACE_ABOVE_SPLINE;
        platform.farBand = Mathf.Max(platform.nearBand, deck * .5f - .3f);
        return true;
    }

    /// <summary>
    /// Place doors on each stopped vehicle's platform-side edge and waiting positions within band beyond the edge.
    /// Vehicle centers lie on tracks or bus lanes; height is underlying centerline plus platform-surface height, or road surface for buses.
    /// </summary>
    private StopVisit BuildVisit(TransitStop stop, int index, TransitMotionClock.StopArrival arrival, Platform platform, PathSummary underlay,
        VehiclePolyline line, Vector3[] positions, Vector3[] directions, float carLength)
    {
        int cars = positions.Length;
        line.Sample(line.Project(positions[cars / 2]), true, out var axis, out var tangent);
        float lateral = Vector3.Dot(positions[cars / 2] - axis, Vector3.Cross(Vector3.up, tangent).normalized);
        int sign = platform.fixedSide != 0 ? platform.fixedSide : (lateral < 0 ? -1 : 1) * (platform.island ? -1 : 1);
        int doorsPerCar = Mathf.Max(1, Mathf.RoundToInt(carLength / DoorSpacing));
        int along = Mathf.Max(1, Mathf.FloorToInt((carLength - .6f) / SlotSpacing));
        int rows = Mathf.Max(1, Mathf.FloorToInt((platform.farBand - platform.nearBand) / .8f) + 1);
        float middle = platform.edge + (platform.nearBand + platform.farBand) * .5f;
        var centers = new Vector3[cars];
        var forwards = new Vector3[cars];
        var sides = new Vector3[cars];
        var doors = new List<Vector3>();
        var slots = new List<Slot>();
        for (int car = 0; car < cars; car++)
        {
            line.Sample(line.Project(positions[car]), true, out var point, out var direction);
            sides[car] = Vector3.Cross(Vector3.up, direction).normalized * sign;
            forwards[car] = new Vector3(directions[car].x, 0, directions[car].z).normalized;
            centers[car] = new Vector3(positions[car].x, stop.bus ? positions[car].y : point.y + platform.surface, positions[car].z);
            for (int d = 0; d < doorsPerCar; d++)
                doors.Add(centers[car] + forwards[car] * (((d + .5f) / doorsPerCar - .5f) * carLength) + sides[car] * platform.edge);
            if (stop.bus) continue;
            for (int a = 0; a < along; a++)
                for (int r = 0; r < rows; r++)
                {
                    int k = slots.Count;
                    float across = rows == 1 ? (platform.nearBand + platform.farBand) * .5f : Mathf.Lerp(platform.nearBand, platform.farBand, r / (rows - 1f));
                    Vector3 position = centers[car] + forwards[car] * ((a - (along - 1) * .5f + (Hash01(stop.node, index, k) - .5f) * .5f) * SlotSpacing)
                        + sides[car] * (platform.edge + across);
                    var facing = Quaternion.AngleAxis((Hash01(stop.node, ~index, k) - .5f) * 50f, Vector3.up) * -sides[car];
                    slots.Add(new Slot { position = position, facing = facing });
                }
        }
        var visit = new StopVisit { arrival = arrival, doors = doors.ToArray() };
        if (stop.bus) return visit;

        var exits = new List<Vector3>();
        var house = underlay.house_path_id is int id ? pathManager.GetPathData(id) : null;
        if (house != null && house.status == Constants.STATUS_ACTIVE) PassageExits(house, line, lateral + sign * middle, platform.surface, exits);
        if (exits.Count == 0)
        {
            exits.Add(centers[0] + forwards[0] * (carLength * .5f) + sides[0] * middle);
            exits.Add(centers[cars - 1] - forwards[cars - 1] * (carLength * .5f) + sides[cars - 1] * middle);
        }
        visit.slots = slots.ToArray();
        visit.exits = exits.ToArray();
        visit.order = new int[visit.slots.Length];
        visit.slotDoor = new int[visit.slots.Length];
        visit.slotExit = new int[visit.slots.Length];
        var keys = new float[visit.slots.Length];
        for (int k = 0; k < visit.slots.Length; k++)
        {
            visit.order[k] = k;
            keys[k] = Hash01(~stop.node, index, k);
            visit.slotDoor[k] = Nearest(visit.doors, visit.slots[k].position);
            visit.slotExit[k] = Nearest(visit.exits, visit.slots[k].position);
        }
        System.Array.Sort(keys, visit.order);
        visit.doorExit = new int[visit.doors.Length];
        for (int d = 0; d < visit.doors.Length; d++) visit.doorExit[d] = Nearest(visit.exits, visit.doors[d]);
        return visit;
    }

    /// <summary>
    /// Use the station passage's straight anchor-to-anchor intersection with the platform centerline as the platform entrance or stairs.
    /// offset is its lateral position toward the waiting-band midpoint in the underlying path's geometric frame.
    /// </summary>
    private static void PassageExits(PathSummary house, VehiclePolyline line, float offset, float surface, List<Vector3> exits)
    {
        var anchors = StationHousePath.PathAnchorPoints2D(house);
        for (int i = 1; i < anchors.Count; i++)
        {
            var a = new Vector3(anchors[i - 1].x, 0, anchors[i - 1].y);
            var b = new Vector3(anchors[i].x, 0, anchors[i].y);
            for (int j = 1; j < line.points.Length; j++)
            {
                if (!RoadShapeGeometry.SegmentsIntersectXZ(a, b, line.points[j - 1], line.points[j], out float t, out _)) continue;
                line.Sample(line.Project(Vector3.Lerp(a, b, t)), true, out var point, out var tangent);
                exits.Add(point + Vector3.Cross(Vector3.up, tangent).normalized * offset + Vector3.up * surface);
            }
        }
    }

    /// <summary>Arrange waiting positions on the sidewalk beside the shelter, with an entrance in front. Resolve shelter position as BusStopManager does.</summary>
    private bool BuildBusStop(TransitStop stop, PathSummary road)
    {
        var node = pathManager.GetNodeData(stop.node);
        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(road.path_variant) : null;
        if (!walks.TryGetValue(road.path_id, out var walk) || walk.centered || node == null || master == null
            || !pathManager.TryGetPathPolyline(road.path_id, out var points, false)
            || !BusStopManager.TryPlace(node.GetAbsolutePosition(), points, master.width, master.sidewalk_width ?? 0, out var shelter, out _)) return false;
        float s = walk.line.Project(shelter);
        walk.line.Sample(s, true, out var axis, out var tangent);
        int side = Vector3.Dot(shelter - axis, Vector3.Cross(Vector3.up, tangent)) < 0 ? -1 : 1;
        int rows = Mathf.Max(1, Mathf.FloorToInt((walk.outer - walk.inner) / .8f) + 1);
        var slots = new List<Slot>();
        for (int m = 0; m < 17; m++)
        {
            float along = (m + 1) / 2 * SlotSpacing * (m % 2 == 0 ? 1 : -1);
            walk.line.Sample(Mathf.Clamp(s + along, 0, walk.line.Length), true, out var point, out var direction);
            Vector3 right = Vector3.Cross(Vector3.up, direction).normalized * side;
            for (int r = 0; r < rows; r++)
            {
                float across = rows == 1 ? (walk.inner + walk.outer) * .5f : Mathf.Lerp(walk.inner, walk.outer, r / (rows - 1f));
                var facing = Quaternion.AngleAxis((Hash01(stop.node, m, r) - .5f) * 50f, Vector3.up) * -right;
                slots.Add(new Slot { position = point + right * across + Vector3.up * walk.surface, facing = facing });
            }
        }
        stop.slots = slots.ToArray();
        stop.gate ??= new Gate { stop = stop };
        stop.gate.path = road.path_id; stop.gate.s = s;
        return true;
    }

    /// <summary>Probability of a passing pedestrian entering. Incoming passenger counts and road pedestrian traffic both use daily counts.</summary>
    private static float JoinProbability(float ridership, PathSummary road)
    {
        float traffic = Traffic(road);
        return traffic > 0 ? Mathf.Clamp01(ridership / traffic) : 1;
    }

    /// <summary>Nearest walking band by distance regardless of road type, matching core station exits. False if its perpendicular foot is outside the management area.</summary>
    private bool NearestWalk(Vector3 point, out Walk nearest, out float s)
    {
        nearest = null; s = 0;
        float best = float.PositiveInfinity;
        foreach (var candidate in pathManager.GetPathsNearCircle(point, 200f))
        {
            if (!walks.TryGetValue(candidate.path_id, out var walk)) continue;
            float at = walk.line.Project(point);
            walk.line.Sample(at, true, out var foot, out _);
            float distance = Utils.XZDistanceSquared(foot, point);
            if (distance < best) { best = distance; nearest = walk; s = at; }
        }
        if (nearest == null) return false;
        foreach (var interval in nearest.intervals)
            if (s >= interval.x && s <= interval.y) return true;
        return false;
    }

    /// <summary>
    /// At each entrance, probabilistically send passing pedestrians into station buildings or bus-stop queues. Return true after queuing so callers remove them.
    /// As with building arrivals, recently spawned pedestrians, including those from stations or buses, do not enter.
    /// </summary>
    private bool PassGate(Pedestrian p, int pathBefore, float sBefore)
    {
        if (p.pathId != pathBefore || p.s == sBefore || p.InConnector || p.Entering || p.fading || p == FollowedPedestrian
            || p.age < minimumJourneyTime || !gatesByPath.TryGetValue(p.pathId, out var list)) return false;
        foreach (var gate in list)
        {
            bool passed = p.s > sBefore ? sBefore < gate.s && gate.s <= p.s : p.s <= gate.s && gate.s < sBefore;
            if (!passed || Random.value >= gate.probability) continue;
            if (gate.stop != null) return JoinQueue(gate.stop, p);
            p.arrivalPending = false; p.arrivalDistance = -1;
            p.BeginEntering(gate.inside);
            return false;
        }
        return false;
    }

    /// <summary>
    /// Advance station exits, bus-stop queues, and alighting passengers; not called while traffic animation is paused.
    /// Exit intervals follow an exponential distribution whose mean gives the boarding rate.
    /// </summary>
    private void UpdateTransit(float dt)
    {
        gateClock += dt;
        foreach (var gate in houseGates.Values)
        {
            if (gate.rate <= 0 || !walks.TryGetValue(gate.path, out var walk)) continue;
            if (gate.next < 0) gate.next = gateClock + Interval(gate.rate);
            for (int i = 0; i < 4 && gateClock >= gate.next; i++)
            {
                SpawnLeaving(walk, gate.s, gate.side, gate.inside);
                gate.next += Interval(gate.rate);
            }
            if (gateClock >= gate.next) gate.next = gateClock + Interval(gate.rate);
        }
        var transit = TransitVehicleManager.Instance;
        if (transit == null) return;
        foreach (var stop in transitStops.Values)
            if (stop.bus) UpdateBusStop(stop, transit, transit.Clock);
    }

    private static float Interval(float rate) => -Mathf.Log(Mathf.Max(1e-6f, 1 - Random.value)) / rate;

    /// <summary>Current bus-stop service. If a bus is dwelling at any arrival, return it in current. previous is the last departure time.</summary>
    private void BusService(TransitStop stop, TransitMotionClock motion, double cycle, double now,
        out StopVisit current, out double arrival, out double departure, out double previous)
    {
        current = null; arrival = departure = 0; previous = double.NegativeInfinity;
        foreach (var visit in stop.visits)
        {
            motion.Service(visit.arrival, trainPhases, cycle, now, out double a, out double d, out double p, out bool dwelling);
            previous = System.Math.Max(previous, p);
            if (dwelling && current == null) { current = visit; arrival = a; departure = d; }
        }
    }

    /// <summary>On first seeing a bus stop, place the queue accumulated since the previous departure. Do not spawn alightings from a bus already stopped.</summary>
    private void SeedBusStop(TransitStop stop, TransitVehicleManager transit, TransitMotionClock motion)
    {
        if (!transit.TryGetTrainPhases(stop.line, trainPhases, out double cycle)) return;
        stop.seeded = true;
        double now = transit.Clock;
        BusService(stop, motion, cycle, now, out var current, out double arrival, out _, out double previous);
        if (current != null) current.lastArrival = arrival;
        int count = Mathf.Min(stop.slots.Length, (int)(stop.rate * (float)(now - previous) + Random.value));
        for (int i = 0; i < count; i++)
            stop.riders.Add(new Rider { from = stop.slots[i].position, slot = stop.slots[i].position, facing = stop.slots[i].facing, start = now,
                appear = now, speed = walkSpeed, walked = Random.value * stepLength * 2, variant = Random.Range(0, MaxModelVariants), index = i });
    }

    /// <summary>On bus arrival, spawn alightings at doors during the first part of the dwell and send queued passengers to doors in order.</summary>
    private void UpdateBusStop(TransitStop stop, TransitVehicleManager transit, double now)
    {
        if (!transit.TryGetLine(stop.line, out var motion, out _, out _) || !transit.TryGetTrainPhases(stop.line, trainPhases, out double cycle)) return;
        BusService(stop, motion, cycle, now, out var current, out double arrival, out double departure, out _);
        if (current != null && arrival > current.lastArrival + .001)
        {
            current.lastArrival = arrival;
            int count = (int)(stop.rate / stop.visits.Count * (cycle / trainPhases.Count) + Random.value);
            for (int j = 0; j < count; j++)
                stop.alighting.Add((arrival + current.arrival.Dwell * AlightSpan * (j + .5f) / count, current.doors[j % current.doors.Length]));
            int waiting = 0, rank = 0;
            foreach (var rider in stop.riders) if (!rider.boarding) waiting++;
            foreach (var rider in stop.riders)
                if (!rider.boarding)
                    Board(rider, current, arrival + current.arrival.Dwell * (BoardFrom + (BoardTo - BoardFrom) * (rank++ + .5f) / waiting), departure, now);
        }
        for (int i = stop.alighting.Count - 1; i >= 0; i--)
        {
            if (stop.alighting[i].time > now) continue;
            if (walks.TryGetValue(stop.path, out var walk))
            {
                Vector3 door = stop.alighting[i].door;
                float s = walk.line.Project(door);
                walk.line.Sample(s, true, out var axis, out var tangent);
                SpawnLeaving(walk, s, Vector3.Dot(door - axis, Vector3.Cross(Vector3.up, tangent)) < 0 ? -1 : 1, door);
            }
            stop.alighting.RemoveAt(i);
        }
        float fade = Mathf.Max(.01f, fadeDuration);
        for (int i = stop.riders.Count - 1; i >= 0; i--)
        {
            var rider = stop.riders[i];
            if (rider.boarding && now >= System.Math.Min(rider.leave, BoardEnd(rider) + fade)) stop.riders.RemoveAt(i);
        }
    }

    /// <summary>Move a pedestrian into the bus-stop queue if a waiting position is free. If a bus is stopped, go directly to board.</summary>
    private bool JoinQueue(TransitStop stop, Pedestrian p)
    {
        var transit = TransitVehicleManager.Instance;
        if (transit == null || stop.slots == null) return false;
        int index = -1;
        for (int k = 0; k < stop.slots.Length && index < 0; k++)
        {
            index = k;
            foreach (var other in stop.riders) if (other.index == k) { index = -1; break; }
        }
        if (index < 0) return false;
        double now = transit.Clock;
        p.Sample(out var position, out _);
        var rider = new Rider { from = position, slot = stop.slots[index].position, facing = stop.slots[index].facing, start = now,
            appear = double.NegativeInfinity, speed = p.speed, walked = p.walked, variant = p.variant, index = index };
        stop.riders.Add(rider);
        if (transit.TryGetLine(stop.line, out var motion, out _, out _) && transit.TryGetTrainPhases(stop.line, trainPhases, out double cycle))
        {
            BusService(stop, motion, cycle, now, out var current, out _, out double departure, out _);
            if (current != null) Board(rider, current, now, departure, now);
        }
        return true;
    }

    /// <summary>Start walking toward the nearest door at start (line clock). Speed up if needed to disappear at the door before departure.</summary>
    private void Board(Rider rider, StopVisit visit, double start, double departure, double now)
    {
        start = System.Math.Max(start, now);
        Leg(rider.from, rider.slot, rider.start, rider.speed, start, out var from, out _, out _);
        rider.boardFrom = from;
        rider.door = visit.doors[Nearest(visit.doors, from)];
        float time = Mathf.Max(.5f, (float)(departure - start) - fadeDuration - .3f);
        rider.boardStart = start;
        rider.boardSpeed = Mathf.Max(rider.speed, XZDistance(from, rider.door) / time);
        rider.leave = departure;
        rider.boarding = true;
    }

    private static double BoardEnd(Rider rider) => rider.boardStart + XZDistance(rider.boardFrom, rider.door) / rider.boardSpeed;

    /// <summary>Add platform passengers, derived from the line clock, and bus-stop queues to render batches.</summary>
    private void AddTransitFigures()
    {
        var transit = TransitVehicleManager.Instance;
        if (transit == null) return;
        double now = transit.Clock;
        foreach (var stop in transitStops.Values)
        {
            if (stop.bus)
            {
                foreach (var rider in stop.riders) AddRider(rider, now);
                continue;
            }
            if (!transit.TryGetLine(stop.line, out var motion, out _, out _) || !transit.TryGetTrainPhases(stop.line, trainPhases, out double cycle)) continue;
            float perService = stop.rate / stop.visits.Count * (float)(cycle / trainPhases.Count);
            for (int v = 0; v < stop.visits.Count; v++) AddPlatform(stop, v, motion, perService, cycle, now);
        }
    }

    /// <summary>
    /// Passengers for one platform arrival: waiting passengers appear at the entrance between previous departure and next arrival, walk to positions, then to doors while dwelling.
    /// Alightings appear at doors during the first part of the dwell and disappear at the entrance. Arrival-time-seeded counts/times remain identical each frame.
    /// </summary>
    private void AddPlatform(TransitStop stop, int index, TransitMotionClock motion, float perService, double cycle, double now)
    {
        var visit = stop.visits[index];
        double dwell = visit.arrival.Dwell;
        float fade = Mathf.Max(.01f, fadeDuration);
        motion.Service(visit.arrival, trainPhases, cycle, now, out double arrival, out double departure, out double previous, out _);
        long service = (long)System.Math.Round(arrival * 4);
        int count = Mathf.Min(visit.order.Length, (int)(perService + Hash01(stop.node, ~index, service)));
        float window = (float)(arrival - previous);
        for (int i = 0; i < count; i++)
        {
            int k = visit.order[i];
            float h = Hash01(stop.node, index * 7919 + i, service);
            float speed = walkSpeed * (1 + walkSpeedJitter * (2 * h - 1));
            Vector3 exit = visit.exits[visit.slotExit[k]], slot = visit.slots[k].position;
            float approach = XZDistance(exit, slot) / speed;
            double appear = previous + Mathf.Max(0, window - approach - 1) * (i + h) / count;
            if (now < appear) continue;
            double board = System.Math.Max(appear + approach, arrival + dwell * (BoardFrom + (BoardTo - BoardFrom) * (i + .5f) / count));
            int variant = (int)(h * 65536) % MaxModelVariants;
            float scale = Mathf.Clamp01((float)(now - appear) / fade);
            if (now < board)
            {
                float walked = Leg(exit, slot, appear, speed, now, out var position, out var heading, out bool moving);
                AddFigure(position, moving ? heading : visit.slots[k].facing, variant, h * stepLength * 2 + walked, moving, scale);
                continue;
            }
            Vector3 door = visit.doors[visit.slotDoor[k]];
            float boardSpeed = Mathf.Max(speed, XZDistance(slot, door) / Mathf.Max(.5f, (float)(departure - board) - fade - .3f));
            double inside = board + XZDistance(slot, door) / boardSpeed;
            if (now >= inside + fade) continue;
            float stepped = Leg(slot, door, board, boardSpeed, now, out var at, out var toward, out bool walking);
            AddFigure(at, toward, variant, h * stepLength * 2 + approach * speed + stepped, walking,
                Mathf.Min(scale, Mathf.Clamp01(1 - (float)(now - inside) / fade)));
        }
        foreach (double phase in trainPhases)
        {
            double since = TransitMotionClock.Mod(phase - visit.arrival.Arrival, cycle);
            if (since > AlighterLife) continue;
            double arrived = now - since;
            long key = (long)System.Math.Round(arrived * 4);
            int alighting = Mathf.Min(visit.order.Length, (int)(perService + Hash01(~stop.node, ~index, key)));
            for (int j = 0; j < alighting; j++)
            {
                float h = Hash01(~stop.node, index * 7919 + j, key);
                int d = Mathf.Min(visit.doors.Length - 1, (int)(h * visit.doors.Length));
                Vector3 door = visit.doors[d], exit = visit.exits[visit.doorExit[d]];
                float speed = walkSpeed * (1 + walkSpeedJitter * (2 * h - 1));
                double appear = arrived + dwell * AlightSpan * (j + .5f) / alighting;
                double gone = appear + XZDistance(door, exit) / speed;
                if (now < appear || now >= gone + fade) continue;
                float walked = Leg(door, exit, appear, speed, now, out var position, out var heading, out bool moving);
                float scale = Mathf.Min(Mathf.Clamp01((float)(now - appear) / fade), Mathf.Clamp01(1 - (float)(now - gone) / fade));
                AddFigure(position, heading, (int)(h * 65536) % MaxModelVariants, h * stepLength * 2 + walked, moving, scale);
            }
        }
    }

    private void AddRider(Rider rider, double now)
    {
        float fade = Mathf.Max(.01f, fadeDuration);
        double until = rider.boarding ? System.Math.Min(now, rider.boardStart) : now;
        float walked = Leg(rider.from, rider.slot, rider.start, rider.speed, until, out var position, out var heading, out bool moving);
        float scale = Mathf.Clamp01((float)(now - rider.appear) / fade);
        if (rider.boarding && now >= rider.boardStart)
        {
            walked += Leg(rider.boardFrom, rider.door, rider.boardStart, rider.boardSpeed, now, out position, out heading, out moving);
            scale = Mathf.Min(scale, Mathf.Clamp01(1 - (float)(now - BoardEnd(rider)) / fade));
        }
        AddFigure(position, moving ? heading : rider.facing, rider.variant, rider.walked + walked, moving, scale);
    }

    /// <summary>Position at now when walking a to b starting at start on the line clock. Returns distance walked (m).</summary>
    private static float Leg(Vector3 a, Vector3 b, double start, float speed, double now, out Vector3 position, out Vector3 heading, out bool moving)
    {
        heading = new Vector3(b.x - a.x, 0, b.z - a.z);
        float length = heading.magnitude;
        float walked = Mathf.Clamp((float)(now - start) * speed, 0, length);
        position = length > .001f ? Vector3.Lerp(a, b, walked / length) : b;
        moving = walked > 0 && walked < length;
        return walked;
    }

    private static float XZDistance(Vector3 a, Vector3 b) => new Vector2(b.x - a.x, b.z - a.z).magnitude;

    private static int Nearest(Vector3[] points, Vector3 from)
    {
        int best = 0;
        for (int i = 1; i < points.Length; i++)
            if (Utils.XZDistanceSquared(points[i], from) < Utils.XZDistanceSquared(points[best], from)) best = i;
        return best;
    }

    /// <summary>Value in [0,1) determined by an integer tuple, keeping a service's passenger counts and positions identical across frames.</summary>
    private static float Hash01(int a, int b, long c)
    {
        unchecked
        {
            ulong h = (ulong)a * 0x9E3779B97F4A7C15UL ^ ((ulong)b + 0x632BE59BD9B4E019UL) * 0xC2B2AE3D27D4EB4FUL ^ (ulong)c * 0x165667B19E3779F9UL;
            h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL;
            h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL;
            h ^= h >> 33;
            return (h >> 40) / 16777216f;
        }
    }

    /// <summary>Add pedestrians leaving origin, a station or bus door, onto sidewalk side. Normal regulation corrects population differences on other roads.</summary>
    private void SpawnLeaving(Walk walk, float s, int side, Vector3 origin)
    {
        if (active.Count >= maxActivePedestrians) return;
        var p = new Pedestrian
        {
            variant = Random.Range(0, MaxModelVariants),
            speed = Mathf.Max(.1f, walkSpeed * (1 + Random.Range(-walkSpeedJitter, walkSpeedJitter))),
            walked = Random.value * stepLength * 2,
        };
        p.SetPath(walk.data.path_id, walk.line, Random.value < .5f, s);
        PlaceOnBand(p, walk, true);
        Leave(p, walk, side, origin);
        active.Add(p);
    }
}
