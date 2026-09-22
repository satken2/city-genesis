using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// モジュラーパーツが存在しない場合のフォールバック建物躯体生成器。
/// 底面ポリゴンを高さまで押し出した側面（壁）と地面パネルを生成する。
/// 屋根は生成しない（屋根ビルダーが別途担当する）。
/// </summary>
public static class DefaultBodyBuilder
{
    /// <summary>
    /// 底面ポリゴンを押し出した壁面と地面パネルを生成する。
    /// モジュラーパーツが一切存在しない建物に対して、最低限の躯体形状を提供するフォールバック。
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

        // 側面（壁）メッシュを生成
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

        // 地面パネル
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
    /// 底面ポリゴンの各辺を高さまで押し出した側面メッシュを生成する。
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
