using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Fixed-capacity GPU pages for road markings. Count visible, parked, and generation images under one budget.</summary>
public sealed class RoadMarkingAtlas : MonoBehaviour
{
    public const long BudgetBytes = 128L * 1024 * 1024;
    public const int MaxPixelsPerFrame = 1024 * 1024;
    public const int MaxBakesPerFrame = 2;
    public static long ReservedBytes { get; private set; }
    public static long TableBytes { get; private set; }
    public static int ResidentPages { get; private set; }
    public static int PendingPages { get; private set; }
    public static int BakedPages { get; private set; }
    public static int CacheHits { get; private set; }
    public static int Evictions { get; private set; }
    private static RoadMarkingAtlas instance;
    private static readonly HashSet<RoadMarkingSurface> surfaces = new();
    private static readonly SpatialHash<RoadMarkingSurface> spatial = new(256f);
    private static readonly Dictionary<RoadMarkingSurface, List<Vector3>> registrations = new();
    private readonly Pool[] pools = new Pool[3];
    private readonly Dictionary<string, Page> cache = new();
    private readonly List<Request> requests = new();
    private Material bakeMaterial;
    private Coroutine worker;
    private int epoch, cycle, frame = -1, framePixels, frameBakes;
    private bool frameBudgetBlocked;
    private readonly Plane[] planes = new Plane[6];

    public sealed class Page
    {
        public string Key;
        public int Tier, Slot, WantedCycle;
        public readonly HashSet<RoadMarkingSurface.Tile> Users = new();
    }
    private sealed class Pool
    {
        public RenderTexture Array, Scratch;
        public Page[] Pages;
        public int Size;
    }
    private sealed class Request
    {
        public RoadMarkingSurface.Tile Tile;
        public int Tier;
        public float Priority;
    }

    public static long TextureBytes(int size, int count, int bytesPerPixel = 1)
    {
        long pixels = 0;
        for (int n = size; n > 0; n >>= 1) pixels += (long)n * n;
        return pixels * count * bytesPerPixel;
    }

    public static bool ReserveTable(long bytes)
    {
        // Sum GPU memory, Texture2D CPU copies, and update Color arrays, leaving image reservation space.
        if (TableBytes + bytes * 3 > 4L * 1024 * 1024) return false;
        TableBytes += bytes * 3; return true;
    }
    public static void ReleaseTable(long bytes) => TableBytes = Math.Max(0, TableBytes - bytes * 3);

    public static void Register(RoadMarkingSurface surface)
    {
        Unregister(surface);
        surfaces.Add(surface);
        var bounds = surface.Renderer.bounds;
        var positions = new List<Vector3>();
        for (int z = Mathf.FloorToInt(bounds.min.z / 256f); z <= Mathf.FloorToInt(bounds.max.z / 256f); z++)
            for (int x = Mathf.FloorToInt(bounds.min.x / 256f); x <= Mathf.FloorToInt(bounds.max.x / 256f); x++)
            {
                var position = new Vector3((x + .5f) * 256f, 0, (z + .5f) * 256f);
                spatial.Add(surface, position); positions.Add(position);
            }
        registrations[surface] = positions;
        if (instance == null)
        {
            var go = new GameObject("RoadMarkingAtlas");
            instance = go.AddComponent<RoadMarkingAtlas>();
        }
        if (Application.isPlaying && instance.worker == null)
            // Basic lines render immediately. Missing-capacity detail images must not block initial loading completion.
            instance.worker = RenderWorkScheduler.Start(instance, instance.Worker(), "RoadMarkings", () => false);
    }
    public static void Unregister(RoadMarkingSurface surface)
    {
        surfaces.Remove(surface);
        if (!registrations.TryGetValue(surface, out var positions)) return;
        foreach (var position in positions) spatial.Remove(surface, position);
        registrations.Remove(surface);
    }

    private IEnumerator Worker()
    {
        while (true)
        {
            var camera = Camera.main;
            if (camera == null) { yield return null; continue; }
            int generation = epoch;
            yield return Collect(camera);
            int pending = 0;
            float refreshAt = Time.unscaledTime + .15f;
            for (int index = 0; index < requests.Count; index++)
            {
                var request = requests[index];
                if (generation != epoch) break;
                bool complete = Process(request);
                while (!complete && frameBudgetBlocked && Time.unscaledTime < refreshAt)
                {
                    yield return null;
                    if (generation != epoch) break;
                    complete = Process(request);
                }
                if (!complete) pending++;
                if (Time.unscaledTime >= refreshAt) { pending += requests.Count - index - 1; break; }
                yield return RenderWorkScheduler.Continue;
            }
            PendingPages = pending;
            yield return new WaitForSecondsRealtime(.15f);
        }
    }

    private IEnumerator Collect(Camera camera)
    {
        cycle++; requests.Clear();
        GeometryUtility.CalculateFrustumPlanes(camera, planes);
        // Cell counts cannot limit density; use both spatial queries and frame budgets.
        var nearby = new HashSet<RoadMarkingSurface>(spatial.Query(camera.transform.position, DrawLodConfig.PATH_NORMAL_EXIT_DISTANCE));
        foreach (var surface in nearby)
        {
            yield return RenderWorkScheduler.Continue;
            if (surface == null || !surface.isActiveAndEnabled || surface.Renderer == null || !surface.Renderer.enabled) continue;
            Bounds bounds = surface.Renderer.bounds;
            float distance = Mathf.Sqrt(bounds.SqrDistance(camera.transform.position));
            if (distance > DrawLodConfig.PATH_NORMAL_EXIT_DISTANCE || !GeometryUtility.TestPlanesAABB(planes, bounds)) continue;
            surface.EnsureTable();
            foreach (var tile in surface.Tiles)
            {
                Vector3 position = surface.World(tile.Rect.center);
                float d = Vector3.Distance(position, camera.transform.position);
                float pixels = camera.orthographic ? RoadMarkingPlan.TileSize * camera.pixelHeight / (2f * camera.orthographicSize)
                    : RoadMarkingPlan.TileSize * camera.pixelHeight / (2f * Mathf.Max(1f, d) * Mathf.Tan(camera.fieldOfView * .5f * Mathf.Deg2Rad));
                int tier = pixels > 640f && d < DrawLodConfig.PATH_CLOSE_EXIT_DISTANCE ? 2 : pixels > 240f ? 1 : 0;
                // Add margin around resolution boundaries to avoid rebaking on tiny zoom oscillations.
                if (tile.Page != null && tile.Page.Tier == 2 && pixels > 500f && d < DrawLodConfig.PATH_CLOSE_EXIT_DISTANCE) tier = 2;
                else if (tile.Page != null && tile.Page.Tier == 1 && pixels > 180f && tier == 0) tier = 1;
                requests.Add(new Request { Tile = tile, Tier = tier, Priority = d });
                // Do not evict/regenerate pages within the same view solely because enumeration order changed.
                for (int level = tier; level >= 0; level--)
                    if (cache.TryGetValue(tile.Key + ":" + level, out var resident)) resident.WantedCycle = cycle;
            }
        }
        requests.Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }

    private bool Process(Request request)
    {
        frameBudgetBlocked = false;
        var tile = request.Tile;
        if (tile.Surface == null || !tile.Surface.isActiveAndEnabled || !surfaces.Contains(tile.Surface) || !tile.Surface.Tiles.Contains(tile)) return true;
        if (frame != Time.frameCount) { frame = Time.frameCount; framePixels = 0; frameBakes = 0; }
        for (int tier = request.Tier; tier >= 0; tier--)
        {
            string key = tile.Key + ":" + tier;
            if (cache.TryGetValue(key, out var cached))
            {
                cached.WantedCycle = cycle;
                if (tile.Page != cached) { tile.Surface.SetPage(tile, cached); CacheHits++; }
                return true;
            }
            int size = 256 << tier;
            if (frameBakes >= MaxBakesPerFrame || framePixels + size * size > MaxPixelsPerFrame)
            { frameBudgetBlocked = true; if (tile.Page != null) tile.Page.WantedCycle = cycle; return false; }
            Pool pool = GetPool(tier);
            if (pool == null) continue;
            int slot = -1, oldest = int.MaxValue;
            for (int i = 0; i < pool.Pages.Length; i++)
            {
                var page = pool.Pages[i];
                if (page == null) { slot = i; break; }
                if (page.WantedCycle < cycle && page.WantedCycle < oldest) { slot = i; oldest = page.WantedCycle; }
            }
            if (slot < 0) continue;
            var previous = pool.Pages[slot];
            if (previous != null)
            {
                foreach (var user in new List<RoadMarkingSurface.Tile>(previous.Users)) user.Surface.SetPage(user, null);
                cache.Remove(previous.Key); ResidentPages--; Evictions++;
            }
            Bake(pool, tile);
            for (int mip = 0; mip < pool.Scratch.mipmapCount; mip++) Graphics.CopyTexture(pool.Scratch, 0, mip, pool.Array, slot, mip);
            var replacement = new Page { Key = key, Tier = tier, Slot = slot, WantedCycle = cycle };
            pool.Pages[slot] = replacement; cache[key] = replacement;
            tile.Surface.SetPage(tile, replacement);
            ResidentPages++; BakedPages++; framePixels += size * size; frameBakes++;
            RenderWorkScheduler.CountEvent("roadMarking.bakedPixels", size * size);
            return true;
        }
        return false;
    }

    private Pool GetPool(int tier)
    {
        if (pools[tier] != null) return pools[tier];
        bool r8 = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8);
        int channels = r8 ? 1 : 4;
        int size = 256 << tier;
        int count = (tier == 0 ? 512 : tier == 1 ? 128 : 24) / channels;
        long bytes = TextureBytes(size, count + 1, channels);
        if (ReservedBytes + bytes + TableBytes > BudgetBytes || !SystemInfo.supports2DArrayTextures) return null;
        var format = r8 ? RenderTextureFormat.R8 : RenderTextureFormat.ARGB32;
        var pool = new Pool { Size = size, Pages = new Page[count] };
        // Default mip count (all levels) makes WebGPU count layers like dimensions; arrays with more layers than side length fail creation. Explicitly match the bake target mip count.
        int mipCount = Mathf.RoundToInt(Mathf.Log(size, 2)) + 1;
        pool.Array = new RenderTexture(new RenderTextureDescriptor(size, size, format, 0, mipCount, RenderTextureReadWrite.Linear)
        { dimension = TextureDimension.Tex2DArray, volumeDepth = count, useMipMap = true, autoGenerateMips = false })
        { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp, name = "Road markings " + size };
        pool.Scratch = new RenderTexture(size, size, 0, format, RenderTextureReadWrite.Linear)
        { useMipMap = true, autoGenerateMips = false, filterMode = FilterMode.Trilinear, name = "Road marking bake " + size };
        if (!pool.Array.Create() || !pool.Scratch.Create())
        { Destroy(pool.Array); Destroy(pool.Scratch); return null; }
        ReservedBytes += bytes; pools[tier] = pool;
        Shader.SetGlobalTexture("_RoadMarkingAtlas" + tier, pool.Array);
        return pool;
    }

    private void Bake(Pool pool, RoadMarkingSurface.Tile tile)
    {
        if (bakeMaterial == null) bakeMaterial = new Material(Resources.Load<Shader>("Shaders/RoadMarkingBake"));
        float gutter = RoadMarkingPlan.TileSize * 2f / (pool.Size - 4f);
        Rect rect = tile.Rect;
        var props = new MaterialPropertyBlock();
        props.SetVector("_BakeRect", new Vector4(rect.xMin - gutter, rect.yMin - gutter, rect.width + 2 * gutter, rect.height + 2 * gutter));
        using var command = new CommandBuffer { name = "Bake road markings" };
        command.SetRenderTarget(pool.Scratch); command.ClearRenderTarget(false, true, Color.clear);
        command.DrawMesh(tile.Surface.Plan.GetMesh(), Matrix4x4.identity, bakeMaterial, 0, 0, props);
        command.GenerateMips(pool.Scratch);
        Graphics.ExecuteCommandBuffer(command);
    }

    /// <summary>Run actual page selection and baking for a full pass during validation too.</summary>
    public static void Refresh(Camera camera)
    {
        if (instance == null) return;
        var collection = instance.Collect(camera);
        while (collection.MoveNext()) { }
        foreach (var request in instance.requests) instance.Process(request);
    }

    public static void Clear()
    {
        if (instance != null)
        {
            instance.epoch++;
            if (instance.worker != null) RenderWorkScheduler.Stop(instance, instance.worker);
            instance.worker = null;
            foreach (var surface in new List<RoadMarkingSurface>(surfaces)) if (surface != null) surface.ReleaseTable();
            instance.cache.Clear(); instance.requests.Clear();
            for (int i = 0; i < instance.pools.Length; i++)
            {
                var pool = instance.pools[i];
                if (pool == null) continue;
                pool.Array.Release(); pool.Scratch.Release(); Destroy(pool.Array); Destroy(pool.Scratch);
                instance.pools[i] = null;
            }
            if (instance.bakeMaterial != null) Destroy(instance.bakeMaterial);
        }
        surfaces.Clear(); spatial.Clear(); registrations.Clear(); ReservedBytes = TableBytes = 0; ResidentPages = PendingPages = BakedPages = CacheHits = Evictions = 0;
        RoadMarkingSurface.ClearMaterials();
    }

    private void OnDestroy() { if (instance == this) { Clear(); instance = null; } }
}
