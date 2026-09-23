using System;
using System.Collections;
using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>索道の循環演出。経済時間・交通量・個体GameObjectを持たず、周辺の搬器だけを描く。</summary>
public sealed class RopewayCabinManager : MonoBehaviour
{
    const float Radius = 480f;
    readonly Dictionary<int, State> states = new();
    readonly List<Matrix4x4> cabinMatrices = new();
    readonly List<Matrix4x4> rotorMatrices = new();
    readonly List<Matrix4x4> supportMatrices = new();
    readonly Matrix4x4[] batch = new Matrix4x4[511];
    PropRenderModel cabin, rotor, support;
    PathManager paths;
    Coroutine worker;
    int revision = -1, session = -1;
    double clock;
    float nextScan;
    Vector3 center;

    sealed class State
    {
        public RopewayNetwork.Route Route;
        public RopewayGeometry Geometry;
        public double Phase;
        public readonly Dictionary<int, Presentation> Visible = new();
    }

    sealed class Presentation
    {
        public GameObject Source, Cable;
        public Renderer[] Original;
        public bool[] Previous;
        public bool Suppressed;

        public void Restore()
        {
            if (!Suppressed) return;
            var presentation = Source != null ? Source.GetComponent<PathPresentation>() : null;
            for (int i = 0; i < Original.Length; i++) if (Original[i] != null)
            {
                if (presentation != null) presentation.SetRendererForceOff(Original[i], Previous[i]);
                else Original[i].forceRenderingOff = Previous[i];
            }
            Suppressed = false;
        }
        public void Suppress()
        {
            for (int i = 0; i < Original.Length; i++) if (Original[i] != null)
            { Previous[i] = Original[i].forceRenderingOff; Original[i].forceRenderingOff = true; }
            Suppressed = true;
        }
        public void Dispose() { Restore(); if (Cable != null) UnityEngine.Object.Destroy(Cable); }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<RopewayCabinManager>() == null)
            new GameObject("RopewayCabinManager").AddComponent<RopewayCabinManager>();
    }

    void Update()
    {
        var store = RopewayNetworkStore.Ensure();
        if (session != store.Session) { Clear(); session = store.Session; clock = 0; }
        if (GameStateManager.Instance == null || !GameStateManager.Instance.IsGameStarted()) { Clear(); return; }
        clock += Time.unscaledDeltaTime;
        if (paths == null) paths = FindAnyObjectByType<PathManager>();
        if (paths == null || Camera.main == null || CameraCellChecker.Instance == null || MasterDataStore.Instance == null) return;
        center = CameraCellChecker.Instance.CurrentWorldPosition;
        if (DriveMode.TryGetLoadCenter(out var rideCenter)) center = rideCenter;
        if (worker == null && Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + .5f;
            // 0.5秒ごとに起動し直す常駐の走査なので、初期描画の完了判定を待たせない。
            worker = RenderWorkScheduler.Start(this, Refresh(store), "ropeway", () => false);
        }
    }

    IEnumerator Refresh(RopewayNetworkStore store)
    {
        try
        {
            if (revision != store.Revision)
            {
                int expected = store.Revision;
                var next = new Dictionary<int, State>();
                foreach (var route in RopewayNetwork.Build(store.Paths))
                {
                    var geometry = RopewayGeometry.Build(route, p => paths.CalculatePathPoints(p, false));
                    if (geometry != null)
                    {
                        var state = new State { Route = route, Geometry = geometry };
                        if (states.TryGetValue(route.Id, out var prior)) PreservePhase(prior, state);
                        next.Add(route.Id, state);
                    }
                    yield return RenderWorkScheduler.Continue;
                    if (expected != store.Revision) yield break;
                }
                ClearStates();
                foreach (var pair in next) states.Add(pair.Key, pair.Value);
                revision = expected;
            }
            if (states.Count == 0 || !LoadModels()) yield break;
            var nearby = new HashSet<int>();
            foreach (var path in paths.GetPathsNearCircle(center, Radius))
                if (RopewayNetwork.Eligible(path)) nearby.Add(path.path_id);
            foreach (var state in states.Values)
            {
                bool changed = false;
                foreach (var link in state.Route.Links)
                {
                    if (!nearby.Contains(link.Path.path_id)) continue;
                    var current = paths.CalculatePathPoints(link.Path, false);
                    if (!SamePoints(current, state.Geometry.SourcePoints[link.Path.path_id])) { changed = true; break; }
                }
                // 地形の詳細化はパスversionを変えないため、近傍だけ実際の描画点列を比較する。
                if (changed)
                {
                    var geometry = RopewayGeometry.Build(state.Route, p => paths.CalculatePathPoints(p, false));
                    if (geometry != null)
                    {
                        var replacement = new State { Geometry = geometry };
                        PreservePhase(state, replacement);
                        foreach (var item in state.Visible.Values) item.Dispose();
                        state.Visible.Clear(); state.Geometry = geometry; state.Phase = replacement.Phase;
                    }
                }
                var keep = new HashSet<int>();
                foreach (var link in state.Route.Links)
                {
                    int id = link.Path.path_id;
                    if (!nearby.Contains(id)) continue;
                    var source = paths.GetPathObject(id);
                    if (!CanPresent(source, paths.GetPathData(id), id)) continue;
                    keep.Add(id);
                    if (!state.Visible.TryGetValue(id, out var view) || view.Source != source)
                    {
                        view?.Dispose();
                        state.Visible[id] = CreatePresentation(state.Geometry, source, id);
                        yield return RenderWorkScheduler.Continue;
                    }
                }
                foreach (int id in new List<int>(state.Visible.Keys)) if (!keep.Contains(id))
                { state.Visible[id].Dispose(); state.Visible.Remove(id); }
                yield return RenderWorkScheduler.Continue;
            }
        }
        finally { worker = null; }
    }

    static bool SamePoints(List<Vector3> a, List<Vector3> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if ((a[i] - b[i]).sqrMagnitude > .0001f) return false;
        return true;
    }

    void PreservePhase(State previous, State next)
    {
        previous.Geometry.Sample(previous.Geometry.Distance(clock, 0, previous.Phase), out var position, out _);
        next.Phase = next.Geometry.Project(position) - clock * RopewayGeometry.Speed;
    }

    bool LoadModels()
    {
        if (ModelManager.Instance == null) return false;
        PropRenderModel Load(string part) => new PropRenderModel(ModelManager.Instance.GetModel(part, true));
        if (cabin == null && ModelManager.Instance.GetModel("Models/Paths/p601/cabin", true) != null)
            cabin = Load("Models/Paths/p601/cabin");
        if (rotor == null && ModelManager.Instance.GetModel("Models/Paths/p602/terminal_rotor", true) != null)
            rotor = Load("Models/Paths/p602/terminal_rotor");
        if (support == null && ModelManager.Instance.GetModel("Models/Paths/p602/terminal_support", true) != null)
            support = Load("Models/Paths/p602/terminal_support");
        return cabin?.normal.Count > 0 && rotor?.normal.Count > 0 && support?.normal.Count > 0;
    }

    Presentation CreatePresentation(RopewayGeometry geometry, GameObject source, int path)
    {
        var root = new GameObject($"RopewayCable_{path}");
        root.transform.SetParent(transform, false);
        var mesh = new PathStructureMesh(root, "Cable", new[] { RoadPath.GetOrCreateRoadMaterialByName("PATH_METAL") });
        foreach (var leg in geometry.Legs) if (leg.PathId == path)
        {
            var points = leg.Line.points;
            for (int i = 1; i < points.Length; i++) mesh.Bar(points[i - 1], points[i], RopewayGeometry.CableRadius);
        }
        mesh.Finish(); root.SetActive(false);
        var renderers = new List<Renderer>();
        foreach (var renderer in source.GetComponentsInChildren<Renderer>(true))
            if (renderer.name.EndsWith("/body", StringComparison.Ordinal)) renderers.Add(renderer);
        return new Presentation { Source = source, Cable = root, Original = renderers.ToArray(), Previous = new bool[renderers.Count] };
    }

    public static bool CanPresent(GameObject source, PathSummary data, int id)
        => source != null && source.activeInHierarchy && RopewayNetwork.Eligible(data)
        && DrawLodInfo.GetDrawLod(source, DrawLod.Far) >= DrawLod.Normal
        && (source.GetComponent<PathPresentation>()?.IsReady ?? true)
        && !ObjectVisibilityFilter.IsHidden(InputObjectType.PATH, id);

    void LateUpdate()
    {
        cabinMatrices.Clear(); rotorMatrices.Clear(); supportMatrices.Clear();
        bool enabled = GameStateManager.Instance != null && GameStateManager.Instance.IsWorldVisible
            && (VisualModeHandler.Instance == null || VisualModeHandler.Instance.GetMode(InputObjectType.PATH) != VisualMode.MODE_HIDDEN);
        foreach (var state in states.Values)
        {
            if (state.Visible.Count == 0) continue;
            bool valid = enabled && RouteCurrent(state.Route);
            foreach (var pair in state.Visible)
            {
                var view = pair.Value;
                view.Restore();
                bool show = valid && CanPresent(view.Source, paths.GetPathData(pair.Key), pair.Key);
                view.Cable.SetActive(show);
                if (show) view.Suppress();
            }
            if (!valid) continue;
            foreach (var leg in state.Geometry.Legs)
            {
                if (!state.Visible.TryGetValue(leg.PathId, out var view) || !view.Cable.activeSelf) continue;
                AddCabins(state, leg);
            }
            foreach (var terminal in state.Geometry.Terminals)
            {
                if (!state.Visible.TryGetValue(terminal.PathId, out var view) || !view.Cable.activeSelf) continue;
                if ((terminal.Center - center).sqrMagnitude > Radius * Radius) continue;
                supportMatrices.Add(Matrix4x4.TRS(terminal.Center, terminal.Rotation, Vector3.one));
                float angle = (float)(clock * RopewayGeometry.Speed / RopewayGeometry.CableOffset * Mathf.Rad2Deg % 360);
                rotorMatrices.Add(Matrix4x4.TRS(terminal.Center, terminal.Rotation * Quaternion.Euler(0, -angle, 0), Vector3.one));
            }
        }
        Draw(cabin, cabinMatrices); Draw(rotor, rotorMatrices); Draw(support, supportMatrices);
    }

    bool RouteCurrent(RopewayNetwork.Route route)
    {
        foreach (var link in route.Links)
        {
            var local = paths.GetPathData(link.Path.path_id);
            if (local != null && (!RopewayNetwork.Eligible(local) || local.version != link.Path.version)) return false;
        }
        return true;
    }

    void AddCabins(State state, RopewayGeometry.Leg leg)
    {
        var g = state.Geometry;
        bool reverseLoop = g.Closed && leg.Start >= g.ForwardLength;
        float start = reverseLoop ? g.ForwardLength : 0;
        float length = g.Closed ? reverseLoop ? g.Length - g.ForwardLength : g.ForwardLength : g.Length;
        int count = g.Closed ? g.CabinCount / 2 : g.CabinCount;
        double spacing = (double)length / count;
        double phase = ((clock * RopewayGeometry.Speed + state.Phase) % spacing + spacing) % spacing;
        int first = Math.Max(0, (int)Math.Ceiling((leg.Start - start - phase) / spacing));
        double end = leg.Start + leg.Line.Length;
        // 可視区間に入る番号だけを直接求め、長い索道の画面外の搬器を列挙しない。
        for (int i = first; i < count; i++)
        {
            double distance = start + phase + i * spacing;
            if (distance >= end) break;
            leg.Line.Sample((float)(distance - leg.Start), true, out var p, out var t);
            if ((p - center).sqrMagnitude > Radius * Radius) continue;
            t.y = 0;
            if (t.sqrMagnitude < .001f) continue;
            cabinMatrices.Add(Matrix4x4.TRS(p, Quaternion.LookRotation(t, Vector3.up), Vector3.one));
        }
    }

    void Draw(PropRenderModel model, List<Matrix4x4> matrices)
    {
        if (model == null || Camera.main == null) return;
        foreach (var part in model.normal)
        for (int start = 0; start < matrices.Count; start += batch.Length)
        {
            int count = Math.Min(batch.Length, matrices.Count - start);
            Bounds bounds = default;
            for (int i = 0; i < count; i++)
            {
                batch[i] = matrices[start + i] * part.local;
                var item = Utils.TransformBounds(part.mesh.bounds, batch[i]);
                if (i == 0) bounds = item; else bounds.Encapsulate(item);
            }
            var parameters = new RenderParams(part.material) { camera = Camera.main, worldBounds = bounds,
                shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
            Graphics.RenderMeshInstanced(parameters, part.mesh, part.submesh, batch, count);
        }
    }

    void ClearStates()
    {
        foreach (var state in states.Values) foreach (var view in state.Visible.Values) view.Dispose();
        states.Clear();
    }
    void Clear()
    {
        if (worker != null) RenderWorkScheduler.Stop(this, worker);
        worker = null; ClearStates(); revision = -1; nextScan = 0;
    }
    void OnDisable() => Clear();
    void OnDestroy() { Clear(); cabin?.Dispose(); rotor?.Dispose(); support?.Dispose(); }
}
