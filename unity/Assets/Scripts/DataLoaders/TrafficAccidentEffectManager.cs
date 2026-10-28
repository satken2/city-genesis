using System.Collections;
using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>
/// 事故の外観はUnity内で抽選し、同じエポック中は表示の退避やフィルター変更をまたいで保持する。
/// コアの事故フラグは道路ごとに1件なので、走行中の個体とは独立した事故車を1台ずつ描く。
/// </summary>
public sealed class TrafficAccidentEffectManager : MonoBehaviour
{
    public static TrafficAccidentEffectManager Instance { get; private set; }
    private const float MaxDistance = 1000f;
    private static readonly int[] VehicleTypes = { Constants.VEHICLE_STANDARD, Constants.VEHICLE_COMMERCIAL, Constants.VEHICLE_TRUCK };
    private static readonly float[] Rolls = { 30f, 90f, 180f };
    private readonly System.Random random = new();
    private readonly Dictionary<int, Accident> accidents = new();
    private readonly HashSet<int> pending = new();
    private readonly HashSet<int> seen = new();
    private readonly List<int> stale = new();
    private PathManager paths;
    private Coroutine worker;
    private int? epoch;
    private bool isVisible = true, filterEnabled = true;
    private float nextCull;

    private sealed class Accident
    {
        public int Type, ModelSeed;
        public float Progress, Lane, Yaw, Roll;
        public bool Forward, Underground, Ready;
        public GameObject Root, Car, Smoke;
        public Bounds Bounds;
        public PathSummary Data;
        public long Revision = -1;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance == null) new GameObject("TrafficAccidentEffectManager").AddComponent<TrafficAccidentEffectManager>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        paths = FindAnyObjectByType<PathManager>();
        AssetPackStore.CityChanging += ClearAll;
        UndergroundView.Changed += RefreshVisibility;
    }

    private void OnDestroy()
    {
        AssetPackStore.CityChanging -= ClearAll;
        UndergroundView.Changed -= RefreshVisibility;
        if (Instance == this) Instance = null;
    }

    private void OnDisable()
    {
        if (worker != null) { RenderWorkScheduler.Stop(this, worker); worker = null; }
    }

    public bool IsVisible
    {
        get => isVisible;
        set { isVisible = value; RefreshVisibility(); }
    }

    public void ApplyAlerts(List<AlertItem> items, int mask, int? resultEpoch)
    {
        if (epoch != resultEpoch) { ClearAll(); epoch = resultEpoch; }
        filterEnabled = (mask & (Constants.ALERT_PATH_ACCIDENT << Constants.ALERT_PATH_MASK_SHIFT)) != 0;
        seen.Clear();
        if (items != null)
        {
            foreach (var item in items)
            {
                if ((item.alert & Constants.ALERT_PATH_ACCIDENT) == 0 || item.status != Constants.STATUS_ACTIVE) continue;
                seen.Add(item.id);
                if (!accidents.ContainsKey(item.id))
                {
                    accidents.Add(item.id, new Accident
                    {
                        Type = VehicleTypes[random.Next(VehicleTypes.Length)], ModelSeed = random.Next(),
                        Progress = (float)random.NextDouble(), Lane = (float)random.NextDouble(),
                        Forward = random.Next(2) == 0, Yaw = (float)random.NextDouble() * 40f - 20f,
                        Roll = Rolls[random.Next(Rolls.Length)] * (random.Next(2) == 0 ? -1 : 1),
                    });
                }
                pending.Add(item.id);
            }
        }
        stale.Clear();
        foreach (int id in accidents.Keys) if (!seen.Contains(id)) stale.Add(id);
        foreach (int id in stale)
        {
            DestroyRoot(accidents[id]);
            accidents.Remove(id);
            pending.Remove(id);
        }
        RefreshVisibility();
    }

    /// <summary>道路の公開・退避・再生成の通知をまとめ、事故のある道路だけを次の描画予算で合わせる。</summary>
    public void RefreshPath(int id)
    {
        if (accidents.ContainsKey(id)) pending.Add(id);
    }

    public void ClearAll()
    {
        if (worker != null) { RenderWorkScheduler.Stop(this, worker); worker = null; }
        foreach (var accident in accidents.Values) DestroyRoot(accident);
        accidents.Clear(); pending.Clear(); epoch = null;
    }

    private static void DestroyRoot(Accident accident)
    {
        if (accident.Root == null) return;
        accident.Root.SetActive(false);
        Destroy(accident.Root);
    }

    private void Update()
    {
        if (worker == null && pending.Count > 0 && paths != null)
            worker = RenderWorkScheduler.Start(this, Synchronize(), "trafficAccidents", () => pending.Count > 0);
        if (Time.unscaledTime >= nextCull)
        {
            nextCull = Time.unscaledTime + .25f;
            RefreshVisibility();
        }
    }

    private IEnumerator Synchronize()
    {
        try
        {
            while (pending.Count > 0)
            {
                int id;
                using (var iterator = pending.GetEnumerator()) { iterator.MoveNext(); id = iterator.Current; }
                pending.Remove(id);
                if (accidents.TryGetValue(id, out var accident)) Synchronize(id, accident);
                yield return RenderWorkScheduler.Continue;
            }
            RefreshVisibility();
        }
        finally { worker = null; }
    }

    private void Synchronize(int id, Accident accident)
    {
        var data = paths.GetPathData(id);
        if (data == null || data.status != Constants.STATUS_ACTIVE || paths.GetPathObject(id) == null)
        {
            accident.Ready = false;
            if (accident.Root != null) accident.Root.SetActive(false);
            return;
        }
        long revision = paths.GetPathGeometryRevision(id);
        if (accident.Ready && revision != -1 && revision == accident.Revision && accident.Data == data) return;
        if (!paths.TryGetPathPolyline(id, out var points)) return;
        if (accident.Root == null)
        {
            accident.Root = new GameObject($"TrafficAccident_{id}");
            accident.Root.transform.SetParent(transform, false);
            accident.Root.SetActive(false);
            int variant = VehicleModelResolver.PickVariantId(accident.Type, accident.ModelSeed);
            accident.Car = VehicleModelResolver.CreateInstance(accident.Type, variant, accident.Root.transform);
            accident.Bounds = VehicleModelResolver.ModelBounds(accident.Car.transform);
            foreach (var collider in accident.Car.GetComponentsInChildren<Collider>(true)) Destroy(collider);
            accident.Car.SetActive(true);
        }
        RoadPath.GetPathMasterParams(data, out float width, out float sidewalk, out float side, out _, out int lanes,
            out _, out float divider, out _, out _, out _, out _);
        var layout = new RoadLaneLayout(width, sidewalk, side, divider, lanes, data.travel_direction);
        accident.Ready = TryPlacement(new VehiclePolyline(points), layout, data.travel_direction, CityInfoStore.TrafficHand,
            accident, out var surface, out var position, out var rotation);
        if (!accident.Ready) { accident.Root.SetActive(false); return; }
        accident.Root.transform.position = position;
        accident.Car.transform.localRotation = rotation;
        accident.Data = data; accident.Revision = revision;
        accident.Underground = TunnelSectionUtil.IsTunnelPoint(surface, TunnelSectionUtil.GetClearanceHeight(data.path_variant));
        UpdateSmoke(accident, SmokeAllowed(data, surface), rotation);
    }

    private static bool SmokeAllowed(PathSummary data, Vector3 surface)
        => !TunnelSectionUtil.IsUndergroundPath(data)
            && !TunnelSectionUtil.IsTunnelPoint(surface, TunnelSectionUtil.GetClearanceHeight(data.path_variant));

    private static void UpdateSmoke(Accident accident, bool smokeAllowed, Quaternion rotation)
    {
        if (smokeAllowed && accident.Smoke == null)
            accident.Smoke = FireEffectFactory.CreateVehicleSmoke(accident.Root.transform);
        if (accident.Smoke != null)
        {
            // 車体だけを傾け、煙の座標軸はワールドの鉛直方向に保つ。
            var source = accident.Bounds.center + Vector3.forward * accident.Bounds.size.z * .3f;
            accident.Smoke.transform.localPosition = rotation * source;
            accident.Smoke.SetActive(smokeAllowed);
        }
    }

    private static bool TryPlacement(VehiclePolyline line, RoadLaneLayout lanes, int direction, int trafficHand,
        Accident accident, out Vector3 surface, out Vector3 position, out Quaternion rotation)
    {
        surface = position = default; rotation = Quaternion.identity;
        float margin = accident.Bounds.size.z * .5f + 1f;
        if (line.Length < margin * 2f) return false;
        float distance = Mathf.Lerp(margin, line.Length - margin, accident.Progress);
        bool forward = direction == Constants.DIR_FORWARD || (direction == Constants.DIR_BOTH && accident.Forward);
        line.SampleOffsetPose(distance, forward, 0, accident.Bounds.size.z, out surface, out var tangent);
        Vector3 right = Vector3.Cross(Vector3.up, tangent).normalized;
        Vector3 normal = Vector3.Cross(tangent, right).normalized;
        rotation = Quaternion.LookRotation(tangent, normal) * Quaternion.Euler(0, accident.Yaw, accident.Roll);
        float minSide = float.PositiveInfinity, maxSide = float.NegativeInfinity, minHeight = float.PositiveInfinity;
        // 接地原点をそのまま回すと転覆車が埋まるため、回転した車体の下端を路面に合わせる。
        for (int x = -1; x <= 1; x += 2)
        for (int y = -1; y <= 1; y += 2)
        for (int z = -1; z <= 1; z += 2)
        {
            Vector3 corner = rotation * (accident.Bounds.center + Vector3.Scale(accident.Bounds.extents, new Vector3(x, y, z)));
            float lateral = Vector3.Dot(corner, right);
            minSide = Mathf.Min(minSide, lateral); maxSide = Mathf.Max(maxSide, lateral);
            minHeight = Mathf.Min(minHeight, Vector3.Dot(corner, normal));
        }
        float lo = -lanes.Outer - minSide, hi = lanes.Outer - maxSide;
        if (lo > hi) return false;
        int lane = Mathf.Min(lanes.Count - 1, Mathf.FloorToInt(accident.Lane * lanes.Count));
        float offset = Mathf.Clamp(lanes.Center(lane, trafficHand), lo, hi);
        position = surface + right * offset - normal * minHeight + Vector3.up * RoadPartHeights.BODY_ABOVE_SPLINE;
        return true;
    }

    private void RefreshVisibility()
    {
        Camera camera = Camera.main;
        bool show = isVisible && filterEnabled && camera != null
            && (GameStateManager.Instance == null || GameStateManager.Instance.IsWorldVisible)
            && (VisualModeHandler.Instance == null || VisualModeHandler.Instance.GetMode(InputObjectType.PATH) != VisualMode.MODE_HIDDEN);
        foreach (var pair in accidents)
        {
            var accident = pair.Value;
            if (accident.Root == null) continue;
            GameObject road = paths != null ? paths.GetPathObject(pair.Key) : null;
            bool visible = show && accident.Ready && (!accident.Underground || UndergroundView.IsActive)
                && road != null && road.activeInHierarchy
                && !ObjectVisibilityFilter.IsHidden(InputObjectType.PATH, pair.Key)
                && (accident.Root.transform.position - camera.transform.position).sqrMagnitude <= MaxDistance * MaxDistance;
            if (accident.Root.activeSelf != visible) accident.Root.SetActive(visible);
        }
    }
}
