
using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;
using UnityEngine.Networking;
using Newtonsoft.Json;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// ノードオブジェクト用のInputHandler
/// </summary>
public class NodeInputHandler : MonoBehaviour, IInputHandler
{
    public int nodeId;

    public int ObjectTypeId => InputObjectType.NODE;
    
    private Renderer objectRenderer;
    private Material originalMaterial;
    private Material highlightMaterial;
    
    // RoadPlanManagerへの参照
    private RoadPlanManager roadPlanManager;

    // ドラッグ関連の変数
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
        
        // RoadPlanManagerへの参照を取得
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
            // ノード上の右クリックは地形右クリックとして転送
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
            
            // 関連するパスIDを取得（ノード名から推測）
            ExtractRelatedPathId();
        }
    }

    public void onDragUpdate(Vector3 worldPosition)
    {
        if (isDragging)
        {
            // ノードの位置を更新
            Vector3 newPosition = new Vector3(worldPosition.x, transform.position.y, worldPosition.z);
            transform.position = newPosition;
            
            // パスの再描画をトリガー（必要に応じて）
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
            
            // 位置が変更された場合、APIで保存
            if (Vector3.Distance(originalPosition, finalPosition) > 0.01f)
            {
                StartCoroutine(SaveNodePosition(finalPosition));
            }
        }
    }

    public void onHoverStart(int mode)
    {
        // ハイライト表示
        if (objectRenderer != null && highlightMaterial != null)
        {
            objectRenderer.material = highlightMaterial;
        }
    }

    public void onHoverEnd(int mode)
    {
        // ハイライト解除
        if (objectRenderer != null && originalMaterial != null)
        {
            objectRenderer.material = originalMaterial;
        }
    }

    public void onHover(int mode)
    {
        // ノードでは継続ホバー処理なし
    }

    private void HandleNormalClick()
    {
        Debug.Log($"NodeInputHandler: ノード {nodeId} がクリックされました");
        
        // ノード情報の表示など
        // TODO: ノード情報表示の実装
    }

    private void HandleEditClick()
    {
        Debug.Log($"NodeInputHandler: ノード {nodeId} の編集モードクリックが実行されました");
        
        // 編集モードでのクリックは主にドラッグ開始のトリガーとして使用
        // 実際のドラッグ処理はInputHandlerから呼び出される
    }

    private void HandleConstructClick()
    {
        // ノード上のクリックは地形クリックとしてRoadPlanManagerに転送
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
        
        // 解体確認ダイアログの表示など
        // TODO: 解体処理の実装
    }

    /// <summary>
    /// ノードが施設の接続端子ノード（インフラ接続点 infra_node_id / 走行網接続点 depot_node_id /
    /// 共用接続ノード）かどうかを判定する。端子ノードは所属パスではなくノード自身を
    /// PATCH /nodes/{id} で移動する（パス未接続時のみ許可）。
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
            // 接続端子ノード（インフラ / 走行網 / 共用）の場合は、ノード自身を更新する (PATCH /api/v1/nodes/{nodeId})
            string patchUrl = $"{ApiClient.BASE_URL}/nodes/{nodeId}";

            // ノードの position はバックエンド座標系 [x, 地面からの相対高さ, z] で保持される
            // （相対高さの考え方はルートAGENTS.md参照。フロントの NodeData.GetAbsolutePosition も
            //  position[1] を地形高さに足して絶対座標へ戻す）。
            // ドラッグ中は transform.position が絶対ワールドYを保っているため、そのままではなく
            // 地形高さを引いて相対高さへ変換してから送信・キャッシュ更新する。絶対Yのまま格納すると
            // GetAbsolutePosition で地形高さが二重加算され、ドロップ直後にマーカーが跳ね上がってしまう。
            float groundHeight = Utils.GetGroundHeight(newPosition);
            float relativeHeight = newPosition.y - groundHeight;
            Vector3 backendPosition = new Vector3(newPosition.x, relativeHeight, newPosition.z);

            var updateData = new {
                position = new float[] { backendPosition.x, backendPosition.y, backendPosition.z }
            };

            string patchJson = JsonConvert.SerializeObject(updateData);
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(patchJson);

            using (UnityWebRequest patchRequest = new UnityWebRequest(patchUrl, "PATCH"))
            {
                patchRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                patchRequest.downloadHandler = new DownloadHandlerBuffer();
                patchRequest.SetRequestHeader("Content-Type", "application/json");

                yield return patchRequest.SendWebRequest();

                if (patchRequest.result == UnityWebRequest.Result.Success)
                {
                    Debug.Log($"NodeInputHandler: インフラ端子ノード {nodeId} の位置を保存しました: {backendPosition}");

                    // キャッシュも相対高さで更新する（GetAbsolutePosition が再度地形高さを足して
                    // 絶対座標へ戻すため、ここで絶対Yを渡すと二重加算になる）。
                    var pathManager = FindAnyObjectByType<PathManager>();
                    if (pathManager != null)
                    {
                        pathManager.UpdateCachedNodePosition(nodeId, backendPosition);
                    }

                    // 車両基地（MODEL_RAIL_YARD）は端子位置へ線路を収束させて描画するため、
                    // 端子の移動が確定したら所属施設のメッシュを新しい位置で再生成する
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

        // まず現在のパス情報を取得
        string getUrl = $"{ApiClient.BASE_URL}/paths/{relatedPathId}";
        
        using (UnityWebRequest getRequest = UnityWebRequest.Get(getUrl))
        {
            yield return getRequest.SendWebRequest();

            if (getRequest.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"パス情報の取得に失敗: {getRequest.error}");
                yield break;
            }

            // パス情報をパース（single_pathスキーマは直接PathDataとして返される）
            string json = getRequest.downloadHandler.text;
            var pathData = JsonConvert.DeserializeObject<GeneratedModels.PathData>(json);

            if (pathData == null)
            {
                Debug.LogError("パスデータのパースに失敗しました");
                yield break;
            }

            // pendingステータスの場合のみ編集可能
            if (pathData.status != Constants.STATUS_TEMPORARY)
            {
                Debug.LogWarning($"パス {relatedPathId} は編集できません。ステータス: {pathData.status}");
                
                // 元の位置に戻す
                transform.position = originalPosition;
                yield break;
            }

            bool isStartNode = idInfo != null && idInfo.role == "start";
            bool isEndNode = idInfo != null && idInfo.role == "end";

            if (isStartNode)
            {
                // 地形からの相対高度を計算
                float groundHeight = Utils.GetGroundHeight(newPosition);
                float relativeHeight = newPosition.y - groundHeight;
                pathData.start_point = new List<object> { newPosition.x, relativeHeight, newPosition.z };
            }
            else if (isEndNode)
            {
                // 地形からの相対高度を計算
                float groundHeight = Utils.GetGroundHeight(newPosition);
                float relativeHeight = newPosition.y - groundHeight;
                pathData.end_point = new List<object> { newPosition.x, relativeHeight, newPosition.z };
            }
            else
            {
                // 中間ノードの場合、waypointsを更新
                // TODO: 中間ノードの処理実装
                Debug.LogWarning("中間ノードの編集は未実装です");
                yield break;
            }

            // パス情報を更新
            string putUrl = $"{ApiClient.BASE_URL}/paths/{relatedPathId}";
            var updateData = new {
                start_point = pathData.start_point,
                end_point = pathData.end_point,
                waypoints = pathData.waypoints
            };
            
            string putJson = JsonConvert.SerializeObject(updateData);
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(putJson);

            UnityWebRequest putRequest = UnityWebRequest.Put(putUrl, bodyRaw);
            putRequest.SetRequestHeader("Content-Type", "application/json");
            yield return putRequest.SendWebRequest();

            if (putRequest.result == UnityWebRequest.Result.Success)
            {
                Debug.Log($"ノード {nodeId} の位置を保存しました: {newPosition}");
                RequestPathRedraw();
            }
            else
            {
                Debug.LogError($"ノード位置の保存に失敗: {putRequest.error}");
                
                // 失敗した場合は元の位置に戻す
                transform.position = originalPosition;
            }
        }
    }

    private void RequestPathRedraw()
    {
        // TODO: パス描画マネージャーがある場合は、そこに再描画をリクエスト
        Debug.Log($"NodeInputHandler: パス {relatedPathId} の再描画をリクエストしました");
    }
} 
