using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Fallback building-body generator when no modular parts exist.
/// Generate side walls by extruding the footprint and a ground panel.
/// No roof (handled separately by roof builders).
/// </summary>
public static class DefaultBodyBuilder
{
    /// <summary>
    /// Generate extruded footprint walls and a ground panel.
    /// Fallback providing minimal body geometry for buildings without modular parts.
    /// </summary>
    public static ModularBodyBuilder.BodyMeshData GenerateBody(Vector2[] footprint, int floors, float floorHeight)
    {
        float buildingHeight = floors * floorHeight;
        Vector2[] normalizedFootprint = (Vector2[])footprint.Clone();

        if (PolygonUtils.SignedArea(normalizedFootprint) < 0f)
        {
            System.Array.Reverse(normalizedFootprint);
        }

        List<CombineInstance> combineInstances = new List<CombineInstance>();
        List<Material> allMaterials = new List<Material>();
        List<Mesh> temporaryMeshes = new List<Mesh>();

        // Generate side (wall) mesh.
        Mesh wallMesh = GenerateWallMesh(normalizedFootprint, buildingHeight);
        if (wallMesh != null)
        {
            combineInstances.Add(new CombineInstance
            {
                mesh = wallMesh,
                subMeshIndex = 0,
                transform = Matrix4x4.identity
            });
            allMaterials.Add(null);
            temporaryMeshes.Add(wallMesh);
        }

        // Ground panel.
        Mesh floorMesh = BuildingMeshBuilder.GenerateHorizontalPolygonMesh(normalizedFootprint, ModularBodyBuilder.GROUND_OFFSET);
        if (floorMesh != null)
        {
            combineInstances.Add(new CombineInstance
            {
                mesh = floorMesh,
                subMeshIndex = 0,
                transform = Matrix4x4.identity
            });
            allMaterials.Add(null);
            temporaryMeshes.Add(floorMesh);
        }

        return new ModularBodyBuilder.BodyMeshData
        {
            CombineInstances = combineInstances,
            Materials = allMaterials,
            TemporaryMeshes = temporaryMeshes,
            ResolvedSuffix = 0,
            BuildingHeight = buildingHeight
        };
    }

    /// <summary>
    /// Generate side meshes by extruding each footprint edge to the height.
    /// </summary>
    private static Mesh GenerateWallMesh(Vector2[] footprint, float buildingHeight)
    {
        int n = footprint.Length;
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();

        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            Vector3 v0 = new Vector3(footprint[i].x, 0, footprint[i].y);
            Vector3 v1 = new Vector3(footprint[next].x, 0, footprint[next].y);
            Vector3 v2 = new Vector3(footprint[i].x, buildingHeight, footprint[i].y);
            Vector3 v3 = new Vector3(footprint[next].x, buildingHeight, footprint[next].y);

            vertices.Add(v0);
            vertices.Add(v1);
            vertices.Add(v2);
            triangles.Add(vertices.Count - 3);
            triangles.Add(vertices.Count - 1);
            triangles.Add(vertices.Count - 2);

            vertices.Add(v1);
            vertices.Add(v3);
            vertices.Add(v2);
            triangles.Add(vertices.Count - 3);
            triangles.Add(vertices.Count - 1);
            triangles.Add(vertices.Count - 2);
        }

        Mesh mesh = new Mesh();
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
