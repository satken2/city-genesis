using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using GeneratedModels;

public partial class PathManager
{
    private readonly Dictionary<int, GameObject> undergroundObjects = new Dictionary<int, GameObject>();
    private readonly HashSet<int> undergroundQueued = new HashSet<int>();
    private readonly Queue<int> undergroundQueue = new Queue<int>();
    private Coroutine undergroundWorker;

    private void InitializeUnderground()
    {
        UndergroundView.Changed += OnUndergroundChanged;
        undergroundWorker = RenderWorkScheduler.Start(this, UndergroundWorker(), "UndergroundPaths", () => false);
    }

    private void OnUndergroundChanged()
    {
        if (!UndergroundView.IsActive)
        {
            ClearUndergroundPaths();
            return;
        }
        // 全体走査はモードを切り替えたときだけ。以後は既存の登録・地形変更・削除の通知で更新する。
        foreach (int id in pathDataCache.Keys) QueueUndergroundPath(id);
    }

    private void QueueUndergroundPath(int id)
    {
        if (UndergroundView.IsActive && undergroundQueued.Add(id)) undergroundQueue.Enqueue(id);
    }

    private IEnumerator UndergroundWorker()
    {
        while (true)
        {
            if (undergroundQueue.Count == 0) { yield return null; continue; }
            int id = undergroundQueue.Dequeue();
            undergroundQueued.Remove(id);
            if (UndergroundView.IsActive && pathDataCache.TryGetValue(id, out var path)) BuildUndergroundPath(path);
            yield return RenderWorkScheduler.Continue;
        }
    }

    private void BuildUndergroundPath(PathSummary path)
    {
        RemoveUndergroundPath(path.path_id);
        string modelType = PathMeshFactory.GetModelType(path);
        if ((modelType == "none" && !InfraNetworkUtil.IsInfraPathType(path.path_type)) || modelType == "station_house") return;
        var points = CalculatePathPoints(path);
        float clearance = TunnelSectionUtil.GetClearanceHeight(path.path_variant);
        var runs = TunnelSectionUtil.CollectTunnelRuns(points, clearance, GetPathFollowTerrain(path));
        if (runs.Count == 0) return;
        var go = new GameObject("UndergroundPath_" + path.path_id);
        go.transform.SetParent(transform, false);
        go.tag = PATH_TAG;
        // 地下専用の色や材質は設けず、地上と同じ生成器で非描画区間のモデルだけを補う。
        var colliders = new List<CombineInstance>();
        foreach (var run in runs)
        {
            PathMeshResult result;
            PathDrawContext.Begin(DrawLod.Normal);
            try
            {
                result = modelType == "none" ? new RoadPath().Generate(path, run) : PathMeshFactory.GeneratePathMesh(path, run);
            }
            finally { PathDrawContext.End(); }
            if (result == null) continue;
            if (result.GeneratedObject != null) result.GeneratedObject.transform.SetParent(go.transform, false);
            if (result.Mesh == null) continue;
            colliders.Add(new CombineInstance { mesh = result.Mesh, transform = Matrix4x4.identity });
            if (result.GeneratedObject != null) result.GeneratedObject.AddComponent<RenderCacheIdentity>().colliderMesh = result.Mesh;
        }
        if (colliders.Count == 0) { Destroy(go); return; }
        var mesh = new Mesh { name = "UndergroundPathCollider", indexFormat = IndexFormat.UInt32 };
        mesh.CombineMeshes(colliders.ToArray(), true, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshCollider>().sharedMesh = mesh;
        go.AddComponent<RenderCacheIdentity>().mesh = mesh;
        var id = go.AddComponent<ObjectIdInfo>(); id.id = path.path_id; id.status = path.status;
        go.AddComponent<UndergroundPathMarker>();
        SetupVisuals(go, path, path.status, modulesCreated: true);
        go.AddComponent<PathInputHandler>().pathId = path.path_id;
        undergroundObjects[path.path_id] = go;
    }

    private void RemoveUndergroundPath(int id)
    {
        if (!undergroundObjects.TryGetValue(id, out var go)) return;
        if (go != null) { go.SetActive(false); Destroy(go); }
        undergroundObjects.Remove(id);
    }

    private void RefreshUndergroundVisibility()
    {
        foreach (var pair in undergroundObjects) ObjectVisibilityFilter.ApplyToObject(pair.Value, InputObjectType.PATH, pair.Key);
    }

    private void ClearUndergroundPaths()
    {
        foreach (var go in undergroundObjects.Values) if (go != null) { go.SetActive(false); Destroy(go); }
        undergroundObjects.Clear(); undergroundQueue.Clear(); undergroundQueued.Clear();
    }

    private void DisposeUnderground()
    {
        UndergroundView.Changed -= OnUndergroundChanged;
        RenderWorkScheduler.Stop(this, undergroundWorker);
        ClearUndergroundPaths();
    }
}
