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
        public bool shadowEligible; // 影を落とす種別・LODか。影の距離判定だけをカメラ移動で更新するために保持する
    }
    private sealed class Chunk
    {
        public Spec spec;
        public Candidate[] points;
        public Candidate[] basePoints;
        public int offered;      // 通常候補の抽出に投入した総数。予算変更時に切り詰めで済むかを判定する
        public long terrainStamp = -1; // 点の高さを取った時点の地形の版(TerrainStamp)。-1 は不明
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
    // RebuildBatches の作業領域。呼び出し元は単一の DrawWorker だけで逐次実行なので共有する。
    private readonly Dictionary<(int, DrawLod, bool), List<Matrix4x4>> groupScratch = new Dictionary<(int, DrawLod, bool), List<Matrix4x4>>();
    private readonly List<(int, DrawLod, bool)> groupKeys = new List<(int, DrawLod, bool)>();
    private readonly List<DrawBatch> batchScratch = new List<DrawBatch>();
    private readonly List<Vector2Int> drawKeys = new List<Vector2Int>();
    private Vector3 lastPassCamera = Vector3.positiveInfinity, lastPassOffset;
    private float lastPassProjection;
    private bool lastPassOrtho;
    // compute dispatch を1フレームに集中させないための、1フレームあたりの GPU 森林の生成点数上限。
    private const int ForestDispatchBudget = 262144;
    private int dispatchFrame = -1, dispatched;
    // 森林パッチの準備・非表示・増減のたびに進める。詳細マスクは版が変わったフレームだけ作り直す。
    private int forestStateVersion, appliedForestStateVersion = -1;
    private Coroutine loading, worker;
    private Transform anchor;
    private bool visible = true;
    private int generation, candidateCount;
    private int activeLoads;
    private float previewDemandUntil = float.NegativeInfinity;
    private const float PreviewDemandHoldSeconds = 2f;
    /// <summary>散布プレビューが直近に障害物を必要としたか。要求が無い間はプロップの無い範囲の障害物を取得しない。</summary>
    private bool PreviewDemanded => Time.unscaledTime < previewDemandUntil;
    private int previewRevision;
    /// <summary>
    /// 障害物・地形の変化でプレビューの作り直しが要るたびに進む版。
    /// 散布配置モードの間は PropPlacementManager が毎フレーム読むので、読み取りを散布プレビューの生存確認として需要を延長する。
    /// </summary>
    public int PreviewRevision { get { ExtendPreviewDemand(); return previewRevision; } }
    private void ExtendPreviewDemand() => previewDemandUntil = Time.unscaledTime + PreviewDemandHoldSeconds;
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
            appliedForestStateVersion = -1;
            overviewDirty = false;
        }
        overviewLoading = null;
    }

    private IEnumerator Load(List<Vector2Int> cells, bool force, int serial, LoadBatch batch)
    {
        // 停止時も iterator の Dispose で finally が走るので、実行中の本数を数えられる。
        activeLoads++;
        try
        {
            if (cells.Count == 0) yield break;
            CalculateMergedBBox(cells, out float x0, out float z0, out float x1, out float z1);
            // 障害物は既存プロップの除外と散布プレビューにしか使わない。プロップが1件も無く
            // プレビューも要求されていなければ、全区画・道路のポリゴン取得を省く。
            if (!PreviewDemanded)
            {
                PropListResponse probe = null;
                yield return ApiClient.GetBulk<PropListResponse>(FormattableString.Invariant($"/props?bbox={x0},{z0},{x1},{z1}&limit=1"),
                    data => probe = data, error => Debug.LogError($"PropManager: {error}"), "/props?limit=1", isCanceled: () => Canceled(serial, batch));
                if (Canceled(serial, batch) || probe == null) yield break;
                if ((probe.scatters == null || probe.scatters.Count == 0) && (probe.instances == null || probe.instances.Count == 0))
                {
                    ApplyEmpty();
                    yield break;
                }
            }
            PropListResponse response = null;
            yield return ApiClient.GetBulk<PropListResponse>(FormattableString.Invariant($"/props?bbox={x0},{z0},{x1},{z1}&include_obstacles=true"),
                data => response = data, error => Debug.LogError($"PropManager: {error}"), isCanceled: () => Canceled(serial, batch));
            if (Canceled(serial, batch) || response == null || response.obstacles == null) yield break;
            yield return ApplySnapshot(response, cells, force, serial, batch);
            loading = null;
        }
        finally { activeLoads--; }
    }

    /// <summary>常駐範囲にプロップが無いときの適用。障害物は未取得なので、どのセルもプレビュー不可のままにする。</summary>
    private void ApplyEmpty()
    {
        LastRegeneratedChunks = 0; LastExclusionUpdatedChunks = 0;
        foreach (var key in new List<Vector2Int>(chunks.Keys)) RemoveChunk(key);
        obstacleGrid.Clear(); obstacleReadyCells.Clear(); previewRevision++;
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
        obstacleReadyCells.Clear(); obstacleReadyCells.UnionWith(cells); previewRevision++;
        var ordered = new Spec[specs.Count];
        specs.Values.CopyTo(ordered, 0);
        Vector3 camera = Camera.main != null ? Camera.main.transform.position - Offset : Vector3.zero;
        var cameraXZ = new Vector2(camera.x, camera.z);
        var distances = new float[ordered.Length];
        for (int i = 0; i < ordered.Length; i++) distances[i] = (ordered[i].rect.center - cameraXZ).sqrMagnitude;
        Array.Sort(distances, ordered);
        var keep = new HashSet<Vector2Int>();
        int activeSpecs = 0;
        foreach (var spec in ordered)
            if (spec.scatters.Exists(s => !PropRenderPolicy.NearOnly(s.data.prop_type)) || spec.instances.Count > 0) activeSpecs++;
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
            // 候補予算は常駐小区画数で変わるので署名に入れない。GPU森林は予算で決まる実効本数だけを入れる。
            spec.sourceSignature = GpuForests + ":" + string.Join(";", spec.scatters.ConvertAll(s => IsGpuTree(s.data.prop_type)
                    ? $"s{s.data.prop_scatter_id}:{s.data.version}:f{ForestRenderer.PatchCount(spec.rect, s.data.density, forestQuota)}"
                    : $"s{s.data.prop_scatter_id}:{s.data.version}")) + "|" +
                string.Join(";", spec.instances.ConvertAll(i => $"i{i.prop_instance_id}:{i.version}"));
            spec.obstacleSignature = string.Join(";", spec.obstacles.ConvertAll(o => o.data.key + ":" + o.data.signature));
            chunks.TryGetValue(spec.key, out var previous);
            bool exclusionChanged = previous == null || previous.spec.obstacleSignature != spec.obstacleSignature;
            Candidate[] points = null;
            int offered = 0;
            if (previous != null && !force && previous.spec.sourceSignature == spec.sourceSignature)
            {
                // StableSample は順位の昇順で返すので、容量を減らした結果は現在の配列の先頭と一致する。
                int have = previous.basePoints.Length, want = Math.Min(previous.offered, quota);
                offered = previous.offered;
                if (want == have || (want > have && want < have * 5 / 4)) points = previous.basePoints;
                else if (want < have)
                {
                    var trimmed = new Candidate[want];
                    Array.Copy(previous.points, trimmed, want);
                    if (!exclusionChanged)
                    {
                        Trim(previous, trimmed);
                        keep.Add(spec.key);
                        if (timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs) { yield return RenderWorkScheduler.Continue; timer.Restart(); }
                        continue;
                    }
                    points = trimmed;
                }
            }
            if (points == null)
            {
                var generated = new StableSample<Candidate>(quota);
                int visited = 0;
                offered = 0;
                foreach (var candidate in ScatterCandidates(spec, false))
                {
                    generated.Add(candidate.rank | (1UL << 63), candidate);
                    offered++;
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
                    offered++;
                }
                points = generated.ToArray();
            }
            keep.Add(spec.key);
            bool sourceChanged = previous == null || !ReferenceEquals(points, previous.basePoints);
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
                long terrainStamp = TerrainStamp(spec.cell);
                for (int i = 0; i < points.Length; i++)
                {
                    points[i].position.y = Height(points[i].position) + points[i].heightOffset;
                    if ((i & 127) == 0 && timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs)
                    { yield return RenderWorkScheduler.Continue; timer.Restart(); if (Canceled(serial, batch)) yield break; }
                }
                var previousBatches = previous != null ? new List<DrawBatch>(previous.batches) : null;
                RemoveChunk(spec.key);
                // 高さを取っている途中に地形が届いた場合は、その通知が旧小区画に当たって失われるので、ここで追従を予約する。
                var chunk = new Chunk { spec = spec, points = points, basePoints = points, offered = offered,
                    terrainStamp = terrainStamp, heightDirty = TerrainStamp(spec.cell) != terrainStamp };
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
                if (chunk.forests.Count > 0) forestStateVersion++;
                if (!chunksByCell.TryGetValue(spec.cell, out var keys)) chunksByCell[spec.cell] = keys = new HashSet<Vector2Int>();
                keys.Add(spec.key);
            }
            if (timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs) { yield return RenderWorkScheduler.Continue; timer.Restart(); }
        }
        foreach (var key in new List<Vector2Int>(chunks.Keys)) if (!keep.Contains(key)) RemoveChunk(key);
        loading = null;
    }

    /// <summary>
    /// 候補予算の縮小を既存の小区画へ反映する。points の先頭は basePoints と同じ並びで、高さ・表示可否は
    /// points 側が最新なので、そこから切り出す。近景散布は basePoints の後ろに連結されているので残す。
    /// </summary>
    private void Trim(Chunk chunk, Candidate[] trimmed)
    {
        int have = chunk.basePoints.Length, detail = chunk.points.Length - have;
        var points = trimmed;
        if (detail > 0)
        {
            points = new Candidate[trimmed.Length + detail];
            Array.Copy(trimmed, points, trimmed.Length);
            Array.Copy(chunk.points, have, points, trimmed.Length, detail);
        }
        candidateCount += points.Length - chunk.points.Length;
        chunk.points = points; chunk.basePoints = trimmed;
        chunk.dirty = true; chunk.revision++;
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
    private float Height(Vector3 saved) => Utils.GetGroundHeight(saved + Offset);
    private Rect ChunkRect(Vector2Int key) => new Rect(key.x * ChunkSize, key.y * ChunkSize, ChunkSize, ChunkSize);
    private Vector2Int Key(Vector2 p) => new Vector2Int(Mathf.FloorToInt(p.x / ChunkSize), Mathf.FloorToInt(p.y / ChunkSize));
    private IEnumerable<Vector2Int> KeysIn(Rect bounds, Dictionary<Vector2Int, Spec> specs)
    {
        // 小さい範囲は重なるセルだけを引き、巨大な散布範囲では常駐セルだけを走査してマップ全域を列挙しない。
        float size = TerrainManager.CellSize;
        float sum = bounds.xMin + bounds.xMax + bounds.yMin + bounds.yMax;
        if (sum > float.NegativeInfinity && sum < float.PositiveInfinity)
        {
            int x0 = Mathf.FloorToInt(bounds.xMin / size), x1 = Mathf.FloorToInt(bounds.xMax / size);
            int z0 = Mathf.FloorToInt(bounds.yMin / size), z1 = Mathf.FloorToInt(bounds.yMax / size);
            long span = ((long)x1 - x0 + 1) * ((long)z1 - z0 + 1);
            if (span > 0 && span <= loadedCells.Count)
            {
                for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++)
                {
                    var cell = new Vector2Int(x, z);
                    if (!loadedCells.Contains(cell)) continue;
                    foreach (var key in KeysInCell(bounds, cell, specs)) yield return key;
                }
                yield break;
            }
        }
        foreach (var cell in loadedCells)
            foreach (var key in KeysInCell(bounds, cell, specs)) yield return key;
    }
    private IEnumerable<Vector2Int> KeysInCell(Rect bounds, Vector2Int cell, Dictionary<Vector2Int, Spec> specs)
    {
        var cellRect = new Rect(cell.x * TerrainManager.CellSize, cell.y * TerrainManager.CellSize, TerrainManager.CellSize, TerrainManager.CellSize);
        if (!cellRect.Overlaps(bounds)) yield break;
        var min = Key(Vector2.Max(bounds.min, cellRect.min));
        var max = Vector2Int.Min(Key(Vector2.Min(bounds.max, cellRect.max)), cell * subdivisions + Vector2Int.one * (subdivisions - 1));
        for (int x = min.x; x <= max.x; x++) for (int z = min.y; z <= max.y; z++)
        { var key = new Vector2Int(x,z); if (specs.ContainsKey(key)) yield return key; }
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
        var cell = TerrainManager.WorldToCell(saved);
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
                    var bounds = Utils.TransformBounds(patch.model.bounds,
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
                var bounds = Utils.TransformBounds(model.bounds,
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
            if (scatter)
                foreach (var patch in chunk.forests)
                    if (patch.sourceId == id && !patch.hidden) { patch.hidden = true; forestStateVersion++; }
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

    private bool DetailWanted(Chunk chunk, Vector3 worldCamera) =>
        ChunkDistance(chunk, worldCamera) <= PropRenderPolicy.DetailResidentDistance
        && chunk.spec.scatters.Exists(s => PropRenderPolicy.NearOnly(s.data.prop_type));

    private IEnumerator RefreshDetail(Chunk chunk, Camera cam)
    {
        bool wanted = DetailWanted(chunk, cam.transform.position);
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
            Vector3 passCamera = cam.transform.position, passOffset = Offset;
            float passProjection = DrawLodEvaluator.GetTanHalfFov(cam);
            bool passOrtho = cam.orthographic;
            // 前回の周回を完了した視点のままで、未処理の小区画も無ければ、巡回しても何も変わらない。
            bool idle = passCamera == lastPassCamera && passOffset == lastPassOffset && passProjection == lastPassProjection
                && passOrtho == lastPassOrtho && !HasPendingDrawWork;
            if (!idle)
            {
                // 世代が進むと途中の小区画が再構築を打ち切るので、その周回は完了扱いにしない。
                int passGeneration = generation;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                drawKeys.Clear(); drawKeys.AddRange(chunks.Keys);
                for (int k = 0; k < drawKeys.Count; k++)
                {
                    var key = drawKeys[k];
                    if (!chunks.TryGetValue(key, out var chunk)) continue;
                    if (DetailWanted(chunk, cam.transform.position) != chunk.detailResident) yield return RefreshDetail(chunk, cam);
                    if (!chunks.TryGetValue(key, out var live) || live != chunk) continue;
                    for (int f = 0; f < chunk.forests.Count; f++)
                    {
                        var patch = chunk.forests[f];
                        if (!patch.dirty && !chunk.heightDirty && chunk.offset == Offset) continue;
                        if (terrainManager == null) terrainManager = FindAnyObjectByType<TerrainManager>();
                        if (terrainManager == null || !terrainManager.LoadedTerrainTiles.TryGetValue(chunk.spec.cell, out var terrain)) continue;
                        // compute の CPU 側コストはほぼ無く時間予算では止まらないので、生成点数で1フレームの量を抑える。
                        if (dispatchFrame != Time.frameCount) { dispatchFrame = Time.frameCount; dispatched = 0; }
                        if (dispatched >= ForestDispatchBudget)
                        {
                            yield return null;
                            timer.Restart();
                            dispatchFrame = Time.frameCount; dispatched = 0;
                            if (!chunks.TryGetValue(key, out live) || live != chunk || patch.points == null) break;
                            if (terrainManager == null || !terrainManager.LoadedTerrainTiles.TryGetValue(chunk.spec.cell, out terrain)) continue;
                        }
                        if (forestRenderer.Generate(patch, terrain, Offset)) { dispatched += patch.count; forestStateVersion++; }
                    }
                    if (!chunks.TryGetValue(key, out live) || live != chunk) continue;
                    if (chunk.points.Length == 0)
                    {
                        if (chunk.heightDirty) chunk.terrainStamp = TerrainStamp(chunk.spec.cell);
                        chunk.batches.Clear(); chunk.heightDirty = false; chunk.dirty = false; chunk.offset = Offset;
                        if (timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs)
                        { yield return RenderWorkScheduler.Continue; timer.Restart(); }
                        continue;
                    }
                    var pos = cam.transform.position;
                    float projection = DrawLodEvaluator.GetTanHalfFov(cam);
                    bool far = !nearCells.Contains(chunk.spec.cell);
                    float chunkDistance = ChunkDistance(chunk, pos);
                    // 遠い小区画ほど同じ移動量での投影サイズの変化が小さいので、再評価の移動量を距離に比例させる。
                    float move = Mathf.Max(DrawLodConfig.REEVALUATE_CAMERA_MOVE_DISTANCE, chunkDistance * 0.02f);
                    bool moved = (pos - chunk.lastCamera).sqrMagnitude >= move * move;
                    // 全点が距離で非表示の小区画は、進入閾値の内側へ戻るまでカメラ移動での再構築を省く。
                    bool beyond = chunkDistance >= DrawLodConfig.PROP_HIDDEN_ENTER_DISTANCE;
                    if (chunk.hidden && beyond && !chunk.dirty && !chunk.heightDirty && chunk.offset == Offset) continue;
                    if (chunk.dirty || chunk.heightDirty || chunk.offset != Offset || chunk.far != far || projection != chunk.lastProjection || moved)
                    {
                        if (chunk.heightDirty)
                        {
                            long stamp = TerrainStamp(chunk.spec.cell);
                            var points = chunk.points;
                            for (int i = 0; i < points.Length; i++)
                            {
                                points[i].position.y = Height(points[i].position) + points[i].heightOffset;
                                if ((i & 127) == 0 && timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs)
                                { yield return RenderWorkScheduler.Continue; timer.Restart(); }
                            }
                            // 途中で配列の差し替えや地形の到着があれば、次の周回でやり直す。
                            if (ReferenceEquals(points, chunk.points) && TerrainStamp(chunk.spec.cell) == stamp)
                            { chunk.heightDirty = false; chunk.terrainStamp = stamp; }
                            chunk.dirty = true; chunk.revision++;
                        }
                        if (!chunks.TryGetValue(key, out var current) || current != chunk) continue;
                        yield return RebuildBatches(chunk, cam, far, projection);
                    }
                    if (timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs) { yield return RenderWorkScheduler.Continue; timer.Restart(); }
                }
                if (generation == passGeneration)
                { lastPassCamera = passCamera; lastPassOffset = passOffset; lastPassProjection = passProjection; lastPassOrtho = passOrtho; }
                else lastPassCamera = Vector3.positiveInfinity;
            }
            for (int i = 1; i < DrawLodConfig.REEVALUATE_INTERVAL_FRAMES; i++) yield return null;
        }
    }
    /// <summary>
    /// 点ごとの LOD を評価し、変化した小区画だけバッチを作り直す。行列は点の位置・向き・倍率と原点だけで決まるので、
    /// 点と原点が変わらず LOD も変わらなければ既存のバッチを使い、影の距離判定だけを更新する。
    /// </summary>
    private IEnumerator RebuildBatches(Chunk chunk, Camera cam, bool far, float projection)
    {
        int serial = generation;
        int revision = chunk.revision;
        Vector3 offset = Offset;
        Vector3 cameraPosition = cam.transform.position;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        bool structural = chunk.dirty || chunk.offset != offset;
        bool changed = false;
        // 小区画の矩形までのXZ距離は各点の実距離の下限なので、進入閾値以上なら個体判定を省いて全点を隠せる。
        bool beyond = ChunkDistance(chunk, cameraPosition) >= DrawLodConfig.PROP_HIDDEN_ENTER_DISTANCE;
        int hiddenCount = 0;
        // 途中で予算の切り詰めや近景散布の差し替えで配列が替わっても、別の点へLODを書き込まないよう開始時の配列を使う。
        var points = chunk.points;
        for (int i = 0; i < points.Length; i++)
        {
            if ((i & 127) == 0 && timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs)
            { yield return RenderWorkScheduler.Continue; timer.Restart(); if (serial != generation) yield break; }
            var p = points[i]; if (!p.visible) continue;
            var lod = DrawLod.Hidden;
            bool distant = false;
            if (!beyond)
            {
                var model = Model(p.type);
                float distance = Vector3.Distance(p.position + offset + model.bounds.center * p.scale, cameraPosition);
                if (!PropRenderPolicy.Hidden(p.type, distance, p.lod))
                {
                    float ratio = DrawLodEvaluator.ProjectedRatio(model.Radius * p.scale, cam.orthographic ? 1 : distance, projection);
                    lod = PropRenderPolicy.NearOnly(p.type) ? DrawLod.Close
                        : DrawLodEvaluator.EvaluateBySize(model.Radius * p.scale, distance, projection, p.lod, true, cam.orthographic);
                    // 常駐範囲では本数を維持し、画面高の1%未満は四角錐へ置き換える。
                    // 戻りを1.2%にして、境界付近の往復でモデルがちらつくのを抑える。
                    distant = lod == DrawLod.Far && model.distant.Count > 0 && ratio < (p.distant ? .012f : .01f);
                }
            }
            if (lod == DrawLod.Hidden) hiddenCount++;
            if (lod == p.lod && distant == p.distant) continue;
            // 書き戻した LOD と既存バッチが食い違うので、途中で打ち切られても次の周回で必ず作り直させる。
            if (!changed) { changed = true; chunk.dirty = true; }
            p.lod = lod; p.distant = distant; points[i] = p;
        }
        bool committable = serial == generation && revision == chunk.revision && offset == Offset && chunks.TryGetValue(chunk.spec.key, out var live) && live == chunk;
        if (!structural && !changed)
        {
            if (!committable) yield break;
            foreach (var batch in chunk.batches) batch.shadows = batch.shadowEligible && Vector3.Distance(batch.bounds.center, cameraPosition) < 150f;
            chunk.far = far; chunk.hidden = beyond; chunk.hiddenCount = hiddenCount;
            chunk.lastCamera = cameraPosition; chunk.lastProjection = projection;
            yield break;
        }
        foreach (var list in groupScratch.Values) list.Clear();
        groupKeys.Clear(); batchScratch.Clear();
        for (int i = 0; i < points.Length; i++)
        {
            if ((i & 127) == 0 && timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs)
            { yield return RenderWorkScheduler.Continue; timer.Restart(); if (serial != generation) yield break; }
            var p = points[i];
            if (!p.visible || p.lod == DrawLod.Hidden) continue;
            var group = (p.type, p.lod, p.distant);
            if (!groupScratch.TryGetValue(group, out var matrices)) groupScratch[group] = matrices = new List<Matrix4x4>();
            if (matrices.Count == 0) groupKeys.Add(group);
            matrices.Add(Matrix4x4.TRS(p.position + offset, Quaternion.Euler(0,p.rotation,0), Vector3.one * p.scale));
        }
        for (int g = 0; g < groupKeys.Count; g++)
        {
            var key = groupKeys[g];
            var transforms = groupScratch[key];
            var model = Model(key.Item1);
            var parts = key.Item3 ? model.distant : key.Item2 == DrawLod.Far ? model.far : key.Item2 == DrawLod.Close ? model.close : model.normal;
            bool shadowEligible = !PropRenderPolicy.NearOnly(key.Item1) && key.Item2 != DrawLod.Far;
            foreach (var part in parts)
                for (int start = 0; start < transforms.Count; start += BatchSize)
                {
                    int count = Mathf.Min(BatchSize, transforms.Count - start);
                    var matrices = new Matrix4x4[count];
                    var bounds = new Bounds();
                    for (int i = 0; i < count; i++)
                    {
                        matrices[i] = transforms[start+i] * part.local;
                        var b = Utils.TransformBounds(part.mesh.bounds, matrices[i]);
                        if (i == 0) bounds = b; else bounds.Encapsulate(b);
                    }
                    batchScratch.Add(new DrawBatch { part = part, matrices = matrices, bounds = bounds, shadowEligible = shadowEligible,
                        shadows = shadowEligible && Vector3.Distance(bounds.center, cameraPosition) < 150f });
                    if (timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs)
                    { yield return RenderWorkScheduler.Continue; timer.Restart(); if (serial != generation) yield break; }
                }
        }
        if (serial == generation && revision == chunk.revision && offset == Offset && chunks.TryGetValue(chunk.spec.key, out live) && live == chunk)
        {
            chunk.batches.Clear(); chunk.batches.AddRange(batchScratch);
            chunk.dirty = false; chunk.offset = offset; chunk.far = far;
            chunk.hidden = beyond; chunk.hiddenCount = hiddenCount;
            chunk.lastCamera = cameraPosition; chunk.lastProjection = projection;
        }
        batchScratch.Clear();
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
    /// <summary>
    /// セルに表示中の地形タイルの版と地形の世代を1つにした値。タイルが無い・版が不明なら -1。
    /// 同じ値なら同じ高さなので、LRU からの復元などで同じ版が再到着しても高さを取り直さない。
    /// </summary>
    private static long TerrainStamp(Vector2Int cell)
    {
        var manager = Utils.CachedTerrainManager;
        if (manager == null) return -1;
        int version = manager.GetAttachedTerrainVersion(cell);
        if (version < 0) return -1;
        return ((long)(uint)manager.TerrainEpoch << 32) | (uint)version;
    }
    private void TerrainReady(Vector2Int cell, Terrain terrain)
    {
        bool hasChunks = chunksByCell.TryGetValue(cell, out var keys);
        if (hasChunks)
        {
            long stamp = TerrainStamp(cell);
            foreach (var key in keys)
                if (chunks.TryGetValue(key, out var chunk) && (stamp < 0 || chunk.terrainStamp != stamp))
                { chunk.heightDirty = true; chunk.revision++; }
        }
        // プロップの無いセルでも、障害物を取得済みなら散布プレビューの高さを取り直す。
        if (hasChunks || obstacleReadyCells.Contains(cell)) previewRevision++;
    }
    public List<Rect> PreviewRects(Rect bounds)
    {
        ExtendPreviewDemand();
        EnsurePreviewObstacles();
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
    /// <summary>
    /// プロップの無い範囲では障害物を取得していないので、プレビュー要求時に常駐セル全体を障害物付きで取り直す。
    /// 取得が終わると PreviewRevision が進み、プレビューが作り直される。
    /// </summary>
    private void EnsurePreviewObstacles()
    {
        if (!visible || activeLoads > 0 || obstacleReadyCells.IsSupersetOf(loadedCells)) return;
        LoadCells(new List<Vector2Int>(loadedCells), false);
    }
    private void LateUpdate()
    {
        SubmittedBatchCount = 0; SubmittedInstanceCount = 0; SubmittedShadowInstances = 0;
        SubmittedForestBatches = 0;
        if (!visible) return;
        var cam = Camera.main; if (cam == null) return;
        if (forestOverview != null && forestOverview.Ready && forestStateVersion != appliedForestStateVersion)
        {
            appliedForestStateVersion = forestStateVersion;
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
        if (chunk.forests.Count > 0) forestStateVersion++;
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
        obstacleGrid.Clear(); obstacleReadyCells.Clear(); candidateCount = 0; previewRevision++;
        forestStateVersion++; lastPassCamera = Vector3.positiveInfinity;
        foreach (var model in models.Values) model.Dispose(); models.Clear();
        forestRenderer?.Dispose(); forestRenderer = null;
    }
}
