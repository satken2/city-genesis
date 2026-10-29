using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Receives VisualModeHandler notifications and updates each object's display state.
/// </summary>
using TMPro;

public class VisualModeObject : MonoBehaviour, IVisualModeHandler
{
    /// <summary>
    /// Color at durability 0.0 (#ff4444), matching meterStartColor in the UI legend.
    /// </summary>
    private static readonly Color DurabilityMinColor = new Color(1f, 0.267f, 0.267f);

    [Tooltip("このオブジェクトの種類(InputObjectType)を指定します")]
    public int objectType;

    [Tooltip("ハイライト表示に使用するマテリアル")]
    [SerializeField] private Material highlightMaterial;

    [Tooltip("pendingステータス用の赤いマテリアル")]
    [SerializeField] private Material pendingMaterial;

    [Tooltip("plannedステータス用の灰色マテリアル")]
    [SerializeField] private Material plannedMaterial;

    [Tooltip("constructionステータス用の黄色いマテリアル")]
    [SerializeField] private Material constructionMaterial;

    [Tooltip("プレビュー表示用のマテリアル")]
    [SerializeField] private Material previewMaterial;

    [Tooltip("道路の交通量・耐久度など数値ベース色分けに使用する白色ベースマテリアル")]
    [SerializeField] private Material pathColorMaterial;

    [Tooltip("建物の数値ベース色分けに使用する白色ベースマテリアル。未設定の場合はResourcesから読み込む。")]
    [SerializeField] private Material buildingColorMaterial;

    [Tooltip("建物のフロア色分けモードで、対象フロアを持たない建物の半透明表示に使用するマテリアル。未設定の場合はResourcesから読み込む。")]
    [SerializeField] private Material buildingColorFadeMaterial;

    [Tooltip("このオブジェクトのマテリアルを一切変更しない（表示/非表示のみ制御する）")]
    public bool preventMaterialChange = false;

    // Shown only while plans are shown (SHOW_PLAN), whatever the status. For objects with no physical look, such as ferry lanes.
    public bool planLayerOnly = false;

    [Header("Renderer Configuration")]
    [Tooltip("子オブジェクトのRendererも対象にするか")]
    public bool useChildRenderers = false;
    [Tooltip("自分自身のRendererを対象外にするか")]
    public bool ignoreSelfRenderer = false;

    // Holds a Renderer and its original materials.
    private class RendererInfo
    {
        public Renderer renderer;
        public Material[] originalMaterials;
        public bool preventMaterialChange;

        public RendererInfo(Renderer r, bool preventChange)
        {
            renderer = r;
            originalMaterials = r.sharedMaterials;
            preventMaterialChange = preventChange;
        }
    }

    private List<RendererInfo> targetRenderers = new List<RendererInfo>();
    private UndergroundGhostObject undergroundGhost;
    /// <summary>Visibility resolved from display mode and status. Associated screen icons follow the same conditions.</summary>
    public bool IsVisualVisible { get; private set; }
    private int objectStatus = Constants.STATUS_ACTIVE; // Object status.

    private bool isHovered = false;
    private bool isPreview = false; // Whether this is preview mode.
    // Whether ObjectVisibilityFilter hides this object.
    // Independent of color modes/flags, taking highest priority while set.
    private bool isFilterHidden = false;
    // Path-interference highlight color; when set, overrides all non-preview appearances.
    // HighlightManager sets it on buildings and existing paths interfering with planned paths.
    private Color? interferenceColor = null;
    private ObjectIdInfo idInfo;

    private void Awake()
    {
        LoadDefaultMaterials();
        InitializeMaterials();
    }

    private void Start()
    {
        // Set default materials loaded from resources.
        LoadDefaultMaterials();

        // Register automatically with VisualModeHandler.
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.RegisterObject(objectType, this);
        }
    }

    private void OnDestroy()
    {
        // Unregister automatically from VisualModeHandler.
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.UnregisterObject(objectType, this);
        }
    }

    /// <summary>
    /// Initialize or refresh material information, recording current sharedMaterials as originals for restoration after highlighting.
    /// Call after replacing meshes/materials. Calling while planned-object colors or other overlays are applied would retain them as originals,
    /// so use RefreshMaterials when adding Renderers after generation.
    /// </summary>
    public void InitializeMaterials()
    {
        CollectMaterials(false);
        undergroundGhost?.Refresh();
    }

    /// <summary>Include added parts while retaining existing Renderers' original materials, then apply the current appearance.</summary>
    public void RefreshMaterials()
    {
        CollectMaterials(true);
        UpdateVisuals();
    }

    private void CollectMaterials(bool preserveOriginals)
    {
        undergroundGhost?.RestoreMaterials();
        var originals = new Dictionary<Renderer, RendererInfo>();
        if (preserveOriginals)
            foreach (var info in targetRenderers)
                if (info.renderer != null) originals[info.renderer] = info;
        targetRenderers.Clear();

        // This object.
        if (!ignoreSelfRenderer)
        {
            var selfRenderer = GetComponent<Renderer>();
            if (selfRenderer != null)
            {
                bool prevent = ShouldPreventMaterialChange(selfRenderer);
                targetRenderers.Add(originals.TryGetValue(selfRenderer, out var original) ? original : new RendererInfo(selfRenderer, prevent));
            }
        }

        // Child objects.
        if (useChildRenderers)
        {
            var childRenderers = GetComponentsInChildren<Renderer>(true);
            foreach (var r in childRenderers)
            {
                if (r.gameObject == this.gameObject) continue;
                bool prevent = ShouldPreventMaterialChange(r);
                targetRenderers.Add(originals.TryGetValue(r, out var original) ? original : new RendererInfo(r, prevent));
            }
        }
    }

    private bool ShouldPreventMaterialChange(Renderer r)
    {
        if (r is SpriteRenderer) return true;
        if (r.GetComponent<TextMeshPro>() != null) return true;
        if (r.GetComponent<OverlayMeshRenderer>() != null) return true;
        // One-way track direction arrows are overlays, so retain their own colors
        // instead of overwriting with hover/planned blue highlights; visibility still follows the body.
        if (r.GetComponent<TrackDirectionArrows>() != null) return true;
        return false;
    }

    /// <summary>
    /// Load each named default material from Resources only once, avoiding about 10 repeated Resources.Load calls per detailed-building generation/restoration.
    /// Cache missing names as real nulls without retrying. Reload only destroyed references detected by Unity null semantics.
    /// </summary>
    private static readonly Dictionary<string, Material> defaultMaterials = new Dictionary<string, Material>();

    private static Material LoadDefault(string name)
    {
        if (defaultMaterials.TryGetValue(name, out Material material) && (ReferenceEquals(material, null) || material != null)) return material;
        material = Resources.Load<Material>(name);
        defaultMaterials[name] = material;
        return material;
    }

    private void LoadDefaultMaterials()
    {
        if (highlightMaterial == null) highlightMaterial = LoadDefault("HighlightMaterial");
        if (pendingMaterial == null) pendingMaterial = LoadDefault("PendingMaterial");
        if (plannedMaterial == null) plannedMaterial = LoadDefault("PlannedMaterial");
        if (constructionMaterial == null) constructionMaterial = LoadDefault("ConstructionMaterial");
        if (previewMaterial == null) previewMaterial = LoadDefault("PreviewBuildingMaterial");

        // Material for numeric road color modes.
        if (pathColorMaterial == null) pathColorMaterial = LoadDefault("PathColorMaterial");

        // Material for numeric building color modes.
        // Load from Resources if not assigned in the Inspector.
        if (buildingColorMaterial == null)
        {
            buildingColorMaterial = LoadDefault("BuildingColorMaterial");
        }
        // Fall back to BuildingMaterial if also absent from Resources.
        // BuildingMaterial uses the DepthOffsetLit shader,
        // giving it the same Z-fighting protection as buildings.
        if (buildingColorMaterial == null)
        {
            buildingColorMaterial = LoadDefault("BuildingMaterial");
        }

        // Translucent material for buildings lacking the target floor type in building floor-color modes.
        if (buildingColorFadeMaterial == null)
        {
            buildingColorFadeMaterial = LoadDefault("BuildingColorFadeMaterial");
        }

        // Fallback.
        if (previewMaterial == null) previewMaterial = LoadDefault("BuildingMaterial");
    }

    public void SetStatus(int status)
    {
        objectStatus = status;
        UpdateVisuals();
    }

    /// <summary>
    /// Set the path-interference highlight color; null clears it.
    /// HighlightManager uses warning colors for buildings/existing paths interfering with planned paths.
    /// Overrides all non-preview appearances: hover, status, and data visualization.
    /// </summary>
    public void SetInterferenceHighlight(Color? color)
    {
        // Use Color? equality to avoid unnecessary redraws.
        if (interferenceColor == color) return;
        interferenceColor = color;
        UpdateVisuals();
    }

    /// <summary>
    /// Set hidden state from per-object filtering.
    /// Dedicated to construction-project tag/object visibility toggles via ObjectVisibilityFilter,
    /// working independently of color modes and planned-object flags.
    /// </summary>
    public void SetFilterHidden(bool hidden)
    {
        if (isFilterHidden == hidden) return;
        isFilterHidden = hidden;
        UpdateVisuals();
    }

    /// <summary>
    /// Set hover state.
    /// </summary>
    public void SetHover(bool hover)
    {
        if (isHovered != hover)
        {
            isHovered = hover;
            UpdateVisuals();
        }
    }

    // IVisualModeHandler implementation.
    public void ApplyVisualMode(int mode)
    {
        // Store the current mode and update.
        UpdateVisuals(mode);
    }

    /// <summary>
    /// Update appearance from the current state.
    /// </summary>
    private void UpdateVisuals(int? modeOverride = null)
    {
        bool surfaceObject = objectType == InputObjectType.BUILDING || objectType == InputObjectType.PATH;
        if (surfaceObject && UndergroundView.IsActive && undergroundGhost == null)
            undergroundGhost = gameObject.AddComponent<UndergroundGhostObject>();
        undergroundGhost?.RestoreMaterials();
        try { UpdateVisualsCore(modeOverride); }
        finally { undergroundGhost?.Refresh(); }
    }

    private void UpdateVisualsCore(int? modeOverride)
    {
        if (targetRenderers == null || targetRenderers.Count == 0) return;

        // Get mode and flags.
        // currentMode is one exclusive color mode; flags is an independent set of visibility toggles.
        int currentMode = modeOverride ?? (VisualModeHandler.Instance != null ? VisualModeHandler.Instance.GetMode(objectType) : VisualMode.MODE_NORMAL);
        int flags = VisualModeHandler.Instance != null ? VisualModeHandler.Instance.GetFlags(objectType) : VisualFlag.SHOW_PLAN;

        // 0. Per-object visibility filter, highest priority.
        // The player explicitly hid this building/road, so override all rendering,
        // including always-visible construction/plans and highlights. Disable picking as well.
        if (isFilterHidden || (UndergroundView.IsActive && currentMode == VisualMode.MODE_HIDDEN))
        {
            SetRenderersEnabled(false);
            EnableCollider(false);
            return;
        }

        // 1. Preview mode.
        if (isPreview)
        {
            SetRenderersEnabled(true);
            if (!preventMaterialChange) ApplyMaterialToAllSubmeshes(previewMaterial);
            return;
        }

        // 1.5. Path-interference highlight, overriding all non-preview appearances.
        // Paint interfering buildings/existing paths with warning colors, overriding other colors.
        if (interferenceColor.HasValue && !preventMaterialChange)
        {
            SetRenderersEnabled(true);
            if (objectType == InputObjectType.BUILDING)
            {
                ApplyBuildingColorOverlay(interferenceColor.Value);
            }
            else
            {
                ApplyColorToRenderers(interferenceColor.Value);
            }
            return;
        }

        // 1.7. Plan-layer-only objects follow the plan visibility and keep their normal materials.
        if (planLayerOnly && ((flags & VisualFlag.SHOW_PLAN) == 0 || currentMode == VisualMode.MODE_HIDDEN))
        {
            SetRenderersEnabled(false);
            EnableCollider(false);
            return;
        }

        // 2. Construction/demolition/collapse statuses.
        // Always display these with their dedicated mesh appearance:
        // construction/demolition = dirt mound/strip + scaffolding + construction machinery + pillars + portals;
        // collapse = rubble mound + collapse parts. Apply no color-mode overlay,
        // preserving the original construction/rubble appearance that recoloring would erase.
        // Highlight on hover as for normal statuses.
        if (objectStatus == Constants.STATUS_CONSTRUCTING
            || objectStatus == Constants.STATUS_DEMOLISHING
            || objectStatus == Constants.STATUS_COLLAPSED)
        {
            SetRenderersEnabled(true);
            if (!preventMaterialChange)
            {
                if (isHovered)
                {
                    ApplyMaterialToAllSubmeshes(highlightMaterial);
                }
                else
                {
                    RestoreOriginalMaterials();
                }
            }
            return;
        }

        // 3. Planned statuses (Pending/Planned).
        // Use a single opaque material for plans. Translucency obscures building shape,
        // so use gray with depth writes and shadows to retain depth cues.
        if (objectStatus == Constants.STATUS_TEMPORARY || objectStatus == Constants.STATUS_SCHEDULED_CONSTRUCTION || objectStatus == Constants.STATUS_PENDING)
        {
            // Show only when SHOW_PLAN is set.
            // Hidden color mode still hides everything, including planned objects.
            if ((flags & VisualFlag.SHOW_PLAN) != 0 && currentMode != VisualMode.MODE_HIDDEN)
            {
                SetRenderersEnabled(true);
                EnableCollider(true);

                if (!preventMaterialChange)
                {
                    Material targetMat = (objectStatus == Constants.STATUS_TEMPORARY) ? pendingMaterial : plannedMaterial;
                    ApplyMaterialToAllSubmeshes(targetMat);
                }
            }
            else
            {
                SetRenderersEnabled(false);
                EnableCollider(false);
            }
            return;
        }

        // 4. Normal status (ACTIVE).
        // Do not handle VisualFlag.HIGHLIGHT_PUBLIC here.
        // PublicFacilityGlowOverlay independently draws light along site-parcel shapes
        // rather than changing building materials, allowing coexistence with every color mode.
        EnableCollider(true);

        // While coverage is displayed, inside/outside colors override building color modes except hidden mode.
        if (objectType == InputObjectType.BUILDING && currentMode != VisualMode.MODE_HIDDEN && TryGetCoverageColor(out Color coverageColor))
        {
            SetRenderersEnabled(true);
            if (!preventMaterialChange)
            {
                if (isHovered)
                {
                    ApplyMaterialToAllSubmeshes(highlightMaterial);
                }
                else
                {
                    ApplyBuildingColorOverlay(coverageColor);
                }
            }
            return;
        }

        switch (currentMode)
        {
            case VisualMode.MODE_HIDDEN:
                SetRenderersEnabled(false);
                EnableCollider(false);
                break;

            case VisualMode.MODE_BOUNDARY:
                if (objectType == InputObjectType.PARCEL)
                {
                    SetRenderersEnabled(false);
                }
                else
                {
                    SetRenderersEnabled(true);
                    if (!preventMaterialChange) ApplyMaterialToAllSubmeshes(highlightMaterial);
                }
                break;

            case VisualMode.MODE_HIGHLIGHT_OWNER:
                SetRenderersEnabled(true);
                if (!preventMaterialChange) ApplyMaterialToAllSubmeshes(highlightMaterial);
                break;

            case VisualModePath.ZONE_COLOR:
                SetRenderersEnabled(true);
                if (!preventMaterialChange && objectType == InputObjectType.PATH)
                {
                    ApplyZoneColorToPath();
                }
                else if (!preventMaterialChange)
                {
                    RestoreOriginalMaterials();
                }
                break;

            case VisualModeBuilding.ZONE_COLOR:
                SetRenderersEnabled(true);
                if (!preventMaterialChange && objectType == InputObjectType.BUILDING)
                {
                    if (isHovered)
                    {
                        ApplyMaterialToAllSubmeshes(highlightMaterial);
                    }
                    else
                    {
                        int zid = ResolveEffectiveZoneId();
                        if (zid > 0 && ZoneManager.Instance != null)
                        {
                            // Fill with the building district's identifying color.
                            ApplyBuildingColorOverlay(ZoneManager.Instance.GetZoneColor(zid));
                        }
                        else
                        {
                            RestoreOriginalMaterials();
                        }
                    }
                }
                else if (!preventMaterialChange)
                {
                    RestoreOriginalMaterials();
                }
                break;

            // --- Numeric road color modes ---
            // Read values from VisualPathStats and use Lerp to determine fill colors.
            // Prioritize highlightMaterial while hovered, as in normal mode.

            case VisualModePath.UTILIZATION:
                SetRenderersEnabled(true);
                if (!preventMaterialChange && objectType == InputObjectType.PATH)
                {
                    if (isHovered)
                    {
                        ApplyMaterialToAllSubmeshes(highlightMaterial);
                    }
                    else
                    {
                        var stats = GetComponent<VisualPathStats>();
                        float value = stats != null ? Mathf.Clamp01(stats.utilization) : 0f;
                        ApplyColorToRenderers(Color.Lerp(Color.white, Color.red, value));
                    }
                }
                else if (!preventMaterialChange)
                {
                    RestoreOriginalMaterials();
                }
                break;

            case VisualModePath.SPEED:
                SetRenderersEnabled(true);
                if (!preventMaterialChange && objectType == InputObjectType.PATH)
                {
                    if (isHovered)
                    {
                        ApplyMaterialToAllSubmeshes(highlightMaterial);
                    }
                    else
                    {
                        var stats = GetComponent<VisualPathStats>();
                        float value = stats != null ? Mathf.Clamp01(stats.effective_speed / 100f) : 0f;
                        ApplyColorToRenderers(Color.Lerp(Color.red, Color.green, value));
                    }
                }
                else if (!preventMaterialChange)
                {
                    RestoreOriginalMaterials();
                }
                break;

            case VisualModePath.PEDESTRIAN:
                SetRenderersEnabled(true);
                if (!preventMaterialChange && objectType == InputObjectType.PATH)
                {
                    if (isHovered)
                    {
                        ApplyMaterialToAllSubmeshes(highlightMaterial);
                    }
                    else
                    {
                        var stats = GetComponent<VisualPathStats>();
                        float value = stats != null ? Mathf.Clamp01(stats.pedestrian_traffic / 100f) : 0f;
                        ApplyColorToRenderers(Color.Lerp(Color.gray, Color.blue, value));
                    }
                }
                else if (!preventMaterialChange)
                {
                    RestoreOriginalMaterials();
                }
                break;

            case VisualModePath.DURABILITY:
                SetRenderersEnabled(true);
                if (!preventMaterialChange && objectType == InputObjectType.PATH)
                {
                    if (isHovered)
                    {
                        ApplyMaterialToAllSubmeshes(highlightMaterial);
                    }
                    else
                    {
                        var stats = GetComponent<VisualPathStats>();
                        float value = stats != null ? Mathf.Clamp01(stats.durability) : 0f;
                        // Durability: 0=red (#ff4444), 1=white (#ffffff).
                        ApplyColorToRenderers(Color.Lerp(DurabilityMinColor, Color.white, value));
                    }
                }
                else if (!preventMaterialChange)
                {
                    RestoreOriginalMaterials();
                }
                break;

            // --- Numeric building color modes ---
            // Read values from VisualBuildingObject and use Lerp to determine fill colors.
            // Prioritize highlightMaterial while hovered, as in normal mode.

            case VisualModeBuilding.RENT:
                SetRenderersEnabled(true);
                if (!preventMaterialChange && objectType == InputObjectType.BUILDING)
                {
                    if (isHovered)
                    {
                        ApplyMaterialToAllSubmeshes(highlightMaterial);
                    }
                    else
                    {
                        var buildingStatsRent = GetComponent<VisualBuildingObject>();
                        Color? rentColor = buildingStatsRent != null ? buildingStatsRent.GetOverlayColor(currentMode) : null;
                        if (rentColor.HasValue)
                        {
                            ApplyBuildingColorOverlay(rentColor.Value);
                        }
                        else
                        {
                            RestoreOriginalMaterials();
                        }
                    }
                }
                else if (!preventMaterialChange)
                {
                    RestoreOriginalMaterials();
                }
                break;

            case VisualModeBuilding.DURABILITY:
                SetRenderersEnabled(true);
                if (!preventMaterialChange && objectType == InputObjectType.BUILDING)
                {
                    if (isHovered)
                    {
                        ApplyMaterialToAllSubmeshes(highlightMaterial);
                    }
                    else
                    {
                        var buildingStatsDur = GetComponent<VisualBuildingObject>();
                        Color? durColor = buildingStatsDur != null ? buildingStatsDur.GetOverlayColor(currentMode) : null;
                        if (durColor.HasValue)
                        {
                            ApplyBuildingColorOverlay(durColor.Value);
                        }
                        else
                        {
                            RestoreOriginalMaterials();
                        }
                    }
                }
                else if (!preventMaterialChange)
                {
                    RestoreOriginalMaterials();
                }
                break;

            // --- Building floor-type color modes ---
            // Fill buildings with capacity for the specified space type using that type's color;
            // show buildings without it translucently.
            // Prioritize highlightMaterial while hovered.

            case VisualModeBuilding.SPACE_OTHER:
            case VisualModeBuilding.SPACE_RESIDENCE:
            case VisualModeBuilding.SPACE_RETAIL:
            case VisualModeBuilding.SPACE_FACTORY:
            case VisualModeBuilding.SPACE_OFFICE:
            case VisualModeBuilding.SPACE_FARM:
                SetRenderersEnabled(true);
                if (!preventMaterialChange && objectType == InputObjectType.BUILDING)
                {
                    if (isHovered)
                    {
                        ApplyMaterialToAllSubmeshes(highlightMaterial);
                    }
                    else
                    {
                        int spaceType = VisualModeToSpaceType(currentMode);
                        var buildingStats = GetComponent<VisualBuildingObject>();
                        bool hasSpace = buildingStats != null && buildingStats.HasSpaceType(spaceType);

                        if (hasSpace)
                        {
                            // Buildings with the target space: fill with the type color.
                            Color spaceColor = GetSpaceTypeColor(spaceType);
                            ApplyBuildingColorOverlay(spaceColor);
                        }
                        else
                        {
                            // Buildings without the target space: display translucently.
                            Color fadedColor = new Color(0.7f, 0.7f, 0.7f, 0.4f);
                            ApplyBuildingColorOverlay(fadedColor, isSemiTransparent: true);
                        }
                    }
                }
                else if (!preventMaterialChange)
                {
                    RestoreOriginalMaterials();
                }
                break;

            case VisualMode.MODE_NORMAL:
            default:
                SetRenderersEnabled(true);

                if (!preventMaterialChange)
                {
                    if (isHovered)
                    {
                        ApplyMaterialToAllSubmeshes(highlightMaterial);
                    }
                    else
                    {
                        RestoreOriginalMaterials();
                    }
                }
                break;
        }
    }

    private bool TryGetCoverageColor(out Color color)
    {
        color = default;
        if (idInfo == null) idInfo = GetComponent<ObjectIdInfo>();
        return idInfo != null && ServiceCoverageOverlay.TryGetBuildingColor(idInfo.id, out color);
    }

    private void SetRenderersEnabled(bool enabled)
    {
        IsVisualVisible = enabled;
        foreach (var info in targetRenderers)
        {
            if (info.renderer != null)
                info.renderer.enabled = enabled && (UndergroundView.IsActive || info.renderer.GetComponentInParent<PathSection>()?.IsUnderground != true);
        }
        BuildingScatterMember.RefreshObject(gameObject);
    }

    /// <summary>
    /// Apply the specified material to all submeshes.
    /// </summary>
    /// <param name="mat">Material to apply; null uses the original.</param>
    /// <param name="useOriginalIfNull">Whether to restore originals when mat is null.</param>
    private void ApplyMaterialToAllSubmeshes(Material mat, bool useOriginalIfNull = false)
    {
        if (mat == null && useOriginalIfNull)
        {
            RestoreOriginalMaterials();
            return;
        }

        if (mat == null) return;

        foreach (var info in targetRenderers)
        {
            if (info.renderer == null) continue;
            if (info.preventMaterialChange) continue;

            int count = info.originalMaterials != null ? info.originalMaterials.Length : 1;
            Material[] newMaterials = new Material[count];
            for (int i = 0; i < count; i++)
            {
                newMaterials[i] = mat;
            }
            info.renderer.sharedMaterials = newMaterials;
        }
    }

    /// <summary>
    /// Restore original materials.
    /// Also clear MaterialPropertyBlock.
    /// </summary>
    private void RestoreOriginalMaterials()
    {
        foreach (var info in targetRenderers)
        {
            if (info.renderer != null && info.originalMaterials != null)
            {
                info.renderer.sharedMaterials = info.originalMaterials;
                info.renderer.SetPropertyBlock(null);
                RoadMarkingSurface.Restore(info.renderer);
            }
        }
    }

    /// <summary>
    /// Apply an opaque building-color overlay.
    /// Apply buildingColorMaterial, an opaque white material, to all submeshes,
    /// then set per-object _BaseColor and _Color through MaterialPropertyBlock.
    /// </summary>
    private void ApplyBuildingColorOverlay(Color color)
    {
        ApplyBuildingColorOverlay(color, isSemiTransparent: false);
    }

    /// <summary>
    /// Apply a building-color overlay.
    /// Use buildingColorMaterial (opaque white) when isSemiTransparent is false,
    /// or buildingColorFadeMaterial (translucent) when true.
    /// Both set per-object _BaseColor and _Color through MaterialPropertyBlock,
    /// rendering correct colors without material instantiation.
    /// </summary>
    private void ApplyBuildingColorOverlay(Color color, bool isSemiTransparent)
    {
        Material baseMat = isSemiTransparent ? buildingColorFadeMaterial : buildingColorMaterial;

        if (baseMat != null)
        {
            ApplyMaterialToAllSubmeshes(baseMat);
        }

        MaterialPropertyBlock props = new MaterialPropertyBlock();
        props.SetColor("_BaseColor", color);
        // Backward compatibility: also set _Color for older shaders using it.
        props.SetColor("_Color", color);

        foreach (var info in targetRenderers)
        {
            if (info.renderer == null || info.preventMaterialChange) continue;

            if (baseMat != null)
            {
                info.renderer.SetPropertyBlock(props);
            }
            else
            {
                // Fallback when no base material exists:
                // directly overwrite existing material colors.
                int count = info.originalMaterials != null ? info.originalMaterials.Length : 0;
                if (count > 0)
                {
                    Material[] mats = info.renderer.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] != null) mats[i].color = color;
                    }
                    info.renderer.sharedMaterials = mats;
                }
            }
        }
    }

    /// <summary>
    /// Rendering for numeric road color modes.
    /// Apply pathColorMaterial to all submeshes,
    /// then set per-object colors through MaterialPropertyBlock.
    /// URP shaders render with _BaseColor instead of _Color,
    /// so set both for backward compatibility.
    /// If pathColorMaterial is unassigned, fall back
    /// to changing original material colors directly.
    /// </summary>
    private void ApplyColorToRenderers(Color color)
    {
        MaterialPropertyBlock props = new MaterialPropertyBlock();
        props.SetColor("_BaseColor", color);
        props.SetColor("_Color", color);

        if (pathColorMaterial != null)
        {
            ApplyMaterialToAllSubmeshes(pathColorMaterial);
        }

        foreach (var info in targetRenderers)
        {
            if (info.renderer == null || info.preventMaterialChange) continue;

            if (pathColorMaterial != null)
            {
                info.renderer.SetPropertyBlock(props);
            }
            else
            {
                int count = info.originalMaterials != null ? info.originalMaterials.Length : 0;
                if (count > 0)
                {
                    Material[] mats = info.renderer.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] != null) mats[i].color = color;
                    }
                    info.renderer.sharedMaterials = mats;
                }
            }
        }
    }

    /// <summary>
    /// Read the district ID for district-color mode from road VisualPathStats or building VisualBuildingStats zone_id.
    /// </summary>
    private int ResolveEffectiveZoneId()
    {
        int zid = 0;
        var pathStats = GetComponent<VisualPathStats>();
        if (pathStats != null) zid = pathStats.zone_id;
        if (zid == 0)
        {
            var bldStats = GetComponent<VisualBuildingStats>();
            if (bldStats != null) zid = bldStats.zone_id;
        }
        return zid;
    }

    /// <summary>
    /// Apply district identification colors to roads.
    /// Use backend zone.color with the road's white base material and PropertyBlock.
    /// Restore original materials for roads without a district.
    /// </summary>
    private void ApplyZoneColorToPath()
    {
        int effectiveZoneId = ResolveEffectiveZoneId();

        if (effectiveZoneId > 0 && ZoneManager.Instance != null)
        {
            Color zoneColor = ZoneManager.Instance.GetZoneColor(effectiveZoneId);
            ApplyColorToRenderers(zoneColor);
        }
        else
        {
            RestoreOriginalMaterials();
        }
    }

    private void EnableCollider(bool enabled)
    {
        var col = GetComponent<Collider>();
        if (col != null)
        {
            col.enabled = enabled;
        }
    }

    /// <summary>
    /// Convert VisualModeBuilding floor-type mode constants
    /// to Constants SPACE_* values.
    /// VisualModeBuilding.SPACE_* is sequential, 303..308.
    /// Constants.SPACE_* uses powers of two: 1, 2, 4, 8, 16, 32.
    /// Derive the bit position from the offset and shift 1 to obtain the constant.
    /// </summary>
    private static int VisualModeToSpaceType(int mode)
    {
        int bitPosition = mode - VisualModeBuilding.SPACE_OTHER;
        if (bitPosition < 0 || bitPosition > 5) return Constants.SPACE_OTHER;
        return 1 << bitPosition;
    }

    /// <summary>
    /// Get the color for a space type ID from LocalConstants.SPACE_TYPE_COLOR_MAP.
    /// Return gray for types absent from the map.
    /// </summary>
    private static Color GetSpaceTypeColor(int spaceType)
    {
        if (LocalConstants.SPACE_TYPE_COLOR_MAP.ContainsKey(spaceType))
        {
            return LocalConstants.SPACE_TYPE_COLOR_MAP[spaceType];
        }
        return new Color(0.7f, 0.7f, 0.7f);
    }
}
