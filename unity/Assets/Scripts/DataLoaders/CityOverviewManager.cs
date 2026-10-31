using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Citywide overview. Immediately fill areas without resident detail using the same building boxes and road bands as Far rendering.
/// Combine core overview lists (/buildings/overview and /paths/overview) into meshes per terrain cell,
/// excluding buildings/roads with actual objects (Far, Normal, or Close) displayed or hidden by the display filter.
/// Overlap lasts only until exclusions are applied; slightly shrink boxes inside actual objects and lower bands to avoid coplanar surfaces.
/// Place at overview terrain heights (TerrainManager.TrySampleOverview), except paths that do not follow terrain (bridges, viaducts):
/// they take the Far shape, suspension towers included, from the whole-city copy (CityDataCache) so they do not sink to the ground or seabed.
/// Refetch lists after each epoch completion or committed edit.
/// </summary>
public class CityOverviewManager : MonoBehaviour
{
    public static CityOverviewManager Instance { get; private set; }

    private const float BOX_SHRINK = 0.98f;
    private const float PLATE_SINK = 0.05f;
    /// <summary>Bands are seen from several km away, so terrain-following samples can be spaced this far apart (m).</summary>
    private const float PLATE_POINT_SPACING = 30f;
    /// <summary>Interval (frames) for checking actual object visibility.</summary>
    private const int SCAN_INTERVAL_FRAMES = 15;

    /// <summary>Fetched list and terrain-cell membership. Indices follow list order.</summary>
    private sealed class Snapshot
    {
        public int[] buildingIds, variants, landmarks, floors, statuses, polygonOffsets;
        public float[] centers, bases, polygon;
        public int[] pathIds, pathVariants, pointOffsets;
        public float[] points;
        public long terrainRevision; // Overview revision at fetch time (TerrainManager.OverviewDataRevision)
        public readonly Dictionary<Vector2Int, (List<int> items, ulong hash)> buildingChunks = new Dictionary<Vector2Int, (List<int>, ulong)>();
        public readonly Dictionary<Vector2Int, (List<int> items, ulong hash)> pathChunks = new Dictionary<Vector2Int, (List<int>, ulong)>();
        public readonly Dictionary<int, Vector2Int> buildingCells = new Dictionary<int, Vector2Int>();
        public readonly Dictionary<int, Vector2Int> pathCells = new Dictionary<int, Vector2Int>();
    }

    /// <summary>Combined mesh for one terrain cell. items holds indices into the current list, replaced on every refetch.</summary>
    private sealed class Chunk
    {
        public Vector2Int cell;
        public bool isPath;
        public List<int> items;
        public ulong hash;
        public GameObject root;
        public readonly List<Mesh> meshes = new List<Mesh>();
        public bool queued;
        /// <summary>Holds paths drawn from the whole-city copy, or missing from it. Rebuilt when the copy's revision changes.</summary>
        public bool readsCityData;
    }

    private TerrainManager terrainManager;
    private BuildingManager buildingManager;
    private PathManager pathManager;
    private Transform container;
    private Snapshot snapshot;
    private readonly Dictionary<Vector2Int, Chunk> buildingChunks = new Dictionary<Vector2Int, Chunk>();
    private readonly Dictionary<Vector2Int, Chunk> pathChunks = new Dictionary<Vector2Int, Chunk>();
    private readonly List<Chunk> dirtyChunks = new List<Chunk>();
    private HashSet<int> shownBuildings = new HashSet<int>(), scanBuildings = new HashSet<int>();
    private HashSet<int> shownPaths = new HashSet<int>(), scanPaths = new HashSet<int>();
    private int filterRevision;
    private int cityDataRevision = -1;
    private bool refreshRequested = true;
    private int generation;
    private Coroutine fetchCoroutine;
    private Coroutine buildWorker;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        container = new GameObject("CityOverview").transform;
        container.SetParent(transform, false);
    }

    public void Initialize(TerrainManager terrain, BuildingManager buildings, PathManager paths)
    {
        terrainManager = terrain;
        buildingManager = buildings;
        pathManager = paths;
    }

    /// <summary>Refetch the list when overview terrain is next ready (immediately after a load batch starts).</summary>
    public void RequestRefresh() => refreshRequested = true;

    /// <summary>
    /// Called once overview terrain is ready. Heights use the overview, so never fetch the list earlier.
    /// Refetch if the overview changed since list retrieval (a refetch requested during fetching may be consumed before the overview updates).
    /// </summary>
    public void OnTerrainOverviewReady()
    {
        if (snapshot != null && terrainManager != null && snapshot.terrainRevision != terrainManager.OverviewDataRevision) refreshRequested = true;
        if (!refreshRequested || fetchCoroutine != null) return;
        refreshRequested = false;
        fetchCoroutine = RenderWorkScheduler.Start(this, FetchSnapshot(), "CityOverviewManager");
    }

    /// <summary>Switch sessions or clear all. Discard in-flight lists and refetch with the next overview terrain.</summary>
    public void Clear()
    {
        generation++;
        if (fetchCoroutine != null) RenderWorkScheduler.Stop(this, fetchCoroutine);
        fetchCoroutine = null;
        foreach (var chunk in buildingChunks.Values) DestroyChunk(chunk);
        foreach (var chunk in pathChunks.Values) DestroyChunk(chunk);
        buildingChunks.Clear();
        pathChunks.Clear();
        dirtyChunks.Clear();
        shownBuildings.Clear();
        shownPaths.Clear();
        snapshot = null;
        refreshRequested = true;
    }

    private void Update()
    {
        if (snapshot == null || Time.frameCount % SCAN_INTERVAL_FRAMES != 0) return;
        scanBuildings.Clear();
        buildingManager?.CollectShownBuildingIds(scanBuildings);
        MarkChanged(scanBuildings, shownBuildings, snapshot.buildingCells, buildingChunks);
        (shownBuildings, scanBuildings) = (scanBuildings, shownBuildings);
        scanPaths.Clear();
        pathManager?.CollectShownPathIds(scanPaths);
        MarkChanged(scanPaths, shownPaths, snapshot.pathCells, pathChunks);
        (shownPaths, scanPaths) = (scanPaths, shownPaths);
        if (filterRevision != ObjectVisibilityFilter.Revision)
        {
            filterRevision = ObjectVisibilityFilter.Revision;
            foreach (var chunk in buildingChunks.Values) MarkDirty(chunk);
            foreach (var chunk in pathChunks.Values) MarkDirty(chunk);
        }
        // Before the first full synchronization the copy grows range by range, so wait for it to complete.
        var cityData = CityDataCache.Instance;
        if (cityData != null && cityData.IsComplete && cityDataRevision != cityData.Revision)
        {
            cityDataRevision = cityData.Revision;
            foreach (var chunk in pathChunks.Values) if (chunk.readsCityData) MarkDirty(chunk);
        }
    }

    /// <summary>Rebuild cells containing buildings/roads whose actual object visibility changed.</summary>
    private void MarkChanged(HashSet<int> now, HashSet<int> before, Dictionary<int, Vector2Int> cells, Dictionary<Vector2Int, Chunk> chunks)
    {
        foreach (int id in now)
            if (!before.Contains(id) && cells.TryGetValue(id, out Vector2Int cell) && chunks.TryGetValue(cell, out Chunk chunk)) MarkDirty(chunk);
        foreach (int id in before)
            if (!now.Contains(id) && cells.TryGetValue(id, out Vector2Int cell) && chunks.TryGetValue(cell, out Chunk chunk)) MarkDirty(chunk);
    }

    private void MarkDirty(Chunk chunk)
    {
        if (chunk.queued) return;
        chunk.queued = true;
        dirtyChunks.Add(chunk);
        if (buildWorker == null) buildWorker = RenderWorkScheduler.Start(this, BuildWorker(), "CityOverviewManager", () => dirtyChunks.Count > 0);
    }

    private IEnumerator FetchSnapshot()
    {
        int requested = generation;
        try
        {
            BuildingOverviewResponse buildings = null;
            PathOverviewResponse paths = null;
            yield return ApiClient.GetBulk<BuildingOverviewResponse>("/buildings/overview", value => buildings = value,
                timingLabel: "CityOverview buildings", timeoutSeconds: 60, isCanceled: () => requested != generation);
            if (requested != generation) yield break;
            yield return ApiClient.GetBulk<PathOverviewResponse>("/paths/overview", value => paths = value,
                timingLabel: "CityOverview paths", timeoutSeconds: 60, isCanceled: () => requested != generation);
            if (requested != generation) yield break;
            if (buildings == null || paths == null)
            {
                refreshRequested = true;
                yield break;
            }
            long terrainRevision = terrainManager != null ? terrainManager.OverviewDataRevision : -1;
            float cellSize = TerrainManager.CellSize;
            Task<Snapshot> parse = BackgroundWork.Run(() => ParseSnapshot(buildings, paths, cellSize, terrainRevision));
            yield return Utils.WaitTask(parse);
            if (requested != generation) yield break;
            if (parse.IsFaulted)
            {
                refreshRequested = true;
                yield break;
            }
            Apply(parse.Result);
        }
        finally
        {
            if (requested == generation) fetchCoroutine = null;
        }
        // If an epoch advances during fetching, fetch again afterward.
        if (refreshRequested && terrainManager != null && terrainManager.OverviewDataRevision >= 0) OnTerrainOverviewReady();
    }

    /// <summary>Decode list bytes into arrays and assign terrain cells using building centers or road centerline midpoints. Called on another thread.</summary>
    private static Snapshot ParseSnapshot(BuildingOverviewResponse buildings, PathOverviewResponse paths, float cellSize, long terrainRevision)
    {
        var s = new Snapshot
        {
            buildingIds = Ints(buildings.agent_id, buildings.count),
            variants = Ints(buildings.variant, buildings.count),
            landmarks = Ints(buildings.landmark, buildings.count),
            floors = Ints(buildings.floors, buildings.count),
            statuses = Ints(buildings.status, buildings.count),
            centers = Floats(buildings.center, buildings.count * 2),
            bases = Floats(buildings.@base, buildings.count),
            polygonOffsets = Ints(buildings.polygon_offset, buildings.count + 1),
            pathIds = Ints(paths.path_id, paths.count),
            pathVariants = Ints(paths.path_variant, paths.count),
            pointOffsets = Ints(paths.point_offset, paths.count + 1),
            terrainRevision = terrainRevision,
        };
        s.polygon = Floats(buildings.polygon, s.polygonOffsets[buildings.count] * 2);
        s.points = Floats(paths.points, s.pointOffsets[paths.count] * 2);

        for (int i = 0; i < buildings.count; i++)
        {
            var cell = new Vector2Int(Mathf.FloorToInt(s.centers[2 * i] / cellSize), Mathf.FloorToInt(s.centers[2 * i + 1] / cellSize));
            s.buildingCells[s.buildingIds[i]] = cell;
            ulong hash = Mix(Mix(Mix(Mix((ulong)(uint)s.buildingIds[i], (uint)s.variants[i]), (uint)s.landmarks[i]), (uint)s.floors[i]), (uint)s.statuses[i]);
            hash = Mix(Mix(Mix(hash, Bits(s.centers[2 * i])), Bits(s.centers[2 * i + 1])), Bits(s.bases[i]));
            for (int k = s.polygonOffsets[i] * 2; k < s.polygonOffsets[i + 1] * 2; k++) hash = Mix(hash, Bits(s.polygon[k]));
            AddToChunk(s.buildingChunks, cell, i, hash);
        }
        for (int i = 0; i < paths.count; i++)
        {
            int middle = (s.pointOffsets[i] + s.pointOffsets[i + 1]) / 2;
            var cell = new Vector2Int(Mathf.FloorToInt(s.points[2 * middle] / cellSize), Mathf.FloorToInt(s.points[2 * middle + 1] / cellSize));
            s.pathCells[s.pathIds[i]] = cell;
            ulong hash = Mix((ulong)(uint)s.pathIds[i], (uint)s.pathVariants[i]);
            for (int k = s.pointOffsets[i] * 2; k < s.pointOffsets[i + 1] * 2; k++) hash = Mix(hash, Bits(s.points[k]));
            AddToChunk(s.pathChunks, cell, i, hash);
        }
        // Heights depend on overview elevation, so rebuild every cell when the overview changes.
        foreach (var chunks in new[] { s.buildingChunks, s.pathChunks })
            foreach (var cell in new List<Vector2Int>(chunks.Keys))
                chunks[cell] = (chunks[cell].items, Mix(chunks[cell].hash, (ulong)terrainRevision));
        return s;
    }

    private static void AddToChunk(Dictionary<Vector2Int, (List<int> items, ulong hash)> chunks, Vector2Int cell, int index, ulong itemHash)
    {
        if (!chunks.TryGetValue(cell, out var chunk)) chunk = (new List<int>(), 0UL);
        chunk.items.Add(index);
        chunks[cell] = (chunk.items, chunk.hash + Mix(itemHash, 0x9E3779B97F4A7C15UL));
    }

    private static int[] Ints(byte[] bytes, int count)
    {
        if (bytes == null || bytes.Length != count * 4) throw new InvalidOperationException("超遠景の一覧の列の長さが不正です");
        var result = new int[count];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }

    private static float[] Floats(byte[] bytes, int count)
    {
        if (bytes == null || bytes.Length != count * 4) throw new InvalidOperationException("超遠景の一覧の列の長さが不正です");
        var result = new float[count];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }

    private static ulong Bits(float value) => (uint)BitConverter.SingleToInt32Bits(value);

    private static ulong Mix(ulong hash, ulong value)
    {
        ulong x = hash ^ value;
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    /// <summary>Replace the list. Reuse meshes in cells with matching contents (and overview revision), and destroy removed cells.</summary>
    private void Apply(Snapshot next)
    {
        snapshot = next;
        Sync(buildingChunks, next.buildingChunks, false);
        Sync(pathChunks, next.pathChunks, true);
    }

    private void Sync(Dictionary<Vector2Int, Chunk> chunks, Dictionary<Vector2Int, (List<int> items, ulong hash)> next, bool isPath)
    {
        var removed = new List<Vector2Int>();
        foreach (var pair in chunks)
            if (!next.ContainsKey(pair.Key)) removed.Add(pair.Key);
        foreach (var cell in removed)
        {
            DestroyChunk(chunks[cell]);
            chunks.Remove(cell);
        }
        dirtyChunks.RemoveAll(chunk => chunk.isPath == isPath && (!chunks.TryGetValue(chunk.cell, out Chunk current) || current != chunk));
        foreach (var pair in next)
        {
            if (!chunks.TryGetValue(pair.Key, out Chunk chunk))
                chunks[pair.Key] = chunk = new Chunk { cell = pair.Key, isPath = isPath, hash = ~pair.Value.hash };
            chunk.items = pair.Value.items;
            if (chunk.hash == pair.Value.hash) continue;
            chunk.hash = pair.Value.hash;
            MarkDirty(chunk);
        }
    }

    private void DestroyChunk(Chunk chunk)
    {
        foreach (var mesh in chunk.meshes) if (mesh != null) Destroy(mesh);
        chunk.meshes.Clear();
        if (chunk.root != null) Destroy(chunk.root);
        chunk.root = null;
        chunk.queued = false;
    }

    /// <summary>Build pending cells one at a time, nearest the camera first.</summary>
    private IEnumerator BuildWorker()
    {
        var watch = new System.Diagnostics.Stopwatch();
        while (true)
        {
            if (dirtyChunks.Count == 0 || snapshot == null)
            {
                yield return null;
                continue;
            }
            Chunk chunk = TakeNearestDirtyChunk();
            watch.Restart();
            if (chunk.isPath) yield return BuildPathChunk(chunk, watch);
            else yield return BuildBuildingChunk(chunk, watch);
            yield return RenderWorkScheduler.Continue;
        }
    }

    private Chunk TakeNearestDirtyChunk()
    {
        Camera cam = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
        float cellSize = TerrainManager.CellSize;
        int best = 0;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < dirtyChunks.Count; i++)
        {
            Vector2Int cell = dirtyChunks[i].cell;
            float dx = (cell.x + 0.5f) * cellSize - camPos.x, dz = (cell.y + 0.5f) * cellSize - camPos.z;
            float distance = dx * dx + dz * dz;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        Chunk chunk = dirtyChunks[best];
        dirtyChunks.RemoveAt(best);
        chunk.queued = false;
        return chunk;
    }

    private IEnumerator BuildBuildingChunk(Chunk chunk, System.Diagnostics.Stopwatch watch)
    {
        Snapshot s = snapshot;
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        foreach (int i in chunk.items)
        {
            int id = s.buildingIds[i];
            if (shownBuildings.Contains(id) || ObjectVisibilityFilter.IsHidden(InputObjectType.BUILDING, id)) continue;
            int start = s.polygonOffsets[i], end = s.polygonOffsets[i + 1];
            if (end - start < 3) continue;
            var footprint = new Vector2[end - start];
            for (int k = start; k < end; k++) footprint[k - start] = new Vector2(s.polygon[2 * k], s.polygon[2 * k + 1]) * BOX_SHRINK;
            footprint = BuildingMeshUtils.NormalizeFootprint(footprint);
            float height = BuildingMeshBuilder.GetFarBoxHeight(s.variants[i], s.landmarks[i], s.floors[i], s.statuses[i]) * BOX_SHRINK;
            float x = s.centers[2 * i], z = s.centers[2 * i + 1];
            float baseY = float.IsNaN(s.bases[i]) ? GroundHeight(x, z) : s.bases[i];
            BuildingMeshBuilder.AppendExtrudedFootprint(footprint, height, new Vector3(x, baseY, z), vertices, triangles);
            if (watch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
            {
                yield return RenderWorkScheduler.Continue;
                watch.Restart();
            }
        }
        var built = new List<(Mesh, Material)>();
        if (triangles.Count > 0)
        {
            var mesh = new Mesh { name = "CityOverviewBuildings", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            built.Add((mesh, BuildingManager.GetFarBuildingMaterial()));
        }
        ReplaceMeshes(chunk, built);
    }

    private IEnumerator BuildPathChunk(Chunk chunk, System.Diagnostics.Stopwatch watch)
    {
        Snapshot s = snapshot;
        var groups = new Dictionary<Material, List<CombineInstance>>();
        var temporary = new List<Mesh>();
        GameObject structures = null;
        chunk.readsCityData = false;
        foreach (int i in chunk.items)
        {
            int id = s.pathIds[i];
            if (shownPaths.Contains(id) || ObjectVisibilityFilter.IsHidden(InputObjectType.PATH, id)) continue;
            List<Vector3> points = PlatePoints(s, i);
            // Paths that do not follow terrain (bridges, viaducts) take their Far shape from the whole-city copy.
            // Until the copy holds the path, draw the overview band and rebuild once it arrives.
            ConstructionPathSummary path = null;
            bool known = CityDataCache.Instance != null && CityDataCache.Instance.TryGetPath(id, out path);
            chunk.readsCityData |= !known;
            if (known && !PathManager.GetPathFollowTerrain(path) && pathManager != null)
            {
                chunk.readsCityData = true;
                List<Vector3> farPoints = pathManager.CalculateFarPoints(path);
                if (farPoints != null)
                {
                    for (int k = 0; k < farPoints.Count; k++) farPoints[k] -= Vector3.up * PLATE_SINK;
                    points = farPoints;
                    structures ??= new GameObject("Structures");
                    SuspensionBridgeGenerator.GenerateFar(structures, points, path);
                }
            }
            Mesh mesh = PathFarModel.Build(s.pathVariants[i], points, out Material[] materials);
            if (mesh == null) continue;
            temporary.Add(mesh);
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                Material material = materials != null && materials.Length > 0 ? materials[Mathf.Min(sub, materials.Length - 1)] : null;
                if (material == null) continue;
                if (!groups.TryGetValue(material, out List<CombineInstance> group)) groups[material] = group = new List<CombineInstance>();
                group.Add(new CombineInstance { mesh = mesh, subMeshIndex = sub, transform = Matrix4x4.identity });
            }
            if (watch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
            {
                yield return RenderWorkScheduler.Continue;
                watch.Restart();
            }
        }
        var built = new List<(Mesh, Material)>();
        foreach (var group in groups)
        {
            var combined = new Mesh { name = "CityOverviewPaths", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            combined.CombineMeshes(group.Value.ToArray(), true, true);
            built.Add((combined, group.Key));
        }
        foreach (var mesh in temporary) Destroy(mesh);
        if (structures != null && structures.transform.childCount == 0)
        {
            Destroy(structures);
            structures = null;
        }
        ReplaceMeshes(chunk, built, structures);
    }

    /// <summary>Subdivide centerlines to terrain-following sample spacing and place at overview heights. Lower slightly to avoid matching actual band heights.</summary>
    private List<Vector3> PlatePoints(Snapshot s, int index)
    {
        var result = new List<Vector3>();
        int start = s.pointOffsets[index], end = s.pointOffsets[index + 1];
        for (int k = start; k < end; k++)
        {
            var point = new Vector2(s.points[2 * k], s.points[2 * k + 1]);
            if (k > start)
            {
                var previous = new Vector2(s.points[2 * k - 2], s.points[2 * k - 1]);
                int steps = Mathf.CeilToInt(Vector2.Distance(previous, point) / PLATE_POINT_SPACING);
                for (int step = 1; step < steps; step++) result.Add(PlatePoint(Vector2.Lerp(previous, point, (float)step / steps)));
            }
            result.Add(PlatePoint(point));
        }
        return result;
    }

    private Vector3 PlatePoint(Vector2 point) =>
        new Vector3(point.x, GroundHeight(point.x, point.y) + RoadPartHeights.ROAD_SPLINE_OFFSET - PLATE_SINK, point.y);

    /// <summary>Overview terrain height. Inside reclaimed land bands, use the crest top (same rule as Utils.GetGroundHeight).</summary>
    private float GroundHeight(float x, float z)
    {
        float height = terrainManager != null && terrainManager.TrySampleOverview(new Vector3(x, 0f, z), out float sampled) ? sampled : 0f;
        if (ReclaimedLandStore.TryGetBandTop(x, z, out float top) && top > height) height = top;
        return height;
    }

    /// <summary>Replace a cell's mesh. Keep one child object per material. Structures (suspension towers) go under the same root and are destroyed with it.</summary>
    private void ReplaceMeshes(Chunk chunk, List<(Mesh mesh, Material material)> built, GameObject structures = null)
    {
        foreach (var mesh in chunk.meshes) if (mesh != null) Destroy(mesh);
        chunk.meshes.Clear();
        if (chunk.root != null) Destroy(chunk.root);
        chunk.root = null;
        if (built.Count == 0 && structures == null) return;
        chunk.root = new GameObject($"{(chunk.isPath ? "Paths" : "Buildings")}_{chunk.cell.x}_{chunk.cell.y}");
        chunk.root.transform.SetParent(container, false);
        structures?.transform.SetParent(chunk.root.transform, false);
        foreach (var (mesh, material) in built)
        {
            var part = new GameObject(mesh.name);
            part.transform.SetParent(chunk.root.transform, false);
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = part.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            chunk.meshes.Add(mesh);
        }
    }
}
