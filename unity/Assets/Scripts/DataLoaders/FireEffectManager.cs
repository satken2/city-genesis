using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>
/// Fire effect manager.
///
/// Responsibilities
/// ----
/// Draw building (agent) `ALERT_AGENT_ON_FIRE` as particles in 3D space
/// rather than billboard icons (flames + embers + black smoke + flickering point lights).
/// Buildings burned down this epoch (`STATUS_COLLAPSED`) emit thin white smoke as smoldering rubble
/// (large flames on rubble would look unnatural).
///
/// Data retrieval
/// ----------
/// Receive `AlertManager` results from `GET /api/v1/alerts` through
/// `ApplyAlerts` without separate HTTP fetching (avoid two requests for the same information).
/// Also follow the display filter (Alerts tab of the display panel) and global visibility toggle through `AlertManager`.
///
/// Placement and lifetime
/// ----------
/// Create effects beneath an owned root at world coordinates, not as children of building GameObjects.
/// Buildings may be destroyed by LOD count limits; parenting effects would remove their flames too.
///
/// `ApplyAlerts` also runs when the camera crosses cells, so retain existing effects
/// **without recreating them** (recreation visibly restarts smoke columns from the base). Recreate only
/// when the effect type changes or a later-loaded building mesh substantially changes dimensions.
///
/// Performance
/// --------------
/// - Even citywide fires number at most a few dozen, but smoke columns use many particles,
///   so deactivate entire effect GameObjects beyond `maxEffectDistance`.
/// - Enable only the nearest `maxFireLights` point lights (URP Forward+ supports many lights,
///   but overlapping broad point-light ranges can add up to washed-out highlights).
/// - Batch distance checks and light assignments every `cullIntervalSeconds`.
/// </summary>
public class FireEffectManager : MonoBehaviour
{
    public static FireEffectManager Instance { get; private set; }

    [Header("表示範囲")]
    [Tooltip("この距離(m)より遠い火災エフェクトは非アクティブにする。")]
    public float maxEffectDistance = 1400f;

    [Tooltip("距離判定と光源割り当てを行う間隔(秒)。")]
    public float cullIntervalSeconds = 0.25f;

    [Header("火災の照り返し")]
    [Tooltip("同時に点灯させる火災の点光源の最大数。カメラに近い順に割り当てる。")]
    public int maxFireLights = 6;

    [Tooltip("点光源を使うか。負荷や画作りの都合で切りたい場合にOFFにする。")]
    public bool enableFireLights = true;

    [Tooltip("光源の明滅の速さ。")]
    public float lightFlickerSpeed = 3f;

    [Tooltip("光源の明滅の深さ(0=明滅なし、1=消灯まで振れる)。")]
    [Range(0f, 1f)] public float lightFlickerDepth = 0.35f;

    /// <summary>Base radius (m) when neither building mesh nor floor count is known.</summary>
    private const float FALLBACK_RADIUS = 6f;

    /// <summary>Height (m) when neither building mesh nor floor count is known.</summary>
    private const float FALLBACK_HEIGHT = 8f;

    /// <summary>Recreate effects if dimensions change beyond this ratio.</summary>
    private const float REBUILD_SIZE_RATIO = 1.35f;

    /// <summary>Fire effects for one building.</summary>
    private class FireEffect
    {
        public GameObject Root;
        public FireEffectFactory.PlumeKind Kind;
        public float Radius;
        public float Height;
        public Light Light;
        public float LightBaseIntensity;
        public float FlickerPhase;
    }

    private readonly Dictionary<int, FireEffect> _effects = new();
    private readonly List<int> _staleIds = new();
    private readonly List<FireEffect> _lightCandidates = new();

    private Transform _root;
    private bool _isVisible = true;
    private float _lastCullTime = -1f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance == null)
        {
            var go = new GameObject("FireEffectManager");
            go.AddComponent<FireEffectManager>();
        }
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        var rootGO = new GameObject("FireEffects");
        rootGO.transform.SetParent(transform, false);
        _root = rootGO.transform;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// Overall effect visibility, following `AlertManager.IsVisible`.
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            if (_root != null) _root.gameObject.SetActive(_isVisible);
        }
    }

    /// <summary>
    /// Synchronize fire effects from the alert list. Called by `AlertManager` immediately after receiving an API response.
    /// </summary>
    /// <param name="agents">agents array from GET /api/v1/alerts</param>
    /// <param name="agentFilterMask">Alert filter from the display panel (lower agent-side bits)</param>
    public void ApplyAlerts(List<AlertItem> agents, int agentFilterMask)
    {
        if (_root == null) return;

        var alive = new HashSet<int>();

        if (agents != null)
        {
            foreach (var item in agents)
            {
                FireEffectFactory.PlumeKind? kind = ResolveKind(item, agentFilterMask);
                if (kind == null) continue;

                alive.Add(item.id);
                SyncEffect(item, kind.Value);
            }
        }

        _staleIds.Clear();
        foreach (var kvp in _effects)
        {
            if (!alive.Contains(kvp.Key)) _staleIds.Add(kvp.Key);
        }
        foreach (int id in _staleIds)
        {
            DestroyEffect(id);
        }

        _lastCullTime = -1f;
    }

    /// <summary>Destroy every effect. Called on full reload.</summary>
    public void ClearAll()
    {
        foreach (var kvp in _effects)
        {
            if (kvp.Value.Root != null) Destroy(kvp.Value.Root);
        }
        _effects.Clear();
    }

    /// <summary>
    /// Determine effect type from alert bits and status. Null if not displayed.
    /// </summary>
    private FireEffectFactory.PlumeKind? ResolveKind(AlertItem item, int agentFilterMask)
    {
        bool onFire = (item.alert & Constants.ALERT_AGENT_ON_FIRE) != 0
                      && (agentFilterMask & Constants.ALERT_AGENT_ON_FIRE) != 0;
        if (!onFire) return null;

        // Render burned-down buildings as smoldering rubble rather than flames.
        return item.status == Constants.STATUS_COLLAPSED
            ? FireEffectFactory.PlumeKind.Smolder
            : FireEffectFactory.PlumeKind.Blaze;
    }

    /// <summary>
    /// Create or update effects for one building. Do nothing if type and dimensions are unchanged
    /// (avoid unnecessary recreation to preserve smoke column continuity).
    /// </summary>
    private void SyncEffect(AlertItem item, FireEffectFactory.PlumeKind kind)
    {
        if (!TryResolvePlacement(item, out Vector3 worldPos, out float radius, out float height)) return;

        if (_effects.TryGetValue(item.id, out FireEffect existing) && existing.Root != null)
        {
            bool sizeChanged = Ratio(existing.Radius, radius) > REBUILD_SIZE_RATIO
                               || Ratio(existing.Height, height) > REBUILD_SIZE_RATIO;
            if (existing.Kind == kind && !sizeChanged)
            {
                existing.Root.transform.position = worldPos;
                return;
            }
            DestroyEffect(item.id);
        }

        var spec = new FireEffectFactory.PlumeSpec(radius, height, ResolveDrift(item.id));
        GameObject root = FireEffectFactory.Create(kind, spec, _root, $"Fire_{item.id}");
        root.transform.position = worldPos;

        Light fireLight = root.GetComponentInChildren<Light>(includeInactive: true);
        _effects[item.id] = new FireEffect
        {
            Root = root,
            Kind = kind,
            Radius = radius,
            Height = height,
            Light = fireLight,
            LightBaseIntensity = fireLight != null ? fireLight.intensity : 0f,
            FlickerPhase = (item.id % 97) * 0.37f,
        };
    }

    private void DestroyEffect(int id)
    {
        if (_effects.TryGetValue(id, out FireEffect effect))
        {
            if (effect.Root != null) Destroy(effect.Root);
            _effects.Remove(id);
        }
    }

    /// <summary>
    /// Resolve building placement, base radius, and height.
    /// Prefer the AABB of the rendered mesh for height; if unloaded (LOD eviction or Far),
    /// fall back to floor count × floor height, then the default if unavailable.
    /// </summary>
    private bool TryResolvePlacement(AlertItem item, out Vector3 worldPos, out float radius, out float height)
    {
        worldPos = Vector3.zero;
        radius = FALLBACK_RADIUS;
        height = FALLBACK_HEIGHT;

        if (item.center_point == null || item.center_point.Count < 2) return false;

        float x = item.center_point[0];
        float z = item.center_point[1];
        float groundHeight = Utils.GetGroundHeight(new Vector3(x, 1000f, z));
        worldPos = new Vector3(x, groundHeight, z);

        BuildingSummary summary = BuildingManager.Instance != null
            ? BuildingManager.Instance.GetBuildingSummary(item.id)
            : null;

        if (summary != null && summary.polygonArray != null && summary.polygonArray.Length >= 3)
        {
            radius = ResolveFootprintRadius(summary, x, z);
        }

        GameObject buildingGO = BuildingManager.Instance != null
            ? BuildingManager.Instance.GetBuildingObject(item.id)
            : null;

        if (buildingGO != null && TryResolveMeshHeight(buildingGO, groundHeight, out float meshHeight))
        {
            height = meshHeight;
        }
        else if (summary != null && summary.floors.HasValue && summary.floors.Value > 0)
        {
            height = summary.floors.Value * BuildingMeshUtils.GetFloorHeight(summary);
        }

        return true;
    }

    /// <summary>Calculate radius around the center from the building base polygon's bounding rectangle.</summary>
    public static float ResolveFootprintRadius(BuildingSummary summary, float centerX, float centerZ)
    {
        if (summary?.polygonArray == null) return FALLBACK_RADIUS;
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;

        foreach (float[] point in summary.polygonArray)
        {
            if (point == null || point.Length < 2) continue;
            minX = Mathf.Min(minX, point[0]);
            maxX = Mathf.Max(maxX, point[0]);
            minZ = Mathf.Min(minZ, point[1]);
            maxZ = Mathf.Max(maxZ, point[1]);
        }

        if (minX > maxX || minZ > maxZ) return FALLBACK_RADIUS;

        // The center may differ from the rectangle midpoint (such as L-shaped buildings), so measure to the farther edges.
        float halfX = Mathf.Max(maxX - centerX, centerX - minX);
        float halfZ = Mathf.Max(maxZ - centerZ, centerZ - minZ);
        return Mathf.Max(halfX, halfZ);
    }

    /// <summary>
    /// Treat the distance from the building GameObject's rendered AABB top to the ground as its height.
    /// Scan children too, since mashup buildings may have multiple child meshes.
    /// </summary>
    private static bool TryResolveMeshHeight(GameObject buildingGO, float groundHeight, out float height)
    {
        height = 0f;

        Renderer[] renderers = buildingGO.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return false;

        float top = float.MinValue;
        foreach (Renderer renderer in renderers)
        {
            top = Mathf.Max(top, renderer.bounds.max.y);
        }
        if (top <= float.MinValue) return false;

        height = top - groundHeight;
        return height > 1f;
    }

    /// <summary>
    /// Smoke drift direction determined by building ID. Rotate by the golden angle
    /// to keep neighboring buildings' smoke columns from sharing the same direction.
    /// </summary>
    private static Vector2 ResolveDrift(int id)
    {
        float angle = id * 2.399963f;
        return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
    }

    private static float Ratio(float a, float b)
    {
        float lo = Mathf.Max(Mathf.Min(a, b), 1e-3f);
        float hi = Mathf.Max(a, b);
        return hi / lo;
    }

    private void Update()
    {
        if (!_isVisible || _effects.Count == 0) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        if (Time.time - _lastCullTime >= cullIntervalSeconds)
        {
            _lastCullTime = Time.time;
            UpdateCullingAndLights(cam.transform.position);
        }

        UpdateLightFlicker();
    }

    /// <summary>
    /// Batch distance culling and selection of point lights to enable.
    /// </summary>
    private void UpdateCullingAndLights(Vector3 camPos)
    {
        float maxSqr = maxEffectDistance * maxEffectDistance;
        _lightCandidates.Clear();

        foreach (var kvp in _effects)
        {
            FireEffect effect = kvp.Value;
            if (effect.Root == null) continue;

            float sqrDist = (effect.Root.transform.position - camPos).sqrMagnitude;
            bool visible = sqrDist <= maxSqr;
            if (effect.Root.activeSelf != visible) effect.Root.SetActive(visible);

            if (effect.Light == null) continue;

            if (visible && enableFireLights && effect.Kind == FireEffectFactory.PlumeKind.Blaze)
            {
                _lightCandidates.Add(effect);
            }
            else if (effect.Light.gameObject.activeSelf)
            {
                effect.Light.gameObject.SetActive(false);
            }
        }

        if (_lightCandidates.Count > 1)
        {
            _lightCandidates.Sort((a, b) =>
                (a.Root.transform.position - camPos).sqrMagnitude
                    .CompareTo((b.Root.transform.position - camPos).sqrMagnitude));
        }

        for (int i = 0; i < _lightCandidates.Count; i++)
        {
            bool lit = i < maxFireLights;
            GameObject lightGO = _lightCandidates[i].Light.gameObject;
            if (lightGO.activeSelf != lit) lightGO.SetActive(lit);
        }
    }

    /// <summary>Vary active light intensity. Matching flame and light flicker makes the fire look convincing.</summary>
    private void UpdateLightFlicker()
    {
        if (!enableFireLights || lightFlickerDepth <= 0f) return;

        for (int i = 0; i < _lightCandidates.Count && i < maxFireLights; i++)
        {
            FireEffect effect = _lightCandidates[i];
            if (effect.Light == null || !effect.Light.gameObject.activeSelf) continue;

            float noise = Mathf.PerlinNoise(effect.FlickerPhase, Time.time * lightFlickerSpeed);
            effect.Light.intensity = effect.LightBaseIntensity * (1f - lightFlickerDepth * (1f - noise));
        }
    }
}
