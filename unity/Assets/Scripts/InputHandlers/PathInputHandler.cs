
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Networking;
using Newtonsoft.Json;
using GeneratedModels;

/// <summary>
/// パス（道路）オブジェクト用のInputHandler
/// </summary>
public class PathInputHandler : MonoBehaviour, IInputHandler
{
    public int pathId;

    public int ObjectTypeId => InputObjectType.PATH;

    private VisualModeObject visualModeObject;

    // RoadPlanManagerへの参照
    private RoadPlanManager roadPlanManager;

    // PathManagerへの参照
    private PathManager pathManager;

    private void Start()
    {
        // VisualModeObjectコンポーネントの取得
        visualModeObject = GetComponent<VisualModeObject>();

        // マネージャーへの参照を取得
        roadPlanManager = FindAnyObjectByType<RoadPlanManager>();
        pathManager = FindAnyObjectByType<PathManager>();
    }

    public void onClick(int mode)
    {
        // パーセルエディターの道路選択モード中は特別処理
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
                // 分割点はクリック地点を対象パスの中心線へ射影して決めるため、
                // クリックされたパスがどれかに関わらずマネージャの射影結果を使う。
                PathTopologyEditManager.Instance?.OnClickForSplit();
                break;
            case InputMode.MODE_EDIT_ROAD_MEMBERS:
                RoadMemberEditManager.Instance?.OnPathClicked(pathId);
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
        // ブルドーザモードでは削除可能(計画中)な道路のみハイライトする
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

        // パス結合の相手選択モードでは、結合できる候補パスのみハイライトする
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

        // 道路の区間編集モードでは、道路に含められる区間(街路・高速道路)だけをハイライトする
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

        // パス分割の分割点選択モードでは、マーカー表示が案内を担うためホバー色は付けない
        if (mode == InputMode.MODE_PATH_SPLIT)
        {
            return;
        }

        // 道路建設モード中はハイライトをスキップ（既存パスとの混乱を避けるため）
        if (InputHandler.Instance != null && InputHandler.Instance.GetMode(InputObjectType.PATH) == InputMode.MODE_ROAD_CONSTRUCTION)
        {
            return;
        }

        // パーセルエディターの道路選択モード中は特別なハイライト
        if (IsParcelEditorPathSelectionMode())
        {
            // 道路沿い区画作成では、稼働中の一般道路のみ選択可能とする
            if (!IsPathSelectableForParcelCreation())
            {
                return;
            }

            // 道路選択モード用のハイライト
            if (visualModeObject != null)
            {
                visualModeObject.SetHover(true);
            }
            return;
        }

        // ハイライト表示
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

        // 建設モードでの右クリック処理
        if (mode == InputMode.MODE_ROAD_CONSTRUCTION && roadPlanManager != null)
        {
            // 新規敷設モード（編集モードでない）の場合は、地形右クリックとして処理
            if (!roadPlanManager.IsEditMode)
            {
                Vector3 terrainClickPosition = new Vector3(InputHandler.Instance.posX, 0, InputHandler.Instance.posZ);
                terrainClickPosition.y = Utils.GetGroundHeight(new Vector3(terrainClickPosition.x, 1000f, terrainClickPosition.z));

                Debug.Log($"道路敷設モード: パス上の右クリックを地形右クリックとして転送 {terrainClickPosition}");
                roadPlanManager.OnTerrainRightClick(terrainClickPosition);
                return;
            }

            // 編集モードの場合は従来通り
            Debug.Log($"道路編集モードでパス {pathId} が右クリックされました");
        }
    }

    public void onHover(int mode)
    {
        // パスでは継続ホバー処理なし
    }

    private void HandleNormalClick()
    {
        // パスクリック時にpath_detailウィンドウを開く
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitSelectObject("path_detail", "path", pathId.ToString());
        }
    }

    private void HandleParcelCreationClick()
    {
        Debug.Log($"パス {pathId} が土地区画作成モードでクリックされました");

        // 道路沿い区画作成では、稼働中の一般道路のみ選択可能とする
        if (!IsPathSelectableForParcelCreation())
        {
            Debug.Log($"パス {pathId} は選択できません（status={GetPathStatus()}、type={GetPathType()}）");
            return;
        }

        // ParcelEditorManagerに道路選択を通知(UIへの `edit_parcel:select_path` 送信も
        // OnPathSelected が担う。クリック地点を中心線へ射影したアンカーを載せるため)
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager != null)
        {
            Vector3[] waypoints = GetPathWaypoints(pathId);
            parcelEditorManager.OnPathSelected(pathId, waypoints, GetCursorWorldPosition());
        }
    }

    /// <summary>
    /// カーソルのワールド座標(XZ)を返す。土地区画作成でどこをクリックしたかの基準になる。
    /// </summary>
    private Vector3 GetCursorWorldPosition()
    {
        if (InputHandler.Instance == null) return Vector3.zero;
        return new Vector3(InputHandler.Instance.posX, 0f, InputHandler.Instance.posZ);
    }

    private void HandleBuildingConstructionClick()
    {
        Debug.Log($"パス {pathId} がクリックされました（建物建設モード）");
        // 建物建設モードでは特別な処理なし
    }

    private void HandleRoadConstructionClick()
    {
        // パス上のクリックは地形クリックとしてRoadPlanManagerに転送
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

        // 道路沿い区画作成では、稼働中の一般道路のみ選択可能とする
        if (!IsPathSelectableForParcelCreation())
        {
            Debug.Log($"パス {pathId} は選択できません（status={GetPathStatus()}、type={GetPathType()}）");
            return;
        }

        // ウェイポイントの取得（実際の実装ではAPIから取得など）
        Vector3[] waypoints = GetPathWaypoints(pathId);

        // ParcelEditorManagerに道路選択を通知
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager != null)
        {
            parcelEditorManager.OnPathSelected(pathId, waypoints, GetCursorWorldPosition());
        }

    }

    /// <summary>
    /// 道路沿い区画作成において、このパスが選択可能かどうかを判定する。
    /// 稼働中で接道可能な街路のみ選択できる。橋は地表にあっても対象外。
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
    /// このパスのステータスを取得する。データがない場合は -1 を返す。
    /// </summary>
    private int GetPathStatus()
    {
        PathSummary pathData = pathManager?.GetPathData(pathId);
        return pathData?.status ?? -1;
    }

    /// <summary>
    /// このパスの種別を取得する。データがない場合は -1 を返す。
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
    /// 道路のウェイポイント情報を取得（仮の実装）
    /// </summary>
    private Vector3[] GetPathWaypoints(int pathId)
    {
        // APIから道路情報を取得する処理
        // ここでは簡易的に仮のウェイポイントを作成
        return new Vector3[]
        {
            new Vector3(-10, 0, 0),
            new Vector3(0, 0, 0),
            new Vector3(10, 0, 0)
        };

        // 実際の実装では:
        // return FetchPathWaypoints(pathId);
    }
}

