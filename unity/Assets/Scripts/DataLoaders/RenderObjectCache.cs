using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

/// <summary>完成した描画物を所有権ごと退避する。個別上限と全キャッシュ共通上限でLRUを制限する。</summary>
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
    public const long GlobalLimit = 512L * 1024 * 1024;
    private static readonly LinkedList<Entry> globalLru = new LinkedList<Entry>();
    public static long TotalBytes { get; private set; }
    private readonly long limit;
    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
    private readonly LinkedList<Entry> lru = new LinkedList<Entry>();
    public long Bytes { get; private set; }
    public long Hits { get; private set; }

    public RenderObjectCache(long byteLimit) { limit = byteLimit; }

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
        var meshes = new HashSet<Mesh>();
        foreach (var filter in value.GetComponentsInChildren<MeshFilter>(true))
            if (filter.sharedMesh != null && meshes.Add(filter.sharedMesh)) bytes += Profiler.GetRuntimeMemorySizeLong(filter.sharedMesh);
        foreach (var collider in value.GetComponentsInChildren<MeshCollider>(true))
            if (collider.sharedMesh != null && meshes.Add(collider.sharedMesh)) bytes += Profiler.GetRuntimeMemorySizeLong(collider.sharedMesh);
        identity.bytes = bytes;
        var entry = new Entry { key = identity.key, value = value, bytes = bytes, owner = this };
        entry.local = lru.AddLast(entry);
        entry.global = globalLru.AddLast(entry);
        entries.Add(entry.key, entry);
        Bytes += bytes;
        TotalBytes += bytes;
        while (Bytes > limit && lru.Count > 0)
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

    public void Clear()
    {
        while (lru.Count > 0) Remove(lru.First.Value, true);
        Hits = 0;
    }
}

/// <summary>生成メッシュの所有者。共有FBXのメッシュは登録しない。</summary>
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
