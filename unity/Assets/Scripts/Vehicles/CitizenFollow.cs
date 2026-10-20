using System.Collections;
using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Continuously renderable paths for a followed car/pedestrian, forming one travel section. paths[index] is the current path.
/// With hasArrival, leave the final path at the nearest point to arrivalPoint, enter the destination building, and disappear;
/// if farther than ArrivalReach from that path, disappear at its end instead. done becomes true when the designated entity disappears.
/// </summary>
public sealed class FollowRun
{
    public readonly List<int> paths = new();
    public readonly List<bool> forwards = new();
    public int index;
    public bool hasArrival;
    public Vector3 arrivalPoint;
    public bool done;
    public const float ArrivalReach = 60f;
}

/// <summary>
/// Citizen following and sampling the citizen represented by an entity clicked while paused. Fetch core estimates via POST /citizens/pick and GET /citizens/{id}/itinerary,
/// then move a designated car/pedestrian along the itinerary and follow it with the camera. Rendering only; city state is unchanged.
/// The itinerary consists of round trips from home; each return selects the next destination proportionally to daily visit frequency.
/// For sections not renderable as cars/pedestrians, such as public transport rides or station passages, hide the entity and move only the camera along the route.
/// </summary>
[DefaultExecutionOrder(100)]
public sealed class CitizenFollow : MonoBehaviour
{
    public static CitizenFollow Instance { get; private set; }
    public static bool IsFollowing => Instance != null && Instance.citizenId > 0;
    public static bool BlocksCamera => IsFollowing;

    private const float StaySeconds = 6f;        // Time spent at a destination, measured by the traffic clock so it does not advance while paused.
    private const float SpawnWaitSeconds = 6f;   // Maximum wait for the first road scan of a section; afterward, proceed with the camera only.
    private const float StuckSeconds = 20f;      // End the section after this duration without progress by the designated entity.
    private const float CarSpeed = 12f;          // Camera-only speed for car sections whose roads cannot be rendered.
    private const int RememberedPicks = 64;      // Number of entities remembered to avoid resampling when the same one is clicked again.

    private readonly Dictionary<object, CitizenPickResult> picks = new();
    private readonly List<object> pickOrder = new();
    private readonly Queue<Segment> segments = new();
    private int citizenId, request;
    private CitizenItinerary itinerary;
    private Segment segment;
    // agent_id of the current and destination visits (-1 while traveling or unknown). Refetching after an epoch may reorder visits,
    // so remember agents rather than indices.
    private int visitAgent = -1, destinationAgent = -1;
    private float stayUntil, segmentStarted, lastMove;
    private Vector3 lastPosition, focus, heading = Vector3.forward;
    private bool hasFocus;
    private readonly ChaseCamera chase = new();
    private Camera cam;
    private VehicleManager cars;
    private PedestrianManager people;
    private PathManager paths;
    private IpcClient ipc;

    // Travel mode. Household car ownership can change when refetching after an epoch, so retain it mid-trip
    // and update from the itinerary when starting the next trip, keeping built sections consistent with the designated entity type.
    private bool car;
    private bool Car => car;

    private sealed class Segment
    {
        public FollowRun run;
        public Vector3? origin;          // Building exit position, only for the first travel section.
        public object adopt;             // Clicked entity, inherited at the start of this section.
        public List<Vector3> points;     // Camera-only polyline for sections that cannot be rendered.
        public float speed, length, travelled;
        public bool started;
        public Vector3? anchor;          // Approximate section origin from core coordinates, used to shift the management center before path geometry is available.
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap() => Ensure();

    public static CitizenFollow Ensure()
    {
        if (Instance == null) new GameObject("CitizenFollow").AddComponent<CitizenFollow>();
        return Instance;
    }

    private void Awake() { Instance = this; }
    private void OnDestroy() { UnbindEvents(); if (Instance == this) Instance = null; }

    public static bool TryGetLoadCenter(out Vector3 center)
    {
        bool active = IsFollowing && Instance.hasFocus;
        center = active ? Instance.focus : default;
        return active;
    }

    // ------------------------------------------------------------------
    // Citizen represented by the clicked entity.
    // ------------------------------------------------------------------

    /// <summary>Ask the core to sample a citizen for a clicked car/pedestrian and open the citizen panel. Do not resample the same entity.</summary>
    public void RequestPick(object target)
    {
        if (picks.TryGetValue(target, out var known)) { OpenPanel(known.citizen_id); return; }
        string mode; int path, previous1, previous2; bool forward;
        if (target is Vehicle v) { mode = "automobile"; path = v.pathId; forward = v.forward; previous1 = v.previousPath; previous2 = v.previousPath2; }
        else if (target is Pedestrian p) { mode = "pedestrian"; path = p.pathId; forward = p.forward; previous1 = p.previousPath; previous2 = p.previousPath2; }
        else return;
        var previous = new List<int>();
        if (previous2 > 0) previous.Add(previous2);
        if (previous1 > 0) previous.Add(previous1);
        var payload = new Dictionary<string, object> { ["mode"] = mode, ["path_id"] = path, ["forward"] = forward, ["previous_path_ids"] = previous };
        StartCoroutine(ApiClient.Post<CitizenPickResult>("/citizens/pick", payload, result =>
        {
            if (result == null || result.status != "ok") { IpcClient.Instance?.EmitCitizenPickFailed(result?.status ?? "error"); return; }
            Remember(target, result);
            OpenPanel(result.citizen_id);
        }, (_, __) => IpcClient.Instance?.EmitCitizenPickFailed("error")));
    }

    private static void OpenPanel(int id) => IpcClient.Instance?.EmitSelectObject("citizen_detail", "citizen", id.ToString());

    private void Remember(object target, CitizenPickResult result)
    {
        picks[target] = result;
        pickOrder.Remove(target);
        pickOrder.Add(target);
        while (pickOrder.Count > RememberedPicks) { picks.Remove(pickOrder[0]); pickOrder.RemoveAt(0); }
    }

    private void ForgetPicks() { picks.Clear(); pickOrder.Clear(); }

    // ------------------------------------------------------------------
    // Start and stop following.
    // ------------------------------------------------------------------

    public void Follow(int id)
    {
        if (GameStateManager.Instance == null || !GameStateManager.Instance.IsGameStarted()) return;
        var input = InputHandler.Instance;
        if (id <= 0 || (input != null && !input.IsNormalMode)) { IpcClient.Instance?.EmitCitizenFollowState(id, false); return; }
        Stop(true);
        if (DriveMode.IsOpen) DriveMode.Instance.Close(false);
        var controller = CameraController.Instance;
        if (controller != null) { controller.StopCinematicMode(); controller.SetFlyMode(false); controller.SetTopDownMode(false); }
        RadialMenuController.Instance?.CloseAndCancel();
        cam = Camera.main;
        cars = FindAnyObjectByType<VehicleManager>();
        people = FindAnyObjectByType<PedestrianManager>();
        paths = FindAnyObjectByType<PathManager>();
        citizenId = id; hasFocus = false; chase.Reset();
        IpcClient.Instance?.EmitCitizenFollowState(id, true);
        StartCoroutine(Load(id, ++request, true));
    }

    /// <summary>
    /// UI stop request. Notify completion even when not following, allowing button state to recover if a previous completion notification was missed.
    /// </summary>
    public void Unfollow(int id)
    {
        if (IsFollowing) Stop(true);
        else if (id > 0) IpcClient.Instance?.EmitCitizenFollowState(id, false);
    }

    /// <summary>Stop following. Return the designated entity to normal traffic and restore normal camera control at its current position.</summary>
    public void Stop(bool notify)
    {
        if (!IsFollowing) return;
        int id = citizenId;
        citizenId = 0; request++;
        itinerary = null; segments.Clear(); segment = null; visitAgent = destinationAgent = -1; hasFocus = false;
        cars?.StopFollow(); people?.StopFollow();
        if (cam != null) CameraController.Instance?.SetView(cam.transform.position, cam.transform.rotation);
        CameraCellChecker.Instance?.ResetViewState();
        if (notify) IpcClient.Instance?.EmitCitizenFollowState(id, false);
    }

    private IEnumerator Load(int id, int token, bool start)
    {
        CitizenItinerary result = null;
        bool failed = false;
        yield return ApiClient.Get<CitizenItinerary>($"/citizens/{id}/itinerary", r => result = r, _ => failed = true);
        if (token != request || citizenId != id) yield break;
        if (failed || result == null || result.status != "ok")
        {
            if (start) { IpcClient.Instance?.EmitCitizenPickFailed(result?.status ?? "error"); Stop(true); }
            yield break;
        }
        itinerary = result;
        if (!start) yield break;
        car = itinerary.mode == "automobile";
        // Wait for the itinerary before moving; otherwise the clicked entity would leave its sampled path.
        bool adopted = TryAdoptPick(id);
        TrafficClock.SetPaused(false);
        if (adopted) yield break;
        // If the clicked entity cannot be inherited, depart immediately from home.
        visitAgent = itinerary.visits.Count > 0 ? itinerary.visits[0].agent_id : -1;
        destinationAgent = -1; stayUntil = TrafficClock.Time;
        hasFocus = TryVisitPoint(0, out focus);
    }

    /// <summary>If the entity clicked to select this citizen is still moving, inherit it midway through the sampled trip.</summary>
    private bool TryAdoptPick(int id)
    {
        for (int i = pickOrder.Count - 1; i >= 0; i--)
        {
            var target = pickOrder[i];
            var pick = picks[target];
            if (pick.citizen_id != id) continue;
            if (pick.snapshot != itinerary.snapshot || pick.trip == null || pick.trip.status != "ok" || pick.step_index == null) return false;
            int path; bool forward;
            if (target is Vehicle v && Car && cars != null && cars.IsActive(v)) { path = v.pathId; forward = v.forward; }
            else if (target is Pedestrian p && !Car && people != null && people.IsActive(p)) { path = p.pathId; forward = p.forward; }
            else return false;
            // The entity may have advanced since sampling, so search for its current path from the sampled position onward.
            // Do not inherit if it left the route, such as turning onto another road while watching after unpausing.
            int start = pick.trip.steps.FindIndex(pick.step_index.Value, item => item.path_id == path && item.forward == forward);
            if (start < 0) return false;
            destinationAgent = pick.to_agent_id; visitAgent = -1;
            BuildSegments(pick.trip, start, null, VisitPoint(VisitIndex(destinationAgent)), target);
            return segments.Count > 0;
        }
        return false;
    }

    // ------------------------------------------------------------------
    // Itinerary.
    // ------------------------------------------------------------------

    private void Update()
    {
        BindEvents();
        if (!IsFollowing) return;
        if (GameStateManager.Instance == null || !GameStateManager.Instance.IsGameStarted()) { Stop(true); return; }
        var controller = CameraController.Instance;
        if (controller != null && (controller.IsFlyMode || controller.IsCinematicMode || controller.IsTopDownMode)) { Stop(true); return; }
        if (Application.isFocused && (InputUtil.KeyWasPressedThisFrame(Key.Escape) || MoveKeyPressed())) { Stop(true); return; }
        if (itinerary == null) return;
        if (segment == null && segments.Count > 0) BeginSegment(segments.Dequeue());
        if (segment != null) AdvanceSegment();
        else if (visitAgent >= 0 && TrafficClock.Time >= stayUntil) StartNextTrip();
    }

    private static bool MoveKeyPressed() =>
        InputUtil.KeyWasPressedThisFrame(Key.W) || InputUtil.KeyWasPressedThisFrame(Key.A)
        || InputUtil.KeyWasPressedThisFrame(Key.S) || InputUtil.KeyWasPressedThisFrame(Key.D);

    /// <summary>At home, choose a destination proportionally to daily visit frequency and depart; away from home, return home.</summary>
    private void StartNextTrip()
    {
        int from = Mathf.Max(0, VisitIndex(visitAgent)), to = from != 0 ? 0 : ChooseDestination();
        if (to < 0) { stayUntil = TrafficClock.Time + StaySeconds; return; }
        var trip = itinerary.trips.Find(t => t.from_visit == from && t.to_visit == to);
        if (trip == null || trip.status != "ok" || trip.steps == null || trip.steps.Count == 0)
        {
            // For trips without a route, move directly to the destination and stay there.
            visitAgent = itinerary.visits[to].agent_id; stayUntil = TrafficClock.Time + StaySeconds;
            if (TryVisitPoint(to, out var point)) focus = point;
            return;
        }
        car = itinerary.mode == "automobile";
        BuildSegments(trip, 0, VisitPoint(from), VisitPoint(to), null);
        destinationAgent = itinerary.visits[to].agent_id; visitAgent = -1;
        if (segments.Count == 0) Arrive();
    }

    private int ChooseDestination()
    {
        float total = 0;
        for (int i = 1; i < itinerary.visits.Count; i++) total += Mathf.Max(0, itinerary.visits[i].per_day);
        if (total <= 0) return -1;
        float pick = Random.value * total;
        for (int i = 1; i < itinerary.visits.Count; i++)
        {
            pick -= Mathf.Max(0, itinerary.visits[i].per_day);
            if (pick <= 0) return i;
        }
        return itinerary.visits.Count - 1;
    }

    private void Arrive()
    {
        // If arriving at a destination absent from the refreshed itinerary, such as a removed shop, treat the next departure as starting from home.
        int index = VisitIndex(destinationAgent);
        visitAgent = index >= 0 ? destinationAgent : itinerary.visits.Count > 0 ? itinerary.visits[0].agent_id : -1;
        destinationAgent = -1;
        stayUntil = TrafficClock.Time + StaySeconds;
        if (TryVisitPoint(index, out var point)) focus = point;
    }

    private int VisitIndex(int agentId) => itinerary == null || agentId < 0 ? -1 : itinerary.visits.FindIndex(item => item.agent_id == agentId);

    private Vector3? VisitPoint(int index) => TryVisitPoint(index, out var point) ? point : (Vector3?)null;

    private bool TryVisitPoint(int index, out Vector3 point)
    {
        point = default;
        if (itinerary == null || index < 0 || index >= itinerary.visits.Count) return false;
        var item = itinerary.visits[index];
        if (item.x == null || item.z == null) return false;
        point = new Vector3(item.x.Value, 0, item.z.Value);
        point.y = Utils.GetGroundHeight(point);
        return true;
    }

    /// <summary>
    /// Split travel steps into continuously renderable sections (FollowRun) and camera-only sections. Legs skipped between steps,
    /// including public transport, people movers, and stop connections, become camera-only sections.
    /// </summary>
    private void BuildSegments(CitizenTrip trip, int start, Vector3? origin, Vector3? arrival, object adopt)
    {
        segments.Clear(); segment = null;
        var steps = trip.steps;
        FollowRun run = null;
        int previousLeg = start > 0 ? Mathf.Max(-1, steps[start].leg - 1) : -1;
        for (int i = start; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.leg >= 0)
            {
                for (int k = previousLeg + 1; k < step.leg; k++)
                    if (AddLegSegment(trip.legs[k])) run = null;
                previousLeg = step.leg;
            }
            if (!Drawable(step.type))
            {
                run = null;
                AddPathSegment(step.path_id, step.forward, Car ? CarSpeed : LocalConstants.PEDESTRIAN_WALK_SPEED);
                continue;
            }
            if (run == null)
            {
                run = new FollowRun();
                var created = new Segment { run = run };
                if (i == start && adopt != null) created.adopt = adopt;
                else if (i == 0 && step.leg == -1) created.origin = origin;
                created.anchor = created.origin ?? LegStart(trip, steps, i);
                segments.Enqueue(created);
            }
            run.paths.Add(step.path_id);
            run.forwards.Add(step.forward);
        }
        if (run != null && arrival.HasValue) { run.hasArrival = true; run.arrivalPoint = arrival.Value; }
    }

    /// <summary>Start of the leg containing steps[index]. For added road-access elements, use the next leg's start.</summary>
    private static Vector3? LegStart(CitizenTrip trip, List<CitizenTripStep> steps, int index)
    {
        for (int i = index; i < steps.Count; i++)
        {
            int leg = steps[i].leg;
            if (leg < 0 || trip.legs == null || leg >= trip.legs.Count) continue;
            var points = trip.legs[leg].points;
            if (points == null || points.Count == 0 || points[0] == null || points[0].Count < 2) return null;
            var point = new Vector3(points[0][0], 0, points[0][1]);
            point.y = Utils.GetGroundHeight(point);
            return point;
        }
        return null;
    }

    private bool Drawable(int type) => Car
        ? type == Constants.PATH_STREET || type == Constants.PATH_MOTORWAY
        : type == Constants.PATH_STREET || type == Constants.PATH_WALKWAY;

    private bool AddLegSegment(CitizenTripLeg leg)
    {
        if (leg == null || leg.points == null || leg.points.Count < 2) return false;
        if (leg.kind != "transit" && leg.kind != "conveyor" && leg.kind != "access") return false;
        var points = new List<Vector3>();
        foreach (var xz in leg.points)
        {
            if (xz == null || xz.Count < 2) continue;
            var point = new Vector3(xz[0], 0, xz[1]);
            point.y = Utils.GetGroundHeight(point);
            points.Add(point);
        }
        float speed = leg.minutes > .01f ? leg.distance_m / (leg.minutes * 60f) : LocalConstants.PEDESTRIAN_WALK_SPEED;
        return AddVirtual(points, Mathf.Clamp(speed, .5f, 80f));
    }

    private void AddPathSegment(int pathId, bool forward, float speed)
    {
        if (paths == null || !paths.TryGetPathPolyline(pathId, out var points, false) || points.Count < 2) return;
        var copy = new List<Vector3>(points);
        if (!forward) copy.Reverse();
        AddVirtual(copy, speed);
    }

    private bool AddVirtual(List<Vector3> points, float speed)
    {
        float length = 0;
        for (int i = 1; i < points.Count; i++) length += Vector3.Distance(points[i - 1], points[i]);
        if (length < 1f) return false;
        segments.Enqueue(new Segment { points = points, speed = speed, length = length });
        return true;
    }

    private void BeginSegment(Segment next)
    {
        segment = next;
        segmentStarted = lastMove = TrafficClock.Time;
        if (next.run != null) next.started = TryStartRun(next);
    }

    private bool TryStartRun(Segment target)
    {
        var run = target.run;
        if (target.adopt != null)
        {
            bool adopted = target.adopt is Vehicle v ? cars != null && cars.AdoptFollow(v, run)
                : target.adopt is Pedestrian p && people != null && people.AdoptFollow(p, run);
            target.adopt = null;
            if (adopted) return true;
        }
        bool started = Car ? cars != null && cars.StartFollow(run, target.origin) : people != null && people.StartFollow(run, target.origin);
        if (!started)
        {
            // Shift the management center toward the section origin and wait for road scanning to reach it.
            Vector3? point = target.anchor;
            if (target.origin == null && paths != null && paths.TryGetPathPolyline(run.paths[0], out var points, false) && points.Count > 0)
                point = run.forwards[0] ? points[0] : points[points.Count - 1];
            if (point.HasValue) { focus = point.Value; hasFocus = true; }
        }
        return started;
    }

    private void AdvanceSegment()
    {
        if (segment.run != null)
        {
            if (!segment.started)
            {
                segment.started = TryStartRun(segment);
                if (!segment.started)
                {
                    if (TrafficClock.Time - segmentStarted > SpawnWaitSeconds) ReplaceWithVirtual(segment.run);
                    return;
                }
            }
            if (segment.run.done) { FinishSegment(); return; }
            Vector3 position = default, direction = default;
            bool posed = Car ? cars != null && cars.TryGetFollowedPose(out position, out direction)
                : people != null && people.TryGetFollowedPose(out position, out direction);
            if (!posed) { FinishSegment(); return; }
            focus = position; hasFocus = true;
            if (direction.sqrMagnitude > .0001f) heading = direction;
            if ((position - lastPosition).sqrMagnitude > .25f) { lastPosition = position; lastMove = TrafficClock.Time; }
            else if (TrafficClock.Time - lastMove > StuckSeconds)
            {
                cars?.StopFollow(); people?.StopFollow();
                FinishSegment();
            }
            return;
        }
        segment.travelled = Mathf.Min(segment.length, segment.travelled + segment.speed * Time.deltaTime * TrafficClock.Scale);
        SampleVirtual(segment, out focus, out var tangent);
        hasFocus = true;
        if (tangent.sqrMagnitude > .0001f) heading = tangent;
        if (segment.travelled >= segment.length) FinishSegment();
    }

    /// <summary>If scanning has not reached a section and no entity can spawn, move only the camera along its path geometry.</summary>
    private void ReplaceWithVirtual(FollowRun run)
    {
        var rest = new List<Segment>(segments);
        segments.Clear(); segment = null;
        float speed = Car ? CarSpeed : LocalConstants.PEDESTRIAN_WALK_SPEED;
        for (int i = 0; i < run.paths.Count; i++) AddPathSegment(run.paths[i], run.forwards[i], speed);
        foreach (var item in rest) segments.Enqueue(item);
        // If path geometry is also unavailable and nothing can be queued, treat the trip as arrived instead of leaving it stuck.
        if (segments.Count == 0) Arrive();
    }

    private void FinishSegment()
    {
        segment = null;
        if (segments.Count == 0) Arrive();
    }

    private static void SampleVirtual(Segment target, out Vector3 position, out Vector3 tangent)
    {
        float left = target.travelled;
        var points = target.points;
        for (int i = 1; i < points.Count; i++)
        {
            float length = Vector3.Distance(points[i - 1], points[i]);
            if (left <= length || i == points.Count - 1)
            {
                position = Vector3.Lerp(points[i - 1], points[i], length > 0 ? Mathf.Clamp01(left / length) : 1);
                tangent = points[i] - points[i - 1];
                return;
            }
            left -= length;
        }
        position = points[points.Count - 1];
        tangent = Vector3.zero;
    }

    // ------------------------------------------------------------------
    // Camera.
    // ------------------------------------------------------------------

    private void LateUpdate()
    {
        if (!IsFollowing || !hasFocus) return;
        if (cam == null) cam = Camera.main;
        if (cam == null) return;
        chase.Apply(cam, focus, heading, Time.unscaledDeltaTime);
    }

    // ------------------------------------------------------------------
    // Session.
    // ------------------------------------------------------------------

    private void BindEvents()
    {
        if (ipc == IpcClient.Instance) return;
        UnbindEvents(); ipc = IpcClient.Instance;
        if (ipc == null) return;
        ipc.OnSessionReset += ResetSession; ipc.OnGameEnd += ResetSession; ipc.OnSessionForceReload += ResetSession;
        ipc.OnWorldChangeState += ChangeState; ipc.OnEpochExecuting += EpochExecuting;
    }

    private void UnbindEvents()
    {
        if (ipc == null) return;
        ipc.OnSessionReset -= ResetSession; ipc.OnGameEnd -= ResetSession; ipc.OnSessionForceReload -= ResetSession;
        ipc.OnWorldChangeState -= ChangeState; ipc.OnEpochExecuting -= EpochExecuting;
        ipc = null;
    }

    private void ResetSession() { Stop(true); ForgetPicks(); TrafficClock.SetPaused(false); }

    private void ChangeState(string state, Dictionary<string, object> parameters)
    {
        if (state != "STATE_NORMAL") Stop(true);
    }

    /// <summary>Epochs change routes and shop choices, so clear sampling history and refetch the itinerary if following; retain the current trip.</summary>
    private void EpochExecuting(bool executing)
    {
        if (executing) return;
        ForgetPicks();
        if (IsFollowing) StartCoroutine(Load(citizenId, request, false));
    }
}
