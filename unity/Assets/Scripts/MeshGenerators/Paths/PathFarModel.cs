using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Distant (Far) paths. Tile one variant-specific distant model (`Models/Paths/p{variant}/far`) along points.
/// Count = rounded total length / model local-Z length (at least 1); stretch each lengthwise into equal-length sections.
/// Width/height stay actual size. Map vertices onto points with local X horizontal-right and Y vertical,
/// keeping posts/towers upright while road surfaces follow grade. Section-boundary vertices coincide, preventing seams.
/// Model origin lies on the rendered centerline (ground for base_height paths); author distant models around that origin.
/// Variants without distant models tile master-width plates using the same rules.
/// Plates stand in for a physical body, so paths without one (model_type="outline", such as ferry lanes) draw nothing at a distance.
/// Elevated spans of those plates use the `body_elevated` material when the variant has that part, as detailed tracks do.
/// Use DepthOffsetLit variants of model materials (road material for plates) to prevent burial in coarse distant terrain.
/// </summary>
public static class PathFarModel
{
    /// <summary>Plate length for variants without distant models (m).</summary>
    private const float PLATE_LENGTH = 15f;

    /// <summary>Elevated range covering the whole polyline, for callers drawing a single elevated part.</summary>
    public static readonly IReadOnlyList<TrackCorridorInfo.PercentRange> WholeElevated =
        new[] { new TrackCorridorInfo.PercentRange { From = 0.0, To = 1.0 } };

    private sealed class Tile
    {
        public Vector3[] vertices;
        public Vector2[] uv;
        public int[][] triangles;
        public Material[] materials;
        public float minZ, length;
    }

    // Models per variant and elevation; null for variants with nothing to draw. Elevated entries share the ground tile
    // when elevated spans look the same. Prefabs (asset packs) and masters may differ by city, so discard on city switches.
    private static readonly Dictionary<(int variant, bool elevated), Tile> tiles = new Dictionary<(int variant, bool elevated), Tile>();

    static PathFarModel() => AssetPackStore.CityChanging += tiles.Clear;

    /// <summary>Whether elevated spans of the variant draw differently from ground spans.</summary>
    public static bool HasElevatedLook(int variant) => GetTile(variant, true) != GetTile(variant, false);

    /// <summary>
    /// Mesh tiled along points. Null if nothing to draw. Caller releases returned meshes.
    /// elevated holds sorted, nonoverlapping length-fraction ranges drawn with the elevated tile; null for none.
    /// Each span is tiled separately, with one submesh per material.
    /// </summary>
    public static Mesh Build(int variant, List<Vector3> points, IReadOnlyList<TrackCorridorInfo.PercentRange> elevated, out Material[] materials)
    {
        materials = null;
        if (points == null || points.Count < 2)
        {
            return null;
        }
        var builder = new Builder();
        double cursor = 0.0;
        if (elevated != null)
        {
            foreach (var range in elevated)
            {
                builder.Append(variant, false, points, cursor, range.From);
                builder.Append(variant, true, points, range.From, range.To);
                cursor = range.To;
            }
        }
        builder.Append(variant, false, points, cursor, 1.0);
        return builder.ToMesh(out materials);
    }

    private sealed class Builder
    {
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Vector2> uv = new List<Vector2>();
        private readonly List<Material> materials = new List<Material>();
        private readonly List<List<int>> triangles = new List<List<int>>();
        private bool hasUv;

        public void Append(int variant, bool elevated, List<Vector3> points, double from, double to)
        {
            if (to - from <= 1e-6)
            {
                return;
            }
            Tile tile = GetTile(variant, elevated);
            if (tile == null)
            {
                return;
            }
            List<Vector3> span = from <= 0.0 && to >= 1.0 ? points : PathDrawContext.SlicePolylineByFraction(points, from, to);
            if (span.Count < 2)
            {
                return;
            }
            var cumulative = new float[span.Count];
            for (int i = 1; i < span.Count; i++)
            {
                cumulative[i] = cumulative[i - 1] + Vector3.Distance(span[i - 1], span[i]);
            }
            float total = cumulative[span.Count - 1];
            if (total < 0.01f)
            {
                return;
            }

            var slots = new List<int>[tile.triangles.Length];
            for (int s = 0; s < slots.Length; s++)
            {
                Material material = tile.materials[s];
                int slot = materials.IndexOf(material);
                if (slot < 0)
                {
                    slot = materials.Count;
                    materials.Add(material);
                    triangles.Add(new List<int>());
                }
                slots[s] = triangles[slot];
            }
            hasUv |= tile.uv != null;
            Vector2[] tileUv = tile.uv ?? new Vector2[tile.vertices.Length];
            int count = Mathf.Max(1, Mathf.RoundToInt(total / tile.length));
            float step = total / count;
            for (int i = 0; i < count; i++)
            {
                int first = vertices.Count;
                foreach (Vector3 v in tile.vertices)
                {
                    Sample(span, cumulative, (i + (v.z - tile.minZ) / tile.length) * step, out Vector3 position, out Vector3 right);
                    vertices.Add(position + right * v.x + Vector3.up * v.y);
                }
                uv.AddRange(tileUv);
                for (int s = 0; s < slots.Length; s++)
                {
                    foreach (int index in tile.triangles[s])
                    {
                        slots[s].Add(first + index);
                    }
                }
            }
        }

        public Mesh ToMesh(out Material[] result)
        {
            result = null;
            if (vertices.Count == 0)
            {
                return null;
            }
            var mesh = new Mesh
            {
                name = "PathFar",
                indexFormat = vertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16,
            };
            mesh.SetVertices(vertices);
            if (hasUv)
            {
                mesh.SetUVs(0, uv);
            }
            mesh.subMeshCount = triangles.Count;
            for (int s = 0; s < triangles.Count; s++)
            {
                mesh.SetTriangles(triangles[s], s);
            }
            mesh.RecalculateNormals();
            if (hasUv)
            {
                mesh.RecalculateTangents();
            }
            mesh.RecalculateBounds();
            result = materials.ToArray();
            return mesh;
        }
    }

    /// <summary>Create a tiled distant child object under parent. Null if nothing to draw.</summary>
    public static GameObject Create(GameObject parent, string name, int variant, List<Vector3> points,
        IReadOnlyList<TrackCorridorInfo.PercentRange> elevated = null)
    {
        Mesh mesh = Build(variant, points, elevated, out Material[] materials);
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

    private static Tile GetTile(int variant, bool elevated)
    {
        if (tiles.TryGetValue((variant, elevated), out Tile cached))
        {
            return cached;
        }
        GameObject prefab = ModelManager.Instance != null ? ModelManager.Instance.GetModel($"Models/Paths/p{variant}/far", true) : null;
        // Distant models cover elevated spans too; only plates take the elevated part's material.
        Material elevatedMaterial = elevated && prefab == null ? ElevatedBodyMaterial(variant) : null;
        Tile tile = elevated && elevatedMaterial == null ? GetTile(variant, false)
            : prefab != null ? ModelTile(prefab) : PlateTile(variant, elevatedMaterial);
        // Caching results before models/masters are ready would leave that variant permanently absent in distant rendering.
        if (ModelManager.Instance != null && MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized)
        {
            tiles[(variant, elevated)] = tile;
        }
        return tile;
    }

    /// <summary>Material of the variant's `body_elevated`, which detailed tracks lay above terrain. Null without that part.</summary>
    private static Material ElevatedBodyMaterial(int variant)
    {
        GameObject prefab = ModelManager.Instance != null ? ModelManager.Instance.GetModel($"Models/Paths/p{variant}/body_elevated", true) : null;
        Material source = prefab != null ? prefab.GetComponent<MeshRenderer>()?.sharedMaterial : null;
        return source != null ? RoadPath.GetOrCreateRoadMaterial(source) : null;
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

    /// <param name="material">Plate material; null resolves the variant's body material.</param>
    private static Tile PlateTile(int variant, Material material)
    {
        PathMasterItem master = MasterDataStore.Instance?.GetPathMaster(variant);
        if (master == null || master.width <= 0f || master.model_type == "outline")
        {
            return null;
        }
        float half = master.width * 0.5f;
        material ??= RoadPath.ResolveBodyMaterial(variant, master.type) ?? AssetPackStore.LoadNamedMaterial("Road");
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
