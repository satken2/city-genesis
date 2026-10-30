using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Input and preview for grid construction (Constants.PATH_BUILD_METHOD_GRID).
/// The first two points define one rectangle side; constrain the third to the perpendicular through the second point to define the rectangle.
/// Send the 3 points through edit_path:update_shape as for single construction; the UI obtains actual paths from core expansion
/// (POST /paths/layout) and returns them through apply_layout. During placement, draw a provisional grid equally divided by each side's count
/// (same allocation as the core). After results arrive, draw path bands (the core excludes overlaps with existing roads and unbuildable sections).
/// Point movement/insertion/deletion are not accepted; restart with panel cancel.
/// </summary>
public partial class RoadPlanManager
{
    /// <summary>Construction method (Constants.PATH_BUILD_METHOD_*), received as build_method in STATE_EDIT_PATH.</summary>
    public int BuildMethod = Constants.PATH_BUILD_METHOD_SINGLE;

    /// <summary>Grid divisions [first side (point 1→2), second side (point 2→3)].</summary>
    public int[] GridDivisions = { 3, 3 };

    public bool IsGridBuild => BuildMethod == Constants.PATH_BUILD_METHOD_GRID;

    // Core-returned paths converted to terrain-following points. Discard on point replacement or division changes.
    private List<List<Vector3>> gridLayoutLines;

    // Terrain-following provisional grid points and their inputs. Avoid resampling terrain every draw frame.
    private List<List<Vector3>> gridGuideLines;
    private Vector3 gridGuideP1, gridGuideP2, gridGuideP3;
    private int gridGuideDiv1, gridGuideDiv2;

    private const float GridLineSampleStep = 5f;
    private static readonly Color GridGuideColor = new Color(0.3f, 0.6f, 1f, 0.6f);

    /// <summary>edit_path:change_grid_divisions. Redraws the provisional grid; after point 3, waits for expansion returned by the UI.</summary>
    public void SetGridDivisions(int[] divisions)
    {
        if (divisions == null || divisions.Length != 2) return;
        GridDivisions = new[] { Mathf.Max(1, divisions[0]), Mathf.Max(1, divisions[1]) };
        gridLayoutLines = null;
        if (IsGridBuild) UpdateGridPreview();
    }

    private void OnGridLeftClick(Vector3 worldPos)
    {
        if (phase != RoadEditorPhase.Placing) return;
        Vector3 position = ApplySnapping(worldPos);
        if (points.Count == 2) position = ConstrainGridCorner(position);
        position.y = Utils.GetPathSurfaceHeight(position);
        var role = points.Count == 0 ? PathPointRole.Start
            : points.Count == 1 ? PathPointRole.Waypoint : PathPointRole.End;
        points.Add(new PathBuildPoint(position, role));
        gridLayoutLines = null;
        if (points.Count < 3)
        {
            CommitShapeChange();
            PlaySound(placePointSound);
            return;
        }
        phase = RoadEditorPhase.Editing;
        CommitShapeChange(finalized: true);
        PlaySound(finalizeShapeSound);
    }

    /// <summary>Projects the third-point candidate onto the perpendicular to point 1→2 through point 2.</summary>
    private Vector3 ConstrainGridCorner(Vector3 position)
    {
        Vector3 p1 = points[0].position, p2 = points[1].position;
        var edge = new Vector2(p2.x - p1.x, p2.z - p1.z);
        if (edge.sqrMagnitude < 1e-6f) return position;
        var normal = new Vector2(-edge.y, edge.x).normalized;
        float width = Vector2.Dot(new Vector2(position.x - p2.x, position.z - p2.z), normal);
        return new Vector3(p2.x + normal.x * width, position.y, p2.z + normal.y * width);
    }

    /// <summary>
    /// apply_layout. Accepts only the current edit generation, drawing generated paths as terrain-following bands.
    /// json is a sequence of [start, end] pairs ([x, relative Y, z]) for each path.
    /// </summary>
    private void ApplyGridLayout(string revision, string json)
    {
        if (!IsGridBuild || revision != routeRevision) return;
        float[][][] segments;
        try { segments = JsonConvert.DeserializeObject<float[][][]>(json ?? "null"); }
        catch (JsonException) { return; }
        var lines = new List<List<Vector3>>();
        foreach (var segment in segments ?? System.Array.Empty<float[][]>())
        {
            if (segment == null || segment.Length < 2 || segment[0] == null || segment[1] == null
                || segment[0].Length < 3 || segment[1].Length < 3) continue;
            lines.Add(SampleGroundLine(new Vector2(segment[0][0], segment[0][2]), new Vector2(segment[1][0], segment[1][2]),
                RoadPartHeights.ROAD_SPLINE_OFFSET));
        }
        gridLayoutLines = lines;
        UpdateGridPreview();
    }

    /// <summary>3D grid preview. After point 3, draws core-returned paths as bands when available; otherwise draws nothing.</summary>
    private void UpdateGridPreview()
    {
        if (roadRenderer == null) return;
        roadRenderer.UpdateLineStrips(phase == RoadEditorPhase.Editing ? gridLayoutLines : null, PathSubtype);
    }

    /// <summary>Grid overlay. Draws a provisional grid during placement and after point 3 until core results arrive.</summary>
    private void DrawGridOverlay(Camera cam)
    {
        bool placing = phase == RoadEditorPhase.Placing;
        if (placing && points.Count == 1) DrawPreviewLine(cam);
        if (gridLayoutLines == null && points.Count >= 2 && (points.Count == 3 || placing))
        {
            Vector3 third = points.Count == 3 ? points[2].position : hoverWorldPosition;
            foreach (var line in GetGridGuideLines(points[0].position, points[1].position, third))
                for (int i = 0; i < line.Count - 1; i++)
                    overlayRenderer.AddSolidLine(line[i], line[i + 1], GridGuideColor, solidLineThickness);
        }
        if (placing) DrawPlacingMarker(cam);
        DrawPoints(cam);
    }

    /// <summary>
    /// Returns terrain-following grid lines (including the perimeter), equally dividing the rectangle defined by 3 points.
    /// Matches core generate_rect_grid_segments allocation.
    /// </summary>
    private List<List<Vector3>> GetGridGuideLines(Vector3 p1, Vector3 p2, Vector3 p3)
    {
        int div1 = GridDivisions[0], div2 = GridDivisions[1];
        if (gridGuideLines != null && p1 == gridGuideP1 && p2 == gridGuideP2 && p3 == gridGuideP3
            && div1 == gridGuideDiv1 && div2 == gridGuideDiv2) return gridGuideLines;
        gridGuideP1 = p1; gridGuideP2 = p2; gridGuideP3 = p3;
        gridGuideDiv1 = div1; gridGuideDiv2 = div2;
        gridGuideLines = new List<List<Vector3>>();

        var a = new Vector2(p1.x, p1.z);
        var edge = new Vector2(p2.x, p2.z) - a;
        if (edge.sqrMagnitude < 1e-6f) return gridGuideLines;
        var normal = new Vector2(-edge.y, edge.x).normalized;
        var side = normal * Vector2.Dot(new Vector2(p3.x - p2.x, p3.z - p2.z), normal);
        for (int k = 0; k <= div2; k++)
        {
            Vector2 start = a + side * k / div2;
            gridGuideLines.Add(SampleGroundLine(start, start + edge, 0f));
        }
        for (int k = 0; k <= div1; k++)
        {
            Vector2 start = a + edge * k / div1;
            gridGuideLines.Add(SampleGroundLine(start, start + side, 0f));
        }
        return gridGuideLines;
    }

    private static List<Vector3> SampleGroundLine(Vector2 a, Vector2 b, float lift)
    {
        int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / GridLineSampleStep));
        var line = new List<Vector3>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            Vector2 p = Vector2.Lerp(a, b, (float)i / steps);
            var position = new Vector3(p.x, 0f, p.y);
            position.y = Utils.GetPathSurfaceHeight(position) + lift;
            line.Add(position);
        }
        return line;
    }
}
