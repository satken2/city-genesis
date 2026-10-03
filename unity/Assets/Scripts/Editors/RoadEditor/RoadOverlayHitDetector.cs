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
/// 表示されている制御点だけをヒット判定の対象にする。
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
        Camera cam, bool includeControls)
    {
        if (points == null || points.Count == 0)
            return RoadOverlayHit.None;

        // 1. 制御点判定（各ポイントの control_in / control_out）
        for (int i = 0; includeControls && i < points.Count; i++)
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

            float t = PolylineUtils.ProjectOnSegment(mouseScreen, s1, s2);
            float dist = Vector2.Distance(mouseScreen, Vector2.Lerp(s1, s2, t));
            if (dist < edgeHitDistance)
            {
                return new RoadOverlayHit { type = HitTargetType.Edge, index = i, edgeT = t };
            }
        }

        return RoadOverlayHit.None;
    }
}
