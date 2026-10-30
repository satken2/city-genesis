using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Routes received IpcClient events to Editor Manager methods.
/// </summary>
public class IpcEventHandler : MonoBehaviour
{
    private RoadPlanManager _roadPlanManager;
    private BuildingPlanManager _buildingPlanManager;
    private ResourceAreaEditManager _resourceAreaEditManager;
    private ParcelManager _parcelManager;
    private readonly HighlightManager _highlightManager = new HighlightManager();

    private void OnEnable()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnBuildingExploded += HandleBuildingExploded;
            IpcClient.Instance.OnEditPathChangeNode += HandleEditPathChangeNode;
            IpcClient.Instance.OnEditPathChangeEmbankment += HandleEditPathChangeEmbankment;
            IpcClient.Instance.OnEditPathFinish += HandleEditPathFinish;
            IpcClient.Instance.OnEditPathChangePlatformSide += HandleEditPathChangePlatformSide;
            IpcClient.Instance.OnEditPathChangeHouseEnds += HandleEditPathChangeHouseEnds;
            IpcClient.Instance.OnEditPathChangePassageMode += HandleEditPathChangePassageMode;
            IpcClient.Instance.OnEditPathSetFollowTerrain += HandleEditPathSetFollowTerrain;
            IpcClient.Instance.OnEditPathChangeLateralOffsets += HandleEditPathChangeLateralOffsets;
            IpcClient.Instance.OnEditPathChangeGridDivisions += HandleEditPathChangeGridDivisions;
            IpcClient.Instance.OnEditPathShapeCommand += HandleEditPathShapeCommand;
            IpcClient.Instance.OnEditPathQueryShape += HandleEditPathQueryShape;
            IpcClient.Instance.OnEditResourceFinish += HandleEditResourceFinish;
            IpcClient.Instance.OnEditTransitFinish += HandleEditTransitFinish;
            IpcClient.Instance.OnEditPropChangeSettings += HandleEditPropChangeSettings;
            IpcClient.Instance.OnEditPropFinish += HandleEditPropFinish;
            IpcClient.Instance.OnEditPropReset += HandleEditPropReset;
            IpcClient.Instance.OnRemoveObjectConfirmResult += HandleRemoveObjectConfirmResult;
            IpcClient.Instance.OnEditBuildingUpdateShape += HandleEditBuildingUpdateShape;
            IpcClient.Instance.OnEditBuildingPlacementCommand += HandleEditBuildingPlacementCommand;
            IpcClient.Instance.OnEditBuildingUpdateFloors += HandleEditBuildingUpdateFloors;
            IpcClient.Instance.OnEditBuildingUpdateVisualVariant += HandleEditBuildingUpdateVisualVariant;
            IpcClient.Instance.OnEditBuildingUpdateLandmark += HandleEditBuildingUpdateLandmark;
            IpcClient.Instance.OnEditBuildingUpdateEffectRange += HandleEditBuildingUpdateEffectRange;
            IpcClient.Instance.OnEditBuildingUpdateDirectParams += HandleEditBuildingUpdateDirectParams;
            IpcClient.Instance.OnParcelShowFrontPath += HandleParcelShowFrontPath;
            IpcClient.Instance.OnParcelHideFrontPath += HandleParcelHideFrontPath;
            IpcClient.Instance.OnWorldHighlightObjects += HandleWorldHighlightObjects;
            IpcClient.Instance.OnWorldClearHighlights += HandleWorldClearHighlights;
            IpcClient.Instance.OnWorldSetRoadFocus += HandleWorldSetRoadFocus;
            IpcClient.Instance.OnWorldClearRoadFocus += HandleWorldClearRoadFocus;
            IpcClient.Instance.OnWorldRefreshRoads += HandleWorldRefreshRoads;
            IpcClient.Instance.OnEditRoadSetMembers += HandleEditRoadSetMembers;
            IpcClient.Instance.OnCameraCaptureThumbnail += HandleCameraCaptureThumbnail;
        }
    }

    private void OnDisable()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnBuildingExploded -= HandleBuildingExploded;
            IpcClient.Instance.OnEditPathChangeNode -= HandleEditPathChangeNode;
            IpcClient.Instance.OnEditPathChangeEmbankment -= HandleEditPathChangeEmbankment;
            IpcClient.Instance.OnEditPathFinish -= HandleEditPathFinish;
            IpcClient.Instance.OnEditPathChangePlatformSide -= HandleEditPathChangePlatformSide;
            IpcClient.Instance.OnEditPathChangeHouseEnds -= HandleEditPathChangeHouseEnds;
            IpcClient.Instance.OnEditPathChangePassageMode -= HandleEditPathChangePassageMode;
            IpcClient.Instance.OnEditPathSetFollowTerrain -= HandleEditPathSetFollowTerrain;
            IpcClient.Instance.OnEditPathChangeLateralOffsets -= HandleEditPathChangeLateralOffsets;
            IpcClient.Instance.OnEditPathChangeGridDivisions -= HandleEditPathChangeGridDivisions;
            IpcClient.Instance.OnEditPathShapeCommand -= HandleEditPathShapeCommand;
            IpcClient.Instance.OnEditPathQueryShape -= HandleEditPathQueryShape;
            IpcClient.Instance.OnEditResourceFinish -= HandleEditResourceFinish;
            IpcClient.Instance.OnEditTransitFinish -= HandleEditTransitFinish;
            IpcClient.Instance.OnEditPropChangeSettings -= HandleEditPropChangeSettings;
            IpcClient.Instance.OnEditPropFinish -= HandleEditPropFinish;
            IpcClient.Instance.OnEditPropReset -= HandleEditPropReset;
            IpcClient.Instance.OnRemoveObjectConfirmResult -= HandleRemoveObjectConfirmResult;
            IpcClient.Instance.OnEditBuildingUpdateShape -= HandleEditBuildingUpdateShape;
            IpcClient.Instance.OnEditBuildingPlacementCommand -= HandleEditBuildingPlacementCommand;
            IpcClient.Instance.OnEditBuildingUpdateFloors -= HandleEditBuildingUpdateFloors;
            IpcClient.Instance.OnEditBuildingUpdateVisualVariant -= HandleEditBuildingUpdateVisualVariant;
            IpcClient.Instance.OnEditBuildingUpdateLandmark -= HandleEditBuildingUpdateLandmark;
            IpcClient.Instance.OnEditBuildingUpdateEffectRange -= HandleEditBuildingUpdateEffectRange;
            IpcClient.Instance.OnEditBuildingUpdateDirectParams -= HandleEditBuildingUpdateDirectParams;
            IpcClient.Instance.OnParcelShowFrontPath -= HandleParcelShowFrontPath;
            IpcClient.Instance.OnParcelHideFrontPath -= HandleParcelHideFrontPath;
            IpcClient.Instance.OnWorldHighlightObjects -= HandleWorldHighlightObjects;
            IpcClient.Instance.OnWorldClearHighlights -= HandleWorldClearHighlights;
            IpcClient.Instance.OnWorldSetRoadFocus -= HandleWorldSetRoadFocus;
            IpcClient.Instance.OnWorldClearRoadFocus -= HandleWorldClearRoadFocus;
            IpcClient.Instance.OnWorldRefreshRoads -= HandleWorldRefreshRoads;
            IpcClient.Instance.OnEditRoadSetMembers -= HandleEditRoadSetMembers;
            IpcClient.Instance.OnCameraCaptureThumbnail -= HandleCameraCaptureThumbnail;
        }
    }

    // Save image size (16:9). Keep small while sufficient for list cards and scenario introductions.
    private const int ThumbnailWidth = 480;
    private const int ThumbnailHeight = 270;

    /// <summary>
    /// camera:capture_thumbnail. Capture the 3D view before saving, send to core for the next save, and reply with camera:thumbnail_captured.
    /// The UI waits for a reply or timeout, so always reply even if capture/upload fails.
    /// </summary>
    private void HandleCameraCaptureThumbnail(string id)
    {
        StartCoroutine(CaptureThumbnail(id));
    }

    private IEnumerator CaptureThumbnail(string id)
    {
        byte[] png = null;
        Camera camera = CameraController.Instance != null ? CameraController.Instance.GetComponent<Camera>() : Camera.main;
        if (camera != null)
        {
            try
            {
                png = CameraCaptureUtil.CapturePng(camera, ThumbnailWidth, ThumbnailHeight);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"IpcEventHandler: 保存用の画像を撮れませんでした: {e.Message}");
            }
        }
        bool success = false;
        if (png != null)
        {
            using (ApiRequest call = ApiClient.Request("PUT", "/sessions/thumbnail", png, "image/png", 10))
            {
                yield return call.Send();
                success = call.Web.result == UnityWebRequest.Result.Success;
                if (!success) Debug.LogWarning($"IpcEventHandler: 保存用の画像を送れませんでした: {call.Web.error}");
            }
        }
        IpcClient.Instance?.EmitCameraThumbnailCaptured(id, success);
    }

    private void HandleBuildingExploded(int agentId)
    {
        CellDataLoader.Instance?.FetchBuilding(agentId, playExplosion: true);
    }

    /// <summary>
    /// edit_path.change_node → RoadPlanManager.OnChangeNodeFromIpc()
    /// Apply panel elevation stepper changes to the selected node (or next placement value).
    /// </summary>
    private void HandleEditPathChangeNode(int nodeIndex, float height)
    {
        var rpm = GetRoadPlanManager();
        if (rpm != null)
        {
            rpm.OnChangeNodeFromIpc(nodeIndex, height);
        }
    }

    /// <summary>
    /// edit_path.change_embankment → RoadPlanManager.OnChangeEmbankmentFromIpc()
    /// Apply panel embankment stepper changes to the selected node (or next placement value).
    /// </summary>
    private void HandleEditPathChangeEmbankment(int nodeIndex, float embankmentHeight)
    {
        var rpm = GetRoadPlanManager();
        if (rpm != null)
        {
            rpm.OnChangeEmbankmentFromIpc(nodeIndex, embankmentHeight);
        }
    }

    /// <summary>
    /// edit_path.finish → RoadPlanManager.FinishRoadPlacement() + CellDataLoader.ReloadAllData()
    /// Called after Tauri directly submits to the backend; resets Unity editing state
    /// and fetches current backend data to update the 3D display.
    /// </summary>
    private void HandleEditPathFinish()
    {
        var rpm = GetRoadPlanManager();
        if (rpm != null)
        {
            rpm.FinishRoadPlacement();
        }
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ReloadAllData();
        }
    }

    /// <summary>
    /// edit_path.change_platform_side → RoadPlanManager.SetPlatformSide()
    /// In response to the UI panel toggle, immediately switch
    /// the side where platforms extend in station construction previews.
    /// </summary>
    private void HandleEditPathChangePassageMode(string mode)
    {
        FindAnyObjectByType<RoadPlanManager>()?.SetPassageMode(mode);
    }

    private void HandleEditPathChangeHouseEnds(string ends)
    {
        FindAnyObjectByType<RoadPlanManager>()?.SetHouseEnds(ends);
    }

    private void HandleEditPathChangePlatformSide(int platformSide)
    {
        var rpm = GetRoadPlanManager();
        if (rpm != null)
        {
            rpm.SetPlatformSide(platformSide);
        }
    }

    /// <summary>
    /// edit_path.set_follow_terrain → RoadPlanManager.SetFollowTerrain()
    /// Immediately switch construction preview terrain following/tunnel visualization
    /// from the UI toggle (-1=unset/0=off/1=on).
    /// </summary>
    private void HandleEditPathSetFollowTerrain(int value)
    {
        var rpm = GetRoadPlanManager();
        if (rpm != null)
        {
            rpm.SetFollowTerrain(value);
        }
    }

    /// <summary>
    /// edit_path.change_lateral_offsets → RoadPlanManager.SetLateralOffsets()
    /// Immediately update compound path (parallel track) preview count, spacing, and directions
    /// (platform sides/arrows) from UI track count/direction changes.
    /// Empty arrays return to a single path.
    /// </summary>
    private void HandleEditPathChangeLateralOffsets(float[] lateralOffsets, int[] travelDirections)
    {
        var rpm = GetRoadPlanManager();
        if (rpm != null)
        {
            rpm.SetLateralOffsets(lateralOffsets, travelDirections);
        }
    }

    /// <summary>
    /// edit_path.change_grid_divisions → RoadPlanManager.SetGridDivisions()
    /// Redraw the provisional placement grid when grid construction divisions change.
    /// </summary>
    private void HandleEditPathChangeGridDivisions(int[] gridDivisions)
    {
        var rpm = GetRoadPlanManager();
        if (rpm != null)
        {
            rpm.SetGridDivisions(gridDivisions);
        }
    }

    /// <summary>
    /// edit_path.shape_command → RoadPlanManager.ApplyShapeCommand()
    /// Node/control operations from the 2D path editor. Results return through update_shape.
    /// </summary>
    private void HandleEditPathShapeCommand(RoadPlanManager.ShapeCommand cmd)
    {
        var rpm = GetRoadPlanManager();
        if (rpm != null)
        {
            rpm.ApplyShapeCommand(cmd);
        }
    }

    /// <summary>
    /// edit_path.query_shape → RoadPlanManager.EmitShapeUpdateOnRequest()
    /// Query current shape immediately after opening the 2D path editor.
    /// </summary>
    private void HandleEditPathQueryShape()
    {
        var rpm = GetRoadPlanManager();
        if (rpm != null)
        {
            rpm.EmitShapeUpdateOnRequest();
        }
    }

    /// <summary>
    /// edit_resource.finish → ResourceAreaEditManager.FinishEditMode() + CellDataLoader.ReloadAllData()
    /// Called after Tauri directly sends POST /api/v1/resources to the backend;
    /// resets Unity editing state, fetches current data, and updates 3D display.
    /// </summary>
    private void HandleEditResourceFinish()
    {
        var ream = GetResourceAreaEditManager();
        if (ream != null)
        {
            ream.FinishEditMode();
        }
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ReloadAllData();
        }
    }

    /// <summary>
    /// edit_transit.finish → TransitPlanManager.FinishPlacement() + CellDataLoader.ReloadAllData()
    /// Called after Tauri directly sends POST /api/v1/transit/lines to the backend;
    /// resets Unity stop placement state, fetches current data, and updates 3D display
    /// (new public transport paths appear in the overlay).
    /// </summary>
    private void HandleEditTransitFinish()
    {
        var tpm = FindAnyObjectByType<TransitPlanManager>();
        if (tpm != null)
        {
            tpm.FinishPlacement();
        }
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ReloadAllData();
        }
    }

    /// <summary>
    /// edit_prop.change_settings → PropPlacementManager.OnChangeSettingsFromIpc()
    /// Immediately apply UI rotation/scale/density/seed changes to placement ghosts/scatter previews.
    /// Pass a dictionary containing only received keys (rotation? / scale? / density? / seed?).
    /// </summary>
    private void HandleEditPropChangeSettings(System.Collections.Generic.Dictionary<string, object> settings)
    {
        var ppm = FindAnyObjectByType<PropPlacementManager>();
        if (ppm != null)
        {
            ppm.OnChangeSettingsFromIpc(settings);
        }
    }

    /// <summary>
    /// edit_prop.finish → PropPlacementManager.FinishPlacement() + CellDataLoader.ReloadAllData()
    /// Called after Tauri directly sends POST /api/v1/props/... to the backend;
    /// resets Unity placement state (ghost/scatter previews), fetches current data, and updates 3D display.
    /// </summary>
    private void HandleEditPropFinish()
    {
        var ppm = FindAnyObjectByType<PropPlacementManager>();
        if (ppm != null)
        {
            ppm.FinishPlacement();
        }
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ReloadAllData();
        }
    }

    /// <summary>
    /// edit_prop.reset → PropPlacementManager.ResetPlacement()
    /// Clear all placed instances, vertices, and provisional finalization on UI reset.
    /// </summary>
    private void HandleEditPropReset()
    {
        var ppm = FindAnyObjectByType<PropPlacementManager>();
        if (ppm != null)
        {
            ppm.ResetPlacement();
        }
    }

    /// <summary>
    /// remove_object.confirm_result → RemovalManager.OnConfirmResult()
    /// Receives UI confirmation replies for bulldoze targets (scatter props).
    /// If accepted, RemovalManager issues DELETE and an incremental reload.
    /// </summary>
    private void HandleRemoveObjectConfirmResult(string objectType, int id, bool accepted)
    {
        RemovalManager.Instance?.OnConfirmResult(objectType, id, accepted);
    }

    /// <summary>
    /// edit_building.update_shape → BuildingPlanManager.OnUpdateShapeFromIpc()
    /// Pass geometry fetched by the UI from backend preview endpoints
    /// (polygon, floors, center_point, height, berth_line) unchanged to BuildingPlanManager.
    /// </summary>
    private void HandleEditBuildingPlacementCommand(Newtonsoft.Json.Linq.JToken command)
    {
        GetBuildingPlanManager()?.OnPlacementCommand(command);
    }

    private void HandleEditBuildingUpdateShape(float[][] polygon, int floors, float[] centerPoint, float height, int revision, float[] berthLine)
    {
        var bpm = GetBuildingPlanManager();
        if (bpm != null)
        {
            bpm.OnUpdateShapeFromIpc(polygon, floors, centerPoint, height, revision, berthLine);
        }
    }

    /// <summary>
    /// edit_building.update_floors → BuildingPlanManager.SetFloors()
    /// </summary>
    private void HandleEditBuildingUpdateFloors(int floors)
    {
        var bpm = GetBuildingPlanManager();
        if (bpm != null)
        {
            bpm.SetFloors(floors);
        }
    }

    /// <summary>
    /// edit_building.update_visual_variant → BuildingPlanManager.SetVisualVariant()
    /// Rebuild Unity 3D preview meshes when UI appearance seed sliders change.
    /// </summary>
    private void HandleEditBuildingUpdateVisualVariant(float vv1, float vv2, float vv3)
    {
        var bpm = GetBuildingPlanManager();
        if (bpm != null)
        {
            bpm.SetVisualVariant(vv1, vv2, vv3);
        }
    }

    /// <summary>
    /// edit_building.update_landmark → BuildingPlanManager.SetLandmark()
    /// Rebuild preview meshes with landmark-specific parts when scenario authoring landmark selection changes.
    /// </summary>
    private void HandleEditBuildingUpdateLandmark(int landmark)
    {
        var bpm = GetBuildingPlanManager();
        if (bpm != null)
        {
            bpm.SetLandmark(landmark);
        }
    }

    /// <summary>
    /// edit_building.update_effect_range → BuildingPlanManager.OnUpdateEffectRangeFromIpc()
    /// Overlay infrastructure coverage fetched by the UI in Unity 3D.
    /// </summary>
    private void HandleEditBuildingUpdateEffectRange(EffectRangePayload payload)
    {
        var bpm = GetBuildingPlanManager();
        if (bpm != null)
        {
            bpm.OnUpdateEffectRangeFromIpc(payload);
        }
    }

    /// <summary>
    /// edit_building.update_direct_params → BuildingPlanManager.SetDirectParams()
    /// Update Unity's real-time cursor-following rectangle ghost
    /// on UI direct placement parameter changes (width, depth, angle, floors).
    /// </summary>
    private void HandleEditBuildingUpdateDirectParams(float width, float depth, float angle, int floors)
    {
        var bpm = GetBuildingPlanManager();
        if (bpm != null)
        {
            bpm.SetDirectParams(width, depth, angle, floors);
        }
    }

    /// <summary>
    /// parcel:show_front_path → ParcelManager.ShowFrontPathIndicator()
    /// Show selected parcel road access as a blue 3D arrow when its detail panel opens.
    /// </summary>
    private void HandleParcelShowFrontPath(int parcelId)
    {
        var pm = GetParcelManager();
        if (pm != null)
        {
            pm.ShowFrontPathIndicator(parcelId);
        }
    }

    /// <summary>
    /// parcel:hide_front_path → ParcelManager.HideFrontPathIndicator()
    /// Clear the road access arrow when the parcel detail panel closes.
    /// </summary>
    private void HandleParcelHideFrontPath()
    {
        var pm = GetParcelManager();
        if (pm != null)
        {
            pm.HideFrontPathIndicator();
        }
    }

    /// <summary>
    /// world:highlight_objects → HighlightManager.Apply()
    /// Highlight parcels, buildings, and existing paths affected by planned paths in 3D.
    /// Sent on construction shape finalization/confirmation and pre-construction path detail display.
    /// </summary>
    private void HandleWorldHighlightObjects(string source, PathInterferencePayload interference)
    {
        _highlightManager.Apply(source, interference);
    }

    /// <summary>
    /// world:clear_highlights → HighlightManager.Clear()
    /// Clear interference highlights for the specified source on construction end/detail panel closure.
    /// </summary>
    private void HandleWorldClearHighlights(string source)
    {
        _highlightManager.Clear(source);
    }

    /// <summary>
    /// world.set_road_focus → RoadFocusOverlay.SetFocus()
    /// Emphasize sections of the road open in the detail panel with road-colored bands (separate from interference world:highlight_objects).
    /// </summary>
    private void HandleWorldSetRoadFocus(int roadId, string color, int[] pathIds)
    {
        RoadFocusOverlay.Ensure().SetFocus(roadId, RoadManager.ParseColor(color), pathIds);
    }

    /// <summary>
    /// world.clear_road_focus → RoadFocusOverlay.ClearFocus()。
    /// With road_id, clear only that road's emphasis (closing another detail panel must preserve the current road's highlight).
    /// </summary>
    private void HandleWorldClearRoadFocus(int roadId)
    {
        RoadFocusOverlay.Instance?.ClearFocus(roadId);
    }

    /// <summary>world.refresh_roads → RoadManager.RequestRefresh() (unchanged version does not rebuild labels).</summary>
    private void HandleWorldRefreshRoads()
    {
        RoadManager.Instance?.RequestRefresh();
    }

    /// <summary>edit_road.set_members → RoadMemberEditManager.SetMembers() (ignored outside section edit mode).</summary>
    private void HandleEditRoadSetMembers(int roadId, int[] pathIds, int[] previewPathIds)
    {
        RoadMemberEditManager.Instance?.SetMembers(roadId, pathIds, previewPathIds);
    }

    private RoadPlanManager GetRoadPlanManager()
    {
        if (_roadPlanManager == null)
        {
            _roadPlanManager = FindAnyObjectByType<RoadPlanManager>();
        }
        return _roadPlanManager;
    }

    private BuildingPlanManager GetBuildingPlanManager()
    {
        if (_buildingPlanManager == null)
        {
            _buildingPlanManager = FindAnyObjectByType<BuildingPlanManager>();
        }
        return _buildingPlanManager;
    }

    private ResourceAreaEditManager GetResourceAreaEditManager()
    {
        if (_resourceAreaEditManager == null)
        {
            _resourceAreaEditManager = FindAnyObjectByType<ResourceAreaEditManager>();
        }
        return _resourceAreaEditManager;
    }

    private ParcelManager GetParcelManager()
    {
        if (_parcelManager == null)
        {
            _parcelManager = FindAnyObjectByType<ParcelManager>();
        }
        return _parcelManager;
    }
}
