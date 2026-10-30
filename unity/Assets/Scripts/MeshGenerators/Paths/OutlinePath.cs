using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>
/// Paths with no physical look (model_type="outline"), such as ferry lanes and ship routes.
/// Draws dashed lines along both edges of the master width with constant screen thickness, following the drawn centerline including curves.
/// The look is the same at every LOD, so the whole path is drawn regardless of detail ranges, and Far paths use <see cref="Create"/> too.
/// The lines are overlays and keep their own color in previews, plans, and highlights.
/// </summary>
public sealed class OutlinePath : IPathMeshGenerator
{
    private const float DashLength = 8f, GapLength = 6f, PixelThickness = 3f;
    private static readonly Color LineColor = new Color(1f, 1f, 1f, .9f);

    public PathMeshResult Generate(PathSummary pathData, List<Vector3> points, SidewalkSetback startSidewalkSetback = default, SidewalkSetback endSidewalkSetback = default)
    {
        if (points == null || points.Count < 2)
        {
            Debug.LogWarning($"[OutlinePath] ポイント不足 ({points?.Count ?? 0}件)。生成スキップ。");
            return new PathMeshResult(null, false);
        }
        PathDrawContext.SuppressDetailRange = true;
        var container = new GameObject($"Outline_{pathData.path_id}");
        Create(container, points, pathData.path_variant);
        Mesh colliderMesh = PathMeshFactory.BuildStripMesh(points, Width(pathData.path_variant));
        if (colliderMesh != null) colliderMesh.name = $"OutlineCollider_{pathData.path_id}";
        return new PathMeshResult(colliderMesh, true, container);
    }

    /// <summary>Add the dashed edges of the variant's width along the centerline points under parent.</summary>
    public static void Create(GameObject parent, List<Vector3> points, int variant)
    {
        float half = Width(variant) * .5f;
        if (points == null || points.Count < 2 || half <= 0f) return;
        var overlay = OverlayMeshRenderer.CreateStatic("Outline", true, parent.transform);
        AddDashes(overlay, points, -half);
        AddDashes(overlay, points, half);
        overlay.EndStaticFrame();
    }

    private static float Width(int variant) => MasterDataStore.Instance?.GetPathMaster(variant)?.width ?? 0f;

    /// <summary>Dashes along the line offset horizontally by lateral (right positive). The dash pattern continues across points so curves stay evenly dashed.</summary>
    private static void AddDashes(OverlayMeshRenderer overlay, List<Vector3> points, float lateral)
    {
        const float period = DashLength + GapLength;
        Vector3 previous = Offset(points, 0, lateral);
        float travelled = 0f;
        for (int i = 1; i < points.Count; i++)
        {
            Vector3 current = Offset(points, i, lateral);
            float length = Vector3.Distance(previous, current);
            for (float s = 0f; s < length;)
            {
                float phase = (travelled + s) % period;
                bool dash = phase < DashLength;
                float next = Mathf.Min(s + Mathf.Max((dash ? DashLength : period) - phase, .001f), length);
                if (dash) overlay.AddSolidLine(Vector3.Lerp(previous, current, s / length), Vector3.Lerp(previous, current, next / length), LineColor, PixelThickness);
                s = next;
            }
            travelled += length;
            previous = current;
        }
    }

    private static Vector3 Offset(List<Vector3> points, int index, float lateral)
    {
        Vector3 direction = PolylineUtils.SmoothedDirection(points, index);
        direction.y = 0f;
        Vector3 right = direction.sqrMagnitude > 1e-8f ? Vector3.Cross(Vector3.up, direction).normalized : Vector3.right;
        return points[index] + right * lateral;
    }
}
