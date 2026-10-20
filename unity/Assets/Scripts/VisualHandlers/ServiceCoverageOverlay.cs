using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;

/// <summary>
/// Resident overlay for the display panel's service-coverage layer: view:change_view with layer=coverage and sub_mode=service product ID.
///
/// For the selected service good, draw each facility's coverage outline along terrain and color active buildings
/// blue inside coverage or red outside. The core, GET /buildings/service-coverage/{product_id}, classifies site parcels;
/// this renderer only looks up the returned building-ID sets.
/// Inactive facilities, such as planned or under-construction ones, do not yet supply services; show dashed ranges but exclude them from classification.
///
/// VisualModeObject queries <see cref="TryGetBuildingColor"/> for building colors, taking precedence over building-layer color modes.
/// Distant buildings use combined rendering without individual colors, so only close/normal buildings are colored.
/// Outlines use shared-material lines facing the camera on the GPU, with no CPU rebuild on camera movement.
/// </summary>
public class ServiceCoverageOverlay : MonoBehaviour
{
    public static ServiceCoverageOverlay Instance { get; private set; }

    /// <summary>Building colors inside/outside coverage, matching the UI legend in coverageColors.ts.</summary>
    public static readonly Color InRangeColor = new Color(0.231f, 0.510f, 0.965f);
    public static readonly Color OutOfRangeColor = new Color(0.937f, 0.267f, 0.267f);

    private static readonly Color OutlineColor = new Color(0.376f, 0.647f, 0.980f, 0.95f);
    private const float OutlinePixelThickness = 2.5f;

    /// <summary>Subdivision interval for terrain-following outlines (m). Draw dashes in pairs of these intervals.</summary>
    private const float OutlineStep = 8f;

    /// <summary>Displayed product ID; 0 means hidden.</summary>
    private int productId;
    private int requestSerial;
    private readonly HashSet<int> coveredBuildings = new HashSet<int>();
    private readonly HashSet<int> uncoveredBuildings = new HashSet<int>();
    private OverlayMeshRenderer outline;

    /// <summary>Create the resident object without scene placement or duplicates.</summary>
    public static void Ensure()
    {
        if (Instance != null) return;
        var go = new GameObject("ServiceCoverageOverlay");
        DontDestroyOnLoad(go);
        go.AddComponent<ServiceCoverageOverlay>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        outline = OverlayMeshRenderer.CreateStatic("ServiceCoverageOutline", true, transform);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// Return a building's color while coverage is displayed. False for inactive buildings or when hidden.
    /// </summary>
    public static bool TryGetBuildingColor(int agentId, out Color color)
    {
        color = default;
        var overlay = Instance;
        if (overlay == null || overlay.productId <= 0) return false;
        if (overlay.coveredBuildings.Contains(agentId))
        {
            color = InRangeColor;
            return true;
        }
        if (overlay.uncoveredBuildings.Contains(agentId))
        {
            color = OutOfRangeColor;
            return true;
        }
        return false;
    }

    /// <summary>Switch the service good; nonpositive values hide it. Reselecting the currently displayed good also refetches.</summary>
    public void SetProduct(int newProductId)
    {
        if (newProductId <= 0)
        {
            if (productId <= 0) return;
            productId = 0;
            Clear();
            return;
        }
        productId = newProductId;
        Refresh();
    }

    /// <summary>If displayed, refetch coverage to reflect new facilities or epoch execution.</summary>
    public void Refresh()
    {
        if (productId <= 0) return;
        int serial = ++requestSerial;
        int requested = productId;
        StartCoroutine(ApiClient.GetBulk<ServiceCoverageResponse>(
            $"/buildings/service-coverage/{requested}",
            response =>
            {
                if (serial != requestSerial || requested != productId) return;
                Apply(response);
            },
            error => Debug.LogError($"ServiceCoverageOverlay: 効果範囲の取得に失敗しました ({error})"),
            "ServiceCoverageOverlay"));
    }

    /// <summary>Clear rendering and building colors but retain the selected good for redraw on the next <see cref="Refresh"/>.</summary>
    public void Clear()
    {
        requestSerial++;
        coveredBuildings.Clear();
        uncoveredBuildings.Clear();
        outline.ClearMesh();
        VisualModeHandler.Instance?.RefreshObjects(InputObjectType.BUILDING);
    }

    private void Apply(ServiceCoverageResponse response)
    {
        coveredBuildings.Clear();
        uncoveredBuildings.Clear();
        if (response != null)
        {
            coveredBuildings.UnionWith(response.covered_building_ids);
            uncoveredBuildings.UnionWith(response.uncovered_building_ids);
        }

        // Terrain heights use a per-frame 1m-grid cache; invalidate it to draw with current heights.
        Utils.InvalidateHeightCache();
        outline.ClearMesh();
        foreach (var supplier in response?.suppliers ?? new List<ServiceCoverageSupplier>())
        {
            var region = EffectRangeRegionData.Parse(supplier.region);
            if (region == null) continue;
            bool dashed = supplier.status != Constants.STATUS_ACTIVE;
            foreach (var ring in region.Rings())
            {
                AddRing(ring, dashed);
            }
        }
        outline.EndStaticFrame();

        VisualModeHandler.Instance?.RefreshObjects(InputObjectType.BUILDING);
    }

    /// <summary>Add a closed ring as terrain-following lines; dashes draw two subdivided intervals and skip two.</summary>
    private void AddRing(List<Vector2> ring, bool dashed)
    {
        int count = ring.Count;
        if (count < 2) return;
        int step = 0;
        for (int i = 0; i < count; i++)
        {
            Vector2 a = ring[i];
            Vector2 b = ring[(i + 1) % count];
            int segments = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / OutlineStep));
            Vector3 prev = GroundPoint(a);
            for (int k = 1; k <= segments; k++, step++)
            {
                Vector3 cur = GroundPoint(Vector2.Lerp(a, b, (float)k / segments));
                if (!dashed || (step / 2) % 2 == 0)
                {
                    outline.AddSolidLine(prev, cur, OutlineColor, OutlinePixelThickness);
                }
                prev = cur;
            }
        }
    }

    private static Vector3 GroundPoint(Vector2 xz)
    {
        float y = Utils.GetGroundHeight(new Vector3(xz.x, 1000f, xz.y));
        return new Vector3(xz.x, y + 0.5f, xz.y);
    }
}

/// <summary>Response from GET /buildings/service-coverage/{product_id}.</summary>
public class ServiceCoverageResponse
{
    public int product_id;
    public List<ServiceCoverageSupplier> suppliers = new List<ServiceCoverageSupplier>();
    public List<int> covered_building_ids = new List<int>();
    public List<int> uncovered_building_ids = new List<int>();
}

/// <summary>One supplying facility with coverage. region matches the construction panel's effect-range format.</summary>
public class ServiceCoverageSupplier
{
    public int agent_id;
    public int status;
    public JToken region;
}
