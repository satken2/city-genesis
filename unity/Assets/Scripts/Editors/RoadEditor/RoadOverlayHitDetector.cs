using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 2Dオーバーレイ上のポイント・辺・制御点に対するスクリーン空間ヒット検出結果。
/// ControlIn/ControlOut は各ポイントの入口/出口制御点に対応する。
/// </summary>
public enum HitTargetType { None, Point, Edge, ControlIn, ControlOut }

public struct RoadOverlayHit
{
    public HitTargetType type;
    /// <summary>Point/ControlIn/ControlOut の場合はポイントインデックス、Edge の場合は辺の開始インデックス</summary>
    public int index;
    /// <summary>Edge ヒット時の辺上の補間パラメータ (0-1)</summary>
    public float edgeT;

    public static RoadOverlayHit None => new RoadOverlayHit { type = HitTargetType.None, index = -1, edgeT = 0f };
}

/// <summary>
/// 2Dオーバーレイ上の配置ポイント・辺・制御点に対するスクリーン空間ヒット検出。
/// GameObjectを使わず数学的に判定する。
/// 各ポイントの control_in / control_out に対して独立してヒット判定を行う。
/// </summary>
public class RoadOverlayHitDetector
{
    /// <summary>ポイント・制御点のスクリーン空間ヒット半径(px)</summary>
    public float pointHitRadius = 14f;

    /// <summary>辺のスクリーン空間ヒット距離(px)</summary>
    public float edgeHitDistance = 10f;

    /// <summary>
    /// マウス位置がポイント・制御点・辺にヒットするか判定する。
    /// 優先順位: ControlIn/ControlOut > Point > Edge
    ///
    /// オーバーレイ描画ではノードや辺を地面高さに投影して表示しているため、
    /// ヒット判定も同じく地面高さに投影した座標で行う。
    /// これにより、高架に配置されたノードであっても、画面上に見えている位置で
    /// 正しくクリックが反応するようになる。
    /// </summary>
    public RoadOverlayHit TestHit(
        Vector2 mouseScreen,
        List<PathBuildPoint> points,
        Camera cam)
    {
        if (points == null || points.Count == 0)
            return RoadOverlayHit.None;

        // 1. 制御点判定（各ポイントの control_in / control_out）
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].control_out.HasValue)
            {
                Vector3 coWorld = points[i].control_out.Value;
                coWorld.y = Utils.GetGroundHeight(coWorld);
                Vector2 coScreen = cam.WorldToScreenPoint(coWorld);
                if (Vector2.Distance(mouseScreen, coScreen) < pointHitRadius)
                {
                    return new RoadOverlayHit { type = HitTargetType.ControlOut, index = i };
                }
            }

            if (points[i].control_in.HasValue)
            {
                Vector3 ciWorld = points[i].control_in.Value;
                ciWorld.y = Utils.GetGroundHeight(ciWorld);
                Vector2 ciScreen = cam.WorldToScreenPoint(ciWorld);
                if (Vector2.Distance(mouseScreen, ciScreen) < pointHitRadius)
                {
                    return new RoadOverlayHit { type = HitTargetType.ControlIn, index = i };
                }
            }
        }

        // 2. ポイント判定
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 ptWorld = points[i].position;
            ptWorld.y = Utils.GetGroundHeight(ptWorld);
            Vector2 vScreen = cam.WorldToScreenPoint(ptWorld);
            if (Vector2.Distance(mouseScreen, vScreen) < pointHitRadius)
            {
                return new RoadOverlayHit { type = HitTargetType.Point, index = i };
            }
        }

        // 3. 辺判定（隣接ポイントペア）
        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector3 p1World = points[i].position;
            p1World.y = Utils.GetGroundHeight(p1World);
            Vector3 p2World = points[i + 1].position;
            p2World.y = Utils.GetGroundHeight(p2World);

            Vector2 s1 = cam.WorldToScreenPoint(p1World);
            Vector2 s2 = cam.WorldToScreenPoint(p2World);

            float t;
            float dist = PointToSegmentDistance(mouseScreen, s1, s2, out t);
            if (dist < edgeHitDistance)
            {
                return new RoadOverlayHit { type = HitTargetType.Edge, index = i, edgeT = t };
            }
        }

        return RoadOverlayHit.None;
    }

    /// <summary>
    /// 点から線分への最短距離とパラメータtを計算する。
    /// </summary>
    private float PointToSegmentDistance(Vector2 point, Vector2 segA, Vector2 segB, out float t)
    {
        Vector2 seg = segB - segA;
        float lenSq = seg.sqrMagnitude;
        if (lenSq < 0.0001f)
        {
            t = 0f;
            return Vector2.Distance(point, segA);
        }

        t = Vector2.Dot(point - segA, seg) / lenSq;
        t = Mathf.Clamp01(t);
        Vector2 closest = segA + seg * t;
        return Vector2.Distance(point, closest);
    }
}
