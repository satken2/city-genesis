using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using UnityEngine;

/// <summary>非同期の物理ベイク中は、表示オブジェクトが消えてもメッシュを保持する。</summary>
public sealed class MeshBakeService : MonoBehaviour
{
    private static MeshBakeService instance;
    private static readonly SemaphoreSlim bakeSlots = new SemaphoreSlim(2);
    private readonly Dictionary<Mesh, Task> tasks = new Dictionary<Mesh, Task>();
    private readonly HashSet<Mesh> retiring = new HashSet<Mesh>();
    private readonly List<Mesh> completed = new List<Mesh>();

    public static Task Bake(Mesh mesh)
    {
        if (instance == null)
        {
            instance = new GameObject("MeshBakeService").AddComponent<MeshBakeService>();
            DontDestroyOnLoad(instance.gameObject);
        }
        if (instance.tasks.TryGetValue(mesh, out var pending)) return pending;
        int id = mesh.GetInstanceID();
        var task = Task.Run(async () =>
        {
            await bakeSlots.WaitAsync();
            try { Physics.BakeMesh(id, false); }
            finally { bakeSlots.Release(); }
        });
        instance.tasks.Add(mesh, task);
        return task;
    }

    public static void Release(Mesh mesh)
    {
        if (mesh == null) return;
        if (instance != null && instance.tasks.TryGetValue(mesh, out var task) && !task.IsCompleted)
            instance.retiring.Add(mesh);
        else Destroy(mesh);
    }

    private void Update()
    {
        completed.Clear();
        foreach (var entry in tasks) if (entry.Value.IsCompleted) completed.Add(entry.Key);
        foreach (var mesh in completed)
        {
            var task = tasks[mesh];
            if (task.IsFaulted) Debug.LogException(task.Exception);
            tasks.Remove(mesh);
            if (retiring.Remove(mesh) && mesh != null) Destroy(mesh);
        }
    }

    private void OnDestroy()
    {
        // 終了時もネイティブ処理が参照するアセットを先に解放しない。
        foreach (var task in tasks.Values)
            try { task.GetAwaiter().GetResult(); } catch (System.Exception) { }
        foreach (var mesh in retiring) if (mesh != null) Destroy(mesh);
        if (instance == this) instance = null;
    }
}
