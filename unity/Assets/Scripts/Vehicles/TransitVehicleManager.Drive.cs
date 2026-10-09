using System;
using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>
/// 選んだ位置から路線の第1編成へ乗車するか、描画中の編成を選んでそのまま眺める。停車・折り返し・各車の配置は通常描画と共通。
/// </summary>
public sealed partial class TransitVehicleManager
{
    public int DrivenLineId { get; private set; } = -1;
    public Vector3 DrivePosition { get; private set; }
    public Vector3 DriveDirection { get; private set; } = Vector3.forward;
    public float DriveCarLength { get; private set; }
    public bool DriveWaiting { get; private set; }
    private int drivenTrain;
    private double driveOffset;
    private float driveDelta;

    public void GetDriveLines(List<TransitLineSummary> result)
    {
        result.Clear();
        foreach (var line in lines)
            if (line.type != Constants.VEHICLE_BUS) result.Add(line.data);
    }

    public bool StartDrive(int lineId, int path, Vector3 point, bool forward)
    {
        var line = lines.Find(item => item.data.line_agent_id == lineId && item.type != Constants.VEHICLE_BUS);
        if (line == null || paths == null || !paths.TryGetPathPolyline(path, out var points, false) || points.Count < 2) return false;
        var polyline = new VehiclePolyline(points);
        float fraction = polyline.Project(point) / Mathf.Max(.001f, polyline.Length);
        if (!TryFindDriveTime(line.motion, path, fraction, forward, out double time)) return false;
        RefreshGeometry(point);
        if (!PoseDrive(line, time)) return false;
        DrivenLineId = lineId; drivenTrain = 0;
        driveOffset = time - clock;
        return true;
    }

    /// <summary>描画中の編成(バスを含む)をそのまま眺める。時計をずらさないので位置は飛ばない。</summary>
    public bool Watch(int lineId, int train)
    {
        var line = lines.Find(item => item.data.line_agent_id == lineId);
        if (line == null || routeGeometry == null) return false;
        int trains = Trains(line);
        if (train < 0 || train >= trains) return false;
        double headway = Headway(line, trains);
        if (!PoseDrive(line, line.motion.Schedule(clock + headway * train, headway * trains))) return false;
        DrivenLineId = lineId; drivenTrain = train;
        driveOffset = 0;
        return true;
    }

    /// <summary>乗車を始めた瞬間の先頭の車両の位置と向き。以後は DrawLine が毎フレーム更新する。</summary>
    private bool PoseDrive(Line line, double time)
    {
        int step = line.motion.AtTime(time, out double distance, out _);
        if (step < 0) return false;
        bool forward = line.motion.Steps[step].to_fraction > line.motion.Steps[step].from_fraction;
        float spacing = CarLength(line);
        double front = (line.positions.Length - 1) * .5 * spacing;
        Vector3 position, direction;
        bool sampled = line.type == Constants.VEHICLE_BUS
            ? routeGeometry.SampleBus(line, distance + front, distance, forward, spacing, out position, out direction)
            : routeGeometry.Sample(line, distance + front, distance, forward, out position, out direction);
        if (!sampled) return false;
        DrivePosition = position; DriveDirection = direction; DriveCarLength = spacing; DriveWaiting = false;
        return true;
    }

    /// <summary>同じパスの往復を区別し、選択位置を含む区間の等加速度時計を逆算する。</summary>
    public static bool TryFindDriveTime(TransitMotionClock motion, int path, float fraction, bool forward, out double time)
    {
        time = 0;
        for (int i = 0; i < motion.Steps.Count; i++)
        {
            var step = motion.Steps[i];
            if (step.path_id != path || (step.to_fraction > step.from_fraction) != forward ||
                fraction < Mathf.Min(step.from_fraction, step.to_fraction) - .00001f ||
                fraction > Mathf.Max(step.from_fraction, step.to_fraction) + .00001f) continue;
            float span = step.to_fraction - step.from_fraction;
            if (Mathf.Abs(span) < .000001f) continue;
            double target = step.length_m * Mathf.Clamp01((fraction - step.from_fraction) / span);
            double acceleration = (step.end_speed_mps - step.start_speed_mps) / step.duration_seconds;
            double low = 0, high = step.duration_seconds;
            for (int iteration = 0; iteration < 40; iteration++)
            {
                double middle = (low + high) * .5;
                if (step.start_speed_mps * middle + .5 * acceleration * middle * middle < target) low = middle;
                else high = middle;
            }
            time = motion.Times[i] + (low + high) * .5;
            return true;
        }
        return false;
    }

    public void StopDrive()
    {
        DrivenLineId = -1; drivenTrain = 0; driveOffset = 0; DriveWaiting = false;
    }
}
