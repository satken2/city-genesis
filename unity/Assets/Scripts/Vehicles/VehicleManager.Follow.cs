using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One car designated for citizen following, plus click detection. It uses the same lanes, speeds, and following as NPCs,
/// but follows the itinerary path sequence (<see cref="FollowRun"/>) at branches. To keep it on the route,
/// exclude it from road-marking direction constraints, scheduled arrivals, and out-of-range removal.
/// </summary>
public partial class VehicleManager
{
    public Vehicle FollowedVehicle { get; private set; }
    private FollowRun followRun;
    private readonly Dictionary<GameObject, Renderer[]> pickRenderers = new();

    /// <summary>On-screen moving cars of all types. Collect at button press for hit tests; callers filter vehicle types.</summary>
    public void CollectPickTargets(List<Vehicle> result, Plane[] frustum)
    {
        foreach (var v in active)
        {
            if (v == DrivenVehicle || v.fading || v.ExitingRoad
                || v.go == null || !v.go.activeInHierarchy) continue;
            foreach (var renderer in PickRenderers(v.go))
                if (renderer.enabled && GeometryUtility.TestPlanesAABB(frustum, renderer.bounds)) { result.Add(v); break; }
        }
    }

    /// <summary>Ray intersection with a body-oriented box, using the same test as <see cref="TransitVehicleManager.TryPick"/>.</summary>
    public bool RayHit(Vehicle v, Ray ray, out float distance)
    {
        distance = float.PositiveInfinity;
        if (v.go == null) return false;
        foreach (var renderer in PickRenderers(v.go))
        {
            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
            var localRay = new Ray(renderer.transform.InverseTransformPoint(ray.origin), renderer.transform.InverseTransformVector(ray.direction));
            if (!renderer.localBounds.IntersectRay(localRay, out float localDistance)) continue;
            float d = Vector3.Dot(renderer.transform.TransformPoint(localRay.GetPoint(localDistance)) - ray.origin, ray.direction);
            if (d >= 0 && d < distance) distance = d;
        }
        return !float.IsPositiveInfinity(distance);
    }

    private Renderer[] PickRenderers(GameObject go)
    {
        if (!pickRenderers.TryGetValue(go, out var renderers)) { renderers = go.GetComponentsInChildren<Renderer>(); pickRenderers[go] = renderers; }
        return renderers;
    }

    public bool IsActive(Vehicle v) => v != null && active.Contains(v) && !v.fading && !v.ExitingRoad;

    /// <summary>
    /// Spawn the designated car on the section's first path. If origin (building position) is given, emerge from the shoulder at the nearest point.
    /// False if scanning has not reached the road; the caller shifts the management center and retries next frame.
    /// </summary>
    public bool StartFollow(FollowRun run, Vector3? origin)
    {
        if (pathManager == null || run.paths.Count == 0 || !roads.TryGetValue(run.paths[0], out var road)) return false;
        StopFollow();
        bool forward = run.forwards[0];
        int type = Constants.VEHICLE_STANDARD;
        int variant = VehicleModelResolver.PickVariantId(type, Random.Range(0, int.MaxValue));
        var go = Acquire(type, ref variant);
        var v = new Vehicle { vehicleType = type, variantId = variant, go = go,
            speed = MeanSpeed(road.data.path_id, type), spawnBlendRemaining = origin.HasValue ? 15 : 0 };
        v.SetPath(road.data.path_id, road.line, forward, origin.HasValue ? road.line.Project(origin.Value) : -1);
        SetLane(v, road, true);
        SetMotion(v);
        active.Add(v);
        run.index = 0;
        FollowedVehicle = v; followRun = run;
        ApplyFollowArrival(v, road);
        Place(v);
        return true;
    }

    /// <summary>Designate the existing clicked car. run.paths[0] is its current path.</summary>
    public bool AdoptFollow(Vehicle v, FollowRun run)
    {
        if (!IsActive(v) || v == DrivenVehicle || run.paths.Count == 0) return false;
        StopFollow();
        v.arrivalPending = false;
        v.ReleaseArrival();
        run.index = 0;
        FollowedVehicle = v; followRun = run;
        if (roads.TryGetValue(v.pathId, out var road)) ApplyFollowArrival(v, road);
        return true;
    }

    /// <summary>Remove designation; the car continues moving as an NPC.</summary>
    public void StopFollow()
    {
        var v = FollowedVehicle;
        if (v != null && v.destinationParcel == FollowArrivalParcel) v.ReleaseArrival();
        FollowedVehicle = null; followRun = null;
    }

    public bool TryGetFollowedPose(out Vector3 position, out Vector3 direction)
    {
        position = direction = default;
        if (FollowedVehicle == null) return false;
        FollowedVehicle.Sample(out position, out direction);
        return true;
    }

    private const int FollowArrivalParcel = -1;   // The designated car's destination bypasses the entrance table, so use this marker instead of a parcel ID.

    private bool NextFollowStep(Vehicle v, out Road next, out bool nextForward)
    {
        next = null; nextForward = v.forward;
        int index = followRun.index + 1;
        if (index >= followRun.paths.Count) { v.fading = true; return false; }
        if (!roads.TryGetValue(followRun.paths[index], out next)) return false;
        nextForward = followRun.forwards[index];
        followRun.index = index;
        return true;
    }

    /// <summary>On entering the final path, set arrival at the point nearest the destination building; if already past it, drive to the end and disappear.</summary>
    private void ApplyFollowArrival(Vehicle v, Road road)
    {
        if (!followRun.hasArrival || followRun.index != followRun.paths.Count - 1) return;
        float target = road.line.Project(followRun.arrivalPoint);
        road.line.Sample(target, true, out Vector3 nearest, out _);
        float d = (target - v.s) * (v.forward ? 1 : -1);
        if (d < 0 || Utils.XZDistanceSquared(nearest, followRun.arrivalPoint) > FollowRun.ArrivalReach * FollowRun.ArrivalReach) return;
        v.arrivalDistance = d;
        v.arrivalBlendLength = Mathf.Min(15, Mathf.Max(.1f, d));
        v.destinationParcel = FollowArrivalParcel; v.destinationBuilding = 0;
        v.destinationPoint = followRun.arrivalPoint;
    }

    private void EndFollowRun()
    {
        if (followRun != null) followRun.done = true;
        FollowedVehicle = null; followRun = null;
    }
}
