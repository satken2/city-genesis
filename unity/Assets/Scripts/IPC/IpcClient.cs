using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// High-level IPC client.
/// Manages transport to/from the UI (IIpcTransport: MMF on Windows, page JS in Web builds)
/// and provides typed emit/subscribe APIs.
/// Runs as a MonoBehaviour, dispatching events on the main thread in Update.
/// </summary>
public class IpcClient : MonoBehaviour
{
    public static IpcClient Instance { get; private set; }

    private IIpcTransport _transport;

    // ---- Received events (ui -> graphic) ----
    public event Action<string, Dictionary<string, object>> OnWorldChangeState;
    // (layer, mode, subMode, flags). flags is CSV such as "plan,public";
    // null means preserve current flags, so pass it unchanged rather than replacing it with ?? "".
    public event Action<string, string, string, string> OnViewChangeView;
    // Node elevation/embankment changes from the path construction panel. Unity's radial menu owns
    // curved/straight and snapping, but selecting existing nodes for numeric elevation/embankment adjustment
    // requires bidirectional editing via panel steppers (UI→Unity) and Unity keys.
    public event Action<int, float> OnEditPathChangeNode;
    public event Action<int, float> OnEditPathChangeEmbankment;
    public event Action<float[][], int, float[], float, int> OnEditBuildingUpdateShape;
    public event Action<Newtonsoft.Json.Linq.JToken> OnEditBuildingPlacementCommand;
    public event Action<int> OnEditBuildingUpdateFloors;
    public event Action<int> OnBuildingExploded;
    public event Action<float, float, float> OnEditBuildingUpdateVisualVariant;
    // Landmark (landmark_master ID) changes. Switches preview part prefixes.
    public event Action<int> OnEditBuildingUpdateLandmark;
    public event Action<EffectRangePayload> OnEditBuildingUpdateEffectRange;
    // Direct placement rectangle parameters (width, depth, angle, floors). UI input changes
    // let Unity draw real-time cursor-following rectangle ghosts.
    public event Action<float, float, float, int> OnEditBuildingUpdateDirectParams;
    // Building copy construction: request to enter source building selection (UI→Unity).
    public event Action OnBuildingConstructionPickSource;
    public event Action<string, float[][][], float[][]> OnEditParcelUpdatePreview;
    public event Action OnEditParcelResetSelection;
    public event Action<float, float> OnEditParcelSetRectParams;
    public event Action<int, float[][]> OnEditParcelStartShapeEdit;
    public event Action OnEditParcelCancelShapeEdit;
    // Shape edit completion. Sent after the UI successfully saves PATCH /parcels/{id}
    // (the UI sends the backend request itself; same role as edit_path:finish).
    public event Action OnEditParcelFinishShapeEdit;
    // Per-object display filters (construction project tags / individual visibility toggles).
    // Arguments are complete replacement arrays of hidden building IDs and hidden path IDs, not deltas.
    // State exists only in the Unity session, never in the backend or save data.
    public event Action<int[], int[]> OnViewSetObjectFilter;
    // Query current hidden sets (sent when a UI panel opens).
    public event Action OnViewQueryObjectFilter;
    public event Action<string, float, float, float?, float?> OnViewSetGrid;
    public event Action OnSystemShutdown;
    public event Action OnSessionReset;
    public event Action OnSessionReload;
    public event Action OnSessionForceReload;
    public event Action<int> OnZoneUpdated;
    public event Action OnEditPathFinish;
    // Abort path merge/split selection. Rust (window.rs) sends this when the path detail panel closes.
    // Windows are only hidden, not destroyed, so React unmount does not run;
    // relying on the panel would leave selection mode active and input stuck.
    // Does nothing outside selection mode (dedicated event avoids affecting other modes).
    public event Action OnEditPathCancelTopologyEdit;
    // Path merge/split confirmation complete. Argument is the path ID removed by merging (0 for splitting).
    // One event ends selection, immediately destroys the removed path, restores normal mode, and refetches data
    // (separate session:reload and world:change_state events can race asynchronous incremental reload
    // against state transitions, preventing display updates).
    public event Action<int> OnEditPathFinishTopologyEdit;
    // Station platform side changes (Constants.PLATFORM_SIDE_*) from the UI panel toggle,
    // switching the side platforms extend in construction previews.
    public event Action<int> OnEditPathChangePlatformSide;
    public event Action<string> OnEditPathChangeHouseEnds;
    public event Action<string> OnEditPathChangePassageMode;
    // Individual terrain-following setting changes (-1=unset/0=off/1=on) from the UI toggle,
    // switching preview terrain following and tunnel visualization.
    public event Action<int> OnEditPathSetFollowTerrain;
    // Compound path (parallel track) lateral offsets (m) and directions (DIR_*) from panel
    // track count/direction changes. Updates preview track count, platform sides,
    // and direction arrows. Arrays correspond by index. Empty arrays mean a single path.
    public event Action<float[], int[]> OnEditPathChangeLateralOffsets;
    // Grid construction divisions [first side, second side] changes, used to redraw the provisional placement grid.
    public event Action<int[]> OnEditPathChangeGridDivisions;
    // Node/control operations from the 2D path editor (XZ only, no snapping).
    // RoadPlanManager remains authoritative, returning full state through edit_path:update_shape.
    public event Action<RoadPlanManager.ShapeCommand> OnEditPathShapeCommand;
    // Current shape query immediately after the 2D path editor opens. Returns update_shape once.
    public event Action OnEditPathQueryShape;
    public event Action OnEditResourceFinish;
    public event Action OnEditGradingFinish;
    public event Action OnEditTransitFinish;
    // Prop placement. change_settings passes (rotation?, scale?, density?, seed?) as a dictionary.
    public event Action<Dictionary<string, object>> OnEditPropChangeSettings;
    public event Action OnEditPropFinish;
    public event Action OnEditPropReset;
    // UI reply to confirmed bulldoze deletion (scatter props): (object_type, id, accepted).
    public event Action<string, int, bool> OnRemoveObjectConfirmResult;
    // Received when loading any new city (new game, tutorial, or saved game).
    public event Action OnGameLoading;
    public event Action OnGameStart;
    public event Action OnGameEnd;
    // Volume arrives only on settings changes or in response to system:graphic_ready; losing a value received before
    // subscription would leave audio silent until another settings change. Immediately deliver the latest received value on subscription.
    private Action<float> _onAmbienceVolume;
    private float? _ambienceVolume;
    public event Action<float> OnAmbienceVolume
    {
        add
        {
            _onAmbienceVolume += value;
            if (_ambienceVolume.HasValue) value(_ambienceVolume.Value);
        }
        remove { _onAmbienceVolume -= value; }
    }
    public event Action OnWeatherQuery;
    public event Action<string> OnWeatherSet;
    // Epoch presentation start (true) and end when closing results (false). Used for sunset→blackout→sunrise.
    public event Action<bool> OnEpochExecuting;
    public event Action<string> OnEpochPrepare;
    // Capture request just before saving a game/scenario (argument: request ID). Send the image directly to the core.
    public event Action<string> OnCameraCaptureThumbnail;
    // Asset manager render request. Return per-row models and capture thumbnails as files (AssetManagerRenderer).
    public event Action<AssetManagerRequest> OnAssetManagerRender;
    // Road access (front_path) arrows, corresponding to parcel detail panel opening/closing.
    public event Action<int> OnParcelShowFrontPath;
    public event Action OnParcelHideFrontPath;
    // Highlight terminal nodes (infra_node_id / depot_node_id).
    // Corresponds to AgentDetailPanel opening/closing or changing displayed agents (read-only emphasis).
    public event Action<int, int, int> OnEditConnectorShow;
    public event Action OnEditConnectorHide;
    // Highlight parcels, buildings, and existing paths affected by planned paths.
    // Corresponds to construction shape finalization/confirmation and pre-construction path detail panels.
    // First argument is the highlight source (source/windowType). Sources have independent layers.
    public event Action<string, PathInterferencePayload> OnWorldHighlightObjects;
    public event Action<string> OnWorldClearHighlights;
    // Road detail highlighting (road_id, color "#rrggbb", path_ids). Replace completely on each message.
    public event Action<int, string, int[]> OnWorldSetRoadFocus;
    // Clear highlighting (road_id). Without an ID (0), clear unconditionally; otherwise clear only that road.
    public event Action<int> OnWorldClearRoadFocus;
    // Refresh the road list/name labels immediately after road creation, renaming, membership changes, or deletion.
    public event Action OnWorldRefreshRoads;
    // Full member and candidate sets during section editing (road_id, path_ids, preview_path_ids).
    public event Action<int, int[], int[]> OnEditRoadSetMembers;
    // Section edit end request (road_id). Sent by panel Done or Rust (window.rs) when the window closes.
    // Do nothing unless editing sections of that road.
    public event Action<int> OnEditRoadCancel;

    /// <summary>
    /// Whether system.ready has been received from Tauri.
    /// </summary>
    public bool IsConnected { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;

        // If GameStateManager exists first, prompt subscription in Awake.
        // Creation order varies, so attempt subscription from both sides.
        GameStateManager.Instance?.SubscribeIpcEvents();
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
        Disconnect();
    }

    /// <summary>
    /// Open IPC transport. Update dispatches received events to the main thread.
    /// </summary>
    public void Initialize()
    {
        _transport = IpcTransports.Open();
        if (_transport == null)
        {
            Debug.LogWarning("IpcClient: このプラットフォームではIPCはサポートされていません");
            return;
        }
        _transport.OnMessageReceived += HandleIncomingMessage;
        Debug.Log("IpcClient: IPCの送受信路を開きました");
    }

    /// <summary>
    /// Disconnects IPC.
    /// Sends system:graphic_stopped before closing so Tauri marks Unity disconnected
    /// (Editor Play stop also reaches this via OnApplicationQuit; crashes cannot send it).
    /// </summary>
    public void Disconnect()
    {
        EmitGraphicStopped();

        if (_transport != null)
        {
            _transport.OnMessageReceived -= HandleIncomingMessage;
            _transport.Dispose();
            _transport = null;
        }
        IsConnected = false;

        Debug.Log("IpcClient: IPC接続を切断しました");
    }

    private void Update()
    {
        _transport?.ProcessReceivedMessages();
    }

    /// <summary>
    /// Notifies Tauri when Unity gains foreground focus.
    /// Activating fullscreen-like Unity can make the shell place topmost UI behind it;
    /// Tauri reasserts topmost status for all UI on this notification (restores UI
    /// when clicking Unity). Do not notify on focus loss.
    /// </summary>
    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus) return;
        SendEnvelope("system:focus_gained", new Dictionary<string, object>());
    }

    // ======================================================================
    // Received message dispatch
    // ======================================================================

    private void HandleIncomingMessage(string json)
    {
        try
        {
            IpcEnvelope envelope = IpcEnvelope.FromJson(json);
            if (envelope == null)
            {
                Debug.LogWarning($"IpcClient: エンベロープのパースに失敗: {json}");
                return;
            }

            Debug.Log($"IpcClient: メッセージ受信 event={envelope.@event} src={envelope.source}");

            DispatchIncomingEvent(envelope);
        }
        catch (Exception e)
        {
            Debug.LogError($"IpcClient: メッセージ処理エラー: {e.Message}\nJSON: {json}");
        }
    }

    public bool ProtocolCompatible { get; private set; }

    private void DispatchIncomingEvent(IpcEnvelope envelope)
    {
        string eventName = envelope.@event;

        // Route ui.action events by the action field in payload.
        if (eventName == "ui:action")
        {
            DispatchUiAction(envelope.payload);
            return;
        }

        switch (eventName)
        {
            case "system:ready":
                var version = Newtonsoft.Json.Linq.JObject.FromObject(envelope.payload);
                ProtocolCompatible = (int?)version["ipc_contract_version"] == FrontendVersion.IpcContract
                    && (bool?)version["accepted"] != false;
                IsConnected = ProtocolCompatible;
                if (!ProtocolCompatible) {
                    GameStateManager.Instance?.SetGameStarted(false, null);
                    Debug.LogError("IPC_CONTRACT_MISMATCH");
                }
                if (version["accepted"] == null && BackendCompatibility.Compatible) EmitGraphicReady();
                Debug.Log("IpcClient: system:ready を受信しました");
                break;

            case "system:pong":
                Debug.Log("IpcClient: system:pong を受信しました");
                break;

            case "ui:error":
                Debug.LogWarning($"IpcClient: ui:error を受信: {envelope.payload}");
                break;

            default:
                Debug.LogWarning($"IpcClient: 未知のイベント: {eventName}");
                break;
        }
    }

    private void DispatchUiAction(object payloadObj)
    {
        if (payloadObj == null) return;

        try
        {
            string payloadJson = Newtonsoft.Json.JsonConvert.SerializeObject(payloadObj);
            var payload = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(payloadJson);
            if (payload == null) return;

            string action = payload.ContainsKey("action") ? payload["action"]?.ToString() : null;
            if (action == null) return;
            if ((!ProtocolCompatible || !BackendCompatibility.Compatible)
                && action != "app:quit" && action != "system:shutdown") return;

            switch (action)
            {
                case "tutorial:apply":
                    TutorialRuntime.ApplyFromIpc(GetParamsDictionary(payload));
                    break;
                case "world:change_state":
                    {
                        string state = GetParamString(payload, "state");
                        if (state != null)
                        {
                            Debug.Log($"IpcClient: world:change_state -> {state}");
                            var ipcParams = GetParamsDictionary(payload);
                            OnWorldChangeState?.Invoke(state, ipcParams);
                        }
                    }
                    break;

                case "view:change_view":
                    {
                        string layer = GetParamString(payload, "layer");
                        string mode = GetParamString(payload, "mode");
                        string subMode = GetParamString(payload, "sub_mode");
                        string flags = GetParamString(payload, "flags");
                        if (layer != null && mode != null)
                        {
                            Debug.Log($"IpcClient: view:change_view -> layer={layer}, mode={mode}, sub_mode={subMode}, flags={flags ?? "(keep)"}");
                            OnViewChangeView?.Invoke(layer, mode, subMode ?? "", flags);
                        }
                    }
                    break;

                case "view:apply_preset":
                    {
                        // Display presets for windows without dedicated edit states, replacing the display
                        // only while open. STATE_NORMAL restores the original display.
                        string preset = GetParamString(payload, "preset");
                        Debug.Log($"IpcClient: view:apply_preset -> {preset}");
                        if (preset != null)
                        {
                            var viewCoordinator = UnityEngine.Object.FindAnyObjectByType<IpcViewCoordinator>();
                            if (viewCoordinator != null)
                            {
                                viewCoordinator.ApplyPreset(preset);
                            }
                        }
                    }
                    break;

                case "view:restore_preset":
                    {
                        // Display preset release notification.
                        // When windows without edit states (e.g. detail panels) close,
                        // safely release only their preset without sending STATE_NORMAL.
                        string preset = GetParamString(payload, "preset");
                        Debug.Log($"IpcClient: view:restore_preset -> {preset}");
                        if (preset != null)
                        {
                            var viewCoordinator = UnityEngine.Object.FindAnyObjectByType<IpcViewCoordinator>();
                            if (viewCoordinator != null)
                            {
                                viewCoordinator.RestorePreset(preset);
                            }
                        }
                    }
                    break;

                case "view:set_object_filter":
                    {
                        int[] hiddenBuildings = GetParamInt1D(payload, "hidden_building_ids");
                        int[] hiddenPaths = GetParamInt1D(payload, "hidden_path_ids");
                        Debug.Log($"IpcClient: view:set_object_filter -> buildings={hiddenBuildings.Length}, paths={hiddenPaths.Length}");
                        OnViewSetObjectFilter?.Invoke(hiddenBuildings, hiddenPaths);
                    }
                    break;

                case "view:query_object_filter":
                    Debug.Log("IpcClient: view:query_object_filter");
                    OnViewQueryObjectFilter?.Invoke();
                    break;

                case "edit_path:change_node":
                    {
                        int nodeIndex = GetParamInt(payload, "node_index");
                        float height = GetParamFloat(payload, "height");
                        Debug.Log($"IpcClient: edit_path:change_node -> index={nodeIndex}, height={height}");
                        OnEditPathChangeNode?.Invoke(nodeIndex, height);
                    }
                    break;

                case "edit_path:change_embankment":
                    {
                        int nodeIndex = GetParamInt(payload, "node_index");
                        float embankmentHeight = GetParamFloat(payload, "embankment_height");
                        Debug.Log($"IpcClient: edit_path:change_embankment -> index={nodeIndex}, embankmentHeight={embankmentHeight}");
                        OnEditPathChangeEmbankment?.Invoke(nodeIndex, embankmentHeight);
                    }
                    break;

                case "edit_path:finish":
                    {
                        Debug.Log("IpcClient: edit_path:finish received");
                        OnEditPathFinish?.Invoke();
                    }
                    break;

                case "edit_path:cancel_topology_edit":
                    {
                        Debug.Log("IpcClient: edit_path:cancel_topology_edit received");
                        OnEditPathCancelTopologyEdit?.Invoke();
                    }
                    break;

                case "edit_path:finish_topology_edit":
                    {
                        int removedPathId = GetParamInt(payload, "removed_path_id");
                        Debug.Log($"IpcClient: edit_path:finish_topology_edit -> removed_path_id={removedPathId}");
                        OnEditPathFinishTopologyEdit?.Invoke(removedPathId);
                    }
                    break;

                case "edit_path:change_passage_mode":
                    OnEditPathChangePassageMode?.Invoke(GetParamString(payload, "passage_mode"));
                    break;
                case "edit_path:change_house_ends":
                    OnEditPathChangeHouseEnds?.Invoke(GetParamString(payload, "house_ends"));
                    break;
                case "edit_path:change_platform_side":
                    {
                        int platformSide = GetParamInt(payload, "platform_side");
                        Debug.Log($"IpcClient: edit_path:change_platform_side -> platform_side={platformSide}");
                        OnEditPathChangePlatformSide?.Invoke(platformSide);
                    }
                    break;

                case "edit_path:set_follow_terrain":
                    {
                        int followTerrain = GetParamInt(payload, "value");
                        Debug.Log($"IpcClient: edit_path:set_follow_terrain -> value={followTerrain}");
                        OnEditPathSetFollowTerrain?.Invoke(followTerrain);
                    }
                    break;

                case "edit_path:change_lateral_offsets":
                    {
                        float[] lateralOffsets = GetParamFloat1D(payload, "lateral_offsets") ?? System.Array.Empty<float>();
                        int[] travelDirections = GetParamInt1D(payload, "travel_directions") ?? System.Array.Empty<int>();
                        Debug.Log($"IpcClient: edit_path:change_lateral_offsets -> count={lateralOffsets.Length}");
                        OnEditPathChangeLateralOffsets?.Invoke(lateralOffsets, travelDirections);
                    }
                    break;

                case "edit_path:change_grid_divisions":
                    {
                        int[] gridDivisions = GetParamInt1D(payload, "grid_divisions");
                        Debug.Log($"IpcClient: edit_path:change_grid_divisions -> {string.Join(",", gridDivisions ?? System.Array.Empty<int>())}");
                        OnEditPathChangeGridDivisions?.Invoke(gridDivisions);
                    }
                    break;

                case "edit_path:shape_command":
                    {
                        var cmd = new RoadPlanManager.ShapeCommand
                        {
                            op = GetParamString(payload, "op"),
                            revision = GetParamString(payload, "revision"),
                            routeJson = GetParamString(payload, "route_json"),
                            alignmentJson = GetParamString(payload, "alignment_json"),
                            junctionJson = GetParamString(payload, "junction_json"),
                            layoutJson = GetParamString(payload, "layout_json"),
                            index = GetParamInt(payload, "index"),
                            edgeIndex = GetParamInt(payload, "edge_index"),
                            which = GetParamString(payload, "which"),
                            x = GetParamFloat(payload, "x"),
                            z = GetParamFloat(payload, "z"),
                        };
                        Debug.Log($"IpcClient: edit_path:shape_command -> op={cmd.op}, index={cmd.index}, edge={cmd.edgeIndex}");
                        OnEditPathShapeCommand?.Invoke(cmd);
                    }
                    break;

                case "edit_path:query_shape":
                    Debug.Log("IpcClient: edit_path:query_shape");
                    OnEditPathQueryShape?.Invoke();
                    break;

                case "edit_resource:finish":
                    {
                        Debug.Log("IpcClient: edit_resource:finish received");
                        OnEditResourceFinish?.Invoke();
                    }
                    break;

                case "edit_grading:finish":
                    {
                        Debug.Log("IpcClient: edit_grading:finish received");
                        OnEditGradingFinish?.Invoke();
                    }
                    break;

                case "edit_transit:finish":
                    {
                        Debug.Log("IpcClient: edit_transit:finish received");
                        OnEditTransitFinish?.Invoke();
                    }
                    break;

                case "edit_prop:change_settings":
                    {
                        // Pass only received rotation / scale / density / seed fields in a dictionary.
                        var propParams = GetParamsDictionary(payload);
                        OnEditPropChangeSettings?.Invoke(propParams);
                    }
                    break;

                case "edit_prop:finish":
                    {
                        Debug.Log("IpcClient: edit_prop:finish received");
                        OnEditPropFinish?.Invoke();
                    }
                    break;

                case "edit_prop:reset":
                    {
                        Debug.Log("IpcClient: edit_prop:reset received");
                        OnEditPropReset?.Invoke();
                    }
                    break;

                case "remove_object:confirm_result":
                    {
                        // Bulldoze confirmation reply. accepted=1 performs deletion.
                        string objectType = GetParamString(payload, "object_type");
                        int id = GetParamInt(payload, "id");
                        bool accepted = GetParamInt(payload, "accepted") != 0;
                        Debug.Log($"IpcClient: remove_object:confirm_result -> {objectType}/{id} accepted={accepted}");
                        OnRemoveObjectConfirmResult?.Invoke(objectType, id, accepted);
                    }
                    break;

                case "edit_building:update_shape":
                    {
                        float[][] polygon = GetParamFloat2D(payload, "polygon");
                        int floors = GetParamInt(payload, "floors");
                        float[] centerPoint = GetParamFloat1D(payload, "center_point");
                        float height = GetParamFloat(payload, "height");
                        Debug.Log($"IpcClient: edit_building:update_shape -> floors={floors}, has_center={centerPoint != null}, height={height}");
                        int revision = (int?)GetParamToken(payload, "placement_revision") ?? -1;
                        OnEditBuildingUpdateShape?.Invoke(polygon, floors, centerPoint, height, revision);
                    }
                    break;

                case "edit_building:update_floors":
                    {
                        int floors = GetParamInt(payload, "floors");
                        Debug.Log($"IpcClient: edit_building:update_floors -> {floors}");
                        OnEditBuildingUpdateFloors?.Invoke(floors);
                    }
                    break;

                case "edit_building:update_landmark":
                    {
                        int landmark = GetParamInt(payload, "landmark");
                        Debug.Log($"IpcClient: edit_building:update_landmark -> {landmark}");
                        OnEditBuildingUpdateLandmark?.Invoke(landmark);
                    }
                    break;

                case "edit_building:update_visual_variant":
                    {
                        float vv1 = GetParamFloat(payload, "visual_variant");
                        float vv2 = GetParamFloat(payload, "visual_variant2");
                        float vv3 = GetParamFloat(payload, "visual_variant3");
                        Debug.Log($"IpcClient: edit_building:update_visual_variant -> vv1={vv1}, vv2={vv2}, vv3={vv3}");
                        OnEditBuildingUpdateVisualVariant?.Invoke(vv1, vv2, vv3);
                    }
                    break;

                case "edit_building:update_effect_range":
                    {
                        // Receive infrastructure coverage (circle or irregular polygon) and visualize in 3D.
                        // region=preview; existing=list of existing facility coverage regions.
                        // Both null/empty means clear.
                        var regionToken = GetParamToken(payload, "region");
                        var existingToken = GetParamToken(payload, "existing");
                        var effectRange = EffectRangePayload.FromJson(regionToken, existingToken);
                        Debug.Log($"IpcClient: edit_building:update_effect_range -> clear={effectRange.IsClear}, existing={effectRange.existing.Count}");
                        OnEditBuildingUpdateEffectRange?.Invoke(effectRange);
                    }
                    break;

                case "edit_building:placement_command":
                    OnEditBuildingPlacementCommand?.Invoke(GetParamToken(payload, "command"));
                    break;

                case "edit_building:update_direct_params":
                    {
                        float width = GetParamFloat(payload, "width");
                        float depth = GetParamFloat(payload, "depth");
                        float angle = GetParamFloat(payload, "angle");
                        int floors = GetParamInt(payload, "floors");
                        OnEditBuildingUpdateDirectParams?.Invoke(width, depth, angle, floors);
                    }
                    break;

                case "building_construction:pick_source":
                    {
                        // Building copy construction: request to enter source building selection.
                        Debug.Log("IpcClient: building_construction:pick_source");
                        OnBuildingConstructionPickSource?.Invoke();
                    }
                    break;

                case "edit_parcel:update_preview":
                    {
                        string mode = GetParamString(payload, "mode") ?? "empty_area";
                        float[][][] previewPolygons = GetParamFloat3D(payload, "preview_polygons");
                        float[][] originalPolygon = GetParamFloat2D(payload, "original_polygon");
                        Debug.Log($"IpcClient: edit_parcel:update_preview -> mode={mode}, previewCount={previewPolygons?.Length ?? 0}");
                        OnEditParcelUpdatePreview?.Invoke(mode, previewPolygons, originalPolygon);
                    }
                    break;

                case "edit_parcel:reset_selection":
                    {
                        // Instruction after creation confirmation/clear to discard selection only and continue in the same mode.
                        // Enables continuous creation without leaving creation mode after each operation.
                        Debug.Log("IpcClient: edit_parcel:reset_selection");
                        OnEditParcelResetSelection?.Invoke();
                    }
                    break;

                case "edit_parcel:set_rect_params":
                    {
                        // Width/depth (panel inputs) for the local rectangle preview during rotation dragging.
                        float width = GetParamFloat(payload, "width");
                        float height = GetParamFloat(payload, "height");
                        OnEditParcelSetRectParams?.Invoke(width, height);
                    }
                    break;

                case "edit_parcel:start_shape_edit":
                    {
                        int parcelId = GetParamInt(payload, "parcel_id");
                        float[][] polygon = GetParamFloat2D(payload, "polygon");
                        Debug.Log($"IpcClient: edit_parcel:start_shape_edit -> parcel={parcelId}, points={polygon?.Length ?? 0}");
                        OnEditParcelStartShapeEdit?.Invoke(parcelId, polygon);
                    }
                    break;

                case "edit_parcel:cancel_shape_edit":
                    {
                        Debug.Log("IpcClient: edit_parcel:cancel_shape_edit");
                        OnEditParcelCancelShapeEdit?.Invoke();
                    }
                    break;

                case "edit_parcel:finish_shape_edit":
                    {
                        // Completion notification after the UI successfully saves PATCH /parcels/{id}.
                        Debug.Log("IpcClient: edit_parcel:finish_shape_edit");
                        OnEditParcelFinishShapeEdit?.Invoke();
                    }
                    break;

                case "parcel:show_front_path":
                    {
                        int parcelId = GetParamInt(payload, "parcel_id");
                        Debug.Log($"IpcClient: parcel:show_front_path -> parcel={parcelId}");
                        OnParcelShowFrontPath?.Invoke(parcelId);
                    }
                    break;

                case "parcel:hide_front_path":
                    Debug.Log("IpcClient: parcel:hide_front_path");
                    OnParcelHideFrontPath?.Invoke();
                    break;

                case "world:highlight_objects":
                    {
                        string source = GetParamString(payload, "source") ?? "default";
                        var parcelsToken = GetParamToken(payload, "parcels");
                        int[] buildingIds = GetParamInt1D(payload, "building_ids");
                        int[] collidingPathIds = GetParamInt1D(payload, "colliding_path_ids");
                        var riverIdsToken = GetParamToken(payload, "colliding_river_ids");
                        var interference = PathInterferencePayload.FromParams(
                            parcelsToken, buildingIds, collidingPathIds, riverIdsToken);
                        Debug.Log(
                            $"IpcClient: world:highlight_objects [{source}] -> " +
                            $"parcels(b={interference.BlockingParcels.Count},r={interference.RemovableParcels.Count}," +
                            $"s={interference.ShavableParcels.Count},e={interference.ExemptParcels.Count}), " +
                            $"buildings={interference.BuildingIds.Count}, paths={interference.CollidingPathIds.Count}, " +
                            $"rivers={interference.CollidingRiverIds.Count}");
                        OnWorldHighlightObjects?.Invoke(source, interference);
                    }
                    break;

                case "world:clear_highlights":
                    {
                        string source = GetParamString(payload, "source") ?? "default";
                        Debug.Log($"IpcClient: world:clear_highlights [{source}]");
                        OnWorldClearHighlights?.Invoke(source);
                    }
                    break;

                case "world:set_road_focus":
                    {
                        int roadId = GetParamInt(payload, "road_id");
                        string color = GetParamString(payload, "color");
                        int[] pathIds = GetParamInt1D(payload, "path_ids") ?? Array.Empty<int>();
                        Debug.Log($"IpcClient: world:set_road_focus -> road={roadId}, paths={pathIds.Length}");
                        OnWorldSetRoadFocus?.Invoke(roadId, color, pathIds);
                    }
                    break;

                case "world:clear_road_focus":
                    {
                        int roadId = GetParamInt(payload, "road_id");
                        Debug.Log($"IpcClient: world:clear_road_focus -> road={roadId}");
                        OnWorldClearRoadFocus?.Invoke(roadId);
                    }
                    break;

                case "world:refresh_roads":
                    Debug.Log("IpcClient: world:refresh_roads");
                    OnWorldRefreshRoads?.Invoke();
                    break;

                case "edit_road:set_members":
                    {
                        int roadId = GetParamInt(payload, "road_id");
                        int[] pathIds = GetParamInt1D(payload, "path_ids") ?? Array.Empty<int>();
                        int[] previewPathIds = GetParamInt1D(payload, "preview_path_ids") ?? Array.Empty<int>();
                        Debug.Log($"IpcClient: edit_road:set_members -> road={roadId}, paths={pathIds.Length}, preview={previewPathIds.Length}");
                        OnEditRoadSetMembers?.Invoke(roadId, pathIds, previewPathIds);
                    }
                    break;

                case "edit_road:cancel":
                    {
                        int roadId = GetParamInt(payload, "road_id");
                        Debug.Log($"IpcClient: edit_road:cancel -> road={roadId}");
                        OnEditRoadCancel?.Invoke(roadId);
                    }
                    break;

                case "session:reset":
                    TutorialRuntime.Clear();
                    Debug.Log("IpcClient: session:reset");
                    OnSessionReset?.Invoke();
                    break;

                case "session:reload":
                    Debug.Log("IpcClient: session:reload");
                    OnSessionReload?.Invoke();
                    break;

                case "building:exploded":
                    OnBuildingExploded?.Invoke(GetParamInt(payload, "agent_id"));
                    break;

                case "session:force_reload":
                    Debug.Log("IpcClient: session:force_reload");
                    OnSessionForceReload?.Invoke();
                    break;

                case "zone:updated":
                    {
                        int zoneId = GetParamInt(payload, "zone_id");
                        Debug.Log($"IpcClient: zone:updated -> zone={zoneId}");
                        OnZoneUpdated?.Invoke(zoneId);
                    }
                    break;

                case "view:query_state":
                    Debug.Log("IpcClient: view:query_state");
                    {
                        var coordinator = UnityEngine.Object.FindAnyObjectByType<IpcViewCoordinator>();
                        if (coordinator != null)
                        {
                            coordinator.QueryCurrentState();
                        }
                    }
                    break;

                case "input:query_modes":
                    Debug.Log("IpcClient: input:query_modes");
                    {
                        var debugCoordinator = UnityEngine.Object.FindAnyObjectByType<IpcInputDebugCoordinator>();
                        if (debugCoordinator != null)
                        {
                            debugCoordinator.QueryCurrentModes();
                        }
                    }
                    break;

                case "game:loading":
                    GameStateManager.Instance?.BeginLoading();
                    OnGameLoading?.Invoke();
                    break;
                case "game:reveal":
                    GameStateManager.Instance?.RevealWorld(GetParamInt(payload, "loading_id"));
                    break;
                case "game:start":
                    GameStateManager.Instance?.PrepareInitialLoad(GetParamInt(payload, "loading_id"));
                    Debug.Log("IpcClient: game:start");
                    OnGameStart?.Invoke();
                    break;

                case "game:end":
                    TutorialRuntime.Clear();
                    Debug.Log("IpcClient: game:end");
                    OnGameEnd?.Invoke();
                    break;

                case "ambience:volume":
                    _ambienceVolume = GetParamFloat(payload, "volume");
                    _onAmbienceVolume?.Invoke(_ambienceVolume.Value);
                    break;

                case "weather:query":
                    OnWeatherQuery?.Invoke();
                    break;

                case "drive:open":
                    DriveMode.Ensure().Open(GetParamsDictionary(payload));
                    break;

                case "citizen:follow":
                    CitizenFollow.Ensure().Follow(GetParamInt(payload, "citizen_id"));
                    break;

                case "citizen:unfollow":
                    CitizenFollow.Ensure().Unfollow(GetParamInt(payload, "citizen_id"));
                    break;

                case "weather:set":
                    OnWeatherSet?.Invoke(GetParamString(payload, "id"));
                    break;

                case "epoch:executing":
                    OnEpochExecuting?.Invoke(bool.TryParse(GetParamString(payload, "executing"), out bool executing) && executing);
                    break;

                case "epoch:prepare":
                    OnEpochPrepare?.Invoke(GetParamString(payload, "id"));
                    break;

                case "view:set_grid":
                    {
                        string mode = GetParamString(payload, "mode") ?? "off";
                        float size = GetParamFloat(payload, "size");
                        float angle = GetParamFloat(payload, "angle");
                        float? originX = null;
                        float? originZ = null;
                        bool hasOrigin = false;
                        if (payload.ContainsKey("origin_x")) hasOrigin = true;
                        else if (payload.TryGetValue("params", out object pObj))
                        {
                            if (pObj is Dictionary<string, object> dict && dict.ContainsKey("origin_x")) hasOrigin = true;
                            else if (pObj is Newtonsoft.Json.Linq.JObject jobj && jobj["origin_x"] != null) hasOrigin = true;
                        }
                        if (hasOrigin)
                        {
                            originX = GetParamFloat(payload, "origin_x");
                            originZ = GetParamFloat(payload, "origin_z");
                        }
                        Debug.Log($"IpcClient: view:set_grid -> mode={mode}, size={size}, angle={angle}, origin=({(originX.HasValue ? originX.Value.ToString() : "null")}, {(originZ.HasValue ? originZ.Value.ToString() : "null")})");
                        OnViewSetGrid?.Invoke(mode, size, angle, originX, originZ);
                    }
                    break;

                case "camera:query_state":
                    Debug.Log("IpcClient: camera:query_state");
                    EmitCameraState();
                    break;

                case "camera:focus":
                    CameraController.Instance?.FocusOnArea(
                        GetParamFloat(payload, "min_x"), GetParamFloat(payload, "min_z"),
                        GetParamFloat(payload, "max_x"), GetParamFloat(payload, "max_z"));
                    break;

                case "camera:capture_thumbnail":
                    Debug.Log("IpcClient: camera:capture_thumbnail");
                    OnCameraCaptureThumbnail?.Invoke(GetParamString(payload, "id"));
                    break;

                case "asset_manager:render":
                    OnAssetManagerRender?.Invoke(new AssetManagerRequest
                    {
                        RequestId = GetParamString(payload, "request_id"),
                        PackId = GetParamString(payload, "pack_id"),
                        Category = GetParamString(payload, "category"),
                        Rows = GetParamToken(payload, "rows")?.ToObject<int[]>() ?? Array.Empty<int>(),
                    });
                    break;

                case "edit_connector:show":
                    {
                        int agentId = GetParamInt(payload, "agent_id");
                        int infraNodeId = GetParamInt(payload, "infra_node_id");
                        int depotNodeId = GetParamInt(payload, "depot_node_id");
                        Debug.Log($"IpcClient: edit_connector:show -> agent={agentId}, infra_node={infraNodeId}, depot_node={depotNodeId}");
                        OnEditConnectorShow?.Invoke(agentId, infraNodeId, depotNodeId);
                    }
                    break;

                case "edit_connector:hide":
                    Debug.Log("IpcClient: edit_connector:hide");
                    OnEditConnectorHide?.Invoke();
                    break;

                case "system:focus":
                    Debug.Log("IpcClient: system:focus -> BringToForeground");
                    WindowUtil.BringToForeground();
                    break;

                case "system:move_display":
                    {
                        int direction = GetParamInt(payload, "direction");
                        Debug.Log($"IpcClient: system:move_display -> direction={direction}");
                        WindowUtil.MoveToAdjacentDisplay(direction);
                    }
                    break;

                case "system:shutdown":
                    Debug.Log("IpcClient: system:shutdown");
                    OnSystemShutdown?.Invoke();
                    break;

                case "system:backend_connection":
                    ApiClient.UiBackendConnected = GetParamToken(payload, "connected")?.ToObject<bool>() == true;
                    break;

                case "system:verification":
                    UnverifiedLabel.SetVerified(GetParamToken(payload, "verified")?.ToObject<bool>() != false);
                    break;

                default:
                    Debug.LogWarning($"IpcClient: 未知のui.action: {action}");
                    break;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"IpcClient: ui.action ディスパッチエラー: {e.Message}");
        }
    }

    // ======================================================================
    // Emit methods (graphic -> ui)
    // ======================================================================

    /// <summary>
    /// Sends world.select_object.
    /// </summary>
    public void EmitSelectObject(string windowType, string entityType, string entityId, float? screenX = null, float? screenY = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["window_type"] = windowType,
            ["entity"] = new Dictionary<string, string>
            {
                ["type"] = entityType,
                ["id"] = entityId
            }
        };

        if (screenX.HasValue && screenY.HasValue)
        {
            payload["params"] = new Dictionary<string, object>
            {
                ["preferred_position"] = new Dictionary<string, object>
                {
                    ["mode"] = "screen_point",
                    ["x"] = screenX.Value,
                    ["y"] = screenY.Value
                }
            };
        }

        SendEnvelope("world:select_object", payload);
    }

    /// <summary>
    /// Sends building_construction:copy_source.
    /// Reports selection of a source building in building copy construction (MODE_PICK_BUILDING).
    /// The UI seeds the panel with variant / floors / visual_variant and enters the
    /// STATE_EDIT_BUILDING parcel selection flow. Backend derives actual footprints from source_agent_id.
    /// </summary>
    public void EmitBuildingConstructionCopySource(int sourceAgentId, int variant, int floors, float vv1, float vv2, float vv3)
    {
        var payload = new Dictionary<string, object>
        {
            ["source_agent_id"] = sourceAgentId,
            ["variant"] = variant,
            ["floors"] = floors,
            ["visual_variant"] = vv1,
            ["visual_variant2"] = vv2,
            ["visual_variant3"] = vv3
        };

        SendEnvelope("building_construction:copy_source", payload);
    }

    /// <summary>
    /// Sends edit_path:select_node.
    /// </summary>
    public void EmitEditPathSelectNode(int pathId, int nodeId, float[] position, string nodeType, float height = 0f, float embankmentHeight = 0f)
    {
        var payload = new Dictionary<string, object>
        {
            ["path_id"] = pathId,
            ["node_id"] = nodeId,
            ["position"] = new List<float> { position[0], position[1], position[2] },
            ["node_type"] = nodeType,
            ["height"] = height,
            ["embankment_height"] = embankmentHeight
        };

        SendEnvelope("edit_path:select_node", payload);
    }

    /// <summary>
    /// Sends edit_path:select_merge_target.
    /// Reports a selected merge partner in STATE_MERGE_PATH.
    /// The UI fetches a merge preview (POST /paths/merge dry_run), displays length, then confirms.
    /// </summary>
    public void EmitPathMergeTargetSelected(int pathId, string pathName)
    {
        var payload = new Dictionary<string, object>
        {
            ["path_id"] = pathId,
            ["path_name"] = pathName ?? ""
        };

        SendEnvelope("edit_path:select_merge_target", payload);
    }

    /// <summary>
    /// Sends edit_path:select_split_point.
    /// Reports a selected split point in STATE_SPLIT_PATH.
    /// Coordinates are [x, y, z] projected onto the target centerline. The UI fetches a split preview
    /// (POST /paths/{id}/split dry_run), displays both lengths, then confirms.
    /// </summary>
    public void EmitPathSplitPointSelected(Vector3 position)
    {
        var payload = new Dictionary<string, object>
        {
            ["point"] = new List<float> { position.x, position.y, position.z }
        };

        SendEnvelope("edit_path:select_split_point", payload);
    }

    /// <summary>
    /// Sends edit_road:toggle_path.
    /// Reports street/highway section clicks in STATE_EDIT_ROAD_MEMBERS.
    /// The UI changes membership via API (shift=true adds sections traced straight ahead together),
    /// returning the full result through edit_road:set_members.
    /// </summary>
    public void EmitRoadTogglePath(int pathId, string pathName, int pathType, bool shift)
    {
        var payload = new Dictionary<string, object>
        {
            ["path_id"] = pathId,
            ["path_name"] = pathName ?? "",
            ["path_type"] = pathType,
            ["shift"] = shift
        };

        SendEnvelope("edit_road:toggle_path", payload);
    }

    /// <summary>
    /// Sends edit_road:ended.
    /// Reports the end of STATE_EDIT_ROAD_MEMBERS regardless of reason
    /// (keeps the UI synchronized with exits outside the panel, such as direct state changes, Esc, or session resets).
    /// sessionId is the value the UI supplied in the initial STATE_EDIT_ROAD_MEMBERS; preserve its type
    /// for strict equality in the panel. Omit session_id when null.
    /// Send only once on mode exit, never periodically.
    /// </summary>
    public void EmitRoadMemberEditEnded(int roadId, object sessionId = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["road_id"] = roadId
        };
        if (sessionId != null) payload["session_id"] = sessionId;

        SendEnvelope("edit_road:ended", payload);
    }

    /// <summary>
    /// Sends world:roads_changed.
    /// Reports road section deletion outside the UI (bulldozing), refreshing road lists,
    /// detail panels, and road names on the city map. The backend deletes roads with no sections,
    /// so open detail panels can show their deletion in response.
    /// Send only after successful deletion, never periodically.
    /// </summary>
    public void EmitRoadsChanged()
    {
        SendEnvelope("world:roads_changed", new Dictionary<string, object>());
    }

    /// <summary>
    /// Sends edit_path:update_shape.
    /// waypointControls contains each intermediate anchor's (selected_points excluding both ends)
    /// Bezier controls {control_in, control_out} ([x, relative height, z]; null if unset).
    /// The UI includes them in submitted waypoints to persist drag-adjusted curves.
    /// isCurved is the current curve mode, owned by Unity (RoadPlanManager.Curved).
    /// The UI uses it for confirmation (curved), previews, and cost estimates,
    /// so always send it even with an empty point list.
    /// extras contains additional 2D path editor fields (phase / selected_index /
    /// lateral_offsets / alignment), merged directly at the top level.
    /// </summary>
    public void EmitEditPathUpdateShape(
        int unsentPathsCount,
        int pathType,
        List<List<float>> selectedPoints = null,
        List<float> startControl = null,
        List<float> endControl = null,
        List<float> embankmentHeights = null,
        List<Dictionary<string, object>> waypointControls = null,
        bool isCurved = false,
        int followTerrain = Constants.VALUE_NONE,
        bool finalized = false,
        bool canConfirm = false,
        Dictionary<string, object> extras = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["unsent_paths_count"] = unsentPathsCount,
            ["path_type"] = pathType,
            ["is_curved"] = isCurved,
            // Always send the individual terrain-following setting (Unity is authoritative). The UI adds
            // follow_terrain to confirmation payloads only when not VALUE_NONE.
            ["follow_terrain"] = followTerrain,
            // true on shape finalization (Placing→Editing, right click). The UI triggers one authoritative
            // backend interference query then. false for drag updates.
            ["is_finalized"] = finalized,
            ["can_confirm"] = canConfirm,
        };

        if (selectedPoints != null)
        {
            payload["selected_points"] = selectedPoints;
        }

        if (startControl != null)
        {
            payload["start_control"] = startControl;
        }

        if (endControl != null)
        {
            payload["end_control"] = endControl;
        }

        if (embankmentHeights != null)
        {
            payload["embankment_heights"] = embankmentHeights;
        }

        if (waypointControls != null)
        {
            payload["waypoint_controls"] = waypointControls;
        }

        // Additional 2D path editor fields (phase / selected_index / lateral_offsets / alignment).
        // Built by RoadPlanManager.BuildShapeSyncExtras.
        if (extras != null)
        {
            foreach (var kv in extras) payload[kv.Key] = kv.Value;
        }

        SendEnvelope("edit_path:update_shape", payload);
    }

    /// <summary>
    /// Sends edit_building:select_parcel.
    /// Reports parcel selection with its geometry to the UI.
    /// </summary>
    public void EmitEditBuildingSelectParcel(int parcelId, float[] position, bool isEditMode, int editingBuildingId, float[][] polygon = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["parcel_id"] = parcelId,
            ["is_edit_mode"] = isEditMode,
            ["editing_building_id"] = editingBuildingId
        };

        if (position != null)
        {
            payload["position"] = new List<float> { position[0], position[1], position[2] };
        }

        if (polygon != null)
        {
            payload["polygon"] = polygon;
        }

        SendEnvelope("edit_building:select_parcel", payload);
    }

    /// <summary>
    /// Sends edit_building:select_point.
    /// Reports clicks outside parcels in direct building placement.
    /// The UI requests a backend rectangle footprint preview centered here and enters direct placement.
    /// angle contains Unity's current direct placement angle (degrees) at confirmation.
    /// Preserves the ghost angle when it was rotated by middle-button dragging before confirmation.
    /// </summary>
    public void EmitEditBuildingSelectPoint(Vector3 position, int angle)
    {
        var payload = new Dictionary<string, object>
        {
            ["position"] = new List<float> { position.x, position.y, position.z },
            ["angle"] = angle
        };

        SendEnvelope("edit_building:select_point", payload);
    }

    /// <summary>Returns placement method, position, dimensions, and angle to the panel as one generation.</summary>
    public void EmitEditBuildingPlacementState(Dictionary<string, object> state)
    {
        SendEnvelope("edit_building:placement_state", state);
    }

    /// <summary>
    /// Sends edit_building:update_direct_angle.
    /// When middle-button rotation ends in direct placement (button release),
    /// reports Unity's final angle (degrees) to update the UI rotation stepper (rectAngle).
    /// </summary>
    public void EmitEditBuildingUpdateDirectAngle(int angle)
    {
        var payload = new Dictionary<string, object>
        {
            ["angle"] = angle
        };

        SendEnvelope("edit_building:update_direct_angle", payload);
    }

    /// <summary>
    /// Sends edit_building:invalid_parcel.
    /// Reports clicks on parcels where the selected building cannot stand in construction mode. Used to display a UI warning.
    /// reason is a BuildingPlanManager.SITE_* code; water_depth and berth_depth (m) let the UI show the shortfall.
    /// </summary>
    public void EmitEditBuildingInvalidParcel(int parcelId, string reason, float waterDepth, float berthDepth)
    {
        var payload = new Dictionary<string, object>
        {
            ["parcel_id"] = parcelId,
            ["reason"] = reason,
            ["water_depth"] = waterDepth,
            ["berth_depth"] = berthDepth,
        };

        SendEnvelope("edit_building:invalid_parcel", payload);
    }

    /// <summary>
    /// Sends edit_parcel:select_point.
    /// Reports 3D point clicks in parcel creation (empty_area).
    /// </summary>
    public void EmitEditParcelSelectPoint(Vector3 position)
    {
        var payload = new Dictionary<string, object>
        {
            ["position"] = new List<float> { position.x, position.y, position.z }
        };

        SendEnvelope("edit_parcel:select_point", payload);
    }

    /// <summary>
    /// Sends edit_parcel:set_mode.
    /// Reports creation mode changes from the 3D radial menu (ParcelEditControlMenu).
    /// Unity (ParcelEditorManager.CurrentMode) owns creation mode; the panel adjusts
    /// its fields (width/depth/average area, etc.) and selection display accordingly.
    /// </summary>
    public void EmitEditParcelSetMode(string mode)
    {
        var payload = new Dictionary<string, object>
        {
            ["mode"] = mode ?? ""
        };

        SendEnvelope("edit_parcel:set_mode", payload);
    }

    /// <summary>
    /// Sends edit_parcel:select_path.
    /// Reports path selection in parcel creation (along_path).
    /// position is the click projected onto the centerline; when creation extent is the clicked point,
    /// it anchors UI creation at that location only (unused for the whole path).
    /// </summary>
    public void EmitEditParcelSelectPath(int pathId, Vector3 position)
    {
        var payload = new Dictionary<string, object>
        {
            ["path_id"] = pathId,
            ["position"] = new List<float> { position.x, position.y, position.z }
        };

        SendEnvelope("edit_parcel:select_path", payload);
    }

    /// <summary>
    /// Sends edit_parcel:select_parcel.
    /// Reports existing parcel selection in parcel subdivision modes.
    /// </summary>
    public void EmitEditParcelSelectParcel(int parcelId)
    {
        var payload = new Dictionary<string, object>
        {
            ["parcel_id"] = parcelId
        };

        SendEnvelope("edit_parcel:select_parcel", payload);
    }

    /// <summary>
    /// Sends edit_parcel:update_shape.
    /// Reports changes to the polygon in parcel shape editing.
    /// </summary>
    public void EmitEditParcelUpdateShape(int parcelId, List<List<float>> polygon)
    {
        var payload = new Dictionary<string, object>
        {
            ["parcel_id"] = parcelId,
            ["polygon"] = polygon
        };

        SendEnvelope("edit_parcel:update_shape", payload);
    }

    /// <summary>
    /// Sends edit_resource:update_shape.
    /// Reports changes to the polygon in resource area editing.
    /// Unlike parcel editing, includes no ID because the area is new and has no existing ID.
    /// </summary>
    public void EmitEditResourceUpdateShape(List<List<float>> polygon)
    {
        var payload = new Dictionary<string, object>
        {
            ["polygon"] = polygon
        };

        SendEnvelope("edit_resource:update_shape", payload);
    }

    /// <summary>
    /// Sends edit_grading:update_shape.
    /// Reports earthworks polygon changes. Each vertex is [x, z].
    /// finalized indicates a shape closed by right click (Editing). The UI does not estimate open polylines.
    /// </summary>
    public void EmitEditGradingUpdateShape(List<List<float>> polygon, bool finalized)
    {
        var payload = new Dictionary<string, object>
        {
            ["polygon"] = polygon,
            ["finalized"] = finalized
        };

        SendEnvelope("edit_grading:update_shape", payload);
    }

    /// <summary>
    /// Sends edit_transit:update_points.
    /// Reports changes to traversal points (ordered stops/waypoints) during public transport line placement.
    /// Each point is {kind: "stop" or "waypoint", position: [x, y, z]} (y=0 because public transport paths use relative Y=0).
    /// New/route edit panels (TransitCreationPanel) update stop/waypoint counts and the traversal points sent on confirmation.
    /// </summary>
    public void EmitEditTransitUpdatePoints(List<Dictionary<string, object>> points)
    {
        var payload = new Dictionary<string, object>
        {
            ["points"] = points
        };

        SendEnvelope("edit_transit:update_points", payload);
    }

    /// <summary>
    /// Sends edit_prop:update_instances.
    /// Reports placed-instance sequence changes in individual prop mode.
    /// Entries are { prop_type, x, y, z, rotation, scale }. PropPlacementPanel updates counts
    /// and the instance list for POST.
    /// </summary>
    public void EmitEditPropUpdateInstances(List<Dictionary<string, object>> instances)
    {
        var payload = new Dictionary<string, object>
        {
            ["instances"] = instances
        };

        SendEnvelope("edit_prop:update_instances", payload);
    }

    /// <summary>
    /// Sends edit_prop:update_shape.
    /// Reports scatter polygon changes in area prop mode.
    /// Vertices are [x, z] (same as edit_resource:update_shape).
    /// </summary>
    public void EmitEditPropUpdateShape(List<List<float>> polygon, bool finalized)
    {
        var payload = new Dictionary<string, object>
        {
            ["polygon"] = polygon,
            ["finalized"] = finalized
        };

        SendEnvelope("edit_prop:update_shape", payload);
    }

    /// <summary>
    /// Sends edit_prop:placement_settings.
    /// Reports next-ghost rotation/height changes from 3D controls in individual prop mode
    /// (middle-button rotation, PageUp/PageDown height) to synchronize panel steppers
    /// (same role as building edit_building:update_direct_angle; the UI does not echo it).
    /// </summary>
    public void EmitEditPropPlacementSettings(float rotation, float height)
    {
        var payload = new Dictionary<string, object>
        {
            ["rotation"] = rotation,
            ["height"] = height
        };

        SendEnvelope("edit_prop:placement_settings", payload);
    }

    /// <summary>
    /// Sends remove_object:request_confirm.
    /// When bulldozing irreversible, broad-impact targets (scatter props, e.g. entire forests),
    /// Unity requests UI confirmation instead of deleting immediately. The persistent cockpit displays a dialog
    /// and returns remove_object:confirm_result. object_type is "prop_scatter".
    /// </summary>
    public void EmitRemoveObjectRequestConfirm(string objectType, int id, int propType)
    {
        var payload = new Dictionary<string, object>
        {
            ["object_type"] = objectType,
            ["id"] = id,
            ["prop_type"] = propType
        };

        SendEnvelope("remove_object:request_confirm", payload);
    }

    /// <summary>
    /// Sends edit_parcel:update_rotation.
    /// Reports the final angle after left-button dragging in parcel creation (empty_area).
    /// </summary>
    public void EmitEditParcelUpdateRotation(float angle)
    {
        var payload = new Dictionary<string, object>
        {
            ["angle"] = angle
        };

        SendEnvelope("edit_parcel:update_rotation", payload);
    }


    public void EmitGridAngleChanged(float angle, float originX, float originZ)
    {
        var payload = new Dictionary<string, object>
        {
            ["angle"] = angle,
            ["origin_x"] = originX,
            ["origin_z"] = originZ
        };

        SendEnvelope("view:grid_angle_changed", payload);
    }

    /// <summary>
    /// Sends view:grid_toggled.
    /// Synchronizes grid settings panel visibility after toggling the grid from the radial menu.
    /// visible=current visibility.
    /// </summary>
    public void EmitViewGridToggled(bool visible)
    {
        var payload = new Dictionary<string, object>
        {
            ["visible"] = visible
        };

        SendEnvelope("view:grid_toggled", payload);
    }

    /// <summary>
    /// Sends citizen:follow_state. Reports follow start/end (including Unity-side reasons such as Esc, camera controls, or edit mode)
    /// to the citizen panel/tracking list so camera buttons match.
    /// </summary>
    public void EmitCitizenFollowState(int citizenId, bool following)
    {
        SendEnvelope("citizen:follow_state", new Dictionary<string, object> { ["citizen_id"] = citizenId, ["following"] = following });
    }

    /// <summary>
    /// Sends view:ui_visibility. Hides all Tauri windows during fly/photography mode or driving to show only the 3D view.
    /// </summary>
    public void EmitUiVisibility(bool visible)
    {
        SendEnvelope("view:ui_visibility", new Dictionary<string, object> { ["visible"] = visible });
    }

    /// <summary>
    /// Sends citizen:pick_failed. The UI explains why citizen selection or itinerary retrieval failed for a clicked instance (core status).
    /// </summary>
    public void EmitCitizenPickFailed(string status)
    {
        SendEnvelope("citizen:pick_failed", new Dictionary<string, object> { ["status"] = status });
    }

    /// <summary>
    /// Sends drive:request. As driving selection text must use the UI's current language,
    /// ask the persistent cockpit to start and begin on its drive:open reply.
    /// </summary>
    public void EmitDriveRequest()
    {
        SendEnvelope("drive:request", new Dictionary<string, object>());
    }

    /// <summary>
    /// Sends camera:state (reply to camera:query_state).
    /// Reports 3D camera position/view direction to the 2D city map.
    /// yaw is clockwise degrees from north (world +Z). Looking straight down removes the horizontal
    /// view component, so use camera Y rotation (screen-up direction) instead.
    /// </summary>
    public void EmitCameraState()
    {
        Camera cam = CameraController.Instance != null
            ? CameraController.Instance.GetComponent<Camera>()
            : Camera.main;
        if (cam == null) return;

        Transform tr = cam.transform;
        Vector3 forward = tr.forward;
        Vector2 flatForward = new Vector2(forward.x, forward.z);
        float yaw = flatForward.sqrMagnitude < 1e-6f
            ? tr.eulerAngles.y
            : Mathf.Atan2(flatForward.x, flatForward.y) * Mathf.Rad2Deg;

        var payload = new Dictionary<string, object>
        {
            ["position"] = new List<float> { tr.position.x, tr.position.y, tr.position.z },
            ["yaw"] = yaw
        };

        SendEnvelope("camera:state", payload);
    }

    /// <summary>
    /// Sends view:current_state.
    /// Returns current display state in response to UI queries.
    /// </summary>
    public void EmitViewCurrentState(Dictionary<string, object> layers)
    {
        var payload = new Dictionary<string, object>
        {
            ["layers"] = layers
        };

        SendEnvelope("view:current_state", payload);
    }

    /// <summary>
    /// Sends view:object_filter_state.
    /// Returns complete current hidden ID sets for per-object display filters.
    /// Unity owns visibility; the UI queries this on panel opening to synchronize
    /// (same pattern as view:query_state / view:current_state).
    /// </summary>
    public void EmitViewObjectFilterState(List<int> hiddenBuildingIds, List<int> hiddenPathIds)
    {
        SendEnvelope("view:object_filter_state", new Dictionary<string, object>
        {
            ["hidden_building_ids"] = hiddenBuildingIds ?? new List<int>(),
            ["hidden_path_ids"] = hiddenPathIds ?? new List<int>()
        });
    }

    public void EmitWeatherState(string id, bool locked, int rain, int snow, object[] presets)
    {
        SendEnvelope("weather:state", new Dictionary<string, object>
        {
            ["id"] = id, ["locked"] = locked, ["rain"] = rain, ["snow"] = snow, ["presets"] = presets
        });
    }

    public void EmitEpochDarkened(string id)
    {
        SendEnvelope("epoch:darkened", new Dictionary<string, object> { ["id"] = id });
    }

    /// <summary>
    /// Sends camera:thumbnail_captured (reply to camera:capture_thumbnail).
    /// success indicates whether the image reached the core. Always send even on failure, as the UI continues saving regardless.
    /// </summary>
    public void EmitCameraThumbnailCaptured(string id, bool success)
    {
        SendEnvelope("camera:thumbnail_captured", new Dictionary<string, object> { ["id"] = id, ["success"] = success });
    }

    /// <summary>
    /// Sends asset_manager:models (reply to asset_manager:render). Each row has row, source (standard or pack ID),
    /// unit (null if no model), and models ([{id, weight}]; weight is null for paths/props).
    /// </summary>
    public void EmitAssetManagerModels(string requestId, List<Dictionary<string, object>> rows)
    {
        SendEnvelope("asset_manager:models", new Dictionary<string, object> { ["request_id"] = requestId, ["rows"] = rows });
    }

    /// <summary>
    /// Sends asset_manager:thumbnail after each thumbnail file is written. file is the filename under the user folder's
    /// cache/asset_thumbnails/ (null if capture failed).
    /// </summary>
    public void EmitAssetManagerThumbnail(string requestId, int row, int model, string file)
    {
        SendEnvelope("asset_manager:thumbnail", new Dictionary<string, object>
        {
            ["request_id"] = requestId, ["row"] = row, ["model"] = model, ["file"] = file
        });
    }

    /// <summary>
    /// Sends input:current_modes.
    /// Returns current input/display modes for debug panel queries.
    /// </summary>
    public void EmitInputCurrentModes(Dictionary<string, object> objectTypes)
    {
        var payload = new Dictionary<string, object>
        {
            ["object_types"] = objectTypes
        };

        SendEnvelope("input:current_modes", payload);
    }

    /// <summary>
    /// Sends game:loaded, notifying Tauri of the request generation whose initial rendering completed.
    /// </summary>
    public void EmitGameLoaded(int loadingId)
    {
        SendEnvelope("game:loaded", new Dictionary<string, object> { { "loading_id", loadingId } });
    }

    /// <summary>
    /// Sends system:graphic_ready.
    /// Notifies Tauri when Unity starts, both initial launch and automatic restart after a crash.
    /// Tauri clears popups only on subsequent receptions (post-crash restarts),
    /// leaving initial-launch loading screens, etc. being operated by the user intact.
    /// </summary>
    public void EmitGraphicReady()
    {
        SendEnvelope("system:graphic_ready", new Dictionary<string, object> {
            { "frontend_version", FrontendVersion.Version },
            { "ipc_contract_version", FrontendVersion.IpcContract },
            { "core_compatible", BackendCompatibility.Compatible }
        });
    }

    /// <summary>
    /// Sends system:graphic_stopped.
    /// Reports normal Unity shutdown (including Editor Play stop), clearing Tauri's connected flag.
    /// Without this, after connecting once Tauri keeps waiting for rendering even in development without Unity,
    /// leaving Tips stuck on Now Loading when reloading after Play stops.
    /// </summary>
    public void EmitGraphicStopped()
    {
        SendEnvelope("system:graphic_stopped", new Dictionary<string, object>());
    }

    // ======================================================================
    // Internal utilities
    // ======================================================================

    /// <summary>
    /// Creates an envelope and sends it to Tauri.
    /// </summary>
    public void EmitTutorialApplied(string runId, int revision)
    {
        SendEnvelope("tutorial:applied", new Dictionary<string, object> { ["runId"] = runId, ["revision"] = revision });
    }

    private void SendEnvelope(string eventName, object payload)
    {
        if (_transport == null) return;

        IpcEnvelope envelope = IpcEnvelope.CreateOutgoing(eventName, payload);
        string json = envelope.ToJson();
        try
        {
            _transport.Send(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"IpcClient: 送信エラー: {e.Message}");
        }
    }

    private static string GetParamString(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue("params", out object paramsObj))
        {
            if (paramsObj is Newtonsoft.Json.Linq.JObject jParams
                && jParams.TryGetValue(key, out var jVal))
            {
                return jVal.Type == Newtonsoft.Json.Linq.JTokenType.Null ? null : jVal.ToString();
            }
            if (paramsObj is Dictionary<string, object> @params
                && @params.TryGetValue(key, out object val))
            {
                return val?.ToString();
            }
        }

        // Keys directly present without a params wrapper.
        if (payload.TryGetValue(key, out object directVal))
        {
            return directVal?.ToString();
        }

        return null;
    }

    /// <summary>
    /// Extracts the params dictionary from payload.
    /// Converts JObject params to Dictionary; returns an empty Dictionary for null.
    /// </summary>
    private static Dictionary<string, object> GetParamsDictionary(Dictionary<string, object> payload)
    {
        if (payload.TryGetValue("params", out object paramsObj))
        {
            if (paramsObj is Newtonsoft.Json.Linq.JObject jParams)
            {
                return jParams.ToObject<Dictionary<string, object>>() ?? new Dictionary<string, object>();
            }
            if (paramsObj is Dictionary<string, object> dict)
            {
                return dict;
            }
        }
        return new Dictionary<string, object>();
    }

    private static int GetParamInt(Dictionary<string, object> payload, string key)
    {
        string str = GetParamString(payload, key);
        if (int.TryParse(str, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int result))
        {
            return result;
        }

        if (payload.TryGetValue("params", out object paramsObj))
        {
            if (paramsObj is Newtonsoft.Json.Linq.JObject jParams)
            {
                var val = jParams[key];
                if (val != null && val.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                    return (int)val;
            }
            else if (paramsObj is Dictionary<string, object> @params)
            {
                if (@params.TryGetValue(key, out object val) && val is long longVal)
                    return (int)longVal;
                if (@params.TryGetValue(key, out object val2) && val2 is int intVal)
                    return intVal;
            }
        }

        return 0;
    }

    private static float GetParamFloat(Dictionary<string, object> payload, string key)
    {
        string str = GetParamString(payload, key);
        if (float.TryParse(str, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float result))
        {
            return result;
        }

        if (payload.TryGetValue("params", out object paramsObj))
        {
            if (paramsObj is Newtonsoft.Json.Linq.JObject jParams)
            {
                var val = jParams[key];
                if (val != null)
                {
                    if (val.Type == Newtonsoft.Json.Linq.JTokenType.Float
                        || val.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                        return (float)val;
                }
            }
            else if (paramsObj is Dictionary<string, object> @params)
            {
                if (@params.TryGetValue(key, out object val) && val is double doubleVal)
                    return (float)doubleVal;
                if (@params.TryGetValue(key, out object val2) && val2 is long longVal)
                    return (float)longVal;
            }
        }

        return 0f;
    }

    /// <summary>
    /// Gets arbitrary JSON tokens (objects, etc.) unchanged from payload params.
    /// Used to pass raw discriminated unions (circle/polygon), such as coverage region.
    /// Returns null if absent/null.
    /// </summary>
    private static Newtonsoft.Json.Linq.JToken GetParamToken(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue("params", out object paramsObj))
        {
            if (paramsObj is Newtonsoft.Json.Linq.JObject jParams
                && jParams.TryGetValue(key, out var jVal))
            {
                return jVal != null && jVal.Type == Newtonsoft.Json.Linq.JTokenType.Null ? null : jVal;
            }
            else if (paramsObj is Dictionary<string, object> @params
                && @params.TryGetValue(key, out object val))
            {
                return val as Newtonsoft.Json.Linq.JToken;
            }
        }
        return null;
    }

    private static float[][] GetParamFloat2D(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue("params", out object paramsObj))
        {
            Newtonsoft.Json.Linq.JArray jArr = null;

            if (paramsObj is Newtonsoft.Json.Linq.JObject jParams
                && jParams.TryGetValue(key, out var jVal)
                && jVal is Newtonsoft.Json.Linq.JArray arr)
            {
                jArr = arr;
            }
            else if (paramsObj is Dictionary<string, object> @params
                && @params.TryGetValue(key, out object val)
                && val is Newtonsoft.Json.Linq.JArray arr2)
            {
                jArr = arr2;
            }

            if (jArr != null)
            {
                var result = new float[jArr.Count][];
                for (int i = 0; i < jArr.Count; i++)
                {
                    var inner = jArr[i] as Newtonsoft.Json.Linq.JArray;
                    if (inner != null)
                    {
                        result[i] = new float[inner.Count];
                        for (int j = 0; j < inner.Count; j++)
                        {
                            result[i][j] = (float)inner[j];
                        }
                    }
                }
                return result;
            }
        }
        return null;
    }

    /// <summary>
    /// Gets a one-dimensional float array from payload params.
    /// Supports both JObject and Dictionary&lt;string, object&gt;.
    /// </summary>
    private static float[] GetParamFloat1D(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue("params", out object paramsObj))
        {
            Newtonsoft.Json.Linq.JArray jArr = null;

            if (paramsObj is Newtonsoft.Json.Linq.JObject jParams
                && jParams.TryGetValue(key, out var jVal)
                && jVal is Newtonsoft.Json.Linq.JArray arr)
            {
                jArr = arr;
            }
            else if (paramsObj is Dictionary<string, object> @params
                && @params.TryGetValue(key, out object val)
                && val is Newtonsoft.Json.Linq.JArray arr2)
            {
                jArr = arr2;
            }

            if (jArr != null)
            {
                var result = new float[jArr.Count];
                for (int i = 0; i < jArr.Count; i++)
                {
                    result[i] = (float)jArr[i];
                }
                return result;
            }
        }
        return null;
    }

    /// <summary>
    /// Gets a one-dimensional int array from payload params.
    /// Supports both JObject and Dictionary&lt;string, object&gt;.
    /// </summary>
    private static int[] GetParamInt1D(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue("params", out object paramsObj))
        {
            Newtonsoft.Json.Linq.JArray jArr = null;

            if (paramsObj is Newtonsoft.Json.Linq.JObject jParams
                && jParams.TryGetValue(key, out var jVal)
                && jVal is Newtonsoft.Json.Linq.JArray arr)
            {
                jArr = arr;
            }
            else if (paramsObj is Dictionary<string, object> @params
                && @params.TryGetValue(key, out object val)
                && val is Newtonsoft.Json.Linq.JArray arr2)
            {
                jArr = arr2;
            }

            if (jArr != null)
            {
                var result = new int[jArr.Count];
                for (int i = 0; i < jArr.Count; i++)
                {
                    result[i] = (int)jArr[i];
                }
                return result;
            }
        }
        return null;
    }

    /// <summary>
    /// Gets a three-dimensional float array from payload params.
    /// Mainly for parsing preview_polygons ([[[x,z], ...], ...]).
    /// </summary>
    private static float[][][] GetParamFloat3D(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue("params", out object paramsObj))
        {
            Newtonsoft.Json.Linq.JArray jOuter = null;

            if (paramsObj is Newtonsoft.Json.Linq.JObject jParams
                && jParams.TryGetValue(key, out var jVal)
                && jVal is Newtonsoft.Json.Linq.JArray arr)
            {
                jOuter = arr;
            }
            else if (paramsObj is Dictionary<string, object> @params
                && @params.TryGetValue(key, out object val)
                && val is Newtonsoft.Json.Linq.JArray arr2)
            {
                jOuter = arr2;
            }

            if (jOuter != null)
            {
                var result = new float[jOuter.Count][][];
                for (int i = 0; i < jOuter.Count; i++)
                {
                    var midArr = jOuter[i] as Newtonsoft.Json.Linq.JArray;
                    if (midArr == null) continue;
                    result[i] = new float[midArr.Count][];
                    for (int j = 0; j < midArr.Count; j++)
                    {
                        var inner = midArr[j] as Newtonsoft.Json.Linq.JArray;
                        if (inner != null)
                        {
                            result[i][j] = new float[inner.Count];
                            for (int k = 0; k < inner.Count; k++)
                            {
                                result[i][j][k] = (float)inner[k];
                            }
                        }
                    }
                }
                return result;
            }
        }
        return null;
    }
}
