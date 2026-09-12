using System;
using System.Collections;
using System.Collections.Generic;
using Dreamteck.Splines;
using UnityEngine;

/// <summary>
/// 道路パーツの固定区間LOD。保存された道路やスプラインは分割せず、同じ曲線の区間を描く。
/// 生成済みの区間は退避して再利用し、カメラ移動で区間境界を動かさない。
/// </summary>
public sealed class SplinePartLod : MonoBehaviour
{
    public const float SegmentLength = 128f;
    public bool IsInitialRenderReady => segments.TrueForAll(segment => segment.lod != DrawLod.Hidden);
    private sealed class Segment
    {
        public double from, to;
        public Bounds bounds;
        public GameObject shown;
        public DrawLod lod = DrawLod.Hidden;
    }
    private readonly List<Segment> segments = new List<Segment>();
    private readonly RenderObjectCache cache = new RenderObjectCache(8L * 1024 * 1024);
    private Func<DrawLod, double, double, GameObject> build;
    private SplineComputer spline;
    private float width, lateral, height;
    private bool body;
    private Material material;
    private Coroutine worker;

    public void Configure(SplineComputer source, float partWidth, float offset, float offsetY, bool isBody,
        Material summaryMaterial, double from, double to, Func<DrawLod, double, double, GameObject> factory)
    {
        spline = source; width = partWidth; lateral = offset; height = offsetY; body = isBody; build = factory;
        material = summaryMaterial;
        int count = Mathf.Max(1, Mathf.CeilToInt(spline.CalculateLength() / SegmentLength));
        for (int i = 0; i < count; i++)
        {
            double a = Math.Max(from, (double)i/count), b = Math.Min(to, (double)(i+1)/count);
            if (b <= a) continue;
            Bounds bounds = new Bounds(transform.InverseTransformPoint(spline.Evaluate(a).position), Vector3.zero);
            for (int j = 1; j <= 8; j++) bounds.Encapsulate(transform.InverseTransformPoint(spline.Evaluate(a+(b-a)*j/8).position));
            bounds.Expand(width + Mathf.Abs(lateral)*2);
            segments.Add(new Segment { from = a, to = b, bounds = bounds });
        }
        OnEnable();
    }

    private void OnEnable()
    {
        if (build != null && worker == null && isActiveAndEnabled)
            worker = RenderWorkScheduler.Start(this, Worker(), "RoadSegments");
    }

    private void OnDisable()
    {
        if (worker != null) RenderWorkScheduler.Stop(this, worker);
        worker = null;
    }

    private IEnumerator Worker()
    {
        var order = new List<Segment>(segments);
        var view = new LodViewState();
        while (true)
        {
            Camera camera = Camera.main;
            if (camera == null || spline == null) { yield return null; continue; }
            if (!view.Update(camera)) { yield return null; continue; }
            Vector3 position = transform.InverseTransformPoint(camera.transform.position);
            order.Sort((a,b) => a.bounds.SqrDistance(position).CompareTo(b.bounds.SqrDistance(position)));
            foreach (var segment in order)
            {
                if (!gameObject.activeInHierarchy) break;
                DrawLod desired = DrawLodEvaluator.EvaluatePathByDistance(Mathf.Sqrt(segment.bounds.SqrDistance(position)), segment.lod, true);
                if (desired == segment.lod) continue;
                string key = segment.from.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":" + desired;
                GameObject next = cache.Take(key);
                if (next == null && (desired != DrawLod.Far || body))
                {
                    next = desired == DrawLod.Far ? BuildStrip(segment) : build(desired, segment.from, segment.to);
                    if (next != null)
                    {
                        var identity = next.GetComponent<RenderCacheIdentity>() ?? next.AddComponent<RenderCacheIdentity>();
                        identity.key = key;
                    }
                }
                if (segment.shown != null) cache.Park(segment.shown);
                segment.shown = next;
                segment.lod = desired;
                var presentation = GetComponentInParent<PathPresentation>();
                if (next != null)
                {
                    presentation?.HidePending(next);
                    next.SetActive(true);
                }
                presentation?.TryCommit();
                var visual = GetComponentInParent<VisualModeObject>();
                if (visual != null) visual.InitializeMaterials();
                yield return null;
            }
            yield return null;
        }
    }

    private GameObject BuildStrip(Segment segment)
    {
        var points = new List<Vector3>();
        for (int i = 0; i <= 16; i++)
        {
            SplineSample sample = spline.Evaluate(segment.from+(segment.to-segment.from)*i/16);
            Vector3 right = Vector3.Cross(Vector3.up, sample.forward).normalized;
            points.Add(transform.InverseTransformPoint(sample.position + right*lateral + Vector3.up*height));
        }
        var go = new GameObject("RoadSummary");
        go.transform.SetParent(transform, false);
        Mesh mesh = PathMeshFactory.BuildStripMesh(points, width);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        go.AddComponent<RenderCacheIdentity>().mesh = mesh;
        return go;
    }

    private void OnDestroy() { cache.Clear(); }
}


/// <summary>遅延生成される道路パーツが揃うまで旧道路を保持し、新旧を同じフレームで切り替える。</summary>
public sealed class PathPresentation : MonoBehaviour
{
    private GameObject fallback;
    private RenderObjectCache cache;
    private SplinePartLod[] parts;
    private bool pending;
    private readonly Dictionary<Renderer, bool> hidden = new Dictionary<Renderer, bool>();

    public static void Replace(GameObject next, GameObject previous, RenderObjectCache cache)
    {
        if (next == previous) return;
        var prior = previous != null ? previous.GetComponent<PathPresentation>() : null;
        GameObject visible = prior != null && prior.fallback != null ? prior.TakeFallback() : previous;
        if (previous != null && previous != visible) cache.Park(previous);
        var state = next.GetComponent<PathPresentation>() ?? next.AddComponent<PathPresentation>();
        state.fallback = visible;
        state.cache = cache;
        state.parts = next.GetComponentsInChildren<SplinePartLod>(true);
        state.pending = true;
        state.HidePending(next);
        state.TryCommit();
    }

    private GameObject TakeFallback()
    {
        var result = fallback;
        fallback = null;
        return result;
    }

    public void HidePending(GameObject target)
    {
        if (!pending) return;
        foreach (var renderer in target.GetComponentsInChildren<Renderer>(true))
        {
            if (!hidden.ContainsKey(renderer)) hidden.Add(renderer, renderer.forceRenderingOff);
            renderer.forceRenderingOff = true;
        }
    }

    public void TryCommit()
    {
        if (!pending || parts == null) return;
        foreach (var part in parts)
            if (part != null && !part.IsInitialRenderReady) return;
        pending = false;
        foreach (var item in hidden)
            if (item.Key != null) item.Key.forceRenderingOff = item.Value;
        hidden.Clear();
        var old = TakeFallback();
        if (old != null) cache.Park(old);
    }

    private void OnDisable()
    {
        // アンロードや全消去時は退避キャッシュへ追加せず解放し、旧道路だけ残るのを防ぐ。
        var old = TakeFallback();
        if (old == null) return;
        old.SetActive(false);
        Destroy(old);
    }
}
