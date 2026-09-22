using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GeneratedModels;

[Serializable]
public class PropScatterItem
{
    public int prop_scatter_id;
    public int prop_type;
    // GeoJSON MultiPolygon の coordinates ([[[外周リング], [穴リング], ...], ...])。
    // ネストが深いため JArray のまま受けて ParseRegion で解釈する。
    public JArray region;
    public List<float> center_point;
    public float density;
    public float edge_fade_width;
    public bool allow_water;
    public int seed;
    public int version;
}

[Serializable]
public class PropInstanceItem
{
    public int prop_instance_id;
    public int prop_type;
    public float x;
    public float y;
    public float z;
    public float rotation;
    public float scale;
    public int version;
}

[Serializable]
public class PropListResponse
{
    public List<PropScatterItem> scatters;
    public List<PropInstanceItem> instances;
    public List<PropObstacleItem> obstacles;
}

[Serializable]
public class PropObstacleItem
{
    public string key, signature;
    public List<List<float>> polygon;
    public List<List<List<float>>> holes;
    public int floor_variant;
    public float visual_variant;
}

/// <summary>
/// 既存LODセル内の小区画を単位に、非破壊の除外と共有メッシュの描画を管理する。
/// 候補点は保存座標で保持し、GPU行列だけにシーン原点の移動を反映する。
/// </summary>
public class PropManager : BaseApiDataManager
{
    public static PropManager Instance { get; private set; }
    [SerializeField, Min(1)] private int subdivisions = 8;
    [SerializeField, Min(1000)] private int maxResidentCandidates = 250000;
    [SerializeField] private bool useGpuForests = true;
    private ForestRenderer forestRenderer;
    private ForestOverview forestOverview;
    private Coroutine overviewLoading;
    private System.Threading.CancellationTokenSource overviewCancel;
    private int overviewRevision;
    private bool overviewDirty = true;
    private readonly List<Rect> forestReadyRects = new List<Rect>();
    private TerrainManager terrainManager;
    private bool GpuForests => useGpuForests && (forestRenderer ?? (forestRenderer = new ForestRenderer())).Supported;
    private bool IsGpuTree(int type) => (type == 1 || type == 2) && GpuForests;
    private const int BatchSize = 511;
    private sealed class Scatter
    {
        public PropScatterItem data;
        public List<PropGeometry.Region> regions;
    }
    private sealed class Obstacle
    {
        public PropObstacleItem data;
        public Vector2[] ring;
        public readonly List<Vector2[]> holes = new List<Vector2[]>();
        public Rect bounds;
    }
    private sealed class Spec
    {
        public Vector2Int key, cell;
        public Rect rect;
        public readonly List<Scatter> scatters = new List<Scatter>();
        public readonly List<PropInstanceItem> instances = new List<PropInstanceItem>();
        public readonly List<Obstacle> obstacles = new List<Obstacle>();
        public string sourceSignature, obstacleSignature;
    }
    private struct Candidate
    {
        public int type;
        public int sourceId; // 元レコードのID(explicitInstance ? prop_instance_id : prop_scatter_id)。削除対象の特定に使う
        public Vector3 position;
        public float rotation, scale, heightOffset;
        public bool visible;
        public bool explicitInstance;
        public bool allowWater;
        public ulong rank;
        public DrawLod lod;
        public bool distant;
    }
    /// <summary>レイ判定で拾った1本のプロップ。scatter=true なら範囲配置(id=prop_scatter_id)、false なら個別配置(id=prop_instance_id)。</summary>
    public struct PickResult
    {
        public bool scatter;
        public int id;
        public int type;
        public Bounds bounds;   // ワールド座標のAABB(ハイライト描画用)
        public float distance;  // レイ原点からの距離
    }
    private sealed class DrawBatch
    {
        public PropRenderModel.Part part;
        public Matrix4x4[] matrices;
        public Bounds bounds;
        public bool shadows;
    }
    private sealed class Chunk
    {
        public Spec spec;
        public Candidate[] points;
        public Candidate[] basePoints;
        public bool detailResident;
        public readonly HashSet<int> hiddenScatters = new HashSet<int>();
        public readonly List<DrawBatch> batches = new List<DrawBatch>();
        public readonly List<ForestRenderer.Patch> forests = new List<ForestRenderer.Patch>();
        public bool dirty = true;
        public bool heightDirty;
        public int revision;
        public Vector3 lastCamera = Vector3.positiveInfinity;
        public float lastProjection;
        public Vector3 offset;
        public bool far;
        public bool hidden;      // 小区画全体が距離の進入閾値より遠く、全点をHiddenにした状態
        public int hiddenCount;  // 直近のバッチ構築で距離によりHiddenにした点数(統計表示用)
    }
    private readonly Dictionary<Vector2Int, Chunk> chunks = new Dictionary<Vector2Int, Chunk>();
    private readonly Dictionary<Vector2Int, HashSet<Vector2Int>> chunksByCell = new Dictionary<Vector2Int, HashSet<Vector2Int>>();
    private readonly Dictionary<int, PropRenderModel> models = new Dictionary<int, PropRenderModel>();
    private readonly HashSet<Vector2Int> nearCells = new HashSet<Vector2Int>();
    private readonly Dictionary<Vector2Int, List<Obstacle>> obstacleGrid = new Dictionary<Vector2Int, List<Obstacle>>();
    private readonly HashSet<Vector2Int> obstacleReadyCells = new HashSet<Vector2Int>();
    private readonly Plane[] planes = new Plane[6];
    private Coroutine loading, worker;
    private Transform anchor;
    private bool visible = true;
    private int generation, candidateCount;
    public int PreviewRevision { get; private set; }
    public Vector3 SceneOffset => Offset;
    public int CandidateBudget => maxResidentCandidates + PropRenderPolicy.DetailCandidateBudget;
    public int ResidentCandidateCount => candidateCount;
    public int ResidentChunkCount => chunks.Count;
    public int SubmittedBatchCount { get; private set; }
    public int SubmittedInstanceCount { get; private set; }
    public int SubmittedShadowInstances { get; private set; }
    public int ForestCandidateCount { get { int n = 0; foreach (var c in chunks.Values) foreach (var p in c.forests) n += p.count; return n; } }
    public int SubmittedForestBatches { get; private set; }
    /// <summary>常駐候補のうち、カメラからの距離でHiddenにしている点数(統計表示用)。</summary>
    public int DistanceHiddenCount { get { int n = 0; foreach (var chunk in chunks.Values) n += chunk.hiddenCount; return n; } }
    public const int ShadowInstanceBudget = 4096;
    public int LastRegeneratedChunks { get; private set; }
    public int LastExclusionUpdatedChunks { get; private set; }
    private float ChunkSize => TerrainManager.CellSize / Mathf.Max(1, subdivisions);
    private Vector3 Offset => anchor != null ? anchor.position : Vector3.zero;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        anchor = GetContainer("PropContainer");
        worker = RenderWorkScheduler.Start(this, DrawWorker(), "PropManager", () => HasPendingDrawWork);
        TerrainManager.OnTerrainTileReady += TerrainReady;
    }
    private void OnDestroy()
    {
        TerrainManager.OnTerrainTileReady -= TerrainReady;
        ClearAllCells();
        if (Instance == this) Instance = null;
    }
    public void SetVisible(bool value)
    {
        if (visible == value) return;
        visible = value;
        if (!visible) ClearAllCells();
        else CellDataLoader.Instance?.ReloadAllData();
    }
    public override void LoadLodCells(LodCellSets sets, bool isForce)
    {
        nearCells.Clear(); nearCells.UnionWith(sets.nearCells);
        LoadCells(sets.farCells, isForce);
    }
    public override void LoadCells(List<Vector2Int> cells, bool isForce)
    {
        if (!visible) return;
        if (isForce) InvalidateForestOverview();
        if (GpuForests && overviewDirty && overviewLoading == null)
            overviewLoading = RenderWorkScheduler.Start(this, LoadForestOverview(), "ForestOverview");
        generation++;
        if (loading != null) RenderWorkScheduler.Stop(this, loading);
        var needed = new HashSet<Vector2Int>(cells);
        foreach (var cell in new List<Vector2Int>(loadedCells)) if (!needed.Contains(cell)) UnloadCell(cell);
        loadedCells.Clear(); loadedCells.UnionWith(needed);
        foreach (var chunk in chunks.Values)
            if (chunk.far != !nearCells.Contains(chunk.spec.cell)) chunk.dirty = true;
        loading = RenderWorkScheduler.Start(this, Load(new List<Vector2Int>(cells), isForce, generation, currentLoadBatch), "PropManager");
    }
    private bool Canceled(int serial, LoadBatch batch) => serial != generation || !visible || (batch != null && batch.IsCanceled);

    public void InvalidateForestOverview()
    {
        overviewRevision++; overviewDirty = true;
        overviewCancel?.Cancel();
        if (overviewLoading != null) RenderWorkScheduler.Stop(this, overviewLoading);
        overviewLoading = null;
    }

    private IEnumerator LoadForestOverview()
    {
        int revision = overviewRevision;
        ForestOverviewResponse response = null;
        while (response == null)
        {
            yield return ApiClient.GetBulk<ForestOverviewResponse>("/props/forest-overview", data => response = data,
                error => Debug.LogWarning($"ForestOverview: {error}"));
            if (revision != overviewRevision) yield break;
            if (response == null) yield return new WaitForSecondsRealtime(2);
        }
        overviewCancel?.Dispose();
        overviewCancel = new System.Threading.CancellationTokenSource();
        var token = overviewCancel.Token;
        var task = System.Threading.Tasks.Task.Run(() => ForestOverview.Build(response, token), token);
        while (!task.IsCompleted) yield return null;
        if (task.IsFaulted) Debug.LogException(task.Exception);
        else if (!task.IsCanceled && revision == overviewRevision && visible)
        {
            forestOverview ??= new ForestOverview();
            if (!forestOverview.Ready)
            {
                forestOverview.SetLeafColor(1, Model(1));
                forestOverview.SetLeafColor(2, Model(2));
            }
            forestOverview.Publish(task.Result);
            overviewDirty = false;
        }
        overviewLoading = null;
    }

    private IEnumerator Load(List<Vector2Int> cells, bool force, int serial, LoadBatch batch)
    {
        if (cells.Count == 0) yield break;
        CalculateMergedBBox(cells, out float x0, out float z0, out float x1, out float z1);
        PropListResponse response = null;
        yield return ApiClient.GetBulk<PropListResponse>(FormattableString.Invariant($"/props?bbox={x0},{z0},{x1},{z1}&include_obstacles=true"),
            data => response = data, error => Debug.LogError($"PropManager: {error}"));
        if (Canceled(serial, batch) || response == null || response.obstacles == null) yield break;
        yield return ApplySnapshot(response, cells, force, serial, batch);
        loading = null;
    }
    private IEnumerator ApplySnapshot(PropListResponse response, List<Vector2Int> cells, bool force, int serial, LoadBatch batch)
    {
        if (Canceled(serial, batch)) yield break;
        LastRegeneratedChunks = 0; LastExclusionUpdatedChunks = 0;
        var specs = new Dictionary<Vector2Int, Spec>();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        foreach (var cell in cells)
            for (int x = 0; x < subdivisions; x++) for (int z = 0; z < subdivisions; z++)
            {
                var key = cell * subdivisions + new Vector2Int(x, z);
                specs.Add(key, new Spec { key = key, cell = cell, rect = ChunkRect(key) });
            }
        foreach (var item in response.scatters ?? new List<PropScatterItem>())
        {
            var scatter = new Scatter { data = item, regions = PropGeometry.Parse(item.region) };
            var assigned = new HashSet<Vector2Int>();
            foreach (var region in scatter.regions)
                foreach (var key in KeysIn(region.bounds, specs))
                    if (assigned.Add(key)) specs[key].scatters.Add(scatter);
            if (timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs) { yield return RenderWorkScheduler.Continue; timer.Restart(); }
            if (Canceled(serial, batch)) yield break;
        }
        foreach (var item in response.instances ?? new List<PropInstanceItem>())
            if (specs.TryGetValue(Key(new Vector2(item.x, item.z)), out var spec)) spec.instances.Add(item);
        var nextObstacles = new Dictionary<Vector2Int, List<Obstacle>>();
        foreach (var item in response.obstacles)
        {
            if (item.polygon == null || item.polygon.Count < 3 || !FloorExists(item)) continue;
            var obstacle = new Obstacle { data = item, ring = item.polygon.ConvertAll(p => new Vector2(p[0], p[1])).ToArray() };
            if (item.holes != null)
                foreach (var hole in item.holes)
                    if (hole != null && hole.Count >= 3)
                        obstacle.holes.Add(hole.ConvertAll(p => new Vector2(p[0], p[1])).ToArray());
            obstacle.bounds = PropGeometry.Bounds(obstacle.ring);
            var expanded = obstacle.bounds; expanded.xMin -= 8; expanded.yMin -= 8; expanded.xMax += 8; expanded.yMax += 8;
            foreach (var key in KeysIn(expanded, specs))
            {
                specs[key].obstacles.Add(obstacle);
                if (!nextObstacles.TryGetValue(key, out var list)) nextObstacles[key] = list = new List<Obstacle>();
                list.Add(obstacle);
            }
            if (timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs) { yield return RenderWorkScheduler.Continue; timer.Restart(); }
            if (Canceled(serial, batch)) yield break;
        }
        obstacleGrid.Clear(); foreach (var pair in nextObstacles) obstacleGrid.Add(pair.Key, pair.Value);
        obstacleReadyCells.Clear(); obstacleReadyCells.UnionWith(cells); PreviewRevision++;
        var ordered = new List<Spec>(specs.Values);
        Vector3 camera = Camera.main != null ? Camera.main.transform.position - Offset : Vector3.zero;
        ordered.Sort((a, b) => (a.rect.center - new Vector2(camera.x, camera.z)).sqrMagnitude.CompareTo((b.rect.center - new Vector2(camera.x, camera.z)).sqrMagnitude));
        var keep = new HashSet<Vector2Int>();
        int activeSpecs = ordered.FindAll(spec => spec.scatters.Exists(s => !PropRenderPolicy.NearOnly(s.data.prop_type)) || spec.instances.Count > 0).Count;
        int quota = maxResidentCandidates / Mathf.Max(1, activeSpecs);
        int forestSpecs = 0;
        foreach (var spec in ordered) foreach (var scatter in spec.scatters) if (IsGpuTree(scatter.data.prop_type)) forestSpecs++;
        int forestQuota = ForestRenderer.CandidateBudget / Mathf.Max(1, forestSpecs);
        foreach (var spec in ordered)
        {
            if (Canceled(serial, batch)) yield break;
            if (spec.scatters.Count == 0 && spec.instances.Count == 0) { RemoveChunk(spec.key); continue; }
            spec.scatters.Sort((a,b) => a.data.prop_scatter_id.CompareTo(b.data.prop_scatter_id));
            spec.instances.Sort((a,b) => a.prop_instance_id.CompareTo(b.prop_instance_id));
            spec.obstacles.Sort((a,b) => string.CompareOrdinal(a.data.key,b.data.key));
            spec.sourceSignature = quota + ":" + forestQuota + ":" + GpuForests + ":" + string.Join(";", spec.scatters.ConvertAll(s => $"s{s.data.prop_scatter_id}:{s.data.version}")) + "|" +
                string.Join(";", spec.instances.ConvertAll(i => $"i{i.prop_instance_id}:{i.version}"));
            spec.obstacleSignature = string.Join(";", spec.obstacles.ConvertAll(o => o.data.key + ":" + o.data.signature));
            chunks.TryGetValue(spec.key, out var previous);
            Candidate[] points = previous != null && !force && previous.spec.sourceSignature == spec.sourceSignature ? previous.basePoints : null;
            if (points == null)
            {
                var generated = new StableSample<Candidate>(quota);
                int visited = 0;
                foreach (var candidate in ScatterCandidates(spec, false))
                {
                    generated.Add(candidate.rank | (1UL << 63), candidate);
                    if ((++visited & 127) == 0 && timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs)
                    { yield return RenderWorkScheduler.Continue; timer.Restart(); if (Canceled(serial, batch)) yield break; }
                }
                foreach (var point in spec.instances)
                {
                    var candidate = MakeCandidate(point.prop_type, new Vector2(point.x, point.z), point.rotation, point.scale, point.y);
                    candidate.explicitInstance = true;
                    candidate.sourceId = point.prop_instance_id;
                    candidate.rank = (uint)point.prop_instance_id;
                    generated.Add(candidate.rank, candidate);
                }
                points = generated.ToArray();
            }
            keep.Add(spec.key);
            bool sourceChanged = previous == null || !ReferenceEquals(points, previous.basePoints);
            bool exclusionChanged = previous == null || previous.spec.obstacleSignature != spec.obstacleSignature;
            if (sourceChanged || exclusionChanged || force)
            {
                if (sourceChanged) LastRegeneratedChunks++;
                if (exclusionChanged) LastExclusionUpdatedChunks++;
                if (!sourceChanged) points = (Candidate[])points.Clone();
                for (int i = 0; i < points.Length; i++)
                {
                    var p = points[i];
                    // 個別配置はプレイヤーが意図した装飾なので、敷地内や水域でも残す。
                    p.visible = p.explicitInstance || !Blocked(new Vector2(p.position.x, p.position.z), Model(p.type).Clearance(p.scale), spec.obstacles, p.allowWater);
                    points[i] = p;
                    if ((i & 127) == 0 && timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs)
                    { yield return RenderWorkScheduler.Continue; timer.Restart(); if (Canceled(serial, batch)) yield break; }
                }
                // 通信待ちの間に地形追従が終わっていても、コミット時の高さを使う。
                for (int i = 0; i < points.Length; i++)
                {
                    points[i].position.y = Height(points[i].position) + points[i].heightOffset;
                    if ((i & 127) == 0 && timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs)
                    { yield return RenderWorkScheduler.Continue; timer.Restart(); if (Canceled(serial, batch)) yield break; }
                }
                var previousBatches = previous != null ? new List<DrawBatch>(previous.batches) : null;
                RemoveChunk(spec.key);
                var chunk = new Chunk { spec = spec, points = points, basePoints = points };
                var obstacles = new List<PropGeometry.Region>();
                var landObstacles = new List<PropGeometry.Region>();
                foreach (var obstacle in spec.obstacles)
                {
                    var region = new PropGeometry.Region { bounds = obstacle.bounds };
                    region.rings.Add(obstacle.ring); region.rings.AddRange(obstacle.holes); obstacles.Add(region);
                    if (!IsWater(obstacle)) landObstacles.Add(region);
                }
                try
                {
                    foreach (var scatter in spec.scatters)
                    {
                        if (!IsGpuTree(scatter.data.prop_type)) continue;
                        var model = Model(scatter.data.prop_type);
                        if (model.normal.Count == 0) continue;
                        var patch = forestRenderer.Create(scatter.data, scatter.regions, scatter.data.allow_water ? landObstacles : obstacles, spec.rect, model, forestQuota);
                        if (patch != null) chunk.forests.Add(patch);
                    }
                }
                catch { foreach (var patch in chunk.forests) patch.Dispose(); throw; }
                if (previousBatches != null) { chunk.batches.AddRange(previousBatches); chunk.offset = previous.offset; }
                chunks.Add(spec.key, chunk); candidateCount += points.Length;
                if (!chunksByCell.TryGetValue(spec.cell, out var keys)) chunksByCell[spec.cell] = keys = new HashSet<Vector2Int>();
                keys.Add(spec.key);
            }
            if (timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs) { yield return RenderWorkScheduler.Continue; timer.Restart(); }
        }
        foreach (var key in new List<Vector2Int>(chunks.Keys)) if (!keep.Contains(key)) RemoveChunk(key);
        loading = null;
    }

    private static bool FloorExists(PropObstacleItem item)
    {
        if (item.floor_variant == 0) return true;
        return BuildingMeshUtils.ResolveFloorMaterialFromMaster(new BuildingSummary { variant = item.floor_variant, visual_variant = item.visual_variant }, $"a{item.floor_variant}") != null;
    }
    private Candidate MakeCandidate(int type, Vector2 position, float rotation, float scale, float heightOffset = 0f)
    {
        var p = new Vector3(position.x, 0, position.y); p.y = Height(p) + heightOffset;
        return new Candidate { type = type, position = p, rotation = rotation, scale = scale, heightOffset = heightOffset, lod = DrawLod.Far };
    }
    private float Height(Vector3 saved) => HeightmapTextureManager.GetTerrainHeight(saved + Offset);
    private Rect ChunkRect(Vector2Int key) => new Rect(key.x * ChunkSize, key.y * ChunkSize, ChunkSize, ChunkSize);
    private Vector2Int Key(Vector2 p) => new Vector2Int(Mathf.FloorToInt(p.x / ChunkSize), Mathf.FloorToInt(p.y / ChunkSize));
    private IEnumerable<Vector2Int> KeysIn(Rect bounds, Dictionary<Vector2Int, Spec> specs)
    {
        // 巨大な散布範囲でもマップ全域の区画を列挙しない。
        foreach (var cell in loadedCells)
        {
            var cellRect = new Rect(cell.x * TerrainManager.CellSize, cell.y * TerrainManager.CellSize, TerrainManager.CellSize, TerrainManager.CellSize);
            if (!cellRect.Overlaps(bounds)) continue;
            var min = Key(Vector2.Max(bounds.min, cellRect.min));
            var max = Vector2Int.Min(Key(Vector2.Min(bounds.max, cellRect.max)), cell * subdivisions + Vector2Int.one * (subdivisions - 1));
            for (int x = min.x; x <= max.x; x++) for (int z = min.y; z <= max.y; z++)
            { var key = new Vector2Int(x,z); if (specs.ContainsKey(key)) yield return key; }
        }
    }
    private PropRenderModel Model(int type)
    {
        if (!models.TryGetValue(type, out var model)) models[type] = model = new PropRenderModel(type);
        return model;
    }
    private static bool IsWater(Obstacle obstacle) => obstacle.data.key?.StartsWith("water:", StringComparison.Ordinal) == true;

    private static bool Blocked(Vector2 p, float clearance, List<Obstacle> obstacles, bool allowWater = false)
    {
        foreach (var obstacle in obstacles)
            if (!(allowWater && IsWater(obstacle)) && PropGeometry.BlockedWithHoles(p, clearance, obstacle.ring, obstacle.bounds, obstacle.holes)) return true;
        return false;
    }
    public bool CanPreview(Vector3 world, int type, float scale)
    {
        Vector3 saved = world - Offset;
        var key = Key(new Vector2(saved.x, saved.z));
        var cell = PositionToCell(saved);
        if (!obstacleReadyCells.Contains(cell)) return false;
        return !obstacleGrid.TryGetValue(key, out var obstacles) || !Blocked(new Vector2(saved.x,saved.z), Model(type).Clearance(scale), obstacles);
    }

    /// <summary>
    /// 画面レイに最も近い表示中プロップを返す(ブルドーザのクリック/ホバー用)。プロップは個体ごとの
    /// GameObject/Colliderを持たないため、物理レイキャストの代わりに候補点ごとのワールドAABB
    /// (モデルbounds×回転×スケール)とレイの交差で判定する。maxDistance より遠い(手前の建物・地形に
    /// 隠れている)ものは拾わない。小区画は描画バッチのBoundsで粗く絞る。
    /// </summary>
    public bool TryPick(Ray ray, float maxDistance, out PickResult result)
    {
        result = default;
        bool found = false;
        float best = maxDistance;
        Vector3 offset = Offset;
        foreach (var chunk in chunks.Values)
        {
            // GPU個体を読み戻さず、レイが横切る小区画だけ同じseedで再現する。
            foreach (var patch in chunk.forests)
            {
                if (!patch.ready || patch.hidden || !patch.bounds.IntersectRay(ray, out float patchDistance) || patchDistance > best) continue;
                var scatter = chunk.spec.scatters.Find(s => s.data.prop_scatter_id == patch.sourceId);
                if (scatter == null) continue;
                for (int index = 0; index < patch.count; index++)
                {
                    int cell = patch.count == patch.gridCount ? index : Mathf.FloorToInt((float)index * patch.gridCount / patch.count);
                    var sample = PropGeometry.Sample(patch.startX + cell % patch.gridWidth, patch.startZ + cell / patch.gridWidth, patch.spacing, patch.seed);
                    if (!patch.rect.Contains(sample.position) || !PropGeometry.Accept(sample, scatter.regions, scatter.data.edge_fade_width)) continue;
                    if (Blocked(sample.position, patch.model.Clearance(sample.scale), chunk.spec.obstacles, scatter.data.allow_water)) continue;
                    var position = new Vector3(sample.position.x, 0, sample.position.y); position.y = Height(position);
                    var cam = Camera.main;
                    if (cam != null && Vector3.Distance(position + offset, cam.transform.position) >= DrawLodConfig.PROP_HIDDEN_ENTER_DISTANCE) continue;
                    var bounds = PropRenderModel.TransformBounds(patch.model.bounds,
                        Matrix4x4.TRS(position + offset, Quaternion.Euler(0,sample.rotation,0),Vector3.one*sample.scale));
                    if (!bounds.IntersectRay(ray, out float distance) || distance >= best) continue;
                    best = distance; found = true;
                    result = new PickResult { scatter = true, id = patch.sourceId, type = patch.type, bounds = bounds, distance = distance };
                }
            }
            if (chunk.offset != offset || chunk.batches.Count == 0) continue;
            bool near = false;
            foreach (var batch in chunk.batches)
                if (batch.bounds.IntersectRay(ray, out float d) && d <= best) { near = true; break; }
            if (!near) continue;
            for (int i = 0; i < chunk.points.Length; i++)
            {
                var p = chunk.points[i];
                if (!p.visible || (PropRenderPolicy.NearOnly(p.type) && p.lod == DrawLod.Hidden)) continue;
                var model = Model(p.type);
                if (model.normal.Count == 0) continue;
                var bounds = PropRenderModel.TransformBounds(model.bounds,
                    Matrix4x4.TRS(p.position + offset, Quaternion.Euler(0, p.rotation, 0), Vector3.one * p.scale));
                if (!bounds.IntersectRay(ray, out float distance) || distance >= best) continue;
                best = distance;
                found = true;
                result = new PickResult { scatter = !p.explicitInstance, id = p.sourceId, type = p.type, bounds = bounds, distance = distance };
            }
        }
        return found;
    }

    /// <summary>範囲配置レコードの散布範囲(常駐セル内で参照しているもの)を返す。ハイライト描画用で、保存座標系。</summary>
    public bool TryGetScatterRegions(int scatterId, out List<PropGeometry.Region> regions)
    {
        regions = null;
        foreach (var chunk in chunks.Values)
            foreach (var scatter in chunk.spec.scatters)
                if (scatter.data.prop_scatter_id == scatterId) { regions = scatter.regions; return true; }
        return false;
    }

    /// <summary>
    /// 削除が確定したレコードの候補を即座に非表示にする。差分リロード(候補の再生成)が完了するまでの
    /// 間も撤去済みに見せるための暫定処理で、常駐データの正はリロード後のスナップショットである。
    /// </summary>
    public void HideSource(bool scatter, int id)
    {
        foreach (var chunk in chunks.Values)
        {
            if (scatter) chunk.hiddenScatters.Add(id);
            if (scatter) foreach (var patch in chunk.forests) if (patch.sourceId == id) patch.hidden = true;
            bool changed = false;
            for (int i = 0; i < chunk.points.Length; i++)
            {
                if (chunk.points[i].explicitInstance == scatter || chunk.points[i].sourceId != id || !chunk.points[i].visible) continue;
                chunk.points[i].visible = false;
                changed = true;
            }
            if (changed) { chunk.dirty = true; chunk.revision++; }
        }
    }

    private bool HasPendingDrawWork
    {
        get
        {
            if (!visible) return false;
            foreach (var chunk in chunks.Values)
            {
                if (chunk.dirty || chunk.heightDirty || chunk.offset != Offset) return true;
                foreach (var patch in chunk.forests) if (patch.dirty) return true;
            }
            return false;
        }
    }

    private IEnumerable<Candidate> ScatterCandidates(Spec spec, bool detailOnly, HashSet<int> hidden = null)
    {
        foreach (var scatter in spec.scatters)
        {
            if (PropRenderPolicy.NearOnly(scatter.data.prop_type) != detailOnly || IsGpuTree(scatter.data.prop_type)
                || (hidden != null && hidden.Contains(scatter.data.prop_scatter_id))) continue;
            if (Model(scatter.data.prop_type).normal.Count == 0) continue;
            foreach (var point in PropGeometry.Scatter(spec.rect, scatter.regions, scatter.data.density, scatter.data.seed, scatter.data.edge_fade_width))
                yield return new Candidate { type = scatter.data.prop_type, sourceId = scatter.data.prop_scatter_id,
                    position = new Vector3(point.position.x, 0, point.position.y), rotation = point.rotation,
                    scale = point.scale, lod = DrawLod.Far, allowWater = scatter.data.allow_water,
                    rank = StableSample<Candidate>.Rank(point.position.x, point.position.y, scatter.data.seed) };
        }
    }

    private IEnumerator RefreshDetail(Chunk chunk, Camera cam)
    {
        bool wanted = ChunkDistance(chunk, cam.transform.position) <= PropRenderPolicy.DetailResidentDistance
            && chunk.spec.scatters.Exists(s => PropRenderPolicy.NearOnly(s.data.prop_type));
        if (wanted == chunk.detailResident) yield break;
        int serial = generation, revision = chunk.revision;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var sampled = new StableSample<Candidate>(PropRenderPolicy.DetailQuota(ChunkSize));
        if (wanted)
        {
            int visited = 0;
            foreach (var p in ScatterCandidates(chunk.spec, true, chunk.hiddenScatters))
            {
                sampled.Add(p.rank, p);
                if ((++visited & 127) == 0 && timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs)
                {
                    yield return RenderWorkScheduler.Continue; timer.Restart();
                    if (serial != generation || revision != chunk.revision) yield break;
                }
            }
        }
        if (serial != generation || revision != chunk.revision || !chunks.TryGetValue(chunk.spec.key, out var live) || live != chunk) yield break;
        var detail = sampled.ToArray();
        for (int i = 0; i < detail.Length; i++)
        {
            var p = detail[i];
            p.position.y = Height(p.position);
            p.visible = !Blocked(new Vector2(p.position.x, p.position.z), Model(p.type).Clearance(p.scale), chunk.spec.obstacles, p.allowWater);
            detail[i] = p;
            if ((i & 127) == 0 && timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs)
            {
                yield return RenderWorkScheduler.Continue; timer.Restart();
                if (serial != generation || revision != chunk.revision) yield break;
            }
        }
        if (!chunks.TryGetValue(chunk.spec.key, out live) || live != chunk) yield break;
        var combined = new Candidate[chunk.basePoints.Length + detail.Length];
        Array.Copy(chunk.points, combined, chunk.basePoints.Length);
        Array.Copy(detail, 0, combined, chunk.basePoints.Length, detail.Length);
        candidateCount += combined.Length - chunk.points.Length;
        chunk.points = combined; chunk.detailResident = wanted; chunk.dirty = true; chunk.revision++;
    }

    private IEnumerator DrawWorker()
    {
        while (true)
        {
            yield return null;
            var cam = Camera.main;
            if (!visible || cam == null) continue;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var keys = new List<Vector2Int>(chunks.Keys);
            foreach (var key in keys)
            {
                if (!chunks.TryGetValue(key, out var chunk)) continue;
                yield return RefreshDetail(chunk, cam);
                if (!chunks.TryGetValue(key, out var live) || live != chunk) continue;
                foreach (var patch in chunk.forests)
                {
                    if (!patch.dirty && !chunk.heightDirty && chunk.offset == Offset) continue;
                    if (terrainManager == null) terrainManager = FindAnyObjectByType<TerrainManager>();
                    if (terrainManager != null && terrainManager.LoadedTerrainTiles.TryGetValue(chunk.spec.cell, out var terrain))
                        forestRenderer.Generate(patch, terrain, Offset);
                }
                if (chunk.points.Length == 0)
                {
                    chunk.batches.Clear(); chunk.heightDirty = false; chunk.dirty = false; chunk.offset = Offset;
                    if (timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs)
                    { yield return RenderWorkScheduler.Continue; timer.Restart(); }
                    continue;
                }
                var pos = cam.transform.position;
                float projection = DrawLodEvaluator.GetTanHalfFov(cam);
                bool far = !nearCells.Contains(chunk.spec.cell);
                bool moved = (pos - chunk.lastCamera).sqrMagnitude >= DrawLodConfig.REEVALUATE_CAMERA_MOVE_DISTANCE * DrawLodConfig.REEVALUATE_CAMERA_MOVE_DISTANCE;
                // 全点が距離で非表示の小区画は、進入閾値の内側へ戻るまでカメラ移動での再構築を省く。
                bool beyond = ChunkDistance(chunk, pos) >= DrawLodConfig.PROP_HIDDEN_ENTER_DISTANCE;
                if (chunk.hidden && beyond && !chunk.dirty && !chunk.heightDirty && chunk.offset == Offset) continue;
                if (chunk.dirty || chunk.heightDirty || chunk.offset != Offset || chunk.far != far || projection != chunk.lastProjection || moved)
                {
                    if (chunk.heightDirty)
                    {
                        for (int i = 0; i < chunk.points.Length; i++)
                        {
                            chunk.points[i].position.y = Height(chunk.points[i].position) + chunk.points[i].heightOffset;
                            if ((i & 127) == 0 && timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs)
                            { yield return RenderWorkScheduler.Continue; timer.Restart(); }
                        }
                        chunk.heightDirty = false; chunk.revision++;
                    }
                    if (!chunks.TryGetValue(key, out var current) || current != chunk) continue;
                    yield return RebuildBatches(chunk, cam, far, projection);
                }
                if (timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs) { yield return RenderWorkScheduler.Continue; timer.Restart(); }
            }
            for (int i = 1; i < DrawLodConfig.REEVALUATE_INTERVAL_FRAMES; i++) yield return null;
        }
    }
    private IEnumerator RebuildBatches(Chunk chunk, Camera cam, bool far, float projection)
    {
        int serial = generation;
        int revision = chunk.revision;
        Vector3 offset = Offset;
        Vector3 cameraPosition = cam.transform.position;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var next = new List<DrawBatch>();
        var grouped = new Dictionary<(int, DrawLod, bool), List<Matrix4x4>>();
        // 小区画の矩形までのXZ距離は各点の実距離の下限なので、進入閾値以上なら個体判定を省いて全点を隠せる。
        bool beyond = ChunkDistance(chunk, cameraPosition) >= DrawLodConfig.PROP_HIDDEN_ENTER_DISTANCE;
        int hiddenCount = 0;
        for (int i = 0; i < chunk.points.Length; i++)
        {
            if ((i & 127) == 0 && timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs)
            { yield return RenderWorkScheduler.Continue; timer.Restart(); if (serial != generation) yield break; }
            var p = chunk.points[i]; if (!p.visible) continue;
            if (beyond) { p.lod = DrawLod.Hidden; p.distant = false; chunk.points[i] = p; hiddenCount++; continue; }
            var model = Model(p.type);
            float distance = Vector3.Distance(p.position + offset + model.bounds.center * p.scale, cameraPosition);
            if (PropRenderPolicy.Hidden(p.type, distance, p.lod))
            { p.lod = DrawLod.Hidden; p.distant = false; chunk.points[i] = p; hiddenCount++; continue; }
            float ratio = DrawLodEvaluator.ProjectedRatio(model.Radius * p.scale, cam.orthographic ? 1 : distance, projection);
            var lod = PropRenderPolicy.NearOnly(p.type) ? DrawLod.Close
                : DrawLodEvaluator.EvaluateBySize(model.Radius * p.scale, distance, projection, p.lod, true, cam.orthographic);
            // 常駐範囲では本数を維持し、画面高の1%未満は四角錐へ置き換える。
            // 戻りを1.2%にして、境界付近の往復でモデルがちらつくのを抑える。
            p.distant = lod == DrawLod.Far && model.distant.Count > 0 && ratio < (p.distant ? .012f : .01f);
            p.lod = lod; chunk.points[i] = p;
            var group = (p.type, lod, p.distant);
            if (!grouped.TryGetValue(group, out var matrices)) grouped[group] = matrices = new List<Matrix4x4>();
            matrices.Add(Matrix4x4.TRS(p.position + offset, Quaternion.Euler(0,p.rotation,0), Vector3.one * p.scale));
        }
        foreach (var group in grouped)
        {
            var model = Model(group.Key.Item1);
            var parts = group.Key.Item3 ? model.distant : group.Key.Item2 == DrawLod.Far ? model.far : group.Key.Item2 == DrawLod.Close ? model.close : model.normal;
            foreach (var part in parts)
                for (int start = 0; start < group.Value.Count; start += BatchSize)
                {
                    int count = Mathf.Min(BatchSize, group.Value.Count - start);
                    var matrices = new Matrix4x4[count];
                    var bounds = new Bounds();
                    for (int i = 0; i < count; i++)
                    {
                        matrices[i] = group.Value[start+i] * part.local;
                        var b = PropRenderModel.TransformBounds(part.mesh.bounds, matrices[i]);
                        if (i == 0) bounds = b; else bounds.Encapsulate(b);
                    }
                    next.Add(new DrawBatch { part = part, matrices = matrices, bounds = bounds, shadows = !PropRenderPolicy.NearOnly(group.Key.Item1) && group.Key.Item2 != DrawLod.Far && Vector3.Distance(bounds.center, cameraPosition) < 150f });
                    if (timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs)
                    { yield return RenderWorkScheduler.Continue; timer.Restart(); if (serial != generation) yield break; }
                }
        }
        if (serial == generation && revision == chunk.revision && offset == Offset && chunks.TryGetValue(chunk.spec.key, out var live) && live == chunk)
        {
            chunk.batches.Clear(); chunk.batches.AddRange(next);
            chunk.dirty = false; chunk.offset = offset; chunk.far = far;
            chunk.hidden = beyond; chunk.hiddenCount = hiddenCount;
            chunk.lastCamera = cameraPosition; chunk.lastProjection = projection;
        }
    }
    /// <summary>ワールド座標のカメラ位置から小区画の矩形(保存座標)までのXZ距離。矩形内なら0。</summary>
    private float ChunkDistance(Chunk chunk, Vector3 worldCamera)
    {
        Vector3 c = worldCamera - Offset;
        var rect = chunk.spec.rect;
        float dx = Mathf.Max(rect.xMin - c.x, 0f, c.x - rect.xMax);
        float dz = Mathf.Max(rect.yMin - c.z, 0f, c.z - rect.yMax);
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
    private void TerrainReady(Vector2Int cell, Terrain terrain)
    {
        if (!chunksByCell.TryGetValue(cell, out var keys)) return;
        foreach (var key in keys) if (chunks.TryGetValue(key, out var chunk)) { chunk.heightDirty = true; chunk.revision++; }
        PreviewRevision++;
    }
    public List<Rect> PreviewRects(Rect bounds)
    {
        var result = new List<Rect>();
        foreach (var cell in obstacleReadyCells)
            for (int x = 0; x < subdivisions; x++) for (int z = 0; z < subdivisions; z++)
            {
                var rect = ChunkRect(cell * subdivisions + new Vector2Int(x,z));
                if (rect.Overlaps(bounds)) result.Add(rect);
            }
        var cam = Camera.main;
        Vector3 position = cam != null ? cam.transform.position - Offset : Vector3.zero;
        result.Sort((a,b) => (a.center - new Vector2(position.x,position.z)).sqrMagnitude.CompareTo((b.center - new Vector2(position.x,position.z)).sqrMagnitude));
        return result;
    }
    private void LateUpdate()
    {
        SubmittedBatchCount = 0; SubmittedInstanceCount = 0; SubmittedShadowInstances = 0;
        SubmittedForestBatches = 0;
        if (!visible) return;
        var cam = Camera.main; if (cam == null) return;
        if (forestOverview != null && forestOverview.Ready)
        {
            forestReadyRects.Clear();
            foreach (var chunk in chunks.Values)
            {
                bool ready = chunk.forests.Count > 0;
                foreach (var patch in chunk.forests) ready &= patch.ready && !patch.hidden;
                if (ready) forestReadyRects.Add(chunk.spec.rect);
            }
            forestOverview.UpdateDetail(forestReadyRects, ChunkSize);
        }
        GeometryUtility.CalculateFrustumPlanes(cam, planes);
        foreach (var chunk in chunks.Values) foreach (var batch in chunk.batches)
        {
            if (chunk.offset != Offset || !GeometryUtility.TestPlanesAABB(planes, batch.bounds)) continue;
            bool shadows = batch.shadows && SubmittedShadowInstances + batch.matrices.Length <= ShadowInstanceBudget;
            if (shadows) SubmittedShadowInstances += batch.matrices.Length;
            var parameters = new RenderParams(batch.part.material) { worldBounds = batch.bounds, camera = cam,
                shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off,
                receiveShadows = true };
            Graphics.RenderMeshInstanced(parameters, batch.part.mesh, batch.part.submesh, batch.matrices);
            SubmittedBatchCount++; SubmittedInstanceCount += batch.matrices.Length;
        }
        int shadowBudget = ShadowInstanceBudget - SubmittedShadowInstances;
        foreach (var chunk in chunks.Values) foreach (var patch in chunk.forests)
            SubmittedForestBatches += forestRenderer.Draw(patch, cam, planes, ref shadowBudget);
        SubmittedShadowInstances = ShadowInstanceBudget - shadowBudget;
    }
    public IEnumerator AdjustPropHeightsForCellsCoroutine(List<Vector2Int> cells)
    {
        Utils.InvalidateHeightCache();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        foreach (var cell in cells)
        {
            if (!chunksByCell.TryGetValue(cell, out var keys)) continue;
            foreach (var key in new List<Vector2Int>(keys))
            {
                if (!chunks.TryGetValue(key, out var chunk)) continue;
                foreach (var patch in chunk.forests) patch.dirty = true;
                for (int i = 0; i < chunk.points.Length; i++)
                {
                    chunk.points[i].position.y = Height(chunk.points[i].position) + chunk.points[i].heightOffset; chunk.dirty = true; chunk.revision++;
                    if ((i & 127) == 0 && timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs)
                    { yield return RenderWorkScheduler.Continue; timer.Restart(); }
                }
            }
        }
    }
    private void RemoveChunk(Vector2Int key)
    {
        if (!chunks.TryGetValue(key, out var chunk)) return;
        foreach (var patch in chunk.forests) patch.Dispose();
        candidateCount -= chunk.points.Length; chunks.Remove(key);
        if (chunksByCell.TryGetValue(chunk.spec.cell, out var keys))
        { keys.Remove(key); if (keys.Count == 0) chunksByCell.Remove(chunk.spec.cell); }
    }
    protected override void UnloadCell(Vector2Int cell)
    {
        if (chunksByCell.TryGetValue(cell, out var keys)) foreach (var key in new List<Vector2Int>(keys)) RemoveChunk(key);
        loadedCells.Remove(cell); obstacleReadyCells.Remove(cell);
    }
    public override void ClearAllCells()
    {
        InvalidateForestOverview();
        overviewCancel?.Dispose(); overviewCancel = null;
        forestOverview?.Dispose(); forestOverview = null;
        generation++;
        if (loading != null) RenderWorkScheduler.Stop(this, loading); loading = null;
        foreach (var chunk in chunks.Values) foreach (var patch in chunk.forests) patch.Dispose();
        chunks.Clear(); chunksByCell.Clear(); loadedCells.Clear(); nearCells.Clear();
        obstacleGrid.Clear(); obstacleReadyCells.Clear(); candidateCount = 0; PreviewRevision++;
        foreach (var model in models.Values) model.Dispose(); models.Clear();
        forestRenderer?.Dispose(); forestRenderer = null;
    }
}
