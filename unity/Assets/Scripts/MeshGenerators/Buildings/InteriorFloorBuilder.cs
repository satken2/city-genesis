using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Add floors following the outline to each level of modular buildings with interior-floor parts.
/// Parts supply thickness/material references. Unlike ground floor parts, do not deform them to terrain.
/// </summary>
public static class InteriorFloorBuilder
{
    private static readonly StageMarker appendMarker = new StageMarker("Building.InteriorFloors");

    public static bool Append(Vector2[] footprint, int floors, float floorHeight,
        string prefix, int suffix, bool includeRoof,
        List<CombineInstance> instances, List<Material> materials, List<Mesh> temporary)
    {
        using var scope = appendMarker.Auto();
        var part = BuildingMeshUtils.GetPartNoPosition(prefix, "interior-floor", suffix, true);
        if (part == null || footprint == null || footprint.Length < 3) return false;
        Mesh reference = BuildingMeshUtils.CombineModuleMeshes(part, out Material[] partMaterials);
        if (reference == null) return false;
        float thickness = reference.bounds.size.y;
        Object.Destroy(reference);
        if (thickness <= .001f || thickness >= floorHeight || partMaterials.Length < 1) return false;

        Material surface = partMaterials[0];
        Material concrete = partMaterials.Length > 1 ? partMaterials[1] : surface;
        Mesh slab = CreateSlab(footprint, thickness);
        if (slab == null) return false;
        temporary.Add(slab);
        for (int floor = 0; floor < floors + (includeRoof ? 1 : 0); floor++)
        {
            // Separate only ground-floor pavement from terrain floor and foundation tops. Keep upper floors on the floor-height grid.
            float y = floor == 0 ? .02f : floor * floorHeight;
            var transform = Matrix4x4.Translate(Vector3.up * y);
            for (int sub = 0; sub < 2; sub++)
            {
                instances.Add(new CombineInstance { mesh = slab, subMeshIndex = sub, transform = transform });
                materials.Add(sub == 0 ? surface : concrete);
            }
        }
        return true;
    }

    /// <summary>Top at 0, bottom at −thickness. Close concave shapes with shared ear-clipping triangulation.</summary>
    public static Mesh CreateSlab(Vector2[] footprint, float thickness)
    {
        var triangles = BuildingMeshBuilder.TriangulateEarClip(footprint);
        if (triangles.Count < 3) return null;
        var vertices = new List<Vector3>();
        var uv = new List<Vector2>();
        var top = new List<int>();
        var shell = new List<int>();
        int n = footprint.Length;
        for (int side = 0; side < 2; side++)
            foreach (Vector2 p in footprint)
            {
                vertices.Add(new Vector3(p.x, side == 0 ? 0f : -thickness, p.y));
                uv.Add(p * .2f);
            }
        for (int i = 0; i < triangles.Count; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            top.Add(a); top.Add(c); top.Add(b);
            shell.Add(n + a); shell.Add(n + b); shell.Add(n + c);
        }
        for (int i = 0; i < n; i++)
        {
            Vector2 a = footprint[i], b = footprint[(i + 1) % n];
            int start = vertices.Count;
            vertices.Add(new Vector3(a.x, 0, a.y));
            vertices.Add(new Vector3(a.x, -thickness, a.y));
            vertices.Add(new Vector3(b.x, -thickness, b.y));
            vertices.Add(new Vector3(b.x, 0, b.y));
            float length = Vector2.Distance(a, b);
            uv.Add(Vector2.zero); uv.Add(new Vector2(0, thickness) * .2f);
            uv.Add(new Vector2(length, thickness) * .2f); uv.Add(new Vector2(length, 0) * .2f);
            shell.Add(start); shell.Add(start + 2); shell.Add(start + 1);
            shell.Add(start); shell.Add(start + 3); shell.Add(start + 2);
        }
        var mesh = new Mesh { name = "InteriorFloorSlab" };
        mesh.SetVertices(vertices); mesh.SetUVs(0, uv); mesh.subMeshCount = 2;
        mesh.SetTriangles(top, 0); mesh.SetTriangles(shell, 1);
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        return mesh;
    }
}
