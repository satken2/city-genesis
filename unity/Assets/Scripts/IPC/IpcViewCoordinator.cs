using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Receives view.change_view via IPC
/// and switches VisualModeHandler per layer.
///
/// This class's memory (_layerModes / _layerSubModes / _layerFlags) owns display state;
/// all views forced by edit states (road construction, parcel editing, etc.) or display presets pass here.
/// On returning to STATE_NORMAL, restore the display the user selected in normal mode
/// from _normalStateMemory.
/// </summary>
public class IpcViewCoordinator : MonoBehaviour
{
    /// <summary>
    /// Preset name for temporarily replacing display while a window is open (view.apply_preset preset).
    /// Used by windows without dedicated edit states (public transport management/creation).
    /// Returning to STATE_NORMAL restores the original display (Rust sends it when closing the window).
    /// </summary>
    public const string PRESET_TRANSIT = "transit";

    /// <summary>
    /// Display preset while the parcel detail panel is open.
    /// Forces parcel outlines visible and restores the original display on closure.
    /// </summary>
    public const string PRESET_PARCEL_DETAIL = "parcel_detail";

    /// <summary>
    /// Layer color modes. Awake/game:loading initialize from <see cref="LAYER_DEFAULTS"/>.
    /// grid has its own toggle outside the display panel, so initialize it here rather than in the defaults table.
    /// </summary>
    private Dictionary<string, string> _layerModes = new Dictionary<string, string> { { "grid", "hidden" } };

    private Dictionary<string, string> _layerSubModes = new Dictionary<string, string>();

    /// <summary>
    /// Per-layer display flags (CSV, e.g. "plan,public").
    /// An ON/OFF set independent of color modes, currently used only by road/building layers.
    /// </summary>
    private Dictionary<string, string> _layerFlags = new Dictionary<string, string>();

    /// <summary>
    /// Current state, used by HandleViewChange to remember changes only in STATE_NORMAL.
    /// </summary>
    private string _currentState = "STATE_NORMAL";

    /// <summary>
    /// Remembers layer display changes made by the user in STATE_NORMAL.
    /// Restored on the next return to STATE_NORMAL.
    /// Changes in other states are not remembered.
    /// </summary>
    private Dictionary<string, (string mode, string subMode, string flags)> _normalStateMemory = new Dictionary<string, (string, string, string)>();

    /// <summary>
    /// Normal-mode display defaults for layers the user has never changed.
    /// Must match UI initial values in displayConstants.ts DISPLAY_LAYERS.
    /// Hide planned buildings/roads normally, showing them temporarily in relevant edit states.
    /// Terrain has no forced color mode, so exclude it from this table and handle separately.
    /// </summary>
    private static readonly Dictionary<string, (string mode, string subMode, string flags)> NORMAL_DEFAULTS =
        new Dictionary<string, (string, string, string)>
    {
        { "building", ("default", null, null) },
        { "road", ("default", null, null) },
        { "parcel", ("hidden", null, null) },
        { "district", ("hidden", null, null) },
        { "prop", ("visible", null, null) },
        { "transit", ("hidden", null, null) },
        { "resource", ("hidden", null, null) },
        { "alert", ("visible", null, null) },
        { "coverage", ("hidden", null, null) },
    };

    /// <summary>
    /// Defaults for layers selectable in the display panel (displayConstants.ts DISPLAY_LAYERS).
    /// Must match UI initial values. Combines normal restoration defaults (NORMAL_DEFAULTS)
    /// with terrain, whose color mode is never forced.
    /// </summary>
    private static readonly Dictionary<string, (string mode, string subMode, string flags)> LAYER_DEFAULTS = BuildLayerDefaults();

    private static Dictionary<string, (string mode, string subMode, string flags)> BuildLayerDefaults()
    {
        var defaults = new Dictionary<string, (string mode, string subMode, string flags)>(NORMAL_DEFAULTS);
        defaults["terrain"] = ("default", null, null);
        return defaults;
    }

    private void Awake()
    {
        ResetLayersToDefaults();
    }

    /// <summary>Resets only remembered _layerModes / _layerSubModes / _layerFlags to defaults, without rendering changes.</summary>
    private void ResetLayersToDefaults()
    {
        foreach (var entry in LAYER_DEFAULTS)
        {
            _layerModes[entry.Key] = entry.Value.mode;
            SetOrRemove(_layerSubModes, entry.Key, entry.Value.subMode);
            SetOrRemove(_layerFlags, entry.Key, entry.Value.flags);
        }
    }

    private static void SetOrRemove(Dictionary<string, string> dictionary, string key, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            dictionary.Remove(key);
        }
        else
        {
            dictionary[key] = value;
        }
    }

    /// <summary>
    /// On loading a new city (game:loading, shared by new games/tutorials/loads),
    /// reset all panel-selectable display state to defaults. Do not carry the previous city's color modes, flags,
    /// or submodes (resource views, alert filters, etc.) into the next city.
    /// Notify the UI to synchronize display panel/tray selections after changes.
    /// </summary>
    private void HandleGameLoading()
    {
        ResetLayersToDefaults();
        _normalStateMemory.Clear();
        _currentState = "STATE_NORMAL";
        ApplyDefaultLayers();
        NotifyStateChanged();
    }

    /// <summary>Applies remembered defaults to rendering.</summary>
    private void ApplyDefaultLayers()
    {
        if (VisualModeHandler.Instance != null)
        {
            foreach (var layer in LAYER_DEFAULTS.Keys)
            {
                // For public transport, only clear displayed types below (ApplyTransitMode refetches lines;
                // avoid fetching the previous city's lines before the new city loads).
                if (layer == "transit") continue;
                _layerSubModes.TryGetValue(layer, out var subMode);
                _layerFlags.TryGetValue(layer, out var flags);
                ApplyLayerMode(layer, _layerModes[layer], subMode, flags ?? "");
            }
        }
        TransitOverlayManager.Instance?.SetVisibleVariants(new HashSet<int>());
        // ApplyLayerMode does not apply alerts (alert filtering changes only through view:change_view).
        _layerSubModes.TryGetValue("alert", out var alertSubMode);
        ApplyAlertMode(_layerModes["alert"], alertSubMode);
    }

    private void OnEnable()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnGameLoading += HandleGameLoading;
            IpcClient.Instance.OnViewChangeView += HandleViewChange;
            IpcClient.Instance.OnViewSetObjectFilter += HandleSetObjectFilter;
            IpcClient.Instance.OnViewQueryObjectFilter += NotifyObjectFilterState;
        }
    }

    private void OnDisable()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnGameLoading -= HandleGameLoading;
            IpcClient.Instance.OnViewChangeView -= HandleViewChange;
            IpcClient.Instance.OnViewSetObjectFilter -= HandleSetObjectFilter;
            IpcClient.Instance.OnViewQueryObjectFilter -= NotifyObjectFilterState;
        }
    }

    /// <summary>
    /// Applies per-object filters (construction project tags / individual visibility).
    /// The UI sends full hidden ID sets, independent of layer color modes/flags.
    /// Return current values afterward to synchronize multiple panels (construction projects/details).
    /// </summary>
    private void HandleSetObjectFilter(int[] hiddenBuildingIds, int[] hiddenPathIds)
    {
        ObjectVisibilityFilter.SetHidden(hiddenBuildingIds, hiddenPathIds);
        NotifyObjectFilterState();
    }

    /// <summary>Reports current hidden sets to the UI.</summary>
    public void NotifyObjectFilterState()
    {
        IpcClient.Instance?.EmitViewObjectFilterState(
            ObjectVisibilityFilter.GetHiddenBuildings(),
            ObjectVisibilityFilter.GetHiddenPaths());
    }

    private void HandleViewChange(string layer, string mode, string subMode, string flags)
    {
        Debug.Log($"IpcViewCoordinator: view.change_view -> layer={layer}, mode={mode}, sub_mode={subMode}, flags={flags ?? "(keep)"}");

        _layerModes[layer] = mode;
        if (subMode != null && subMode.Length > 0)
        {
            _layerSubModes[layer] = subMode;
        }
        else
        {
            _layerSubModes.Remove(layer);
        }

        // flags: null=preserve current / ""=all OFF / otherwise=CSV flag set.
        // Preserve lets older callers such as construction panels change only modes without destroying toggle state.
        if (flags != null)
        {
            if (flags.Length > 0)
            {
                _layerFlags[layer] = flags;
            }
            else
            {
                _layerFlags.Remove(layer);
            }
        }
        string effectiveFlags = _layerFlags.TryGetValue(layer, out var f) ? f : "";

        // Remember only changes in STATE_NORMAL.
        if (_currentState == "STATE_NORMAL")
        {
            _normalStateMemory[layer] = (mode, subMode, effectiveFlags);
            Debug.Log($"HandleViewChange: STATE_NORMAL 中の変更を記憶 -> {layer}={mode}, sub={subMode ?? "(none)"}, flags={effectiveFlags}");
        }

        if (VisualModeHandler.Instance == null) return;

        switch (layer)
        {
            case "terrain":
                ApplyTerrainMode(mode, subMode);
                break;

            case "grid":
                ApplyGridMode(mode);
                break;

            case "road":
                ApplyRoadMode(mode, subMode, effectiveFlags);
                break;

            case "building":
                ApplyBuildingMode(mode, subMode, effectiveFlags);
                break;

            case "parcel":
                ApplyParcelMode(mode, subMode);
                break;

            case "district":
                ApplyDistrictMode(mode);
                break;

            case "resource":
                ApplyResourceMode(mode);
                break;

            case "alert":
                ApplyAlertMode(mode, subMode);
                break;

            case "transit":
                ApplyTransitMode(mode, subMode);
                break;

            case "prop":
                ApplyPropMode(mode);
                break;

            case "coverage":
                ApplyCoverageMode(mode, subMode);
                break;

            default:
                Debug.LogWarning($"IpcViewCoordinator: 未知のレイヤー: {layer}");
                break;
        }
    }

    /// <summary>
    /// Called on UI view.query_state.
    /// Returns all current layer display modes together.
    /// </summary>
    public void QueryCurrentState()
    {
        Debug.Log("IpcViewCoordinator: 現在の表示状態を送信します");
        NotifyStateChanged();
    }

    /// <summary>
    /// Called by IpcStateCoordinator after state changes.
    /// Synchronizes _layerModes for the state and notifies the UI.
    /// ipcParams resolves bd_type from the building variant selected in STATE_EDIT_BUILDING
    /// to set the parcel buildable_color subMode.
    /// </summary>
    public void SyncFromState(string state, Dictionary<string, object> ipcParams = null)
    {
        _currentState = state;

        // During edit states, leave building colors to work displays and hide coverage (construction panels show their own).
        if (state != "STATE_NORMAL")
        {
            ForceLayer("coverage", "hidden");
        }

        switch (state)
        {
            case "STATE_NORMAL":
                RestoreNormalLayerModes();
                break;

            case "STATE_EDIT_PATH":
            case "STATE_MERGE_PATH":
            case "STATE_SPLIT_PATH":
            case "STATE_EDIT_ROAD_MEMBERS":
            case "STATE_EDIT_GRADING":
                ApplyConstructionPreset("border", null);
                break;

            case "STATE_EDIT_BUILDING":
            {
                // In building construction, set parcels to buildable_color for the selected building type
                // to highlight eligible parcels.
                int currentBdType = 0;
                if (ipcParams != null && ipcParams.TryGetValue("building_variant", out object bvObj) && bvObj != null)
                {
                    int buildingVariant = Convert.ToInt32(bvObj);
                    if (MasterDataStore.Instance != null)
                    {
                        var agentMaster = MasterDataStore.Instance.GetAgentMaster(buildingVariant);
                        if (agentMaster != null && agentMaster.bd_type.HasValue)
                        {
                            currentBdType = agentMaster.bd_type.Value;
                        }
                    }
                }

                string filterSubMode = BuildingPlanManager.BdTypeToParcelFilterSubMode(currentBdType);
                if (filterSubMode != null)
                {
                    ApplyConstructionPreset("buildable_color", filterSubMode);
                }
                else
                {
                    ApplyConstructionPreset("border", null);
                }
                break;
            }

            case "STATE_EDIT_PARCEL":
                ApplyLandEditPreset();
                break;

            case "STATE_EDIT_TRANSIT":
                ApplyTransitPreset();
                break;

            case "STATE_REMOVE_OBJECTS":
                // Bulldoze mode shows plans and hides districts.
                // Show parcel boundaries to reveal empty parcels for deletion.
                // Keep props visible because they are deletion targets (unlike construction presets).
                ForceLayer("parcel", "border");
                ForceLayer("road", "default", null, "plan");
                ForceLayer("building", "default", null, "plan");
                ForceLayer("district", "hidden");
                break;

            default:
                Debug.LogWarning($"IpcViewCoordinator: SyncFromState 不明なステート: {state}");
                return;
        }

        Debug.Log($"IpcViewCoordinator: SyncFromState -> {state} (currentState was {_currentState})");
        NotifyStateChanged();
    }

    /// <summary>
    /// Applies a window-specific display preset (view.apply_preset).
    /// Windows without dedicated edit states use it to replace display only while open.
    /// Uses ForceLayer like edit states, preserving _normalStateMemory
    /// and restoring the original display on STATE_NORMAL.
    /// </summary>
    public void ApplyPreset(string preset)
    {
        switch (preset)
        {
            case PRESET_TRANSIT:
                ApplyTransitPreset();
                break;
            case PRESET_PARCEL_DETAIL:
                ApplyParcelDetailPreset();
                break;
            default:
                Debug.LogWarning($"IpcViewCoordinator: 未知の表示プリセット: {preset}");
                return;
        }

        Debug.Log($"IpcViewCoordinator: ApplyPreset -> {preset}");
        NotifyStateChanged();
    }

    /// <summary>
    /// Display while parcel detail is open.
    /// Force parcel boundaries visible to inspect selected parcel/site shapes.
    /// </summary>
    private void ApplyParcelDetailPreset()
    {
        ForceLayer("parcel", "border");
    }

    /// <summary>
    /// Releases a window display preset and restores original state (view.restore_preset).
    /// Lets detail panels coexisting with edit states release only their own preset safely
    /// without sending STATE_NORMAL.
    /// </summary>
    public void RestorePreset(string preset)
    {
        switch (preset)
        {
            case PRESET_PARCEL_DETAIL:
                RestoreParcelDetailPreset();
                break;
            case PRESET_TRANSIT:
                RestoreTransitPreset();
                break;
            default:
                Debug.LogWarning($"IpcViewCoordinator: 未知の復帰プリセット: {preset}");
                return;
        }

        Debug.Log($"IpcViewCoordinator: RestorePreset -> {preset}");
        NotifyStateChanged();
    }

    private void RestoreParcelDetailPreset()
    {
        if (_currentState == "STATE_NORMAL")
        {
            RestoreLayerToNormal("parcel");
        }
        else
        {
            SyncFromState(_currentState);
        }
    }

    private void RestoreTransitPreset()
    {
        if (_currentState == "STATE_NORMAL")
        {
            RestoreLayerToNormal("road");
            RestoreLayerToNormal("building");
            RestoreLayerToNormal("transit");
        }
        else
        {
            SyncFromState(_currentState);
        }
    }

    /// <summary>
    /// Restores one layer from _normalStateMemory or NORMAL_DEFAULTS and applies it to rendering.
    /// </summary>
    private void RestoreLayerToNormal(string layer)
    {
        if (layer == "terrain")
        {
            if (_normalStateMemory.TryGetValue("terrain", out var terrainMem))
            {
                _layerModes["terrain"] = terrainMem.mode;
                if (!string.IsNullOrEmpty(terrainMem.subMode))
                {
                    _layerSubModes["terrain"] = terrainMem.subMode;
                }
                else
                {
                    _layerSubModes.Remove("terrain");
                }
            }
            else
            {
                _layerSubModes.Remove("terrain");
            }
        }
        else if (NORMAL_DEFAULTS.TryGetValue(layer, out var defaultVal))
        {
            var value = _normalStateMemory.TryGetValue(layer, out var mem) ? mem : defaultVal;
            _layerModes[layer] = value.mode;
            if (!string.IsNullOrEmpty(value.subMode))
            {
                _layerSubModes[layer] = value.subMode;
            }
            else
            {
                _layerSubModes.Remove(layer);
            }
            if (!string.IsNullOrEmpty(value.flags))
            {
                _layerFlags[layer] = value.flags;
            }
            else
            {
                _layerFlags.Remove(layer);
            }
        }

        if (VisualModeHandler.Instance == null) return;
        string effectiveFlags = _layerFlags.TryGetValue(layer, out var f) ? f : "";
        ApplyLayerMode(layer, _layerModes[layer], _layerSubModes.TryGetValue(layer, out var s) ? s : null, effectiveFlags);
    }

    /// <summary>
    /// Edit states/presets force one layer's display.
    /// Centralizes remembered state (_layerModes, etc.) and rendering updates;
    /// IpcStateCoordinator must not directly touch VisualModeHandler
    /// (duplicate ownership would desynchronize UI-reported state from actual appearance).
    ///
    /// flags follows view.change_view: null=preserve / ""=all OFF / CSV=specified set.
    /// Do not store these changes in _normalStateMemory; returning to normal
    /// must restore the user's own selections.
    /// </summary>
    private void ForceLayer(string layer, string mode, string subMode = null, string flags = null)
    {
        _layerModes[layer] = mode;
        if (string.IsNullOrEmpty(subMode))
        {
            _layerSubModes.Remove(layer);
        }
        else
        {
            _layerSubModes[layer] = subMode;
        }

        if (flags != null)
        {
            if (flags.Length > 0)
            {
                _layerFlags[layer] = flags;
            }
            else
            {
                _layerFlags.Remove(layer);
            }
        }

        if (VisualModeHandler.Instance == null) return;
        string effectiveFlags = _layerFlags.TryGetValue(layer, out var f) ? f : "";
        ApplyLayerMode(layer, mode, _layerSubModes.TryGetValue(layer, out var s) ? s : null, effectiveFlags);
    }

    /// <summary>
    /// Display during road/track/infrastructure/building construction and earthworks drawing.
    /// Show planned structures and positioning references (parcels, districts, contours),
    /// hiding only obstructing props. The caller chooses parcel color mode
    /// (buildable colors for buildings; boundaries otherwise, including overlaps with existing parcels for earthworks).
    /// </summary>
    private void ApplyConstructionPreset(string parcelMode, string parcelSubMode)
    {
        ForceLayer("parcel", parcelMode, parcelSubMode);
        ForceLayer("road", "default", null, "plan");
        ForceLayer("building", "default", null, "plan");
        ForceLayer("district", "border_icon");
        ForceLayer("prop", "hidden");
        // Enable only contours. Preserve the user's terrain color mode (e.g. elevation colors).
        ForceLayer("terrain", _layerModes["terrain"], "contour_on");
    }

    /// <summary>
    /// Parcel editing display. Show parcels/districts
    /// to identify district membership on the ground. Include planned roads/buildings.
    /// </summary>
    private void ApplyLandEditPreset()
    {
        ForceLayer("parcel", "border");
        ForceLayer("road", "default", null, "plan");
        ForceLayer("building", "default", null, "plan");
        ForceLayer("district", "border_icon");
    }

    /// <summary>
    /// Public transport list/line placement display. Show all existing line types
    /// and planned roads/buildings (stops may be placed on under-construction paths).
    /// Leave parcels/districts unchanged because stops can only be placed on existing paths.
    /// </summary>
    private void ApplyTransitPreset()
    {
        ForceLayer("road", "default", null, "plan");
        ForceLayer("building", "default", null, "plan");

        string allVariants = AllTransitVariantsCsv();
        if (string.IsNullOrEmpty(allVariants))
        {
            // Leave display unchanged until master data is available to filter types.
            return;
        }
        ForceLayer("transit", "default", allVariants);
    }

    /// <summary>
    /// Returns all transit master line types (transit_path_id) as CSV.
    /// The transit submode is a set of displayed types, so
    /// show all means a string listing all IDs (same representation as UI type toggles).
    /// </summary>
    private string AllTransitVariantsCsv()
    {
        var master = MasterDataStore.Instance?.GetTransitMasterDictionary();
        if (master == null || master.Count == 0) return null;

        var ids = new List<string>(master.Count);
        foreach (var item in master.Values)
        {
            ids.Add(item.transit_path_id.ToString());
        }
        return string.Join(",", ids);
    }

    /// <summary>
    /// On STATE_NORMAL, restore display state from _normalStateMemory.
    /// Layers without memory use NORMAL_DEFAULTS (matching UI initial values).
    ///
    /// **Include every layer forced by edit states/display presets here.**
    /// Omissions preserve current values when no memory exists, leaving edit-mode displays in normal mode.
    /// The caller (IpcStateCoordinator) applies rendering through ApplyAllLayerModes.
    /// </summary>
    private void RestoreNormalLayerModes()
    {
        foreach (var entry in NORMAL_DEFAULTS)
        {
            string layer = entry.Key;
            var value = _normalStateMemory.TryGetValue(layer, out var mem) ? mem : entry.Value;

            _layerModes[layer] = value.mode;
            if (!string.IsNullOrEmpty(value.subMode))
            {
                _layerSubModes[layer] = value.subMode;
            }
            else
            {
                _layerSubModes.Remove(layer);
            }
            if (!string.IsNullOrEmpty(value.flags))
            {
                _layerFlags[layer] = value.flags;
            }
            else
            {
                _layerFlags.Remove(layer);
            }
        }

        // Terrain color is not forced (editing changes only the contour submode), so
        // without memory, retain current color mode and disable only contours.
        if (_normalStateMemory.TryGetValue("terrain", out var terrainMem))
        {
            _layerModes["terrain"] = terrainMem.mode;
            if (!string.IsNullOrEmpty(terrainMem.subMode))
            {
                _layerSubModes["terrain"] = terrainMem.subMode;
            }
            else
            {
                _layerSubModes.Remove("terrain");
            }
        }
        else
        {
            _layerSubModes.Remove("terrain");
        }
    }

    /// <summary>
    /// Applies all current _layerModes / _layerSubModes to VisualModeHandler.
    /// Called by IpcStateCoordinator.ApplyStateNormal, etc.
    /// </summary>
    public void ApplyAllLayerModes()
    {
        if (VisualModeHandler.Instance == null) return;

        foreach (var kvp in _layerModes)
        {
            string sub = _layerSubModes.TryGetValue(kvp.Key, out var s) ? s : null;
            string flags = _layerFlags.TryGetValue(kvp.Key, out var f) ? f : "";
            ApplyLayerMode(kvp.Key, kvp.Value, sub, flags);
        }
    }

    private void ApplyLayerMode(string layer, string mode, string subMode, string flags)
    {
        switch (layer)
        {
            case "terrain": ApplyTerrainMode(mode, subMode); break;
            case "grid": ApplyGridMode(mode); break;
            case "road": ApplyRoadMode(mode, subMode, flags); break;
            case "building": ApplyBuildingMode(mode, subMode, flags); break;
            case "parcel": ApplyParcelMode(mode, subMode); break;
            case "district": ApplyDistrictMode(mode); break;
            case "resource": ApplyResourceMode(mode); break;
            case "transit": ApplyTransitMode(mode, subMode); break;
            case "prop": ApplyPropMode(mode); break;
            case "coverage": ApplyCoverageMode(mode, subMode); break;
        }
    }

    /// <summary>
    /// Clears STATE_NORMAL display memory.
    /// Used to initialize state on session resets/reloads, etc.
    /// </summary>
    public void ClearNormalStateMemory()
    {
        _normalStateMemory.Clear();
    }

    /// <summary>
    /// Reports all current layer display modes to the UI.
    /// </summary>
    public void NotifyStateChanged()
    {
        var layers = new Dictionary<string, object>();
        foreach (var kvp in _layerModes)
        {
            string sub = _layerSubModes.ContainsKey(kvp.Key) ? _layerSubModes[kvp.Key] : null;
            string flags = _layerFlags.ContainsKey(kvp.Key) ? _layerFlags[kvp.Key] : null;
            layers[kvp.Key] = new Dictionary<string, string>
            {
                { "mode", kvp.Value },
                { "sub_mode", sub ?? "" },
                { "flags", flags ?? "" }
            };
        }

        IpcClient.Instance?.EmitViewCurrentState(layers);
    }

    private void ApplyTerrainMode(string mode, string subMode = null)
    {
        switch (mode)
        {
            case "default":
                VisualModeHandler.Instance.SetMode(InputObjectType.TERRAIN, VisualMode.MODE_NORMAL);
                break;
            case "transparent":
                VisualModeHandler.Instance.SetMode(InputObjectType.TERRAIN, VisualModeTerrain.TRANSPARENT);
                break;
            case "elevation":
                VisualModeHandler.Instance.SetMode(InputObjectType.TERRAIN, VisualModeTerrain.ELEVATION);
                break;
        }

        // Toggle contour visibility.
        bool showContours = (subMode == "contour_on");
        var tm = FindAnyObjectByType<TerrainManager>();
        if (tm != null)
        {
            tm.SetContourLinesVisible(showContours);
        }
    }

    private void ApplyGridMode(string mode)
    {
        // Grid is managed by its own system.
        // Currently logs only. GridOverlay notifications, etc. are implemented separately.
        Debug.Log($"IpcViewCoordinator: Grid mode -> {mode}");
    }

    private void ApplyRoadMode(string mode, string subMode, string flags)
    {
        // Apply flags such as planned visibility independently of color modes.
        VisualModeHandler.Instance.SetFlags(InputObjectType.PATH, ParseFlags(flags));

        switch (mode)
        {
            case "default":
                VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_NORMAL);
                break;
            case "traffic_color":
                int trafficMode = subMode switch
                {
                    "TRAFFIC_PEDESTRIAN" => VisualModePath.PEDESTRIAN,
                    "TRAFFIC_ROAD" => VisualModePath.UTILIZATION,
                    "TRAFFIC_RAIL" => VisualModePath.SPEED,
                    _ => VisualModePath.UTILIZATION
                };
                VisualModeHandler.Instance.SetMode(InputObjectType.PATH, trafficMode);
                break;
            case "speed_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualModePath.SPEED);
                break;
            case "durability_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualModePath.DURABILITY);
                break;
            case "zone_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualModePath.ZONE_COLOR);
                break;
            case "hidden":
                VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_HIDDEN);
                break;
        }
    }

    private void ApplyBuildingMode(string mode, string subMode, string flags)
    {
        // Apply planned visibility/public facility highlight flags independently of color modes.
        VisualModeHandler.Instance.SetFlags(InputObjectType.BUILDING, ParseFlags(flags));

        switch (mode)
        {
            case "default":
                VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_NORMAL);
                break;
            case "floor_color":
                int floorMode = subMode switch
                {
                    "SPACE_OTHER" => VisualModeBuilding.SPACE_OTHER,
                    "SPACE_RESIDENCE" => VisualModeBuilding.SPACE_RESIDENCE,
                    "SPACE_RETAIL" => VisualModeBuilding.SPACE_RETAIL,
                    "SPACE_FACTORY" => VisualModeBuilding.SPACE_FACTORY,
                    "SPACE_OFFICE" => VisualModeBuilding.SPACE_OFFICE,
                    "SPACE_FARM" => VisualModeBuilding.SPACE_FARM,
                    _ => VisualModeBuilding.SPACE_OTHER
                };
                VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, floorMode);
                break;
            case "durability_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualModeBuilding.DURABILITY);
                break;
            case "zone_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualModeBuilding.ZONE_COLOR);
                break;
            case "hidden":
                VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_HIDDEN);
                break;
        }
    }

    /// <summary>
    /// Converts CSV flags ("plan,public") to a VisualFlag bitwise union.
    /// Ignores unknown flag IDs.
    /// </summary>
    private static int ParseFlags(string flags)
    {
        int result = VisualFlag.NONE;
        if (string.IsNullOrEmpty(flags)) return result;

        foreach (var part in flags.Split(','))
        {
            switch (part.Trim())
            {
                case "plan":
                    result |= VisualFlag.SHOW_PLAN;
                    break;
                case "public":
                    result |= VisualFlag.HIGHLIGHT_PUBLIC;
                    break;
                case "speed_limit":
                    result |= VisualFlag.SPEED_LIMIT_SIGNS;
                    break;
            }
        }
        return result;
    }

    private void ApplyParcelMode(string mode, string subMode)
    {
        switch (mode)
        {
            case "hidden":
                VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_HIDDEN);
                break;
            case "border":
                VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_BOUNDARY);
                break;
            case "land_price_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualModeParcel.LAND_PRICE);
                break;
            case "zone_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualModeParcel.ZONE_COLOR);
                break;
            case "noise_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualModeParcel.NOISE);
                break;
            case "air_pollution_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualModeParcel.AIR_POLLUTION);
                break;
            case "crime_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualModeParcel.CRIME);
                break;
            case "sunshine_color":
                VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualModeParcel.SUNSHINE);
                break;
            case "buildable_color":
                int filterMode = subMode switch
                {
                    "BUILDING_APARTMENT" => VisualModeParcel.FILTER_APARTMENT,
                    "BUILDING_HOUSE" => VisualModeParcel.FILTER_HOUSE,
                    "BUILDING_RETAIL" => VisualModeParcel.FILTER_COMMERCIAL,
                    "BUILDING_FACTORY" => VisualModeParcel.FILTER_FACTORY,
                    "BUILDING_OFFICE" => VisualModeParcel.FILTER_OFFICE,
                    "BUILDING_FARM" => VisualModeParcel.FILTER_FARM,
                    "BUILDING_MINE" => VisualModeParcel.FILTER_MINE,
                    "BUILDING_PORT" => VisualModeParcel.FILTER_PORT,
                    "BUILDING_OTHER" => VisualModeParcel.FILTER_OTHER,
                    "BUILDING_PUBLIC" => VisualModeParcel.FILTER_OTHER,
                    _ => VisualModeParcel.FILTER_HOUSE
                };
                VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, filterMode);
                break;
        }
    }

    private void ApplyDistrictMode(string mode)
    {
        switch (mode)
        {
            case "hidden":
                VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_HIDDEN);
                break;
            case "border_icon":
                VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_NORMAL);
                break;
        }
    }

    /// <summary>
    /// Toggles coverage display. subMode is the product ID of the service good to show.
    /// Refetch even for the same good so returning from editing includes new facility coverage.
    /// </summary>
    private void ApplyCoverageMode(string mode, string subMode)
    {
        int productId = mode != "hidden" && int.TryParse(subMode, out int id) ? id : 0;
        ServiceCoverageOverlay.Ensure();
        ServiceCoverageOverlay.Instance.SetProduct(productId);
    }

    private void ApplyResourceMode(string mode)
    {
        if (ResourceManager.Instance != null)
        {
            ResourceManager.Instance.SetVisible(mode == "visible");
        }
    }

    /// <summary>
    /// Toggles public transport display.
    /// subMode is CSV of displayed line types (transit_master transit_path_id = section path variant).
    /// Empty hides all types. Pass the displayed variant set to TransitOverlayManager.
    /// </summary>
    private void ApplyTransitMode(string mode, string subMode)
    {
        if (TransitOverlayManager.Instance == null) return;

        var variants = new HashSet<int>();
        if (mode != "hidden" && !string.IsNullOrEmpty(subMode))
        {
            foreach (var part in subMode.Split(','))
            {
                if (int.TryParse(part, out int v))
                {
                    variants.Add(v);
                }
            }
        }
        TransitOverlayManager.Instance.SetVisibleVariants(variants);
        // Refetch lines on every display switch. The UI resends this event after saving line colors or deleting lines,
        // updating band colors/composition immediately without waiting for TransitDataStore's periodic fetch.
        TransitOverlayManager.Instance.RefreshLines();
    }

    private void ApplyPropMode(string mode)
    {
        if (PropManager.Instance != null)
        {
            PropManager.Instance.SetVisible(mode == "visible");
        }
    }

    /// <summary>
    /// Applies alert layer filtering.
    /// subMode is an alert-type bitmask (low byte=ALERT_AGENT_*,
    /// high byte=ALERT_PATH_* shifted by ALERT_PATH_MASK_SHIFT). Unspecified shows all types.
    /// </summary>
    private void ApplyAlertMode(string mode, string subMode = null)
    {
        if (AlertManager.Instance != null)
        {
            int mask = AlertManager.DefaultFilterMask;
            if (!string.IsNullOrEmpty(subMode))
            {
                int.TryParse(subMode, out mask);
            }
            AlertManager.Instance.SetFilterMask(mask);
        }
    }

}
