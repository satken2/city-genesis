using System.Collections.Generic;
using UnityEngine;

/// <summary>Park completed render objects with ownership. Limit LRU by individual and shared cache caps.</summary>
public sealed class RenderObjectCache
{
    private sealed class Entry
    {
        public string key;
        public GameObject value;
        public long bytes;
        public RenderObjectCache owner;
        public LinkedListNode<Entry> local, global;
    }
    public static long GlobalLimit => PerformanceConfig.RenderCacheBytes;
    private static readonly LinkedList<Entry> globalLru = new LinkedList<Entry>();
    private static readonly List<MeshFilter> filterScratch = new List<MeshFilter>();
    private static readonly List<MeshCollider> colliderScratch = new List<MeshCollider>();
    private static readonly HashSet<Mesh> meshScratch = new HashSet<Mesh>();
    public static long TotalBytes { get; private set; }
    private readonly System.Func<long> limit;
    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
    private readonly LinkedList<Entry> lru = new LinkedList<Entry>();
    public long Bytes { get; private set; }
    public long Hits { get; private set; }

    public RenderObjectCache(long byteLimit) : this(() => byteLimit) { }
    public RenderObjectCache(System.Func<long> byteLimit) { limit = byteLimit; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Reset()
    {
        globalLru.Clear();
        TotalBytes = 0;
    }

    private static void Remove(Entry entry, bool destroy)
    {
        entry.owner.entries.Remove(entry.key);
        entry.owner.lru.Remove(entry.local);
        if (entry.global.List != null) globalLru.Remove(entry.global);
        entry.owner.Bytes -= entry.bytes;
        TotalBytes = System.Math.Max(0, TotalBytes - entry.bytes);
        if (destroy && entry.value != null) Object.Destroy(entry.value);
    }

    public GameObject Take(string key)
    {
        if (!entries.TryGetValue(key, out var entry)) return null;
        Remove(entry, false);
        if (entry.value != null) Hits++;
        return entry.value;
    }

    public void Park(GameObject value)
    {
        if (value == null) return;
        value.SetActive(false);
        var identity = value.GetComponent<RenderCacheIdentity>();
        if (identity == null || string.IsNullOrEmpty(identity.key)) { Object.Destroy(value); return; }
        if (entries.TryGetValue(identity.key, out var duplicate)) Remove(duplicate, duplicate.value != value);
        long bytes = 4096;
        value.GetComponentsInChildren(true, filterScratch);
        foreach (var filter in filterScratch)
            if (filter.sharedMesh != null && meshScratch.Add(filter.sharedMesh)) bytes += EstimateMeshBytes(filter.sharedMesh);
        value.GetComponentsInChildren(true, colliderScratch);
        foreach (var collider in colliderScratch)
            if (collider.sharedMesh != null && meshScratch.Add(collider.sharedMesh)) bytes += EstimateMeshBytes(collider.sharedMesh);
        filterScratch.Clear();
        colliderScratch.Clear();
        meshScratch.Clear();
        identity.bytes = bytes;
        var entry = new Entry { key = identity.key, value = value, bytes = bytes, owner = this };
        entry.local = lru.AddLast(entry);
        entry.global = globalLru.AddLast(entry);
        entries.Add(entry.key, entry);
        Bytes += bytes;
        TotalBytes += bytes;
        while (Bytes > limit() && lru.Count > 0)
        {
            RenderWorkScheduler.CountEvent("renderCache.localEvicted");
            Remove(lru.First.Value, true);
        }
        while (TotalBytes > GlobalLimit && globalLru.Count > 0)
        {
            RenderWorkScheduler.CountEvent("renderCache.globalEvicted");
            Remove(globalLru.First.Value, true);
        }
    }

    /// <summary>
    /// Estimate mesh memory from vertex buffers and indices. Unlike Profiler, works in production builds.
    /// Count meshes retaining CPU copies (isReadable) twice.
    /// </summary>
    public static long EstimateMeshBytes(Mesh mesh)
    {
        if (mesh == null) return 0;
        long vertices = mesh.vertexCount;
        long bytes = 0;
        for (int stream = 0; stream < mesh.vertexBufferCount; stream++) bytes += mesh.GetVertexBufferStride(stream) * vertices;
        long indices = 0;
        for (int sub = 0; sub < mesh.subMeshCount; sub++) indices += mesh.GetIndexCount(sub);
        bytes += indices * (mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32 ? 4 : 2);
        return mesh.isReadable ? bytes * 2 : bytes;
    }

    public void Clear()
    {
        while (lru.Count > 0) Remove(lru.First.Value, true);
        Hits = 0;
    }
}

/// <summary>Owner of generated meshes. Do not register shared FBX meshes.</summary>
public sealed class RenderCacheIdentity : MonoBehaviour
{
    public string key;
    public long bytes;
    public Mesh mesh, colliderMesh;
    public Material[] materials;
    private void OnDestroy()
    {
        if (mesh != null) MeshBakeService.Release(mesh);
        if (colliderMesh != null && colliderMesh != mesh) MeshBakeService.Release(colliderMesh);
        if (materials != null)
            foreach (var material in materials) if (material != null) Destroy(material);
    }
}
