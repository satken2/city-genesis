using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;

/// <summary>
/// ReclaimedLandManager:
/// Persistent manager rendering reclaimed-land quays (crests covering bands and vertical outer faces).
///
/// <see cref="TerrainManager.FetchCoastline"/> calls <see cref="LoadAndBuild"/> after coastlines, lakes, and rivers
/// (initial load, forced reload, every session:reload). Refetch because completed reclamation changes coastline/reclaimed land,
/// but rebuild nothing if <see cref="ReclaimedLandStore"/> contents match.
///
/// One GameObject per reclaimed-land record (crest and faces in one mesh). Like background terrain chunks, use Ground layer,
/// MeshCollider, VisualTerrainObject, and TerrainInputHandler for terrain clicking/visibility.
/// Only face lower edges depend on terrain; rebuild records whose tile revisions (<see cref="TerrainManager.GetTerrainSignature(Rect)"/>)
/// changed through the rebuilding worker.
///
/// No scene placement required; create automatically in AfterSceneLoad.
/// </summary>
public class ReclaimedLandManager : MonoBehaviour
{
    public static ReclaimedLandManager Instance { get; private set; }

    /// <summary>
    /// Called after store contents (<see cref="ReclaimedLandStore.ContentVersion"/>) change.
    /// Rebuild visible objects following quays here, such as ports.
    /// </summary>
    public static event Action OnContentChanged;

    private sealed class LandObject
    {
        public GameObject GameObject;
        public Mesh Mesh;
        public long TerrainSignature;
        public bool HasTerrainSignature;
    }

    private readonly Dictionary<int, LandObject> landObjects = new Dictionary<int, LandObject>();
    // Membership table allowing InputHandler to treat quays as terrain.
    private readonly HashSet<GameObject> quayObjects = new HashSet<GameObject>();
    private readonly Queue<int> rebuildQueue = new Queue<int>();
    private readonly HashSet<int> rebuildQueued = new HashSet<int>();
    private Coroutine rebuildCoroutine;
    private TerrainManager subscribedTerrainManager;
    private Material quayMaterial;
    // Advance on every Clear; discard responses spanning a clear as belonging to the old city.
    private int loadGeneration;

    private static readonly Func<float, float, float> SampleGround =
        (x, z) => Utils.GetGroundHeight(new Vector3(x, 0f, z));

    /// <summary>Create after scene load to work without preplacement.</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance == null)
        {
            var go = new GameObject("ReclaimedLandManager");
            go.AddComponent<ReclaimedLandManager>();
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
        TerrainManager.OnTerrainTileReady += OnTerrainTileReady;
    }

    private void OnDestroy()
    {
        if (Instance != this) return;
        TerrainManager.OnTerrainTileReady -= OnTerrainTileReady;
        if (subscribedTerrainManager != null) subscribedTerrainManager.OnAllTerrainLodLoaded -= OnAllTerrainLodLoaded;
        subscribedTerrainManager = null;
        DestroyAllLands();
        Instance = null;
    }

    /// <summary>
    /// Fetch reclaimed land from the backend and rebuild only records with changed contents.
    /// Do not clear before fetching; build new meshes before discarding old ones, avoiding flicker on every session:reload.
    /// Retain prior visuals and store if fetching fails.
    /// </summary>
    public IEnumerator LoadAndBuild()
    {
        EnsureTerrainLoadSubscription();
        int generation = loadGeneration;

        ReclaimedLandsResponse response = null;
        string error = null;
        yield return ApiClient.Get<ReclaimedLandsResponse>(
            "/terrain/reclaimed-lands",
            res => response = res,
            err => error = err
        );
        if (generation != loadGeneration) yield break;
        if (!string.IsNullOrEmpty(error))
        {
            Debug.LogWarning($"ReclaimedLandManager: 埋立地の取得に失敗したため前回の表示を残します: {error}");
            yield break;
        }

        var previous = new Dictionary<int, ReclaimedLandRecord>();
        foreach (var land in ReclaimedLandStore.Lands) previous[land.Id] = land;

        bool contentChanged = ReclaimedLandStore.Replace(ParseLands(response));
        if (contentChanged) NotifyContentChanged();

        // Build only changed reclaimed land and records missing quays because previous creation was interrupted.
        var alive = new HashSet<int>();
        var targets = new List<ReclaimedLandRecord>();
        foreach (var land in ReclaimedLandStore.Lands)
        {
            alive.Add(land.Id);
            bool modified = contentChanged && (!previous.TryGetValue(land.Id, out var old) || !ReclaimedLandStore.SameContent(old, land));
            if (modified || (land.TopHeight.HasValue && !landObjects.ContainsKey(land.Id))) targets.Add(land);
        }
        var stale = new List<int>();
        foreach (int id in landObjects.Keys)
        {
            if (!alive.Contains(id)) stale.Add(id);
        }

        foreach (var land in targets)
        {
            BuildLand(land);
            yield return RenderWorkScheduler.Continue;
            if (generation != loadGeneration) yield break;
        }
        foreach (int id in stale) DestroyLand(id);
    }

    // Subscriber exceptions must not stop quay creation.
    private static void NotifyContentChanged()
    {
        try { OnContentChanged?.Invoke(); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>
    /// Destroy every quay and clear the store. Sessions switch within one scene,
    /// so leave neither previous-city quays nor grounding crests (called by CellDataLoader.ClearAllManagerData).
    /// </summary>
    public void Clear()
    {
        loadGeneration++;
        if (rebuildCoroutine != null)
        {
            RenderWorkScheduler.Stop(this, rebuildCoroutine);
            rebuildCoroutine = null;
        }
        rebuildQueue.Clear();
        rebuildQueued.Clear();
        bool hadLands = ReclaimedLandStore.Lands.Count > 0;
        ReclaimedLandStore.Clear();
        DestroyAllLands();
        if (hadLands) NotifyContentChanged();
    }

    /// <summary>Whether this GameObject is a reclaimed-land quay. InputHandler uses this to treat it as terrain (TERRAIN).</summary>
    public bool IsQuayObject(GameObject go) => go != null && quayObjects.Contains(go);

    private void EnsureTerrainLoadSubscription()
    {
        TerrainManager terrainManager = Utils.CachedTerrainManager;
        if (terrainManager == subscribedTerrainManager) return;
        if (subscribedTerrainManager != null) subscribedTerrainManager.OnAllTerrainLodLoaded -= OnAllTerrainLodLoaded;
        subscribedTerrainManager = terrainManager;
        if (terrainManager != null) terrainManager.OnAllTerrainLodLoaded += OnAllTerrainLodLoaded;
    }

    /// <summary>
    /// Queue reclaimed land overlapping the arrived tile whose terrain signatures differ from creation time.
    /// Do not build inside the handler, avoiding spikes from successive tile arrivals.
    /// </summary>
    private void OnTerrainTileReady(Vector2Int cell, Terrain tile)
    {
        if (landObjects.Count == 0) return;
        float cellSize = TerrainManager.CellSize;
        var cellRect = new Rect(cell.x * cellSize, cell.y * cellSize, cellSize, cellSize);
        foreach (var land in ReclaimedLandStore.Lands)
        {
            if (!landObjects.ContainsKey(land.Id) || !TerrainArea(land).Overlaps(cellRect)) continue;
            EnqueueIfTerrainChanged(land.Id);
        }
        StartRebuildWorker();
    }

    // Cells without tiles use overview heights for face lower edges; check signatures here when the overview arrives too.
    private void OnAllTerrainLodLoaded(IReadOnlyList<Vector2Int> updatedCells)
    {
        if (landObjects.Count == 0) return;
        foreach (int id in landObjects.Keys) EnqueueIfTerrainChanged(id);
        StartRebuildWorker();
    }

    private void EnqueueIfTerrainChanged(int landId)
    {
        if (rebuildQueued.Contains(landId)) return;
        ReclaimedLandRecord land = ReclaimedLandStore.Find(landId);
        if (land == null || !landObjects.TryGetValue(landId, out LandObject obj)) return;
        if (obj.HasTerrainSignature && TryGetTerrainSignature(land, out long signature) && signature == obj.TerrainSignature) return;
        rebuildQueued.Add(landId);
        rebuildQueue.Enqueue(landId);
    }

    private void StartRebuildWorker()
    {
        if (rebuildCoroutine != null || rebuildQueue.Count == 0 || !isActiveAndEnabled) return;
        rebuildCoroutine = RenderWorkScheduler.Start(this, RebuildWorker(), "ReclaimedLandManager", () => rebuildQueue.Count > 0);
    }

    private IEnumerator RebuildWorker()
    {
        try
        {
            while (rebuildQueue.Count > 0)
            {
                int landId = rebuildQueue.Dequeue();
                rebuildQueued.Remove(landId);
                ReclaimedLandRecord land = ReclaimedLandStore.Find(landId);
                if (land == null || !landObjects.TryGetValue(landId, out LandObject obj)) continue;
                // Skip if rebuilt with the same revision since enqueueing.
                if (obj.HasTerrainSignature && TryGetTerrainSignature(land, out long signature) && signature == obj.TerrainSignature) continue;
                BuildLand(land);
                yield return RenderWorkScheduler.Continue;
            }
        }
        finally
        {
            rebuildCoroutine = null;
        }
    }

    /// <summary>
    /// Build one reclaimed-land mesh against current ground and replace its GameObject mesh (create if absent).
    /// Draw nothing if it has no crest (no quay).
    /// </summary>
    private void BuildLand(ReclaimedLandRecord land)
    {
        if (!land.TopHeight.HasValue)
        {
            DestroyLand(land.Id);
            return;
        }

        bool hasSignature = TryGetTerrainSignature(land, out long signature);
        ReclaimedLandMeshBuilder.MeshData data = ReclaimedLandMeshBuilder.Build(land.Band, land.Walls, land.TopHeight.Value, SampleGround);
        if (data.Vertices.Count < 3 || data.TopTriangles.Count + data.WallTriangles.Count < 3)
        {
            DestroyLand(land.Id);
            return;
        }

        var triangles = new List<int>(data.TopTriangles.Count + data.WallTriangles.Count);
        triangles.AddRange(data.TopTriangles);
        triangles.AddRange(data.WallTriangles);
        var mesh = new Mesh { name = $"ReclaimedLand_{land.Id}" };
        if (data.Vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(data.Vertices);
        mesh.SetNormals(data.Normals);
        mesh.SetUVs(0, data.Uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();

        if (landObjects.TryGetValue(land.Id, out LandObject obj) && obj.GameObject != null)
        {
            Mesh oldMesh = obj.Mesh;
            obj.GameObject.GetComponent<MeshFilter>().sharedMesh = mesh;
            obj.GameObject.GetComponent<MeshCollider>().sharedMesh = mesh;
            obj.Mesh = mesh;
            if (oldMesh != null) Destroy(oldMesh);
        }
        else
        {
            obj = new LandObject { GameObject = CreateLandObject(land.Id, mesh), Mesh = mesh };
            landObjects[land.Id] = obj;
            quayObjects.Add(obj.GameObject);
        }
        obj.TerrainSignature = signature;
        obj.HasTerrainSignature = hasSignature;
    }

    /// <summary>
    /// Use the same structure as background terrain chunks. Add VisualTerrainObject after the renderer, since Awake captures MeshRenderer;
    /// do not assign a tag (SeaObject could let lake water borrow this material).
    /// </summary>
    private GameObject CreateLandObject(int landId, Mesh mesh)
    {
        var go = new GameObject($"ReclaimedLand_{landId}");
        go.transform.SetParent(transform, false);
        int groundLayer = LayerMask.NameToLayer("Ground");
        if (groundLayer >= 0) go.layer = groundLayer;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = ResolveMaterial();
        go.AddComponent<MeshCollider>().sharedMesh = mesh;
        go.AddComponent<VisualTerrainObject>();
        go.AddComponent<TerrainInputHandler>();
        return go;
    }

    // Shared DepthOffsetLit version rendering in front of terrain (+1,+1), avoiding Z-fighting where crest inner edges meet terrain at equal heights.
    private Material ResolveMaterial()
    {
        if (quayMaterial == null)
        {
            Material[] shared = BuildingManager.GetSharedDepthOffsetMaterials(new[] { BuildingMeshBuilder.GetCommonConcreteMaterial() });
            quayMaterial = shared != null && shared.Length > 0 ? shared[0] : null;
        }
        return quayMaterial;
    }

    private void DestroyLand(int landId)
    {
        if (!landObjects.TryGetValue(landId, out LandObject obj)) return;
        landObjects.Remove(landId);
        if (obj.GameObject != null)
        {
            quayObjects.Remove(obj.GameObject);
            Destroy(obj.GameObject);
        }
        if (obj.Mesh != null) Destroy(obj.Mesh);
    }

    private void DestroyAllLands()
    {
        foreach (int id in new List<int>(landObjects.Keys)) DestroyLand(id);
        quayObjects.Clear();
    }

    // Face lower edges sample ground QUAY_PROBE_DISTANCE outside the perimeter; examine revisions over an expanded bounding rectangle.
    private static Rect TerrainArea(ReclaimedLandRecord land)
    {
        float margin = Constants.QUAY_PROBE_DISTANCE + 1f;
        Rect b = land.Bounds;
        return Rect.MinMaxRect(b.xMin - margin, b.yMin - margin, b.xMax + margin, b.yMax + margin);
    }

    // Without TerrainManager the revision is unknown, so return false (callers must not skip rebuilding).
    private static bool TryGetTerrainSignature(ReclaimedLandRecord land, out long signature)
    {
        TerrainManager terrainManager = Utils.CachedTerrainManager;
        if (terrainManager == null)
        {
            signature = 0;
            return false;
        }
        signature = terrainManager.GetTerrainSignature(TerrainArea(land));
        return true;
    }

    // === API response conversion ===

    private static List<ReclaimedLandRecord> ParseLands(ReclaimedLandsResponse response)
    {
        var result = new List<ReclaimedLandRecord>();
        if (response?.reclaimed_lands == null) return result;
        foreach (var dto in response.reclaimed_lands)
        {
            if (dto == null) continue;
            Vector2[] ring = CleanRing(GeneratedModelUtils.ParseRing(dto.polygon));
            if (ring == null) continue;

            var band = new List<Vector2[][]>();
            var bandBounds = new List<Rect>();
            if (dto.band is JArray polygons)
            {
                foreach (JToken polygonToken in polygons)
                {
                    if (!(polygonToken is JArray ringTokens) || ringTokens.Count == 0) continue;
                    Vector2[] outer = CleanRing(GeneratedModelUtils.ParseRing(ringTokens[0]));
                    if (outer == null) continue;
                    var rings = new List<Vector2[]> { outer };
                    for (int i = 1; i < ringTokens.Count; i++)
                    {
                        Vector2[] hole = CleanRing(GeneratedModelUtils.ParseRing(ringTokens[i]));
                        if (hole != null) rings.Add(hole);
                    }
                    band.Add(rings.ToArray());
                    bandBounds.Add(BoundsOf(outer));
                }
            }

            var record = new ReclaimedLandRecord
            {
                Id = dto.reclaimed_land_id,
                Version = dto.version,
                Ring = ring,
                TopHeight = dto.top_height,
                Quays = ParseSegments(dto.quays),
                Walls = ParseSegments(dto.walls),
                Band = band.ToArray(),
                BandBounds = bandBounds.ToArray(),
            };
            Rect bounds = BoundsOf(ring);
            foreach (Rect rect in record.BandBounds) bounds = Union(bounds, rect);
            foreach (var wall in record.Walls) bounds = Union(bounds, Rect.MinMaxRect(
                Mathf.Min(wall.A.x, wall.B.x), Mathf.Min(wall.A.y, wall.B.y), Mathf.Max(wall.A.x, wall.B.x), Mathf.Max(wall.A.y, wall.B.y)));
            record.Bounds = bounds;
            result.Add(record);
        }
        return result;
    }

    /// <summary>Remove closing points and consecutive duplicates. Null if fewer than 3 points.</summary>
    private static Vector2[] CleanRing(List<Vector2> points)
    {
        if (points == null) return null;
        var ring = new List<Vector2>(points.Count);
        foreach (Vector2 p in points)
        {
            if (ring.Count == 0 || (ring[ring.Count - 1] - p).sqrMagnitude > 1e-10f) ring.Add(p);
        }
        while (ring.Count >= 2 && (ring[0] - ring[ring.Count - 1]).sqrMagnitude <= 1e-10f) ring.RemoveAt(ring.Count - 1);
        return ring.Count >= 3 ? ring.ToArray() : null;
    }

    private static ReclaimedLandSegment[] ParseSegments(List<SegmentDto> dtos)
    {
        var result = new List<ReclaimedLandSegment>();
        if (dtos == null) return result.ToArray();
        foreach (var dto in dtos)
        {
            if (dto?.a == null || dto.b == null || dto.normal == null) continue;
            if (dto.a.Length < 2 || dto.b.Length < 2 || dto.normal.Length < 2) continue;
            result.Add(new ReclaimedLandSegment(
                new Vector2(dto.a[0], dto.a[1]), new Vector2(dto.b[0], dto.b[1]), new Vector2(dto.normal[0], dto.normal[1])));
        }
        return result.ToArray();
    }

    private static Rect BoundsOf(Vector2[] ring)
    {
        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        foreach (Vector2 p in ring)
        {
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y);
        }
        return Rect.MinMaxRect(minX, minZ, maxX, maxZ);
    }

    private static Rect Union(Rect a, Rect b) =>
        Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));

    [Serializable]
    private class ReclaimedLandsResponse
    {
        public List<ReclaimedLandDto> reclaimed_lands;
    }

    [Serializable]
    private class ReclaimedLandDto
    {
        public int reclaimed_land_id;
        public int version;
        public float? top_height;
        public JToken polygon;
        public List<SegmentDto> quays;
        public List<SegmentDto> walls;
        public JToken band; // [[outer boundary, holes...], ...] (GeoJSON MultiPolygon coordinates). Receive raw JToken because nesting is deep
    }

    [Serializable]
    private class SegmentDto
    {
        public float[] a;
        public float[] b;
        public float[] normal;
    }
}
