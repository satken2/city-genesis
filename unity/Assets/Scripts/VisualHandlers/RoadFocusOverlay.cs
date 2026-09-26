using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

/// <summary>
/// 道路(名前付き道路)の区間を、中心線に沿った道路色の帯で最前面に描くオーバーレイ。
/// 公共交通ハイライト(TransitOverlayManager)と同じく OverlayMeshRenderer の ZTest Always で
/// 地形・建物に埋もれず、画面上の幅はズームによらず一定にする。
///
/// 描く集合は2系統ある。
/// - 強調(focus): 道路詳細パネルが開いている道路の区間(world:set_road_focus)
/// - 区間編集: 区間編集モードの所属区間と、直進方向の自動延長で追加候補になった区間(薄い帯)
/// 区間編集中は強調を描かない(強調の集合はパネルが取り直すまで古いままなので、外した区間が
/// 残って見えるのを防ぐ)。編集を終えると強調が戻る。
///
/// 形状は区間の版(PathManager.GetPathGeometryRevision)とデータの参照を署名にして定期的に比べ、
/// 区間のロード・形状の変化で作り直す。未ロードの区間は描かない。
/// </summary>
public sealed class RoadFocusOverlay : MonoBehaviour
{
    public static RoadFocusOverlay Instance { get; private set; }

    private const float CheckInterval = .5f, SafetyInterval = 5f; // 形状変化の確認間隔と、変化が無くても作り直す間隔
    private const int GroupSize = 32;
    private const float BorderPixels = 14f, RibbonPixels = 9f, HoverPixels = 3f;
    private const float MinPointSpacing = .5f; // これより近い点は間引く(m)
    private static readonly Color BorderColor = new Color(.04f, .05f, .07f, 1f);
    private static readonly Color HoverColor = new Color(1f, 1f, 1f, .95f);
    private const float PreviewAlpha = .45f;

    private struct Segment { public Vector3 a, b; public Color color; public bool bordered; }
    /// <summary>連続する GroupSize 本の線分の外接箱。視錐台カリングの単位にする。</summary>
    private struct SegmentGroup { public int start, count; public Bounds bounds; }

    private readonly HashSet<int> focusIds = new HashSet<int>();
    private readonly HashSet<int> memberIds = new HashSet<int>();
    private readonly HashSet<int> previewIds = new HashSet<int>();
    private readonly HashSet<int> watchedIds = new HashSet<int>();
    private readonly List<Segment> segments = new List<Segment>();
    private readonly List<SegmentGroup> groups = new List<SegmentGroup>();
    private readonly List<Segment> hoverSegments = new List<Segment>();
    private readonly Plane[] planes = new Plane[6];

    private Color focusColor = Color.white;
    private Color memberColor = Color.white;
    private bool editing;
    private int hoverPathId = -1;

    private OverlayMeshRenderer border, ribbon, hover;
    private PathManager paths;
    private bool dirty, terrainChanged, drawn;
    private long pathSignature;
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
        // 公共交通の帯(3200/3201)より手前に描く。
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

    /// <summary>道路詳細パネルの強調を置き換える(送られるたびに全置換)。</summary>
    public void SetFocus(Color color, IEnumerable<int> pathIds)
    {
        focusColor = color;
        Replace(focusIds, pathIds);
        dirty = true;
    }

    public void ClearFocus()
    {
        focusIds.Clear();
        dirty = true;
    }

    /// <summary>区間編集モードの所属区間と追加候補を置き換える(差分ではなく全集合)。</summary>
    public void SetEditLayers(Color color, IEnumerable<int> members, IEnumerable<int> preview)
    {
        editing = true;
        memberColor = color;
        Replace(memberIds, members);
        Replace(previewIds, preview);
        previewIds.ExceptWith(memberIds);
        dirty = true;
    }

    /// <summary>区間編集の帯を消す。強調(focus)は残す。</summary>
    public void ClearEditLayers()
    {
        editing = false;
        memberIds.Clear();
        previewIds.Clear();
        hoverPathId = -1;
        dirty = true;
    }

    /// <summary>区間編集モードでカーソルが乗っている区間を白い細線で示す(-1で解除)。</summary>
    public void SetHoverPath(int pathId)
    {
        if (hoverPathId == pathId) return;
        hoverPathId = pathId;
        dirty = true;
    }

    /// <summary>カーソルが離れた区間のホバー線を消す(次の区間のホバーが先に届いていれば何もしない)。</summary>
    public void ClearHoverPath(int pathId)
    {
        if (hoverPathId == pathId) SetHoverPath(-1);
    }

    public void ClearAll()
    {
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
        bool due = dirty || !drawn || now >= nextSafety;
        if (!due && now >= nextCheck)
        {
            nextCheck = now + CheckInterval;
            // 版が分かる区間は地形署名込みで比べられるので、地形の到着で作り直すのは版が不明な区間があるときだけ。
            due = PathSignature(out bool uncertain) != pathSignature || (terrainChanged && uncertain);
        }
        if (due)
        {
            BuildGeometry();
            pathSignature = PathSignature(out _);
            dirty = terrainChanged = false;
            nextSafety = now + SafetyInterval;
            nextCheck = now + CheckInterval;
            Draw(cam);
        }
        else if (cam.transform.position != cameraPosition || cam.transform.rotation != cameraRotation
            || cam.fieldOfView != cameraFov || Screen.width != screenWidth || Screen.height != screenHeight)
        {
            Draw(cam);
        }
    }

    private void CollectWatched()
    {
        watchedIds.Clear();
        if (editing)
        {
            watchedIds.UnionWith(memberIds);
            watchedIds.UnionWith(previewIds);
            if (hoverPathId > 0) watchedIds.Add(hoverPathId);
        }
        else
        {
            watchedIds.UnionWith(focusIds);
        }
    }

    /// <summary>
    /// 描く区間について、データの参照・version・描画形状の版をまとめた順序非依存の署名。
    /// 形状の版が -1(キャッシュ外)の区間があれば uncertain を立て、地形の到着を変化として扱わせる。
    /// </summary>
    private long PathSignature(out bool uncertain)
    {
        uncertain = false;
        ulong sum = 0;
        unchecked
        {
            foreach (int id in watchedIds)
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
                sum += Mix(term);
            }
            return (long)sum;
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

    /// <summary>未切断の中心線(交差点の中まで通す)を線分に分けて積む。帯が交差点で途切れないようにするため。</summary>
    private void AddPath(int id, Color color, bool bordered, List<Segment> target)
    {
        if (!paths.TryGetPathPolyline(id, out var points, false)) return;
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

    private void Draw(Camera cam)
    {
        border.BeginFrame(); ribbon.BeginFrame(); hover.BeginFrame();
        GeometryUtility.CalculateFrustumPlanes(cam, planes);
        Vector3 eye = cam.transform.position;
        // OverlayMeshRenderer と同じ換算(距離×tan(fov/2)/画面高の半分)で、帯の外縁が入る余白を取る。
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
        foreach (var segment in hoverSegments) hover.AddSolidLine(segment.a, segment.b, segment.color, HoverPixels);
        border.EndFrame(cam); ribbon.EndFrame(cam); hover.EndFrame(cam);
        drawn = true;
        cameraPosition = cam.transform.position; cameraRotation = cam.transform.rotation; cameraFov = cam.fieldOfView;
        screenWidth = Screen.width; screenHeight = Screen.height;
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
