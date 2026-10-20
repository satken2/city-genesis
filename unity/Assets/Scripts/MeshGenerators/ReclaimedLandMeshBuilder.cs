using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Build reclaimed-quay top/vertical meshes as pure data. Vertices are world coordinates; GameObject stays at origin.
///
/// Use backend-derived band/wall/top values (<see cref="ReclaimedLandStore"/>) without recomputing decisions here.
/// Receive ground height as a function, allowing scene-free validation without terrain tiles/stores.
/// Caller (<see cref="ReclaimedLandManager"/>) assembles Mesh on the main thread.
/// </summary>
public static class ReclaimedLandMeshBuilder
{
    public const float WALL_SAMPLE_SPACING = 2f;   // Maximum column spacing for matching face bottoms to exterior ground (m).
    public const float WALL_FOOTING_DEPTH = 1f;    // Extra depth below exterior ground, hiding between-column terrain gaps (m).
    public const float WALL_MIN_HEIGHT = 0.05f;    // Minimum retained wall height even when exterior ground exceeds top (m).
    public const float UV_TILE_SIZE = 10f;         // World distance per texture tile (m), matching building concrete foundations.

    /// <summary>Build result. Separate top/wall triangles (both index Vertices).</summary>
    public sealed class MeshData
    {
        public readonly List<Vector3> Vertices = new List<Vector3>();
        public readonly List<Vector3> Normals = new List<Vector3>();
        public readonly List<Vector2> Uvs = new List<Vector2>();
        public readonly List<int> TopTriangles = new List<int>();
        public readonly List<int> WallTriangles = new List<int>();
    }

    /// <summary>
    /// Create top surfaces (band polygons horizontal at top, upward normals) and vertical walls along sections.
    /// Wall top=top; sample ground <see cref="Constants.QUAY_PROBE_DISTANCE"/> outside the section;
    /// bottom lies <see cref="WALL_FOOTING_DEPTH"/> below it, capped at top − <see cref="WALL_MIN_HEIGHT"/>. Normals face section-outward.
    /// </summary>
    /// <param name="band">Band polygons, each [exterior, holes...] rings without closing duplicates.</param>
    /// <param name="walls">Wall sections; Normal is unit outward.</param>
    /// <param name="top">Absolute top elevation.</param>
    /// <param name="groundY">World (x,z) → absolute ground elevation.</param>
    public static MeshData Build(Vector2[][][] band, ReclaimedLandSegment[] walls, float top, Func<float, float, float> groundY)
    {
        var data = new MeshData();
        if (band != null)
        {
            foreach (Vector2[][] polygon in band) AppendTop(data, polygon, top);
        }
        if (walls != null && groundY != null)
        {
            foreach (ReclaimedLandSegment wall in walls) AppendWall(data, wall, top, groundY);
        }
        return data;
    }

    private static void AppendTop(MeshData data, Vector2[][] polygon, float top)
    {
        if (polygon == null || polygon.Length == 0 || polygon[0] == null || polygon[0].Length < 3) return;
        var holes = new List<Vector2[]>();
        for (int i = 1; i < polygon.Length; i++)
        {
            if (polygon[i] != null && polygon[i].Length >= 3) holes.Add(polygon[i]);
        }

        List<Vector2> points;
        List<int> triangles;
        try
        {
            // Share hole-capable triangulation with OpenSpace surfaces (e.g. islands surrounded by quay).
            var surface = new OpenSpaceLayout.Surface { Polygon = polygon[0], Holes = holes.Count > 0 ? holes.ToArray() : null };
            OpenSpaceBuildingMeshBuilder.Triangulate(surface, out points, out triangles);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"ReclaimedLandMeshBuilder: 天端の三角形分割に失敗しました: {e.Message}");
            return;
        }
        if (triangles.Count < 3) return;

        int baseIndex = data.Vertices.Count;
        foreach (Vector2 p in points)
        {
            data.Vertices.Add(new Vector3(p.x, top, p.y));
            data.Normals.Add(Vector3.up);
            data.Uvs.Add(p / UV_TILE_SIZE);
        }
        for (int i = 0; i + 2 < triangles.Count; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            Vector2 pa = points[a], pb = points[b], pc = points[c];
            float cross = (pb.x - pa.x) * (pc.y - pa.y) - (pb.y - pa.y) * (pc.x - pa.x);
            // XZ-counterclockwise triangles face down; reverse to face upward.
            if (cross > 0f)
            {
                data.TopTriangles.Add(baseIndex + a);
                data.TopTriangles.Add(baseIndex + c);
                data.TopTriangles.Add(baseIndex + b);
            }
            else
            {
                data.TopTriangles.Add(baseIndex + a);
                data.TopTriangles.Add(baseIndex + b);
                data.TopTriangles.Add(baseIndex + c);
            }
        }
    }

    private static void AppendWall(MeshData data, ReclaimedLandSegment wall, float top, Func<float, float, float> groundY)
    {
        Vector2 d = wall.B - wall.A;
        float length = d.magnitude;
        if (length < 1e-4f) return;
        Vector2 outward = wall.Normal.sqrMagnitude > 1e-8f ? wall.Normal.normalized : new Vector2(d.y, -d.x) / length;
        var normal = new Vector3(outward.x, 0f, outward.y);

        int columns = Mathf.Max(1, Mathf.CeilToInt(length / WALL_SAMPLE_SPACING));
        int baseIndex = data.Vertices.Count;
        for (int c = 0; c <= columns; c++)
        {
            float t = c / (float)columns;
            Vector2 p = wall.A + d * t;
            Vector2 probe = p + outward * Constants.QUAY_PROBE_DISTANCE;
            float bottom = Mathf.Min(groundY(probe.x, probe.y) - WALL_FOOTING_DEPTH, top - WALL_MIN_HEIGHT);
            float u = length * t / UV_TILE_SIZE;
            data.Vertices.Add(new Vector3(p.x, top, p.y));
            data.Normals.Add(normal);
            data.Uvs.Add(new Vector2(u, top / UV_TILE_SIZE));
            data.Vertices.Add(new Vector3(p.x, bottom, p.y));
            data.Normals.Add(normal);
            data.Uvs.Add(new Vector2(u, bottom / UV_TILE_SIZE));
        }

        // (top i, top i+1, bottom i+1) faces (dz,−dx) in XZ; reverse if opposite outward.
        bool flip = d.y * outward.x - d.x * outward.y < 0f;
        for (int c = 0; c < columns; c++)
        {
            int t0 = baseIndex + c * 2, b0 = t0 + 1, t1 = t0 + 2, b1 = t0 + 3;
            if (flip)
            {
                data.WallTriangles.Add(t0); data.WallTriangles.Add(b1); data.WallTriangles.Add(t1);
                data.WallTriangles.Add(t0); data.WallTriangles.Add(b0); data.WallTriangles.Add(b1);
            }
            else
            {
                data.WallTriangles.Add(t0); data.WallTriangles.Add(t1); data.WallTriangles.Add(b1);
                data.WallTriangles.Add(t0); data.WallTriangles.Add(b1); data.WallTriangles.Add(b0);
            }
        }
    }
}
