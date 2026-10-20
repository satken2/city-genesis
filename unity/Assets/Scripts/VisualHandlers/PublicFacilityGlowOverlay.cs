using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// Resident overlay rendering public-facility highlights (VisualFlag.HIGHLIGHT_PUBLIC).
///
/// Leave building materials untouched. Follow the public facility's parcel shape
/// with a ground fill and upward light column fading to transparent at the top.
/// Without material replacement, this can coexist with all color modes,
/// including district colors and durability; the old replacement approach overwrote them.
///
/// Target only STATUS_ACTIVE buildings with public_facility=true.
/// Resolve sites spatially from building centers through ParcelManager.GetParcelIdAtPoint,
/// because GET /buildings/ multi_agent schema omits parcel_id,
/// so building parcel_id cannot be trusted. If the parcel is not loaded or cannot be resolved,
/// fall back to the building footprint polygon.
///
/// Meshes are world-fixed and camera-independent, so rebuild only at RebuildInterval,
/// rather than every frame. Animate pulsing by updating the material's
/// _Tint every frame instead of rebuilding meshes.
/// </summary>
public class PublicFacilityGlowOverlay : MonoBehaviour
{
    public static PublicFacilityGlowOverlay Instance { get; private set; }

    /// <summary>Base light color, yellow.</summary>
    private static readonly Color GlowColor = new Color(1f, 0.85f, 0.30f);

    /// <summary>Ground-fill alpha.</summary>
    private const float GroundAlpha = 0.30f;

    /// <summary>Light-column alpha at ground level, fading to 0 at the top.</summary>
    private const float ColumnBaseAlpha = 0.42f;

    /// <summary>Light-column height range (meters), clamping a height proportional to parcel size.</summary>
    private const float ColumnHeightMin = 10f;
    private const float ColumnHeightMax = 34f;

    /// <summary>Ground-fill elevation offset above terrain (meters).</summary>
    private const float GroundOffset = 0.08f;

    /// <summary>Column-edge subdivision interval (meters), allowing its base to follow uneven terrain.</summary>
    private const float EdgeSubdivideStep = 6f;

    /// <summary>Do not render public facilities beyond this distance (meters).</summary>
    private const float CullDistance = 900f;
    private const float CullDistanceSq = CullDistance * CullDistance;

    /// <summary>Mesh rebuild interval (seconds), also following building/parcel streaming changes.</summary>
    private const float RebuildInterval = 0.4f;

    /// <summary>Pulse period (seconds) and amplitude, making the light appear to breathe.</summary>
    private const float PulsePeriod = 2.0f;
    private const float PulseAmplitude = 0.18f;

    private OverlayMeshRenderer glowRenderer;
    private ParcelManager cachedParcelManager;
    private float nextRebuildTime;

    /// <summary>Whether highlights should currently be visible, resolved every frame from flags and color mode.</summary>
    private bool isActive;

    /// <summary>
    /// Create the resident overlay object from BuildingManager initialization.
    /// No scene placement is needed, and duplicate creation is prevented.
    /// </summary>
    public static void Ensure()
    {
        if (Instance != null) return;
        var go = new GameObject("PublicFacilityGlowOverlay");
        DontDestroyOnLoad(go);
        go.AddComponent<PublicFacilityGlowOverlay>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // Disable depth testing to keep light above uneven terrain, the same tradeoff as ParcelManager highlighting.
        glowRenderer = OverlayMeshRenderer.Create("PublicFacilityGlowMesh", alwaysOnTop: true, parent: transform);
        // Use additive blending (DstBlend=One) so it looks like light rather than paint.
        glowRenderer.Material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        // Read state every frame instead of registering with VisualModeHandler; registration would delay switching
        // until batched notifications arrive over several frames when many buildings exist.
        bool nextActive = ResolveActive();

        if (nextActive != isActive)
        {
            isActive = nextActive;
            if (!isActive)
            {
                glowRenderer.ClearMesh();
                return;
            }
            // Draw immediately when enabled, without waiting.
            nextRebuildTime = 0f;
        }

        if (!isActive) return;

        // Apply pulsing every frame because it requires no mesh rebuild.
        float pulse = 1f - PulseAmplitude * 0.5f * (1f - Mathf.Cos(Time.time * Mathf.PI * 2f / PulsePeriod));
        glowRenderer.SetTint(new Color(1f, 1f, 1f, pulse));

        if (Time.time < nextRebuildTime) return;
        nextRebuildTime = Time.time + RebuildInterval;
        RebuildMesh();
    }

    /// <summary>
    /// Resolve highlighting from HIGHLIGHT_PUBLIC and the building color mode.
    /// Do not illuminate hidden buildings, avoiding emphasis of hidden objects.
    /// </summary>
    private bool ResolveActive()
    {
        var handler = VisualModeHandler.Instance;
        if (handler == null) return false;
        if ((handler.GetFlags(InputObjectType.BUILDING) & VisualFlag.HIGHLIGHT_PUBLIC) == 0) return false;
        return handler.GetMode(InputObjectType.BUILDING) != VisualMode.MODE_HIDDEN;
    }

    private void RebuildMesh()
    {
        if (BuildingManager.Instance == null)
        {
            glowRenderer.ClearMesh();
            return;
        }

        Camera cam = Camera.main;
        if (cam == null) return;
        Vector3 camPos = cam.transform.position;

        if (cachedParcelManager == null)
        {
            cachedParcelManager = FindAnyObjectByType<ParcelManager>();
        }

        glowRenderer.BeginFrame();

        foreach (BuildingSummary building in BuildingManager.Instance.GetAllBuildings())
        {
            if (building == null) continue;
            if (!building.public_facility) continue;
            if (building.status != Constants.STATUS_ACTIVE) continue;

            float[] center = building.centerPointArray;
            if (center == null || center.Length < 2) continue;

            float dx = camPos.x - center[0];
            float dz = camPos.z - center[1];
            if (dx * dx + dz * dz >= CullDistanceSq) continue;

            float[][] footprint = ResolveGlowFootprint(building, center);
            if (footprint == null || footprint.Length < 3) continue;

            AddGlow(footprint);
        }

        glowRenderer.EndFrame(cam);
    }

    /// <summary>
    /// Resolve the world-XZ polygon emitting light.
    /// Return the site parcel shape if resolved, otherwise the building footprint polygon.
    /// </summary>
    private float[][] ResolveGlowFootprint(BuildingSummary building, float[] center)
    {
        if (cachedParcelManager != null)
        {
            int parcelId = cachedParcelManager.GetParcelIdAtPoint(new Vector3(center[0], 0f, center[1]));
            if (parcelId > 0)
            {
                ParcelSummary parcel = cachedParcelManager.GetParcelSummary(parcelId);
                float[][] parcelPolygon = parcel != null ? parcel.GetPolygonArray() : null;
                if (parcelPolygon != null && parcelPolygon.Length >= 3) return parcelPolygon;
            }
        }
        return building.polygonArray;
    }

    /// <summary>
    /// Queue one parcel's light: ground fill plus light column.
    /// polygon uses [i][0]=world X and [i][1]=world Z.
    /// </summary>
    private void AddGlow(float[][] polygon)
    {
        int count = polygon.Length;

        // Ground fill: sample terrain height at each vertex to follow relief.
        var groundVerts = new List<Vector3>(count);
        Color groundColor = new Color(GlowColor.r, GlowColor.g, GlowColor.b, GroundAlpha);
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            if (polygon[i] == null || polygon[i].Length < 2) return;
            float x = polygon[i][0];
            float z = polygon[i][1];
            groundVerts.Add(GroundPoint(x, z));
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
        }
        glowRenderer.AddPolygon(groundVerts, groundColor);

        // Scale column height with parcel size to avoid overly tall columns on small parcels.
        float diagonal = Mathf.Sqrt((maxX - minX) * (maxX - minX) + (maxZ - minZ) * (maxZ - minZ));
        float columnHeight = Mathf.Clamp(diagonal * 0.7f, ColumnHeightMin, ColumnHeightMax);

        Color baseColor = new Color(GlowColor.r, GlowColor.g, GlowColor.b, ColumnBaseAlpha);
        Color topColor = new Color(GlowColor.r, GlowColor.g, GlowColor.b, 0f);

        var columnVerts = new List<Vector3>(count * 12);
        var columnColors = new List<Color>(count * 12);

        for (int i = 0; i < count; i++)
        {
            Vector2 a = new Vector2(polygon[i][0], polygon[i][1]);
            int next = (i + 1) % count;
            Vector2 b = new Vector2(polygon[next][0], polygon[next][1]);

            // Subdivide long edges so the base follows terrain.
            int segments = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / EdgeSubdivideStep));
            Vector3 prev = GroundPoint(a.x, a.y);
            for (int s = 1; s <= segments; s++)
            {
                Vector2 p = Vector2.Lerp(a, b, (float)s / segments);
                Vector3 cur = GroundPoint(p.x, p.y);

                Vector3 prevTop = prev + Vector3.up * columnHeight;
                Vector3 curTop = cur + Vector3.up * columnHeight;

                columnVerts.Add(prev); columnColors.Add(baseColor);
                columnVerts.Add(prevTop); columnColors.Add(topColor);
                columnVerts.Add(curTop); columnColors.Add(topColor);

                columnVerts.Add(prev); columnColors.Add(baseColor);
                columnVerts.Add(curTop); columnColors.Add(topColor);
                columnVerts.Add(cur); columnColors.Add(baseColor);

                prev = cur;
            }
        }

        glowRenderer.AddPreTriangulated(columnVerts, columnColors);
    }

    /// <summary>Return the terrain point at world (x, z), raised slightly above the surface.</summary>
    private static Vector3 GroundPoint(float x, float z)
    {
        float y = Utils.GetGroundHeight(new Vector3(x, 1000f, z));
        return new Vector3(x, y + GroundOffset, z);
    }
}
