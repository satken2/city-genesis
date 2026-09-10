
using UnityEngine;
using System.Collections;
using UnityEngine.Networking;
using Newtonsoft.Json;
using System.Collections.Generic;

/// <summary>
/// 建物オブジェクト用のInputHandler
/// </summary>
public class BuildingInputHandler : MonoBehaviour, IInputHandler
{
    public int buildingId;

    public int ObjectTypeId => InputObjectType.BUILDING;
    
    private VisualModeObject visualModeObject;

    private void Start()
    {
        // VisualModeObjectコンポーネントの取得
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
                // 建設モード時は建物クリックは無効
                break;
            case InputMode.MODE_NONE:
                // 何もしない
                break;
            default:
                HandleNormalClick();
                break;
        }
    }

    public void onHoverStart(int mode)
    {
        // ブルドーザモードでは削除可能(計画中)な建物のみハイライトする
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
        if (mode == InputMode.MODE_ZONE_EDIT)
        {
            var stats = GetComponent<VisualBuildingStats>();
            int zoneId = stats != null ? stats.zone_id : 0;
            ZoneEditContext.Instance?.SelectZone(zoneId);
        }
    }

    public void onHover(int mode)
    {
        // 建物では継続ホバー処理なし
    }

    private void HandleNormalClick()
    {
        // 建物クリック時にagent_detailウィンドウを開く
        // buildingIdにはBuildingManagerでagent_idが設定されているため、そのままエンティティIDとして使用する
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitSelectObject("agent_detail", "agent", buildingId.ToString());
        }
    }

    /// <summary>
    /// 既存建物コピー建設のコピー元選択。クリックされた建物の variant / floors / visual_variant を
    /// キャッシュ済みの BuildingSummary から読み取り、building_construction:copy_source として UI へ通知する。
    /// 実際のフットプリント寸法はバックエンドがコピー元 agent_id から導出するため送らない。
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

        int floors = summary.floors ?? 1;
        IpcClient.Instance.EmitBuildingConstructionCopySource(
            buildingId,
            summary.variant,
            floors,
            summary.structure,
            summary.visual_variant,
            summary.visual_variant2,
            summary.visual_variant3
        );
        Debug.Log($"BuildingInputHandler: コピー元建物 {buildingId} を選択 (variant={summary.variant}, floors={floors})");
    }

    private void HandleBuildingConstructionClick()
    {
        Debug.Log($"建物 {buildingId} の建設が要求されました");
        
        // 解体確認ダイアログの表示など
        // TODO: 解体処理の実装
        
        // 仮の実装：解体APIの呼び出し
        StartCoroutine(DemolishBuilding());
    }

    private IEnumerator DemolishBuilding()
    {
        string url = $"{ApiClient.BASE_URL}/buildings/{buildingId}/demolish";

        using (UnityWebRequest request = UnityWebRequest.PostWwwForm(url, ""))
        {
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                Debug.Log($"建物 {buildingId} の解体が完了しました");
                
                // 建物オブジェクトを削除
                if (BuildingManager.Instance != null)
                {
                    BuildingManager.Instance.ClearAllCells(); // 全体リロード（改善の余地あり）
                }
            }
            else
            {
                Debug.LogError($"建物解体に失敗しました: {request.error}");
            }
        }
    }
}
