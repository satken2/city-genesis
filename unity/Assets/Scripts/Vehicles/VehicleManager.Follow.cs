using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 市民の追従で指名した1台と、一時停止中のクリック判定。指名した車も NPC と同じ車線・速度・追従で走り、
/// 分岐だけを行程のパスの列(<see cref="FollowRun"/>)に従わせる。経路から外れないことを優先して、
/// 路面標示の進行方向の制約・到着予約・管理範囲外の削除から外す。
/// </summary>
public partial class VehicleManager
{
    public Vehicle FollowedVehicle { get; private set; }
    private FollowRun followRun;
    private readonly Dictionary<GameObject, Renderer[]> pickRenderers = new();

    /// <summary>画面内の自家用車。一時停止中は動かないので、クリックの瞬間に集めて当たり判定に使う。</summary>
    public void CollectPickTargets(List<Vehicle> result, Plane[] frustum)
    {
        foreach (var v in active)
        {
            if (v.vehicleType != Constants.VEHICLE_STANDARD || v == DrivenVehicle || v.fading || v.ExitingRoad
                || v.go == null || !v.go.activeInHierarchy) continue;
            foreach (var renderer in PickRenderers(v.go))
                if (renderer.enabled && GeometryUtility.TestPlanesAABB(frustum, renderer.bounds)) { result.Add(v); break; }
        }
    }

    /// <summary>車体の向きに沿った箱とレイの交差(<see cref="TransitVehicleManager.TryPick"/> と同じ判定)。</summary>
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
    /// 区間の最初のパスに指名車を出す。origin(建物の位置)があれば、その最寄りの地点から路肩寄せで出る。
    /// 走査がまだ道路に届いていなければ false(呼び出し側が管理中心を寄せて次のフレームに試し直す)。
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

    /// <summary>クリックで選んだ車をそのまま指名車にする。run.paths[0] は車が今いるパス。</summary>
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

    /// <summary>指名を解く。車は NPC に戻ってそのまま走り続ける。</summary>
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

    private const int FollowArrivalParcel = -1;   // 指名車の到着先は入口表を通さないので、区画IDの代わりにこの印を置く

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

    /// <summary>区間の最後のパスに入ったら、行き先の建物の最寄りの地点を到着点にする(通り過ぎていれば端まで走って消える)。</summary>
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
