using System.Collections.Generic;
using UnityEngine;

/// <summary>観光用の1台もNPCと同じ車線・速度・追従を使い、退出と分岐の選択だけを変える。</summary>
public partial class VehicleManager
{
    public Vehicle DrivenVehicle { get; private set; }
    public int RequestedDrivePath { get; set; } = -1;
    public readonly struct DriveBranch
    {
        public readonly int Path;
        public readonly float Angle;
        public DriveBranch(int path, float angle) { Path = path; Angle = angle; }
    }

    public bool StartDrive(int path, Vector3 point, bool forward, int type)
    {
        if (pathManager == null) return false;
        var data = pathManager.GetPathData(path);
        if (!Drivable(data) || !DirectionAllowsEntry(data.travel_direction, forward)) return false;
        RefreshRoads(point);
        if (!roads.TryGetValue(path, out var road)) return false;
        StopDrive();
        int variant = VehicleModelResolver.PickVariantId(type, Random.Range(0, int.MaxValue));
        var v = new Vehicle { vehicleType = type, variantId = variant, go = Acquire(type, variant),
            speed = MeanSpeed(path, type), age = fadeDuration };
        v.SetPath(path, road.line, forward, road.line.Project(point));
        SetLane(v, road, false);
        SetMotion(v);
        active.Add(v);
        DrivenVehicle = v;
        RequestedDrivePath = -1;
        lastCenter = point; hasCenter = true;
        Place(v);
        return true;
    }

    public void StopDrive()
    {
        if (DrivenVehicle != null)
        {
            int index = active.IndexOf(DrivenVehicle);
            if (index >= 0) Despawn(index);
        }
        DrivenVehicle = null;
        RequestedDrivePath = -1;
    }

    private float CollectCandidates(Vehicle v, int node)
    {
        candidates.Clear();
        float total = 0;
        foreach (var data in pathManager.GetPathsConnectedToNode(node))
        {
            if (data.path_id == v.pathId || !Drivable(data) || !roads.TryGetValue(data.path_id, out var road)) continue;
            bool forward = data.start_node_id == node;
            if (!forward && data.end_node_id != node) continue;
            if (!DirectionAllowsEntry(data.travel_direction, forward)) continue;
            float weight = Traffic(data, v.vehicleType);
            candidates.Add((road, forward, weight)); total += weight;
        }
        return total;
    }

    private float CandidateAngle(Vehicle v, int index)
    {
        v.road.Sample(v.forward ? v.TotalLength : 0, v.forward, out _, out var incoming);
        var candidate = candidates[index];
        candidate.road.line.Sample(candidate.forward ? 0 : candidate.road.line.Length, candidate.forward, out _, out var outgoing);
        incoming.y = outgoing.y = 0;
        return Vector3.SignedAngle(incoming, outgoing, Vector3.up);
    }

    private int ChooseDriveCandidate(Vehicle v)
    {
        int chosen = 0;
        float best = float.PositiveInfinity;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].road.data.path_id == RequestedDrivePath) return i;
            float angle = Mathf.Abs(CandidateAngle(v, i));
            if (angle < best || (Mathf.Approximately(angle, best) && candidates[i].road.data.path_id < candidates[chosen].road.data.path_id))
            { best = angle; chosen = i; }
        }
        return chosen;
    }

    public void GetDriveBranches(List<DriveBranch> result)
    {
        result.Clear();
        var v = DrivenVehicle;
        if (v == null || v.InIntersection) return;
        var data = pathManager.GetPathData(v.pathId);
        if (data == null) return;
        CollectCandidates(v, v.AheadNodeId(data.start_node_id, data.end_node_id));
        for (int i = 0; i < candidates.Count; i++) result.Add(new DriveBranch(candidates[i].road.data.path_id, CandidateAngle(v, i)));
        result.Sort((a, b) => { int c = a.Angle.CompareTo(b.Angle); return c != 0 ? c : a.Path.CompareTo(b.Path); });
        if (candidates.Count > 0) RequestedDrivePath = candidates[ChooseDriveCandidate(v)].road.data.path_id;
    }
}
