
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

    // 地区拡張ドラッグのファンアウト色付けで使う ParcelManager への参照
    private ParcelManager parcelManager;

    private void Start()
    {
        // VisualModeObjectコンポーネントの取得
        visualModeObject = GetComponent<VisualModeObject>();

        // マネージャーへの参照を取得
        roadPlanManager = FindAnyObjectByType<RoadPlanManager>();
        pathManager = FindAnyObjectByType<PathManager>();
        parcelManager = FindAnyObjectByType<ParcelManager>();
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
            case InputMode.MODE_ZONE_EDIT:
                HandleZoneEditClick();
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

        // 地区編集モードではツールに応じたツールチップを表示
        if (mode == InputMode.MODE_ZONE_EDIT)
        {
            var hoverCtx = ZoneEditContext.Instance;
            if (hoverCtx != null && hoverCtx.CurrentTool == ZoneEditTool.Create)
            {
                TooltipManager.Instance?.Show("クリックで新規地区を作成");
            }
            else
            {
                TooltipManager.Instance?.Show("Shift+ドラッグで地区を拡張 / 右クリックで地区を選択");
            }
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

        if (mode == InputMode.MODE_ZONE_EDIT || mode == InputMode.MODE_PATH_MERGE)
        {
            TooltipManager.Instance?.Hide();
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
        // 地区編集モードでの右クリック処理
        else if (mode == InputMode.MODE_ZONE_EDIT)
        {
            PathSummary pathData = pathManager?.GetPathData(pathId);
            int zoneId = pathData?.zone_id ?? 0;
            ZoneEditContext.Instance?.SelectZone(zoneId);
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

        // ParcelEditorManagerに道路選択を通知
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager != null)
        {
            Vector3[] waypoints = GetPathWaypoints(pathId);
            parcelEditorManager.OnPathSelected(pathId, waypoints);
        }

        // IPC: 土地区画作成モード用のパス選択イベントをUIに直接通知
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.EmitEditParcelSelectPath(pathId);
        }
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
            parcelEditorManager.OnPathSelected(pathId, waypoints);
        }

    }

    /// <summary>
    /// 道路沿い区画作成において、このパスが選択可能かどうかを判定する。
    /// 稼働中（STATUS_ACTIVE）かつ一般道路（PATH_STREET）のみ選択可能とする。
    /// </summary>
    private bool IsPathSelectableForParcelCreation()
    {
        PathSummary pathData = pathManager?.GetPathData(pathId);
        if (pathData == null)
        {
            return false;
        }
        return pathData.status == Constants.STATUS_ACTIVE && pathData.path_type == Constants.PATH_STREET;
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
    /// 地区編集モードでのパスクリック処理。即時反映型で、確定ボタンは無い。
    /// ツールは Ctrlラジアルメニュー(ZoneEditControlMenu)で切り替える。
    ///
    /// - 作成ツール(Create): クリックした街路を含む新規地区を即座に作成する
    /// - 拡張ツール(Expand): クリックでは何もしない(拡張は既存地区からの Shift+ドラッグ)
    ///
    /// バックエンドへの送信は ZoneEditContext が直接行う(3Dジェスチャで完結する操作のため、
    /// パネル確定型のようにUI(Tauri)を経由しない)。
    /// </summary>
    private void HandleZoneEditClick()
    {
        var ctx = ZoneEditContext.Instance;
        if (ctx == null) return;

        PathSummary pathData = pathManager?.GetPathData(pathId);

        // PATH_STREET以外は選択不可
        if (pathData != null && pathData.path_type != Constants.PATH_STREET)
        {
            TooltipManager.Instance?.Show("この道路は選択できません");
            return;
        }

        if (ctx.CurrentTool == ZoneEditTool.Create)
        {
            ctx.CreateZone(pathId);
        }
        else
        {
            TooltipManager.Instance?.Show("Shift+ドラッグで地区を拡張");
        }
    }

    /// <summary>
    /// 地区ドラッグ中にパス上にカーソルが重なった際の処理。
    /// PATH_STREETかつ既存地区の道路と接続されている場合のみ追加を許可し、
    /// 即座に地区色分け表示を変更する。
    /// 接続性がない道路はスキップし、ユーザーにツールチップで理由を通知する。
    /// </summary>
    public void onZoneDragHover(int sourceZoneId)
    {
        // PATH_STREET以外は対象外
        PathSummary pathData = pathManager?.GetPathData(pathId);
        if (pathData != null && pathData.path_type != Constants.PATH_STREET)
        {
            return;
        }

        // 接続性チェック：既存地区の道路または既にドラッグで追加された道路と接続しているか
        if (pathManager != null)
        {
            var candidates = pathManager.GetZonePathIds(sourceZoneId);
            var ctx = ZoneEditContext.Instance;
            if (ctx != null)
            {
                foreach (int pendingId in ctx.DragPendingPathIds)
                {
                    candidates.Add(pendingId);
                }
            }
            bool connected = pathManager.IsPathConnectedToSet(pathId, candidates);
            if (!connected)
            {
                TooltipManager.Instance?.Show("この道路は地区と繋がっていません");
                return;
            }
        }

        if (visualModeObject != null)
        {
            visualModeObject.SetHover(true);
            visualModeObject.SetTempZoneId(sourceZoneId);
        }
        TooltipManager.Instance?.Show("このパスを地区に追加");

        var dragCtx = ZoneEditContext.Instance;
        if (dragCtx != null)
        {
            dragCtx.RegisterTempColoredPath(pathId);

            // 初回タッチ時のみ、この街路に面する区画と建物へ地区色を波及させる
            // (毎フレームの再走査を避ける。フロントの逆引きインデックスのみ・バックエンド通信なし)。
            if (dragCtx.AddDragPendingPathId(pathId))
            {
                FanOutZoneColorToParcelsAndBuildings(pathId, sourceZoneId, dragCtx);
            }

            // ドラッグ中のリアルタイムポリゴンプレビューを更新
            if (dragCtx.DragPendingPathIds.Count > 0)
            {
                ZonePolygonPreview.ShowExpandPreview(sourceZoneId, dragCtx.DragPendingPathIds);
            }
        }
    }

    /// <summary>
    /// 指定街路(pathId)に面する区画と、その区画上の建物へ、ドラッグ中の地区色を一時適用する。
    /// フロントの逆引きインデックス(ParcelManager.GetParcelIdsByFrontPath /
    /// BuildingManager.GetBuildingIdsByParcel)のみで解決し、バックエンド通信は行わない。
    /// front_path_id 未取得(スキーマ未再生成)時は面区画が空集合になり、道路のみが色付く。
    /// 区画は ParcelManager のオーバーレイ描画、建物は VisualModeObject.tempZoneId で色付ける。
    /// </summary>
    private void FanOutZoneColorToParcelsAndBuildings(int pathId, int sourceZoneId, ZoneEditContext dragCtx)
    {
        if (parcelManager == null) return;

        foreach (int parcelId in parcelManager.GetParcelIdsByFrontPath(pathId))
        {
            parcelManager.SetTempZoneOverride(parcelId, sourceZoneId);

            if (BuildingManager.Instance != null)
            {
                foreach (int buildingId in BuildingManager.Instance.GetBuildingIdsByParcel(parcelId))
                {
                    GameObject bldObj = BuildingManager.Instance.GetBuildingObject(buildingId);
                    bldObj?.GetComponent<VisualModeObject>()?.SetTempZoneId(sourceZoneId);
                    dragCtx.RegisterTempColoredBuilding(buildingId);
                }
            }
        }
    }

    /// <summary>
    /// 地区ドラッグ終了時にパス上で離された際の処理。
    /// ここではハイライト解除のみ行う。追加予定リストの一括割り当ては、パス上以外で
    /// 離した場合にも確定できるよう InputHandler.EndDrag が
    /// ZoneEditContext.CommitDragPendingPaths で行う。
    /// </summary>
    public void onZoneDragEnd(int sourceZoneId)
    {
        if (visualModeObject != null)
        {
            visualModeObject.SetHover(false);
        }
        TooltipManager.Instance?.Hide();
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

