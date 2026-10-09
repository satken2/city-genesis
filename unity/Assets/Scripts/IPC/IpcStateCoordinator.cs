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

    /// <summary>
    /// Unity が実際に適用したステート。world:change_state の受信だけでなく、パネルを閉じたときの
    /// 編集モードの打ち切りや Esc など、Unity 内部で通常モードへ戻したときにも発火する
    /// (モード表示を IPC のステートだけで追うと、内部の復帰に追従できないため)。
    /// </summary>
    public static event Action<string> OnStateApplied;

    private void OnEnable()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnWorldChangeState += HandleChangeState;
            IpcClient.Instance.OnEditParcelUpdatePreview += HandleEditParcelUpdatePreview;
            IpcClient.Instance.OnEditParcelResetSelection += HandleEditParcelResetSelection;
            IpcClient.Instance.OnEditParcelSetRectParams += HandleEditParcelSetRectParams;
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
            IpcClient.Instance.OnEditPathCancelTopologyEdit += HandleEditPathCancelTopologyEdit;
            IpcClient.Instance.OnEditPathFinishTopologyEdit += HandleEditPathFinishTopologyEdit;
            IpcClient.Instance.OnBuildingConstructionPickSource += HandleBuildingConstructionPickSource;
            IpcClient.Instance.OnEditRoadCancel += HandleEditRoadCancel;
            IpcClient.Instance.OnEditGradingFinish += HandleEditGradingFinish;
        }
    }

    private void OnDisable()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnWorldChangeState -= HandleChangeState;
            IpcClient.Instance.OnEditParcelUpdatePreview -= HandleEditParcelUpdatePreview;
            IpcClient.Instance.OnEditParcelResetSelection -= HandleEditParcelResetSelection;
            IpcClient.Instance.OnEditParcelSetRectParams -= HandleEditParcelSetRectParams;
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
            IpcClient.Instance.OnEditPathCancelTopologyEdit -= HandleEditPathCancelTopologyEdit;
            IpcClient.Instance.OnEditPathFinishTopologyEdit -= HandleEditPathFinishTopologyEdit;
            IpcClient.Instance.OnBuildingConstructionPickSource -= HandleBuildingConstructionPickSource;
            IpcClient.Instance.OnEditRoadCancel -= HandleEditRoadCancel;
            IpcClient.Instance.OnEditGradingFinish -= HandleEditGradingFinish;
        }
    }

    /// <summary>
    /// 既存建物コピー建設: コピー元建物の選択モードへ入る。
    /// 建物だけをクリック可能(MODE_PICK_BUILDING)にし、選択されると BuildingInputHandler が
    /// building_construction:copy_source を UI へ送る。以降は UI 主導で STATE_EDIT_BUILDING へ遷移する。
    /// </summary>
    private void HandleBuildingConstructionPickSource()
    {
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.PICK_BUILDING);
        }

        // 全建物をクリック・ホバー対象にする(計画中建物は含めない: 稼働中/建設済みの実在建物をコピー元とする)。
        if (VisualModeHandler.Instance != null)
        {
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetMode(InputObjectType.PARCEL, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_NORMAL);
        }

        Debug.Log("IpcStateCoordinator: コピー元建物の選択モードに入りました");
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

        // 土地区画作成モードから他ステートへ直接遷移した場合にも作成モードを終了させる。
        // これを怠ると ParcelEditControlMenu(Ctrlラジアル)とカーソルHUDが別モードでも生き残る
        // (STATE_NORMAL 経由なら ApplyStateNormal 内でも終了するが冪等なので問題ない)。
        if (state != "STATE_EDIT_PARCEL" && ParcelEditorManager.Instance != null && ParcelEditorManager.Instance.IsActive)
        {
            ParcelEditorManager.Instance.ExitCreationMode();
        }

        // パス結合/分割の選択モードから別モードへ直接遷移した場合にも、候補パスの着色と
        // 分割点マーカーを確実に片付ける(STATE_NORMAL 経由なら ApplyStateNormal 内でも
        // 終了するが冪等なので問題ない)。
        if (state != "STATE_MERGE_PATH" && state != "STATE_SPLIT_PATH"
            && PathTopologyEditManager.Instance != null && PathTopologyEditManager.Instance.IsActive)
        {
            PathTopologyEditManager.Instance.EndMode();
        }

        // 道路の区間編集モードから別モードへ直接遷移した場合にも、区間編集の帯を確実に片付ける
        // (End が edit_road:ended を送り、開いたままの道路詳細パネルの「区間を編集中」を解除させる)。
        if (state != "STATE_EDIT_ROAD_MEMBERS" && RoadMemberEditManager.Instance != null && RoadMemberEditManager.Instance.IsActive)
        {
            RoadMemberEditManager.Instance.End();
        }

        // 造成範囲の作図から別モードへ直接遷移した場合にも、作図中のポリゴンとオーバーレイを片付ける
        // (STATE_NORMAL 経由なら ApplyStateNormal 内でも終了するが冪等なので問題ない)。
        if (state != "STATE_EDIT_GRADING")
        {
            FindAnyObjectByType<GradingAreaEditManager>()?.FinishEditMode();
        }

        // 接続点編集から別モードへ直接遷移した場合にも、端子のドラッグ受付を終えて読み取り専用の
        // ハイライトに戻す。edit_connector:hide はこのフラグで接続点編集中かを判断するため、
        // STATE_NORMAL を経由しない遷移でも残してはいけない。
        if (state != "STATE_EDIT_CONNECTOR")
        {
            ConnectorEditManager.Instance?.ExitEditMode();
        }

        // 一方通行線路の進行方向矢印は道路敷設モードでのみ表示する
        // （既存線路に向きの対を作って接続する際の編集支援表示のため）。
        TrackDirectionArrows.SetVisible(state == "STATE_EDIT_PATH");

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

            case "STATE_EDIT_PARCEL":
                ApplyStateEditParcel(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_EDIT_PARCEL_SHAPE":
                ApplyStateEditParcelShape(ipcParams);
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

            case "STATE_EDIT_GRADING":
                ApplyStateEditGrading(ipcParams);
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
                // 接続点編集モード。このステートを送った AgentDetailPanel を
                // 前面に残す必要があるため、他ステートと異なり WindowUtil.BringToForeground() は
                // 呼ばない（Unity を最前面にすると右パネルが操作できなくなる）。
                ApplyStateEditConnector(ipcParams);
                break;

            case "STATE_REMOVE_OBJECTS":
                ApplyStateRemoveObjects(ipcParams);
                WindowUtil.BringToForeground();
                break;

            case "STATE_MERGE_PATH":
            case "STATE_SPLIT_PATH":
                // パス結合/分割の対象選択モード。選択結果の長さを見て確定するパネル
                // (PathDetailPanel)を操作し続ける必要があるため、接続点編集と同じく
                // WindowUtil.BringToForeground() は呼ばない(Unityを最前面にすると
                // パネルが操作できなくなる)。
                ApplyStatePathTopologyEdit(state, ipcParams);
                break;

            case "STATE_EDIT_ROAD_MEMBERS":
                // 道路の区間編集モード。区間をクリックするたびに道路詳細パネルが所属を更新して
                // 結果を表示するため、パス結合と同じく WindowUtil.BringToForeground() は呼ばない。
                ApplyStateEditRoadMembers(ipcParams);
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

        OnStateApplied?.Invoke(state);
    }

    /// <summary>
    /// UI から STATE_NORMAL が届いたときと同じ手順で、Unity 内部の理由により通常モードへ戻す。
    /// 表示は記憶された通常状態を復元してから一括適用する(ApplyStateNormal の注記を参照)。
    /// </summary>
    private void ReturnToNormalState()
    {
        ApplyStateNormal();

        var coordinator = FindAnyObjectByType<IpcViewCoordinator>();
        if (coordinator != null)
        {
            coordinator.SyncFromState("STATE_NORMAL");
            coordinator.ApplyAllLayerModes();
        }

        OnStateApplied?.Invoke("STATE_NORMAL");
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

        // 造成範囲の作図を終了
        var gradingAreaEditManager = FindAnyObjectByType<GradingAreaEditManager>();
        if (gradingAreaEditManager != null)
        {
            gradingAreaEditManager.FinishEditMode();
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

        // 土地区画作成モードを終了する(選択・プレビュー・Ctrlラジアル/カーソルHUDを片付ける)
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager != null)
        {
            parcelEditorManager.ExitCreationMode();
        }

        // 接続点編集モードを終了する（マーカーは読み取り専用ハイライトに戻す）。
        // 詳細パネルは開いたままのことがあるのでマーカーは残し、パネルを閉じたときの
        // edit_connector:hide で消す。
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

        // パス結合/分割の選択モードを終了する(候補パスの着色・分割点マーカーを消す)。
        // 確定・キャンセルのいずれもUI側は STATE_NORMAL への遷移を送ってくる。
        if (PathTopologyEditManager.Instance != null)
        {
            PathTopologyEditManager.Instance.EndMode();
        }

        // 道路の区間編集モードを終了する(区間編集の帯を消す。道路詳細パネルの強調は残す)。
        RoadMemberEditManager.Instance?.End();
    }

    /// <summary>
    /// STATE_REMOVE_OBJECTS: ブルドーザ(オブジェクト削除)モード。
    /// 空き地区画・計画中の建物/道路を左クリックで削除する(ドラッグでは削除しない)。
    /// 実際の削除可否判定・DELETE 発行・重複排除は RemovalManager が担う
    /// (確定送信は Unity が直接バックエンドへ行い、成功後に差分リロードで反映する)。
    /// 地区表示は非表示にして、削除操作中に地区パネルが誤って開くのを防ぐ。
    /// ipcParams の instant_remove=true(UI が神モードのときに付ける)では、
    /// 建物・道路をステータスを問わず即時に削除する(Unity は神モードかを知らないので UI の指定に従う)。
    /// </summary>
    private void ApplyStateRemoveObjects(Dictionary<string, object> ipcParams)
    {
        // InputMode: 削除プリセット(建物・区画・道路・地形を MODE_REMOVE に)
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.REMOVE_OBJECTS);
        }

        // VisualMode の適用は IpcViewCoordinator.SyncFromState(ステート別の表示プリセット)が行う。

        // RemovalManager を get-or-create し、削除済みセットを初期化する
        var removalManager = RemovalManager.Instance;
        if (removalManager == null)
        {
            var go = new GameObject("RemovalManager");
            removalManager = go.AddComponent<RemovalManager>();
        }
        removalManager.SetClickSound(trashSound);
        bool instantRemove = ipcParams != null
            && ipcParams.TryGetValue("instant_remove", out var ir)
            && System.Convert.ToBoolean(ir);
        removalManager.BeginRemovalMode(instantRemove);
    }

    /// <summary>
    /// STATE_MERGE_PATH / STATE_SPLIT_PATH: パスの結合相手・分割点の選択モード。
    /// ipcParams の path_id はパス詳細パネルで開いている操作対象パス
    /// (結合では survivor、分割では分割元)。
    ///
    /// 表示は道路敷設モードと同じ「計画表示・地区非表示」に揃え、対象パスと候補パスの
    /// 色分け・分割点マーカーは PathTopologyEditManager が担う。
    /// </summary>
    private void ApplyStatePathTopologyEdit(string state, Dictionary<string, object> ipcParams)
    {
        bool isMerge = state == "STATE_MERGE_PATH";

        int pathId = 0;
        if (ipcParams != null && ipcParams.TryGetValue("path_id", out object pidObj) && pidObj != null)
        {
            pathId = Convert.ToInt32(pidObj);
        }
        if (pathId <= 0)
        {
            Debug.LogWarning($"IpcStateCoordinator: {state} に path_id が指定されていません");
            return;
        }

        // InputMode: 結合はパスのみ、分割はパス＋地形(パス脇からも分割点を指せる)を拾う
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(
                isMerge ? InputModePreset.PATH_MERGE : InputModePreset.PATH_SPLIT);
        }

        // VisualMode の適用は IpcViewCoordinator.SyncFromState(ステート別の表示プリセット)が行う。

        var manager = PathTopologyEditManager.Instance;
        if (manager == null)
        {
            var go = new GameObject("PathTopologyEditManager");
            manager = go.AddComponent<PathTopologyEditManager>();
        }

        if (isMerge) manager.BeginMergeMode(pathId);
        else manager.BeginSplitMode(pathId);
    }

    /// <summary>
    /// STATE_EDIT_ROAD_MEMBERS: 道路の区間編集モード。
    /// params は {road_id, color, path_ids, session_id}(path_ids は開始時点の所属区間。session_id は
    /// 終了時の edit_road:ended でそのまま返す照合用の値で、省略可)。街路・高速道路の区間だけを
    /// クリック可能にし、クリックは RoadMemberEditManager が edit_road:toggle_path として UI へ送る。
    /// 表示は計画中の区間も選べるよう道路敷設モードと同じ「計画表示」に揃える(SyncFromState)。
    /// </summary>
    private void ApplyStateEditRoadMembers(Dictionary<string, object> ipcParams)
    {
        int roadId = 0;
        if (ipcParams != null && ipcParams.TryGetValue("road_id", out object ridObj) && ridObj != null)
        {
            roadId = Convert.ToInt32(ridObj);
        }
        if (roadId <= 0)
        {
            Debug.LogWarning("IpcStateCoordinator: STATE_EDIT_ROAD_MEMBERS に road_id が指定されていません");
            return;
        }
        string color = ipcParams.TryGetValue("color", out object colorObj) ? colorObj?.ToString() : null;
        int[] pathIds = ipcParams.TryGetValue("path_ids", out object idsObj) ? ReadIntArray(idsObj) : System.Array.Empty<int>();
        object sessionId = ipcParams.TryGetValue("session_id", out object sessionObj) ? ReadSessionId(sessionObj) : null;

        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.ROAD_MEMBER_EDIT);
        }

        // VisualMode の適用は IpcViewCoordinator.SyncFromState(ステート別の表示プリセット)が行う。

        RoadMemberEditManager.Ensure().Begin(roadId, RoadManager.ParseColor(color), pathIds, sessionId);
    }

    /// <summary>
    /// params.session_id を、受け取った JSON の型のまま返せる値にする(整数は long、文字列は string)。
    /// UI は edit_road:ended の session_id を厳密比較するため、数値を文字列に変えたりしない。
    /// 整数・文字列以外(null・小数・配列など)は照合に使えないので null(返すときに省く)。
    /// </summary>
    private static object ReadSessionId(object value)
    {
        if (value is Newtonsoft.Json.Linq.JValue jValue) value = jValue.Value;
        switch (value)
        {
            case string s: return s;
            case long l: return l;
            case int i: return (long)i;
            default: return null;
        }
    }

    /// <summary>
    /// edit_road:cancel {road_id}: 道路詳細パネルの「完了」、またはそのパネルのウィンドウが閉じられた
    /// (閉じたときは、非表示になるだけでアンマウント処理が走らないため Rust 側が送る。
    /// edit_path:cancel_topology_edit と同じ理由)。その道路の区間編集モード中なら打ち切って通常モードへ戻す。
    /// 別の道路の編集中やモード外では何もしない(残ったパネルの古い「完了」や、関係のないパネルを閉じたことで
    /// 道路敷設など他のモードや他の道路の編集を巻き込まないため)。
    /// </summary>
    private void HandleEditRoadCancel(int roadId)
    {
        var manager = RoadMemberEditManager.Instance;
        if (manager == null || !manager.IsActive || manager.RoadId != roadId) return;

        manager.End();
        ReturnToNormalState();
    }

    /// <summary>区間編集モード中の Esc(RoadMemberEditManager から呼ばれる)。通常モードへ戻す。</summary>
    public void ExitRoadMemberEditToNormal()
    {
        var manager = RoadMemberEditManager.Instance;
        if (manager == null || !manager.IsActive) return;

        manager.End();
        ReturnToNormalState();
    }

    /// <summary>
    /// edit_path:finish_topology_edit: パスの結合/分割が確定した(UIがバックエンドへの送信に成功した)。
    ///
    /// 選択モードの終了・結合で消えたパスの即時破棄・通常モードへの復帰・データ再取得を
    /// 1つのハンドラでこの順に行う。`session:reload` と `world:change_state` を別々に送ると、
    /// 非同期の差分リロード(コルーチン)と状態遷移が前後して表示が更新されないことがあるため、
    /// 道路敷設の `edit_path:finish` と同じく1イベントにまとめてある。
    ///
    /// removedPathId は結合で吸収されて消えたパスID(分割時は0)。差分リロードの
    /// 「レスポンスに含まれないパスを消す」判定を待たずに確実に消すために明示的に渡す。
    /// </summary>
    private void HandleEditPathFinishTopologyEdit(int removedPathId)
    {
        PathTopologyEditManager.Instance?.EndMode();

        if (removedPathId > 0)
        {
            FindAnyObjectByType<PathManager>()?.RemovePathLocally(removedPathId);
        }

        ReturnToNormalState();

        // 再取得は最後に開始する(この後に状態遷移が割り込むと、進行中の差分リロードの
        // コルーチンが取り消されて削除・再生成が中途半端に終わる)。
        CellDataLoader.Instance?.ReloadAllData();
    }

    /// <summary>
    /// edit_path:cancel_topology_edit: パス詳細パネルのウィンドウが閉じられた。
    /// 結合/分割の対象選択モード中なら打ち切って通常モードへ戻す。
    ///
    /// パネルのウィンドウは閉じても破棄されず非表示(再利用待ち)になるだけで React の
    /// アンマウント処理が走らないため、パネル側の後始末だけに任せると選択モードのまま
    /// 入力が固まる。選択モード中でなければ何もしない(ブルドーザ等、他のモードを
    /// 巻き込まないため専用イベントにしてある)。
    /// </summary>
    private void HandleEditPathCancelTopologyEdit()
    {
        var manager = PathTopologyEditManager.Instance;
        if (manager == null || !manager.IsActive) return;

        manager.EndMode();
        ReturnToNormalState();
    }

    /// <summary>
    /// STATE_EDIT_PATH: 道路敷設モード
    /// ipcParams には UI側で選択された path_type / path_variant / path_id が含まれる。
    /// path_id が指定されている場合は既存パスの編集モードとして動作する。
    /// </summary>
    private void ApplyStateEditPath(Dictionary<string, object> ipcParams)
    {
        // InputMode: 道路敷設プリセット
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.ROAD_CONSTRUCTION);
        }

        // VisualMode の適用は IpcViewCoordinator.SyncFromState(ステート別の表示プリセット)が行う。

        // RoadPlanManagerを道路敷設モードで初期化
        var roadPlanManager = FindAnyObjectByType<RoadPlanManager>();
        if (roadPlanManager != null)
        {
            // IPCパラメータからpath_type / path_variant / path_idを読み取って設定する
            int? pathId = null;
            // 複合パス(軌道複線)の各線横オフセット・進行方向。paramsに無ければ単一パス(空)へ必ず戻す
            // (前回の敷設セッションのオフセットが残ると道路等でも複線プレビューされてしまう)。
            float[] lateralOffsets = System.Array.Empty<float>();
            int[] travelDirections = System.Array.Empty<int>();
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
                // 複合パス(軌道複線)の各線横オフセット・進行方向。敷設モード初期化時の初期値として渡す
                // (以後の線路数・進行方向の変更は edit_path:change_lateral_offsets で逐次反映される)。
                if (ipcParams.TryGetValue("lateral_offsets", out object loObj) && loObj != null)
                {
                    lateralOffsets = ReadFloatArray(loObj);
                    Debug.Log($"IpcStateCoordinator: lateral_offsets = {lateralOffsets.Length}本");
                }
                if (ipcParams.TryGetValue("travel_directions", out object tdObj) && tdObj != null)
                {
                    travelDirections = ReadIntArray(tdObj);
                }
            }
            roadPlanManager.HouseEnds = ipcParams != null && ipcParams.TryGetValue("house_ends", out var he)
                ? Convert.ToString(he) : "both";
            roadPlanManager.PassageMode = ipcParams != null && ipcParams.TryGetValue("passage_mode", out var pm)
                ? Convert.ToString(pm) : "elevated";
            roadPlanManager.LateralOffsets = lateralOffsets;
            roadPlanManager.TravelDirections = travelDirections;

            roadPlanManager.InitializeRoadPlacement(pathId,
                ipcParams != null && ipcParams.TryGetValue("initial_height", out var ih) ? System.Convert.ToSingle(ih) : (float?)null,
                ipcParams != null && ipcParams.TryGetValue("initial_embankment_height", out var ie) ? System.Convert.ToSingle(ie) : (float?)null,
                ipcParams != null && ipcParams.TryGetValue("initial_curved", out var ic) ? System.Convert.ToBoolean(ic) : (bool?)null,
                ipcParams != null && ipcParams.TryGetValue("initial_follow_terrain", out var it) ? System.Convert.ToInt32(it) : (int?)null);
        }
    }

    /// <summary>
    /// STATE_EDIT_BUILDING: 建物建設モード
    /// ipcParams には UI側で選択された building_variant / agent_id が含まれる。
    /// テンプレート作成モードの観光名所では landmark(観光名所の種別)も含まれる。
    /// 任意で placement_mode(配置方法の初期値。parcel/free/roadside)を含む。
    /// agent_id が指定されている場合は既存建物の編集モードとして動作する。
    ///
    /// パーセル表示は、選択された建物のbd_typeに対応する建設可能フィルタモード
    /// （buildable_color）に設定され、建設可能な土地区画のみがハイライトされる。
    /// </summary>
    private void ApplyStateEditBuilding(Dictionary<string, object> ipcParams)
    {
        int? agentId = null;
        if (ipcParams != null && ipcParams.TryGetValue("agent_id", out object aidObj) && aidObj != null)
        {
            agentId = Convert.ToInt32(aidObj);
        }

        // InputMode: 建物建設プリセット
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.BUILDING_CONSTRUCTION);
        }

        // VisualMode の適用は IpcViewCoordinator.SyncFromState(ステート別の表示プリセット)が行う。
        // 建設可能な区画のハイライトも、選択中の建物種類から解決したフィルタを
        // そちらが区画レイヤーのサブモードとして設定する。

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
                // 観光名所の種別(0=観光名所ではない)。名所ごとにプレビューのパーツフォルダが変わる。
                if (ipcParams.TryGetValue("landmark", out object lmObj) && lmObj != null)
                {
                    buildingPlanManager.SetLandmark(Convert.ToInt32(lmObj));
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
            buildingPlanManager.DirectAllowed = !agentId.HasValue &&
                !(ipcParams != null && ipcParams.TryGetValue("copy_source_agent_id", out var copyId) && copyId != null);

            // 配置方法の初期値(placement_mode: parcel/free/roadside)。省略時は区画配置。
            // UIが状況に応じて指定する(都市創設時の市庁舎は道路も区画も無いのが通常なので自由配置から始める)。
            // SetPlacementMode は PlacementActive かつ DirectAllowed を要求するため、必ず両者が揃ったこの位置で適用する。
            if (ipcParams != null && ipcParams.TryGetValue("placement_mode", out object pmObj) && pmObj is string pmText &&
                Enum.TryParse(pmText, true, out BuildingPlacementMode initialMode))
            {
                buildingPlanManager.SetPlacementMode(initialMode);
            }
        }
    }

    /// <summary>
    /// STATE_EDIT_PARCEL: 土地区画作成モード
    /// ipcParams には UI側で選択された parcel_creation_mode が含まれる(省略時は既定モード)。
    /// 以後の作成方法の切り替えは3DビューのCtrlラジアルメニュー(ParcelEditControlMenu)が行い、
    /// このステートは「作成モードに入る」ことと表示モードの設定だけを担う。
    /// </summary>
    private void ApplyStateEditParcel(Dictionary<string, object> ipcParams)
    {
        string creationMode = ParcelEditorManager.DefaultMode;
        if (ipcParams != null && ipcParams.TryGetValue("parcel_creation_mode", out object modeObj) && modeObj != null)
        {
            creationMode = modeObj.ToString();
        }

        // VisualMode の適用は IpcViewCoordinator.SyncFromState(ステート別の表示プリセット)が行う。

        // 作成モードの適用(入力プリセットの切り替え・選択状態のリセット・任意ポリゴンの初期化)は
        // 真実源である ParcelEditorManager に集約されている。Unity側のラジアルメニューからの
        // 切り替えとまったく同じ経路を通す(片方だけ直す実装差を作らないため)。
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager == null)
        {
            Debug.LogError("IpcStateCoordinator: ParcelEditorManagerが見つかりません");
            return;
        }
        parcelEditorManager.EnterCreationMode(creationMode);

        // 作成モード切替のCtrlラジアルメニュー+カーソルHUDのドライバを同じGameObjectに常駐させる
        // (ドライバは IsActive をポーリングして自律的に開閉するため、ここでは初期化のみ行えばよい)。
        var controlMenu = parcelEditorManager.GetComponent<ParcelEditControlMenu>();
        if (controlMenu == null)
        {
            controlMenu = parcelEditorManager.gameObject.AddComponent<ParcelEditControlMenu>();
        }
        controlMenu.Initialize(parcelEditorManager);

        Debug.Log($"IpcStateCoordinator: 土地区画作成モード -> {creationMode}");
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
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetFlags(InputObjectType.PATH, VisualFlag.SHOW_PLAN);
            VisualModeHandler.Instance.SetFlags(InputObjectType.BUILDING, VisualFlag.SHOW_PLAN);
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
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetFlags(InputObjectType.PATH, VisualFlag.SHOW_PLAN);
            VisualModeHandler.Instance.SetFlags(InputObjectType.BUILDING, VisualFlag.SHOW_PLAN);
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
    /// STATE_EDIT_GRADING: 造成範囲の作図モード(新規作成)。params は無い。
    /// 陸地整地か埋立かと目標の高さは、描いた範囲からバックエンドが決める。
    /// Unity は範囲を edit_grading:update_shape で送るだけで、見積もりと発注は UI が行う。
    /// </summary>
    private void ApplyStateEditGrading(Dictionary<string, object> ipcParams)
    {
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.EDIT_GRADING);
        }

        // VisualMode の適用は IpcViewCoordinator.SyncFromState(ステート別の表示プリセット)が行う。

        var gradingAreaEditManager = FindAnyObjectByType<GradingAreaEditManager>();
        if (gradingAreaEditManager == null)
        {
            var go = new GameObject("GradingAreaEditManager");
            gradingAreaEditManager = go.AddComponent<GradingAreaEditManager>();
        }
        gradingAreaEditManager.InitializeCreateMode();

        Debug.Log("IpcStateCoordinator: 造成範囲の作図モードを適用しました");
    }

    /// <summary>
    /// edit_grading:finish: 造成の発注が確定した(UIがバックエンドへの送信に成功した)。
    /// 作図の終了・通常モードへの復帰・データ再取得をこの順に1つのハンドラで行う
    /// (edit_path:finish_topology_edit と同じ理由)。発注の時点では地形も海岸線も変わらないので、
    /// 海岸線は取り直さない。作図中でなければ(別の編集モードへ移った後に届いた場合)
    /// そのモードを巻き込まないよう、再取得だけを行う。
    /// UI は確定の後に造成ウィンドウを閉じるので、Rust のウィンドウを閉じる処理(× と同じ経路)から
    /// 冗長な STATE_NORMAL が続けて届く。ここで既に通常ステートへ戻しており、STATE_NORMAL の処理は
    /// 表示と入力を適用し直すだけで差分リロードを取り消さないので害は無い。
    /// </summary>
    private void HandleEditGradingFinish()
    {
        var gradingAreaEditManager = FindAnyObjectByType<GradingAreaEditManager>();
        if (gradingAreaEditManager != null && gradingAreaEditManager.IsEditMode)
        {
            gradingAreaEditManager.FinishEditMode();
            ReturnToNormalState();
        }

        // 再取得は最後に開始する(通常モードへの復帰の後に置き、このハンドラの中で再取得と状態遷移が
        // 前後しないようにする)。差分リロードを取り消すのは、新しい再取得・カメラのセル移動・セッションの
        // 切り替えだけで、後から届く STATE_NORMAL は取り消さない。
        CellDataLoader.Instance?.ReloadAllData();
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

        // VisualMode の適用は IpcViewCoordinator.SyncFromState(ステート別の表示プリセット)が行う。

        int pathVariant = 0;
        int[] stopPathVariants = System.Array.Empty<int>();
        int[] routePathVariants = System.Array.Empty<int>();
        var points = new List<TransitPlanManager.RoutePoint>();
        int lineAgentId = -1;
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
            if (ipcParams.TryGetValue("route_path_variants", out object rpvObj) && rpvObj != null)
            {
                routePathVariants = ReadIntArray(rpvObj);
            }
            if (ipcParams.TryGetValue("points", out object pointsObj) && pointsObj != null)
            {
                points = ReadTransitPoints(pointsObj);
            }
            if (ipcParams.TryGetValue("line_agent_id", out object lineObj) && lineObj != null)
            {
                lineAgentId = Convert.ToInt32(lineObj);
            }
        }

        // TransitPlanManager を路線設置モードで初期化(無ければ動的生成)。経路編集では現在の巡回点から始める。
        var transitPlanManager = FindAnyObjectByType<TransitPlanManager>();
        if (transitPlanManager == null)
        {
            var go = new GameObject("TransitPlanManager");
            transitPlanManager = go.AddComponent<TransitPlanManager>();
        }
        transitPlanManager.InitializeTransitPlacement(pathVariant, stopPathVariants, routePathVariants, points, lineAgentId);

        Debug.Log($"IpcStateCoordinator: 公共交通設置モード -> path_variant={pathVariant}, stop_variants={stopPathVariants.Length}, points={points.Count}, line={lineAgentId}");
    }

    /// <summary>
    /// STATE_EDIT_TRANSIT の points(巡回点 [{kind, position:[x,y,z]}] の配列)を読む。形式の崩れた要素は飛ばす。
    /// </summary>
    private static List<TransitPlanManager.RoutePoint> ReadTransitPoints(object value)
    {
        var result = new List<TransitPlanManager.RoutePoint>();
        if (value is not Newtonsoft.Json.Linq.JArray array) return result;
        foreach (var token in array)
        {
            if (token is not Newtonsoft.Json.Linq.JObject item) continue;
            if (item["position"] is not Newtonsoft.Json.Linq.JArray position || position.Count < 3) continue;
            var world = new Vector3((float)position[0], (float)position[1], (float)position[2]);
            result.Add(new TransitPlanManager.RoutePoint(world, (string)item["kind"] == "waypoint"));
        }
        return result;
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
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetFlags(InputObjectType.PATH, VisualFlag.SHOW_PLAN);
            VisualModeHandler.Instance.SetFlags(InputObjectType.BUILDING, VisualFlag.SHOW_PLAN);
            VisualModeHandler.Instance.SetMode(InputObjectType.ZONE, VisualMode.MODE_HIDDEN);
        }

        int propType = 0;
        string placementMode = "instance";
        float height = 0f;
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
            if (ipcParams.TryGetValue("height", out object hObj) && hObj != null)
                height = Convert.ToSingle(hObj);
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
        propPlacementManager.InitializePlacement(placementMode, propType, rotation, scale, density, seed, height);

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
        int agentId = 0;
        if (ipcParams != null)
        {
            if (ipcParams.TryGetValue("infra_node_id", out object inObj) && inObj != null)
                infraNodeId = Convert.ToInt32(inObj);
            if (ipcParams.TryGetValue("depot_node_id", out object dnObj) && dnObj != null)
                depotNodeId = Convert.ToInt32(dnObj);
            // 施設ID。端子の可動範囲(施設が立地する土地区画)の解決に使う。
            if (ipcParams.TryGetValue("agent_id", out object agObj) && agObj != null)
                agentId = Convert.ToInt32(agObj);
        }

        // InputMode: 接続点編集プリセット（NODE=MODE_EDIT、他=MODE_NONE）
        if (InputSystemManager.Instance != null)
        {
            InputSystemManager.Instance.SetModePreset(InputModePreset.CONNECTOR_EDIT);
        }

        GetOrCreateConnectorEditManager().EnterEditMode(infraNodeId, depotNodeId, agentId);
        Debug.Log($"IpcStateCoordinator: 接続点編集モード -> infra_node={infraNodeId}, depot_node={depotNodeId}, agent={agentId}");
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
    /// edit_connector:hide: 経済主体詳細パネルが閉じた／別エージェントに切り替わった。ハイライトを消し、
    /// 接続点編集モード中なら通常モードへ戻す。
    ///
    /// 詳細パネルは建物建設などの編集ウィンドウと同時に開けるので、別のステートへ移った後に届いたときは
    /// ハイライトを消すだけにする(素の STATE_NORMAL だと後から開いた編集ウィンドウのステートまで打ち切る。
    /// edit_path:cancel_topology_edit と同じ理由)。
    /// </summary>
    private void HandleEditConnectorHide()
    {
        var mgr = ConnectorEditManager.Instance;
        if (mgr == null) return;

        bool wasEditing = mgr.IsEditMode;
        mgr.Hide();
        if (wasEditing) ReturnToNormalState();
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
    /// ipcParams から取り出した値(JArray または IEnumerable)を float 配列へ変換する。
    /// ReadIntArray の float 版(lateral_offsets のように小数を含む配列に使う)。
    /// </summary>
    private static float[] ReadFloatArray(object value)
    {
        if (value is Newtonsoft.Json.Linq.JArray jArr)
        {
            var list = new List<float>(jArr.Count);
            foreach (var token in jArr)
            {
                list.Add((float)token);
            }
            return list.ToArray();
        }
        if (value is System.Collections.IEnumerable en && !(value is string))
        {
            var list = new List<float>();
            foreach (var item in en)
            {
                if (item != null) list.Add(Convert.ToSingle(item, System.Globalization.CultureInfo.InvariantCulture));
            }
            return list.ToArray();
        }
        return System.Array.Empty<float>();
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
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetFlags(InputObjectType.PATH, VisualFlag.SHOW_PLAN);
            VisualModeHandler.Instance.SetFlags(InputObjectType.BUILDING, VisualFlag.SHOW_PLAN);
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
            VisualModeHandler.Instance.SetMode(InputObjectType.PATH, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetMode(InputObjectType.BUILDING, VisualMode.MODE_NORMAL);
            VisualModeHandler.Instance.SetFlags(InputObjectType.PATH, VisualFlag.SHOW_PLAN);
            VisualModeHandler.Instance.SetFlags(InputObjectType.BUILDING, VisualFlag.SHOW_PLAN);
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
    /// edit_parcel:reset_selection: 作成モードを維持したまま選択とプレビューだけを捨てる。
    /// UIが作成を確定した直後・「クリア」ボタン押下時に送られ、3Dビューをそのまま
    /// クリックし続けて次の区画を作れるようにする(1回作るたびにモードを抜けないための要)。
    /// </summary>
    private void HandleEditParcelResetSelection()
    {
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager == null) return;
        parcelEditorManager.ResetSelection();

        // 確定・クリアのボタンはTauriのパネル側にあるため、押した時点でフォーカスはパネルにある。
        // 次の操作は3Dビューのクリックなので、ステート遷移時と同じくUnityを前面へ戻して
        // 「押した直後にそのままクリックで次を作る」流れを切らさない。
        WindowUtil.BringToForeground();
    }

    /// <summary>
    /// edit_parcel:set_rect_params: 作成パネルの幅・奥行きの入力値を受け取る。
    /// 角度ドラッグ中のローカル矩形プレビューをパネルの設定値に合わせるためだけに使う
    /// (確定形状はバックエンドのプレビューが正)。
    /// </summary>
    private void HandleEditParcelSetRectParams(float width, float height)
    {
        var parcelEditorManager = FindAnyObjectByType<ParcelEditorManager>();
        if (parcelEditorManager == null) return;
        parcelEditorManager.SetRectPreviewSize(width, height);
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

        // 道路敷設モード中のリセットで進行方向矢印の表示フラグが残らないようにする
        // （リセット後に再生成された線路の矢印が通常表示に出てしまうのを防ぐ）。
        TrackDirectionArrows.SetVisible(false);

        // オブジェクト単位の表示フィルタは建物ID・パスIDの集合なので、別の都市を
        // 読み込むと全く別のオブジェクトを指してしまう。セッションの切り替えでは必ず捨てる。
        ObjectVisibilityFilter.Clear();

        // 道路の強調・区間編集もパスIDの集合なので、別の都市では別の区間を指してしまう。
        RoadMemberEditManager.Instance?.End();
        RoadFocusOverlay.Instance?.ClearAll();
        ServiceCoverageOverlay.Instance?.Clear();

        // カメラ状態のバックエンド送信を止める。新しい都市の準備中に前の視点を PATCH すると、
        // ロード後に適用するはずの保存視点を上書きしてしまうため(game:start で再開する)。
        GameStateManager.Instance?.BeginLoading();
        CameraController.Instance?.SuspendCameraSync();

        // 各データマネージャーおよび内部状態キャッシュ、各種ストアを完全にクリアし、
        // session:reload が届くまでカメラ由来のロードを止める
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ClearAllManagerDataForSessionReset();
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
        // 海岸線・水面・川を取り直してから3D更新を行う。
        var terrainManager = FindAnyObjectByType<TerrainManager>();
        if (terrainManager != null)
        {
            StartCoroutine(FetchCoastlineThenReload(terrainManager));
        }
        else if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ReloadAllData();
        }

        // 経路(route_path_ids)は毎エポック再計算される。session:reload ごとに路線一覧を取り直し、
        // 再ルート/一時停止など再計算結果を公共交通オーバーレイへ反映する。
        TransitOverlayManager.Instance?.RefreshLines();

        // 施設の竣工・新設や建物の増減を効果範囲の表示へ反映する。
        ServiceCoverageOverlay.Instance?.Refresh();
    }

    private System.Collections.IEnumerator FetchCoastlineThenReload(TerrainManager terrainManager)
    {
        yield return terrainManager.FetchCoastline();
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
