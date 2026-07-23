using System;
using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>
/// IPC経由で world.change_state イベントを受信し、
/// InputHandler + VisualModeHandler の状態を一括で切り替えるコーディネーター。
/// </summary>
public class IpcStateCoordinator : MonoBehaviour
{
    // ブルドーザ(削除)モードでのクリック削除時に鳴らす効果音。
    // インスペクタで Assets/Sounds/trash.mp3 を割り当てる。
    [SerializeField] private AudioClip trashSound;

    private void OnEnable()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnWorldChangeState += HandleChangeState;
            IpcClient.Instance.OnEditParcelUpdatePreview += HandleEditParcelUpdatePreview;
            IpcClient.Instance.OnEditParcelStartShapeEdit += HandleEditParcelStartShapeEdit;
            IpcClient.Instance.OnEditParcelCancelShapeEdit += HandleEditParcelCancelShapeEdit;
            IpcClient.Instance.OnEditParcelFinishShapeEdit += HandleEditParcelFinishShapeEdit;
            IpcClient.Instance.OnSessionReset += HandleSessionReset;
            IpcClient.Instance.OnSessionReload += HandleSessionReload;
            IpcClient.Instance.OnSessionForceReload += HandleSessionForceReload;
            IpcClient.Instance.OnSystemShutdown += HandleSystemShutdown;
            IpcClient.Instance.OnZoneUpdated += HandleZoneUpdated;
            IpcClient.Instance.OnEditConnectorShow += HandleEditConnectorShow;
            IpcClient.Instance.OnEditConnectorHide += HandleEditConnectorHide;
        }
    }

    private void OnDisable()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnWorldChangeState -= HandleChangeState;
            IpcClient.Instance.OnEditParcelUpdatePreview -= HandleEditParcelUpdatePreview;
            IpcClient.Instance.OnEditParcelStartShapeEdit -= HandleEditParcelStartShapeEdit;
            IpcClient.Instance.OnEditParcelCancelShapeEdit -= HandleEditParcelCancelShapeEdit;
            IpcClient.Instance.OnEditParcelFinishShapeEdit -= HandleEditParcelFinishShapeEdit;
            IpcClient.Instance.OnSessionReset -= HandleSessionReset;
            IpcClient.Instance.OnSessionReload -= HandleSessionReload;
            IpcClient.Instance.OnSessionForceReload -= HandleSessionForceReload;
            IpcClient.Instance.OnSystemShutdown -= HandleSystemShutdown;
            IpcClient.Instance.OnZoneUpdated -= HandleZoneUpdated;
            IpcClient.Instance.OnEditConnectorShow -= HandleEditConnectorShow;
            IpcClient.Instance.OnEditConnectorHide -= HandleEditConnectorHide;
        }
    }

    /// <summary>
    /// world.change_state イベントのハンドラ。
    /// IPC連携ドキュメントのステート表に従って InputMode + VisualMode を一括変更する。
    /// </summary>
    private void HandleChangeState(string state, Dictionary<string, object> ipcParams)
    {
        Debug.Log($"IpcStateCoordinator: ステート変更 -> {state}");

        // ステート変更に伴い、もしグリッド角度設定モード中ならキャンセルする（遷移先がSTATE_SET_GRID以外の場合）
        if (state != "STATE_SET_GRID" && GridSetManager.Instance != null)
        {
            GridSetManager.Instance.CancelGridSetting();
        }

        // 地区編集モードから他ステートへ直接遷移した場合にも地区編集の状態を確実に片付ける。
        // 地区編集はタスクバーの常駐トグル(ウィンドウを開かない)であり、他の編集モードのように
        // ウィンドウクローズ経由の STATE_NORMAL が発生しない。そのため STATE_NORMAL を経由せず
        // 別の編集モードへ移った場合、Exit() が呼ばれないと ZoneEditControlMenu(Ctrlラジアル/HUD)や
        // ホバーフィルが取り残される。STATE_NORMAL は ApplyStateNormal 内でも Exit するが冪等なので問題ない。
        if (state != "STATE_EDIT_ZONE" && ZoneEditContext.Instance != null && ZoneEditContext.Instance.IsActive)
        {
            ZoneEditContext.Instance.Exit();
        }

        switch (state)
        {
            case "STATE_NORMAL":
                ApplyStateNormal();
                break;

            case "STATE_EDIT_PATH":
                ApplyStateEditPath(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_EDIT_BUILDING":
                ApplyStateEditBuilding(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_EDIT_ZONE":
                ApplyStateEditZone(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_EDIT_PARCEL":
                ApplyStateEditParcel(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_EDIT_PARCEL_SHAPE":
                ApplyStateEditParcelShape(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_UNLOCK_CELL":
                ApplyStateUnlockCell();
                WindowUtil.BringToForeground();
                break;

            case "STATE_SET_GRID":
                ApplyStateSetGrid(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_EDIT_RESOURCE":
                ApplyStateEditResource(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_EDIT_TRANSIT":
                ApplyStateEditTransit(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_EDIT_PROP":
                ApplyStateEditProp(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_EDIT_CONNECTOR":
                // 接続点編集モード。編集開始/終了のトグルボタンを持つ AgentDetailPanel を
                // 前面に残す必要があるため、他ステートと異なり WindowUtil.BringToForeground() は
                // 呼ばない（Unity を最前面にすると右パネルが操作できなくなる）。
                ApplyStateEditConnector(ipcParams);
                break;

            case "STATE_REMOVE_OBJECTS":
                ApplyStateRemoveObjects(ipcParams);
                WindowUtil.BringToForeground();
                break;

            default:
                Debug.LogWarning($"IpcStateCoordinator: 未知のステート: {state}");
                return;
        }

        // ステート変更に伴う表示モードの変更をUIに通知する
        var coordinator = FindAnyObjectByType<IpcViewCoordinator>();
        if (coordinator != null)
        {
            coordinator.SyncFromState(state, ipcParams);

            // STATE_NORMAL の場合、SyncFromState で復元された _layerModes を
            // VisualModeHandler に一括適用する。これにより記憶された通常表示状態が反映される。
            if (state == "STATE_NORMAL")
            {
                coordinator.ApplyAllLayerModes();
            }
        }
    }

    /// <summary>
    /// STATE_NORMAL: 通常モード
    /// </summary>
    private void ApplyStateNormal()
    {
        // InputMode: 全オブジェクトをNORMALに
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.ALL_NORMAL);
        }

        // VisualMode の適用は HandleChangeState 内で SyncFromState の後に行う。
        // ここでは行わない。理由: この時点では IpcViewCoordinator の _layerModes が
        // まだ編集モードの値のままであり、記憶された通常状態を復元する前に適用してしまうと、
        // 編集モードの表示状態がそのまま残ってしまうため。

        // 地区編集モードを終了
        var zoneEditCtx = ZoneEditContext.Instance;
        if (zoneEditCtx != null)
        {
            zoneEditCtx.Exit();
        }

        // 道路エディタを終了状態に戻す
        var roadPlanManager = FindAnyObjectByType<RoadPlanManager>();
        if (roadPlanManager != null)
        {
            roadPlanManager.FinishRoadPlacement();
        }

        // 土地区画形状編集を終了
        var parcelShapeEditManager = FindAnyObjectByType<ParcelShapeEditManager>();
        if (parcelShapeEditManager != null)
        {
            parcelShapeEditManager.FinishEditMode();
        }

        // 資源エリア編集を終了
        var resourceAreaEditManager = FindAnyObjectByType<ResourceAreaEditManager>();
        if (resourceAreaEditManager != null)
        {
            resourceAreaEditManager.FinishEditMode();
        }

        // 土地区画の任意ポリゴン作成を終了(オーバーレイをクリア)
        var parcelAreaEditManager = FindAnyObjectByType<ParcelAreaEditManager>();
        if (parcelAreaEditManager != null)
        {
            parcelAreaEditManager.FinishEditMode();
        }

        // 公共交通の路線設置を終了
        var transitPlanManager = FindAnyObjectByType<TransitPlanManager>();
        if (transitPlanManager != null)
        {
            transitPlanManager.FinishPlacement();
        }

        // プロップ配置を終了
        var propPlacementManager = FindAnyObjectByType<PropPlacementManager>();
        if (propPlacementManager != null)
        {
            propPlacementManager.FinishPlacement();
        }

        // 建物建設モードを終了する。
        // 確定・キャンセルのいずれもUI側は STATE_NORMAL への遷移を送ってくるため、
        // ここで建物建設モードを解除しないと edit_building:update_shape で生成したプレビューメッシュが残り続ける。
        // (建設の確定送信はUI側が直接バックエンドへ行い、成功後に session:reload と STATE_NORMAL が届く)
        var buildingPlanManager = FindAnyObjectByType<BuildingPlanManager>();
        if (buildingPlanManager != null)
        {
            buildingPlanManager.SetBuildingMode(false);
        }

        // 土地区画作成プレビューをクリア
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager != null)
        {
            parcelEditorManager.ClearWorldSelectionState();
        }

        // 接続点編集モードを終了する（マーカーは読み取り専用ハイライトに戻す）。
        // パネルは開いたままのため、UI 側が続けて edit_connector:show を再送して
        // ハイライトを維持する。パネルを閉じた場合は edit_connector:hide で完全に消える。
        var connectorEditManager = FindAnyObjectByType<ConnectorEditManager>();
        if (connectorEditManager != null)
        {
            connectorEditManager.ExitEditMode();
        }

        // ブルドーザ(削除)モードを終了する。再入場時に前回の削除済みセットが
        // 残らないよう状態をクリアする。
        if (RemovalManager.Instance != null)
        {
            RemovalManager.Instance.EndRemovalMode();
        }
    }

    /// <summary>
    /// STATE_REMOVE_OBJECTS: ブルドーザ(オブジェクト削除)モード。
    /// 空き地区画・計画中の建物/道路を左クリックまたは左ドラッグ(ペイント式)で削除する。
    /// 実際の削除可否判定・DELETE 発行・重複排除は RemovalManager が担う
    /// (確定送信は Unity が直接バックエンドへ行い、成功後に差分リロードで反映する)。
    /// 地区表示は非表示にして、削除操作中に地区パネルが誤って開くのを防ぐ。
    /// </summary>
    private void ApplyStateRemoveObjects(Dictionary<string, object> ipcParams)
    {
        // InputMode: 削除プリセット(建物・区画・道路・地形を MODE_REMOVE に)
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.REMOVE_OBJECTS);
        }

        // VisualMode: 削除対象が見えるように、区画は境界線表示、Path/Building は計画表示
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_BOUNDARY);
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_HIDDEN);
        }

        // RemovalManager を get-or-create し、削除済みセットを初期化する
        var removalManager = RemovalManager.Instance;
        if (removalManager == null)
        {
            var go = new GameObject("RemovalManager");
            removalManager = go.AddComponent<RemovalManager>();
        }
        removalManager.SetClickSound(trashSound);
        removalManager.BeginRemovalMode();
    }

    /// <summary>
    /// STATE_EDIT_PATH: 道路敷設モード
    /// ipcParams には UI側で選択された path_type / path_variant / path_id が含まれる。
    /// path_id が指定されている場合は既存パスの編集モードとして動作する。
    ///
    /// 地区表示は非表示にする。道路敷設中に地区を誤ってクリックして
    /// 地区パネルが開くのを防ぐため。
    /// </summary>
    private void ApplyStateEditPath(Dictionary<string, object> ipcParams)
    {
        // InputMode: 道路敷設プリセット
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.ROAD_CONSTRUCTION);
        }

        // VisualMode: 計画表示（地区は非表示）
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_BOUNDARY);
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_HIDDEN);
        }

        // RoadPlanManagerを道路敷設モードで初期化
        var roadPlanManager = FindAnyObjectByType<RoadPlanManager>();
        if (roadPlanManager != null)
        {
            // IPCパラメータからpath_type / path_variant / path_idを読み取って設定する
            int? pathId = null;
            if (ipcParams != null)
            {
                if (ipcParams.TryGetValue("path_id", out object pidObj) && pidObj != null)
                {
                    pathId = Convert.ToInt32(pidObj);
                    roadPlanManager.SetPathId(pathId.Value);
                    Debug.Log($"IpcStateCoordinator: path_id = {pathId.Value} (existing path edit mode)");
                }
                if (ipcParams.TryGetValue("path_type", out object ptObj) && ptObj != null)
                {
                    int pathType = Convert.ToInt32(ptObj);
                    roadPlanManager.SetPathType(pathType);
                    Debug.Log($"IpcStateCoordinator: path_type = {pathType}");
                }
                if (ipcParams.TryGetValue("path_variant", out object pvObj) && pvObj != null)
                {
                    int pathVariant = Convert.ToInt32(pvObj);
                    roadPlanManager.SetPathSubtype(pathVariant);
                    Debug.Log($"IpcStateCoordinator: path_variant = {pathVariant}");
                }
                // 駅パスのホーム設置側。敷設モード初期化時の初期値として渡す
                // (以後の切り替えは edit_path:change_platform_side で逐次反映される)。
                if (ipcParams.TryGetValue("platform_side", out object psObj) && psObj != null)
                {
                    roadPlanManager.PlatformSide = Convert.ToInt32(psObj);
                    Debug.Log($"IpcStateCoordinator: platform_side = {roadPlanManager.PlatformSide}");
                }
            }

            roadPlanManager.InitializeRoadPlacement(pathId);
        }
    }

    /// <summary>
    /// STATE_EDIT_BUILDING: 建物建設モード
    /// ipcParams には UI側で選択された building_variant / agent_id が含まれる。
    /// agent_id が指定されている場合は既存建物の編集モードとして動作する。
    ///
    /// パーセル表示は、選択された建物のbd_typeに対応する建設可能フィルタモード
    /// （buildable_color）に設定され、建設可能な土地区画のみがハイライトされる。
    /// </summary>
    private void ApplyStateEditBuilding(Dictionary<string, object> ipcParams)
    {
        // まずbuilding_variantを読み取り、bd_typeを確定する
        int currentBdType = 0;
        int? agentId = null;

        if (ipcParams != null)
        {
            if (ipcParams.TryGetValue("agent_id", out object aidObj) && aidObj != null)
            {
                agentId = Convert.ToInt32(aidObj);
            }
            if (ipcParams.TryGetValue("building_variant", out object bvObj) && bvObj != null)
            {
                int buildingVariant = Convert.ToInt32(bvObj);
                // MasterDataStoreからbd_typeを取得
                if (MasterDataStore.Instance != null)
                {
                    var agentMaster = MasterDataStore.Instance.GetAgentMaster(buildingVariant);
                    if (agentMaster != null && agentMaster.bd_type.HasValue)
                    {
                        currentBdType = agentMaster.bd_type.Value;
                    }
                }
            }
        }

        // InputMode: 建物建設プリセット
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.BUILDING_CONSTRUCTION);
        }

        // VisualMode: 計画表示
        // パーセルは選択された建物種類に対応するフィルタモード（建設可能な区画をハイライト）
        if (VisualModeHandler.Instance != null)
        {
            int parcelMode = BuildingPlanManager.BdTypeToParcelFilterMode(currentBdType);
            VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, parcelMode);
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_NORMAL);
        }

        // BuildingPlanManagerを建物建設モードで初期化
        var buildingPlanManager = FindAnyObjectByType<BuildingPlanManager>();
        if (buildingPlanManager != null)
        {
            if (ipcParams != null)
            {
                if (ipcParams.TryGetValue("building_variant", out object bvObj) && bvObj != null)
                {
                    int buildingVariant = Convert.ToInt32(bvObj);
                    buildingPlanManager.SetBuildingVariant(buildingVariant);
                    Debug.Log($"IpcStateCoordinator: building_variant = {buildingVariant}");
                }
            }

            if (agentId.HasValue && agentId.Value > 0)
            {
                buildingPlanManager.SetEditMode(agentId.Value);
                Debug.Log($"IpcStateCoordinator: agent_id = {agentId.Value} (existing building edit mode)");
            }
            else
            {
                buildingPlanManager.SetNewBuildingMode();
            }

            buildingPlanManager.SetBuildingMode(true);
        }
    }

    /// <summary>
    /// STATE_EDIT_ZONE: 地区編集モード
    /// ipcParams に zone_id が含まれる場合は既存地区編集、含まれない場合は新規作成モードとする。
    /// </summary>
    private void ApplyStateEditZone(Dictionary<string, object> ipcParams)
    {
        int? zoneId = null;
        if (ipcParams != null && ipcParams.TryGetValue("zone_id", out var val))
        {
            if (val is long l) zoneId = (int)l;
            else if (val is int i) zoneId = i;
        }

        var ctx = ZoneEditContext.Instance;
        if (ctx == null)
        {
            var go = new GameObject("ZoneEditContext");
            ctx = go.AddComponent<ZoneEditContext>();
        }

        if (zoneId.HasValue)
        {
            ctx.EnterEditMode(zoneId.Value);
        }
        else
        {
            ctx.EnterCreateMode();
        }

        // ツール切替のCtrlラジアルメニュー+カーソルHUDのドライバを ctx と同じGameObjectに常駐させる
        // (RoadPlanManager における RoadEditControlMenu と同じ構成。ドライバは ctx.IsActive を
        //  ポーリングして自律的に開閉するため、ここでは初期化のみ行えばよい)。
        var controlMenu = ctx.GetComponent<ZoneEditControlMenu>();
        if (controlMenu == null)
        {
            controlMenu = ctx.gameObject.AddComponent<ZoneEditControlMenu>();
        }
        controlMenu.Initialize(ctx);

        // InputMode: 地区編集プリセット
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.ZONE_EDIT);
        }

        // VisualMode: 計画表示
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualModeParcel.ZONE_COLOR);
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualModePath.ZONE_COLOR);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualModeBuilding.ZONE_COLOR);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_NORMAL);
        }
    }

    /// <summary>
    /// STATE_EDIT_PARCEL: 土地区画作成モード
    /// ipcParams には UI側で選択された parcel_creation_mode が含まれる。
    /// モードに応じて InputModePreset を切り替える。
    /// </summary>
    private void ApplyStateEditParcel(Dictionary<string, object> ipcParams)
    {
        string creationMode = "empty_area";
        if (ipcParams != null && ipcParams.TryGetValue("parcel_creation_mode", out object modeObj) && modeObj != null)
        {
            creationMode = modeObj.ToString();
        }

        int preset;
        switch (creationMode)
        {
            case "empty_area":
            case "tile":
            case "random":
                preset = InputModePreset.PARCEL_CREATION_POINT;
                break;
            case "along_path":
                preset = InputModePreset.PARCEL_CREATION_PATH;
                break;
            case "split_rectangle":
            case "split_random":
            case "split_perimeter":
                preset = InputModePreset.PARCEL_CREATION_SPLIT;
                break;
            case "manual":
                preset = InputModePreset.PARCEL_CREATION_POLYGON;
                break;
            default:
                preset = InputModePreset.PARCEL_CREATION_POINT;
                break;
        }

        // InputMode: 土地区画作成プリセット
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(preset);
        }

        // VisualMode: 計画表示（Parcelは境界線表示、Path/Buildingは計画表示）
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_BOUNDARY);
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_HIDDEN);
        }

        // ParcelEditorManagerにモードを通知
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager != null)
        {
            switch (creationMode)
            {
                case "empty_area":
                case "tile":
                case "random":
                    parcelEditorManager.EnablePointSelectionMode();
                    break;
                case "along_path":
                    parcelEditorManager.EnablePathSelectionMode();
                    break;
                case "split_rectangle":
                case "split_random":
                case "split_perimeter":
                    parcelEditorManager.EnableParcelSelectionMode();
                    break;
                case "manual":
                    // 任意ポリゴン作成では選択系の ParcelEditorManager は使わないため無効化する。
                    parcelEditorManager.ClearWorldSelectionState();
                    break;
            }
        }

        // 任意ポリゴン作成モードでは、頂点配置・ドラッグでポリゴンを組み立てる
        // ParcelAreaEditManager を新規作成モードで初期化する(無ければ動的生成。資源エリア編集と同じ枠組み)。
        // それ以外のモードに切り替わったときは、STATE_NORMAL を経由せず直接モード変更される
        // (作成パネルでのモード切替)ケースに備え、残っている任意ポリゴンのオーバーレイを消す。
        if (creationMode == "manual")
        {
            var parcelAreaEditManager = FindAnyObjectByType<ParcelAreaEditManager>();
            if (parcelAreaEditManager == null)
            {
                var go = new GameObject("ParcelAreaEditManager");
                parcelAreaEditManager = go.AddComponent<ParcelAreaEditManager>();
            }
            parcelAreaEditManager.InitializeCreateMode();
        }
        else
        {
            var parcelAreaEditManager = FindAnyObjectByType<ParcelAreaEditManager>();
            if (parcelAreaEditManager != null)
            {
                parcelAreaEditManager.FinishEditMode();
            }
        }

        Debug.Log($"IpcStateCoordinator: 土地区画作成モード -> {creationMode} (preset={preset})");
    }

    /// <summary>
    /// STATE_EDIT_PARCEL_SHAPE: 土地区画形状編集モード
    /// ipcParams には parcel_id と polygon が含まれる。
    /// ParcelShapeEditManager を初期化して頂点編集モードを開始する。
    /// </summary>
    private void ApplyStateEditParcelShape(Dictionary<string, object> ipcParams)
    {
        int parcelId = 0;
        float[][] polygon = null;
        if (ipcParams != null)
        {
            if (ipcParams.TryGetValue("parcel_id", out var pidObj) && pidObj != null)
            {
                parcelId = Convert.ToInt32(pidObj);
            }
            // world.change_state の場合は polygon は params に含まれない
            // edit_parcel:start_shape_edit の場合は直接 HandleEditParcelStartShapeEdit で処理する
        }

        // InputMode: 地形を形状編集モードに、それ以外は無効化
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.PARCEL_SHAPE_EDIT);
        }

        // VisualMode: 計画表示
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_BOUNDARY);
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_HIDDEN);
        }

        // ParcelShapeEditManager を初期化
        var shapeEditManager = FindAnyObjectByType<ParcelShapeEditManager>();
        if (shapeEditManager == null)
        {
            var go = new GameObject("ParcelShapeEditManager");
            shapeEditManager = go.AddComponent<ParcelShapeEditManager>();
        }

        if (parcelId > 0 && polygon != null)
        {
            shapeEditManager.InitializeEditMode(parcelId, polygon);
        }

        Debug.Log($"IpcStateCoordinator: 土地区画形状編集モード -> parcel={parcelId}");
    }

    /// <summary>
    /// STATE_EDIT_RESOURCE: 資源エリア編集モード(新規作成)
    /// UI側で資源種別が選択されたタイミングで送られてくる(ipcParamsに resource_type が含まれるが、
    /// 種別自体はUI側の状態としてのみ保持され、Unity側のポリゴン編集ロジックには影響しない)。
    /// </summary>
    private void ApplyStateEditResource(Dictionary<string, object> ipcParams)
    {
        // InputMode: 資源エリア編集プリセット
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.EDIT_RESOURCE);
        }

        // VisualMode: 計画表示(土地区画形状編集と同じ設定。地区は非表示)
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_BOUNDARY);
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_HIDDEN);
        }

        // ResourceAreaEditManager を新規作成モードで初期化
        var resourceAreaEditManager = FindAnyObjectByType<ResourceAreaEditManager>();
        if (resourceAreaEditManager == null)
        {
            var go = new GameObject("ResourceAreaEditManager");
            resourceAreaEditManager = go.AddComponent<ResourceAreaEditManager>();
        }
        resourceAreaEditManager.InitializeCreateMode();

        Debug.Log("IpcStateCoordinator: 資源エリア編集モードを適用しました");
    }

    /// <summary>
    /// STATE_EDIT_TRANSIT: 公共交通の路線設置モード
    /// ipcParams には UI側で選択された path_variant(区間パスのバリアント=transit_master の
    /// transit_path_id)と、停留所を設置できる下地パスのバリアント一覧 stop_path_variants が含まれる。
    /// 停留所は stop_path_variants に属するパス(駅・街路など)の上にのみ設置できるため、
    /// その集合を TransitPlanManager に渡して吸着対象を絞る。
    ///
    /// 地区表示は非表示にする。路線設置中に地区を誤ってクリックして地区パネルが開くのを防ぐため。
    /// </summary>
    private void ApplyStateEditTransit(Dictionary<string, object> ipcParams)
    {
        // InputMode: 公共交通設置プリセット
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.TRANSIT_CONSTRUCTION);
        }

        // VisualMode: 計画表示（地区は非表示）。道路建設と同じ設定。
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_BOUNDARY);
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_HIDDEN);
        }

        int pathVariant = 0;
        int[] stopPathVariants = System.Array.Empty<int>();
        if (ipcParams != null)
        {
            if (ipcParams.TryGetValue("path_variant", out object pvObj) && pvObj != null)
            {
                pathVariant = Convert.ToInt32(pvObj);
            }
            if (ipcParams.TryGetValue("stop_path_variants", out object spvObj) && spvObj != null)
            {
                stopPathVariants = ReadIntArray(spvObj);
            }
        }

        // TransitPlanManager を路線設置モードで初期化(無ければ動的生成)。
        var transitPlanManager = FindAnyObjectByType<TransitPlanManager>();
        if (transitPlanManager == null)
        {
            var go = new GameObject("TransitPlanManager");
            transitPlanManager = go.AddComponent<TransitPlanManager>();
        }
        transitPlanManager.InitializeTransitPlacement(pathVariant, stopPathVariants);

        Debug.Log($"IpcStateCoordinator: 公共交通設置モード -> path_variant={pathVariant}, stop_variants={stopPathVariants.Length}");
    }

    /// <summary>
    /// STATE_EDIT_PROP: プロップ配置モード
    /// ipcParams には UI側で選択された prop_type / placement_mode("instance" | "scatter") と、
    /// 初期の配置設定 rotation / scale(個別モード) / density / seed(範囲モード) が含まれる。
    /// PropPlacementManager をモードごとに初期化する(無ければ動的生成)。公共交通設置と同じく
    /// 確定送信(POST /api/v1/props/...)は UI(Tauri)側が行い、本コーディネータはモード設定のみ担う。
    /// </summary>
    private void ApplyStateEditProp(Dictionary<string, object> ipcParams)
    {
        // InputMode: プロップ配置プリセット(地形クリックで配置)。
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.PROP_PLACEMENT);
        }

        // VisualMode: 計画表示（地区は非表示）。公共交通設置と同じ設定。
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_BOUNDARY);
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_HIDDEN);
        }

        int propType = 0;
        string placementMode = "instance";
        float rotation = 0f;
        float scale = 1f;
        float density = 400f;
        int seed = 0;
        if (ipcParams != null)
        {
            if (ipcParams.TryGetValue("prop_type", out object ptObj) && ptObj != null)
                propType = Convert.ToInt32(ptObj);
            if (ipcParams.TryGetValue("placement_mode", out object pmObj) && pmObj != null)
                placementMode = Convert.ToString(pmObj);
            if (ipcParams.TryGetValue("rotation", out object rObj) && rObj != null)
                rotation = Convert.ToSingle(rObj);
            if (ipcParams.TryGetValue("scale", out object sObj) && sObj != null)
                scale = Convert.ToSingle(sObj);
            if (ipcParams.TryGetValue("density", out object dObj) && dObj != null)
                density = Convert.ToSingle(dObj);
            if (ipcParams.TryGetValue("seed", out object seedObj) && seedObj != null)
                seed = Convert.ToInt32(seedObj);
        }

        // PropPlacementManager を配置モードで初期化(無ければ動的生成)。
        var propPlacementManager = FindAnyObjectByType<PropPlacementManager>();
        if (propPlacementManager == null)
        {
            var go = new GameObject("PropPlacementManager");
            propPlacementManager = go.AddComponent<PropPlacementManager>();
        }
        propPlacementManager.InitializePlacement(placementMode, propType, rotation, scale, density, seed);

        Debug.Log($"IpcStateCoordinator: プロップ配置モード -> prop_type={propType}, mode={placementMode}");
    }

    /// <summary>
    /// STATE_EDIT_CONNECTOR: 接続点編集モード
    /// ipcParams には UI側で選択された agent_id / infra_node_id / depot_node_id が含まれる。
    /// 対象の接続端子ノードをドラッグ移動可能にし、それ以外のオブジェクトのクリックは無効化する。
    /// 表示モード(VisualMode)は変更しない（マーカーは ConnectorEditManager がノードを強制表示して描く）。
    /// </summary>
    private void ApplyStateEditConnector(Dictionary<string, object> ipcParams)
    {
        int infraNodeId = 0;
        int depotNodeId = 0;
        if (ipcParams != null)
        {
            if (ipcParams.TryGetValue("infra_node_id", out object inObj) && inObj != null)
                infraNodeId = Convert.ToInt32(inObj);
            if (ipcParams.TryGetValue("depot_node_id", out object dnObj) && dnObj != null)
                depotNodeId = Convert.ToInt32(dnObj);
        }

        // InputMode: 接続点編集プリセット（NODE=MODE_EDIT、他=MODE_NONE）
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.CONNECTOR_EDIT);
        }

        GetOrCreateConnectorEditManager().EnterEditMode(infraNodeId, depotNodeId);
        Debug.Log($"IpcStateCoordinator: 接続点編集モード -> infra_node={infraNodeId}, depot_node={depotNodeId}");
    }

    /// <summary>
    /// edit_connector:show: AgentDetailPanel が施設を表示している間、接続端子ノードを
    /// 読み取り専用でハイライト表示する。
    /// </summary>
    private void HandleEditConnectorShow(int agentId, int infraNodeId, int depotNodeId)
    {
        GetOrCreateConnectorEditManager().ShowConnectors(infraNodeId, depotNodeId);
    }

    /// <summary>
    /// edit_connector:hide: パネルが閉じた／別エージェントに切り替わった。ハイライトを消す。
    /// </summary>
    private void HandleEditConnectorHide()
    {
        var mgr = FindAnyObjectByType<ConnectorEditManager>();
        if (mgr != null) mgr.Hide();
    }

    private ConnectorEditManager GetOrCreateConnectorEditManager()
    {
        var mgr = FindAnyObjectByType<ConnectorEditManager>();
        if (mgr == null)
        {
            var go = new GameObject("ConnectorEditManager");
            mgr = go.AddComponent<ConnectorEditManager>();
        }
        return mgr;
    }

    /// <summary>
    /// ipcParams から取り出した値(JArray または IEnumerable)を int 配列へ変換する。
    /// Newtonsoft のデシリアライズにより配列は JArray、要素は数値トークンになる。
    /// </summary>
    private static int[] ReadIntArray(object value)
    {
        if (value is Newtonsoft.Json.Linq.JArray jArr)
        {
            var list = new List<int>(jArr.Count);
            foreach (var token in jArr)
            {
                list.Add((int)token);
            }
            return list.ToArray();
        }
        if (value is System.Collections.IEnumerable en && !(value is string))
        {
            var list = new List<int>();
            foreach (var item in en)
            {
                if (item != null) list.Add(Convert.ToInt32(item));
            }
            return list.ToArray();
        }
        return System.Array.Empty<int>();
    }

    /// <summary>
    /// edit_parcel:start_shape_edit: UI側から形状編集開始を受信。
    /// ステート変更なしで直接 ParcelShapeEditManager を初期化する。
    /// </summary>
    private void HandleEditParcelStartShapeEdit(int parcelId, float[][] polygon)
    {
        Debug.Log($"IpcStateCoordinator: 土地区画形状編集開始 -> parcel={parcelId}");

        // InputMode: 地形を形状編集モードに
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.PARCEL_SHAPE_EDIT);
        }

        // VisualMode: 計画表示
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_BOUNDARY);
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_HIDDEN);
        }

        var shapeEditManager = FindAnyObjectByType<ParcelShapeEditManager>();
        if (shapeEditManager == null)
        {
            var go = new GameObject("ParcelShapeEditManager");
            shapeEditManager = go.AddComponent<ParcelShapeEditManager>();
        }
        shapeEditManager.InitializeEditMode(parcelId, polygon);
    }

    /// <summary>
    /// edit_parcel:cancel_shape_edit: UI側からキャンセルを受信。
    /// ParcelShapeEditManager を終了して通常モードに戻す。
    /// </summary>
    private void HandleEditParcelCancelShapeEdit()
    {
        Debug.Log("IpcStateCoordinator: 土地区画形状編集キャンセル");
        var shapeEditManager = FindAnyObjectByType<ParcelShapeEditManager>();
        if (shapeEditManager != null)
        {
            shapeEditManager.FinishEditMode();
        }
        ApplyStateNormal();
    }

    /// <summary>
    /// edit_parcel:finish_shape_edit: UI側が PATCH /parcels/{id} でポリゴンの保存に
    /// 成功した後に届く終了通知(バックエンドへの送信自体はUI側が行う)。
    /// ParcelShapeEditManager を終了して最新データを再取得し、通常モードに戻す。
    /// </summary>
    private void HandleEditParcelFinishShapeEdit()
    {
        Debug.Log("IpcStateCoordinator: 土地区画形状編集終了(保存済み)");
        var shapeEditManager = FindAnyObjectByType<ParcelShapeEditManager>();
        if (shapeEditManager != null)
        {
            shapeEditManager.CompleteEditMode();
        }
        ApplyStateNormal();
    }

    /// <summary>
    /// STATE_UNLOCK_CELL: 地形セル開放モード
    /// 地形クリックでセルを選択し、UI側で開放確定を行う。
    /// </summary>
    private void ApplyStateUnlockCell()
    {
        // InputMode: 地形セル開放プリセット
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.UNLOCK_CELL);
        }

        // VisualMode: 通常表示（地形オーバーレイはそのまま）
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_HIDDEN);
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_HIDDEN);
        }

        // 道路エディタを終了状態に戻す
        var roadPlanManager = FindAnyObjectByType<RoadPlanManager>();
        if (roadPlanManager != null)
        {
            roadPlanManager.FinishRoadPlacement();
        }

        Debug.Log("IpcStateCoordinator: 地形セル開放モードを適用しました");
    }

    /// <summary>
    /// STATE_SET_GRID: グリッド角度設定ノード選択モード
    /// </summary>
    private void ApplyStateSetGrid(Dictionary<string, object> ipcParams)
    {
        // InputMode: グリッド設定用プリセット
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.SET_GRID);
        }

        // VisualMode: 通常の計画表示（地形クリックで吸着を有効にするため、ノードや道路が表示されている状態を維持）
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_BOUNDARY);
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_HIDDEN);
        }

        // GridSetManagerを初期化して開始
        var gridSetManager = FindAnyObjectByType<GridSetManager>();
        if (gridSetManager == null)
        {
            var go = new GameObject("GridSetManager");
            gridSetManager = go.AddComponent<GridSetManager>();
        }
        gridSetManager.StartGridSetting();
    }

    /// <summary>
    /// edit_parcel:update_preview: UI側からプレビュー形状データを受信し、
    /// ParcelEditorManager 経由で ParcelPlanRenderer に描画指示を出す。
    /// </summary>
    private void HandleEditParcelUpdatePreview(string mode, float[][][] previewPolygons, float[][] originalPolygon)
    {
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager == null)
        {
            Debug.LogWarning("IpcStateCoordinator: ParcelEditorManager が見つかりません");
            return;
        }

        // プレビューデータが空の場合はクリア
        if (previewPolygons == null || previewPolygons.Length == 0)
        {
            parcelEditorManager.ClearPreview();
            return;
        }

        // float[][][] → List<List<Vector3>> に変換
        List<List<Vector3>> parcelPolygons = new List<List<Vector3>>();
        foreach (var poly in previewPolygons)
        {
            if (poly == null) continue;
            List<Vector3> vecList = new List<Vector3>();
            foreach (var point in poly)
            {
                if (point == null || point.Length < 2) continue;
                float x = point[0];
                float z = point[1];
                float y = Utils.GetGroundHeight(new Vector3(x, 1000f, z)) + 0.1f;
                vecList.Add(new Vector3(x, y, z));
            }
            if (vecList.Count > 0)
            {
                parcelPolygons.Add(vecList);
            }
        }

        // 元区画ポリゴンの変換（分割モード時）
        List<Vector3> originalBlock = null;
        if (originalPolygon != null && originalPolygon.Length > 0)
        {
            originalBlock = new List<Vector3>();
            foreach (var point in originalPolygon)
            {
                if (point == null || point.Length < 2) continue;
                float x = point[0];
                float z = point[1];
                float y = Utils.GetGroundHeight(new Vector3(x, 1000f, z)) + 0.1f;
                originalBlock.Add(new Vector3(x, y, z));
            }
        }

        // モードに応じて描画メソッドを切り替え
        switch (mode)
        {
            case "empty_area":
                parcelEditorManager.RenderEmptyParcelPreview(previewPolygons[0]);
                break;
            case "along_path":
                // 道路沿いの場合、元のパス情報は保持していないため、新規区画のみ描画
                parcelEditorManager.parcelPlanRenderer.RenderBlockPreview(null, parcelPolygons);
                break;
            case "split_rectangle":
            case "split_random":
            case "split_perimeter":
                parcelEditorManager.parcelPlanRenderer.RenderSplitPreview(originalBlock, parcelPolygons);
                break;
            default:
                parcelEditorManager.parcelPlanRenderer.RenderBlockPreview(originalBlock, parcelPolygons);
                break;
        }

        Debug.Log($"IpcStateCoordinator: プレビュー描画 -> mode={mode}, parcels={parcelPolygons.Count}");
    }

    /// <summary>
    /// session.reset: 現在の3Dオブジェクトや地形のデータを全てクリアする
    /// </summary>
    private void HandleSessionReset()
    {
        Debug.Log("IpcStateCoordinator: セッションリセットを実行します");

        // 表示状態の記憶をクリア
        var viewCoordinator = FindAnyObjectByType<IpcViewCoordinator>();
        viewCoordinator?.ClearNormalStateMemory();

        // 各データマネージャーおよび内部状態キャッシュ、各種ストアを完全にクリア
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ClearAllManagerData();
        }
    }

    /// <summary>
    /// session.reload: バックエンドからデータを取得して、3Dを再描画する。
    /// エポック完了後の更新などで呼ばれる。カメラ範囲内のデータを全量再取得し、
    /// ローカルキャッシュと比較して変化があったものだけを再作成・削除する。
    /// version が変わっていないオブジェクトには一切触れない。
    /// </summary>
    private void HandleSessionReload()
    {
        Debug.Log("IpcStateCoordinator: セッションリロードを実行します（versionベース差分更新）");

        // 表示状態の記憶をクリア
        var viewCoordinator = FindAnyObjectByType<IpcViewCoordinator>();
        viewCoordinator?.ClearNormalStateMemory();

        // セッションリロード時は別のセーブデータが読み込まれた可能性があるため、
        // 開放済みセルリストをバックエンドから再取得してから3D更新を行う。
        var terrainManager = FindAnyObjectByType<TerrainManager>();
        if (terrainManager != null)
        {
            StartCoroutine(FetchAcquiredCellsThenReload(terrainManager));
        }
        else if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ReloadAllData();
        }

        // 経路(route_path_ids)は毎エポック再計算される。session:reload ごとに路線一覧を取り直し、
        // 再ルート/一時停止など再計算結果を公共交通オーバーレイへ反映する。
        TransitOverlayManager.Instance?.RefreshLines();
    }

    private System.Collections.IEnumerator FetchAcquiredCellsThenReload(TerrainManager terrainManager)
    {
        yield return terrainManager.FetchAcquiredCells();
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ReloadAllData();
        }
    }

    /// <summary>
    /// session.force_reload: すべての3Dオブジェクトを消去してからバックエンドからデータを取得し、再描画する。
    /// タスクバーのリロードボタンなど、明示的に全オブジェクトを作り直したい場合に使用する。
    /// </summary>
    private void HandleSessionForceReload()
    {
        Debug.Log("IpcStateCoordinator: セッション強制全リロードを実行します（全オブジェクト消去→再取得）");

        // 表示状態の記憶をクリア
        var viewCoordinator = FindAnyObjectByType<IpcViewCoordinator>();
        viewCoordinator?.ClearNormalStateMemory();

        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ForceReloadAllData();
        }
    }

    /// <summary>
    /// system.shutdown: グラフィックプログラムを終了する
    /// </summary>
    private void HandleSystemShutdown()
    {
        Debug.Log("IpcStateCoordinator: システムシャットダウン要求を受信しました");
        Application.Quit();
    }

    /// <summary>
    /// zone.updated: UI側で地区情報（名前や建築規制等）が更新された。
    /// ZoneManagerに該当地区の再取得を指示し、キャッシュとUI表示を即座に更新する。
    /// 地区表示が非表示の状態でも、キャッシュは更新されて次回表示時に正しい内容が反映される。
    /// </summary>
    private void HandleZoneUpdated(int zoneId)
    {
        Debug.Log($"IpcStateCoordinator: 地区 {zoneId} の更新を反映します");
        var zoneManager = FindAnyObjectByType<ZoneManager>();
        if (zoneManager != null)
        {
            StartCoroutine(zoneManager.FetchZone(zoneId));
        }
    }
}
