using System.Collections.Generic;
using UnityEngine;

/// <summary>元の占有輪郭内に外階段の帯を確保し、頂点3を原点として各階へ配置する。</summary>
public static class ExteriorStairBuilder
{
    public sealed class Layout
    {
        public Vector2[] Body;
        public Vector3 Origin;
        public Quaternion Rotation;
        public float OpeningStart, OpeningEnd;
        public float MidScale;
        public readonly Mesh[] Meshes = new Mesh[3];
        public readonly Material[][] Materials = new Material[3][];

        public void Release()
        {
            foreach (Mesh mesh in Meshes) if (mesh != null) Object.Destroy(mesh);
        }

        public void Append(int floors, float height, List<CombineInstance> instances,
            List<Material> materials, List<Mesh> temporary)
        {
            for (int floor = 0; floor < floors; floor++)
            {
                int level = floor == 0 ? 0 : floor == floors - 1 ? 2 : 1;
                Mesh mesh = Meshes[level];
                var matrix = Matrix4x4.TRS(Origin + Vector3.up * (floor * height), Rotation,
                    new Vector3(1, level == 1 ? MidScale : 1, 1));
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    instances.Add(new CombineInstance { mesh = mesh, subMeshIndex = sub, transform = matrix });
                    materials.Add(Materials[level][sub]);
                }
            }
            temporary.AddRange(Meshes);
        }
    }

    public static Layout Create(Vector2[] footprint, string prefix, int suffix, float floorHeight)
    {
        if (footprint == null || footprint.Length < 4 || suffix < 0) return null;
        if (!BuildingMeshUtils.HasModelAtPath($"Models/Buildings/{prefix}/{suffix}/mid-stair")) return null;
        var layout = new Layout();
        Bounds bounds = new Bounds();
        for (int level = 0; level < 3; level++)
        {
            string name = level == 0 ? "base-stair" : level == 1 ? "mid-stair" : "top-stair";
            var part = BuildingMeshUtils.GetPartNoPosition(prefix, name, suffix, true)
                ?? BuildingMeshUtils.GetPartNoPosition(prefix, "mid-stair", suffix, true);
            layout.Meshes[level] = BuildingMeshUtils.CombineModuleMeshes(part, out layout.Materials[level]);
            if (layout.Meshes[level] == null) { layout.Release(); return null; }
            if (level == 0) bounds = layout.Meshes[level].bounds;
            else bounds.Encapsulate(layout.Meshes[level].bounds);
        }
        // 手すり・床厚を含む階段のbbox高さではなく、同じ建物の中層壁の著作階高で倍率を決める。
        var wall = BuildingMeshUtils.GetPart(prefix, "mid", "w", 1, suffix, true);
        Mesh reference = BuildingMeshUtils.CombineModuleMeshes(wall, out _);
        float authoredHeight = reference != null ? reference.bounds.size.y : 0;
        if (reference != null) Object.Destroy(reference);
        if (authoredHeight <= .01f || !TryLayout(footprint, bounds, out var body, out var origin,
            out var rotation, out float from, out float to)) { layout.Release(); return null; }
        layout.Body = body; layout.Origin = origin; layout.Rotation = rotation;
        layout.OpeningStart = from; layout.OpeningEnd = to;
        layout.MidScale = floorHeight / authoredHeight;
        return layout;
    }

    /// <summary>辺2を平行移動し隣辺との交点を求める。成立しない敷地では元の描画を維持する。</summary>
    public static bool TryLayout(Vector2[] polygon, Bounds bounds, out Vector2[] body,
        out Vector3 origin, out Quaternion rotation, out float openingStart, out float openingEnd)
    {
        body = null; origin = Vector3.zero; rotation = Quaternion.identity;
        openingStart = openingEnd = 0;
        if (polygon == null || polygon.Length < 4 || PolygonUtils.SignedArea(polygon) <= 0) return false;
        Vector2 edge = polygon[3] - polygon[2];
        if (edge.magnitude < .1f || bounds.size.x < .1f || -bounds.min.z < .1f
            || bounds.min.x < -.01f || bounds.max.z > .01f) return false;
        Vector2 along = edge.normalized;
        Vector2 inward = new Vector2(-along.y, along.x);
        float depth = -bounds.min.z;
        body = (Vector2[])polygon.Clone();
        if (!Intersect(polygon[2] + inward * depth, along, polygon[1], polygon[2] - polygon[1], out body[2])
            || !Intersect(polygon[3] + inward * depth, along, polygon[3], polygon[4 % polygon.Length] - polygon[3], out body[3])) return false;
        // 辺を反転・自己交差させず、本体と外階段を元の輪郭内に収める。
        if (Vector2.Dot(body[3] - body[2], along) < .1f || !SimpleInside(body, polygon)
            || PolygonUtils.SignedArea(body) < .1f) return false;
        origin = new Vector3(polygon[3].x, 0, polygon[3].y);
        rotation = Quaternion.LookRotation(new Vector3(-inward.x, 0, -inward.y));
        var corners = new Vector2[4];
        int index = 0;
        foreach (var local in new[] {new Vector2(bounds.min.x,bounds.min.z),new Vector2(bounds.max.x,bounds.min.z),
            new Vector2(bounds.max.x,bounds.max.z),new Vector2(bounds.min.x,bounds.max.z)})
        {
            Vector3 p = origin + rotation * new Vector3(local.x, 0, local.y);
            corners[index++] = new Vector2(p.x,p.z);
        }
        if (!SimpleInside(corners, polygon)) return false;
        openingStart = float.PositiveInfinity; openingEnd = float.NegativeInfinity;
        foreach (var point in corners)
        {
            float distance = Vector2.Dot(point - body[2], along);
            openingStart = Mathf.Min(openingStart, distance);
            openingEnd = Mathf.Max(openingEnd, distance);
        }
        return true;
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
}
