using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using GeneratedModels;
using UnityEngine;

/// <summary>
/// Whole-city copy of the core's buildings, paths, nodes, parcels, props, and road travel times.
/// Managers read resident cells from here instead of asking the core per range, so the camera can move anywhere
/// (including while an epoch runs) without requests. Only CellDataLoader.ReloadAllData synchronizes with the core
/// (load, committed edits, epoch completion, session:reload); nothing here refetches on a timer.
///
/// Synchronization: /…/sync returns every ID with its version and the list fields that change without a version bump.
/// Refetch only changed or new IDs through the list API's ids query, drop absent IDs, and overwrite the other fields
/// from the columns. Nodes, props, and travel times are small or change every epoch, so take them whole.
/// Records are never modified in place (managers compare old and new objects); replacements are new objects.
///
/// Until the first synchronization completes, EnsureRect fetches the requested range through the list APIs,
/// so the first view appears as fast as before the whole city arrives.
/// </summary>
public sealed class CityDataCache : MonoBehaviour
{
    public static CityDataCache Instance { get; private set; }

    /// <summary>Whether the whole city has been synchronized at least once since the last Clear.</summary>
    public bool IsComplete { get; private set; }
    /// <summary>Whether the cache matches the core with no synchronization running or requested. Epochs start only then.</summary>
    public bool IsSettled => IsComplete && syncCoroutine == null && completedTicket == requestedTicket;
    /// <summary>Advanced whenever records change, for callers that cache derived results.</summary>
    public int Revision { get; private set; }
    public float MotionActiveHours { get; private set; }

    private const int IdsPerRequest = 500;
    private const int MotionPerRequest = 4096;   // Upper bound accepted by POST /paths/motion.

    private readonly SpatialTable<BuildingSummary> buildings = new(b => b.agent_id, b => b.version, PointBounds<BuildingSummary>(b => b.centerPointArray));
    private readonly SpatialTable<ConstructionPathSummary> paths = new(p => p.path_id, p => p.version, PathBounds);
    private readonly SpatialTable<ParcelSummary> parcels = new(p => p.parcel_id, p => p.version, PolygonBounds<ParcelSummary>(p => p.GetPolygonArray()));
    private readonly SpatialTable<NodeData> nodes = new(n => n.node_id, n => 0, NodeBounds);
    private readonly SpatialTable<PropScatterItem> scatters = new(s => s.prop_scatter_id, s => s.version, ScatterBounds);
    private readonly SpatialTable<PropInstanceItem> instances = new(i => i.prop_instance_id, i => i.version, i => new Rect(i.x, i.z, 0, 0));
    private readonly List<(PropObstacleItem item, Rect bounds)> obstacles = new();
    private readonly Dictionary<int, RoadMotionData> motion = new();

    private IpcClient boundIpc;
    /// <summary>epoch:prepare awaiting the reply. The UI starts the epoch on epoch:ready, so reply only once the cache matches the core.</summary>
    private string pendingEpochId;
    private int requestedTicket, completedTicket;
    private int generation;
    private Coroutine syncCoroutine;
    private float retryAt;

    public static CityDataCache Ensure()
    {
        if (Instance == null)
        {
            var go = new GameObject("CityDataCache");
            DontDestroyOnLoad(go);
            go.AddComponent<CityDataCache>();
        }
        return Instance;
    }

    private void Awake() => Instance = this;

    private void OnDestroy()
    {
        if (boundIpc != null) boundIpc.OnEpochPrepare -= OnEpochPrepare;
        if (Instance == this) Instance = null;
    }

    private void OnEpochPrepare(string id) => pendingEpochId = id;

    private void Update()
    {
        if (boundIpc != IpcClient.Instance)
        {
            if (boundIpc != null) boundIpc.OnEpochPrepare -= OnEpochPrepare;
            boundIpc = IpcClient.Instance;
            if (boundIpc != null) boundIpc.OnEpochPrepare += OnEpochPrepare;
        }
        if (pendingEpochId != null && IsSettled)
        {
            boundIpc?.EmitEpochReady(pendingEpochId);
            pendingEpochId = null;
        }
        if (syncCoroutine == null && completedTicket != requestedTicket && Time.unscaledTime >= retryAt
            && GameStateManager.Instance != null && GameStateManager.Instance.IsGameStarted())
            syncCoroutine = RenderWorkScheduler.Start(this, Sync(requestedTicket, generation), "CityDataCache", () => false);
    }

    /// <summary>Request synchronization with the core. Returns a ticket for IsSyncedThrough.</summary>
    public int RequestSync() => ++requestedTicket;

    public bool IsSyncedThrough(int ticket) => completedTicket >= ticket && syncCoroutine == null;

    /// <summary>Discard everything (session switch or full reload). Synchronize again only when requested.</summary>
    public void Clear()
    {
        generation++;
        if (syncCoroutine != null) RenderWorkScheduler.Stop(this, syncCoroutine);
        syncCoroutine = null;
        buildings.Clear(); paths.Clear(); parcels.Clear(); nodes.Clear(); scatters.Clear(); instances.Clear();
        obstacles.Clear(); motion.Clear();
        MotionActiveHours = 0;
        IsComplete = false;
        completedTicket = requestedTicket;
        retryAt = 0;
        Revision++;
    }

    // ================================================================
    // Queries (same range semantics as the core's bbox queries)
    // ================================================================

    /// <summary>Buildings whose center lies in the rectangle (the core indexes buildings by center).</summary>
    public List<BuildingSummary> BuildingsIn(Rect rect) => buildings.Query(rect);
    /// <summary>Paths whose bounds overlap the rectangle.</summary>
    public List<ConstructionPathSummary> PathsIn(Rect rect) => paths.Query(rect);
    public List<NodeData> NodesIn(Rect rect) => nodes.Query(rect);
    /// <summary>Parcels whose bounds overlap the rectangle.</summary>
    public List<ParcelSummary> ParcelsIn(Rect rect) => parcels.Query(rect);

    /// <summary>Props in the rectangle and obstacles within margin of it, as GET /props?bbox&amp;include_obstacles returns.</summary>
    public PropListResponse PropsIn(Rect rect, float obstacleMargin)
    {
        var expanded = Rect.MinMaxRect(rect.xMin - obstacleMargin, rect.yMin - obstacleMargin, rect.xMax + obstacleMargin, rect.yMax + obstacleMargin);
        var response = new PropListResponse { scatters = scatters.Query(rect), instances = instances.Query(rect), obstacles = new List<PropObstacleItem>() };
        foreach (var (item, bounds) in obstacles)
            if (SpatialTable<PropObstacleItem>.Overlaps(bounds, expanded)) response.obstacles.Add(item);
        return response;
    }

    public bool TryGetBuilding(int agentId, out BuildingSummary building) => buildings.TryGet(agentId, out building);
    public bool TryGetPath(int pathId, out ConstructionPathSummary path) => paths.TryGet(pathId, out path);
    public bool TryGetNode(int nodeId, out NodeData node) => nodes.TryGet(nodeId, out node);
    public bool TryGetMotion(int pathId, out RoadMotionData data) => motion.TryGetValue(pathId, out data);

    /// <summary>Register records the managers fetched themselves (single-object refreshes after edits), keeping the newest version.</summary>
    public void Register(BuildingSummary building) { if (buildings.PutIfNotOlder(building)) Revision++; }
    public void Register(ConstructionPathSummary path) { if (paths.PutIfNotOlder(path)) Revision++; }
    public void Register(ParcelSummary parcel) { if (parcels.PutIfNotOlder(parcel)) Revision++; }
    public void Register(NodeData node) { nodes.Put(node); Revision++; }

    // ================================================================
    // Range fetching before the first synchronization
    // ================================================================

    public enum Kind { Buildings, Paths, Nodes, Parcels, Props }

    /// <summary>
    /// Make the rectangle's records available. Once the whole city is synchronized this returns immediately;
    /// before that, fetch the range through the list APIs. Leaves the cache unchanged on failure or cancellation.
    /// </summary>
    public IEnumerator EnsureRect(Kind kind, Rect rect, Func<bool> isCanceled, Action<bool> onDone = null)
    {
        if (IsComplete) { onDone?.Invoke(true); yield break; }
        int expected = generation;
        Func<bool> canceled = () => expected != generation || (isCanceled != null && isCanceled());
        string bbox = FormattableString.Invariant($"{rect.xMin},{rect.yMin},{rect.xMax},{rect.yMax}");
        bool ok = false;
        switch (kind)
        {
            case Kind.Buildings:
                yield return FetchPages<BuildingsListResponse>(c => $"/buildings/?bbox={bbox}&limit={IdsPerRequest}&cursor={c}", r => r.next_cursor,
                    r => { foreach (var b in r.buildings) buildings.PutIfNotOlder(b); }, canceled, v => ok = v);
                break;
            case Kind.Paths:
                yield return FetchPages<PagedPathListResponse>(c => $"/paths/?bbox={bbox}&limit={IdsPerRequest}&cursor={c}", r => r.next_cursor,
                    r => { foreach (var p in r.paths) paths.PutIfNotOlder(p); }, canceled, v => ok = v);
                break;
            case Kind.Nodes:
                NodeGeometryResponse nodeResponse = null;
                yield return ApiClient.GetBulk<NodeGeometryResponse>(FormattableString.Invariant(
                    $"/nodes?start_x={rect.xMin}&end_x={rect.xMax}&start_z={rect.yMin}&end_z={rect.yMax}"), r => nodeResponse = r, isCanceled: canceled);
                if (!canceled() && nodeResponse?.nodes != null) { foreach (var n in nodeResponse.nodes) nodes.Put(n); ok = true; }
                break;
            case Kind.Parcels:
                ParcelListResponse parcelResponse = null;
                yield return ApiClient.GetBulk<ParcelListResponse>($"/parcels/?bbox={bbox}&limit={int.MaxValue}", r => parcelResponse = r, isCanceled: canceled);
                if (!canceled() && parcelResponse?.parcels != null) { foreach (var p in parcelResponse.parcels) parcels.PutIfNotOlder(p); ok = true; }
                break;
            case Kind.Props:
                PropListResponse propResponse = null;
                yield return ApiClient.GetBulk<PropListResponse>($"/props?bbox={bbox}&include_obstacles=true", r => propResponse = r, isCanceled: canceled);
                if (!canceled() && propResponse?.obstacles != null) { PutProps(propResponse, false); ok = true; }
                break;
        }
        if (ok) Revision++;
        onDone?.Invoke(ok);
    }

    /// <summary>Make the nodes available. Before the first synchronization, fetch the missing ones by ID.</summary>
    public IEnumerator EnsureNodes(List<int> nodeIds, Func<bool> isCanceled)
    {
        if (IsComplete) yield break;
        var missing = nodeIds.FindAll(id => !nodes.TryGet(id, out _));
        int expected = generation;
        for (int i = 0; i < missing.Count; i += 100)
        {
            NodeGeometryResponse response = null;
            string ids = string.Join(",", missing.GetRange(i, Math.Min(100, missing.Count - i)));
            yield return ApiClient.GetBulk<NodeGeometryResponse>($"/nodes?ids={ids}", r => response = r,
                isCanceled: () => expected != generation || (isCanceled != null && isCanceled()));
            if (expected != generation || response?.nodes == null) yield break;
            foreach (var n in response.nodes) nodes.Put(n);
            Revision++;
        }
    }

    private static IEnumerator FetchPages<T>(Func<int, string> url, Func<T, int?> nextCursor, Action<T> apply, Func<bool> canceled, Action<bool> onDone) where T : class
    {
        int cursor = 0;
        while (true)
        {
            T page = null;
            yield return ApiClient.GetBulk<T>(url(cursor), r => page = r, isCanceled: canceled);
            if (canceled() || page == null) { onDone(false); yield break; }
            int? next = nextCursor(page);
            if (next.HasValue && next.Value <= cursor) { onDone(false); yield break; }
            apply(page);
            if (!next.HasValue) break;
            cursor = next.Value;
        }
        onDone(true);
    }

    // ================================================================
    // Synchronization
    // ================================================================

    [Serializable]
    private sealed class SyncColumns
    {
        public int count;
        public byte[] id;
        public byte[] version;
        public Dictionary<string, List<object>> values;
    }

    private IEnumerator Sync(int ticket, int expected)
    {
        bool ok = false;
        try
        {
            Func<bool> canceled = () => expected != generation;
            yield return SyncTable(buildings, "buildings", ids => $"/buildings/?ids={ids}", (BuildingsListResponse r) => r.buildings, canceled, v => ok = v);
            if (ok) yield return SyncTable(paths, "paths", ids => $"/paths/?ids={ids}", (PagedPathListResponse r) => r.paths, canceled, v => ok = v);
            if (ok) yield return SyncTable(parcels, "parcels", ids => $"/parcels/?ids={ids}", (ParcelListResponse r) => r.parcels, canceled, v => ok = v);
            if (ok) yield return SyncNodes(canceled, v => ok = v);
            if (ok) yield return SyncProps(canceled, v => ok = v);
            if (ok) yield return SyncMotion(canceled, v => ok = v);
            if (canceled()) yield break;
            if (ok)
            {
                IsComplete = true;
                completedTicket = ticket;
            }
            else retryAt = Time.unscaledTime + 5f;
        }
        finally
        {
            if (expected == generation) syncCoroutine = null;
        }
    }

    private IEnumerator SyncTable<T, TList>(SpatialTable<T> table, string resource, Func<string, string> idsUrl, Func<TList, List<T>> items,
        Func<bool> canceled, Action<bool> onDone) where T : class where TList : class
    {
        SyncColumns columns = null;
        yield return ApiClient.GetBulk<SyncColumns>($"/{resource}/sync", r => columns = r, timeoutSeconds: 60, isCanceled: canceled);
        if (canceled() || columns?.id == null || columns.version == null) { onDone(false); yield break; }
        int count = columns.count;
        var ids = new int[count];
        var versions = new int[count];
        Buffer.BlockCopy(columns.id, 0, ids, 0, count * sizeof(int));
        Buffer.BlockCopy(columns.version, 0, versions, 0, count * sizeof(int));

        var stale = new List<int>();
        var listed = new HashSet<int>(ids);
        for (int i = 0; i < count; i++)
            if (!table.TryGetVersion(ids[i], out int version) || version != versions[i]) stale.Add(ids[i]);
        var fetched = new HashSet<int>();
        for (int i = 0; i < stale.Count; i += IdsPerRequest)
        {
            TList response = null;
            string query = string.Join(",", stale.GetRange(i, Math.Min(IdsPerRequest, stale.Count - i)));
            yield return ApiClient.GetBulk<TList>(idsUrl(query), r => response = r, timeoutSeconds: 60, isCanceled: canceled);
            List<T> received = response != null ? items(response) : null;
            if (canceled() || received == null) { onDone(false); yield break; }
            foreach (var item in received) { table.Put(item); fetched.Add(table.IdOf(item)); }
            Revision++;
        }
        foreach (int id in table.Ids()) if (!listed.Contains(id)) table.Remove(id);

        // Overwrite the version-free fields of the records not refetched just now, off the main thread.
        var targets = new List<(int index, T record)>();
        for (int i = 0; i < count; i++)
            if (!fetched.Contains(ids[i]) && table.TryGet(ids[i], out T record)) targets.Add((i, record));
        Dictionary<string, List<object>> values = columns.values ?? new Dictionary<string, List<object>>();
        Task<List<T>> apply = BackgroundWork.Run(() =>
        {
            var result = new List<T>(targets.Count);
            var fields = new List<KeyValuePair<string, object>>(values.Count);
            foreach (var (index, record) in targets)
            {
                fields.Clear();
                foreach (var column in values) fields.Add(new KeyValuePair<string, object>(column.Key, column.Value[index]));
                result.Add(MsgPackDecoder.CloneWithFields(record, fields));
            }
            return result;
        });
        yield return Utils.WaitTask(apply);
        if (canceled() || apply.IsFaulted) { onDone(false); yield break; }
        foreach (T record in apply.Result) table.Replace(record);
        Revision++;
        onDone(true);
    }

    private IEnumerator SyncNodes(Func<bool> canceled, Action<bool> onDone)
    {
        NodeGeometryResponse response = null;
        yield return ApiClient.GetBulk<NodeGeometryResponse>("/nodes", r => response = r, timeoutSeconds: 60, isCanceled: canceled);
        if (canceled() || response?.nodes == null) { onDone(false); yield break; }
        nodes.Clear();
        foreach (var n in response.nodes) nodes.Put(n);
        Revision++;
        onDone(true);
    }

    private IEnumerator SyncProps(Func<bool> canceled, Action<bool> onDone)
    {
        PropListResponse response = null;
        yield return ApiClient.GetBulk<PropListResponse>("/props?include_obstacles=true", r => response = r, timeoutSeconds: 60, isCanceled: canceled);
        if (canceled() || response?.obstacles == null) { onDone(false); yield break; }
        PutProps(response, true);
        Revision++;
        onDone(true);
    }

    private void PutProps(PropListResponse response, bool replaceAll)
    {
        if (replaceAll) { scatters.Clear(); instances.Clear(); obstacles.Clear(); }
        if (response.scatters != null) foreach (var s in response.scatters) scatters.Put(s);
        if (response.instances != null) foreach (var i in response.instances) instances.Put(i);
        var known = new HashSet<string>();
        foreach (var (item, _) in obstacles) known.Add(item.key);
        foreach (var o in response.obstacles)
            if (known.Add(o.key)) obstacles.Add((o, ObstacleBounds(o)));
    }

    private IEnumerator SyncMotion(Func<bool> canceled, Action<bool> onDone)
    {
        var ids = new List<int>(paths.Ids());
        var received = new Dictionary<int, RoadMotionData>();
        float activeHours = 0;
        for (int i = 0; i < ids.Count; i += MotionPerRequest)
        {
            RoadMotionResponse response = null;
            bool failed = false;
            yield return ApiClient.Post<RoadMotionResponse>("/paths/motion", ids.GetRange(i, Math.Min(MotionPerRequest, ids.Count - i)),
                r => response = r, (_, __) => failed = true);
            if (canceled() || failed || response == null) { onDone(false); yield break; }
            if (response.paths != null) foreach (var item in response.paths) if (item.Valid) received[item.path_id] = item;
            if (response.active_hours > 0) activeHours = response.active_hours;
        }
        motion.Clear();
        foreach (var item in received) motion.Add(item.Key, item.Value);
        if (activeHours > 0) MotionActiveHours = activeHours;
        onDone(true);
    }

    // ================================================================
    // Bounds (same sources as the core's R-tree entries)
    // ================================================================

    private static Func<T, Rect?> PointBounds<T>(Func<T, float[]> point) => item =>
    {
        float[] p = point(item);
        return p != null && p.Length >= 2 ? new Rect(p[0], p[1], 0, 0) : (Rect?)null;
    };

    private static Func<T, Rect?> PolygonBounds<T>(Func<T, float[][]> polygon) => item => BoundsOf(polygon(item));

    private static Rect? BoundsOf(IEnumerable<float[]> points)
    {
        if (points == null) return null;
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (float[] p in points)
        {
            if (p == null || p.Length < 2) continue;
            float x = p[0], z = p.Length >= 3 ? p[2] : p[1];
            minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
            minZ = Mathf.Min(minZ, z); maxZ = Mathf.Max(maxZ, z);
        }
        return minX <= maxX ? Rect.MinMaxRect(minX, minZ, maxX, maxZ) : (Rect?)null;
    }

    /// <summary>Path polygon (at least 3 points) or else its control points, matching the core's path R-tree bounds.</summary>
    private static Rect? PathBounds(ConstructionPathSummary p)
    {
        float[][] polygon = GeneratedModelUtils.ToFloatJaggedArray(p.polygon);
        if (polygon != null && polygon.Length >= 3) return BoundsOf(polygon);
        var points = new List<float[]>
        {
            GeneratedModelUtils.ToFloatArray(p.start_point), GeneratedModelUtils.ToFloatArray(p.start_control),
            GeneratedModelUtils.ToFloatArray(p.end_point), GeneratedModelUtils.ToFloatArray(p.end_control),
        };
        var waypoints = p.GetWaypointList();
        if (waypoints != null)
            foreach (var w in waypoints) { points.Add(w.position); points.Add(w.control_in); points.Add(w.control_out); }
        return BoundsOf(points);
    }

    private static Rect? NodeBounds(NodeData n)
    {
        if (n.position == null || n.position.Length < 2) return null;
        float z = n.position.Length >= 3 ? n.position[2] : n.position[1];
        return new Rect(n.position[0], z, 0, 0);
    }

    private static Rect? ScatterBounds(PropScatterItem s)
    {
        var points = new List<float[]>();
        void Collect(Newtonsoft.Json.Linq.JToken token)
        {
            if (token is Newtonsoft.Json.Linq.JArray array)
            {
                if (array.Count >= 2 && array[0].Type != Newtonsoft.Json.Linq.JTokenType.Array)
                    points.Add(new[] { (float)array[0], (float)array[1] });
                else foreach (var child in array) Collect(child);
            }
        }
        if (s.region != null) Collect(s.region);
        return BoundsOf(points);
    }

    private static Rect ObstacleBounds(PropObstacleItem o)
    {
        var points = new List<float[]>();
        if (o.polygon != null) foreach (var p in o.polygon) points.Add(p.ToArray());
        return BoundsOf(points) ?? new Rect();
    }

    /// <summary>Records indexed by ID and by the grid squares their bounds touch.</summary>
    private sealed class SpatialTable<T> where T : class
    {
        private const float GridSize = 500f;
        private readonly Func<T, int> idOf;
        private readonly Func<T, int> versionOf;
        private readonly Func<T, Rect?> boundsOf;
        private readonly Dictionary<int, T> rows = new();
        private readonly Dictionary<int, Rect> bounds = new();
        private readonly Dictionary<Vector2Int, HashSet<int>> grid = new();

        public SpatialTable(Func<T, int> idOf, Func<T, int> versionOf, Func<T, Rect?> boundsOf)
        {
            this.idOf = idOf;
            this.versionOf = versionOf;
            this.boundsOf = boundsOf;
        }

        public int IdOf(T item) => idOf(item);
        public IEnumerable<int> Ids() => new List<int>(rows.Keys);
        public bool TryGet(int id, out T item) => rows.TryGetValue(id, out item);

        public bool TryGetVersion(int id, out int version)
        {
            bool found = rows.TryGetValue(id, out T item);
            version = found ? versionOf(item) : 0;
            return found;
        }

        public void Clear() { rows.Clear(); bounds.Clear(); grid.Clear(); }

        public void Put(T item)
        {
            int id = idOf(item);
            Remove(id);
            rows[id] = item;
            Rect? b = boundsOf(item);
            if (!b.HasValue) return;
            bounds[id] = b.Value;
            ForEachSquare(b.Value, square =>
            {
                if (!grid.TryGetValue(square, out HashSet<int> ids)) grid[square] = ids = new HashSet<int>();
                ids.Add(id);
            });
        }

        /// <summary>Store unless a newer version is already held.</summary>
        public bool PutIfNotOlder(T item)
        {
            if (TryGetVersion(idOf(item), out int version) && version > versionOf(item)) return false;
            Put(item);
            return true;
        }

        /// <summary>Swap the record of an ID whose shape (and therefore bounds) is unchanged.</summary>
        public void Replace(T item)
        {
            int id = idOf(item);
            if (rows.ContainsKey(id)) rows[id] = item;
        }

        public void Remove(int id)
        {
            if (!rows.Remove(id)) return;
            if (!bounds.TryGetValue(id, out Rect b)) return;
            bounds.Remove(id);
            ForEachSquare(b, square =>
            {
                if (grid.TryGetValue(square, out HashSet<int> ids) && ids.Remove(id) && ids.Count == 0) grid.Remove(square);
            });
        }

        /// <summary>Records whose bounds overlap the rectangle, edges included, ordered by ID.</summary>
        public List<T> Query(Rect rect)
        {
            var found = new HashSet<int>();
            ForEachSquare(rect, square =>
            {
                if (!grid.TryGetValue(square, out HashSet<int> ids)) return;
                foreach (int id in ids) if (Overlaps(bounds[id], rect)) found.Add(id);
            });
            var sorted = new List<int>(found);
            sorted.Sort();
            var result = new List<T>(sorted.Count);
            foreach (int id in sorted) result.Add(rows[id]);
            return result;
        }

        public static bool Overlaps(Rect a, Rect b) => a.xMin <= b.xMax && b.xMin <= a.xMax && a.yMin <= b.yMax && b.yMin <= a.yMax;

        private static void ForEachSquare(Rect rect, Action<Vector2Int> action)
        {
            int x0 = Mathf.FloorToInt(rect.xMin / GridSize), x1 = Mathf.FloorToInt(rect.xMax / GridSize);
            int z0 = Mathf.FloorToInt(rect.yMin / GridSize), z1 = Mathf.FloorToInt(rect.yMax / GridSize);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                    action(new Vector2Int(x, z));
        }
    }
}
