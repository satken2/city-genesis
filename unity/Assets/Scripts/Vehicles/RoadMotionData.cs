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

/// <summary>追従の遅れを自由走行速度以内で吸収する。実現不能な締切は超過を許す。</summary>
public static class RoadMotionControl
{
    public static float DesiredSpeed(float remainingDistance,float remainingSeconds,float freeSpeed)
        => remainingSeconds > .001f ? Mathf.Clamp(remainingDistance/remainingSeconds,0,freeSpeed) : freeSpeed;
    public static float FollowingSpeed(float desired,float current,float gap,float dt)
    {
        float safe = Mathf.Max(0,gap-2);
        float stoppingSpeed = Mathf.Sqrt(2*3*safe);
        float target = Mathf.Min(desired,stoppingSpeed);
        float next = Mathf.MoveTowards(current,target,(target<current?3:1.5f)*dt);
        return Mathf.Min(next,safe/Mathf.Max(.0001f,dt));
    }
}
