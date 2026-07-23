
using UnityEngine;
using System;

/// <summary>
/// 土地区画オブジェクト用のInputHandler
/// マウスオーバー時のハイライト表示は ParcelManager の2Dオーバーレイ経由で行う。
/// 3D MeshRenderer の有効/無効を切り替える方式から、
/// UI Canvas 上にポリゴンを描画する方式に変更し、ドローコールを削減している。
/// </summary>
public class ParcelInputHandler : MonoBehaviour, IInputHandler
{
    public int parcelId;

    public int ObjectTypeId => InputObjectType.PARCEL;

    private Renderer objectRenderer;
    private Material originalMaterial;
    private Material highlightMaterial;

    private void Start()
    {
        objectRenderer = GetComponent<Renderer>();
        if (objectRenderer != null)
        {
            originalMaterial = objectRenderer.material;
            highlightMaterial = Resources.Load<Material>("HighlightMaterial");
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
        // ブルドーザモードでは削除可能(空き地)な区画のみハイライトする
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
        if (mode == InputMode.MODE_ZONE_EDIT)
        {
            int zoneId = 0;
            var parcelManager = FindAnyObjectByType<ParcelManager>();
            var zoneManager = FindAnyObjectByType<ZoneManager>();
            if (parcelManager != null && zoneManager != null)
            {
                var summary = parcelManager.GetParcelSummary(parcelId);
                if (summary != null)
                {
                    var center = summary.center_point;
                    if (center != null && center.Count >= 2)
                    {
                        Vector3 centerPos = new Vector3(
                            Convert.ToSingle(center[0]), 
                            summary.altitude, 
                            Convert.ToSingle(center[1])
                        );
                        zoneId = zoneManager.GetZoneIdAtPoint(centerPos);
                    }
                }
            }
            ZoneEditContext.Instance?.SelectZone(zoneId);
        }
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

        // 建物建設モードで土地区画がクリックされたら、そのままBuildingPlanManagerに通知する。
        // 所有権の検証は建設確定時にバックエンド側で行われる。
        var buildingPlanManager = FindAnyObjectByType<BuildingPlanManager>();
        if (buildingPlanManager != null)
        {
            buildingPlanManager.OnParcelClick(parcelId, Vector3.zero, null);
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
    /// ParcelManager の2Dオーバーレイでパーセルをハイライト表示する。
    /// 従来の3D MeshRenderer有効化ではなく、UI Canvas上のポリゴン描画で表現する。
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
    /// ParcelManager の2Dオーバーレイのハイライトを解除する。
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
