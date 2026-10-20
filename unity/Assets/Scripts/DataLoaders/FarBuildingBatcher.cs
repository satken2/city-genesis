using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Combine distant outline meshes into batches of up to 64 buildings per small spatial group (256m × material × parent).
/// When members enter or leave, immediately dismantle only their batch and restore individual rendering;
/// the persistent worker recombines only dirty groups within budget.
/// </summary>
public sealed class FarBuildingBatcher : MonoBehaviour
{
    internal sealed class Batch
    {
        public GameObject go;
        public readonly List<FarBuildingMember> members = new List<FarBuildingMember>(MaxBatchMembers);
    }
    private sealed class Group
    {
        public (int, int, EntityId, EntityId) key;
        public readonly HashSet<FarBuildingMember> members = new HashSet<FarBuildingMember>();
        public readonly List<Batch> batches = new List<Batch>();
        public bool queued;
    }
    private const int MaxBatchMembers = 64;
    /// <summary>Treat smaller batches as remainders and merge with other remainders or unbatched candidates, preventing endless fragmentation as members come and go.</summary>
    private const int MergeBelow = 32;
    private static FarBuildingBatcher instance;
    private readonly Dictionary<(int, int, EntityId, EntityId), Group> groups = new Dictionary<(int, int, EntityId, EntityId), Group>();
    private readonly Queue<Group> dirtyGroups = new Queue<Group>();
    private readonly List<FarBuildingMember> candidates = new List<FarBuildingMember>();
    private readonly List<FarBuildingMember> batchScratch = new List<FarBuildingMember>(MaxBatchMembers);
    private readonly List<CombineInstance> combineScratch = new List<CombineInstance>();

    public static void Register(FarBuildingMember member)
    {
        if (instance == null)
        {
            instance = new GameObject("FarBuildingBatches").AddComponent<FarBuildingBatcher>();
            RenderWorkScheduler.Start(instance, instance.Worker(), "BuildingSummaries", () => false);
        }
        var position = member.transform.position;
        member.groupKey = (Mathf.FloorToInt(position.x/256f), Mathf.FloorToInt(position.z/256f),
            member.source.sharedMaterial.GetEntityId(), member.transform.parent != null ? member.transform.parent.GetEntityId() : EntityId.None);
        if (!instance.groups.TryGetValue(member.groupKey, out var group))
            instance.groups.Add(member.groupKey, group = new Group { key = member.groupKey });
        group.members.Add(member);
        member.batch = null;
        instance.MarkDirty(group);
    }

    /// <summary>Remove a member. Dismantle its batch immediately so the removed building's outline does not remain in the combined mesh.</summary>
    public static void Remove(FarBuildingMember member)
    {
        if (instance == null || !instance.groups.TryGetValue(member.groupKey, out var group))
        {
            if (member.source != null) member.source.forceRenderingOff = false;
            member.batch = null;
            return;
        }
        if (member.batch != null) instance.Dissolve(group, member.batch);
        if (member.source != null) member.source.forceRenderingOff = false;
        member.batch = null;
        group.members.Remove(member);
        if (group.members.Count == 0)
        {
            for (int i = group.batches.Count - 1; i >= 0; i--) instance.Dissolve(group, group.batches[i]);
            instance.groups.Remove(group.key);
        }
        else instance.MarkDirty(group);
    }

    private void MarkDirty(Group group)
    {
        if (group.queued) return;
        group.queued = true;
        dirtyGroups.Enqueue(group);
    }

    /// <summary>Dismantle a batch and restore individual rendering for every member.</summary>
    private void Dissolve(Group group, Batch batch)
    {
        foreach (var member in batch.members)
        {
            if (ReferenceEquals(member, null)) continue;
            if (member.batch == batch) member.batch = null;
            if (member.source != null) member.source.forceRenderingOff = false;
        }
        batch.members.Clear();
        if (batch.go != null) { batch.go.SetActive(false); Destroy(batch.go); }
        batch.go = null;
        group.batches.Remove(batch);
    }

    private static bool IsCombinable(FarBuildingMember member)
    {
        return member != null && member.isActiveAndEnabled && member.mesh != null && member.source != null
            && member.source.enabled && !member.source.HasPropertyBlock();
    }

    private void CollectCandidates(Group group)
    {
        candidates.Clear();
        foreach (var member in group.members)
            if (member != null && member.batch == null && IsCombinable(member)) candidates.Add(member);
    }

    /// <summary>
    /// Persistent worker recombining only dirty groups. Scan nothing when the queue is empty.
    /// Yield to the budget after each batch; if the group becomes dirty again meanwhile, defer rebuilding to its next turn.
    /// </summary>
    private IEnumerator Worker()
    {
        while (true)
        {
            if (dirtyGroups.Count == 0) { yield return null; continue; }
            var group = dirtyGroups.Dequeue();
            group.queued = false;
            if (!groups.TryGetValue(group.key, out var current) || current != group) continue;

            CollectCandidates(group);
            int small = 0;
            foreach (var batch in group.batches) if (batch.members.Count < MergeBelow) small++;
            if (small >= 2 || (small == 1 && candidates.Count > 0))
            {
                for (int i = group.batches.Count - 1; i >= 0; i--)
                    if (group.batches[i].members.Count < MergeBelow) Dissolve(group, group.batches[i]);
                CollectCandidates(group);
            }
            if (candidates.Count < 2)
            {
                candidates.Clear();
                yield return RenderWorkScheduler.Continue;
                continue;
            }

            for (int start = 0; start < candidates.Count; start += MaxBatchMembers)
            {
                BuildBatch(group, start, Mathf.Min(MaxBatchMembers, candidates.Count - start));
                yield return RenderWorkScheduler.Continue;
                if (group.queued || !groups.TryGetValue(group.key, out current) || current != group) break;
            }
            candidates.Clear();
            yield return RenderWorkScheduler.Continue;
        }
    }

    /// <summary>Combine candidates[start..start+count) into one batch. Merge all submeshes into one (Far uses a single material).</summary>
    private void BuildBatch(Group group, int start, int count)
    {
        batchScratch.Clear();
        for (int i = 0; i < count; i++)
        {
            var member = candidates[start + i];
            if (member != null && member.batch == null && group.members.Contains(member) && IsCombinable(member)) batchScratch.Add(member);
        }
        if (batchScratch.Count < 2) { batchScratch.Clear(); return; }

        var first = batchScratch[0];
        Transform parent = first.transform.parent;
        Matrix4x4 worldToLocal = parent != null ? parent.worldToLocalMatrix : Matrix4x4.identity;
        combineScratch.Clear();
        foreach (var member in batchScratch)
        {
            Matrix4x4 matrix = worldToLocal * member.transform.localToWorldMatrix;
            for (int s = 0; s < member.mesh.subMeshCount; s++)
                combineScratch.Add(new CombineInstance { mesh = member.mesh, subMeshIndex = s, transform = matrix });
        }
        var mesh = new Mesh { name = "BuildingSummary", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.CombineMeshes(combineScratch.ToArray(), true, true);
        combineScratch.Clear();
        var go = new GameObject("BuildingSummary");
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = first.source.sharedMaterial;
        go.AddComponent<UndergroundGhostObject>();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        go.AddComponent<RenderCacheIdentity>().mesh = mesh;

        var batch = new Batch { go = go };
        foreach (var member in batchScratch)
        {
            batch.members.Add(member);
            member.batch = batch;
            member.source.forceRenderingOff = true;
        }
        group.batches.Add(batch);
        batchScratch.Clear();
    }

    private void OnDestroy()
    {
        foreach (var group in groups.Values)
            for (int i = group.batches.Count - 1; i >= 0; i--) Dissolve(group, group.batches[i]);
        dirtyGroups.Clear();
        if (instance == this) instance = null;
    }
}

/// <summary>
/// Forward source lifetime and visibility changes to the combined batch. Enabling/disabling propagates automatically.
/// Code changing Renderer.enabled, position, material, or property blocks calls Refresh. No per-frame polling.
/// </summary>
public sealed class FarBuildingMember : MonoBehaviour, IVisualModeHandler
{
    internal MeshRenderer source;
    internal Mesh mesh;
    internal (int, int, EntityId, EntityId) groupKey;
    internal FarBuildingBatcher.Batch batch;
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
        RegisterBatch();
        VisualModeHandler.Instance?.RegisterObject(InputObjectType.BUILDING, this);
    }
    private void RegisterBatch()
    {
        matrix = transform.localToWorldMatrix; material = source.sharedMaterial; wasEnabled = source.enabled;
        hadPropertyBlock = source.HasPropertyBlock();
        FarBuildingBatcher.Register(this); registered = true;
    }
    private void OnDisable()
    {
        VisualModeHandler.Instance?.UnregisterObject(InputObjectType.BUILDING, this);
        if (registered) FarBuildingBatcher.Remove(this);
        registered = false;
    }

    public void ApplyVisualMode(int mode)
    {
        if (source == null) return;
        var id = GetComponent<ObjectIdInfo>();
        bool visible = mode != VisualMode.MODE_HIDDEN && (id == null || !ObjectVisibilityFilter.IsHidden(InputObjectType.BUILDING, id.id));
        if (id != null && (id.status == Constants.STATUS_PENDING || id.status == Constants.STATUS_TEMPORARY || id.status == Constants.STATUS_SCHEDULED_CONSTRUCTION))
            visible &= VisualModeHandler.Instance == null || (VisualModeHandler.Instance.GetFlags(InputObjectType.BUILDING) & VisualFlag.SHOW_PLAN) != 0;
        source.enabled = visible;
        var collider = GetComponent<Collider>();
        if (collider != null) collider.enabled = visible;
        Refresh();
    }

    /// <summary>
    /// Called by code changing a Far building's Renderer.enabled, Transform, sharedMaterial, or property block.
    /// Reregister with the combined batch if anything changed.
    /// </summary>
    public void Refresh()
    {
        if (source == null) return;
        if (matrix != transform.localToWorldMatrix || material != source.sharedMaterial || wasEnabled != source.enabled || hadPropertyBlock != source.HasPropertyBlock())
        {
            // Material replacement does not reregister the visual mode, avoiding overwriting partially applied visibility with another notification.
            if (registered) FarBuildingBatcher.Remove(this);
            registered = false;
            if (isActiveAndEnabled && mesh != null && source.sharedMaterial != null) RegisterBatch();
        }
    }

    /// <summary>Call Refresh if go (may be null or destroyed) has a FarBuildingMember.</summary>
    public static void RefreshObject(GameObject go)
    {
        if (go == null) return;
        var member = go.GetComponent<FarBuildingMember>();
        if (member != null) member.Refresh();
    }
}
