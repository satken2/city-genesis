using System.Collections;
using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>路上駐車のストック台数を、周辺道路の静止車両としてインスタンシング描画する。</summary>
public sealed class ParkedVehicleManager : MonoBehaviour
{
    private const float Radius = 480f;
    private const float CenterLead = 300f;   // 走行車(VehicleManager.maxCenterLead)と同じ管理中心にそろえる
    private const float ScanInterval = .5f;
    private readonly List<ParkedVehicleLayout.Model> models = new();
    private readonly List<PropRenderModel> renderModels = new();
    private readonly Dictionary<int, Road> roads = new();
    private List<Batch> batches = new();
    private PathManager paths;
    private Coroutine worker;
    private int session = -1;
    private float nextScan;
    private Vector3 lastCenter;
    private bool hasCenter;
    private Vector3 trackedCenter;
    private bool hasTrackedCenter;
    private bool forceRebatch;
    private bool modelsReady;

    private sealed class Road
    {
        public PathSummary data;
        public VehiclePolyline line;
        public long geometryRevision = -1;   // PathManager.GetPathGeometryRevision。-1 は版不明で点列比較に頼る
        public List<ParkedVehicleLayout.Placement> slots;
        public float width, sidewalk, divider;
        public int direction, hand, count;
    }

    private sealed class Batch
    {
        public PropRenderModel.Part part;
        public Matrix4x4[] matrices;
        public Bounds bounds;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindAnyObjectByType<ParkedVehicleManager>() == null)
            new GameObject("ParkedVehicleManager").AddComponent<ParkedVehicleManager>();
    }

    private void Start() => CityInfoStore.EnsureLoaded(this);

    private void Update()
    {
        int currentSession = TransitDataStore.Ensure().Session;
        if (session != currentSession) { Clear(); session = currentSession; }
        if (GameStateManager.Instance == null || !GameStateManager.Instance.IsGameStarted())
        { Clear(); return; }
        if (paths == null) paths = FindAnyObjectByType<PathManager>();
        if (paths == null || Camera.main == null || CameraCellChecker.Instance == null) return;
        Vector3 center = VehicleManager.ResolveTrafficCenter(CameraCellChecker.Instance, CenterLead);
        // 中心が大きく跳んだら走査をやり直す。配置候補は中心に依存しないので roads は残し、
        // 新しいバッチができるまで旧バッチを描き続ける。
        if (hasTrackedCenter && Utils.XZDistanceSquared(center, trackedCenter) > Radius * Radius)
        {
            if (worker != null) { RenderWorkScheduler.Stop(this, worker); worker = null; }
            nextScan = 0; forceRebatch = true;
        }
        trackedCenter = center; hasTrackedCenter = true;
        if (worker == null && Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + ScanInterval;
            // 0.5秒ごとに起動し直す常駐の走査なので、初期描画の完了判定を待たせない。
            worker = RenderWorkScheduler.Start(this, Refresh(center), "parkedVehicles", () => false);
        }
    }

    private void LateUpdate()
    {
        if (VisualModeHandler.Instance != null && VisualModeHandler.Instance.GetMode(InputObjectType.PATH) == VisualMode.MODE_HIDDEN) return;
        var camera = Camera.main;
        if (camera == null) return;
        foreach (var batch in batches)
        {
            var parameters = new RenderParams(batch.part.material) { camera = camera, worldBounds = batch.bounds,
                shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
            Graphics.RenderMeshInstanced(parameters, batch.part.mesh, batch.part.submesh, batch.matrices);
        }
    }

    public static bool Eligible(PathSummary path) => path != null && path.path_type == Constants.PATH_STREET
        && path.status == Constants.STATUS_ACTIVE && path.street_parking_capacity > 0 && path.parked_cars > 0;

    private IEnumerator Refresh(Vector3 center)
    {
        try
        {
            var candidates = paths.GetPathsNearCircle(center, Radius);
            candidates.RemoveAll(path => !Eligible(path) || ObjectVisibilityFilter.IsHidden(InputObjectType.PATH, path.path_id));
            if (candidates.Count > 0 && !modelsReady)
            {
                if (ModelManager.Instance == null) yield break;
                // 中断された前回の読み込みの残りを捨てる。models と renderModels は添字で対応させるので途中から足さない。
                foreach (var model in renderModels) model.Dispose();
                renderModels.Clear(); models.Clear();
                foreach (int type in new[] { Constants.VEHICLE_STANDARD, Constants.VEHICLE_COMMERCIAL, Constants.VEHICLE_TRUCK })
                {
                    var variants = VehicleModelConfig.GetVariants(type);
                    for (int i = 0; i < Mathf.Max(1, variants.Count); i++)
                    {
                        int variant = variants.Count > 0 ? variants[i] : 0;
                        // 接地原点・前方軸・LODの解決は走行車両と共通。個体GOは作らない。
                        var template = VehicleModelResolver.CreateInstance(type, variant, transform);
                        var model = new PropRenderModel(template);
                        Destroy(template);
                        renderModels.Add(model);
                        models.Add(new ParkedVehicleLayout.Model { type = type, variant = variant, size = model.bounds.size });
                        yield return RenderWorkScheduler.Continue;
                    }
                }
                modelsReady = true;
            }
            bool changed = forceRebatch || !hasCenter || Utils.XZDistanceSquared(center, lastCenter) > 1f;
            var seen = new HashSet<int>();
            foreach (var candidate in candidates)
            {
                // yield中に削除・再取得された道路は、必ず最新キャッシュで判定する。
                var data = paths.GetPathData(candidate.path_id);
                if (!Eligible(data) || paths.GetPathObject(data.path_id) == null) continue;
                roads.TryGetValue(data.path_id, out var road);
                long revision = paths.GetPathGeometryRevision(data.path_id);
                VehiclePolyline line;
                if (road != null && revision != -1 && revision == road.geometryRevision && road.data == data) line = road.line;
                else
                {
                    if (!paths.TryGetPathPolyline(data.path_id, out var points)) continue;
                    line = road != null && VehiclePolyline.SameSource(road.line, points) ? road.line : new VehiclePolyline(points);
                }
                if (line.Length <= ParkedVehicleLayout.EndMargin * 2 || line.InsideIntervals(center, Radius).Count == 0) continue;
                RoadPath.GetPathMasterParams(data, out float width, out float sidewalk, out _, out _, out _,
                    out _, out float divider, out _, out _, out _, out _);
                seen.Add(data.path_id);
                int hand = CityInfoStore.TrafficHand;
                if (road == null || road.width != width || road.sidewalk != sidewalk ||
                    road.divider != divider || road.direction != data.travel_direction || road.hand != hand || road.line != line)
                {
                    road = new Road { data = data, line = line, geometryRevision = revision, width = width, sidewalk = sidewalk, divider = divider,
                        direction = data.travel_direction, hand = hand,
                        slots = ParkedVehicleLayout.Build(line, data.path_id, width, sidewalk, divider, data.travel_direction, hand, models) };
                    roads[data.path_id] = road;
                    changed = true;
                }
                else { road.data = data; road.geometryRevision = revision; }
                int count = ParkedVehicleLayout.Count(data.parked_cars, road.slots.Count, data.path_id);
                if (count != road.count) { road.count = count; changed = true; }
                yield return RenderWorkScheduler.Continue;
            }
            foreach (int id in new List<int>(roads.Keys))
                if (!seen.Contains(id)) { roads.Remove(id); changed = true; }
            if (!changed) yield break;
            var placements = new List<Matrix4x4>[models.Count];
            for (int i = 0; i < placements.Length; i++) placements[i] = new List<Matrix4x4>();
            foreach (var entry in roads)
            {
                if (!Eligible(paths.GetPathData(entry.Key)) || ObjectVisibilityFilter.IsHidden(InputObjectType.PATH, entry.Key)) continue;
                var road = entry.Value;
                for (int i = 0; i < road.count; i++)
                {
                    var p = road.slots[i];
                    if (Utils.XZDistanceSquared(p.position, center) > Radius * Radius) continue;
                    placements[p.model].Add(Matrix4x4.TRS(p.position, p.rotation, Vector3.one));
                }
                yield return RenderWorkScheduler.Continue;
            }
            var next = new List<Batch>();
            for (int model = 0; model < models.Count; model++)
            for (int start = 0; start < placements[model].Count; start += 511)
            foreach (var part in renderModels[model].normal)
            {
                var batch = new Batch { part = part, matrices = new Matrix4x4[Mathf.Min(511, placements[model].Count - start)] };
                for (int i = 0; i < batch.matrices.Length; i++)
                {
                    batch.matrices[i] = placements[model][start + i] * part.local;
                    var bounds = Utils.TransformBounds(part.mesh.bounds, batch.matrices[i]);
                    if (i == 0) batch.bounds = bounds; else batch.bounds.Encapsulate(bounds);
                }
                next.Add(batch);
                yield return RenderWorkScheduler.Continue;
            }
            batches = next;
            lastCenter = center; hasCenter = true; forceRebatch = false;
        }
        finally { worker = null; }
    }

    private void Clear()
    {
        if (worker != null) RenderWorkScheduler.Stop(this, worker);
        worker = null;
        roads.Clear(); batches.Clear(); hasCenter = false; hasTrackedCenter = false; forceRebatch = false; nextScan = 0;
        if (!modelsReady)
        {
            foreach (var model in renderModels) model.Dispose();
            renderModels.Clear(); models.Clear();
        }
    }

    private void OnDisable() => Clear();

    private void OnDestroy()
    {
        Clear();
        foreach (var model in renderModels) model.Dispose();
        renderModels.Clear(); models.Clear();
    }
}
