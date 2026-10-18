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
/// 一般車の速度制御。車は自由走行の速度で走り、混雑による遅れは一様な減速ではなく交差点の手前での待ちとして描く。
/// 待つ車の後ろには前車への追従で列ができ、締切(コアの混雑込みの所要時間)どおりに抜けるので平均の所要時間は変わらない。
/// </summary>
public static class RoadMotionControl
{
    public const float CarLength = 4.5f;   // 追従と停止線の判定に使う標準の車長
    private const float StopMargin = 2f;   // 止まったときに前の車との間に残す距離

    /// <summary>自由走行の速度で端まで走っても締切より早く着く間は、前のバンパーが道路の端に来る位置を前の車とみなす。</summary>
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
