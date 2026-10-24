using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One pedestrian designated for citizen following, plus click detection. It uses the same walking bands and speed as others,
/// but follows the itinerary's path sequence (<see cref="FollowRun"/>) at branches and is excluded from arrival scheduling and out-of-range removal.
/// </summary>
public sealed partial class PedestrianManager
{
    public Pedestrian FollowedPedestrian { get; private set; }
    private FollowRun followRun;
    // Pedestrians are small, so use a slightly oversized click box with its origin at the feet.
    private static readonly Bounds PickBounds = new Bounds(new Vector3(0, 1.05f, 0), new Vector3(1f, 2.1f, 1f));

    /// <summary>On-screen pedestrians. Collect at button press for hit tests.</summary>
    public void CollectPickTargets(List<Pedestrian> result, Plane[] frustum)
    {
        foreach (var p in active)
        {
            if (p.fading || p.Entering || p.InConnector) continue;
            if (GeometryUtility.TestPlanesAABB(frustum, Utils.TransformBounds(PickBounds, PickMatrix(p)))) result.Add(p);
        }
    }

    public bool RayHit(Pedestrian p, Ray ray, out float distance)
    {
        distance = float.PositiveInfinity;
        var matrix = PickMatrix(p);
        var inverse = matrix.inverse;
        var localRay = new Ray(inverse.MultiplyPoint3x4(ray.origin), inverse.MultiplyVector(ray.direction));
        if (!PickBounds.IntersectRay(localRay, out float localDistance)) return false;
        float d = Vector3.Dot(matrix.MultiplyPoint3x4(localRay.GetPoint(localDistance)) - ray.origin, ray.direction);
        if (d < 0) return false;
        distance = d;
        return true;
    }

    private static Matrix4x4 PickMatrix(Pedestrian p)
    {
        p.Sample(out Vector3 position, out Vector3 tangent);
        tangent.y = 0;
        if (tangent.sqrMagnitude < .0001f) tangent = Vector3.forward;
        return Matrix4x4.TRS(position, Quaternion.LookRotation(tangent, Vector3.up), Vector3.one);
    }

    public bool IsActive(Pedestrian p) => p != null && active.Contains(p) && !p.fading && !p.Entering;

    /// <summary>
    /// Spawn a designated pedestrian on the section's first path. If origin (building position) is provided, walk out to the sidewalk from there.
    /// False if scanning has not reached the road; the caller shifts the management center and retries next frame.
    /// </summary>
    public bool StartFollow(FollowRun run, Vector3? origin)
    {
        if (pathManager == null || run.paths.Count == 0 || !walks.TryGetValue(run.paths[0], out var walk)) return false;
        StopFollow();
        bool forward = run.forwards[0];
        var p = new Pedestrian
        {
            variant = Random.Range(0, MaxModelVariants),
            speed = Mathf.Max(.1f, walkSpeed),
            walked = Random.value * stepLength * 2,
        };
        p.SetPath(walk.data.path_id, walk.line, forward, origin.HasValue ? walk.line.Project(origin.Value) : -1);
        PlaceOnBand(p, walk, true);
        if (origin.HasValue) p.BeginLeaving(origin.Value);
        active.Add(p);
        run.index = 0;
        FollowedPedestrian = p; followRun = run;
        ApplyFollowArrival(p, walk);
        return true;
    }

    /// <summary>Designate the existing clicked pedestrian. run.paths[0] is its current path.</summary>
    public bool AdoptFollow(Pedestrian p, FollowRun run)
    {
        if (!IsActive(p) || run.paths.Count == 0) return false;
        StopFollow();
        p.arrivalPending = false; p.arrivalDistance = -1; p.destinationParcel = 0;
        run.index = 0;
        FollowedPedestrian = p; followRun = run;
        if (walks.TryGetValue(p.pathId, out var walk)) ApplyFollowArrival(p, walk);
        return true;
    }

    /// <summary>Remove designation; the pedestrian continues walking as ordinary traffic.</summary>
    public void StopFollow()
    {
        var p = FollowedPedestrian;
        if (p != null && p.destinationParcel == FollowArrivalParcel) { p.arrivalDistance = -1; p.destinationParcel = 0; }
        FollowedPedestrian = null; followRun = null;
    }

    public bool TryGetFollowedPose(out Vector3 position, out Vector3 direction)
    {
        position = direction = default;
        if (FollowedPedestrian == null) return false;
        FollowedPedestrian.Sample(out position, out direction);
        return true;
    }

    private const int FollowArrivalParcel = -1;   // The designated pedestrian's arrival bypasses the entrance table, so use this marker in place of a parcel ID.

    /// <summary>Advance to the next itinerary path. Disappear at section end; wait for roads not yet reached by scanning.</summary>
    private bool FollowTransition(Pedestrian p)
    {
        int index = followRun.index + 1;
        if (index >= followRun.paths.Count) { p.fading = true; return true; }
        if (!walks.TryGetValue(followRun.paths[index], out var next)) return false;
        bool nextForward = followRun.forwards[index];
        followRun.index = index;
        if (next.data.path_id == p.pathId) p.forward = nextForward;
        else MoveToWalk(p, next, nextForward);
        ApplyFollowArrival(p, next);
        return true;
    }

    /// <summary>On entering the final path, use the point nearest the destination building as the arrival point; if already past it, walk to the end and disappear.</summary>
    private void ApplyFollowArrival(Pedestrian p, Walk walk)
    {
        if (!followRun.hasArrival || followRun.index != followRun.paths.Count - 1) return;
        float target = walk.line.Project(followRun.arrivalPoint);
        walk.line.Sample(target, true, out Vector3 nearest, out _);
        float d = (target - p.s) * (p.forward ? 1 : -1);
        if (d < 0 || Utils.XZDistanceSquared(nearest, followRun.arrivalPoint) > FollowRun.ArrivalReach * FollowRun.ArrivalReach) return;
        p.arrivalDistance = d;
        p.destinationParcel = FollowArrivalParcel; p.destinationBuilding = 0;
        p.destinationPoint = followRun.arrivalPoint;
    }

    private void EndFollowRun()
    {
        if (followRun != null) followRun.done = true;
        FollowedPedestrian = null; followRun = null;
    }
}
