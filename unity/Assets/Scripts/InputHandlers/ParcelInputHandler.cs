
using UnityEngine;
using GeneratedModels;

/// <summary>
/// InputHandler for parcel objects.
/// Mouse-over highlighting uses ParcelManager's 2D overlay.
/// Drawing polygons on a UI Canvas rather than toggling
/// 3D MeshRenderers reduces draw calls.
/// </summary>
public class ParcelInputHandler : MonoBehaviour, IInputHandler
{
    public int parcelId;

    public int ObjectTypeId => InputObjectType.PARCEL;

    // Do not access Renderer.material: its getter clones a Material per renderer, not released when the parcel GameObject is destroyed.
    private void Start()
    {
        var objectRenderer = GetComponent<Renderer>();
        if (objectRenderer != null)
        {
            objectRenderer.enabled = false;
        }
    }

    public void onClick(int mode)
    {
        switch (mode)
        {
            case InputMode.MODE_NORMAL:
                HandleNormalClick();
                break;
            case InputMode.MODE_BUILDING_CONSTRUCTION:
                HandleBuildingConstructionClick();
                break;
            case InputMode.MODE_PARCEL_CREATION_SPLIT:
                HandleParcelEditorParcelSelection();
                break;
            case InputMode.MODE_REMOVE:
                RemovalManager.Instance?.TryRemove(InputObjectType.PARCEL, parcelId);
                break;
            case InputMode.MODE_NONE:
                break;
            default:
                HandleNormalClick();
                break;
        }
    }

    public void onHoverStart(int mode)
    {
        if (mode == InputMode.MODE_NORMAL || mode == InputMode.MODE_PARCEL_CREATION_SPLIT || mode == InputMode.MODE_BUILDING_CONSTRUCTION)
        {
            ShowParcelHighlight();
        }
        // In bulldoze mode, highlight only removable (empty) parcels.
        else if (mode == InputMode.MODE_REMOVE
            && RemovalManager.Instance != null
            && RemovalManager.Instance.IsRemovable(InputObjectType.PARCEL, parcelId))
        {
            ShowParcelHighlight();
        }
    }

    public void onHoverEnd(int mode)
    {
        HideParcelHighlight();
    }

    public void onRightClick(int mode)
    {
        Debug.Log($"ParcelInputHandler: 土地区画右クリック時の処理: {mode}");
    }

    public void onHover(int mode)
    {
    }

    private void HandleNormalClick()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitSelectObject("parcel_detail", "parcel", parcelId.ToString());
        }
    }

    private void HandleBuildingConstructionClick()
    {
        Debug.Log($"土地区画 {parcelId} での建設が要求されました");

        // Notify BuildingPlanManager when a parcel is clicked in building construction mode.
        // Pass actual parcel data for parcel_type location checks.
        // The backend performs ownership and other validation when construction is confirmed.
        var buildingPlanManager = FindAnyObjectByType<BuildingPlanManager>();
        if (buildingPlanManager != null)
        {
            var parcelManager = FindAnyObjectByType<ParcelManager>();
            ParcelSummary summary = parcelManager != null ? parcelManager.GetParcelSummary(parcelId) : null;
            buildingPlanManager.OnParcelClick(parcelId, Vector3.zero, summary);
        }
        else
        {
            Debug.LogWarning("BuildingPlanManagerが見つかりません。");
        }
    }

    private void HandleParcelEditorParcelSelection()
    {
        Debug.Log($"パーセルエディターで土地区画 {parcelId} が選択されました");

        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager != null)
        {
            parcelEditorManager.OnParcelSelected(parcelId);
        }
    }

    /// <summary>
    /// Highlights parcels using ParcelManager's 2D overlay.
    /// Draws polygons on a UI Canvas instead of enabling 3D MeshRenderers.
    /// </summary>
    private void ShowParcelHighlight()
    {
        var manager = FindAnyObjectByType<ParcelManager>();
        if (manager != null)
        {
            manager.SetHighlightedParcel(parcelId);
        }
    }

    /// <summary>
    /// Clears highlighting in ParcelManager's 2D overlay.
    /// </summary>
    private void HideParcelHighlight()
    {
        var manager = FindAnyObjectByType<ParcelManager>();
        if (manager != null)
        {
            manager.ClearHighlightedParcel();
        }
    }

}
