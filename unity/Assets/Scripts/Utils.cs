// Assets/Scripts/MeshGeneration/Generators/CarRoadMeshGenerator.cs
using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

public static class Utils
{
    /// <summary>
    /// Terrain-height cache shared only for exactly matching XZ coordinates within the same frame.
    /// Rounding nearby coordinates would make slope heights depend on query order.
    /// </summary>
    private static Dictionary<Vector2, float> heightCache = new Dictionary<Vector2, float>();
    private static Dictionary<Vector2, float> surfaceCache = new Dictionary<Vector2, float>(); // GetPathSurfaceHeight, same lifetime as heightCache
    private static int cacheFrame = -1;

    /// <summary>
    /// Cached TerrainManager reference.
    /// FindAnyObjectByType scans all scene objects in O(n);
    /// since GetGroundHeight calls frequently, retain a static reference instead of searching each time.
    /// TerrainManager.Awake sets it via RegisterTerrainManager.
    /// An internal property supports checking whether to clear it in OnDestroy.
    /// </summary>
    private static TerrainManager cachedTerrainManager;

    /// <summary>
    /// Internal property for comparing cached references in OnDestroy and similar cleanup.
    /// </summary>
    internal static TerrainManager CachedTerrainManager => cachedTerrainManager;

    /// <summary>
    /// Register the TerrainManager instance in the cache.
    /// Calling from TerrainManager.Awake lets subsequent GetGroundHeight calls
    /// use the instance without FindAnyObjectByType.
    /// </summary>
    public static void RegisterTerrainManager(TerrainManager manager)
    {
        cachedTerrainManager = manager;
    }

    /// <summary>
    /// Force-clear the cache, such as after replacing Terrain LOD.
    /// </summary>
    public static void InvalidateHeightCache()
    {
        cacheFrame = -1;
        heightCache.Clear();
        surfaceCache.Clear();
    }

    /// <summary>
    /// Return ground height at a position, using the cache for identical XZ coordinates in the same frame.
    /// Inside reclaimed-land bands, return max(terrain, crest) via <see cref="ReclaimedLandStore.TryGetBandTop"/>.
    /// Call InvalidateHeightCache() when tile heights are updated or replaced.
    /// </summary>
    public static float GetGroundHeight(Vector3 position)
    {
        int frame = Time.frameCount;
        if (frame != cacheFrame)
        {
            heightCache.Clear();
            surfaceCache.Clear();
            cacheFrame = frame;
        }

        var key = new Vector2(position.x, position.z);
        if (heightCache.TryGetValue(key, out float cached))
        {
            return cached;
        }

        float groundHeight = ComputeGroundHeight(position);
        heightCache[key] = groundHeight;
        return groundHeight;
    }

    /// <summary>
    /// Reference surface that path and node relative Y is measured from: the water surface over water, otherwise
    /// <see cref="GetGroundHeight"/> (same rule as backend geo_sea_lake.surface_elevations). Measure from GetGroundHeight
    /// instead for anything about the terrain itself (tunnel depth, pillars and foundations, earthwork).
    /// </summary>
    public static float GetPathSurfaceHeight(Vector3 position)
    {
        float ground = GetGroundHeight(position);
        var key = new Vector2(position.x, position.z);
        if (surfaceCache.TryGetValue(key, out float cached)) return cached;
        float surface = IsOnWater(position.x, position.z, ground, out float waterY) ? waterY : ground;
        surfaceCache[key] = surface;
        return surface;
    }

    /// <summary>
    /// Whether (x, z) is over water by the backend's definition (geo_sea_lake): inside the city, outside land and rivers, with
    /// ground below the water level (the lake level inside a lake footprint, otherwise sea level 0). Rivers have their own check.
    /// </summary>
    public static bool IsOnWater(float x, float z, float groundY, out float waterY)
    {
        var lake = WaterSurfaceStore.FindAt(x, z);
        waterY = lake != null ? lake.Level : 0f;
        if (groundY >= waterY || !CoastlineStore.HasCoastline) return false;
        // Outside the city there is no coastline data to tell sea from land.
        if (cachedTerrainManager == null || !cachedTerrainManager.HasTerrainSettings) return false;
        if (Mathf.Abs(x) > cachedTerrainManager.GetMapWidth() * 0.5f || Mathf.Abs(z) > cachedTerrainManager.GetMapHeight() * 0.5f) return false;
        return RiverStore.FindAt(x, z) == null && !CoastlineStore.IsLand(x, z);
    }

    /// <summary>
    /// Determine district/road-name display distance (m) from camera height above the ground directly below.
    /// Show nearby names near ground level and a wider area from higher viewpoints.
    /// </summary>
    public static float GetMapLabelMaxDistance(Vector3 cameraPosition)
    {
        float heightAboveGround = cameraPosition.y - GetGroundHeight(cameraPosition);
        return Mathf.Lerp(100f, 4000f, heightAboveGround / 1000f);
    }

    private static float ComputeGroundHeight(Vector3 position)
    {
        float height = ComputeTerrainHeight(position);
        // Inside reclaimed-land bands, the concrete crest top is ground, matching backend grade/path validation's max(terrain, crest) rule.
        if (ReclaimedLandStore.TryGetBandTop(position.x, position.z, out float top) && top > height) return top;
        return height;
    }

    private static float ComputeTerrainHeight(Vector3 position)
    {
        Terrain terrain = GetTerrainAtPosition(position);
        if (terrain != null)
        {
            // Terrain.SampleHeight returns tile-local height excluding base Y,
            // so add tile transform.position.y to obtain world Y. Terrain tiles lower base Y
            // to represent seabed depth (TerrainManager.SeaFloorDepthSpan);
            // omitting it would offset heights near sea level by a constant amount.
            return terrain.SampleHeight(position) + terrain.transform.position.y;
        }

        if (cachedTerrainManager != null && cachedTerrainManager.TrySampleOverview(position, out float backgroundHeight))
            return backgroundHeight;

        int groundLayerMask = LayerMask.GetMask("Ground");
        Ray ray = new Ray(new Vector3(position.x, 1000f, position.z), Vector3.down);
        if (Physics.Raycast(ray, out RaycastHit hit, 2000f, groundLayerMask))
        {
            return hit.point.y;
        }

        return 0f;
    }

    /// <summary>
    /// Coroutine wrapper for awaiting Task completion.
    /// Rethrow or log exceptions after completion.
    /// </summary>
    public static IEnumerator WaitTask(Task task)
    {
        while (!task.IsCompleted) yield return null;
        if (task.IsFaulted && task.Exception != null)
        {
            Debug.LogError($"Background task failed: {task.Exception.Flatten()}");
        }
    }

    /// <summary>
    /// Return the rendered tile grid. All tiles share one resolution and boundary grid points.
    /// Subdivide ground-covering meshes to match this grid and its diagonals.
    /// </summary>
    public static bool TryGetTerrainGrid(Vector3 position, out Vector2 origin, out Vector2 spacing)
    {
        Terrain terrain = GetTerrainAtPosition(position);
        origin = default;
        spacing = default;
        if (terrain == null || terrain.terrainData == null) return false;
        var data = terrain.terrainData;
        var pos = terrain.transform.position;
        origin = new Vector2(pos.x, pos.z);
        spacing = new Vector2(data.size.x, data.size.z) / (data.heightmapResolution - 1);
        return spacing.x > 0f && spacing.y > 0f;
    }

    private static Terrain GetTerrainAtPosition(Vector3 position)
    {
        // Prefer the cached TerrainManager reference.
        // TerrainManager overwrites its terrainTiles dictionary with the latest Terrain when LOD changes,
        // so even if a cell contains multiple terrain LODs, such as near 257 and very near 513,
        // this always retrieves the most detailed active Terrain.
        // This ordering prevents Terrain.activeTerrains array order from selecting low-resolution terrain
        // when several Terrains share the same cell size.
        if (cachedTerrainManager != null)
        {
            Terrain managedTerrain = cachedTerrainManager.GetTerrainAtPosition(position);
            if (managedTerrain != null)
            {
                return managedTerrain;
            }

            // All city terrain tiles belong to TerrainManager; nonresident cells contain no other Terrain.
            // Skip the activeTerrains allocation and full scan below, leaving overview sampling to the caller.
            if (cachedTerrainManager.IsCityCell(TerrainManager.WorldToCell(position)))
            {
                return null;
            }
        }

        // Fallback when TerrainManager cannot provide a tile, such as outside the map.
        // Consider all active Terrains containing the coordinates,
        // preferring the one with the smallest horizontal area.
        Terrain[] terrains = Terrain.activeTerrains;
        Terrain best = null;
        float bestArea = float.MaxValue;

        foreach (Terrain terrain in terrains)
        {
            if (terrain == null || terrain.terrainData == null) continue;
            if (!terrain.gameObject.activeInHierarchy) continue;

            Vector3 terrainPosition = terrain.transform.position;
            Vector3 terrainSize = terrain.terrainData.size;

            if (position.x >= terrainPosition.x &&
                position.x <= terrainPosition.x + terrainSize.x &&
                position.z >= terrainPosition.z &&
                position.z <= terrainPosition.z + terrainSize.z)
            {
                float area = terrainSize.x * terrainSize.z;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = terrain;
                }
            }
        }

        return best;
    }

    private static Shader cachedDepthOffsetShader;

    public static void ApplyDepthOffsetShader(MeshRenderer renderer, string contextName = "", System.Action<Material[]> onInstantiated = null)
    {
        if (renderer == null) return;

        if (cachedDepthOffsetShader == null)
        {
            cachedDepthOffsetShader = Shader.Find("Custom/DepthOffsetLit");
        }
        Shader offsetShader = cachedDepthOffsetShader;
        if (offsetShader == null)
        {
            string prefix = string.IsNullOrEmpty(contextName) ? "" : contextName + ": ";
            Debug.LogWarning($"{prefix}Custom/DepthOffsetLit shader not found");
            return;
        }

        Material[] mats = renderer.materials;
        onInstantiated?.Invoke(mats);
        bool changed = false;
        for (int i = 0; i < mats.Length; i++)
        {
            if (mats[i] != null && mats[i].shader != offsetShader && mats[i].shader.name != "Custom/RoadSurface")
            {
                mats[i].shader = offsetShader;
                changed = true;
            }
        }
        if (changed)
        {
            renderer.materials = mats;
        }
    }

    public static IEnumerator DestroyObjectAfterFrames(MonoBehaviour host, GameObject obj)
    {
        yield return null;
        yield return null;

        if (obj != null)
        {
            Object.Destroy(obj);
        }
    }

    // ================================================================
    // Shared helpers for view-prioritized generation/rebuilding order.
    // ================================================================

    /// <summary>
    /// Return the normalized horizontal component of camera forward; fall back to +Z when looking straight up/down.
    /// Used for horizontal-angle checks prioritizing visible heavy generation/rebuilding work,
    /// such as BuildingManager detail meshes and PathManager terrain-following rebuilds.
    /// </summary>
    public static Vector3 GetCameraHorizontalForward(Camera cam)
    {
        Vector3 fwd = cam.transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) return Vector3.forward;
        return fwd.normalized;
    }

    /// <summary>
    /// Horizontal check for whether a location is in the view that should receive immediate heavy generation.
    /// Locations within <paramref name="nearDistance"/> of the camera count as visible regardless of direction.
    /// Use the frustum only to weight processing order, never to filter generation eligibility;
    /// permanently skipping off-screen objects would leave gaps when the camera turns.
    /// </summary>
    public static bool IsInHorizontalView(Vector3 position, Vector3 camPos, Vector3 camForwardHorizontal, float nearDistance, float halfAngleDeg)
    {
        Vector3 to = position - camPos;
        to.y = 0f;
        float dist = to.magnitude;
        if (dist <= nearDistance) return true;
        return Vector3.Angle(camForwardHorizontal, to) <= halfAngleDeg;
    }

    public static float CalculateAverageWidth(List<float> widths)
    {
        if (widths == null || widths.Count == 0) return 0f;
        float sum = 0f;
        for (int i = 0; i < widths.Count; i++) sum += widths[i];
        return sum / widths.Count;
    }

    /// <summary>Squared horizontal XZ distance, for range checks ignoring height, such as leaving a draw radius.</summary>
    public static float XZDistanceSquared(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

    /// <summary>Horizontal XZ distance, ignoring height.</summary>
    public static float XZDistance(Vector3 a, Vector3 b) => Mathf.Sqrt(XZDistanceSquared(a, b));

    /// <summary>
    /// Create a ScreenSpaceOverlay Canvas for labels/icons following 3D objects at a fixed size
    /// independent of camera distance. Add CanvasScaler and GraphicRaycaster.
    /// sortingOrder defines layering between managers: resource labels 55 &lt; district labels 60 &lt; alerts 70.
    /// persistent=true survives scene transitions through DontDestroyOnLoad.
    /// </summary>
    public static Canvas CreateOverlayCanvas(string name, int sortingOrder, bool persistent)
    {
        var root = new GameObject(name);
        if (persistent) Object.DontDestroyOnLoad(root);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;
        root.AddComponent<CanvasScaler>();
        root.AddComponent<GraphicRaycaster>();
        return canvas;
    }

    /// <summary>
    /// Enable clicks on labels in a CreateOverlayCanvas Canvas only in normal input mode. Call every frame.
    /// Within label rectangles, EventSystem treats the pointer as over UI, blocking InputHandler's 3D input and camera controls,
    /// so during editing keep labels visible but pass clicks through to the 3D world below.
    /// </summary>
    public static void SyncOverlayCanvasClickable(GraphicRaycaster raycaster)
    {
        if (raycaster == null) return;
        bool clickable = InputHandler.Instance == null || InputHandler.Instance.IsNormalMode;
        if (raycaster.enabled != clickable) raycaster.enabled = clickable;
    }

    /// <summary>
    /// Load an icon Sprite from Resources/UIIcons. Sprite Mode Multiple imports can make single loads return null,
    /// so use the first sub-sprite as a fallback; the correct import setting is Single.
    /// </summary>
    public static Sprite LoadUIIcon(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var sprite = Resources.Load<Sprite>($"UIIcons/{name}");
        if (sprite != null) return sprite;
        var all = Resources.LoadAll<Sprite>($"UIIcons/{name}");
        return all != null && all.Length > 0 ? all[0] : null;
    }

    /// <summary>
    /// Axis-aligned bounding box after transforming a local AABB by a matrix. Transform all eight corners and enclose them again,
    /// ensuring containment even with rotation, though the result is larger than the rotated box itself.
    /// </summary>
    public static Bounds TransformBounds(Bounds bounds, Matrix4x4 matrix)
    {
        var result = new Bounds(matrix.MultiplyPoint3x4(bounds.center), Vector3.zero);
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = bounds.center + Vector3.Scale(bounds.extents,
                new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
            result.Encapsulate(matrix.MultiplyPoint3x4(corner));
        }
        return result;
    }
}
