using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;

/// <summary>
/// ReclaimedLandManager:
/// 埋立地の岸壁(帯を覆う天端と、外周の垂直の面)を描く常設マネージャ。
///
/// <see cref="TerrainManager.FetchCoastline"/> が海岸線・湖・川の後に <see cref="LoadAndBuild"/> を呼ぶ
/// (初回ロード・強制リロード・毎回の session:reload)。埋立の完了で海岸線と埋立地が変わるため毎回取り直すが、
/// <see cref="ReclaimedLandStore"/> の中身が変わらなければ何も作り直さない。
///
/// 埋立地1件につき GameObject 1つ(天端と面を1メッシュ)。背景地形のチャンクと同じく Ground レイヤーと
/// MeshCollider・VisualTerrainObject・TerrainInputHandler を持ち、地形としてクリック・表示切替される。
/// 面の下端だけが地形に依存するので、地形タイルの版(<see cref="TerrainManager.GetTerrainSignature(Rect)"/>)が
/// 変わった埋立地を作り直しワーカーで作り直す。
///
/// シーンに事前配置する必要はなく、AfterSceneLoad で自己生成する。
/// </summary>
public class ReclaimedLandManager : MonoBehaviour
{
    public static ReclaimedLandManager Instance { get; private set; }

    /// <summary>
    /// ストアの中身(<see cref="ReclaimedLandStore.ContentVersion"/>)が変わった後に呼ばれる。
    /// 岸壁に沿って描くもの(港など)は、表示中のものをここで作り直す。
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
    // 入力判定(InputHandler)が岸壁を地形として扱うための所属表。
    private readonly HashSet<GameObject> quayObjects = new HashSet<GameObject>();
    private readonly Queue<int> rebuildQueue = new Queue<int>();
    private readonly HashSet<int> rebuildQueued = new HashSet<int>();
    private Coroutine rebuildCoroutine;
    private TerrainManager subscribedTerrainManager;
    private Material quayMaterial;
    // Clear のたびに進め、取得中に消去を挟んだ応答を古い都市のものとして捨てる。
    private int loadGeneration;

    private static readonly Func<float, float, float> SampleGround =
        (x, z) => Utils.GetGroundHeight(new Vector3(x, 0f, z));

    /// <summary>シーンに未配置でも動作するよう、シーンロード後に自己生成する。</summary>
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
    /// バックエンドから埋立地を取得し、中身が変わった埋立地だけ作り直す。
    /// 取得の前に消さず、新しいメッシュを作ってから古いものを捨てる(session:reload のたびにちらつかせない)。
    /// 取得に失敗したら前回の表示とストアをそのまま残す。
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

        // 中身が変わった埋立地と、前回の作成が中断されて岸壁の無い埋立地だけを作る。
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

    // 購読側の例外で岸壁の作成を止めない。
    private static void NotifyContentChanged()
    {
        try { OnContentChanged?.Invoke(); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>
    /// すべての岸壁とストアを破棄する。単一シーン内でセッションを切り替えるため、
    /// 前の都市の岸壁と接地の天端を残さない(CellDataLoader.ClearAllManagerData から呼ぶ)。
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

    /// <summary>埋立地の岸壁の GameObject か。InputHandler が地形(TERRAIN)として扱う判定に使う。</summary>
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
    /// タイルが届いたセルに掛かる埋立地のうち、地形の署名が作成時と違うものを作り直し待ちに積む。
    /// ハンドラの中では作らない(タイル到着の連続でスパイクを起こさないため)。
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

    // タイルの無いセルは全景の高さで面の下端を決めるので、全景が届いた後もここで署名を確かめる。
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
                // 積んだ後に同じ版で作り直し済みなら省く。
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
    /// 埋立地1件のメッシュを今の地面で作り、既存の GameObject のメッシュと差し替える(無ければ作る)。
    /// 天端が無い(岸壁が無い)埋立地には何も描かない。
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
    /// 背景地形のチャンクと同じ構成にする。VisualTerrainObject は Awake で MeshRenderer を掴むのでレンダラーより後、
    /// タグは付けない(SeaObject を付けると湖の水面がこの素材を借りうる)。
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

    // 地形(+1,+1)より手前に描く共有の DepthOffsetLit 版。帯の内縁で天端と地形が同じ高さになっても Z-fighting しない。
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

    // 面の下端は外周の外側 QUAY_PROBE_DISTANCE の地面を読むので、外接矩形をその分より広げた範囲の版を見る。
    private static Rect TerrainArea(ReclaimedLandRecord land)
    {
        float margin = Constants.QUAY_PROBE_DISTANCE + 1f;
        Rect b = land.Bounds;
        return Rect.MinMaxRect(b.xMin - margin, b.yMin - margin, b.xMax + margin, b.yMax + margin);
    }

    // TerrainManager が無いときは版が不明なので false(呼び出し側は作り直しを省かない)。
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

    // === API レスポンスの変換 ===

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

    /// <summary>閉じ点と連続する重複点を除く。3点未満なら null。</summary>
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
        public JToken band; // [[外周, 穴...], ...](GeoJSON MultiPolygon の coordinates)。入れ子が深いので生の JToken で受ける
    }

    [Serializable]
    private class SegmentDto
    {
        public float[] a;
        public float[] b;
        public float[] normal;
    }
}
