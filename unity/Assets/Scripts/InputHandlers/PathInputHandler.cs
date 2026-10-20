
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Networking;
using Newtonsoft.Json;
using GeneratedModels;

/// <summary>
/// InputHandler for path (road) objects
/// </summary>
public class PathInputHandler : MonoBehaviour, IInputHandler
{
    public int pathId;

    public int ObjectTypeId => InputObjectType.PATH;

    private VisualModeObject visualModeObject;

    // RoadPlanManager reference
    private RoadPlanManager roadPlanManager;

    // PathManager reference
    private PathManager pathManager;

    private void Start()
    {
        // Get the VisualModeObject component.
        visualModeObject = GetComponent<VisualModeObject>();

        // Get manager references.
        roadPlanManager = FindAnyObjectByType<RoadPlanManager>();
        pathManager = FindAnyObjectByType<PathManager>();
    }

    public void onClick(int mode)
    {
        // Special handling for parcel editor road selection mode.
        if (IsParcelEditorPathSelectionMode())
        {
            HandleParcelEditorPathSelection();
            return;
        }

        switch (mode)
        {
            case InputMode.MODE_NORMAL:
                HandleNormalClick();
                break;
            case InputMode.MODE_PARCEL_CREATION_PATH:
                HandleParcelCreationClick();
                break;
            case InputMode.MODE_BUILDING_CONSTRUCTION:
                HandleBuildingConstructionClick();
                break;
            case InputMode.MODE_ROAD_CONSTRUCTION:
                HandleRoadConstructionClick();
                break;
            case InputMode.MODE_REMOVE:
                RemovalManager.Instance?.TryRemove(InputObjectType.PATH, pathId);
                break;
            case InputMode.MODE_PATH_MERGE:
                PathTopologyEditManager.Instance?.OnPathClickedForMerge(pathId);
                break;
            case InputMode.MODE_PATH_SPLIT:
                // Split points come from projecting the click onto the target centerline,
                // so use the manager's projection regardless of which path was clicked.
                PathTopologyEditManager.Instance?.OnClickForSplit();
                break;
            case InputMode.MODE_EDIT_ROAD_MEMBERS:
                RoadMemberEditManager.Instance?.OnPathClicked(pathId);
                break;
            case InputMode.MODE_NONE:
                // Do nothing.
                break;
            default:
                HandleNormalClick();
                break;
        }
    }

    public void onHoverStart(int mode)
    {
        // In bulldoze mode, highlight only removable (planned) roads.
        if (mode == InputMode.MODE_REMOVE)
        {
            if (RemovalManager.Instance != null
                && RemovalManager.Instance.IsRemovable(InputObjectType.PATH, pathId)
                && visualModeObject != null)
            {
                visualModeObject.SetHover(true);
            }
            return;
        }

        // In merge partner selection mode, highlight only merge candidates.
        if (mode == InputMode.MODE_PATH_MERGE)
        {
            if (PathTopologyEditManager.Instance != null
                && PathTopologyEditManager.Instance.IsMergeCandidate(pathId)
                && visualModeObject != null)
            {
                visualModeObject.SetHover(true);
                TooltipManager.Instance?.Show("クリックでこのパスと結合");
            }
            return;
        }

        // In road section editing, highlight only eligible sections (streets/highways).
        if (mode == InputMode.MODE_EDIT_ROAD_MEMBERS)
        {
            if (RoadMemberEditManager.Instance != null
                && RoadMemberEditManager.Instance.OnPathHoverStart(pathId)
                && visualModeObject != null)
            {
                visualModeObject.SetHover(true);
            }
            return;
        }

        // In split-point selection, markers provide guidance; do not apply hover color.
        if (mode == InputMode.MODE_PATH_SPLIT)
        {
            return;
        }

        // Skip highlighting during road construction to avoid confusion with existing paths.
        if (InputHandler.Instance != null && InputHandler.Instance.GetMode(InputObjectType.PATH) == InputMode.MODE_ROAD_CONSTRUCTION)
        {
            return;
        }

        // Special highlight for parcel editor road selection mode.
        if (IsParcelEditorPathSelectionMode())
        {
            // Roadside parcel creation allows only active ordinary roads.
            if (!IsPathSelectableForParcelCreation())
            {
                return;
            }

            // Highlight for road selection mode.
            if (visualModeObject != null)
            {
                visualModeObject.SetHover(true);
            }
            return;
        }

        // Highlight.
        if (visualModeObject != null)
        {
            visualModeObject.SetHover(true);
        }
    }

    public void onHoverEnd(int mode)
    {
        if (visualModeObject != null)
        {
            visualModeObject.SetHover(false);
        }

        if (mode == InputMode.MODE_PATH_MERGE)
        {
            TooltipManager.Instance?.Hide();
        }
        else if (mode == InputMode.MODE_EDIT_ROAD_MEMBERS)
        {
            RoadMemberEditManager.Instance?.OnPathHoverEnd(pathId);
        }
    }

    public void onRightClick(int mode)
    {
        Debug.Log($"PathInputHandler: パス右クリック時の処理: {mode}");

        // Right-click handling in construction mode
        if (mode == InputMode.MODE_ROAD_CONSTRUCTION && roadPlanManager != null)
        {
            // In new construction (not editing), handle as a terrain right click.
            if (!roadPlanManager.IsEditMode)
            {
                Vector3 terrainClickPosition = new Vector3(InputHandler.Instance.posX, 0, InputHandler.Instance.posZ);
                terrainClickPosition.y = Utils.GetGroundHeight(new Vector3(terrainClickPosition.x, 1000f, terrainClickPosition.z));

                Debug.Log($"道路敷設モード: パス上の右クリックを地形右クリックとして転送 {terrainClickPosition}");
                roadPlanManager.OnTerrainRightClick(terrainClickPosition);
                return;
            }

            // In edit mode, use normal handling.
            Debug.Log($"道路編集モードでパス {pathId} が右クリックされました");
        }
    }

    public void onHover(int mode)
    {
        // Paths have no continuous hover processing.
    }

    private void HandleNormalClick()
    {
        // Open path_detail on path clicks.
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitSelectObject("path_detail", "path", pathId.ToString());
        }
    }

    private void HandleParcelCreationClick()
    {
        Debug.Log($"パス {pathId} が土地区画作成モードでクリックされました");

        // Roadside parcel creation allows only active ordinary roads.
        if (!IsPathSelectableForParcelCreation())
        {
            Debug.Log($"パス {pathId} は選択できません（status={GetPathStatus()}、type={GetPathType()}）");
            return;
        }

        // Notify ParcelEditorManager of road selection (OnPathSelected also sends
        // `edit_parcel:select_path` to the UI, including the click projected onto the centerline as an anchor).
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager != null)
        {
            Vector3[] waypoints = GetPathWaypoints(pathId);
            parcelEditorManager.OnPathSelected(pathId, waypoints, GetCursorWorldPosition());
        }
    }

    /// <summary>
    /// Returns cursor world XZ, the reference for where the user clicked during parcel creation.
    /// </summary>
    private Vector3 GetCursorWorldPosition()
    {
        if (InputHandler.Instance == null) return Vector3.zero;
        return new Vector3(InputHandler.Instance.posX, 0f, InputHandler.Instance.posZ);
    }

    private void HandleBuildingConstructionClick()
    {
        Debug.Log($"パス {pathId} がクリックされました（建物建設モード）");
        // No special handling in building construction mode.
    }

    private void HandleRoadConstructionClick()
    {
        // Forward path clicks to RoadPlanManager as terrain clicks.
        if (roadPlanManager != null)
        {
            Vector3 terrainClickPosition = new Vector3(InputHandler.Instance.posX, 0, InputHandler.Instance.posZ);
            terrainClickPosition.y = Utils.GetGroundHeight(new Vector3(terrainClickPosition.x, 1000f, terrainClickPosition.z));
            roadPlanManager.OnTerrainLeftClick(terrainClickPosition);
        }
    }

    private void HandleParcelEditorPathSelection()
    {
        Debug.Log($"パーセルエディターで道路 {pathId} が選択されました");

        // Roadside parcel creation allows only active ordinary roads.
        if (!IsPathSelectableForParcelCreation())
        {
            Debug.Log($"パス {pathId} は選択できません（status={GetPathStatus()}、type={GetPathType()}）");
            return;
        }

        // Get waypoints (e.g. from the API in the actual implementation).
        Vector3[] waypoints = GetPathWaypoints(pathId);

        // Notify ParcelEditorManager of road selection.
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager != null)
        {
            parcelEditorManager.OnPathSelected(pathId, waypoints, GetCursorWorldPosition());
        }

    }

    /// <summary>
    /// Tests whether this path is selectable for roadside parcel creation.
    /// Only active streets supporting road access are selectable. Bridges are excluded even at ground level.
    /// </summary>
    private bool IsPathSelectableForParcelCreation()
    {
        PathSummary pathData = pathManager?.GetPathData(pathId);
        if (pathData == null)
        {
            return false;
        }
        var master = MasterDataStore.Instance?.GetPathMaster(pathData.path_variant);
        return pathData.status == Constants.STATUS_ACTIVE && pathData.path_type == Constants.PATH_STREET
            && master != null && master.frontage_allowed;
    }

    /// <summary>
    /// Gets this path's status, or -1 if data is absent.
    /// </summary>
    private int GetPathStatus()
    {
        PathSummary pathData = pathManager?.GetPathData(pathId);
        return pathData?.status ?? -1;
    }

    /// <summary>
    /// Gets this path's type, or -1 if data is absent.
    /// </summary>
    private int GetPathType()
    {
        PathSummary pathData = pathManager?.GetPathData(pathId);
        return pathData?.path_type ?? -1;
    }

    private bool IsParcelEditorPathSelectionMode()
    {
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        return parcelEditorManager != null &&
               parcelEditorManager.enabled &&
               parcelEditorManager.IsPathSelectionMode();
    }

    /// <summary>
    /// Get road waypoint data (temporary implementation).
    /// </summary>
    private Vector3[] GetPathWaypoints(int pathId)
    {
        // Fetch road data from the API.
        // Create simple placeholder waypoints here.
        return new Vector3[]
        {
            new Vector3(-10, 0, 0),
            new Vector3(0, 0, 0),
            new Vector3(10, 0, 0)
        };

        // In the actual implementation:
        // return FetchPathWaypoints(pathId);
    }
}

