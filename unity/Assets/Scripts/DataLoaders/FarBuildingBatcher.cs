using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>遠景の輪郭メッシュを小さな空間区画で結合する。差分更新中は元の描画へ戻す。</summary>
public sealed class FarBuildingBatcher : MonoBehaviour
{
    private sealed class Group
    {
        public readonly HashSet<FarBuildingMember> members = new HashSet<FarBuildingMember>();
        public readonly List<GameObject> batches = new List<GameObject>();
        public bool dirty;
    }
    private static FarBuildingBatcher instance;
    private readonly Dictionary<(int, int, int, int), Group> groups = new Dictionary<(int, int, int, int), Group>();
    public int BatchCount { get; private set; }
    public int CombinedBuildings { get; private set; }

    public static void Register(FarBuildingMember member)
    {
        if (instance == null)
        {
            instance = new GameObject("FarBuildingBatches").AddComponent<FarBuildingBatcher>();
            RenderWorkScheduler.Start(instance, instance.Worker(), "BuildingSummaries", () => false);
        }
        var position = member.transform.position;
        member.groupKey = (Mathf.FloorToInt(position.x/256f), Mathf.FloorToInt(position.z/256f),
            member.source.sharedMaterial.GetInstanceID(), member.transform.parent != null ? member.transform.parent.GetInstanceID() : 0);
        if (!instance.groups.TryGetValue(member.groupKey, out var group))
            instance.groups.Add(member.groupKey, group = new Group());
        group.members.Add(member);
        instance.Invalidate(group);
    }

    public static void Remove(FarBuildingMember member)
    {
        if (instance == null || !instance.groups.TryGetValue(member.groupKey, out var group)) return;
        if (member.source != null) member.source.forceRenderingOff = false;
        group.members.Remove(member);
        instance.Invalidate(group);
        if (group.members.Count == 0) instance.groups.Remove(member.groupKey);
    }

    private void Invalidate(Group group)
    {
        foreach (var member in group.members) if (member != null && member.source != null) member.source.forceRenderingOff = false;
        foreach (var batch in group.batches) if (batch != null) { batch.SetActive(false); Destroy(batch); }
        group.batches.Clear();
        group.dirty = true;
    }

    private IEnumerator Worker()
    {
        while (true)
        {
            var snapshot = new List<Group>(groups.Values);
            foreach (var group in snapshot)
            {
                if (!group.dirty) continue;
                group.dirty = false;
                var members = new List<FarBuildingMember>();
                foreach (var member in group.members)
                    if (member != null && member.isActiveAndEnabled && member.source.enabled && !member.source.HasPropertyBlock()) members.Add(member);
                if (members.Count < 2) continue;
                // 1回の結合を最大64棟に制限する。大都市でも全都市のメッシュを一括で作らない。
                for (int start = 0; start < members.Count; start += 64)
                {
                    if (group.dirty) break;
                    int count = Mathf.Min(64, members.Count-start);
                    var first = members[start];
                    if (first == null) break;
                    Transform parent = first.transform.parent;
                    Matrix4x4 worldToLocal = parent != null ? parent.worldToLocalMatrix : Matrix4x4.identity;
                    var combines = new CombineInstance[count];
                    bool valid = true;
                    for (int i = 0; i < count; i++)
                    {
                        var member = members[start+i];
                        if (member == null || !member.isActiveAndEnabled) { valid = false; break; }
                        combines[i] = new CombineInstance { mesh = member.mesh, transform = worldToLocal * member.transform.localToWorldMatrix };
                    }
                    if (!valid) break;
                    var mesh = new Mesh { name = "BuildingSummary", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                    mesh.CombineMeshes(combines, true, true);
                    var go = new GameObject("BuildingSummary");
                    go.transform.SetParent(parent, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = first.source.sharedMaterial;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    go.AddComponent<RenderCacheIdentity>().mesh = mesh;
                    group.batches.Add(go);
                    for (int i = 0; i < count; i++) members[start+i].source.forceRenderingOff = true;
                    yield return null;
                }
            }
            BatchCount = CombinedBuildings = 0;
            foreach (var group in groups.Values)
            {
                BatchCount += group.batches.Count;
                foreach (var member in group.members) if (member != null && member.source.forceRenderingOff) CombinedBuildings++;
            }
            yield return null;
        }
    }

    private void OnDestroy()
    {
        foreach (var group in groups.Values) Invalidate(group);
        if (instance == this) instance = null;
    }
}

/// <summary>集約元の寿命と表示変更を通知する。非表示・編集直後の旧輪郭を残さない。</summary>
public sealed class FarBuildingMember : MonoBehaviour
{
    internal MeshRenderer source;
    internal Mesh mesh;
    internal (int, int, int, int) groupKey;
    private bool registered, wasEnabled, hadPropertyBlock;
    private Matrix4x4 matrix;
    private Material material;
    public void Configure(MeshRenderer renderer, Mesh sourceMesh)
    {
        source = renderer; mesh = sourceMesh;
        OnEnable();
    }
    private void OnEnable()
    {
        if (registered || source == null || mesh == null || source.sharedMaterial == null || !isActiveAndEnabled) return;
        matrix = transform.localToWorldMatrix; material = source.sharedMaterial; wasEnabled = source.enabled;
        hadPropertyBlock = source.HasPropertyBlock();
        FarBuildingBatcher.Register(this); registered = true;
    }
    private void OnDisable()
    {
        if (registered) FarBuildingBatcher.Remove(this);
        registered = false;
    }
    private void LateUpdate()
    {
        if (source == null) return;
        if (matrix != transform.localToWorldMatrix || material != source.sharedMaterial || wasEnabled != source.enabled || hadPropertyBlock != source.HasPropertyBlock())
        {
            OnDisable(); OnEnable();
        }
    }
}
