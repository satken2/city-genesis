
using UnityEngine;
using System.Collections;
using UnityEngine.Networking;
using Newtonsoft.Json;
using System.Collections.Generic;

/// <summary>
/// InputHandler for building objects
/// </summary>
public class BuildingInputHandler : MonoBehaviour, IInputHandler
{
    public int buildingId;

    public int ObjectTypeId => InputObjectType.BUILDING;
    
    private VisualModeObject visualModeObject;

    private void Start()
    {
        // Get the VisualModeObject component.
        visualModeObject = GetComponent<VisualModeObject>();
    }

    public void onClick(int mode)
    {
        Debug.Log($"BuildingInputHandler: 建物クリック時の処理を選択モードに応じて実行する: {mode}");
        switch (mode)
        {
            case InputMode.MODE_NORMAL:
                HandleNormalClick();
                break;
            case InputMode.MODE_BUILDING_CONSTRUCTION:
                HandleBuildingConstructionClick();
                break;
            case InputMode.MODE_REMOVE:
                RemovalManager.Instance?.TryRemove(InputObjectType.BUILDING, buildingId);
                break;
            case InputMode.MODE_PICK_BUILDING:
                HandlePickBuildingClick();
                break;
            case InputMode.MODE_ROAD_CONSTRUCTION:
                // Building clicks are disabled in construction mode.
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
        // In bulldoze mode, highlight only removable (planned) buildings.
        if (mode == InputMode.MODE_REMOVE)
        {
            if (RemovalManager.Instance != null
                && RemovalManager.Instance.IsRemovable(InputObjectType.BUILDING, buildingId)
                && visualModeObject != null)
            {
                visualModeObject.SetHover(true);
            }
            return;
        }

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
    }

    public void onRightClick(int mode)
    {
        Debug.Log($"BuildingInputHandler: 建物右クリック時の処理: {mode}");
    }

    public void onHover(int mode)
    {
        // Buildings have no continuous hover processing.
    }

    private void HandleNormalClick()
    {
        // Open agent_detail when a building is clicked.
        // BuildingManager sets buildingId to agent_id, so use it directly as the entity ID.
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitSelectObject("agent_detail", "agent", buildingId.ToString());
        }
    }

    /// <summary>
    /// Selects the source for constructing a copy of an existing building. Reads variant / floors / visual_variant
    /// from the cached BuildingSummary and reports building_construction:copy_source to the UI.
    /// Does not send footprint dimensions; the backend derives them from the source agent_id.
    /// Landmarks are unique within a city and rejected as sources by the backend, so do not allow their selection.
    /// </summary>
    private void HandlePickBuildingClick()
    {
        if (IpcClient.Instance == null) return;

        var summary = BuildingManager.Instance != null
            ? BuildingManager.Instance.GetBuildingSummary(buildingId)
            : null;
        if (summary == null)
        {
            Debug.LogWarning($"BuildingInputHandler: コピー元建物 {buildingId} のサマリが取得できませんでした");
            return;
        }

        if (summary.landmark > 0)
        {
            Debug.LogWarning($"BuildingInputHandler: 建物 {buildingId} は観光名所のためコピー元にできません");
            return;
        }

        int floors = summary.floors ?? 1;
        IpcClient.Instance.EmitBuildingConstructionCopySource(
            buildingId,
            summary.variant,
            floors,
            summary.visual_variant,
            summary.visual_variant2,
            summary.visual_variant3
        );
        Debug.Log($"BuildingInputHandler: コピー元建物 {buildingId} を選択 (variant={summary.variant}, floors={floors})");
    }

    private void HandleBuildingConstructionClick()
    {
        Debug.Log($"建物 {buildingId} の建設が要求されました");
        
        // Show a demolition confirmation dialog, etc.
        // TODO: Implement demolition.
        
        // Temporary implementation: call the demolition API.
        StartCoroutine(DemolishBuilding());
    }

    private IEnumerator DemolishBuilding()
    {
        using (ApiRequest call = ApiClient.Request("POST", $"/buildings/{buildingId}/demolish"))
        {
            yield return call.Send();
            UnityWebRequest request = call.Web;

            if (request.result == UnityWebRequest.Result.Success)
            {
                Debug.Log($"建物 {buildingId} の解体が完了しました");
                
                // Delete the building object.
                if (BuildingManager.Instance != null)
                {
                    BuildingManager.Instance.ClearAllCells(); // Full reload (room for improvement).
                }
            }
            else
            {
                Debug.LogError($"建物解体に失敗しました: {request.error}");
            }
        }
    }
}
