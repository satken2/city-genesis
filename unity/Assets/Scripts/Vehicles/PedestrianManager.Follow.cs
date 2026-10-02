using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 市民の追従で指名した1人と、一時停止中のクリック判定。指名した歩行者も他の歩行者と同じ歩行帯・速度で歩き、
/// 分岐だけを行程のパスの列(<see cref="FollowRun"/>)に従わせる。到着予約と管理範囲外の削除から外す。
/// </summary>
public sealed partial class PedestrianManager
{
    public Pedestrian FollowedPedestrian { get; private set; }
    private FollowRun followRun;
    // 歩行者は小さいので、実寸より少し大きい箱でクリックを受ける(原点は足元)。
    private static readonly Bounds PickBounds = new Bounds(new Vector3(0, 1.05f, 0), new Vector3(1f, 2.1f, 1f));

    /// <summary>画面内の歩行者。一時停止中は動かないので、クリックの瞬間に集めて当たり判定に使う。</summary>
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
    /// 区間の最初のパスに指名した歩行者を出す。origin(建物の位置)があれば、そこから歩道へ出てくる。
    /// 走査がまだ道路に届いていなければ false(呼び出し側が管理中心を寄せて次のフレームに試し直す)。
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

    /// <summary>クリックで選んだ歩行者をそのまま指名する。run.paths[0] は歩行者が今いるパス。</summary>
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

    /// <summary>指名を解く。歩行者は他の歩行者に戻って歩き続ける。</summary>
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

    private const int FollowArrivalParcel = -1;   // 指名した歩行者の到着先は入口表を通さないので、区画IDの代わりにこの印を置く

    /// <summary>行程のパスの列の次へ進む。区間の終わりでは消え、走査がまだ届いていない道路は届くまで待つ。</summary>
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

    /// <summary>区間の最後のパスに入ったら、行き先の建物の最寄りの地点を到着点にする(通り過ぎていれば端まで歩いて消える)。</summary>
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
