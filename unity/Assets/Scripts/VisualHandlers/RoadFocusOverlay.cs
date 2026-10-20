using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

/// <summary>
/// Overlay drawing named-road sections as road-colored bands along centerlines, always in front.
/// Like TransitOverlayManager highlights, use OverlayMeshRenderer with ZTest Always
/// to avoid terrain/building occlusion and keep screen width constant across zoom levels.
///
/// Two independent displayed sets:
/// - Focus: sections of the road whose detail panel is open (world:set_road_focus).
/// - Section editing: member sections and candidates added by automatic straight extension, shown as faint bands.
/// Suppress focus while editing sections because its set remains stale until the panel refetches,
/// which could leave removed sections visibly highlighted. Restore focus after editing ends.
///
/// Periodically compare geometry using section revision (PathManager.GetPathGeometryRevision) and data-reference signatures,
/// rebuilding on section loading or geometry changes. Do not draw unloaded sections.
/// White hover lines use separate hoverDirty tracking; hover changes rebuild only those lines, leaving bands untouched.
/// </summary>
public sealed class RoadFocusOverlay : MonoBehaviour
{
    public static RoadFocusOverlay Instance { get; private set; }

    private const float CheckInterval = .5f, SafetyInterval = 5f; // Geometry-check interval and forced-rebuild interval even without changes.
    private const int GroupSize = 32;
    private const float BorderPixels = 14f, RibbonPixels = 9f, HoverPixels = 3f;
    private const float MinPointSpacing = .5f; // Decimate points closer than this (m).
    private static readonly Color BorderColor = new Color(.04f, .05f, .07f, 1f);
    private static readonly Color HoverColor = new Color(1f, 1f, 1f, .95f);
    private const float PreviewAlpha = .45f;

    private struct Segment { public Vector3 a, b; public Color color; public bool bordered; }
    /// <summary>Bounding box of GroupSize consecutive segments, used for frustum culling.</summary>
    private struct SegmentGroup { public int start, count; public Bounds bounds; }

    private readonly HashSet<int> focusIds = new HashSet<int>();
    private readonly HashSet<int> memberIds = new HashSet<int>();
    private readonly HashSet<int> previewIds = new HashSet<int>();
    private readonly HashSet<int> watchedIds = new HashSet<int>();
    private readonly List<Segment> segments = new List<Segment>();
    private readonly List<SegmentGroup> groups = new List<SegmentGroup>();
    private readonly List<Segment> hoverSegments = new List<Segment>();
    private readonly Plane[] planes = new Plane[6];

    private int focusRoadId = Constants.VALUE_NONE;
    private Color focusColor = Color.white;
    private Color memberColor = Color.white;
    private bool editing;
    private int hoverPathId = -1;

    private OverlayMeshRenderer border, ribbon, hover;
    private PathManager paths;
    private bool dirty, hoverDirty, terrainChanged, drawn;
    private long pathSignature, hoverSignature;
    private float nextCheck, nextSafety;
    private Vector3 cameraPosition;
    private Quaternion cameraRotation;
    private float cameraFov;
    private int screenWidth, screenHeight;

    public static RoadFocusOverlay Ensure()
    {
        if (Instance == null) new GameObject("RoadFocusOverlay").AddComponent<RoadFocusOverlay>();
        return Instance;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        // Render in front of public transport bands (3200/3201).
        border = CreateRenderer("RoadFocusOutline", 3202);
        ribbon = CreateRenderer("RoadFocusRibbon", 3203);
        hover = CreateRenderer("RoadFocusHover", 3204);
        TerrainManager.OnTerrainTileReady += OnTerrainReady;
    }

    private OverlayMeshRenderer CreateRenderer(string name, int queue)
    {
        var renderer = OverlayMeshRenderer.Create(name, alwaysOnTop: true, parent: transform);
        renderer.Material.renderQueue = queue;
        return renderer;
    }

    private void OnTerrainReady(Vector2Int cell, Terrain terrain) => terrainChanged = true;

    /// <summary>Replace road-detail focus, using full replacement on every message.</summary>
    public void SetFocus(int roadId, Color color, IEnumerable<int> pathIds)
    {
        focusRoadId = roadId;
        focusColor = color;
        Replace(focusIds, pathIds);
        dirty = true;
    }

    /// <summary>Clear focus; if roadId is positive, clear only when it matches the currently focused road.</summary>
    public void ClearFocus(int roadId = Constants.VALUE_NONE)
    {
        if (roadId > 0 && roadId != focusRoadId) return;
        focusRoadId = Constants.VALUE_NONE;
        focusIds.Clear();
        dirty = true;
    }

    /// <summary>Replace section-edit members and candidates with complete sets rather than deltas.</summary>
    public void SetEditLayers(Color color, IEnumerable<int> members, IEnumerable<int> preview)
    {
        editing = true;
        memberColor = color;
        Replace(memberIds, members);
        Replace(previewIds, preview);
        previewIds.ExceptWith(memberIds);
        dirty = true;
    }

    /// <summary>Clear section-edit bands while retaining focus.</summary>
    public void ClearEditLayers()
    {
        editing = false;
        memberIds.Clear();
        previewIds.Clear();
        hoverPathId = -1;
        dirty = true;
    }

    /// <summary>Show the hovered section as a thin white line during section editing; -1 clears it.</summary>
    public void SetHoverPath(int pathId)
    {
        if (hoverPathId == pathId) return;
        hoverPathId = pathId;
        hoverDirty = true;
    }

    /// <summary>Clear a section's hover line when the cursor leaves, doing nothing if a new section's hover arrived first.</summary>
    public void ClearHoverPath(int pathId)
    {
        if (hoverPathId == pathId) SetHoverPath(-1);
    }

    public void ClearAll()
    {
        focusRoadId = Constants.VALUE_NONE;
        focusIds.Clear();
        ClearEditLayers();
    }

    private static void Replace(HashSet<int> target, IEnumerable<int> source)
    {
        target.Clear();
        if (source == null) return;
        foreach (int id in source) if (id > 0) target.Add(id);
    }

    private bool HasAnything => editing ? memberIds.Count > 0 || previewIds.Count > 0 || hoverPathId > 0 : focusIds.Count > 0;

    private void LateUpdate()
    {
        Camera cam = Camera.main;
        bool visible = HasAnything && cam != null
            && GameStateManager.Instance != null && GameStateManager.Instance.IsWorldVisible;
        if (!visible)
        {
            if (drawn)
            {
                border.ClearMesh(); ribbon.ClearMesh(); hover.ClearMesh();
                drawn = false;
            }
            return;
        }
        if (paths == null) paths = FindAnyObjectByType<PathManager>();
        if (paths == null) return;

        float now = Time.unscaledTime;
        bool hoverChanged = hoverDirty;
        bool due = dirty || !drawn || now >= nextSafety;
        if (!due && now >= nextCheck)
        {
            nextCheck = now + CheckInterval;
            // Known section revisions include terrain signatures; rebuild on terrain arrival only if some section revisions are unknown.
            due = PathSignature(out bool uncertain) != pathSignature || (terrainChanged && uncertain);
            hoverChanged |= HoverSignature() != hoverSignature;
        }
        if (due)
        {
            BuildGeometry();
            pathSignature = PathSignature(out _);
            hoverSignature = HoverSignature();
            dirty = hoverDirty = terrainChanged = false;
            nextSafety = now + SafetyInterval;
            nextCheck = now + CheckInterval;
            Draw(cam);
            return;
        }
        if (hoverChanged) RebuildHover();
        if (cam.transform.position != cameraPosition || cam.transform.rotation != cameraRotation
            || cam.fieldOfView != cameraFov || Screen.width != screenWidth || Screen.height != screenHeight)
        {
            Draw(cam);
        }
        else if (hoverChanged)
        {
            DrawHover(cam);
        }
    }

    private void RebuildHover()
    {
        hoverDirty = false;
        hoverSegments.Clear();
        if (editing && hoverPathId > 0) AddPath(hoverPathId, HoverColor, false, hoverSegments);
        hoverSignature = HoverSignature();
    }

    private void CollectWatched()
    {
        watchedIds.Clear();
        if (editing)
        {
            watchedIds.UnionWith(memberIds);
            watchedIds.UnionWith(previewIds);
        }
        else
        {
            watchedIds.UnionWith(focusIds);
        }
    }

    /// <summary>
    /// Order-independent signature combining data references, version, and rendered-geometry revisions for sections in the main bands.
    /// Set uncertain if any revision is -1 (outside cache), causing terrain arrival to count as a change.
    /// Exclude hovered sections so hover transitions do not rebuild bands; HoverSignature tracks hover lines separately.
    /// </summary>
    private long PathSignature(out bool uncertain)
    {
        uncertain = false;
        ulong sum = 0;
        unchecked
        {
            foreach (int id in watchedIds) sum += PathTerm(id, ref uncertain);
            return (long)sum;
        }
    }

    private long HoverSignature()
    {
        if (!editing || hoverPathId <= 0) return 0;
        bool uncertain = false;
        return (long)PathTerm(hoverPathId, ref uncertain);
    }

    private ulong PathTerm(int id, ref bool uncertain)
    {
        unchecked
        {
            ulong term = (ulong)id;
            var data = paths.GetPathData(id);
            if (data != null)
            {
                long revision = paths.GetPathGeometryRevision(id);
                if (revision < 0) uncertain = true;
                term = Mix(term ^ Mix((ulong)data.version << 32 ^ (uint)RuntimeHelpers.GetHashCode(data)));
                term = Mix(term ^ Mix((ulong)revision));
            }
            return Mix(term);
        }
    }

    private static ulong Mix(ulong x)
    {
        unchecked
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }

    private void BuildGeometry()
    {
        CollectWatched();
        segments.Clear();
        groups.Clear();
        hoverSegments.Clear();
        if (editing)
        {
            Color previewColor = memberColor;
            previewColor.a = PreviewAlpha;
            AddPaths(previewIds, previewColor, false, segments);
            AddPaths(memberIds, memberColor, true, segments);
            if (hoverPathId > 0) AddPath(hoverPathId, HoverColor, false, hoverSegments);
        }
        else
        {
            AddPaths(focusIds, focusColor, true, segments);
        }
        for (int start = 0; start < segments.Count; start += GroupSize)
        {
            int count = Mathf.Min(GroupSize, segments.Count - start);
            var bounds = new Bounds(segments[start].a, Vector3.zero);
            for (int i = start; i < start + count; i++) { bounds.Encapsulate(segments[i].a); bounds.Encapsulate(segments[i].b); }
            groups.Add(new SegmentGroup { start = start, count = count, bounds = bounds });
        }
    }

    private void AddPaths(HashSet<int> ids, Color color, bool bordered, List<Segment> target)
    {
        foreach (int id in ids) AddPath(id, color, bordered, target);
    }

    /// <summary>Queue segments from the uncut centerline through intersections, preventing gaps in bands at intersections.</summary>
    private void AddPath(int id, Color color, bool bordered, List<Segment> target)
    {
        if (!paths.TryGetUntrimmedPolylineShared(id, out var points)) return;
        Vector3 previous = points[0];
        for (int i = 1; i < points.Count; i++)
        {
            Vector3 point = points[i];
            bool last = i == points.Count - 1;
            if (!last && (point - previous).sqrMagnitude < MinPointSpacing * MinPointSpacing) continue;
            if ((point - previous).sqrMagnitude > 1e-6f)
                target.Add(new Segment { a = previous, b = point, color = color, bordered = bordered });
            previous = point;
        }
    }

    /// <summary>Redraw both main bands and hover lines.</summary>
    private void Draw(Camera cam)
    {
        border.BeginFrame(); ribbon.BeginFrame();
        GeometryUtility.CalculateFrustumPlanes(cam, planes);
        Vector3 eye = cam.transform.position;
        // Use OverlayMeshRenderer's conversion, distance x tan(fov/2) / half screen height, to pad bounds for band outer edges.
        float perPixel = Mathf.Tan(cam.fieldOfView * .5f * Mathf.Deg2Rad) / Mathf.Max(1, Screen.height * .5f);
        foreach (var group in groups)
        {
            var bounds = group.bounds;
            float reach = Vector3.Distance(eye, bounds.center) + bounds.extents.magnitude;
            bounds.Expand(2 * BorderPixels * reach * perPixel);
            if (!GeometryUtility.TestPlanesAABB(planes, bounds)) continue;
            int end = Mathf.Min(group.start + group.count, segments.Count);
            for (int i = group.start; i < end; i++)
            {
                var segment = segments[i];
                if (segment.bordered) border.AddSolidLine(segment.a, segment.b, BorderColor, BorderPixels);
                ribbon.AddSolidLine(segment.a, segment.b, segment.color, RibbonPixels);
            }
        }
        border.EndFrame(cam); ribbon.EndFrame(cam);
        DrawHover(cam);
        drawn = true;
        cameraPosition = cam.transform.position; cameraRotation = cam.transform.rotation; cameraFov = cam.fieldOfView;
        screenWidth = Screen.width; screenHeight = Screen.height;
    }

    private void DrawHover(Camera cam)
    {
        hover.BeginFrame();
        foreach (var segment in hoverSegments) hover.AddSolidLine(segment.a, segment.b, segment.color, HoverPixels);
        hover.EndFrame(cam);
    }

    private void OnDestroy()
    {
        TerrainManager.OnTerrainTileReady -= OnTerrainReady;
        foreach (var renderer in new[] { border, ribbon, hover })
        {
            if (renderer == null) continue;
            Destroy(renderer.GetComponent<MeshFilter>().sharedMesh);
            Destroy(renderer.GetComponent<MeshRenderer>().sharedMaterial);
        }
        if (Instance == this) Instance = null;
    }
}
