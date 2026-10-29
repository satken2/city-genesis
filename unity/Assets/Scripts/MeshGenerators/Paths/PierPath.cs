using UnityEngine;
using System.Collections.Generic;
using Dreamteck.Splines;
using GeneratedModels;

/// <summary>
/// Ferry pier (model_type="pier") mesh generator.
///
/// Ferries berth on the pier centerline, so the berth stays open water and the quay lies on one side only.
/// body is the quay for the right side of the laying direction (start to end), placed at actual size with its origin on the
/// centerline at road-surface height (no width stretching or pivot correction, unlike RoadPath bodies). Laying the pier
/// the other way puts the quay on the other side. Author the quay inside the master width, which is the site and picking width.
/// Without a body model, fall back to the master-width white strip like other paths.
/// </summary>
public class PierPath : IPathMeshGenerator
{
    public PathMeshResult Generate(PathSummary pathData, List<Vector3> points, SidewalkSetback startSidewalkSetback = default, SidewalkSetback endSidewalkSetback = default)
    {
        if (points == null || points.Count < 2)
        {
            Debug.LogWarning($"[PierPath] ポイント不足 ({points?.Count ?? 0}件)。生成スキップ。");
            return new PathMeshResult(null, false);
        }

        RoadPath.GetPathMasterParams(pathData, out float width, out _, out _, out _, out _, out _, out _,
            out _, out _, out _, out _);

        GameObject container = new GameObject($"Pier_{pathData.path_id}");
        SplineComputer spline = RoadPath.BuildSpline(container, points, pathData.curved);
        PathDrawContext.SetAdaptiveTileLength(PathDrawContext.ComputeAdaptiveTileLength(spline, RoadPath.MAX_EXTRUDE_TILE_LENGTH));

        GameObject body = RoadPath.CreateSplineMesh(container, spline, $"Models/Paths/p{pathData.path_variant}/body", width, 0f,
            RoadPartHeights.BODY_ABOVE_SPLINE, false, $"Models/Paths/p{pathData.path_type}/body",
            stretchToWidth: false, compensatePivot: false);
        if (body == null) RoadPath.CreateFallbackRoadBody(container, spline, width);

        Mesh colliderMesh = RoadPath.BuildColliderMeshFromSpline(spline, width);
        if (colliderMesh != null) colliderMesh.name = $"PierCollider_{pathData.path_id}";
        return new PathMeshResult(colliderMesh, true, container);
    }
}
