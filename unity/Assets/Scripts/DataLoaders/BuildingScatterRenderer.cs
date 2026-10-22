using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Draw scatter parts (scatter1, 2, …) of shown buildings with GPU instancing of shared models instead of combining copies
/// into each building mesh. Buildings decide placements (<see cref="ScatterLayout"/>); this groups parts spatially across
/// buildings and selects render LOD per part with the terrain-prop rules, independent of the building's own render LOD.
/// Parts keep their own materials, so building color modes and highlights do not apply. Like other site decorations, they cast no shadows.
/// </summary>
public sealed class BuildingScatterRenderer : MonoBehaviour
{
    /// <summary>One placed part. path is the part resource path without the LOD suffix; local is relative to the building object.</summary>
    public struct Instance
    {
        public string path;
        public Matrix4x4 local;
    }

    /// <summary>Shared part model. layout is the placement bounding box, measured as when combining into a building mesh. render is null without meshes.</summary>
    public sealed class Model
    {
        public PropRenderModel render;
        public Bounds layout;
    }

    /// <summary>
    /// Destination for scatter parts while generating a shown building. Null combines them into the generated mesh
    /// (plan previews and thumbnails, which have no building object lifetime).
    /// </summary>
    internal static List<Instance> Sink;

    /// <summary>Group side (m). Keeps frustum culling effective and rebuilds cheap when one building changes.</summary>
    private const float GroupSize = 64f;
    private const int BatchSize = 511;

    private sealed class Group
    {
        public readonly HashSet<BuildingScatterMember> members = new HashSet<BuildingScatterMember>();
        public readonly List<Batch> batches = new List<Batch>();
        public bool dirty;
        public Vector3 lastCamera = Vector3.positiveInfinity;
        public float lastProjection;
        public bool lastOrthographic;
    }

    private sealed class Batch
    {
        public PropRenderModel.Part part;
        public Matrix4x4[] matrices;
        public Bounds bounds;
    }

    private static BuildingScatterRenderer instance;
    private static readonly Dictionary<string, Model> models = new Dictionary<string, Model>();
    private readonly Dictionary<Vector2Int, Group> groups = new Dictionary<Vector2Int, Group>();
    private readonly Dictionary<(PropRenderModel, DrawLod), List<Matrix4x4>> placements = new Dictionary<(PropRenderModel, DrawLod), List<Matrix4x4>>();
    private readonly Plane[] planes = new Plane[6];

    // Models resolve through ModelManager and may come from an asset pack. Reload them on city switches.
    static BuildingScatterRenderer() => AssetPackStore.CityChanging += ForgetModels;

    /// <summary>Shared model of a part, or null if the part does not exist.</summary>
    internal static Model GetModel(string path)
    {
        if (models.TryGetValue(path, out var model)) return model;
        var manager = ModelManager.Instance;
        if (manager == null) return null;
        var prefab = manager.GetModel(path, true);
        if (prefab != null)
        {
            model = new Model();
            Mesh mesh = BuildingMeshUtils.CombineModuleMeshes(prefab, out _);
            if (mesh != null)
            {
                model.layout = mesh.bounds;
                model.render = PropRenderModel.FromPath(path);
                Destroy(mesh);
            }
        }
        models[path] = model;
        return model;
    }

    private static void ForgetModels()
    {
        foreach (var model in models.Values) model?.render?.Dispose();
        models.Clear();
        if (instance == null) return;
        instance.placements.Clear();
        foreach (var group in instance.groups.Values)
        {
            foreach (var member in group.members) member.models = null;
            group.batches.Clear();
            group.dirty = true;
        }
    }

    internal static void Register(BuildingScatterMember member)
    {
        if (instance == null)
        {
            instance = new GameObject("BuildingScatter").AddComponent<BuildingScatterRenderer>();
            RenderWorkScheduler.Start(instance, instance.Worker(), "BuildingScatter", () => false);
        }
        Resolve(member);
        var cam = Camera.main;
        if (cam != null) Evaluate(member, cam.transform.position, DrawLodEvaluator.GetTanHalfFov(cam), cam.orthographic);
        Vector3 position = member.transform.position;
        member.groupKey = new Vector2Int(Mathf.FloorToInt(position.x / GroupSize), Mathf.FloorToInt(position.z / GroupSize));
        if (!instance.groups.TryGetValue(member.groupKey, out var group)) instance.groups.Add(member.groupKey, group = new Group());
        group.members.Add(member);
        group.dirty = true;
    }

    /// <summary>Remove a member. Its group is rebuilt before the next draw, so removed parts do not remain.</summary>
    internal static void Remove(BuildingScatterMember member)
    {
        if (instance == null || !instance.groups.TryGetValue(member.groupKey, out var group) || !group.members.Remove(member)) return;
        if (group.members.Count == 0) instance.groups.Remove(member.groupKey);
        else group.dirty = true;
    }

    /// <summary>Resolve models and world placements from the building's current transform.</summary>
    private static void Resolve(BuildingScatterMember member)
    {
        int count = member.instances.Count;
        member.models = new Model[count];
        member.world = new Matrix4x4[count];
        member.centers = new Vector3[count];
        member.lods = new DrawLod[count];
        Matrix4x4 building = member.transform.localToWorldMatrix;
        for (int i = 0; i < count; i++)
        {
            var model = GetModel(member.instances[i].path);
            member.models[i] = model;
            member.world[i] = building * member.instances[i].local;
            member.centers[i] = member.world[i].MultiplyPoint3x4(model?.render != null ? model.render.bounds.center : Vector3.zero);
            member.lods[i] = DrawLod.Normal;
        }
    }

    /// <summary>Select each part's render LOD with the terrain-prop rules. Return whether any part changed.</summary>
    private static bool Evaluate(BuildingScatterMember member, Vector3 camera, float projection, bool orthographic)
    {
        if (member.models == null) Resolve(member);
        bool changed = false;
        for (int i = 0; i < member.lods.Length; i++)
        {
            var render = member.models[i]?.render;
            if (render == null) continue;
            float distance = Vector3.Distance(member.centers[i], camera);
            DrawLod lod = DrawLodEvaluator.PropHiddenByDistance(distance, member.lods[i]) ? DrawLod.Hidden
                : DrawLodEvaluator.EvaluateBySize(render.Radius, distance, projection, member.lods[i], true, orthographic);
            if (lod == member.lods[i]) continue;
            member.lods[i] = lod;
            changed = true;
        }
        return changed;
    }

    /// <summary>Resident worker reevaluating part LOD in groups the camera has moved relative to. Batches are rebuilt in LateUpdate.</summary>
    private IEnumerator Worker()
    {
        var keys = new List<Vector2Int>();
        while (true)
        {
            keys.Clear();
            keys.AddRange(groups.Keys);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            foreach (var key in keys)
            {
                var cam = Camera.main;
                if (cam == null) break;
                if (!groups.TryGetValue(key, out var group)) continue;
                Vector3 position = cam.transform.position;
                float projection = DrawLodEvaluator.GetTanHalfFov(cam);
                float move = DrawLodConfig.REEVALUATE_CAMERA_MOVE_DISTANCE;
                if ((position - group.lastCamera).sqrMagnitude < move * move && projection == group.lastProjection
                    && cam.orthographic == group.lastOrthographic) continue;
                group.lastCamera = position;
                group.lastProjection = projection;
                group.lastOrthographic = cam.orthographic;
                foreach (var member in group.members)
                    if (Evaluate(member, position, projection, cam.orthographic)) group.dirty = true;
                if (timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs) { yield return RenderWorkScheduler.Continue; timer.Restart(); }
            }
            for (int i = 0; i < DrawLodConfig.REEVALUATE_INTERVAL_FRAMES; i++) yield return null;
        }
    }

    private void Rebuild(Group group)
    {
        group.dirty = false;
        group.batches.Clear();
        foreach (var list in placements.Values) list.Clear();
        foreach (var member in group.members)
        {
            if (member.models == null) Resolve(member);
            for (int i = 0; i < member.lods.Length; i++)
            {
                var render = member.models[i]?.render;
                if (render == null || member.lods[i] == DrawLod.Hidden) continue;
                var key = (render, member.lods[i]);
                if (!placements.TryGetValue(key, out var list)) placements.Add(key, list = new List<Matrix4x4>());
                list.Add(member.world[i]);
            }
        }
        foreach (var entry in placements)
        {
            var transforms = entry.Value;
            if (transforms.Count == 0) continue;
            var render = entry.Key.Item1;
            var parts = entry.Key.Item2 == DrawLod.Close ? render.close : entry.Key.Item2 == DrawLod.Far ? render.far : render.normal;
            foreach (var part in parts)
                for (int start = 0; start < transforms.Count; start += BatchSize)
                {
                    var batch = new Batch { part = part, matrices = new Matrix4x4[Mathf.Min(BatchSize, transforms.Count - start)] };
                    for (int i = 0; i < batch.matrices.Length; i++)
                    {
                        batch.matrices[i] = transforms[start + i] * part.local;
                        var bounds = Utils.TransformBounds(part.mesh.bounds, batch.matrices[i]);
                        if (i == 0) batch.bounds = bounds; else batch.bounds.Encapsulate(bounds);
                    }
                    group.batches.Add(batch);
                }
        }
    }

    private void LateUpdate()
    {
        var cam = Camera.main;
        if (cam == null) return;
        foreach (var group in groups.Values) if (group.dirty) Rebuild(group);
        GeometryUtility.CalculateFrustumPlanes(cam, planes);
        foreach (var group in groups.Values)
            foreach (var batch in group.batches)
            {
                if (!GeometryUtility.TestPlanesAABB(planes, batch.bounds)) continue;
                var parameters = new RenderParams(UndergroundMaterials.ForSurface(batch.part.material))
                {
                    camera = cam, worldBounds = batch.bounds, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = true
                };
                Graphics.RenderMeshInstanced(parameters, batch.part.mesh, batch.part.submesh, batch.matrices);
            }
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }
}

/// <summary>
/// Hold one building's scatter parts and forward its lifetime, visibility, and movement to <see cref="BuildingScatterRenderer"/>.
/// Parts are shown while the building's own renderer is enabled. Code changing that renderer's enabled state or the building's
/// Transform calls RefreshObject. No per-frame polling.
/// </summary>
public sealed class BuildingScatterMember : MonoBehaviour
{
    internal readonly List<BuildingScatterRenderer.Instance> instances = new List<BuildingScatterRenderer.Instance>();
    internal BuildingScatterRenderer.Model[] models;
    internal Matrix4x4[] world;
    internal Vector3[] centers;
    internal DrawLod[] lods;
    internal Vector2Int groupKey;
    private MeshRenderer source;
    private bool registered, wasVisible;
    private Matrix4x4 matrix;

    /// <summary>Add parts placed for building (a building object owning the MeshRenderer).</summary>
    public static void Add(GameObject building, List<BuildingScatterRenderer.Instance> items)
    {
        if (building == null || items == null || items.Count == 0) return;
        if (!building.TryGetComponent(out BuildingScatterMember member))
        {
            member = building.AddComponent<BuildingScatterMember>();
            member.source = building.GetComponent<MeshRenderer>();
        }
        member.instances.AddRange(items);
        member.Reregister();
    }

    private bool Visible => isActiveAndEnabled && source != null && source.enabled && instances.Count > 0;

    private void OnEnable() => Reregister();

    private void OnDisable()
    {
        if (registered) BuildingScatterRenderer.Remove(this);
        registered = false;
    }

    private void Reregister()
    {
        if (registered) BuildingScatterRenderer.Remove(this);
        registered = false;
        matrix = transform.localToWorldMatrix;
        wasVisible = Visible;
        if (!wasVisible) return;
        BuildingScatterRenderer.Register(this);
        registered = true;
    }

    /// <summary>Reregister if the building's renderer visibility or Transform changed.</summary>
    public void Refresh()
    {
        if (wasVisible != Visible || matrix != transform.localToWorldMatrix) Reregister();
    }

    /// <summary>Call Refresh if go (may be null or destroyed) has a BuildingScatterMember.</summary>
    public static void RefreshObject(GameObject go)
    {
        if (go != null && go.TryGetComponent(out BuildingScatterMember member)) member.Refresh();
    }
}
