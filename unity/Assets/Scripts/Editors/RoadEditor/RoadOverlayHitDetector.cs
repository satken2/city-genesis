using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Screen-space hit result for points, edges, and control points in a 2D overlay.
/// ControlIn/ControlOut correspond to each point's incoming/outgoing control points.
/// </summary>
public enum HitTargetType { None, Point, Edge, ControlIn, ControlOut }

public struct RoadOverlayHit
{
    public HitTargetType type;
    /// <summary>Point index for Point/ControlIn/ControlOut; edge start index for Edge.</summary>
    public int index;
    /// <summary>Interpolation parameter along the edge on an Edge hit (0-1).</summary>
    public float edgeT;

    public static RoadOverlayHit None => new RoadOverlayHit { type = HitTargetType.None, index = -1, edgeT = 0f };
}

/// <summary>
/// Screen-space hit detection for placement points, edges, and control points in a 2D overlay.
/// Uses mathematical tests without GameObjects.
/// Tests only control points that are currently visible.
/// </summary>
public class RoadOverlayHitDetector
{
    /// <summary>Screen-space hit radius for points/control points (px).</summary>
    public float pointHitRadius = 14f;

    /// <summary>Screen-space hit distance for edges (px).</summary>
    public float edgeHitDistance = 10f;

    /// <summary>
    /// Tests whether the mouse hits a point, control point, or edge.
    /// Priority: ControlIn/ControlOut > Point > Edge
    ///
    /// Overlay rendering projects nodes and edges onto ground height,
    /// so hit testing uses the same ground-projected coordinates.
    /// This lets clicks respond at the visible screen position
    /// even for elevated nodes.
    /// </summary>
    public RoadOverlayHit TestHit(
        Vector2 mouseScreen,
        List<PathBuildPoint> points,
        Camera cam, bool includeControls)
    {
        if (points == null || points.Count == 0)
            return RoadOverlayHit.None;

        // 1. Control point tests (control_in / control_out for each point)
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

        // 2. Point tests
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

        // 3. Edge tests (adjacent point pairs)
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
