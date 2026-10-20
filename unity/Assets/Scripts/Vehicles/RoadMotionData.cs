using System.Collections.Generic;
using System;
using UnityEngine;

[Serializable]
public class RoadMotionData
{
    public int path_id;
    public float free_seconds, total_seconds, length_m;
    public bool Valid => free_seconds > 0 && total_seconds >= free_seconds && length_m > 0 && !float.IsInfinity(total_seconds) && !float.IsNaN(total_seconds);
    public float MeanSpeed => length_m/total_seconds;
    public float FreeSpeed => length_m/free_seconds;
}
[Serializable]
public class RoadMotionResponse { public List<RoadMotionData> paths; public float active_hours; }

/// <summary>
/// Speed control for ordinary cars. Cars travel at free-flow speed; congestion delays appear as waits before intersections rather than uniform slowing.
/// Following forms queues behind waiting cars. They clear by the core's congestion-inclusive deadline, preserving average travel time.
/// </summary>
public static class RoadMotionControl
{
    public const float CarLength = 4.5f;   // Reference vehicle length for following and stop-line checks.
    private const float StopMargin = 2f;   // Gap retained to the car ahead when stopped.

    /// <summary>While free-flow travel to the end would beat the deadline, treat the road-end position of the front bumper as a leading vehicle.</summary>
    public static float StopLineGap(float distanceToEnd,float remainingSeconds,float freeSpeed)
        => remainingSeconds > distanceToEnd/Mathf.Max(.1f,freeSpeed) ? Mathf.Max(0,distanceToEnd-CarLength*.5f)+StopMargin : float.PositiveInfinity;
    public static float FollowingSpeed(float desired,float current,float gap,float dt)
    {
        float safe = Mathf.Max(0,gap-StopMargin);
        float stoppingSpeed = Mathf.Sqrt(2*3*safe);
        float target = Mathf.Min(desired,stoppingSpeed);
        float next = Mathf.MoveTowards(current,target,(target<current?3:1.5f)*dt);
        return Mathf.Min(next,safe/Mathf.Max(.0001f,dt));
    }
}
