
using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;
using UnityEngine.Networking;
using Newtonsoft.Json;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// InputHandler for node objects
/// </summary>
public class NodeInputHandler : MonoBehaviour, IInputHandler
{
    public int nodeId;

    public int ObjectTypeId => InputObjectType.NODE;
    
    private Renderer objectRenderer;
    private Material originalMaterial;
    private Material highlightMaterial;
    
    // RoadPlanManager reference
    private RoadPlanManager roadPlanManager;

    // Drag variables
    private bool isDragging = false;
    private Vector3 dragStartPosition;
    private Vector3 originalPosition;
    private int relatedPathId = -1;

    private void Start()
    {
        objectRenderer = GetComponent<Renderer>();
        if (objectRenderer != null)
        {
            originalMaterial = objectRenderer.material;
            highlightMaterial = Resources.Load<Material>("HighlightMaterial");
        }
        
        // Get the RoadPlanManager reference.
        roadPlanManager = FindAnyObjectByType<RoadPlanManager>();
    }

    public void onClick(int mode)
    {
        switch (mode)
        {
            case InputMode.MODE_NORMAL:
                HandleNormalClick();
                break;
            case InputMode.MODE_BUILDING_CONSTRUCTION:
                break;
            case InputMode.MODE_ROAD_CONSTRUCTION:
                HandleConstructClick();
                break;
            case InputMode.MODE_EDIT:
                HandleEditClick();
                break;
            case InputMode.MODE_NONE:
                HandleDemolishClick();
                break;
            default:
                HandleNormalClick();
                break;
        }
    }

    public void onRightClick(int mode)
    {
        if (mode == InputMode.MODE_ROAD_CONSTRUCTION && roadPlanManager != null)
        {
            // Forward node right clicks as terrain right clicks.
            Vector3 terrainClickPosition = new Vector3(InputHandler.Instance.posX, 0, InputHandler.Instance.posZ);
            terrainClickPosition.y = Utils.GetGroundHeight(new Vector3(terrainClickPosition.x, 1000f, terrainClickPosition.z));
            roadPlanManager.OnTerrainRightClick(terrainClickPosition);
        }
    }

    public void onDragStart(int mode)
    {
        if (mode == InputMode.MODE_EDIT)
        {
            isDragging = true;
            dragStartPosition = InputUtil.MousePosition;
            originalPosition = transform.position;
            
            Debug.Log($"NodeInputHandler: ノード {nodeId} のドラッグを開始しました");
            
            // Get the related path ID (inferred from the node name).
            ExtractRelatedPathId();
        }
    }

    public void onDragUpdate(Vector3 worldPosition)
    {
        if (isDragging)
        {
            // Update the node position.
            Vector3 newPosition = new Vector3(worldPosition.x, transform.position.y, worldPosition.z);
            transform.position = newPosition;
            
            // Trigger path redraw as needed.
            RequestPathRedraw();
        }
    }

    public void onDragEnd(Vector3 worldPosition)
    {
        if (isDragging)
        {
            isDragging = false;
            
            Vector3 finalPosition = new Vector3(worldPosition.x, transform.position.y, worldPosition.z);
            transform.position = finalPosition;
            
            Debug.Log($"NodeInputHandler: ノード {nodeId} のドラッグを終了しました。新しい位置: {finalPosition}");
            
            // Save through the API if the position changed.
            if (Vector3.Distance(originalPosition, finalPosition) > 0.01f)
            {
                StartCoroutine(SaveNodePosition(finalPosition));
            }
        }
    }

    public void onHoverStart(int mode)
    {
        // Highlight.
        if (objectRenderer != null && highlightMaterial != null)
        {
            objectRenderer.material = highlightMaterial;
        }
    }

    public void onHoverEnd(int mode)
    {
        // Clear highlighting.
        if (objectRenderer != null && originalMaterial != null)
        {
            objectRenderer.material = originalMaterial;
        }
    }

    public void onHover(int mode)
    {
        // Nodes have no continuous hover processing.
    }

    private void HandleNormalClick()
    {
        Debug.Log($"NodeInputHandler: ノード {nodeId} がクリックされました");
        
        // Show node information, etc.
        // TODO: Implement node information display.
    }

    private void HandleEditClick()
    {
        Debug.Log($"NodeInputHandler: ノード {nodeId} の編集モードクリックが実行されました");
        
        // Edit-mode clicks primarily trigger drag starts.
        // Actual dragging is invoked by InputHandler.
    }

    private void HandleConstructClick()
    {
        // Forward node clicks to RoadPlanManager as terrain clicks.
        if (roadPlanManager != null)
        {
            Vector3 terrainClickPosition = new Vector3(InputHandler.Instance.posX, 0, InputHandler.Instance.posZ);
            terrainClickPosition.y = Utils.GetGroundHeight(new Vector3(terrainClickPosition.x, 1000f, terrainClickPosition.z));
            roadPlanManager.OnTerrainLeftClick(terrainClickPosition);
        }
        else
        {
            Debug.Log($"ノード {nodeId} がクリックされました（建設モード）");
        }
    }

    private void HandleDemolishClick()
    {
        Debug.Log($"NodeInputHandler: ノード {nodeId} の解体が要求されました");
        
        // Show a demolition confirmation dialog, etc.
        // TODO: Implement demolition.
    }

    /// <summary>
    /// Tests whether the node is a facility terminal (infrastructure infra_node_id / vehicle network depot_node_id /
    /// shared connection). Terminals move the node itself through PATCH /nodes/{id},
    /// rather than its owning path (allowed only without connected paths).
    /// </summary>
    public static bool IsTerminalRole(string role)
    {
        return role == "infra_terminal"
            || role == "depot_terminal"
            || role == "connector_terminal";
    }

    private void ExtractRelatedPathId()
    {
        var idInfo = GetComponent<ObjectIdInfo>();
        if (idInfo != null)
        {
            if (IsTerminalRole(idInfo.role))
            {
                relatedPathId = -1;
                Debug.Log($"NodeInputHandler: ノード {nodeId} は接続端子ノード（role={idInfo.role}）です。");
            }
            else
            {
                relatedPathId = idInfo.id;
                Debug.Log($"NodeInputHandler: ノード {nodeId} はパス {relatedPathId} に関連しています");
            }
        }
    }

    private IEnumerator SaveNodePosition(Vector3 newPosition)
    {
        var idInfo = GetComponent<ObjectIdInfo>();
        if (idInfo != null && IsTerminalRole(idInfo.role))
        {
            // For infrastructure/vehicle/shared terminals, update the node itself (PATCH /api/v1/nodes/{nodeId}).

            // Backend node positions use [x, relative height above ground, z]
            // (see root AGENTS.md for relative heights; frontend NodeData.GetAbsolutePosition also
            // adds position[1] to terrain height to restore absolute coordinates).
            // During dragging, transform.position holds absolute world Y; subtract terrain height
            // before sending/updating the cache. Storing absolute Y would make
            // GetAbsolutePosition add terrain height twice, causing the marker to jump on drop.
            float groundHeight = Utils.GetGroundHeight(newPosition);
            float relativeHeight = newPosition.y - groundHeight;
            Vector3 backendPosition = new Vector3(newPosition.x, relativeHeight, newPosition.z);

            var updateData = new {
                position = new float[] { backendPosition.x, backendPosition.y, backendPosition.z }
            };

            string patchJson = JsonConvert.SerializeObject(updateData);

            using (ApiRequest patchCall = ApiClient.Request("PATCH", $"/nodes/{nodeId}", patchJson))
            {
                yield return patchCall.Send();
                UnityWebRequest patchRequest = patchCall.Web;

                if (patchRequest.result == UnityWebRequest.Result.Success)
                {
                    Debug.Log($"NodeInputHandler: インフラ端子ノード {nodeId} の位置を保存しました: {backendPosition}");

                    // Update cached relative height as well (GetAbsolutePosition adds terrain height again
                    // to restore absolute coordinates; passing absolute Y here would double it).
                    var pathManager = FindAnyObjectByType<PathManager>();
                    if (pathManager != null)
                    {
                        pathManager.UpdateCachedNodePosition(nodeId, backendPosition);
                    }

                    // Depots (MODEL_RAIL_YARD) draw tracks converging on the terminal,
                    // so regenerate the owning facility mesh at the new position after committing terminal movement.
                    BuildingManager.Instance?.OnTerminalNodeMoved(nodeId);
                }
                else
                {
                    Debug.LogError($"NodeInputHandler: インフラ端子ノード位置の保存に失敗: {patchRequest.error}");
                    transform.position = originalPosition;
                }
            }
            yield break;
        }

        if (relatedPathId <= 0)
        {
            Debug.LogWarning($"NodeInputHandler: ノード {nodeId} の関連パスIDが不明です");
            yield break;
        }

        // First get current path data.
        using (ApiRequest getCall = ApiClient.Request("GET", $"/paths/{relatedPathId}"))
        {
            yield return getCall.Send();
            UnityWebRequest getRequest = getCall.Web;

            if (getRequest.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"パス情報の取得に失敗: {getRequest.error}");
                yield break;
            }

            // Parse path data (single_path schema returns PathData directly).
            string json = getRequest.downloadHandler.text;
            var pathData = JsonConvert.DeserializeObject<GeneratedModels.PathData>(json);

            if (pathData == null)
            {
                Debug.LogError("パスデータのパースに失敗しました");
                yield break;
            }

            // Editable only in pending status.
            if (pathData.status != Constants.STATUS_TEMPORARY)
            {
                Debug.LogWarning($"パス {relatedPathId} は編集できません。ステータス: {pathData.status}");
                
                // Restore the original position.
                transform.position = originalPosition;
                yield break;
            }

            bool isStartNode = idInfo != null && idInfo.role == "start";
            bool isEndNode = idInfo != null && idInfo.role == "end";

            if (isStartNode)
            {
                // Calculate height relative to terrain.
                float groundHeight = Utils.GetGroundHeight(newPosition);
                float relativeHeight = newPosition.y - groundHeight;
                pathData.start_point = new List<object> { newPosition.x, relativeHeight, newPosition.z };
            }
            else if (isEndNode)
            {
                // Calculate height relative to terrain.
                float groundHeight = Utils.GetGroundHeight(newPosition);
                float relativeHeight = newPosition.y - groundHeight;
                pathData.end_point = new List<object> { newPosition.x, relativeHeight, newPosition.z };
            }
            else
            {
                // Update waypoints for intermediate nodes.
                // TODO: Implement intermediate node handling.
                Debug.LogWarning("中間ノードの編集は未実装です");
                yield break;
            }

            // Update path data.
            var updateData = new {
                start_point = pathData.start_point,
                end_point = pathData.end_point,
                waypoints = pathData.waypoints
            };
            
            string putJson = JsonConvert.SerializeObject(updateData);

            using (ApiRequest putCall = ApiClient.Request("PUT", $"/paths/{relatedPathId}", putJson))
            {
                yield return putCall.Send();
                UnityWebRequest putRequest = putCall.Web;

                if (putRequest.result == UnityWebRequest.Result.Success)
                {
                    Debug.Log($"ノード {nodeId} の位置を保存しました: {newPosition}");
                    RequestPathRedraw();
                }
                else
                {
                    Debug.LogError($"ノード位置の保存に失敗: {putRequest.error}");

                    // Restore the original position on failure.
                    transform.position = originalPosition;
                }
            }
        }
    }

    private void RequestPathRedraw()
    {
        // TODO: Request redraw from the path rendering manager if available.
        Debug.Log($"NodeInputHandler: パス {relatedPathId} の再描画をリクエストしました");
    }
} 
