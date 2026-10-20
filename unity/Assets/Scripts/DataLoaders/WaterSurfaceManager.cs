using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;

/// <summary>
/// WaterSurfaceManager:
/// Persistent manager rendering non-sea water surfaces (lakes, etc.) in 3D, separate from the Y=0 sea plane.
///
/// Fetch water polygons from backend <c>GET /api/v1/terrain/water</c>,
/// triangulating each into a horizontal mesh at water_level (absolute elevation Y).
/// Borrow material from the scene sea (SeaObject) to match its appearance
/// (create a Custom/DepthOffsetLit fallback if unavailable).
///
/// Use the same depth offset tier as the sea plane (sea/water = Custom/DepthOffsetLit, Offset -1,-1,
/// in front of per-cell terrain), avoiding Z-fighting with lakebeds (per-cell tiles)
/// by the same principle as the sea. At banks above water, per-cell terrain
/// naturally renders in front because of world Y differences.
///
/// Water is map-specific static data fixed during init_game, but <see cref="TerrainManager.FetchCoastline"/>
/// calls after coastlines every time, so refetch/regenerate on initial load, forced reload, and session:reload.
///
/// No scene placement required; create automatically in AfterSceneLoad.
/// </summary>
public class WaterSurfaceManager : MonoBehaviour
{
    public static WaterSurfaceManager Instance { get; private set; }

    /// <summary>
    /// Water fallback material shader (Custom/DepthOffsetLit).
    /// Unused when sea material can be borrowed. Reference directly through
    /// SerializeField for guaranteed build inclusion (Shader.Find fallback if unset).
    /// </summary>
    [SerializeField] private Shader waterShader;

    /// <summary>Fallback water color (translucent blue) if sea material cannot be borrowed.</summary>
    [SerializeField] private Color fallbackWaterColor = new Color(0.20f, 0.45f, 0.65f, 0.75f);

    // Generated water GameObjects, destroyed together on reload.
    private readonly List<GameObject> waterObjects = new List<GameObject>();
    // Generated meshes, released with Destroy on cleanup.
    private readonly List<Mesh> waterMeshes = new List<Mesh>();
    // Generated fallback material (null when borrowing sea material).
    private Material fallbackMaterial;

    /// <summary>Create after scene load to work without preplacement.</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance == null)
        {
            var go = new GameObject("WaterSurfaceManager");
            go.AddComponent<WaterSurfaceManager>();
        }
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        ClearWaterObjects();
    }

    /// <summary>
    /// Fetch water polygons and regenerate water meshes.
    /// Called by TerrainManager.FetchCoastline during initial load, forced reload, and session:reload.
    /// Destroy existing water before rebuilding on every call (idempotent).
    /// </summary>
    public IEnumerator LoadAndBuild()
    {
        ClearWaterObjects();

        WaterResponse response = null;
        string error = null;
        yield return ApiClient.Get<WaterResponse>(
            "/terrain/water",
            res => response = res,
            err => error = err
        );

        if (!string.IsNullOrEmpty(error))
        {
            Debug.LogWarning($"WaterSurfaceManager: 水面の取得に失敗: {error}");
            yield break;
        }
        if (response?.water_surfaces == null || response.water_surfaces.Count == 0)
        {
            yield break;
        }

        Material mat = ResolveWaterMaterial();
        foreach (var ws in response.water_surfaces)
        {
            if (ws == null) continue;
            BuildOneWaterSurface(ws, mat);
        }

        Debug.Log($"WaterSurfaceManager: 水面を{waterObjects.Count}枚生成しました。");
    }

    /// <summary>
    /// Public method for external cleanup (such as session shutdown) to destroy all water objects.
    /// Sessions switch within one scene, so water meshes and GameObjects
    /// from old sessions must be reliably removed.
    /// </summary>
    public void Clear()
    {
        ClearWaterObjects();
    }

    /// <summary>Destroy all generated water GameObjects/Meshes and the query store.</summary>
    private void ClearWaterObjects()
    {
        WaterSurfaceStore.Clear();
        foreach (var go in waterObjects)
        {
            if (go != null) Destroy(go);
        }
        waterObjects.Clear();
        foreach (var m in waterMeshes)
        {
            if (m != null) Destroy(m);
        }
        waterMeshes.Clear();
    }

    /// <summary>
    /// Resolve water material. First borrow the scene sea material (SeaObject tag)
    /// to match appearance exactly. Only if unavailable, generate and reuse
    /// a Custom/DepthOffsetLit fallback material.
    /// </summary>
    private Material ResolveWaterMaterial()
    {
        // Try borrowing sea material.
        GameObject sea = null;
        try { sea = GameObject.FindWithTag("SeaObject"); }
        catch { sea = null; } // Do not fail if the tag is undefined
        if (sea != null)
        {
            Renderer seaRenderer = sea.GetComponentInChildren<Renderer>();
            if (seaRenderer != null && seaRenderer.sharedMaterial != null)
            {
                return seaRenderer.sharedMaterial;
            }
        }

        // Fallback: create one Custom/DepthOffsetLit material and reuse it.
        if (fallbackMaterial == null)
        {
            Shader sh = waterShader;
            if (sh == null) sh = Shader.Find("Custom/DepthOffsetLit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
            fallbackMaterial = new Material(sh) { name = "WaterSurfaceFallback" };
            if (fallbackMaterial.HasProperty("_BaseColor"))
                fallbackMaterial.SetColor("_BaseColor", fallbackWaterColor);
            if (fallbackMaterial.HasProperty("_Color"))
                fallbackMaterial.SetColor("_Color", fallbackWaterColor);
        }
        return fallbackMaterial;
    }

    /// <summary>
    /// Create one water surface GameObject. Triangulate polygon (Polygon/MultiPolygon) as a horizontal plane
    /// and place the mesh at water_level.
    /// Register the area including holes in WaterSurfaceStore for path-placement crossing checks.
    /// </summary>
    private void BuildOneWaterSurface(WaterSurfaceDto ws, Material mat)
    {
        float level = ws.water_level;

        // Extract (outer ring, holes) pairs from polygon.
        List<(List<Vector2> exterior, List<List<Vector2>> holes)> parts = ParsePolygon(ws.polygon);
        if (parts.Count == 0) return;

        var record = new WaterSurfaceRecord { Id = ws.id, Level = level };
        foreach (var (exterior, holes) in parts)
        {
            record.AddRing(exterior);
            foreach (var hole in holes) record.AddRing(hole);
        }
        WaterSurfaceStore.Add(record);

        var vertices = new List<Vector3>();
        var triangles = new List<int>();

        foreach (var (exterior, holes) in parts)
        {
            AppendRingMesh(exterior, holes, level, vertices, triangles);
        }
        if (vertices.Count < 3 || triangles.Count < 3) return;

        Mesh mesh = new Mesh { name = $"WaterSurfaceMesh_{ws.id}" };
        if (vertices.Count > 65000)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        waterMeshes.Add(mesh);

        GameObject go = new GameObject($"WaterSurface_{ws.id}");
        go.transform.SetParent(transform);
        // Vertices retain backend world coordinates (same system as UltraFar land meshes and sea planes).
        // Place the GameObject itself at the origin.
        go.transform.position = Vector3.zero;

        MeshFilter mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;

        // Like sea surfaces, allow hiding water with terrain transparency/hidden modes.
        go.tag = "SeaObject";
        go.AddComponent<VisualSeaObject>();

        waterObjects.Add(go);
    }

    /// <summary>
    /// Triangulate one outer ring plus holes and append to vertices/triangles.
    /// Vertices use world coordinates (x, level, z). Adjust winding for +Y upward normals.
    ///
    /// The v1 editor is assumed not to generate interior rings, so currently skip holes
    /// and log warnings, triangulating only outer boundaries. Add hole bridging here
    /// when supporting polygons with holes later.
    /// </summary>
    private void AppendRingMesh(List<Vector2> exterior, List<List<Vector2>> holes, float level,
                               List<Vector3> vertices, List<int> triangles)
    {
        if (exterior == null || exterior.Count < 3) return;
        if (holes != null && holes.Count > 0)
        {
            Debug.LogWarning($"WaterSurfaceManager: 穴付き水面ポリゴンは現状未対応です(外周のみ描画)。");
        }

        // Remove the last point if it duplicates the first.
        List<Vector2> ring = new List<Vector2>(exterior);
        if (ring.Count >= 2 && (ring[0] - ring[ring.Count - 1]).sqrMagnitude < 1e-6f)
            ring.RemoveAt(ring.Count - 1);
        if (ring.Count < 3) return;

        // Ear clipping expects CCW input; normalize using signed area in 2D (x=X, y=Z).
        if (PolygonUtils.SignedArea(ring) < 0f) ring.Reverse();

        Vector2[] poly = ring.ToArray();
        List<int> localTris = BuildingMeshBuilder.TriangulateOrFan(poly);
        if (localTris.Count < 3) return;

        int baseIndex = vertices.Count;
        for (int i = 0; i < poly.Length; i++)
        {
            vertices.Add(new Vector3(poly[i].x, level, poly[i].y));
        }
        // CCW triangles in XZ have -Y normals; reverse winding for upward +Y normals.
        for (int i = 0; i + 2 < localTris.Count; i += 3)
        {
            triangles.Add(baseIndex + localTris[i]);
            triangles.Add(baseIndex + localTris[i + 2]);
            triangles.Add(baseIndex + localTris[i + 1]);
        }
    }

    /// <summary>
    /// Convert GeoJSON-like polygon (JToken) to a list of (outer ring, holes) pairs.
    /// Polygon returns one element; MultiPolygon returns several. Coordinates are world XZ.
    /// </summary>
    private static List<(List<Vector2>, List<List<Vector2>>)> ParsePolygon(JToken polygon)
    {
        var result = new List<(List<Vector2>, List<List<Vector2>>)>();
        if (polygon == null) return result;

        string gtype = polygon["type"]?.ToString();
        JToken coords = polygon["coordinates"];
        if (coords == null) return result;

        if (gtype == "Polygon")
        {
            var part = ParseSinglePolygon(coords);
            if (part.HasValue) result.Add(part.Value);
        }
        else if (gtype == "MultiPolygon")
        {
            foreach (JToken polyCoords in coords)
            {
                var part = ParseSinglePolygon(polyCoords);
                if (part.HasValue) result.Add(part.Value);
            }
        }
        return result;
    }

    /// <summary>Convert [outer boundary, hole 1, hole 2, ...] rings to (outer ring, holes).</summary>
    private static (List<Vector2>, List<List<Vector2>>)? ParseSinglePolygon(JToken rings)
    {
        if (!(rings is JArray ringArray) || ringArray.Count == 0) return null;
        List<Vector2> exterior = GeneratedModelUtils.ParseRing(ringArray[0]);
        if (exterior == null || exterior.Count < 3) return null;

        var holes = new List<List<Vector2>>();
        for (int i = 1; i < ringArray.Count; i++)
        {
            List<Vector2> hole = GeneratedModelUtils.ParseRing(ringArray[i]);
            if (hole != null && hole.Count >= 3) holes.Add(hole);
        }
        return (exterior, holes);
    }


    // === API response types ===

    [System.Serializable]
    private class WaterResponse
    {
        public List<WaterSurfaceDto> water_surfaces;
    }

    [System.Serializable]
    private class WaterSurfaceDto
    {
        public string id;
        public float water_level;
        public string kind;
        // GeoJSON-like Polygon/MultiPolygon. Receive raw JToken because nesting depends on type.
        public JToken polygon;
    }
}
