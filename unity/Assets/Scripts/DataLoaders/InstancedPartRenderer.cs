using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 表示中の建物の散布パーツ(scatter1, 2, …)と道路の沿道設備を、1つずつのメッシュやオブジェクトにせず、共有モデルの GPU インスタンシングで描く。
/// 配置は持ち主が決める(建物は <see cref="ScatterLayout"/>、道路は <see cref="RoadEquipmentGenerator"/>)。ここでは持ち主をまたいで
/// 部品を空間でまとめ、部品ごとの描画LODを持ち主の描画LODとは別に地形プロップの規則で選ぶ。
/// 部品は自分の材質のまま描くので、持ち主の色モードとハイライトは掛からない。
/// 建物の部品はほかの敷地装飾と同じく影を落とさず、道路の設備は木とプロップの影の設定に従って落とす。
/// </summary>
public sealed class InstancedPartRenderer : MonoBehaviour
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

    /// <summary>
    /// Group side (m). Keeps frustum culling effective and rebuilds cheap when one owner changes.
    /// 部品は自分の位置のグループに入る。道路のように長い持ち主の部品は複数のグループに分かれる。
    /// </summary>
    private const float GroupSize = 64f;
    private const int BatchSize = 511;

    private sealed class Group
    {
        public readonly HashSet<InstancedPartMember> members = new HashSet<InstancedPartMember>();
        public readonly List<Batch> batches = new List<Batch>();
        public bool dirty;
        public Vector3 lastCamera = Vector3.positiveInfinity;
        public float lastProjection;
        public bool lastOrthographic;
        public int lastDistanceVersion;
    }

    private sealed class Batch
    {
        public PropRenderModel.Part part;
        public Matrix4x4[] matrices;
        public Bounds bounds;
        public bool shadows;
    }

    private static InstancedPartRenderer instance;
    private static readonly Dictionary<(string, bool), Model> models = new Dictionary<(string, bool), Model>();
    private readonly Dictionary<Vector2Int, Group> groups = new Dictionary<Vector2Int, Group>();
    private readonly Dictionary<(PropRenderModel, DrawLod, bool), List<Matrix4x4>> placements = new Dictionary<(PropRenderModel, DrawLod, bool), List<Matrix4x4>>();
    private readonly Plane[] planes = new Plane[6];

    // Models resolve through ModelManager and may come from an asset pack. Reload them on city switches.
    static InstancedPartRenderer() => AssetPackStore.CityChanging += ForgetModels;

    /// <summary>Shared model of a part, or null if the part does not exist. road は道路の部品で、材質を道路の共有プールから引く。</summary>
    internal static Model GetModel(string path, bool road = false)
    {
        if (models.TryGetValue((path, road), out var model)) return model;
        var manager = ModelManager.Instance;
        if (manager == null) return null;
        var prefab = manager.GetModel(path, true);
        if (prefab != null)
        {
            model = new Model();
            Mesh mesh = road ? null : BuildingMeshUtils.CombineModuleMeshes(prefab, out _);
            if (road) model.render = PropRenderModel.FromPath(path, RoadPath.GetOrCreateRoadMaterial);
            else if (mesh != null)
            {
                model.layout = mesh.bounds;
                model.render = PropRenderModel.FromPath(path);
                Destroy(mesh);
            }
        }
        models[(path, road)] = model;
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

    internal static void Register(InstancedPartMember member)
    {
        if (instance == null)
        {
            instance = new GameObject("InstancedParts").AddComponent<InstancedPartRenderer>();
            RenderWorkScheduler.Start(instance, instance.Worker(), "InstancedParts", () => false);
        }
        Resolve(member);
        var cam = Camera.main;
        foreach (var cell in member.cells)
        {
            if (cam != null) Evaluate(member, cell.Value, cam.transform.position, DrawLodEvaluator.GetTanHalfFov(cam), cam.orthographic);
            if (!instance.groups.TryGetValue(cell.Key, out var group)) instance.groups.Add(cell.Key, group = new Group());
            group.members.Add(member);
            group.dirty = true;
        }
    }

    /// <summary>Remove a member. Its groups are rebuilt before the next draw, so removed parts do not remain.</summary>
    internal static void Remove(InstancedPartMember member)
    {
        if (instance == null || member.cells == null) return;
        foreach (var key in member.cells.Keys)
        {
            if (!instance.groups.TryGetValue(key, out var group) || !group.members.Remove(member)) continue;
            if (group.members.Count == 0) instance.groups.Remove(key);
            else group.dirty = true;
        }
    }

    /// <summary>Resolve models and world placements from the owner's current transform.</summary>
    private static void Resolve(InstancedPartMember member)
    {
        int count = member.instances.Count;
        member.models = new Model[count];
        member.world = new Matrix4x4[count];
        member.centers = new Vector3[count];
        member.lods = new DrawLod[count];
        member.cells = new Dictionary<Vector2Int, List<int>>();
        Matrix4x4 owner = member.transform.localToWorldMatrix;
        for (int i = 0; i < count; i++)
        {
            var model = GetModel(member.instances[i].path, member.road);
            member.models[i] = model;
            member.world[i] = owner * member.instances[i].local;
            member.centers[i] = member.world[i].MultiplyPoint3x4(model?.render != null ? model.render.bounds.center : Vector3.zero);
            member.lods[i] = DrawLod.Normal;
            // モデルを読み直しても同じグループに残るよう、モデルの大きさによらない配置の原点で分ける。
            Vector3 origin = member.world[i].GetColumn(3);
            var key = new Vector2Int(Mathf.FloorToInt(origin.x / GroupSize), Mathf.FloorToInt(origin.z / GroupSize));
            if (!member.cells.TryGetValue(key, out var indices)) member.cells.Add(key, indices = new List<int>());
            indices.Add(i);
        }
    }

    /// <summary>Select the render LOD of the member's parts in one group (indices) with the terrain-prop rules. Return whether any part changed.</summary>
    private static bool Evaluate(InstancedPartMember member, List<int> indices, Vector3 camera, float projection, bool orthographic)
    {
        bool changed = false;
        foreach (int i in indices)
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

    /// <summary>Resident worker reevaluating part LOD in groups the camera has moved relative to, or all groups after a distance setting change. Batches are rebuilt in LateUpdate.</summary>
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
                    && cam.orthographic == group.lastOrthographic && group.lastDistanceVersion == GraphicsConfig.DistanceVersion) continue;
                group.lastCamera = position;
                group.lastProjection = projection;
                group.lastOrthographic = cam.orthographic;
                group.lastDistanceVersion = GraphicsConfig.DistanceVersion;
                foreach (var member in group.members)
                {
                    if (member.models == null) Resolve(member);
                    if (member.cells.TryGetValue(key, out var indices) && Evaluate(member, indices, position, projection, cam.orthographic)) group.dirty = true;
                }
                if (timer.ElapsedMilliseconds > PerformanceConfig.SliceBudgetMs) { yield return RenderWorkScheduler.Continue; timer.Restart(); }
            }
            for (int i = 0; i < DrawLodConfig.REEVALUATE_INTERVAL_FRAMES; i++) yield return null;
        }
    }

    private void Rebuild(Vector2Int groupKey, Group group)
    {
        group.dirty = false;
        group.batches.Clear();
        foreach (var list in placements.Values) list.Clear();
        foreach (var member in group.members)
        {
            if (member.models == null) Resolve(member);
            if (!member.cells.TryGetValue(groupKey, out var indices)) continue;
            foreach (int i in indices)
            {
                var render = member.models[i]?.render;
                if (render == null || member.lods[i] == DrawLod.Hidden) continue;
                var key = (render, member.lods[i], member.road);
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
                    var batch = new Batch { part = part, shadows = entry.Key.Item3, matrices = new Matrix4x4[Mathf.Min(BatchSize, transforms.Count - start)] };
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
        foreach (var entry in groups) if (entry.Value.dirty) Rebuild(entry.Key, entry.Value);
        GeometryUtility.CalculateFrustumPlanes(cam, planes);
        Vector3 position = cam.transform.position;
        float shadowDistance = GraphicsConfig.PropShadows ? PropRenderPolicy.ShadowDistance : 0f;
        foreach (var group in groups.Values)
            foreach (var batch in group.batches)
            {
                // 影を落とすバッチは視界の外でも出す(影は視界の中へ届く。視界の外の本体は Unity が worldBounds で省く)。
                bool shadows = batch.shadows && PropRenderPolicy.WithinShadowDistance(batch.bounds, position, shadowDistance);
                if (!shadows && !GeometryUtility.TestPlanesAABB(planes, batch.bounds)) continue;
                var parameters = new RenderParams(UndergroundMaterials.ForSurface(batch.part.material))
                {
                    camera = cam, worldBounds = batch.bounds, receiveShadows = true,
                    shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off
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
/// 持ち主1つぶんの部品を持ち、その寿命・表示・移動を <see cref="InstancedPartRenderer"/> へ伝える。
/// 建物の部品は建物自身のレンダラーが有効な間、道路の部品は道路の表示(<see cref="VisualModeObject.IsVisualVisible"/>)が有効で
/// 差し替え待ち(<see cref="PathPresentation"/>)でない間だけ描く。
/// これらの状態や持ち主の Transform を変えるコードが RefreshObject を呼ぶ。毎フレームの監視はしない。
/// </summary>
public sealed class InstancedPartMember : MonoBehaviour
{
    internal readonly List<InstancedPartRenderer.Instance> instances = new List<InstancedPartRenderer.Instance>();
    internal InstancedPartRenderer.Model[] models;
    internal Matrix4x4[] world;
    internal Vector3[] centers;
    internal DrawLod[] lods;
    /// <summary>グループの鍵から、そのグループに入る部品の番号。</summary>
    internal Dictionary<Vector2Int, List<int>> cells;
    /// <summary>道路の部品。材質を道路の共有プールから引き、影を落とし、表示を道路の表示状態に合わせる。</summary>
    internal bool road;
    private MeshRenderer source;
    private bool registered, wasVisible;
    private Matrix4x4 matrix;

    /// <summary>Add parts placed for owner: a building object owning the MeshRenderer, or (road) an object under a path.</summary>
    public static void Add(GameObject owner, List<InstancedPartRenderer.Instance> items, bool road = false)
    {
        if (owner == null || items == null || items.Count == 0) return;
        if (!owner.TryGetComponent(out InstancedPartMember member))
        {
            member = owner.AddComponent<InstancedPartMember>();
            member.source = owner.GetComponent<MeshRenderer>();
            member.road = road;
        }
        member.instances.AddRange(items);
        member.Reregister();
    }

    private bool Visible => isActiveAndEnabled && instances.Count > 0 && (road ? RoadShown : source != null && source.enabled);

    /// <summary>表示管理や差し替え待ちは生成の後から道路に付くので、その都度親から探す。どちらも無い道路(プレビューなど)は描く。</summary>
    private bool RoadShown
    {
        get
        {
            var visual = GetComponentInParent<VisualModeObject>();
            var presentation = GetComponentInParent<PathPresentation>();
            return (visual == null || visual.IsVisualVisible) && (presentation == null || presentation.IsReady);
        }
    }

    private void OnEnable() => Reregister();

    private void OnDisable()
    {
        if (registered) InstancedPartRenderer.Remove(this);
        registered = false;
    }

    private void Reregister()
    {
        if (registered) InstancedPartRenderer.Remove(this);
        registered = false;
        matrix = transform.localToWorldMatrix;
        wasVisible = Visible;
        if (!wasVisible) return;
        InstancedPartRenderer.Register(this);
        registered = true;
    }

    /// <summary>Reregister if the owner's visibility or Transform changed.</summary>
    public void Refresh()
    {
        if (wasVisible != Visible || matrix != transform.localToWorldMatrix) Reregister();
    }

    private static readonly List<InstancedPartMember> found = new List<InstancedPartMember>();

    /// <summary>Call Refresh on every InstancedPartMember on go (may be null or destroyed) and under it.</summary>
    public static void RefreshObject(GameObject go)
    {
        if (go == null) return;
        go.GetComponentsInChildren(true, found);
        foreach (var member in found) member.Refresh();
        found.Clear();
    }
}
