using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using GeneratedModels;

/// <summary>
/// BuildingPlanManager:
/// Manages building plans, including new construction and editing existing buildings.
/// Integrates with the InputHandler system to support building construction through mouse input.
/// </summary>
public partial class BuildingPlanManager : MonoBehaviour
{
    [Header("Construction Settings")]
    public int bdType = 0;        // Building type (bd_type)
    public int agentType = 0;        // Agent type (agent_type)
    public int agentVariant = 0;     // Agent variant (agent_variant)
    public int defaultFloors = 3;           // Default number of floors

    /// <summary>
    /// Parcel type required by the variant selected in construction mode (agent_master.parcel_type).
    /// 0 means outside construction mode, including buildable filters selected directly from display layers.
    /// Shared by buildable parcel highlighting (ParcelManager) and parcel click validation so both use the same conditions.
    /// Stored separately because bd_type-based FILTER_* modes cannot express variant-specific conditions.
    /// </summary>
    public static int SelectedParcelType { get; private set; }

    // Construction values actually used (input values)
    private int currentFloors = 3;
    // Visual variants (visual_variant1/2/3). Values from 0.0 to 1.0 determine the building appearance.
    // Linked to the UI appearance seed sliders and set on BuildingSummary when generating the preview mesh.
    // Sent to the backend on confirmation.
    private float currentVisualVariant = 0.5f;
    private float currentVisualVariant2 = 0.5f;
    private float currentVisualVariant3 = 0.5f;
    // Landmark type (landmark_master ID; 0=not a landmark). Linked to landmark selection in scenario authoring mode,
    // and set on BuildingSummary.landmark when generating the preview mesh (affects part prefix resolution).
    private int currentLandmark = 0;

    // Cached preview geometry, retained to rebuild the mesh from cached polygons
    // when visual variants change without requesting the geometry from the backend again.
    private float[][] cachedPolygon;
    private float[] cachedCenterPoint;
    private float cachedHeight;

    // Selected parcel ID
    private int selectedParcelId = -1;

    [SerializeField] private Material previewMaterial; // Preview material
    
    // Preview component
    [SerializeField] private MeshFilter previewMeshFilter;
    [SerializeField] private MeshRenderer previewMeshRenderer;
    private Mesh previewMesh;

    [Header("Sound Effects")]
    [SerializeField] private AudioClip selectParcelSound;
    [SerializeField] private AudioClip changeFloorsSound;

    // Sound effect playback
    private AudioSource audioSource;

    // Current mode (new construction or editing)
    private bool isEditMode = false;
    private int editingBuildingId = -1;
    private BuildingDetail editingBuildingData = null;

    // Building construction mode state
    private bool isBuildingModeActive = false;

    // 3D overlay of the coverage of infrastructure facilities (substations, sewage treatment plants, radio towers, etc.).
    // Shown when a facility with coverage and its site are selected; hidden on mode exit or a clear command.
    private EffectRangeOverlay effectRangeOverlay;

    // ===== Real-time rectangle ghost for direct placement =====
    // Direct placement parameters initialized from master data and updated by keyboard or panel delta commands.
    // A direct placement footprint is a rotated rectangle, so Unity can generate it independently
    // and draw a real-time cursor-following preview (ghost) without a backend round trip.
    private float directWidth = 0f;
    private float directDepth = 0f;
    // Rotation angle (degrees). Negative values and values over 360 are retained (the UI applies mod 360 on confirmation).
    // Unity is authoritative for the angle during a middle-button drag.
    private float directAngle = 0f;
    private bool hasDirectParams = false;    // Whether any parameters have been received from the UI
    private bool directCommitted = false;    // Whether the position has been fixed (left click). true=phase B
    private bool directPreviewVisible = false; // Whether the direct placement preview mesh is currently visible
    private Vector3 directCenter = Vector3.zero; // Current rectangle center (phase A: cursor / phase B: fixed point)

    // Middle-button drag rotation (PlacementInput.cs, shared with prop placement). Unity owns the angle during a drag.
    private readonly MiddleDragRotation middleDrag = new MiddleDragRotation();
    private bool isMiddleDragging => middleDrag.IsDragging;

    // Hover ghost rebuild throttle (last rendered center and angle)
    private Vector3 lastGhostCenter = new Vector3(float.MaxValue, 0f, float.MaxValue);
    private float lastGhostAngle = float.NaN;
    private const float GhostRebuildDistance = 0.5f; // Rebuild when the cursor moves at least this distance (m).

    private void Awake()
    {
        if (previewMeshFilter == null || previewMeshRenderer == null)
        {
            // Find or create the preview object.
            GameObject previewObj = GameObject.Find("BuildingPreview");
            if (previewObj == null)
            {
                previewObj = new GameObject("BuildingPreview");
                // Configure settings such as disabling shadow casting here if needed.
            }

            if (previewMeshFilter == null)
            {
                previewMeshFilter = previewObj.GetComponent<MeshFilter>();
                if (previewMeshFilter == null) previewMeshFilter = previewObj.AddComponent<MeshFilter>();
            }

            if (previewMeshRenderer == null)
            {
                previewMeshRenderer = previewObj.GetComponent<MeshRenderer>();
                if (previewMeshRenderer == null) previewMeshRenderer = previewObj.AddComponent<MeshRenderer>();
            }
        }

        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }
        gameObject.AddComponent<BuildingEditControlMenu>().Initialize(this);
    }

    /// <summary>
    /// Data structure for building details
    /// </summary>
    [Serializable]
    public class BuildingDetail
    {
        public int agent_id;
        public int bd_type;
        public int bd_variant;
        public int status;
        public int floors;
        public float construction_ratio;
        public int parcel_id;
        // Visual variants. Null if absent from the single_agent schema (float? is null when the JSON field is absent).
        // In that case, fall back to the BuildingManager cache (multi_agent schema).
        public float? visual_variant;
        public float? visual_variant2;
        public float? visual_variant3;
        // Landmark type (null for a non-landmark, treated as 0)
        public int? landmark;
    }

    [Serializable]
    private class AgentResponse
    {
        public BuildingDetail economic_agent;
    }

    public void SetEditMode(int buildingId)
    {
        Debug.Log($"BuildingPlanManager: 編集モードを設定(SetEditMode) - 建物ID: {buildingId}");
        isEditMode = true;
        editingBuildingId = buildingId;
        
        // Fetch the building to edit.
        StartCoroutine(LoadEditingBuildingData());
    }

    public void SetNewBuildingMode()
    {
        Debug.Log("BuildingPlanManager: 新規建設モードを設定(SetNewBuildingMode)");
        isEditMode = false;
        editingBuildingId = -1;
        editingBuildingData = null;
    }

    public void SetBuildingVariant(int variant)
    {
        agentVariant = variant;
        // Landmarks are selected only in scenario authoring mode, so clear the selection when the variant changes.
        currentLandmark = 0;

        // Treat a variant change as starting a new placement flow and initialize the direct placement ghost state
        // (unfixed position=return to phase A). Initialize dimensions from master data and reset rotation to 0 degrees.
        ResetDirectPlacementState();
        InitializePlacement();
        
        // Get agent_master from MasterDataStore and set the agent and building types.
        if (MasterDataStore.Instance != null)
        {
            var agentMaster = MasterDataStore.Instance.GetAgentMaster(variant);
            if (agentMaster != null)
            {
                // Set agent_type (TYPE_BUILDING, etc.).
                if (agentMaster.type.HasValue)
                {
                    agentType = agentMaster.type.Value;
                }

                // Set bd_type (building subtype).
                if (agentMaster.bd_type.HasValue)
                {
                    bdType = agentMaster.bd_type.Value;
                }

                SelectedParcelType = agentMaster.parcel_type ?? 0;

                Debug.Log($"BuildingPlanManager: 建物バリアントを設定しました: {variant}, agent_type: {agentType}, bd_type: {bdType}, parcel_type: {SelectedParcelType}");
            }
            else
            {
                SelectedParcelType = 0;
                Debug.LogWarning($"BuildingPlanManager: agent_master (variant={variant}) が見つかりません");
            }
        }
        else
        {
            SelectedParcelType = 0;
            Debug.LogWarning("BuildingPlanManager: MasterDataStore.Instanceがnullです");
            Debug.Log($"BuildingPlanManager: 建物バリアントを設定しました: {variant}");
        }
    }

    public void SetBuildingMode(bool mode)
    {
        isBuildingModeActive = mode;
        if (mode)
        {
            ApplyPlacementInputMode();
            EmitPlacementState();
            Debug.Log("建物建設モード: ON");
        }
        else
        {
            Debug.Log("建物建設モード: OFF");
            ResetBuildingConstruction();
        }
    }

    private IEnumerator LoadEditingBuildingData()
    {
        yield return ApiClient.Get<AgentResponse>(
            $"/agents/{editingBuildingId}",
            response =>
            {
                if (response == null || response.economic_agent == null)
                {
                    Debug.LogError($"建物 {editingBuildingId} の詳細データが取得できませんでした");
                    return;
                }

                editingBuildingData = response.economic_agent;

                // Check whether editing is allowed.
                if (editingBuildingData.status != Constants.STATUS_TEMPORARY && editingBuildingData.status != Constants.STATUS_SCHEDULED_CONSTRUCTION)
                {
                    Debug.LogWarning($"建物 {editingBuildingId} は編集できません。ステータス: {editingBuildingData.status}");
                    return;
                }

                // Load the current settings from the building data.
                agentType = editingBuildingData.bd_type;
                agentVariant = editingBuildingData.bd_variant;
                defaultFloors = editingBuildingData.floors;

                // Also update the current construction values.
                currentFloors = editingBuildingData.floors;
                currentLandmark = editingBuildingData.landmark ?? 0;

                // Load the visual variants.
                // If the single_agent schema omits visual_variant, the fields are null, so
                // fall back to the BuildingManager cache (the multi_agent schema includes visual_variant).
                if (editingBuildingData.visual_variant.HasValue || editingBuildingData.visual_variant2.HasValue || editingBuildingData.visual_variant3.HasValue)
                {
                    currentVisualVariant = editingBuildingData.visual_variant ?? 0.5f;
                    currentVisualVariant2 = editingBuildingData.visual_variant2 ?? 0.5f;
                    currentVisualVariant3 = editingBuildingData.visual_variant3 ?? 0.5f;
                }
                else if (BuildingManager.Instance != null)
                {
                    BuildingSummary cached = BuildingManager.Instance.GetBuildingSummary(editingBuildingId);
                    if (cached != null)
                    {
                        currentVisualVariant = cached.visual_variant;
                        currentVisualVariant2 = cached.visual_variant2;
                        currentVisualVariant3 = cached.visual_variant3;
                        Debug.Log($"編集対象建物のvisual_variantをキャッシュから取得: vv1={currentVisualVariant}, vv2={currentVisualVariant2}, vv3={currentVisualVariant3}");
                    }
                }

                Debug.Log($"編集対象建物データを読み込みました: Type={agentType}, Variant={agentVariant}, Floors={currentFloors}");
            },
            error => Debug.LogError($"建物詳細の取得に失敗: {error}")
        );
    }

    public void OnParcelClick(int parcelId, Vector3 position, ParcelSummary parcelData = null)
    {
        if (!isEditMode && PlacementMode != BuildingPlacementMode.Parcel) return;
        if (!isBuildingModeActive)
        {
            Debug.Log("建物建設モードが無効のため、クリックを無視します");
            return;
        }

        // Use only the variant to detect an empty selection. Border checkpoints have type=TYPE_DEFAULT(0), so type cannot distinguish them.
        if (agentVariant == 0)
        {
            Debug.LogWarning("バリアントが設定されていません");
            return;
        }

        // Reject locations using the same conditions as buildable parcel highlighting and warn the UI.
        // Calls without parcelData cannot be checked here; allow them through for backend validation.
        if (parcelData != null && !IsParcelTypeAllowed(parcelData.parcel_type, bdType))
        {
            Debug.Log($"BuildingPlanManager: parcel_type={parcelData.parcel_type} はバリアント {agentVariant} の立地条件({ResolveRequiredParcelType(bdType)})を満たしません。クリックを無視します。");
            // Notify the UI to display a warning.
            if (IpcClient.Instance != null)
            {
                IpcClient.Instance.EmitEditBuildingInvalidParcel(parcelId);
            }
            return;
        }

        // Record the selected parcel (used for preview generation and confirmation).
        selectedParcelId = parcelId;
        PlaySound(selectParcelSound);

        // Hide the direct placement ghost/preview when a parcel is selected (switch to parcel-based placement).
        // Clear directCommitted first; otherwise HideDirectGhost cannot remove a committed preview.
        directCommitted = false;
        HideDirectGhost();

        if (isEditMode)
        {
            // In edit mode, notify the UI of move confirmation via IPC.
            if (IpcClient.Instance != null)
            {
                IpcClient.Instance.EmitEditBuildingSelectParcel(
                    parcelId,
                    new float[] { position.x, position.y, position.z },
                    true,
                    editingBuildingId,
                    parcelData?.GetPolygonArray()
                );
            }
        }
        else
        {
            // For new construction, notify the UI of parcel selection via IPC.
            if (IpcClient.Instance != null)
            {
                IpcClient.Instance.EmitEditBuildingSelectParcel(
                    parcelId,
                    new float[] { position.x, position.y, position.z },
                    false,
                    -1,
                    parcelData?.GetPolygonArray()
                );
            }
        }
    }

    /// <summary>
    /// Fix the position for free or roadside placement using the same snapping calculation as the displayed rectangle.
    /// Report the center, dimensions, and angle together through placement_state. The panel confirms construction.
    /// </summary>
    public void OnTerrainClickDirect(Vector3 position)
    {
        if (!IsDirectPlacement || directCommitted || isMiddleDragging) return;
        OnTerrainHoverDirect(position);
        if (!directPreviewVisible) return;
        if (!isBuildingModeActive)
        {
            Debug.Log("建物建設モードが無効のため、区画外クリックを無視します");
            return;
        }

        if (isEditMode)
        {
            // Do not use direct placement while editing (relocating) an existing building.
            return;
        }

        // Border checkpoints have type=TYPE_DEFAULT(0), so use only the variant to detect an empty selection.
        if (agentVariant == 0)
        {
            Debug.LogWarning("バリアントが設定されていません");
            return;
        }

        // Clear parcel selection when switching to direct placement (a selected parcel suppresses the ghost,
        // and this also prevents an old parcel_id from remaining in the preview BuildingSummary).
        selectedParcelId = -1;

        // Fix the position (phase B). Hover no longer moves the center; rotation pivots around this fixed point.
        directCommitted = true;
        directPreviewVisible = true;

        PlaySound(selectParcelSound);

        if (IpcClient.Instance != null)
        {
            // Report the displayed center, dimensions, and angle as a single generation.
            EmitPlacementState();
        }
    }

    private void ResetBuildingConstruction()
    {
        isEditMode = false;
        editingBuildingId = -1;
        editingBuildingData = null;
        // Clear the selection on leaving construction mode so buildable filters chosen from display layers
        // do not retain the parcel type of the previous variant.
        SelectedParcelType = 0;

        // Reset the current construction values to defaults.
        currentFloors = defaultFloors;
        // Reset visual variants to the default (0.5) as well.
        currentVisualVariant = 0.5f;
        currentVisualVariant2 = 0.5f;
        currentVisualVariant3 = 0.5f;
        // Clear the preview geometry cache.
        cachedPolygon = null;
        cachedCenterPoint = null;
        cachedHeight = 0f;
        selectedParcelId = -1;

        // Reset the direct placement ghost state.
        ResetDirectPlacementState();

        ClearPreview();

        // Also hide the infrastructure facility coverage overlay.
        if (effectRangeOverlay != null)
        {
            effectRangeOverlay.Clear();
        }

        Debug.Log("建物建設の状態をリセットしました");
    }

    /// <summary>
    /// Handles building geometry updates received via IPC.
    /// Receives geometry fetched by the UI from backend POST /api/v1/buildings/preview
    /// and directly generates the 3D preview mesh. This is the only source of parcel preview geometry;
    /// Unity does not query the backend for geometry.
    /// </summary>
    public void OnUpdateShapeFromIpc(float[][] polygon, int floors, float[] centerPoint, float height, int revision = -1)
    {
        if (!isBuildingModeActive) return;
        // Match direct placement generations so asynchronous replies cannot restore canceled positions or old dimensions.
        if (revision >= 0 && (!IsDirectPlacement || !directCommitted || revision != placementRevision || floors != currentFloors)) return;
        if (revision < 0 && (IsDirectPlacement || (!isEditMode && selectedParcelId < 0))) return;
        if (isMiddleDragging) return;
        Debug.Log($"BuildingPlanManager: IPC経由で形状変更を受信 floors={floors}, height={height}");
        currentFloors = floors;

        if (polygon == null || polygon.Length < 3)
        {
            Debug.LogWarning("BuildingPlanManager: ポリゴンデータが不正です（頂点数 < 3）");
            return;
        }

        // Cache the preview geometry. Rebuild from this polygon when visual variants change.
        cachedPolygon = polygon;
        cachedCenterPoint = centerPoint;
        cachedHeight = height;

        // When receiving the authoritative direct placement preview (phase B, from preview-direct),
        // retain its center so middle-button rotation can rotate the rectangle locally around it.
        if (directCommitted && centerPoint != null && centerPoint.Length >= 2)
        {
            directCenter = new Vector3(centerPoint[0], 0f, centerPoint[1]);
            directPreviewVisible = true;
        }

        BuildAndShowPreviewMesh(polygon, floors, centerPoint, height);
    }

    /// <summary>
    /// Generates and displays a preview mesh from the given footprint polygon, floors, center, and height.
    /// Shared by IPC reception (OnUpdateShapeFromIpc), cache rebuilding (RebuildPreviewFromCache),
    /// and the local direct placement rectangle ghost (BuildDirectRectanglePreview).
    /// The polygon uses coordinates relative to center_point, like actual backend buildings, while the center
    /// uses world coordinates (the mesh is generated locally and placed at center_point).
    /// The caller is responsible for updating the cache and setting currentFloors.
    /// </summary>
    private void BuildAndShowPreviewMesh(float[][] polygon, int floors, float[] centerPoint, float height)
    {
        if (polygon == null || polygon.Length < 3)
        {
            Debug.LogWarning("BuildingPlanManager: ポリゴンデータが不正です（頂点数 < 3）");
            return;
        }

        // Build polygon coordinates in the BuildingSummary polygon format (List<object>).
        var polygonObjList = new List<object>();
        for (int i = 0; i < polygon.Length; i++)
        {
            if (polygon[i] != null && polygon[i].Length >= 2)
            {
                polygonObjList.Add(new List<object> { polygon[i][0], polygon[i][1] });
            }
        }

        BuildingSummary buildingData = new BuildingSummary
        {
            agent_id = -1,
            parcel_id = selectedParcelId > 0 ? selectedParcelId : 0,
            type = agentType,
            variant = agentVariant,
            bd_type = bdType,
            floors = floors,
            height = height,
            polygon = polygonObjList,
            landmark = currentLandmark,
            visual_variant = currentVisualVariant,
            visual_variant2 = currentVisualVariant2,
            visual_variant3 = currentVisualVariant3,
            center_point = centerPoint != null && centerPoint.Length >= 2
                ? new List<object> { centerPoint[0], centerPoint[1] }
                : null
        };

        // Generate the mesh with BuildingMeshBuilder.
        BuildingMeshResult meshResult = new BuildingMeshBuilder().Generate(buildingData);

        Vector3 centerPosition = ComputePreviewCenterPosition(polygon, centerPoint);
        SetBuildingPreviewMesh(meshResult, centerPosition, previewMaterial);
    }

    /// <summary>
    /// Calculates the preview mesh position (center in world coordinates).
    /// Uses center_point when present, otherwise the polygon centroid, aligns it to the ground height,
    /// and raises it slightly to avoid Z-fighting.
    /// </summary>
    private Vector3 ComputePreviewCenterPosition(float[][] polygon, float[] centerPoint)
    {
        if (centerPoint != null && centerPoint.Length >= 2)
        {
            float cx = centerPoint[0];
            float cz = centerPoint[1];
            float groundHeight = Utils.GetGroundHeight(new Vector3(cx, 1000f, cz));
            return new Vector3(cx, groundHeight + 0.01f, cz);
        }

        List<Vector3> points = new List<Vector3>();
        for (int i = 0; i < polygon.Length; i++)
        {
            if (polygon[i] != null && polygon[i].Length >= 2)
            {
                points.Add(new Vector3(polygon[i][0], 0, polygon[i][1]));
            }
        }
        if (points.Count == 0) return Vector3.zero;

        Vector3 sum = Vector3.zero;
        foreach (var p in points) sum += p;
        Vector3 c = sum / points.Count;
        float gh = Utils.GetGroundHeight(new Vector3(c.x, 1000f, c.z));
        return new Vector3(c.x, gh + 0.01f, c.z);
    }

    /// <summary>
    /// Handles infrastructure facility coverage received via IPC.
    /// Applies region data fetched by the UI from POST /api/v1/buildings/preview-effect-range
    /// to the 3D overlay (EffectRangeOverlay). Hides it when payload requests a clear.
    /// </summary>
    public void OnUpdateEffectRangeFromIpc(EffectRangePayload payload)
    {
        EnsureEffectRangeOverlay();
        effectRangeOverlay.SetData(payload);
    }

    /// <summary>Creates the coverage overlay instance if absent.</summary>
    private void EnsureEffectRangeOverlay()
    {
        if (effectRangeOverlay != null) return;
        var overlayObj = new GameObject("EffectRangeOverlay");
        effectRangeOverlay = overlayObj.AddComponent<EffectRangeOverlay>();
    }

    /// <summary>
    /// Sets the number of floors. Only rebuilds the local direct placement rectangle ghost; for parcel previews,
    /// the UI refetches POST /buildings/preview with the same floor change and delivers it through edit_building:update_shape.
    /// </summary>
    public void SetFloors(int floors)
    {
        bool changed = currentFloors != floors;
        currentFloors = floors;
        if (changed) PlaySound(changeFloorsSound);
        if (IsDirectPlacement && directPreviewVisible) BuildDirectRectanglePreview();
        Debug.Log($"BuildingPlanManager: 階数を設定しました: {floors}");
    }

    /// <summary>
    /// Sets visual variants (visual_variant1/2/3) and rebuilds the preview mesh.
    /// Called when the UI appearance seed sliders change. Without refetching from the backend,
    /// rebuilds the mesh from the cached footprint polygon using the current visual_variant.
    /// </summary>
    public void SetVisualVariant(float vv1, float vv2, float vv3)
    {
        currentVisualVariant = vv1;
        currentVisualVariant2 = vv2;
        currentVisualVariant3 = vv3;
        Debug.Log($"BuildingPlanManager: 外観バリアントを設定しました: vv1={vv1}, vv2={vv2}, vv3={vv3}");
        RebuildPreviewFromCache();
    }

    /// <summary>
    /// Sets the landmark type and rebuilds the preview mesh. Called by landmark selection in scenario authoring mode.
    /// Master data determines landmark site dimensions and floors as well as part prefixes (landmark-{model_prefix}),
    /// so rebuild the direct placement ghost and placement_state using the landmark's actual dimensions.
    /// The footprint polygon itself comes from the authoritative backend preview and is not refetched here.
    /// </summary>
    public void SetLandmark(int landmark)
    {
        currentLandmark = landmark;
        if (landmark > 0)
        {
            ApplyFootprintDefaults();
            RebuildAfterSizeChange();
            EmitPlacementState(false);
        }
        Debug.Log($"BuildingPlanManager: 観光名所を設定しました: landmark={landmark}");
        RebuildPreviewFromCache();
    }

    /// <summary>
    /// Rebuilds the preview mesh from the cached footprint polygon and current visual_variant.
    /// Called when visual variants change. No backend round trip is needed.
    /// Does nothing without a cache (e.g. before selecting a parcel).
    /// </summary>
    private void RebuildPreviewFromCache()
    {
        if (cachedPolygon == null || cachedPolygon.Length < 3)
        {
            // Cannot display a preview without a cache.
            return;
        }

        // Rebuild the mesh from the cached footprint polygon and current visual_variant/floors.
        BuildAndShowPreviewMesh(cachedPolygon, currentFloors, cachedCenterPoint, cachedHeight);
    }

    // ======================================================================
    // Real-time rectangle ghost for direct placement
    // ======================================================================

    /// <summary>
    /// Initializes direct placement ghost state (parameters, committed flag, drag state, and throttle).
    /// Does not remove the mesh here (ResetBuildingConstruction calls ClearPreview for a full reset).
    /// </summary>
    private void ResetDirectPlacementState()
    {
        ResetRoadAnchor();
        hasRawPlacementPosition = false;
        placementRevision++;
        directWidth = 0f;
        directDepth = 0f;
        directAngle = 0f;
        freeAngle = 0f;
        hasDirectParams = false;
        directCommitted = false;
        directPreviewVisible = false;
        directCenter = Vector3.zero;
        middleDrag.Cancel();
        lastGhostCenter = new Vector3(float.MaxValue, 0f, float.MaxValue);
        lastGhostAngle = float.NaN;
    }

    /// <summary>
    /// Receives direct placement rectangle parameters (width, depth, angle, and floors) from the UI panel.
    /// Via edit_building:update_direct_params. Synchronizes dimensions and rotation of the cursor-following ghost
    /// and committed preview with Unity. Redraws immediately if a direct preview is visible.
    /// </summary>
    public void SetDirectParams(float width, float depth, float angle, int floors)
    {
        directWidth = width;
        directDepth = depth;
        currentFloors = floors;
        hasDirectParams = true;

        // Unity owns the angle during a middle-button drag. Do not overwrite it with a UI echo
        // (avoids unnecessary redraws and echo round trips).
        if (!isMiddleDragging)
        {
            directAngle = angle;
            freeAngle = angle;
        }

        // Redraw immediately if the direct preview is visible (phase A: ghost or phase B: committed).
        if (directPreviewVisible && isBuildingModeActive && !isEditMode)
        {
            BuildDirectRectanglePreview();
        }
    }

    /// <summary>
    /// Called when hovering over empty terrain in direct placement mode (TerrainInputHandler.onHover).
    /// Draws a rectangle ghost centered on the cursor only before fixing the position (phase A).
    /// Does nothing after confirmation (phase B) to avoid moving the authoritative preview.
    /// </summary>
    public void OnTerrainHoverDirect(Vector3 pos)
    {
        lastRawPlacementPosition = pos;
        hasRawPlacementPosition = true;
        if (!IsDirectPlacement) return;
        if (isEditMode || !hasDirectParams || directCommitted) return;


        // Keep the ghost center fixed during middle-button rotation (anchor it so cursor movement
        // during rotation does not make it slide). Update() redraws the rotation.
        if (isMiddleDragging) return;

        directCenter = new Vector3(pos.x, 0f, pos.z);
        ResolveRoadPlacement(pos);

        // Throttle: rebuild only after moving a certain distance or changing angle since the last draw
        // (avoids GC/stutter from rebuilding the mesh every frame).
        if (Vector3.Distance(directCenter, lastGhostCenter) < GhostRebuildDistance &&
            directAngle == lastGhostAngle && directPreviewVisible)
        {
            return;
        }

        BuildDirectRectanglePreview();
    }

    /// <summary>
    /// Hides the direct placement hover ghost (TerrainInputHandler.onHoverEnd).
    /// Does nothing after fixing the position (phase B) to preserve the authoritative preview.
    /// </summary>
    public void HideDirectGhost()
    {
        if (directCommitted) return;
        if (!directPreviewVisible) return;

        if (previewMeshFilter != null)
        {
            previewMeshFilter.mesh = null;
        }
        directPreviewVisible = false;
        lastGhostCenter = new Vector3(float.MaxValue, 0f, float.MaxValue);
        lastGhostAngle = float.NaN;
    }

    /// <summary>
    /// Locally calculates a footprint polygon for a directWidth×directDepth rectangle rotated by directAngle
    /// around directCenter, then generates and displays its preview mesh (no IPC transmission).
    /// The polygon uses coordinates relative to center_point, matching the authoritative backend preview (preview-direct),
    /// and the center uses world coordinates (matching the existing BuildAndShowPreviewMesh convention).
    /// </summary>
    private void BuildDirectRectanglePreview()
    {
        if (directWidth <= 0f || directDepth <= 0f) return;

        float cx = directCenter.x;
        float cz = directCenter.z;
        float halfW = directWidth * 0.5f;
        float halfD = directDepth * 0.5f;
        float rad = directAngle * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rad);
        float sin = Mathf.Sin(rad);

        // Four corners of the rectangle rotated around its center (relative to center_point). Rotate local (±halfW, ±halfD).
        Vector2[] local =
        {
            new Vector2(-halfW, -halfD),
            new Vector2( halfW, -halfD),
            new Vector2( halfW,  halfD),
            new Vector2(-halfW,  halfD),
        };
        float[][] polygon = new float[4][];
        for (int i = 0; i < 4; i++)
        {
            float rx = local[i].x * cos - local[i].y * sin;
            float rz = local[i].x * sin + local[i].y * cos;
            polygon[i] = new float[] { rx, rz };
        }

        // BuildingMeshBuilder recalculates height from floors, so height can be an approximate reference value.
        float heightEstimate = Mathf.Max(1, currentFloors) * 3.0f;
        cachedPolygon = polygon;
        cachedCenterPoint = new float[] { cx, cz };
        cachedHeight = heightEstimate;
        BuildAndShowPreviewMesh(polygon, currentFloors, new float[] { cx, cz }, heightEstimate);

        directPreviewVisible = true;
        lastGhostCenter = directCenter;
        lastGhostAngle = directAngle;
    }

    /// <summary>
    /// Processes middle-button drag rotation for direct placement every frame.
    /// Rotates locally and immediately in both phases A/B, reporting the final angle to the UI once
    /// through edit_building:placement_state on button release (nothing is sent during the drag).
    /// Do not rotate in roadside placement or while positioning free placement snapped to a road (phase A), as the road sets the orientation.
    /// </summary>
    private void Update()
    {
        if (!UpdatePlacementInput()) return;
        if (PlacementMode == BuildingPlacementMode.Roadside || (roadAnchorActive && !directCommitted)) return;
        if (!isBuildingModeActive || isEditMode || !hasDirectParams || !directPreviewVisible)
        {
            // Clear the state if the mode is left during a drag, etc.
            middleDrag.Cancel();
            return;
        }

        switch (middleDrag.Update(out int deltaDegrees))
        {
            case MiddleDragRotation.Result.Started:
                placementRevision++;
                break;
            case MiddleDragRotation.Result.Rotated:
                RotateFree(directAngle + deltaDegrees);
                BuildDirectRectanglePreview();
                break;
            case MiddleDragRotation.Result.Released:
                if (IpcClient.Instance != null)
                {
                    EmitPlacementState();
                }
                break;
        }
    }

    /// <summary>
    /// Sets preview meshes from BuildingMeshResult.
    /// </summary>
    /// <param name="meshResult">Building mesh generation result</param>
    /// <param name="centerPosition">Building center (world coordinates)</param>
    private void SetBuildingPreviewMesh(BuildingMeshResult meshResult, Vector3 centerPosition, Material previewMaterial = null)
    {
        if (meshResult == null || meshResult.Mesh == null)
        {
            Debug.LogWarning("BuildingPlanManager: meshResult が null または Mesh が null です。");
            return;
        }

        if (previewMeshFilter == null || previewMeshRenderer == null)
        {
            Debug.LogWarning("BuildingPlanManager: Preview Mesh Components are missing.");
            return;
        }

        if (previewMesh != null && previewMesh != meshResult.Mesh) Destroy(previewMesh);
        previewMesh = meshResult.Mesh;
        previewMeshFilter.mesh = previewMesh;

        // Set materials.
        if (previewMaterial != null)
        {
            // If a preview material is specified, apply it to all submeshes.
            int subMeshCount = previewMesh.subMeshCount;
            Material[] mats = new Material[subMeshCount];
            for (int i = 0; i < subMeshCount; i++)
            {
                mats[i] = previewMaterial;
            }
            previewMeshRenderer.sharedMaterials = mats;
        }
        else if (meshResult.Materials != null && meshResult.Materials.Length > 0)
        {
            previewMeshRenderer.sharedMaterials = meshResult.Materials;
        }

        // Set the position.
        previewMeshFilter.transform.position = centerPosition;
    }

    /// <summary>
    /// Plays the specified AudioClip.
    /// Does nothing if the clip or AudioSource is unset.
    /// </summary>
    private void PlaySound(AudioClip clip)
    {
        if (clip == null || audioSource == null) return;
        audioSource.PlayOneShot(clip);
    }

    /// <summary>
    /// Clears the preview (removes the mesh).
    /// </summary>
    private void ClearPreview()
    {
        if (previewMesh != null)
        {
            Destroy(previewMesh);
            previewMesh = null;
        }
        if (previewMeshFilter != null)
        {
            previewMeshFilter.mesh = null;
        }
        // Also clear the preview geometry cache.
        cachedPolygon = null;
        cachedCenterPoint = null;
        cachedHeight = 0f;
    }

    // ======================================================================
    // Building type and parcel type compatibility utilities
    // ======================================================================

    /// <summary>
    /// Returns the parcel type (PARCEL_*) on which the building can be located.
    /// Uses agent_master.parcel_type directly when a variant is selected in construction mode.
    /// Otherwise (when a display layer filter selects only bd_type),
    /// defaults to coastal for ports and land for everything else.
    /// </summary>
    public static int ResolveRequiredParcelType(int bdType)
    {
        if (SelectedParcelType > 0) return SelectedParcelType;
        return (bdType & Constants.BUILDING_PORT) != 0 ? Constants.PARCEL_SHORE : Constants.PARCEL_LAND;
    }

    /// <summary>
    /// Tests whether parcel_type satisfies the building location requirements.
    /// </summary>
    public static bool IsParcelTypeAllowed(int parcelType, int bdType)
    {
        return parcelType == ResolveRequiredParcelType(bdType);
    }

    /// <summary>
    /// Returns the parcel display filter subMode string for bd_type.
    /// Used to set IpcViewCoordinator._layerSubModes.
    /// </summary>
    public static string BdTypeToParcelFilterSubMode(int bdType)
    {
        if (bdType == Constants.BUILDING_APARTMENT) return "BUILDING_APARTMENT";
        if (bdType == Constants.BUILDING_HOUSE) return "BUILDING_HOUSE";
        if (bdType == Constants.BUILDING_RETAIL) return "BUILDING_RETAIL";
        if (bdType == Constants.BUILDING_FACTORY) return "BUILDING_FACTORY";
        if (bdType == Constants.BUILDING_OFFICE) return "BUILDING_OFFICE";
        if (bdType == Constants.BUILDING_FARM) return "BUILDING_FARM";
        if (bdType == Constants.BUILDING_MINE) return "BUILDING_MINE";
        if (bdType == Constants.BUILDING_PORT) return "BUILDING_PORT";
        if (bdType == Constants.BUILDING_OTHER) return "BUILDING_OTHER";
        if (bdType == Constants.BUILDING_PUBLIC) return "BUILDING_PUBLIC";
        return null;
    }
}
