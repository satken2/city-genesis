using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Distant (Far) paths. Tile one variant-specific distant model (`Models/Paths/p{variant}/far`) along points.
/// Count = rounded total length / model local-Z length (at least 1); stretch each lengthwise into equal-length sections.
/// Width/height stay actual size. Map vertices onto points with local X horizontal-right and Y vertical,
/// keeping posts/towers upright while road surfaces follow grade. Section-boundary vertices coincide, preventing seams.
/// Model origin lies on the rendered centerline (ground for base_height paths); author distant models around that origin.
/// Variants without distant models tile master-width plates using the same rules.
/// Use DepthOffsetLit variants of model materials (road material for plates) to prevent burial in coarse distant terrain.
/// </summary>
public static class PathFarModel
{
    /// <summary>Plate length for variants without distant models (m).</summary>
    private const float PLATE_LENGTH = 15f;

    private sealed class Tile
    {
        public Vector3[] vertices;
        public Vector2[] uv;
        public int[][] triangles;
        public Material[] materials;
        public float minZ, length;
    }

    // Models per variant; null for variants with nothing to draw. Prefabs (asset packs) and masters may differ
    // by city, so discard on city switches.
    private static readonly Dictionary<int, Tile> tiles = new Dictionary<int, Tile>();

    static PathFarModel() => AssetPackStore.CityChanging += tiles.Clear;

    /// <summary>Mesh tiled along points. Null if nothing to draw. Caller releases returned meshes.</summary>
    public static Mesh Build(int variant, List<Vector3> points, out Material[] materials)
    {
        materials = null;
        Tile tile = GetTile(variant);
        if (tile == null || points == null || points.Count < 2)
        {
            return null;
        }
        var cumulative = new float[points.Count];
        for (int i = 1; i < points.Count; i++)
        {
            cumulative[i] = cumulative[i - 1] + Vector3.Distance(points[i - 1], points[i]);
        }
        float total = cumulative[points.Count - 1];
        if (total < 0.01f)
        {
            return null;
        }

        int count = Mathf.Max(1, Mathf.RoundToInt(total / tile.length));
        float step = total / count;
        var vertices = new List<Vector3>(count * tile.vertices.Length);
        var uv = tile.uv != null ? new List<Vector2>(count * tile.uv.Length) : null;
        var triangles = new List<int>[tile.triangles.Length];
        for (int s = 0; s < triangles.Length; s++)
        {
            triangles[s] = new List<int>(count * tile.triangles[s].Length);
        }
        for (int i = 0; i < count; i++)
        {
            int first = vertices.Count;
            foreach (Vector3 v in tile.vertices)
            {
                Sample(points, cumulative, (i + (v.z - tile.minZ) / tile.length) * step, out Vector3 position, out Vector3 right);
                vertices.Add(position + right * v.x + Vector3.up * v.y);
            }
            uv?.AddRange(tile.uv);
            for (int s = 0; s < triangles.Length; s++)
            {
                foreach (int index in tile.triangles[s])
                {
                    triangles[s].Add(first + index);
                }
            }
        }

        var mesh = new Mesh
        {
            name = "PathFar",
            indexFormat = vertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16,
        };
        mesh.SetVertices(vertices);
        if (uv != null)
        {
            mesh.SetUVs(0, uv);
        }
        mesh.subMeshCount = triangles.Length;
        for (int s = 0; s < triangles.Length; s++)
        {
            mesh.SetTriangles(triangles[s], s);
        }
        mesh.RecalculateNormals();
        if (uv != null)
        {
            mesh.RecalculateTangents();
        }
        mesh.RecalculateBounds();
        materials = tile.materials;
        return mesh;
    }

    /// <summary>Create a tiled distant child object under parent. Null if nothing to draw.</summary>
    public static GameObject Create(GameObject parent, string name, int variant, List<Vector3> points)
    {
        Mesh mesh = Build(variant, points, out Material[] materials);
        if (mesh == null)
        {
            return null;
        }
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = materials;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        go.AddComponent<RenderCacheIdentity>().mesh = mesh;
        return go;
    }

    /// <summary>Position at arc length s and horizontal right direction there.</summary>
    private static void Sample(List<Vector3> points, float[] cumulative, float s, out Vector3 position, out Vector3 right)
    {
        int last = points.Count - 2;
        int k = System.Array.BinarySearch(cumulative, s);
        k = k >= 0 ? k : ~k - 1;
        k = Mathf.Clamp(k, 0, last);
        float length = cumulative[k + 1] - cumulative[k];
        position = Vector3.Lerp(points[k], points[k + 1], length > 1e-6f ? (s - cumulative[k]) / length : 0f);
        Vector3 direction = points[k + 1] - points[k];
        direction.y = 0f;
        right = direction.sqrMagnitude > 1e-8f ? Vector3.Cross(Vector3.up, direction).normalized : Vector3.right;
    }

    private static Tile GetTile(int variant)
    {
        if (tiles.TryGetValue(variant, out Tile cached))
        {
            return cached;
        }
        GameObject prefab = ModelManager.Instance != null ? ModelManager.Instance.GetModel($"Models/Paths/p{variant}/far", true) : null;
        Tile tile = prefab != null ? ModelTile(prefab) : PlateTile(variant);
        // Caching results before models/masters are ready would leave that variant permanently absent in distant rendering.
        if (ModelManager.Instance != null && MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized)
        {
            tiles[variant] = tile;
        }
        return tile;
    }

    private static Tile ModelTile(GameObject prefab)
    {
        Mesh mesh = BuildingMeshUtils.CombineModuleMeshes(prefab, out Material[] sourceMaterials);
        if (mesh == null)
        {
            return null;
        }
        var tile = new Tile
        {
            vertices = mesh.vertices,
            uv = mesh.uv.Length == mesh.vertexCount ? mesh.uv : null,
            triangles = new int[mesh.subMeshCount][],
            materials = new Material[mesh.subMeshCount],
            minZ = mesh.bounds.min.z,
            length = Mathf.Max(mesh.bounds.size.z, 0.1f),
        };
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            tile.triangles[s] = mesh.GetTriangles(s);
            Material source = sourceMaterials != null && sourceMaterials.Length > 0 ? sourceMaterials[Mathf.Min(s, sourceMaterials.Length - 1)] : null;
            tile.materials[s] = RoadPath.GetOrCreateRoadMaterial(source);
        }
        tile.materials = BuildingManager.GetSharedDepthOffsetMaterials(tile.materials);
        Object.Destroy(mesh);
        return tile;
    }

    private static Tile PlateTile(int variant)
    {
        PathMasterItem master = MasterDataStore.Instance?.GetPathMaster(variant);
        if (master == null || master.width <= 0f)
        {
            return null;
        }
        float half = master.width * 0.5f;
        Material material = RoadPath.ResolveBodyMaterial(variant, master.type) ?? AssetPackStore.LoadNamedMaterial("Road");
        return new Tile
        {
            vertices = new[] { new Vector3(-half, 0f, 0f), new Vector3(-half, 0f, PLATE_LENGTH), new Vector3(half, 0f, PLATE_LENGTH), new Vector3(half, 0f, 0f) },
            uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) },
            triangles = new[] { new[] { 0, 1, 2, 0, 2, 3 } },
            materials = BuildingManager.GetSharedDepthOffsetMaterials(new[] { material }),
            minZ = 0f,
            length = PLATE_LENGTH,
        };
    }
}
