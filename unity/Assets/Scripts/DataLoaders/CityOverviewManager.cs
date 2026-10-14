using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 都市全域の超遠景。詳細を常駐させていない範囲も、建物は遠景と同じ箱、道路は遠景と同じ帯で最初から埋める。
/// コアの全域一覧(/buildings/overview・/paths/overview)を地形セル単位の結合メッシュにまとめ、
/// 実物(遠景・通常・接写のどれか)を表示している建物・道路と、表示フィルタで隠した建物・道路はメッシュから除く。
/// 実物との重なりは除外を反映するまでの短い間だけなので、箱は実物の内側へわずかに縮め、帯はわずかに下げて面の重なりを避ける。
/// 高さは全景地形(TerrainManager.TrySampleOverview)に置く。一覧はエポック完了・編集確定のたびに取り直す。
/// </summary>
public class CityOverviewManager : MonoBehaviour
{
    public static CityOverviewManager Instance { get; private set; }

    private const float BOX_SHRINK = 0.98f;
    private const float PLATE_SINK = 0.05f;
    /// <summary>数km先から見る帯なので、地形へ沿わせる点の間隔はこの距離(m)まで粗くてよい。</summary>
    private const float PLATE_POINT_SPACING = 30f;
    /// <summary>実物の表示状態を照合する間隔(フレーム)。</summary>
    private const int SCAN_INTERVAL_FRAMES = 15;

    /// <summary>取得した一覧と、地形セルごとの所属。添字は一覧の並び。</summary>
    private sealed class Snapshot
    {
        public int[] buildingIds, variants, landmarks, floors, statuses, polygonOffsets;
        public float[] centers, bases, polygon;
        public int[] pathIds, pathVariants, pointOffsets;
        public float[] points;
        public long terrainRevision; // 取得時の全景の版(TerrainManager.OverviewDataRevision)
        public readonly Dictionary<Vector2Int, (List<int> items, ulong hash)> buildingChunks = new Dictionary<Vector2Int, (List<int>, ulong)>();
        public readonly Dictionary<Vector2Int, (List<int> items, ulong hash)> pathChunks = new Dictionary<Vector2Int, (List<int>, ulong)>();
        public readonly Dictionary<int, Vector2Int> buildingCells = new Dictionary<int, Vector2Int>();
        public readonly Dictionary<int, Vector2Int> pathCells = new Dictionary<int, Vector2Int>();
    }

    /// <summary>地形セル1つ分の結合メッシュ。items は現在の一覧の添字で、一覧を取り直すたびに差し替える。</summary>
    private sealed class Chunk
    {
        public Vector2Int cell;
        public bool isPath;
        public List<int> items;
        public ulong hash;
        public GameObject root;
        public readonly List<Mesh> meshes = new List<Mesh>();
        public bool queued;
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

    /// <summary>次に全景地形が揃ったとき(ロードバッチの開始直後)に一覧を取り直す。</summary>
    public void RequestRefresh() => refreshRequested = true;

    /// <summary>
    /// 全景地形が揃った時点で呼ばれる。高さを全景に合わせるので、それより前には一覧を取らない。
    /// 全景が一覧の取得時から変わっていたら取り直す(取得中に重なった再取得の要求が、全景の更新前に消化されることがあるため)。
    /// </summary>
    public void OnTerrainOverviewReady()
    {
        if (snapshot != null && terrainManager != null && snapshot.terrainRevision != terrainManager.OverviewDataRevision) refreshRequested = true;
        if (!refreshRequested || fetchCoroutine != null) return;
        refreshRequested = false;
        fetchCoroutine = RenderWorkScheduler.Start(this, FetchSnapshot(), "CityOverviewManager");
    }

    /// <summary>セッションの切り替え・全消去。取得中の一覧も捨て、次の全景地形で取り直す。</summary>
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
    }

    /// <summary>実物の表示が切り替わった建物・道路を含むセルを作り直す。</summary>
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
        // 取得中にエポックが進んだら、続けて取り直す。
        if (refreshRequested && terrainManager != null && terrainManager.OverviewDataRevision >= 0) OnTerrainOverviewReady();
    }

    /// <summary>一覧のバイト列を配列へ戻し、建物は中心、道路は中心線の中央の点で地形セルへ振り分ける。別スレッドで呼ぶ。</summary>
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
        // 高さは全景の標高で決まるので、全景が変わったら全セルを作り直す。
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

    /// <summary>新しい一覧に差し替える。内容(と全景の版)が同じセルはメッシュを使い回し、消えたセルは破棄する。</summary>
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

    /// <summary>作り直しを待つセルを、カメラに近い順に1つずつ作る。</summary>
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
        foreach (int i in chunk.items)
        {
            int id = s.pathIds[i];
            if (shownPaths.Contains(id) || ObjectVisibilityFilter.IsHidden(InputObjectType.PATH, id)) continue;
            Mesh mesh = PathFarModel.Build(s.pathVariants[i], PlatePoints(s, i), out Material[] materials);
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
        ReplaceMeshes(chunk, built);
    }

    /// <summary>中心線を地形へ沿わせる間隔まで細かくし、全景の高さへ置く。実物の帯と同じ高さにしないよう少し下げる。</summary>
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

    /// <summary>全景地形の高さ。埋立地の帯の中は天端の上面(Utils.GetGroundHeight と同じ規則)。</summary>
    private float GroundHeight(float x, float z)
    {
        float height = terrainManager != null && terrainManager.TrySampleOverview(new Vector3(x, 0f, z), out float sampled) ? sampled : 0f;
        if (ReclaimedLandStore.TryGetBandTop(x, z, out float top) && top > height) height = top;
        return height;
    }

    /// <summary>セルのメッシュを差し替える。材質ごとに子オブジェクトを1つずつ持つ。</summary>
    private void ReplaceMeshes(Chunk chunk, List<(Mesh mesh, Material material)> built)
    {
        foreach (var mesh in chunk.meshes) if (mesh != null) Destroy(mesh);
        chunk.meshes.Clear();
        if (chunk.root != null) Destroy(chunk.root);
        chunk.root = null;
        if (built.Count == 0) return;
        chunk.root = new GameObject($"{(chunk.isPath ? "Paths" : "Buildings")}_{chunk.cell.x}_{chunk.cell.y}");
        chunk.root.transform.SetParent(container, false);
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
