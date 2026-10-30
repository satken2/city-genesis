using System;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// Coordinator receiving world.change_state via IPC
/// and switching InputHandler + VisualModeHandler states together.
/// </summary>
public class IpcStateCoordinator : MonoBehaviour
{
    // Sound played for click deletion in bulldoze mode.
    // Assign Assets/Sounds/trash.mp3 in the Inspector.
    [SerializeField] private AudioClip trashSound;

    /// <summary>
    /// State actually applied by Unity. Fires on world:change_state and on internal normal-mode restoration
    /// such as ending editing on panel close or Esc
    /// (tracking only IPC state would miss internal restoration).
    /// </summary>
    public static event Action<string> OnStateApplied;

    private void OnEnable()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnWorldChangeState += HandleChangeState;
            IpcClient.Instance.OnEditParcelUpdatePreview += HandleEditParcelUpdatePreview;
            IpcClient.Instance.OnEditParcelResetSelection += HandleEditParcelResetSelection;
            IpcClient.Instance.OnEditParcelSetRectParams += HandleEditParcelSetRectParams;
            IpcClient.Instance.OnEditParcelStartShapeEdit += HandleEditParcelStartShapeEdit;
            IpcClient.Instance.OnEditParcelCancelShapeEdit += HandleEditParcelCancelShapeEdit;
            IpcClient.Instance.OnEditParcelFinishShapeEdit += HandleEditParcelFinishShapeEdit;
            IpcClient.Instance.OnSessionReset += HandleSessionReset;
            IpcClient.Instance.OnSessionReload += HandleSessionReload;
            IpcClient.Instance.OnSessionForceReload += HandleSessionForceReload;
            IpcClient.Instance.OnSystemShutdown += HandleSystemShutdown;
            IpcClient.Instance.OnZoneUpdated += HandleZoneUpdated;
            IpcClient.Instance.OnEditConnectorShow += HandleEditConnectorShow;
            IpcClient.Instance.OnEditConnectorHide += HandleEditConnectorHide;
            IpcClient.Instance.OnEditPathCancelTopologyEdit += HandleEditPathCancelTopologyEdit;
            IpcClient.Instance.OnEditPathFinishTopologyEdit += HandleEditPathFinishTopologyEdit;
            IpcClient.Instance.OnBuildingConstructionPickSource += HandleBuildingConstructionPickSource;
            IpcClient.Instance.OnEditRoadCancel += HandleEditRoadCancel;
            IpcClient.Instance.OnEditGradingFinish += HandleEditGradingFinish;
        }
    }

    private void OnDisable()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnWorldChangeState -= HandleChangeState;
            IpcClient.Instance.OnEditParcelUpdatePreview -= HandleEditParcelUpdatePreview;
            IpcClient.Instance.OnEditParcelResetSelection -= HandleEditParcelResetSelection;
            IpcClient.Instance.OnEditParcelSetRectParams -= HandleEditParcelSetRectParams;
            IpcClient.Instance.OnEditParcelStartShapeEdit -= HandleEditParcelStartShapeEdit;
            IpcClient.Instance.OnEditParcelCancelShapeEdit -= HandleEditParcelCancelShapeEdit;
            IpcClient.Instance.OnEditParcelFinishShapeEdit -= HandleEditParcelFinishShapeEdit;
            IpcClient.Instance.OnSessionReset -= HandleSessionReset;
            IpcClient.Instance.OnSessionReload -= HandleSessionReload;
            IpcClient.Instance.OnSessionForceReload -= HandleSessionForceReload;
            IpcClient.Instance.OnSystemShutdown -= HandleSystemShutdown;
            IpcClient.Instance.OnZoneUpdated -= HandleZoneUpdated;
            IpcClient.Instance.OnEditConnectorShow -= HandleEditConnectorShow;
            IpcClient.Instance.OnEditConnectorHide -= HandleEditConnectorHide;
            IpcClient.Instance.OnEditPathCancelTopologyEdit -= HandleEditPathCancelTopologyEdit;
            IpcClient.Instance.OnEditPathFinishTopologyEdit -= HandleEditPathFinishTopologyEdit;
            IpcClient.Instance.OnBuildingConstructionPickSource -= HandleBuildingConstructionPickSource;
            IpcClient.Instance.OnEditRoadCancel -= HandleEditRoadCancel;
            IpcClient.Instance.OnEditGradingFinish -= HandleEditGradingFinish;
        }
    }

    /// <summary>
    /// Building copy construction: enter source selection.
    /// Enable only building clicks (MODE_PICK_BUILDING); BuildingInputHandler sends
    /// building_construction:copy_source on selection. UI then drives transition to STATE_EDIT_BUILDING.
    /// </summary>
    private void HandleBuildingConstructionPickSource()
    {
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.PICK_BUILDING);
        }

        // Enable all building clicks/hover, excluding planned buildings: copy sources must be active/completed real buildings.
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_NORMAL);
        }

        Debug.Log("IpcStateCoordinator: コピー元建物の選択モードに入りました");
    }

    /// <summary>
    /// Handles world.change_state.
    /// Switches input/display together by state (IpcViewCoordinator.SyncFromState remembers display and notifies the UI).
    /// </summary>
    private void HandleChangeState(string state, Dictionary<string, object> ipcParams)
    {
        Debug.Log($"IpcStateCoordinator: ステート変更 -> {state}");

        // On state changes, cancel active grid angle setting unless the destination is STATE_SET_GRID.
        if (state != "STATE_SET_GRID" && GridSetManager.Instance != null)
        {
            GridSetManager.Instance.CancelGridSetting();
        }

        // End parcel creation even when transitioning directly to another state.
        // Otherwise ParcelEditControlMenu (Ctrl radial menu) and cursor HUD survive into other modes
        // (ApplyStateNormal also ends them on STATE_NORMAL; idempotence makes this safe).
        if (state != "STATE_EDIT_PARCEL" && ParcelEditorManager.Instance != null && ParcelEditorManager.Instance.IsActive)
        {
            ParcelEditorManager.Instance.ExitCreationMode();
        }

        // Also clear merge/split candidate colors and split markers when transitioning directly
        // to another mode (ApplyStateNormal also ends them on STATE_NORMAL;
        // idempotence makes this safe).
        if (state != "STATE_MERGE_PATH" && state != "STATE_SPLIT_PATH"
            && PathTopologyEditManager.Instance != null && PathTopologyEditManager.Instance.IsActive)
        {
            PathTopologyEditManager.Instance.EndMode();
        }

        // Always clear section-edit bands when transitioning directly to another mode
        // (End sends edit_road:ended so open road detail panels clear their editing indicator).
        if (state != "STATE_EDIT_ROAD_MEMBERS" && RoadMemberEditManager.Instance != null && RoadMemberEditManager.Instance.IsActive)
        {
            RoadMemberEditManager.Instance.End();
        }

        // Clear earthworks polygons/overlays when transitioning directly to another mode
        // (ApplyStateNormal also ends them on STATE_NORMAL; idempotence makes this safe).
        if (state != "STATE_EDIT_GRADING")
        {
            FindAnyObjectByType<GradingAreaEditManager>()?.FinishEditMode();
        }

        // On direct transitions from connector editing, stop accepting terminal drags and return
        // to read-only highlighting. edit_connector:hide uses this flag to detect editing,
        // so it must be cleared even when bypassing STATE_NORMAL.
        if (state != "STATE_EDIT_CONNECTOR")
        {
            ConnectorEditManager.Instance?.ExitEditMode();
        }

        // Show one-way track arrows only in road construction mode
        // (they help connect existing tracks as direction pairs).
        TrackDirectionArrows.SetVisible(state == "STATE_EDIT_PATH");

        switch (state)
        {
            case "STATE_NORMAL":
                ApplyStateNormal();
                break;

            case "STATE_EDIT_PATH":
                ApplyStateEditPath(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_EDIT_BUILDING":
                ApplyStateEditBuilding(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_EDIT_PARCEL":
                ApplyStateEditParcel(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_EDIT_PARCEL_SHAPE":
                ApplyStateEditParcelShape(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_SET_GRID":
                ApplyStateSetGrid(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_EDIT_RESOURCE":
                ApplyStateEditResource(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_EDIT_GRADING":
                ApplyStateEditGrading(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_EDIT_TRANSIT":
                ApplyStateEditTransit(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_EDIT_PROP":
                ApplyStateEditProp(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_EDIT_CONNECTOR":
                // Connector edit mode. Keep the initiating AgentDetailPanel
                // in front, so unlike other states do not call WindowUtil.BringToForeground()
                // (bringing Unity forward would make the right panel unusable).
                ApplyStateEditConnector(ipcParams);
                break;

            case "STATE_REMOVE_OBJECTS":
                ApplyStateRemoveObjects(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_MERGE_PATH":
            case "STATE_SPLIT_PATH":
                // Path merge/split selection. The user must keep operating PathDetailPanel
                // to inspect preview lengths and confirm, so as with connector editing,
                // do not call WindowUtil.BringToForeground()
                // (bringing Unity forward would make the panel unusable).
                ApplyStatePathTopologyEdit(state, ipcParams);
                break;

            case "STATE_EDIT_ROAD_MEMBERS":
                // Road section editing. The detail panel updates membership/results on each click,
                // so do not call WindowUtil.BringToForeground(), as with path merging.
                ApplyStateEditRoadMembers(ipcParams);
                break;

            default:
                Debug.LogWarning($"IpcStateCoordinator: 未知のステート: {state}");
                return;
        }

        // Notify the UI of display mode changes accompanying state changes.
        var coordinator = FindAnyObjectByType<IpcViewCoordinator>();
        if (coordinator != null)
        {
            coordinator.SyncFromState(state, ipcParams);

            // For STATE_NORMAL, apply all _layerModes restored by SyncFromState
            // to VisualModeHandler, reflecting the remembered normal display state.
            if (state == "STATE_NORMAL")
            {
                coordinator.ApplyAllLayerModes();
            }
        }

        OnStateApplied?.Invoke(state);
    }

    /// <summary>
    /// Returns to normal mode for internal Unity reasons using the same procedure as UI STATE_NORMAL.
    /// Restore the remembered normal display before applying all modes (see ApplyStateNormal notes).
    /// </summary>
    private void ReturnToNormalState()
    {
        ApplyStateNormal();

        var coordinator = FindAnyObjectByType<IpcViewCoordinator>();
        if (coordinator != null)
        {
            coordinator.SyncFromState("STATE_NORMAL");
            coordinator.ApplyAllLayerModes();
        }

        OnStateApplied?.Invoke("STATE_NORMAL");
    }

    /// <summary>
    /// STATE_NORMAL: normal mode
    /// </summary>
    private void ApplyStateNormal()
    {
        // InputMode: set all objects to NORMAL.
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.ALL_NORMAL);
        }

        // Apply VisualMode after SyncFromState in HandleChangeState,
        // not here: IpcViewCoordinator._layerModes still contains edit mode values
        // at this point. Applying them before restoring remembered normal state
        // would leave the edit display active.

        // Return the road editor to its finished state.
        var roadPlanManager = FindAnyObjectByType<RoadPlanManager>();
        if (roadPlanManager != null)
        {
            roadPlanManager.FinishRoadPlacement();
        }

        // End parcel shape editing.
        var parcelShapeEditManager = FindAnyObjectByType<ParcelShapeEditManager>();
        if (parcelShapeEditManager != null)
        {
            parcelShapeEditManager.FinishEditMode();
        }

        // End resource area editing.
        var resourceAreaEditManager = FindAnyObjectByType<ResourceAreaEditManager>();
        if (resourceAreaEditManager != null)
        {
            resourceAreaEditManager.FinishEditMode();
        }

        // End earthworks area drawing.
        var gradingAreaEditManager = FindAnyObjectByType<GradingAreaEditManager>();
        if (gradingAreaEditManager != null)
        {
            gradingAreaEditManager.FinishEditMode();
        }

        // End public transport line placement.
        var transitPlanManager = FindAnyObjectByType<TransitPlanManager>();
        if (transitPlanManager != null)
        {
            transitPlanManager.FinishPlacement();
        }

        // End prop placement.
        var propPlacementManager = FindAnyObjectByType<PropPlacementManager>();
        if (propPlacementManager != null)
        {
            propPlacementManager.FinishPlacement();
        }

        // End building construction mode.
        // The UI sends STATE_NORMAL on both confirmation and cancellation;
        // failing to end construction here would leave previews generated by edit_building:update_shape visible.
        // (The UI submits construction directly to the backend; success sends session:reload and STATE_NORMAL.)
        var buildingPlanManager = FindAnyObjectByType<BuildingPlanManager>();
        if (buildingPlanManager != null)
        {
            buildingPlanManager.SetBuildingMode(false);
        }

        // End parcel creation (clear selection, preview, Ctrl radial menu/cursor HUD).
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager != null)
        {
            parcelEditorManager.ExitCreationMode();
        }

        // End connector edit mode (markers return to read-only highlighting).
        // The detail panel may remain open, so retain markers until
        // edit_connector:hide arrives on panel closure.
        var connectorEditManager = FindAnyObjectByType<ConnectorEditManager>();
        if (connectorEditManager != null)
        {
            connectorEditManager.ExitEditMode();
        }

        // End bulldoze mode, clearing state so the previous handled set
        // does not survive reentry.
        if (RemovalManager.Instance != null)
        {
            RemovalManager.Instance.EndRemovalMode();
        }

        // End merge/split selection (clear candidate path colors and split markers).
        // The UI sends STATE_NORMAL on both confirmation and cancellation.
        if (PathTopologyEditManager.Instance != null)
        {
            PathTopologyEditManager.Instance.EndMode();
        }

        // End road section editing (clear edit bands, retain road detail highlighting).
        RoadMemberEditManager.Instance?.End();
    }

    /// <summary>
    /// STATE_REMOVE_OBJECTS: bulldoze (object deletion) mode.
    /// Left click removes empty parcels and planned buildings/roads (dragging does not delete).
    /// RemovalManager handles eligibility, DELETE requests, and deduplication
    /// (Unity submits directly to the backend; incremental reload reflects success).
    /// Hide districts to avoid accidentally opening their panels during deletion.
    /// With ipcParams instant_remove=true (set by the UI in scenario authoring mode),
    /// delete buildings/roads immediately regardless of status (Unity follows the UI flag without knowing scenario mode).
    /// </summary>
    private void ApplyStateRemoveObjects(Dictionary<string, object> ipcParams)
    {
        // InputMode: deletion preset (buildings/parcels/roads/terrain use MODE_REMOVE).
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.REMOVE_OBJECTS);
        }

        // IpcViewCoordinator.SyncFromState applies VisualMode (state-specific display presets).

        // Get or create RemovalManager and initialize handled sets.
        var removalManager = RemovalManager.Instance;
        if (removalManager == null)
        {
            var go = new GameObject("RemovalManager");
            removalManager = go.AddComponent<RemovalManager>();
        }
        removalManager.SetClickSound(trashSound);
        bool instantRemove = ipcParams != null
            && ipcParams.TryGetValue("instant_remove", out var ir)
            && System.Convert.ToBoolean(ir);
        removalManager.BeginRemovalMode(instantRemove);
    }

    /// <summary>
    /// STATE_MERGE_PATH / STATE_SPLIT_PATH: merge partner / split-point selection.
    /// ipcParams path_id is the target path open in the detail panel
    /// (merge survivor or split source).
    ///
    /// Match road construction display (show plans, hide districts). PathTopologyEditManager
    /// colors target/candidate paths and draws split markers.
    /// </summary>
    private void ApplyStatePathTopologyEdit(string state, Dictionary<string, object> ipcParams)
    {
        bool isMerge = state == "STATE_MERGE_PATH";

        int pathId = 0;
        if (ipcParams != null && ipcParams.TryGetValue("path_id", out object pidObj) && pidObj != null)
        {
            pathId = Convert.ToInt32(pidObj);
        }
        if (pathId <= 0)
        {
            Debug.LogWarning($"IpcStateCoordinator: {state} に path_id が指定されていません");
            return;
        }

        // InputMode: merge picks paths only; split picks paths + terrain to allow points beside paths.
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(
                isMerge ? InputModePreset.PATH_MERGE : InputModePreset.PATH_SPLIT);
        }

        // IpcViewCoordinator.SyncFromState applies VisualMode (state-specific display presets).

        var manager = PathTopologyEditManager.Instance;
        if (manager == null)
        {
            var go = new GameObject("PathTopologyEditManager");
            manager = go.AddComponent<PathTopologyEditManager>();
        }

        if (isMerge) manager.BeginMergeMode(pathId);
        else manager.BeginSplitMode(pathId);
    }

    /// <summary>
    /// STATE_EDIT_ROAD_MEMBERS: road section editing.
    /// params is {road_id, color, path_ids, session_id} (initial member sections; session_id is an optional
    /// correlation value echoed unchanged through edit_road:ended). Only street/highway sections are clickable;
    /// RoadMemberEditManager sends clicks through edit_road:toggle_path.
    /// Show plans like road construction so planned sections can also be selected (SyncFromState).
    /// </summary>
    private void ApplyStateEditRoadMembers(Dictionary<string, object> ipcParams)
    {
        int roadId = 0;
        if (ipcParams != null && ipcParams.TryGetValue("road_id", out object ridObj) && ridObj != null)
        {
            roadId = Convert.ToInt32(ridObj);
        }
        if (roadId <= 0)
        {
            Debug.LogWarning("IpcStateCoordinator: STATE_EDIT_ROAD_MEMBERS に road_id が指定されていません");
            return;
        }
        string color = ipcParams.TryGetValue("color", out object colorObj) ? colorObj?.ToString() : null;
        int[] pathIds = ipcParams.TryGetValue("path_ids", out object idsObj) ? ReadIntArray(idsObj) : System.Array.Empty<int>();
        object sessionId = ipcParams.TryGetValue("session_id", out object sessionObj) ? ReadSessionId(sessionObj) : null;

        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.ROAD_MEMBER_EDIT);
        }

        // IpcViewCoordinator.SyncFromState applies VisualMode (state-specific display presets).

        RoadMemberEditManager.Ensure().Begin(roadId, RoadManager.ParseColor(color), pathIds, sessionId);
    }

    /// <summary>
    /// Converts params.session_id to an echoable value preserving its JSON type (integer→long, string→string).
    /// The UI strictly compares session_id in edit_road:ended; do not convert numbers to strings.
    /// Other types (null, decimal, array, etc.) cannot be correlated; use null (omit when returning).
    /// </summary>
    private static object ReadSessionId(object value)
    {
        if (value is Newtonsoft.Json.Linq.JValue jValue) value = jValue.Value;
        switch (value)
        {
            case string s: return s;
            case long l: return l;
            case int i: return (long)i;
            default: return null;
        }
    }

    /// <summary>
    /// edit_road:cancel {road_id}: Done in the road detail panel or its window closure
    /// (Rust sends on closure because hiding does not unmount React;
    /// same reason as edit_path:cancel_topology_edit). End editing and return to normal only for that road.
    /// Do nothing for another road or outside the mode, so stale Done actions or unrelated panel closure
    /// cannot end road construction or another road's editing.
    /// </summary>
    private void HandleEditRoadCancel(int roadId)
    {
        var manager = RoadMemberEditManager.Instance;
        if (manager == null || !manager.IsActive || manager.RoadId != roadId) return;

        manager.End();
        ReturnToNormalState();
    }

    /// <summary>Esc during section editing (called by RoadMemberEditManager). Returns to normal mode.</summary>
    public void ExitRoadMemberEditToNormal()
    {
        var manager = RoadMemberEditManager.Instance;
        if (manager == null || !manager.IsActive) return;

        manager.End();
        ReturnToNormalState();
    }

    /// <summary>
    /// edit_path:finish_topology_edit: merge/split confirmed (UI backend submission succeeded).
    ///
    /// One handler ends selection, immediately destroys merged-away paths, restores normal mode, and refetches data
    /// in that order. Separate `session:reload` and `world:change_state` messages can race
    /// incremental reload coroutines with state transitions and leave display stale,
    /// so combine them as with road construction `edit_path:finish`.
    ///
    /// removedPathId is the path absorbed by merging (0 for splitting), passed explicitly to guarantee removal
    /// without waiting for incremental reload's missing-response-path deletion check.
    /// </summary>
    private void HandleEditPathFinishTopologyEdit(int removedPathId)
    {
        PathTopologyEditManager.Instance?.EndMode();

        if (removedPathId > 0)
        {
            FindAnyObjectByType<PathManager>()?.RemovePathLocally(removedPathId);
        }

        ReturnToNormalState();

        // Begin refetching last (a later state transition could cancel the incremental reload
        // coroutine and leave deletion/regeneration incomplete).
        CellDataLoader.Instance?.ReloadAllData();
    }

    /// <summary>
    /// edit_path:cancel_topology_edit: path detail window closed.
    /// Abort active merge/split selection and return to normal.
    ///
    /// Closing only hides the window for reuse, without destroying it or running React
    /// unmount cleanup, so relying on panel cleanup would leave selection mode
    /// active and input stuck. Do nothing outside selection mode; this dedicated event avoids
    /// affecting bulldozing or other modes.
    /// </summary>
    private void HandleEditPathCancelTopologyEdit()
    {
        var manager = PathTopologyEditManager.Instance;
        if (manager == null || !manager.IsActive) return;

        manager.EndMode();
        ReturnToNormalState();
    }

    /// <summary>
    /// STATE_EDIT_PATH: road construction.
    /// ipcParams contains UI-selected path_type / path_variant / path_id.
    /// A supplied path_id enables existing path editing.
    /// </summary>
    private void ApplyStateEditPath(Dictionary<string, object> ipcParams)
    {
        // InputMode: road construction preset
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.ROAD_CONSTRUCTION);
        }

        // IpcViewCoordinator.SyncFromState applies VisualMode (state-specific display presets).

        // Initialize RoadPlanManager in road construction mode.
        var roadPlanManager = FindAnyObjectByType<RoadPlanManager>();
        if (roadPlanManager != null)
        {
            // Read and set path_type / path_variant / path_id from IPC parameters.
            int? pathId = null;
            // Compound path (parallel track) offsets/directions. Always reset to empty (single path) if absent
            // to avoid retaining prior session offsets and showing parallel previews for roads, etc.
            float[] lateralOffsets = System.Array.Empty<float>();
            int[] travelDirections = System.Array.Empty<int>();
            if (ipcParams != null)
            {
                if (ipcParams.TryGetValue("path_id", out object pidObj) && pidObj != null)
                {
                    pathId = Convert.ToInt32(pidObj);
                    roadPlanManager.SetPathId(pathId.Value);
                    Debug.Log($"IpcStateCoordinator: path_id = {pathId.Value} (existing path edit mode)");
                }
                if (ipcParams.TryGetValue("path_type", out object ptObj) && ptObj != null)
                {
                    int pathType = Convert.ToInt32(ptObj);
                    roadPlanManager.SetPathType(pathType);
                    Debug.Log($"IpcStateCoordinator: path_type = {pathType}");
                }
                if (ipcParams.TryGetValue("path_variant", out object pvObj) && pvObj != null)
                {
                    int pathVariant = Convert.ToInt32(pvObj);
                    roadPlanManager.SetPathSubtype(pathVariant);
                    Debug.Log($"IpcStateCoordinator: path_variant = {pathVariant}");
                }
                // Station platform side, supplied as the construction mode initial value
                // (subsequent changes arrive through edit_path:change_platform_side).
                if (ipcParams.TryGetValue("platform_side", out object psObj) && psObj != null)
                {
                    roadPlanManager.PlatformSide = Convert.ToInt32(psObj);
                    Debug.Log($"IpcStateCoordinator: platform_side = {roadPlanManager.PlatformSide}");
                }
                // Compound path (parallel track) offsets/directions, supplied as construction mode initial values
                // (subsequent count/direction changes arrive through edit_path:change_lateral_offsets).
                if (ipcParams.TryGetValue("lateral_offsets", out object loObj) && loObj != null)
                {
                    lateralOffsets = ReadFloatArray(loObj);
                    Debug.Log($"IpcStateCoordinator: lateral_offsets = {lateralOffsets.Length}本");
                }
                if (ipcParams.TryGetValue("travel_directions", out object tdObj) && tdObj != null)
                {
                    travelDirections = ReadIntArray(tdObj);
                }
            }
            roadPlanManager.HouseEnds = ipcParams != null && ipcParams.TryGetValue("house_ends", out var he)
                ? Convert.ToString(he) : "both";
            roadPlanManager.PassageMode = ipcParams != null && ipcParams.TryGetValue("passage_mode", out var pm)
                ? Convert.ToString(pm) : "elevated";
            roadPlanManager.LateralOffsets = lateralOffsets;
            roadPlanManager.TravelDirections = travelDirections;
            // Construction method changes click steps. Default to single if absent; do not retain the previous session method.
            roadPlanManager.BuildMethod = ipcParams != null && ipcParams.TryGetValue("build_method", out var bm) && bm != null
                ? Convert.ToInt32(bm) : Constants.PATH_BUILD_METHOD_SINGLE;
            if (ipcParams != null && ipcParams.TryGetValue("grid_divisions", out var gd) && gd != null)
            {
                roadPlanManager.SetGridDivisions(ReadIntArray(gd));
            }

            roadPlanManager.InitializeRoadPlacement(pathId,
                ipcParams != null && ipcParams.TryGetValue("initial_height", out var ih) ? System.Convert.ToSingle(ih) : (float?)null,
                ipcParams != null && ipcParams.TryGetValue("initial_embankment_height", out var ie) ? System.Convert.ToSingle(ie) : (float?)null,
                ipcParams != null && ipcParams.TryGetValue("initial_curved", out var ic) ? System.Convert.ToBoolean(ic) : (bool?)null,
                ipcParams != null && ipcParams.TryGetValue("initial_follow_terrain", out var it) ? System.Convert.ToInt32(it) : (int?)null);
        }
    }

    /// <summary>
    /// STATE_EDIT_BUILDING: building construction.
    /// ipcParams contains UI-selected building_variant / agent_id.
    /// Scenario authoring landmarks also include landmark (landmark type).
    /// Optional placement_mode specifies initial parcel/free/roadside placement.
    /// A supplied agent_id enables existing building editing.
    ///
    /// Parcel display uses the buildable filter (buildable_color) for the selected building's bd_type,
    /// highlighting only eligible parcels.
    /// </summary>
    private void ApplyStateEditBuilding(Dictionary<string, object> ipcParams)
    {
        int? agentId = null;
        if (ipcParams != null && ipcParams.TryGetValue("agent_id", out object aidObj) && aidObj != null)
        {
            agentId = Convert.ToInt32(aidObj);
        }

        // InputMode: building construction preset
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.BUILDING_CONSTRUCTION);
        }

        // IpcViewCoordinator.SyncFromState applies VisualMode (state-specific display presets).
        // It also sets the parcel layer submode to the buildable filter
        // resolved from the selected building type.

        // Initialize BuildingPlanManager in building construction mode.
        var buildingPlanManager = FindAnyObjectByType<BuildingPlanManager>();
        if (buildingPlanManager != null)
        {
            if (ipcParams != null)
            {
                if (ipcParams.TryGetValue("building_variant", out object bvObj) && bvObj != null)
                {
                    int buildingVariant = Convert.ToInt32(bvObj);
                    buildingPlanManager.SetBuildingVariant(buildingVariant);
                    Debug.Log($"IpcStateCoordinator: building_variant = {buildingVariant}");
                }
                // Landmark type (0=not a landmark). Preview part folders vary by landmark.
                if (ipcParams.TryGetValue("landmark", out object lmObj) && lmObj != null)
                {
                    buildingPlanManager.SetLandmark(Convert.ToInt32(lmObj));
                }
            }

            if (agentId.HasValue && agentId.Value > 0)
            {
                buildingPlanManager.SetEditMode(agentId.Value);
                Debug.Log($"IpcStateCoordinator: agent_id = {agentId.Value} (existing building edit mode)");
            }
            else
            {
                buildingPlanManager.SetNewBuildingMode();
            }

            buildingPlanManager.SetBuildingMode(true);
            buildingPlanManager.DirectAllowed = !agentId.HasValue &&
                !(ipcParams != null && ipcParams.TryGetValue("copy_source_agent_id", out var copyId) && copyId != null);

            // Initial placement_mode: parcel/free/roadside. Defaults to parcel placement.
            // The UI chooses contextually (founding City Hall usually has no roads/parcels, so starts in free placement).
            // SetPlacementMode requires PlacementActive and DirectAllowed; apply here only after both are ready.
            if (ipcParams != null && ipcParams.TryGetValue("placement_mode", out object pmObj) && pmObj is string pmText &&
                Enum.TryParse(pmText, true, out BuildingPlacementMode initialMode))
            {
                buildingPlanManager.SetPlacementMode(initialMode);
            }
        }
    }

    /// <summary>
    /// STATE_EDIT_PARCEL: parcel creation.
    /// ipcParams contains UI-selected parcel_creation_mode (default if omitted).
    /// Subsequent method changes use the 3D Ctrl radial menu (ParcelEditControlMenu);
    /// this state only enters creation mode and configures display mode.
    /// </summary>
    private void ApplyStateEditParcel(Dictionary<string, object> ipcParams)
    {
        string creationMode = ParcelEditorManager.DefaultMode;
        if (ipcParams != null && ipcParams.TryGetValue("parcel_creation_mode", out object modeObj) && modeObj != null)
        {
            creationMode = modeObj.ToString();
        }

        // IpcViewCoordinator.SyncFromState applies VisualMode (state-specific display presets).

        // Applying creation mode (input presets, selection reset, arbitrary polygon initialization)
        // is centralized in authoritative ParcelEditorManager. Use the exact same path
        // as Unity radial menu switches to avoid differing implementations.
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager == null)
        {
            Debug.LogError("IpcStateCoordinator: ParcelEditorManagerが見つかりません");
            return;
        }
        parcelEditorManager.EnterCreationMode(creationMode);

        // Keep the Ctrl radial menu/cursor HUD driver for creation mode on the same GameObject
        // (it autonomously opens/closes by polling IsActive, so initialization alone is sufficient here).
        var controlMenu = parcelEditorManager.GetComponent<ParcelEditControlMenu>();
        if (controlMenu == null)
        {
            controlMenu = parcelEditorManager.gameObject.AddComponent<ParcelEditControlMenu>();
        }
        controlMenu.Initialize(parcelEditorManager);

        Debug.Log($"IpcStateCoordinator: 土地区画作成モード -> {creationMode}");
    }

    /// <summary>
    /// STATE_EDIT_PARCEL_SHAPE: parcel shape editing.
    /// ipcParams contains parcel_id and polygon.
    /// Initialize ParcelShapeEditManager to start vertex editing.
    /// </summary>
    private void ApplyStateEditParcelShape(Dictionary<string, object> ipcParams)
    {
        int parcelId = 0;
        float[][] polygon = null;
        if (ipcParams != null)
        {
            if (ipcParams.TryGetValue("parcel_id", out var pidObj) && pidObj != null)
            {
                parcelId = Convert.ToInt32(pidObj);
            }
            // world.change_state does not include polygon in params.
            // edit_parcel:start_shape_edit is handled directly by HandleEditParcelStartShapeEdit.
        }

        // InputMode: terrain shape editing; all others disabled.
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.PARCEL_SHAPE_EDIT);
        }

        // VisualMode: show plans.
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_BOUNDARY);
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetFlags(InputObjectType.PATH, VisualFlag.SHOW_PLAN);
            VisualModeHandler.Instance.SetFlags(InputObjectType.BUILDING, VisualFlag.SHOW_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_HIDDEN);
        }

        // Initialize ParcelShapeEditManager.
        var shapeEditManager = FindAnyObjectByType<ParcelShapeEditManager>();
        if (shapeEditManager == null)
        {
            var go = new GameObject("ParcelShapeEditManager");
            shapeEditManager = go.AddComponent<ParcelShapeEditManager>();
        }

        if (parcelId > 0 && polygon != null)
        {
            shapeEditManager.InitializeEditMode(parcelId, polygon);
        }

        Debug.Log($"IpcStateCoordinator: 土地区画形状編集モード -> parcel={parcelId}");
    }

    /// <summary>
    /// STATE_EDIT_RESOURCE: resource area editing (new creation).
    /// Sent when the UI selects a resource type (ipcParams contains resource_type,
    /// but the type is UI-only state and does not affect Unity polygon editing).
    /// </summary>
    private void ApplyStateEditResource(Dictionary<string, object> ipcParams)
    {
        // InputMode: resource area editing preset
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.EDIT_RESOURCE);
        }

        // VisualMode: show plans (same as parcel shape editing; hide districts).
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_BOUNDARY);
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetFlags(InputObjectType.PATH, VisualFlag.SHOW_PLAN);
            VisualModeHandler.Instance.SetFlags(InputObjectType.BUILDING, VisualFlag.SHOW_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_HIDDEN);
        }

        // Initialize ResourceAreaEditManager in new creation mode.
        var resourceAreaEditManager = FindAnyObjectByType<ResourceAreaEditManager>();
        if (resourceAreaEditManager == null)
        {
            var go = new GameObject("ResourceAreaEditManager");
            resourceAreaEditManager = go.AddComponent<ResourceAreaEditManager>();
        }
        resourceAreaEditManager.InitializeCreateMode();

        Debug.Log("IpcStateCoordinator: 資源エリア編集モードを適用しました");
    }

    /// <summary>
    /// STATE_EDIT_GRADING: new earthworks area drawing. No params.
    /// Backend determines land grading/reclamation and target height from the drawn area.
    /// Unity only sends the area through edit_grading:update_shape; the UI estimates and places the order.
    /// </summary>
    private void ApplyStateEditGrading(Dictionary<string, object> ipcParams)
    {
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.EDIT_GRADING);
        }

        // IpcViewCoordinator.SyncFromState applies VisualMode (state-specific display presets).

        var gradingAreaEditManager = FindAnyObjectByType<GradingAreaEditManager>();
        if (gradingAreaEditManager == null)
        {
            var go = new GameObject("GradingAreaEditManager");
            gradingAreaEditManager = go.AddComponent<GradingAreaEditManager>();
        }
        gradingAreaEditManager.InitializeCreateMode();

        Debug.Log("IpcStateCoordinator: 造成範囲の作図モードを適用しました");
    }

    /// <summary>
    /// edit_grading:finish: earthworks order confirmed (UI backend submission succeeded).
    /// One handler ends drawing, restores normal mode, and refetches data in that order
    /// (same reason as edit_path:finish_topology_edit). Terrain/coastlines are unchanged when ordering,
    /// so do not refetch coastlines. If no longer drawing (reply arrived after entering another edit mode),
    /// only refetch to avoid affecting that mode.
    /// After confirmation, the UI closes the earthworks window, so Rust's window closure path (same as ×)
    /// sends a redundant STATE_NORMAL. This handler already restored normal state; STATE_NORMAL only
    /// reapplies input/display and does not cancel incremental reload, so this is harmless.
    /// </summary>
    private void HandleEditGradingFinish()
    {
        var gradingAreaEditManager = FindAnyObjectByType<GradingAreaEditManager>();
        if (gradingAreaEditManager != null && gradingAreaEditManager.IsEditMode)
        {
            gradingAreaEditManager.FinishEditMode();
            ReturnToNormalState();
        }

        // Begin refetching last, after normal-mode restoration, to preserve ordering within this handler.
        // Only new fetches, camera cell movement, and session switches cancel incremental reload;
        // later STATE_NORMAL messages do not.
        CellDataLoader.Instance?.ReloadAllData();
    }

    /// <summary>
    /// STATE_EDIT_TRANSIT: public transport line placement.
    /// ipcParams contains the UI-selected path_variant (section variant = transit_master
    /// transit_path_id) and stop_path_variants listing underlying paths eligible for stops.
    /// Stops can only be placed on these paths (stations, streets, etc.),
    /// so pass the set to TransitPlanManager to filter snap targets.
    ///
    /// Hide districts to avoid accidentally opening their panels during line placement.
    /// </summary>
    private void ApplyStateEditTransit(Dictionary<string, object> ipcParams)
    {
        // InputMode: public transport placement preset
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.TRANSIT_CONSTRUCTION);
        }

        // IpcViewCoordinator.SyncFromState applies VisualMode (state-specific display presets).

        int pathVariant = 0;
        int[] stopPathVariants = System.Array.Empty<int>();
        int[] routePathVariants = System.Array.Empty<int>();
        var points = new List<TransitPlanManager.RoutePoint>();
        int lineAgentId = -1;
        if (ipcParams != null)
        {
            if (ipcParams.TryGetValue("path_variant", out object pvObj) && pvObj != null)
            {
                pathVariant = Convert.ToInt32(pvObj);
            }
            if (ipcParams.TryGetValue("stop_path_variants", out object spvObj) && spvObj != null)
            {
                stopPathVariants = ReadIntArray(spvObj);
            }
            if (ipcParams.TryGetValue("route_path_variants", out object rpvObj) && rpvObj != null)
            {
                routePathVariants = ReadIntArray(rpvObj);
            }
            if (ipcParams.TryGetValue("points", out object pointsObj) && pointsObj != null)
            {
                points = ReadTransitPoints(pointsObj);
            }
            if (ipcParams.TryGetValue("line_agent_id", out object lineObj) && lineObj != null)
            {
                lineAgentId = Convert.ToInt32(lineObj);
            }
        }

        // Initialize TransitPlanManager for line placement (create dynamically if absent). Route editing starts from current traversal points.
        var transitPlanManager = FindAnyObjectByType<TransitPlanManager>();
        if (transitPlanManager == null)
        {
            var go = new GameObject("TransitPlanManager");
            transitPlanManager = go.AddComponent<TransitPlanManager>();
        }
        transitPlanManager.InitializeTransitPlacement(pathVariant, stopPathVariants, routePathVariants, points, lineAgentId);

        Debug.Log($"IpcStateCoordinator: 公共交通設置モード -> path_variant={pathVariant}, stop_variants={stopPathVariants.Length}, points={points.Count}, line={lineAgentId}");
    }

    /// <summary>
    /// Read STATE_EDIT_TRANSIT points (array of [{kind, position:[x,y,z]}]). Skip malformed entries.
    /// </summary>
    private static List<TransitPlanManager.RoutePoint> ReadTransitPoints(object value)
    {
        var result = new List<TransitPlanManager.RoutePoint>();
        if (value is not Newtonsoft.Json.Linq.JArray array) return result;
        foreach (var token in array)
        {
            if (token is not Newtonsoft.Json.Linq.JObject item) continue;
            if (item["position"] is not Newtonsoft.Json.Linq.JArray position || position.Count < 3) continue;
            var world = new Vector3((float)position[0], (float)position[1], (float)position[2]);
            result.Add(new TransitPlanManager.RoutePoint(world, (string)item["kind"] == "waypoint"));
        }
        return result;
    }

    /// <summary>
    /// STATE_EDIT_PROP: prop placement.
    /// ipcParams contains UI-selected prop_type / placement_mode ("instance" or "scatter")
    /// and initial rotation / scale (individual) / density / seed (area) settings.
    /// Initialize PropPlacementManager for the mode (create dynamically if absent). As with public transport,
    /// the UI (Tauri) submits confirmation (POST /api/v1/props/...); this coordinator only sets the mode.
    /// </summary>
    private void ApplyStateEditProp(Dictionary<string, object> ipcParams)
    {
        // InputMode: prop placement preset (terrain clicks place props).
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.PROP_PLACEMENT);
        }

        // VisualMode: show plans, hide districts (same as public transport placement).
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_BOUNDARY);
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetFlags(InputObjectType.PATH, VisualFlag.SHOW_PLAN);
            VisualModeHandler.Instance.SetFlags(InputObjectType.BUILDING, VisualFlag.SHOW_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_HIDDEN);
        }

        int propType = 0;
        string placementMode = "instance";
        float height = 0f;
        float rotation = 0f;
        float scale = 1f;
        float density = 400f;
        int seed = 0;
        if (ipcParams != null)
        {
            if (ipcParams.TryGetValue("prop_type", out object ptObj) && ptObj != null)
                propType = Convert.ToInt32(ptObj);
            if (ipcParams.TryGetValue("placement_mode", out object pmObj) && pmObj != null)
                placementMode = Convert.ToString(pmObj);
            if (ipcParams.TryGetValue("height", out object hObj) && hObj != null)
                height = Convert.ToSingle(hObj);
            if (ipcParams.TryGetValue("rotation", out object rObj) && rObj != null)
                rotation = Convert.ToSingle(rObj);
            if (ipcParams.TryGetValue("scale", out object sObj) && sObj != null)
                scale = Convert.ToSingle(sObj);
            if (ipcParams.TryGetValue("density", out object dObj) && dObj != null)
                density = Convert.ToSingle(dObj);
            if (ipcParams.TryGetValue("seed", out object seedObj) && seedObj != null)
                seed = Convert.ToInt32(seedObj);
        }

        // Initialize PropPlacementManager in placement mode (create dynamically if absent).
        var propPlacementManager = FindAnyObjectByType<PropPlacementManager>();
        if (propPlacementManager == null)
        {
            var go = new GameObject("PropPlacementManager");
            propPlacementManager = go.AddComponent<PropPlacementManager>();
        }
        propPlacementManager.InitializePlacement(placementMode, propType, rotation, scale, density, seed, height);

        Debug.Log($"IpcStateCoordinator: プロップ配置モード -> prop_type={propType}, mode={placementMode}");
    }

    /// <summary>
    /// STATE_EDIT_CONNECTOR: connector editing.
    /// ipcParams contains UI-selected agent_id / infra_node_id / depot_node_id.
    /// Enable dragging target terminal nodes and disable other object clicks.
    /// Keep VisualMode unchanged (ConnectorEditManager forces nodes visible and draws markers).
    /// </summary>
    private void ApplyStateEditConnector(Dictionary<string, object> ipcParams)
    {
        int infraNodeId = 0;
        int depotNodeId = 0;
        int agentId = 0;
        if (ipcParams != null)
        {
            if (ipcParams.TryGetValue("infra_node_id", out object inObj) && inObj != null)
                infraNodeId = Convert.ToInt32(inObj);
            if (ipcParams.TryGetValue("depot_node_id", out object dnObj) && dnObj != null)
                depotNodeId = Convert.ToInt32(dnObj);
            // Facility ID, used to resolve terminal movement bounds (the facility parcel).
            if (ipcParams.TryGetValue("agent_id", out object agObj) && agObj != null)
                agentId = Convert.ToInt32(agObj);
        }

        // InputMode: connector editing preset (NODE=MODE_EDIT, others=MODE_NONE).
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.CONNECTOR_EDIT);
        }

        GetOrCreateConnectorEditManager().EnterEditMode(infraNodeId, depotNodeId, agentId);
        Debug.Log($"IpcStateCoordinator: 接続点編集モード -> infra_node={infraNodeId}, depot_node={depotNodeId}, agent={agentId}");
    }

    /// <summary>
    /// edit_connector:show: highlight terminal nodes read-only
    /// while AgentDetailPanel displays the facility.
    /// </summary>
    private void HandleEditConnectorShow(int agentId, int infraNodeId, int depotNodeId)
    {
        GetOrCreateConnectorEditManager().ShowConnectors(infraNodeId, depotNodeId);
    }

    /// <summary>
    /// edit_connector:hide: agent detail panel closed/switched agent. Clear highlights
    /// and return to normal if connector editing is active.
    ///
    /// Detail panels can coexist with building construction and other edit windows; if received after another state starts,
    /// only clear highlighting (plain STATE_NORMAL would end the newer edit state;
    /// same reason as edit_path:cancel_topology_edit).
    /// </summary>
    private void HandleEditConnectorHide()
    {
        var mgr = ConnectorEditManager.Instance;
        if (mgr == null) return;

        bool wasEditing = mgr.IsEditMode;
        mgr.Hide();
        if (wasEditing) ReturnToNormalState();
    }

    private ConnectorEditManager GetOrCreateConnectorEditManager()
    {
        var mgr = FindAnyObjectByType<ConnectorEditManager>();
        if (mgr == null)
        {
            var go = new GameObject("ConnectorEditManager");
            mgr = go.AddComponent<ConnectorEditManager>();
        }
        return mgr;
    }

    /// <summary>
    /// Converts ipcParams values (JArray or IEnumerable) to int arrays.
    /// Newtonsoft deserializes arrays as JArray with numeric token elements.
    /// </summary>
    private static int[] ReadIntArray(object value)
    {
        if (value is Newtonsoft.Json.Linq.JArray jArr)
        {
            var list = new List<int>(jArr.Count);
            foreach (var token in jArr)
            {
                list.Add((int)token);
            }
            return list.ToArray();
        }
        if (value is System.Collections.IEnumerable en && !(value is string))
        {
            var list = new List<int>();
            foreach (var item in en)
            {
                if (item != null) list.Add(Convert.ToInt32(item));
            }
            return list.ToArray();
        }
        return System.Array.Empty<int>();
    }

    /// <summary>
    /// Converts ipcParams values (JArray or IEnumerable) to float arrays.
    /// Float version of ReadIntArray, for arrays with fractional values such as lateral_offsets.
    /// </summary>
    private static float[] ReadFloatArray(object value)
    {
        if (value is Newtonsoft.Json.Linq.JArray jArr)
        {
            var list = new List<float>(jArr.Count);
            foreach (var token in jArr)
            {
                list.Add((float)token);
            }
            return list.ToArray();
        }
        if (value is System.Collections.IEnumerable en && !(value is string))
        {
            var list = new List<float>();
            foreach (var item in en)
            {
                if (item != null) list.Add(Convert.ToSingle(item, System.Globalization.CultureInfo.InvariantCulture));
            }
            return list.ToArray();
        }
        return System.Array.Empty<float>();
    }

    /// <summary>
    /// edit_parcel:start_shape_edit: UI requests shape editing.
    /// Initialize ParcelShapeEditManager directly without a state change.
    /// </summary>
    private void HandleEditParcelStartShapeEdit(int parcelId, float[][] polygon)
    {
        Debug.Log($"IpcStateCoordinator: 土地区画形状編集開始 -> parcel={parcelId}");

        // InputMode: terrain shape editing.
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.PARCEL_SHAPE_EDIT);
        }

        // VisualMode: show plans.
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_BOUNDARY);
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetFlags(InputObjectType.PATH, VisualFlag.SHOW_PLAN);
            VisualModeHandler.Instance.SetFlags(InputObjectType.BUILDING, VisualFlag.SHOW_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_HIDDEN);
        }

        var shapeEditManager = FindAnyObjectByType<ParcelShapeEditManager>();
        if (shapeEditManager == null)
        {
            var go = new GameObject("ParcelShapeEditManager");
            shapeEditManager = go.AddComponent<ParcelShapeEditManager>();
        }
        shapeEditManager.InitializeEditMode(parcelId, polygon);
    }

    /// <summary>
    /// edit_parcel:cancel_shape_edit: UI requests cancellation.
    /// End ParcelShapeEditManager and return to normal mode.
    /// </summary>
    private void HandleEditParcelCancelShapeEdit()
    {
        Debug.Log("IpcStateCoordinator: 土地区画形状編集キャンセル");
        var shapeEditManager = FindAnyObjectByType<ParcelShapeEditManager>();
        if (shapeEditManager != null)
        {
            shapeEditManager.FinishEditMode();
        }
        ApplyStateNormal();
    }

    /// <summary>
    /// edit_parcel:finish_shape_edit: completion after UI successfully saves the polygon
    /// through PATCH /parcels/{id} (the UI sends the backend request itself).
    /// End ParcelShapeEditManager, refetch current data, and return to normal mode.
    /// </summary>
    private void HandleEditParcelFinishShapeEdit()
    {
        Debug.Log("IpcStateCoordinator: 土地区画形状編集終了(保存済み)");
        var shapeEditManager = FindAnyObjectByType<ParcelShapeEditManager>();
        if (shapeEditManager != null)
        {
            shapeEditManager.CompleteEditMode();
        }
        ApplyStateNormal();
    }

    /// <summary>
    /// STATE_SET_GRID: node selection for grid angle setting
    /// </summary>
    private void ApplyStateSetGrid(Dictionary<string, object> ipcParams)
    {
        // InputMode: grid settings preset
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.SET_GRID);
        }

        // VisualMode: normal plan display (retain visible nodes/roads for snapping on terrain clicks).
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_BOUNDARY);
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetFlags(InputObjectType.PATH, VisualFlag.SHOW_PLAN);
            VisualModeHandler.Instance.SetFlags(InputObjectType.BUILDING, VisualFlag.SHOW_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_HIDDEN);
        }

        // Initialize and start GridSetManager.
        var gridSetManager = FindAnyObjectByType<GridSetManager>();
        if (gridSetManager == null)
        {
            var go = new GameObject("GridSetManager");
            gridSetManager = go.AddComponent<GridSetManager>();
        }
        gridSetManager.StartGridSetting();
    }

    /// <summary>
    /// edit_parcel:update_preview: receive UI preview geometry
    /// and instruct ParcelPlanRenderer to draw it through ParcelEditorManager.
    /// </summary>
    private void HandleEditParcelUpdatePreview(string mode, float[][][] previewPolygons, float[][] originalPolygon)
    {
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager == null)
        {
            Debug.LogWarning("IpcStateCoordinator: ParcelEditorManager が見つかりません");
            return;
        }

        // Clear if preview data is empty.
        if (previewPolygons == null || previewPolygons.Length == 0)
        {
            parcelEditorManager.ClearPreview();
            return;
        }

        // Convert float[][][] → List<List<Vector3>>.
        List<List<Vector3>> parcelPolygons = new List<List<Vector3>>();
        foreach (var poly in previewPolygons)
        {
            if (poly == null) continue;
            List<Vector3> vecList = new List<Vector3>();
            foreach (var point in poly)
            {
                if (point == null || point.Length < 2) continue;
                float x = point[0];
                float z = point[1];
                float y = Utils.GetGroundHeight(new Vector3(x, 1000f, z)) + 0.1f;
                vecList.Add(new Vector3(x, y, z));
            }
            if (vecList.Count > 0)
            {
                parcelPolygons.Add(vecList);
            }
        }

        // Convert the source parcel polygon (subdivision mode).
        List<Vector3> originalBlock = null;
        if (originalPolygon != null && originalPolygon.Length > 0)
        {
            originalBlock = new List<Vector3>();
            foreach (var point in originalPolygon)
            {
                if (point == null || point.Length < 2) continue;
                float x = point[0];
                float z = point[1];
                float y = Utils.GetGroundHeight(new Vector3(x, 1000f, z)) + 0.1f;
                originalBlock.Add(new Vector3(x, y, z));
            }
        }

        // Choose drawing methods by mode.
        switch (mode)
        {
            case "empty_area":
                parcelEditorManager.RenderEmptyParcelPreview(previewPolygons[0]);
                break;
            case "along_path":
                // Roadside creation retains no source path data, so draw only new parcels.
                parcelEditorManager.parcelPlanRenderer.RenderBlockPreview(null, parcelPolygons);
                break;
            case "split_rectangle":
            case "split_random":
            case "split_perimeter":
                parcelEditorManager.parcelPlanRenderer.RenderSplitPreview(originalBlock, parcelPolygons);
                break;
            default:
                parcelEditorManager.parcelPlanRenderer.RenderBlockPreview(originalBlock, parcelPolygons);
                break;
        }

        Debug.Log($"IpcStateCoordinator: プレビュー描画 -> mode={mode}, parcels={parcelPolygons.Count}");
    }

    /// <summary>
    /// edit_parcel:reset_selection: clear only selection/preview while retaining creation mode.
    /// Sent after UI creation confirmation or Clear button presses, allowing continued clicks
    /// in the 3D view to create the next parcel (essential for continuous creation without mode exit).
    /// </summary>
    private void HandleEditParcelResetSelection()
    {
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager == null) return;
        parcelEditorManager.ResetSelection();

        // Confirm/Clear buttons live in the Tauri panel, which has focus when pressed.
        // The next action is a 3D click, so bring Unity forward as on state transitions
        // to preserve the flow of clicking immediately to create the next parcel.
        WindowUtil.BringToForeground();
    }

    /// <summary>
    /// edit_parcel:set_rect_params: receive creation panel width/depth inputs.
    /// Used only to align local rectangle previews during angle dragging with panel settings
    /// (backend previews are authoritative for final geometry).
    /// </summary>
    private void HandleEditParcelSetRectParams(float width, float height)
    {
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager == null) return;
        parcelEditorManager.SetRectPreviewSize(width, height);
    }

    /// <summary>
    /// session.reset: clear all current 3D object and terrain data.
    /// </summary>
    private void HandleSessionReset()
    {
        Debug.Log("IpcStateCoordinator: セッションリセットを実行します");

        // Clear remembered display state.
        var viewCoordinator = FindAnyObjectByType<IpcViewCoordinator>();
        viewCoordinator?.ClearNormalStateMemory();

        // Clear direction arrow flags on resets during road construction
        // to prevent regenerated tracks showing arrows in normal view.
        TrackDirectionArrows.SetVisible(false);

        // Per-object filters store building/path ID sets, which would identify unrelated objects
        // in another city. Always discard on session switches.
        ObjectVisibilityFilter.Clear();

        // Road highlights/section editing also store path IDs, which identify other sections in a different city.
        RoadMemberEditManager.Instance?.End();
        RoadFocusOverlay.Instance?.ClearAll();
        ServiceCoverageOverlay.Instance?.Clear();

        // Stop sending camera state to the backend. Patching the previous viewpoint while a new city loads
        // would overwrite its saved viewpoint before restoration (resume on game:start).
        GameStateManager.Instance?.BeginLoading();
        CameraController.Instance?.SuspendCameraSync();

        // Fully clear all data managers, internal state caches, and stores;
        // suspend camera-driven loads until session:reload arrives.
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ClearAllManagerDataForSessionReset();
        }
    }

    /// <summary>
    /// session.reload: fetch backend data and redraw 3D.
    /// Called after epochs, etc. Refetch all data within camera range,
    /// compare with local caches, and recreate/delete only changed objects.
    /// Never touch objects with unchanged versions.
    /// If session_id identifies a new city, refetch master data/asset packs/city settings before redrawing everything.
    /// </summary>
    private void HandleSessionReload()
    {
        Debug.Log("IpcStateCoordinator: セッションリロードを実行します（versionベース差分更新）");

        // Clear remembered display state.
        var viewCoordinator = FindAnyObjectByType<IpcViewCoordinator>();
        viewCoordinator?.ClearNormalStateMemory();

        // Session reload may load another save, so refetch
        // coastlines/water surfaces/rivers before updating 3D.
        StartCoroutine(PrepareCityThenReload(FindAnyObjectByType<TerrainManager>()));

        // Reflect completed/new facilities and building count changes in coverage displays.
        ServiceCoverageOverlay.Instance?.Refresh();
    }

    // City whose master data/asset packs/city settings were refreshed (GET /sessions session_id)
    private string preparedSessionId;

    [Serializable]
    private class SessionIdentity { public string session_id; }

    private System.Collections.IEnumerator PrepareCityThenReload(TerrainManager terrainManager)
    {
        string sessionId = null;
        yield return ApiClient.Get<SessionIdentity>("/sessions", session => sessionId = session?.session_id);
        if (sessionId != null && sessionId != preparedSessionId)
        {
            // Do not retain render objects/caches resolved using the previous city's master data/asset packs/traffic side.
            CellDataLoader.Instance?.ClearAllManagerDataForSessionReset();
            yield return AssetPackStore.PrepareForCity();
            yield return CityInfoStore.Load();
            if (MasterDataStore.Instance != null)
            {
                yield return new WaitUntil(() => MasterDataStore.Instance.IsInitialized);
                yield return MasterDataStore.Instance.Initialize(forceReload: true);
            }
            preparedSessionId = sessionId;
        }
        if (terrainManager != null)
        {
            yield return terrainManager.FetchCoastline();
        }
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ReloadAllData();
        }
    }

    /// <summary>
    /// session.force_reload: delete all 3D objects, fetch backend data, and redraw.
    /// Used for explicit full rebuilds such as the taskbar reload button.
    /// </summary>
    private void HandleSessionForceReload()
    {
        Debug.Log("IpcStateCoordinator: セッション強制全リロードを実行します（全オブジェクト消去→再取得）");

        // Clear remembered display state.
        var viewCoordinator = FindAnyObjectByType<IpcViewCoordinator>();
        viewCoordinator?.ClearNormalStateMemory();

        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ForceReloadAllData();
        }
    }

    /// <summary>
    /// system.shutdown: exit the graphics program.
    /// </summary>
    private void HandleSystemShutdown()
    {
        Debug.Log("IpcStateCoordinator: システムシャットダウン要求を受信しました");
        Application.Quit();
    }

    /// <summary>
    /// zone.updated: UI changed district data (name, building regulations, etc.).
    /// Tell ZoneManager to refetch that district and immediately update cache/UI display.
    /// Even if districts are hidden, update the cache so the next display is correct.
    /// </summary>
    private void HandleZoneUpdated(int zoneId)
    {
        Debug.Log($"IpcStateCoordinator: 地区 {zoneId} の更新を反映します");
        var zoneManager = FindAnyObjectByType<ZoneManager>();
        if (zoneManager != null)
        {
            StartCoroutine(zoneManager.FetchZone(zoneId));
        }
    }
}
