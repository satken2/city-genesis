using System;
using System.Collections;
using System.Collections.Generic;
using Dreamteck.Splines;
using UnityEngine;

/// <summary>
/// Fixed-section LOD for road parts. Render sections of the same curve without splitting saved roads or splines.
/// Park and reuse generated sections; camera movement never moves section boundaries.
/// </summary>
public sealed class SplinePartLod : MonoBehaviour
{
    public const float SegmentLength = 128f;
    public bool IsInitialRenderReady => segments.TrueForAll(segment => segment.lod != DrawLod.Hidden);
    public bool IsBody => body;
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
    private int farVariant;
    private Vector3? queriedPosition;
    private Segment queriedSegment;
    private PathPresentation presentation;
    private VisualModeObject visual;

    /// <summary>
    /// Enabled parts. One shared worker switches sections of every part nearest the camera first;
    /// per-part workers would take turns regardless of distance, letting distant roads delay the sections in front of the camera.
    /// </summary>
    private static readonly HashSet<SplinePartLod> active = new HashSet<SplinePartLod>();
    private static SplinePartLodWorker host;
    /// <summary>Set when a part is enabled, so its sections are evaluated without waiting for camera movement.</summary>
    private static bool partsChanged;

    /// <summary>
    /// Camera movement generation. Advance once in frames with movement of at least REEVALUATE_CAMERA_MOVE_DISTANCE.
    /// The idle worker sleeps in WaitUntil (native waiting outside scheduler budget) until the generation changes.
    /// </summary>
    private static int cameraEpoch;
    private static int tickFrame = -1;
    private static LodViewState sharedView; // This is a struct; readonly would make UpdatePosition affect only a defensive copy, losing recorded state.
    private static readonly Comparison<(float distance, SplinePartLod part, Segment segment)> ByDistance = (a, b) => a.distance.CompareTo(b.distance);

    private static void Tick()
    {
        if (Time.frameCount == tickFrame) return;
        tickFrame = Time.frameCount;
        if (sharedView.UpdatePosition(Camera.main, DrawLodConfig.REEVALUATE_CAMERA_MOVE_DISTANCE)) cameraEpoch++;
    }

    /// <summary>LOD of the section displayed at the specified position. Hidden while awaiting generation or parked.</summary>
    public DrawLod GetDrawLodAtPosition(Vector3 worldPosition)
    {
        if (!isActiveAndEnabled || spline == null) return DrawLod.Hidden;
        // For queries at fixed positions such as signs, avoid projecting onto the curve every frame.
        if (!queriedPosition.HasValue || queriedPosition.Value != worldPosition)
        {
            queriedPosition = worldPosition;
            queriedSegment = null;
            double percent = spline.Project(worldPosition).percent;
            foreach (var segment in segments)
            {
                if (percent < segment.from || percent > segment.to) continue;
                queriedSegment = segment;
                break;
            }
        }
        return queriedSegment?.shown != null && queriedSegment.shown.activeInHierarchy
            ? queriedSegment.lod : DrawLod.Hidden;
    }

    /// <param name="pathVariant">Far model variant repeated in distant sections (PathFarModel). 0 means do not draw Far sections.</param>
    public void Configure(SplineComputer source, float partWidth, float offset, float offsetY, bool isBody,
        int pathVariant, double from, double to, Func<DrawLod, double, double, GameObject> factory)
    {
        spline = source; width = partWidth; lateral = offset; height = offsetY; body = isBody; build = factory;
        queriedPosition = null;
        farVariant = pathVariant;
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
        if (build == null || !isActiveAndEnabled) return;
        active.Add(this);
        partsChanged = true;
        if (host == null && Application.isPlaying)
        {
            host = new GameObject("SplinePartLodWorker").AddComponent<SplinePartLodWorker>();
            RenderWorkScheduler.Start(host, Worker(), "RoadSegments", HasUnreadyPart);
        }
    }

    private void OnDisable() => active.Remove(this);

    private static bool HasUnreadyPart()
    {
        foreach (var part in active)
            if (part != null && !part.IsInitialRenderReady) return true;
        return false;
    }

    /// <summary>
    /// Shared worker. After camera movement or part enabling, collect sections of all enabled parts whose LOD differs and switch them nearest first.
    /// After switching for REEVALUATE_INTERVAL_FRAMES, discard the rest of the list on further changes and recollect from the current camera.
    /// </summary>
    private static IEnumerator Worker()
    {
        var parts = new List<SplinePartLod>();
        var order = new List<(float distance, SplinePartLod part, Segment segment)>();
        var watch = new System.Diagnostics.Stopwatch();
        int seenEpoch = -1;
        var waitChange = new WaitUntil(() => { Tick(); return cameraEpoch != seenEpoch || partsChanged; });
        while (true)
        {
            Camera camera = Camera.main;
            if (camera == null) { yield return null; continue; }
            Tick();
            if (seenEpoch == cameraEpoch && !partsChanged) { yield return waitChange; continue; }
            seenEpoch = cameraEpoch;
            partsChanged = false;
            Vector3 cameraPosition = camera.transform.position;
            parts.Clear();
            parts.AddRange(active);
            order.Clear();
            watch.Restart();
            foreach (var part in parts)
            {
                if (watch.Elapsed.TotalMilliseconds > PerformanceConfig.SliceBudgetMs)
                {
                    yield return RenderWorkScheduler.Continue;
                    watch.Restart();
                }
                if (part == null || !part.isActiveAndEnabled || part.spline == null) continue;
                RenderWorkScheduler.CountEvent("roadSegment.lodScanned", part.segments.Count);
                Vector3 position = part.transform.InverseTransformPoint(cameraPosition);
                foreach (var segment in part.segments)
                {
                    float distance = Mathf.Sqrt(segment.bounds.SqrDistance(position));
                    if (DrawLodEvaluator.EvaluatePathByDistance(distance, segment.lod, true) != segment.lod)
                        order.Add((distance, part, segment));
                }
            }
            order.Sort(ByDistance);
            // Count the interval from the end of collection so a collection slower than the interval still leaves time to switch sections.
            int nextScanFrame = Time.frameCount + DrawLodConfig.REEVALUATE_INTERVAL_FRAMES;
            foreach (var (_, part, segment) in order)
            {
                Tick();
                if ((cameraEpoch != seenEpoch || partsChanged) && Time.frameCount >= nextScanFrame) break;
                if (part == null || !part.isActiveAndEnabled) continue;
                camera = Camera.main;
                if (camera == null) { seenEpoch = -1; break; }
                if (part.Apply(segment, camera.transform.position)) yield return RenderWorkScheduler.Continue;
            }
        }
    }

    /// <summary>
    /// Switch a section to the LOD for the current camera. Return false if it already matches,
    /// so obsolete detail parts are not generated after the camera moved since collection.
    /// </summary>
    private bool Apply(Segment segment, Vector3 cameraPosition)
    {
        Vector3 position = transform.InverseTransformPoint(cameraPosition);
        DrawLod desired = DrawLodEvaluator.EvaluatePathByDistance(Mathf.Sqrt(segment.bounds.SqrDistance(position)), segment.lod, true);
        if (desired == segment.lod) return false;
        string key = segment.from.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":" + desired;
        GameObject next = cache.Take(key);
        if (next != null) RenderWorkScheduler.CountEvent("roadSegment.reuseCached");
        if (next == null && (desired != DrawLod.Far || farVariant > 0))
        {
            RenderWorkScheduler.CountEvent("roadSegment.buildRequested");
            next = desired == DrawLod.Far ? BuildFar(segment) : build(desired, segment.from, segment.to);
            if (next != null)
            {
                var identity = next.GetComponent<RenderCacheIdentity>();
                if (identity == null) identity = next.AddComponent<RenderCacheIdentity>();
                identity.key = key;
            }
        }
        if (segment.shown != null) cache.Park(segment.shown);
        segment.shown = next;
        segment.lod = desired;
        // Both attach to the path root and retain parents through parking/restoration; cache them once found.
        if (presentation == null) presentation = GetComponentInParent<PathPresentation>();
        if (next != null)
        {
            if (presentation != null) presentation.HidePending(next);
            next.SetActive(true);
        }
        if (visual == null) visual = GetComponentInParent<VisualModeObject>();
        if (visual != null) visual.RefreshMaterials();
        if (presentation != null) presentation.TryCommit();
        return true;
    }

    private GameObject BuildFar(Segment segment)
    {
        var points = new List<Vector3>();
        for (int i = 0; i <= 16; i++)
        {
            SplineSample sample = spline.Evaluate(segment.from+(segment.to-segment.from)*i/16);
            Vector3 right = Vector3.Cross(Vector3.up, sample.forward).normalized;
            points.Add(transform.InverseTransformPoint(sample.position + right*lateral + Vector3.up*height));
        }
        return PathFarModel.Create(gameObject, "RoadSummary", farVariant, points);
    }

    private void OnDestroy() { cache.Clear(); }
}

/// <summary>Owner of the shared SplinePartLod worker. Created with the first enabled part.</summary>
public sealed class SplinePartLodWorker : MonoBehaviour { }

/// <summary>Retain the old road until deferred parts are ready, switching old/new in the same frame.</summary>
public sealed class PathPresentation : MonoBehaviour
{
    public bool IsReady => !pending;
    /// <summary>Keep generation-wait visibility hidden while updating values to restore when rendering resumes.</summary>
    public void SetRendererForceOff(Renderer renderer, bool value)
    {
        if (pending && hidden.ContainsKey(renderer)) hidden[renderer] = value;
        else renderer.forceRenderingOff = value;
    }
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
        var state = next.GetComponent<PathPresentation>();
        if (state == null) state = next.AddComponent<PathPresentation>();
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
        // During unload/clear, release rather than adding to parked caches, preventing only the old road from remaining.
        var old = TakeFallback();
        if (old == null) return;
        old.SetActive(false);
        Destroy(old);
    }
}
