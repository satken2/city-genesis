using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Keep external components (per-edge stairs {base|mid|top}-stair{edgeIndex+1} and front entrance) within the original occupied outline,
/// creating a body outline with component-bearing edges set back. Place exterior stairs on each floor relative to the edge endpoint.
/// Unlike walls/corners, position numbers have no fallback. Omit stairs on edges without mid-stair{n}.
/// </summary>
public static class ExteriorStairBuilder
{
    static readonly string[] Levels = { "base", "mid", "top" };
    static readonly StageMarker createMarker = new StageMarker("Building.ExteriorStairs");

    /// <summary>Exterior stairs for one edge.</summary>
    public sealed class Stair
    {
        public int Edge;
        public Bounds Bounds;
        public Vector3 Origin;
        public Quaternion Rotation;
        public float OpeningStart, OpeningEnd; // Opening range measured from the edge start on the same edge of the setback body outline.
        public readonly Mesh[] Meshes = new Mesh[3];
        public readonly Material[][] Materials = new Material[3][];

        public void Release()
        {
            foreach (Mesh mesh in Meshes) if (mesh != null) Object.Destroy(mesh);
        }
    }

    public sealed class Layout
    {
        public Vector2[] Body;
        public float MidScale;
        public Bounds? Entrance; // Accepted entrance bounds (enclose standard/close-up). Null omits the entrance.
        public readonly List<Stair> Stairs = new List<Stair>();

        /// <summary>Release loaded part meshes when discarded without Append.</summary>
        public void Release()
        {
            foreach (var stair in Stairs) stair.Release();
        }

        public bool TryGetOpening(int edge, out float start, out float end)
        {
            foreach (var stair in Stairs)
            {
                if (stair.Edge != edge) continue;
                start = stair.OpeningStart; end = stair.OpeningEnd;
                return true;
            }
            start = end = 0;
            return false;
        }

        /// <summary>Omit corner parts at vertices reached by an opening, as they overlap stairs.</summary>
        public bool OmitsCorner(int vertex)
        {
            int n = Body.Length;
            foreach (var stair in Stairs)
            {
                int end = (stair.Edge + 1) % n;
                if (stair.Edge == vertex && stair.OpeningStart <= .01f) return true;
                if (end == vertex && stair.OpeningEnd >= Vector2.Distance(Body[stair.Edge], Body[end]) - .01f) return true;
            }
            return false;
        }

        public void Append(int floors, float height, List<CombineInstance> instances,
            List<Material> materials, List<Mesh> temporary)
        {
            foreach (var stair in Stairs)
            {
                for (int floor = 0; floor < floors; floor++)
                {
                    int level = floor == 0 ? 0 : floor == floors - 1 ? 2 : 1;
                    Mesh mesh = stair.Meshes[level];
                    var matrix = Matrix4x4.TRS(stair.Origin + Vector3.up * (floor * height), stair.Rotation,
                        new Vector3(1, level == 1 ? MidScale : 1, 1));
                    for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    {
                        instances.Add(new CombineInstance { mesh = mesh, subMeshIndex = sub, transform = matrix });
                        materials.Add(stair.Materials[level][sub]);
                    }
                }
                temporary.AddRange(stair.Meshes);
            }
        }
    }

    /// <summary>
    /// Accept/reject the entrance first, then exterior stairs in order from edge 0. Omit components incompatible with accepted ones.
    /// Return null if no component is accepted (body outline stays unchanged).
    /// </summary>
    /// <param name="withStairs">Whether to place exterior stairs. Omit for SHAPE_FULL, which draws only the top floor.</param>
    /// <param name="entrance">Entrance bounds (enclose standard/close-up). Null for buildings without an entrance or part.</param>
    public static Layout Create(Vector2[] footprint, string prefix, int suffix, float floorHeight, bool withStairs, Bounds? entrance)
    {
        using var scope = createMarker.Auto();
        if (footprint == null || footprint.Length < 3 || suffix < 0) return null;
        var layout = new Layout();
        if (entrance.HasValue && TryLayout(footprint, layout.Stairs, entrance, out var body))
        {
            layout.Body = body;
            layout.Entrance = entrance;
        }
        if (withStairs) AddStairs(layout, footprint, prefix, suffix, floorHeight);
        return layout.Body != null ? layout : null;
    }

    static void AddStairs(Layout layout, Vector2[] footprint, string prefix, int suffix, float floorHeight)
    {
        var candidates = new List<Stair>();
        for (int edge = 0; edge < footprint.Length; edge++)
        {
            var stair = Load(prefix, edge, suffix);
            if (stair != null) candidates.Add(stair);
        }
        if (candidates.Count == 0) return;
        // Determine scale from the same building's authored mid-wall floor height, not the stairs' bounding-box height including railings/slab thickness.
        var wall = BuildingMeshUtils.GetPart(prefix, "mid", "w", 1, suffix, true);
        Mesh reference = BuildingMeshUtils.CombineModuleMeshes(wall, out _);
        float authoredHeight = reference != null ? reference.bounds.size.y : 0;
        if (reference != null) Object.Destroy(reference);
        if (authoredHeight <= .01f)
        {
            foreach (var stair in candidates) stair.Release();
            return;
        }
        layout.MidScale = floorHeight / authoredHeight;
        // A successful trial rewrites placement for all accepted components, so openings are always measured on the final body outline.
        foreach (var stair in candidates)
        {
            layout.Stairs.Add(stair);
            if (TryLayout(footprint, layout.Stairs, layout.Entrance, out var body)) { layout.Body = body; continue; }
            layout.Stairs.RemoveAt(layout.Stairs.Count - 1);
            stair.Release();
        }
    }

    static Stair Load(string prefix, int edge, int suffix)
    {
        int position = edge + 1;
        if (!BuildingMeshUtils.HasPart(prefix, "mid", "stair", position, suffix)) return null;
        var stair = new Stair { Edge = edge };
        for (int level = 0; level < 3; level++)
        {
            var part = BuildingMeshUtils.GetPart(prefix, Levels[level], "stair", position, suffix, true)
                ?? BuildingMeshUtils.GetPart(prefix, "mid", "stair", position, suffix, true);
            stair.Meshes[level] = BuildingMeshUtils.CombineModuleMeshes(part, out stair.Materials[level]);
            if (stair.Meshes[level] == null) { stair.Release(); return null; }
            if (level == 0) stair.Bounds = stair.Meshes[level].bounds;
            else stair.Bounds.Encapsulate(stair.Meshes[level].bounds);
        }
        return stair;
    }

    /// <summary>
    /// Shift component-bearing edges inward by their maximum component depth; intersect adjacent lines (setback lines if applicable) to form the body outline.
    /// Align stairs' inner faces and the entrance's wall alignment line to the setback body face.
    /// Reject layouts causing flipped edges, self-intersections, protrusion beyond the original outline, or overlaps between stairs or with the body.
    /// Write stair origins, orientations, and openings only on success.
    /// </summary>
    public static bool TryLayout(Vector2[] polygon, IReadOnlyList<Stair> stairs, Bounds? entrance, out Vector2[] body)
    {
        body = null;
        int n = polygon?.Length ?? 0;
        if (n < 3 || stairs == null || PolygonUtils.SignedArea(polygon) <= 0) return false;
        var depths = new float[n];
        foreach (var stair in stairs)
        {
            if (stair.Edge < 0 || stair.Edge >= n || depths[stair.Edge] > 0) return false;
            Bounds bounds = stair.Bounds;
            if (Vector2.Distance(polygon[stair.Edge], polygon[(stair.Edge + 1) % n]) < .1f || bounds.size.x < .1f
                || -bounds.min.z < .1f || bounds.min.x < -.01f || bounds.max.z > .01f) return false;
            depths[stair.Edge] = -bounds.min.z;
        }
        // Entrance depth is its exterior (+Z) projection. The inward side (-Z) may overlap the body.
        if (entrance.HasValue) depths[0] = Mathf.Max(depths[0], entrance.Value.max.z);
        var result = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            int prev = (i + n - 1) % n;
            if (depths[prev] <= 0 && depths[i] <= 0) { result[i] = polygon[i]; continue; }
            if (!Intersect(polygon[prev] + Inward(polygon, prev) * depths[prev], polygon[i] - polygon[prev],
                polygon[i] + Inward(polygon, i) * depths[i], polygon[(i + 1) % n] - polygon[i], out result[i])) return false;
        }
        for (int i = 0; i < n; i++)
        {
            int prev = (i + n - 1) % n, next = (i + 1) % n;
            if (depths[prev] <= 0 && depths[i] <= 0 && depths[next] <= 0) continue;
            Vector2 along = (polygon[next] - polygon[i]).normalized;
            if (Vector2.Dot(result[next] - result[i], along) < (depths[i] > 0 ? .1f : .01f)) return false;
        }
        if (!SimpleInside(result, polygon) || PolygonUtils.SignedArea(result) < .1f) return false;
        // Place the entrance at the center of the setback front edge (BuildingMeshBuilder.GenerateEntrance).
        // Do not test overlap with exterior stairs.
        if (entrance.HasValue)
        {
            Vector2 middle = (result[0] + result[1]) * .5f;
            if (!SimpleInside(Rectangle(entrance.Value, new Vector3(middle.x, 0, middle.y), Facing(polygon, 0)), polygon)) return false;
        }

        var origins = new Vector3[stairs.Count];
        var rotations = new Quaternion[stairs.Count];
        var openings = new Vector2[stairs.Count];
        var footprints = new Vector2[stairs.Count][];
        for (int k = 0; k < stairs.Count; k++)
        {
            int start = stairs[k].Edge, end = (start + 1) % n;
            Vector2 along = (polygon[end] - polygon[start]).normalized;
            Bounds bounds = stairs[k].Bounds;
            // Align the inner face to the body face. If these stairs determine the edge setback, this is the original edge endpoint.
            Vector2 origin = polygon[end] + Inward(polygon, start) * (depths[start] + bounds.min.z);
            origins[k] = new Vector3(origin.x, 0, origin.y);
            rotations[k] = Facing(polygon, start);
            var corners = Rectangle(bounds, origins[k], rotations[k]);
            if (!SimpleInside(corners, polygon) || Overlaps(corners, result)) return false;
            for (int j = 0; j < k; j++) if (Overlaps(corners, footprints[j])) return false;
            footprints[k] = corners;
            float from = float.PositiveInfinity, to = float.NegativeInfinity;
            foreach (var point in corners)
            {
                float distance = Vector2.Dot(point - result[start], along);
                from = Mathf.Min(from, distance);
                to = Mathf.Max(to, distance);
            }
            openings[k] = new Vector2(from, to);
        }
        for (int k = 0; k < stairs.Count; k++)
        {
            stairs[k].Origin = origins[k];
            stairs[k].Rotation = rotations[k];
            stairs[k].OpeningStart = openings[k].x;
            stairs[k].OpeningEnd = openings[k].y;
        }
        body = result;
        return true;
    }

    /// <summary>Unit inward normal of edge in a counterclockwise outline.</summary>
    static Vector2 Inward(Vector2[] polygon, int edge)
    {
        Vector2 along = (polygon[(edge + 1) % polygon.Length] - polygon[edge]).normalized;
        return new Vector2(-along.y, along.x);
    }

    /// <summary>Rotation facing local +Z outward from edge. Same orientation as wall placement (face along edge, then rotate 180° about Y).</summary>
    static Quaternion Facing(Vector2[] polygon, int edge)
    {
        Vector2 inward = Inward(polygon, edge);
        return Quaternion.LookRotation(new Vector3(-inward.x, 0, -inward.y));
    }

    /// <summary>Four corners of the placed part's horizontal bounds.</summary>
    static Vector2[] Rectangle(Bounds bounds, Vector3 origin, Quaternion rotation)
    {
        var corners = new Vector2[4];
        int index = 0;
        foreach (var local in new[] {new Vector2(bounds.min.x,bounds.min.z),new Vector2(bounds.max.x,bounds.min.z),
            new Vector2(bounds.max.x,bounds.max.z),new Vector2(bounds.min.x,bounds.max.z)})
        {
            Vector3 p = origin + rotation * new Vector3(local.x, 0, local.y);
            corners[index++] = new Vector2(p.x, p.z);
        }
        return corners;
    }

    static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    static bool Intersect(Vector2 a, Vector2 da, Vector2 b, Vector2 db, out Vector2 p)
    {
        float det = Cross(da, db); p = a;
        if (Mathf.Abs(det) < .0001f) return false;
        p = a + da * (Cross(b - a, db) / det);
        return true;
    }
    static bool Crosses(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        float ab1 = Cross(b-a,c-a), ab2 = Cross(b-a,d-a);
        float cd1 = Cross(d-c,a-c), cd2 = Cross(d-c,b-c);
        float abTolerance = (b-a).magnitude * .005f;
        float cdTolerance = (d-c).magnitude * .005f;
        return ((ab1 > abTolerance && ab2 < -abTolerance) || (ab2 > abTolerance && ab1 < -abTolerance))
            && ((cd1 > cdTolerance && cd2 < -cdTolerance) || (cd2 > cdTolerance && cd1 < -cdTolerance));
    }
    static bool SimpleInside(Vector2[] ring, Vector2[] boundary)
    {
        for (int i=0;i<ring.Length;i++)
        {
            Vector2 a=ring[i], b=ring[(i+1)%ring.Length];
            if (!PolygonUtils.Contains(boundary,a,.005f) || !PolygonUtils.Contains(boundary,(a+b)*.5f,.005f)) return false;
            for (int j=0;j<boundary.Length;j++)
                if (Crosses(a,b,boundary[j],boundary[(j+1)%boundary.Length])) return false;
            for (int j=i+1;j<ring.Length;j++)
                if (Crosses(a,b,ring[j],ring[(j+1)%ring.Length])) return false;
        }
        return true;
    }

    /// <summary>Whether interiors overlap. Sharing an edge only (the body/exterior-stair band) is not overlap.</summary>
    static bool Overlaps(Vector2[] a, Vector2[] b)
    {
        for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++)
                if (Crosses(a[i], a[(i+1)%a.Length], b[j], b[(j+1)%b.Length])) return true;
        return HasPointInside(a, b) || HasPointInside(b, a);
    }
    static bool HasPointInside(Vector2[] ring, Vector2[] boundary)
    {
        Vector2 centroid = Vector2.zero;
        for (int i = 0; i < ring.Length; i++)
        {
            Vector2 a = ring[i], b = ring[(i+1)%ring.Length];
            centroid += a;
            if (StrictlyInside(boundary, a) || StrictlyInside(boundary, (a+b)*.5f)) return true;
        }
        return StrictlyInside(boundary, centroid / ring.Length);
    }
    static bool StrictlyInside(Vector2[] polygon, Vector2 point)
        => PolygonUtils.Contains(polygon, point) && PolygonUtils.DistanceToBoundary(polygon, point) > .005f;
}
