using System.Collections.Generic;
using Dreamteck.Splines;
using UnityEngine;

/// <summary>Extend lower platform exterior walls from track surface to ground. Preserve shelter/window dimensions.</summary>
public static class PlatformFoundationGenerator
{
    /// <summary>
    /// trackOffset is lateral center of the track facing the platform (TrackPath.PlatformTrackOffset; zero for single track).
    /// Parts follow origin=track centerline/right-side convention, so place outward from this position.
    /// At S-curve joins, move with track by PathDrawContext.Warp lateral offset.
    /// </summary>
    public static void Generate(GameObject parent, SplineComputer spline, string variantBase,
        string typeBase, bool leftSide, float trackOffset, double from, double to)
    {
        var prefab = PathDrawContext.ResolvePrefab($"{variantBase}/platform_foundation",
            $"{typeBase}/platform_foundation", out _);
        if (prefab == null || !PathDrawContext.IntersectDetailRange(from, to, out from, out to)) return;
        var filter = prefab.GetComponentInChildren<MeshFilter>();
        var renderer = prefab.GetComponentInChildren<MeshRenderer>();
        Mesh source = filter != null ? filter.sharedMesh : null;
        if (source == null || renderer == null || source.vertexCount > RoadPath.SPLINE_MESH_VERTEX_BUDGET) return;
        var bounds = source.bounds;
        // Author top at origin height (track surface), bottom below origin. Extend the bottom to local ground.
        if (bounds.min.y >= 0 || Mathf.Abs(bounds.max.y) > .001f || bounds.size.z < .01f) return;
        float length = spline.CalculateLength(from, to);
        if (length < .01f) return;
        int tiles = Mathf.Max(1, Mathf.CeilToInt(length / bounds.size.z));
        var batch = new PathStructureMesh(parent, "PlatformFoundation", renderer.sharedMaterials);
        var vertices = source.vertices;
        var world = new Vector3[vertices.Length];
        float side = leftSide ? -1f : 1f;
        PartWarp warp = PathDrawContext.Warp;
        for (int tile = 0; tile < tiles; tile++)
        {
            var sections = new Dictionary<float, (Vector3 point, Vector3 right, float shift)>();
            var groundBySection = new Dictionary<Vector2, float>();
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 v = vertices[i];
                if (!sections.TryGetValue(v.z, out var section))
                {
                    float distance = (tile + (v.z - bounds.min.z) / bounds.size.z) * length / tiles;
                    double percent = RoadPath.ClampSplinePercent(spline.Travel(from, distance, Spline.Direction.Forward));
                    var sample = spline.Evaluate(percent);
                    section = (sample.position, Vector3.Cross(Vector3.up, sample.forward).normalized, warp?.Shift != null ? warp.Shift(percent) : 0f);
                    sections.Add(v.z, section);
                }
                Vector3 top = section.point + section.right * (trackOffset + section.shift + side * v.x)
                    + Vector3.up * RoadPartHeights.PLATFORM_BASE_ABOVE_SPLINE;
                var key = new Vector2(v.x, v.z);
                if (!groundBySection.TryGetValue(key, out float ground))
                {
                    // Bury 15cm to avoid floating hems from terrain error; evaluate slopes at exterior-wall positions.
                    ground = Mathf.Min(top.y, Utils.GetGroundHeight(top) - .15f);
                    groundBySection.Add(key, ground);
                }
                world[i] = new Vector3(top.x, Mathf.Lerp(top.y, ground, v.y / bounds.min.y), top.z);
            }
            batch.Append(source, world, reverseWinding: leftSide);
        }
        batch.Finish();
    }
}
