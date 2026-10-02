using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// IPC通信の高レベルクライアント。
/// MmfReader（Tauriからの受信）とMmfWriter（Tauriへの送信）を管理し、
/// 型付きのemit/subscribe APIを提供する。
/// MonoBehaviourとして動作し、Updateでメインスレッド上にイベントをディスパッチする。
/// </summary>
public class IpcClient : MonoBehaviour
{
    public static IpcClient Instance { get; private set; }

    /// <summary>
    /// Tauri→Unity方向のMMFリーダー。
    /// </summary>
    private MmfReader _reader;

    /// <summary>
    /// Unity→Tauri方向のMMFライター。
    /// </summary>
    private MmfWriter _writer;

    // ---- 受信イベント（ui -> graphic）----
    public event Action<string, Dictionary<string, object>> OnWorldChangeState;
    // (layer, mode, subMode, flags)。flags は "plan,public" 形式のCSVで、
    // null は「現在のフラグを維持」を意味するため ?? "" で潰さずそのまま渡す。
    public event Action<string, string, string, string> OnViewChangeView;
    // パス敷設パネルからのノード標高・盛土変更。曲線/直線・吸着はUnity側のラジアルメニューが
    // 真実源だが、標高・盛土は既存ノードを選択して数値で微調整する用途があるため、
    // パネルのステッパー(UI→Unity)とUnityキー操作の双方向で編集できるようにしている。
    public event Action<int, float> OnEditPathChangeNode;
    public event Action<int, float> OnEditPathChangeEmbankment;
    public event Action<float[][], int, float[], float, int> OnEditBuildingUpdateShape;
    public event Action<Newtonsoft.Json.Linq.JToken> OnEditBuildingPlacementCommand;
    public event Action<int> OnEditBuildingUpdateFloors;
    public event Action<int> OnBuildingExploded;
    public event Action<float, float, float> OnEditBuildingUpdateVisualVariant;
    // 観光名所(landmark_master のID)の変更。プレビューのパーツ prefix を切り替える。
    public event Action<int> OnEditBuildingUpdateLandmark;
    public event Action<EffectRangePayload> OnEditBuildingUpdateEffectRange;
    // 直接配置モードの矩形パラメータ(width, depth, angle, floors)。UI側の入力変更を受けて、
    // Unityがカーソル追従のリアルタイム矩形ゴーストを描画するために使用する。
    public event Action<float, float, float, int> OnEditBuildingUpdateDirectParams;
    // 既存建物コピー建設: コピー元建物の選択モード開始要求(UI→Unity)。
    public event Action OnBuildingConstructionPickSource;
    public event Action<string, float[][][], float[][]> OnEditParcelUpdatePreview;
    public event Action OnEditParcelResetSelection;
    public event Action<float, float> OnEditParcelSetRectParams;
    public event Action<int, float[][]> OnEditParcelStartShapeEdit;
    public event Action OnEditParcelCancelShapeEdit;
    // 形状編集の終了通知。UI側が PATCH /parcels/{id} の保存に成功した後に送信される
    // (バックエンドへの送信自体はUI側が行う。edit_path:finish と同じ役割)。
    public event Action OnEditParcelFinishShapeEdit;
    // 地区統合の確定/取消の通知(UI→Unity)。UI側が POST /zones/{id}/merge を送り終えた後、
    // または確定パネルでキャンセルされたときに送られる。引数は統合が実行されたかどうか。
    public event Action<bool> OnEditZoneFinishMerge;
    // オブジェクト単位の表示フィルタ(建設プロジェクトのタグ単位／個別の表示切替)。
    // 引数は「非表示にする建物ID配列」「非表示にするパスID配列」で、差分ではなく全置換。
    // 状態はUnityのセッション内にしか存在せず、バックエンドにもセーブにも残さない。
    public event Action<int[], int[]> OnViewSetObjectFilter;
    // 現在の非表示集合の問い合わせ(UIのパネルが開いたときに送られる)。
    public event Action OnViewQueryObjectFilter;
    public event Action<string, float, float, float?, float?> OnViewSetGrid;
    public event Action OnSystemShutdown;
    public event Action OnSessionReset;
    public event Action OnSessionReload;
    public event Action OnSessionForceReload;
    public event Action<int> OnZoneUpdated;
    public event Action OnEditPathFinish;
    // パス結合/分割の対象選択モードの打ち切り。パス詳細パネルのウィンドウが閉じられたときに
    // Rust側(window.rs)が送る。ウィンドウは破棄されず非表示になるだけでReactのアンマウント
    // 処理が走らないため、パネル任せにすると選択モードに入ったまま入力が固まる。
    // 選択モード中でなければ何もしない(他のモードを巻き込まないための専用イベント)。
    public event Action OnEditPathCancelTopologyEdit;
    // パス結合/分割の確定完了通知。引数は結合で消えたパスID(分割時は0)。
    // 選択モードの終了・消えたパスの即時破棄・通常モードへの復帰・データ再取得を
    // 1イベントでまとめて行う(session:reload と world:change_state を別々に送ると、
    // 非同期の差分リロードと状態遷移が競合して表示が更新されないことがある)。
    public event Action<int> OnEditPathFinishTopologyEdit;
    // 駅パスのホーム設置側(Constants.PLATFORM_SIDE_*)の変更。UIパネルのトグルを受けて、
    // 敷設中プレビューのプラットフォーム張り出し側を切り替えるために使用する。
    public event Action<int> OnEditPathChangePlatformSide;
    public event Action<string> OnEditPathChangeHouseEnds;
    public event Action<string> OnEditPathChangePassageMode;
    // 地形追従の個別設定(-1=未設定/0=非追従/1=追従)の変更。UIパネルのトグルを受けて、
    // 敷設中プレビューの地形追従・トンネル可視化を切り替えるために使用する。
    public event Action<int> OnEditPathSetFollowTerrain;
    // 複合パス(軌道複線)の各線横オフセット(m)と進行方向(DIR_*)の変更。UIパネルの
    // 線路数・進行方向の変更を受けて、敷設中プレビューの平行本数・ホーム設置側・
    // 進行方向矢印を切り替えるために使用する。両配列はindex対応。空配列=単一パス。
    public event Action<float[], int[]> OnEditPathChangeLateralOffsets;
    // 2Dパス形状編集パネルからのノード・制御点操作(XZのみ。吸着なし)。
    // 形状の真実源は RoadPlanManager のままで、結果は edit_path:update_shape で全状態を返す。
    public event Action<RoadPlanManager.ShapeCommand> OnEditPathShapeCommand;
    // 2Dパス形状編集パネルが開いた直後の現在形状の問い合わせ。update_shape を1回返す。
    public event Action OnEditPathQueryShape;
    public event Action OnEditResourceFinish;
    public event Action OnEditGradingFinish;
    public event Action OnEditTransitFinish;
    // プロップ配置。change_settings は (rotation?, scale?, density?, seed?) を辞書で渡す。
    public event Action<Dictionary<string, object>> OnEditPropChangeSettings;
    public event Action OnEditPropFinish;
    public event Action OnEditPropReset;
    // ブルドーザの確認付き削除(範囲配置プロップ)に対するUIの回答: (object_type, id, accepted)
    public event Action<string, int, bool> OnRemoveObjectConfirmResult;
    // 新しい都市の読み込みを始めるとき(新規ゲーム・チュートリアル・ロードのすべて)に届く。
    public event Action OnGameLoading;
    public event Action OnGameStart;
    public event Action OnGameEnd;
    // 音量は設定変更時とUnity起動通知(system:graphic_ready)への応答でしか届かないため、
    // 購読より先に届いた値を取りこぼすと次の設定変更まで無音になる。購読時に受信済みの最新値を即時通知する。
    private Action<float> _onAmbienceVolume;
    private float? _ambienceVolume;
    public event Action<float> OnAmbienceVolume
    {
        add
        {
            _onAmbienceVolume += value;
            if (_ambienceVolume.HasValue) value(_ambienceVolume.Value);
        }
        remove { _onAmbienceVolume -= value; }
    }
    public event Action OnWeatherQuery;
    public event Action<string> OnWeatherSet;
    // エポック演出の開始(true)と結果画面クローズ時の終了(false)。日没→暗転→日の出に使う。
    public event Action<bool> OnEpochExecuting;
    public event Action<string> OnEpochPrepare;
    // セーブ・シナリオ保存の直前の撮影要求(引数は要求ID)。撮った画像はコアへ直接送る。
    public event Action<string> OnCameraCaptureThumbnail;
    // 接道(front_path)の矢印表示。土地区画詳細パネルの開閉に対応する。
    public event Action<int> OnParcelShowFrontPath;
    public event Action OnParcelHideFrontPath;
    // 接続端子ノード(infra_node_id / depot_node_id)のハイライト表示。
    // AgentDetailPanel の開閉・表示エージェント変更に対応する（読み取り専用の強調表示）。
    public event Action<int, int, int> OnEditConnectorShow;
    public event Action OnEditConnectorHide;
    // 計画中パスが干渉する区画・建物・既存パスのハイライト表示。
    // 敷設の形状確定/確定ボタン、着工前パスの詳細パネル表示に対応する。
    // 第1引数はハイライトの発信元(source/windowType)。ソースごとに独立レイヤーで扱う。
    public event Action<string, PathInterferencePayload> OnWorldHighlightObjects;
    public event Action<string> OnWorldClearHighlights;
    // 道路詳細パネルの強調表示(road_id, color "#rrggbb", path_ids)。送られるたびに全置換する。
    public event Action<int, string, int[]> OnWorldSetRoadFocus;
    // 強調の解除(road_id)。road_id が無い(0)ときは無条件に消し、あるときはその道路の強調だけを消す。
    public event Action<int> OnWorldClearRoadFocus;
    // 道路の作成・改名・所属変更・削除の直後に、道路一覧(道路名ラベル)を取り直させる。
    public event Action OnWorldRefreshRoads;
    // 区間編集モード中の所属区間と追加候補の全集合(road_id, path_ids, preview_path_ids)。
    public event Action<int, int[], int[]> OnEditRoadSetMembers;
    // 区間編集の終了要求(road_id)。パネルの「完了」と、ウィンドウを閉じたときの Rust(window.rs)が送る。
    // その道路の区間編集モード中でなければ何もしない。
    public event Action<int> OnEditRoadCancel;

    /// <summary>
    /// Tauriからsystem.readyを受信済みかどうか。
    /// </summary>
    public bool IsConnected { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;

        // GameStateManager が先に生成されている場合、Awake 時点で購読を促す。
        // 生成順序が不定なため、双方向に購読を試行する。
        GameStateManager.Instance?.SubscribeIpcEvents();
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
        Disconnect();
    }

    /// <summary>
    /// IPCトランスポートを初期化する。
    /// MMFと同期オブジェクトをCreateOrOpenで開き、リーダーを起動する。
    /// </summary>
    public void Initialize()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        // Tauriからのメッセージを受信するリーダーを起動
        _reader = new MmfReader(
            "genesis-ipc-ui-to-graphic",
            "genesis-ipc-mutex-utg",
            "genesis-ipc-event-utg"
        );
        _reader.OnMessageReceived += HandleIncomingMessage;
        _reader.Start();

        // Unity→Tauri方向のライターを準備
        _writer = new MmfWriter(
            "genesis-ipc-graphic-to-ui",
            "genesis-ipc-mutex-gtu",
            "genesis-ipc-event-gtu"
        );

        Debug.Log("IpcClient: MmfReader を起動し、MmfWriter を準備しました");
#else
        Debug.LogWarning("IpcClient: このプラットフォームではIPCはサポートされていません");
#endif
    }

    /// <summary>
    /// IPC接続を切断する。
    /// 閉じる前に system:graphic_stopped を送り、Tauri 側が Unity を未接続扱いに戻せるようにする
    /// (Editor の Play 停止も OnApplicationQuit 経由でここに来る。クラッシュ時は送れない)。
    /// </summary>
    public void Disconnect()
    {
        EmitGraphicStopped();

        if (_reader != null)
        {
            _reader.OnMessageReceived -= HandleIncomingMessage;
            _reader.Dispose();
            _reader = null;
        }

        _writer?.Dispose();
        _writer = null;
        IsConnected = false;

        Debug.Log("IpcClient: IPC接続を切断しました");
    }

    private void Update()
    {
        _reader?.ProcessReceivedMessages();
    }

    /// <summary>
    /// Unity がフォアグラウンドになった瞬間を Tauri に知らせる。
    /// フルスクリーン扱いの Unity が活性化するとシェルが topmost の UI を裏へ回すことがあり、
    /// Tauri 側はこの通知を受けて UI 全体の最前面を打ち直す(「Unity をクリックすれば
    /// UI が戻る」復帰経路)。フォーカス喪失側は通知しない。
    /// </summary>
    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus) return;
        SendEnvelope("system:focus_gained", new Dictionary<string, object>());
    }

    // ======================================================================
    // 受信メッセージのディスパッチ
    // ======================================================================

    private void HandleIncomingMessage(string json)
    {
        try
        {
            IpcEnvelope envelope = IpcEnvelope.FromJson(json);
            if (envelope == null)
            {
                Debug.LogWarning($"IpcClient: エンベロープのパースに失敗: {json}");
                return;
            }

            Debug.Log($"IpcClient: メッセージ受信 event={envelope.@event} src={envelope.source}");

            DispatchIncomingEvent(envelope);
        }
        catch (Exception e)
        {
            Debug.LogError($"IpcClient: メッセージ処理エラー: {e.Message}\nJSON: {json}");
        }
    }

    public bool ProtocolCompatible { get; private set; }

    private void DispatchIncomingEvent(IpcEnvelope envelope)
    {
        string eventName = envelope.@event;

        // ui.action イベントの場合、payload内の action フィールドでルーティング
        if (eventName == "ui:action")
        {
            DispatchUiAction(envelope.payload);
            return;
        }

        switch (eventName)
        {
            case "system:ready":
                var version = Newtonsoft.Json.Linq.JObject.FromObject(envelope.payload);
                ProtocolCompatible = (int?)version["ipc_contract_version"] == FrontendVersion.IpcContract
                    && (bool?)version["accepted"] != false;
                IsConnected = ProtocolCompatible;
                if (!ProtocolCompatible) {
                    GameStateManager.Instance?.SetGameStarted(false, null);
                    Debug.LogError("IPC_CONTRACT_MISMATCH");
                }
                if (version["accepted"] == null && BackendCompatibility.Compatible) EmitGraphicReady();
                Debug.Log("IpcClient: system:ready を受信しました");
                break;

            case "system:pong":
                Debug.Log("IpcClient: system:pong を受信しました");
                break;

            case "ui:error":
                Debug.LogWarning($"IpcClient: ui:error を受信: {envelope.payload}");
                break;

            default:
                Debug.LogWarning($"IpcClient: 未知のイベント: {eventName}");
                break;
        }
    }

    private void DispatchUiAction(object payloadObj)
    {
        if (payloadObj == null) return;

        try
        {
            string payloadJson = Newtonsoft.Json.JsonConvert.SerializeObject(payloadObj);
            var payload = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(payloadJson);
            if (payload == null) return;

            string action = payload.ContainsKey("action") ? payload["action"]?.ToString() : null;
            if (action == null) return;
            if ((!ProtocolCompatible || !BackendCompatibility.Compatible)
                && action != "app:quit" && action != "system:shutdown") return;

            switch (action)
            {
                case "tutorial:apply":
                    TutorialRuntime.ApplyFromIpc(GetParamsDictionary(payload));
                    break;
                case "world:change_state":
                    {
                        string state = GetParamString(payload, "state");
                        if (state != null)
                        {
                            Debug.Log($"IpcClient: world:change_state -> {state}");
                            var ipcParams = GetParamsDictionary(payload);
                            OnWorldChangeState?.Invoke(state, ipcParams);
                        }
                    }
                    break;

                case "view:change_view":
                    {
                        string layer = GetParamString(payload, "layer");
                        string mode = GetParamString(payload, "mode");
                        string subMode = GetParamString(payload, "sub_mode");
                        string flags = GetParamString(payload, "flags");
                        if (layer != null && mode != null)
                        {
                            Debug.Log($"IpcClient: view:change_view -> layer={layer}, mode={mode}, sub_mode={subMode}, flags={flags ?? "(keep)"}");
                            OnViewChangeView?.Invoke(layer, mode, subMode ?? "", flags);
                        }
                    }
                    break;

                case "view:apply_preset":
                    {
                        // 専用の編集ステートを持たないウィンドウが、開いている間だけ表示を
                        // 差し替えるためのプリセット指定。元の表示への復帰は STATE_NORMAL が担う。
                        string preset = GetParamString(payload, "preset");
                        Debug.Log($"IpcClient: view:apply_preset -> {preset}");
                        if (preset != null)
                        {
                            var viewCoordinator = UnityEngine.Object.FindAnyObjectByType<IpcViewCoordinator>();
                            if (viewCoordinator != null)
                            {
                                viewCoordinator.ApplyPreset(preset);
                            }
                        }
                    }
                    break;

                case "view:restore_preset":
                    {
                        // 表示プリセットの解除通知。
                        // 詳細パネルなど編集ステートを持たないウィンドウが閉じた際に、
                        // STATE_NORMAL を送らずに自身のプリセットのみを安全に解除する。
                        string preset = GetParamString(payload, "preset");
                        Debug.Log($"IpcClient: view:restore_preset -> {preset}");
                        if (preset != null)
                        {
                            var viewCoordinator = UnityEngine.Object.FindAnyObjectByType<IpcViewCoordinator>();
                            if (viewCoordinator != null)
                            {
                                viewCoordinator.RestorePreset(preset);
                            }
                        }
                    }
                    break;

                case "view:set_object_filter":
                    {
                        int[] hiddenBuildings = GetParamInt1D(payload, "hidden_building_ids");
                        int[] hiddenPaths = GetParamInt1D(payload, "hidden_path_ids");
                        Debug.Log($"IpcClient: view:set_object_filter -> buildings={hiddenBuildings.Length}, paths={hiddenPaths.Length}");
                        OnViewSetObjectFilter?.Invoke(hiddenBuildings, hiddenPaths);
                    }
                    break;

                case "view:query_object_filter":
                    Debug.Log("IpcClient: view:query_object_filter");
                    OnViewQueryObjectFilter?.Invoke();
                    break;

                case "edit_path:change_node":
                    {
                        int nodeIndex = GetParamInt(payload, "node_index");
                        float height = GetParamFloat(payload, "height");
                        Debug.Log($"IpcClient: edit_path:change_node -> index={nodeIndex}, height={height}");
                        OnEditPathChangeNode?.Invoke(nodeIndex, height);
                    }
                    break;

                case "edit_path:change_embankment":
                    {
                        int nodeIndex = GetParamInt(payload, "node_index");
                        float embankmentHeight = GetParamFloat(payload, "embankment_height");
                        Debug.Log($"IpcClient: edit_path:change_embankment -> index={nodeIndex}, embankmentHeight={embankmentHeight}");
                        OnEditPathChangeEmbankment?.Invoke(nodeIndex, embankmentHeight);
                    }
                    break;

                case "edit_path:finish":
                    {
                        Debug.Log("IpcClient: edit_path:finish received");
                        OnEditPathFinish?.Invoke();
                    }
                    break;

                case "edit_path:cancel_topology_edit":
                    {
                        Debug.Log("IpcClient: edit_path:cancel_topology_edit received");
                        OnEditPathCancelTopologyEdit?.Invoke();
                    }
                    break;

                case "edit_path:finish_topology_edit":
                    {
                        int removedPathId = GetParamInt(payload, "removed_path_id");
                        Debug.Log($"IpcClient: edit_path:finish_topology_edit -> removed_path_id={removedPathId}");
                        OnEditPathFinishTopologyEdit?.Invoke(removedPathId);
                    }
                    break;

                case "edit_path:change_passage_mode":
                    OnEditPathChangePassageMode?.Invoke(GetParamString(payload, "passage_mode"));
                    break;
                case "edit_path:change_house_ends":
                    OnEditPathChangeHouseEnds?.Invoke(GetParamString(payload, "house_ends"));
                    break;
                case "edit_path:change_platform_side":
                    {
                        int platformSide = GetParamInt(payload, "platform_side");
                        Debug.Log($"IpcClient: edit_path:change_platform_side -> platform_side={platformSide}");
                        OnEditPathChangePlatformSide?.Invoke(platformSide);
                    }
                    break;

                case "edit_path:set_follow_terrain":
                    {
                        int followTerrain = GetParamInt(payload, "value");
                        Debug.Log($"IpcClient: edit_path:set_follow_terrain -> value={followTerrain}");
                        OnEditPathSetFollowTerrain?.Invoke(followTerrain);
                    }
                    break;

                case "edit_path:change_lateral_offsets":
                    {
                        float[] lateralOffsets = GetParamFloat1D(payload, "lateral_offsets") ?? System.Array.Empty<float>();
                        int[] travelDirections = GetParamInt1D(payload, "travel_directions") ?? System.Array.Empty<int>();
                        Debug.Log($"IpcClient: edit_path:change_lateral_offsets -> count={lateralOffsets.Length}");
                        OnEditPathChangeLateralOffsets?.Invoke(lateralOffsets, travelDirections);
                    }
                    break;

                case "edit_path:shape_command":
                    {
                        var cmd = new RoadPlanManager.ShapeCommand
                        {
                            op = GetParamString(payload, "op"),
                            revision = GetParamString(payload, "revision"),
                            routeJson = GetParamString(payload, "route_json"),
                            alignmentJson = GetParamString(payload, "alignment_json"),
                            index = GetParamInt(payload, "index"),
                            edgeIndex = GetParamInt(payload, "edge_index"),
                            which = GetParamString(payload, "which"),
                            x = GetParamFloat(payload, "x"),
                            z = GetParamFloat(payload, "z"),
                        };
                        Debug.Log($"IpcClient: edit_path:shape_command -> op={cmd.op}, index={cmd.index}, edge={cmd.edgeIndex}");
                        OnEditPathShapeCommand?.Invoke(cmd);
                    }
                    break;

                case "edit_path:query_shape":
                    Debug.Log("IpcClient: edit_path:query_shape");
                    OnEditPathQueryShape?.Invoke();
                    break;

                case "edit_resource:finish":
                    {
                        Debug.Log("IpcClient: edit_resource:finish received");
                        OnEditResourceFinish?.Invoke();
                    }
                    break;

                case "edit_grading:finish":
                    {
                        Debug.Log("IpcClient: edit_grading:finish received");
                        OnEditGradingFinish?.Invoke();
                    }
                    break;

                case "edit_transit:finish":
                    {
                        Debug.Log("IpcClient: edit_transit:finish received");
                        OnEditTransitFinish?.Invoke();
                    }
                    break;

                case "edit_prop:change_settings":
                    {
                        // rotation / scale / density / seed のうち送られてきたものだけを辞書で渡す。
                        var propParams = GetParamsDictionary(payload);
                        OnEditPropChangeSettings?.Invoke(propParams);
                    }
                    break;

                case "edit_prop:finish":
                    {
                        Debug.Log("IpcClient: edit_prop:finish received");
                        OnEditPropFinish?.Invoke();
                    }
                    break;

                case "edit_prop:reset":
                    {
                        Debug.Log("IpcClient: edit_prop:reset received");
                        OnEditPropReset?.Invoke();
                    }
                    break;

                case "remove_object:confirm_result":
                    {
                        // ブルドーザの確認ダイアログの回答。accepted=1 で削除を実行する。
                        string objectType = GetParamString(payload, "object_type");
                        int id = GetParamInt(payload, "id");
                        bool accepted = GetParamInt(payload, "accepted") != 0;
                        Debug.Log($"IpcClient: remove_object:confirm_result -> {objectType}/{id} accepted={accepted}");
                        OnRemoveObjectConfirmResult?.Invoke(objectType, id, accepted);
                    }
                    break;

                case "edit_building:update_shape":
                    {
                        float[][] polygon = GetParamFloat2D(payload, "polygon");
                        int floors = GetParamInt(payload, "floors");
                        float[] centerPoint = GetParamFloat1D(payload, "center_point");
                        float height = GetParamFloat(payload, "height");
                        Debug.Log($"IpcClient: edit_building:update_shape -> floors={floors}, has_center={centerPoint != null}, height={height}");
                        int revision = (int?)GetParamToken(payload, "placement_revision") ?? -1;
                        OnEditBuildingUpdateShape?.Invoke(polygon, floors, centerPoint, height, revision);
                    }
                    break;

                case "edit_building:update_floors":
                    {
                        int floors = GetParamInt(payload, "floors");
                        Debug.Log($"IpcClient: edit_building:update_floors -> {floors}");
                        OnEditBuildingUpdateFloors?.Invoke(floors);
                    }
                    break;

                case "edit_building:update_landmark":
                    {
                        int landmark = GetParamInt(payload, "landmark");
                        Debug.Log($"IpcClient: edit_building:update_landmark -> {landmark}");
                        OnEditBuildingUpdateLandmark?.Invoke(landmark);
                    }
                    break;

                case "edit_building:update_visual_variant":
                    {
                        float vv1 = GetParamFloat(payload, "visual_variant");
                        float vv2 = GetParamFloat(payload, "visual_variant2");
                        float vv3 = GetParamFloat(payload, "visual_variant3");
                        Debug.Log($"IpcClient: edit_building:update_visual_variant -> vv1={vv1}, vv2={vv2}, vv3={vv3}");
                        OnEditBuildingUpdateVisualVariant?.Invoke(vv1, vv2, vv3);
                    }
                    break;

                case "edit_building:update_effect_range":
                    {
                        // インフラ施設の効果範囲(円 or 不定形ポリゴン)を受け取り、3D可視化する。
                        // region=プレビュー領域、existing=既存施設のカバー済み領域一覧。
                        // 両方 null/空 のときはクリア指示。
                        var regionToken = GetParamToken(payload, "region");
                        var existingToken = GetParamToken(payload, "existing");
                        var effectRange = EffectRangePayload.FromJson(regionToken, existingToken);
                        Debug.Log($"IpcClient: edit_building:update_effect_range -> clear={effectRange.IsClear}, existing={effectRange.existing.Count}");
                        OnEditBuildingUpdateEffectRange?.Invoke(effectRange);
                    }
                    break;

                case "edit_building:placement_command":
                    OnEditBuildingPlacementCommand?.Invoke(GetParamToken(payload, "command"));
                    break;

                case "edit_building:update_direct_params":
                    {
                        float width = GetParamFloat(payload, "width");
                        float depth = GetParamFloat(payload, "depth");
                        float angle = GetParamFloat(payload, "angle");
                        int floors = GetParamInt(payload, "floors");
                        OnEditBuildingUpdateDirectParams?.Invoke(width, depth, angle, floors);
                    }
                    break;

                case "building_construction:pick_source":
                    {
                        // 既存建物コピー建設: コピー元建物の選択モード開始要求。
                        Debug.Log("IpcClient: building_construction:pick_source");
                        OnBuildingConstructionPickSource?.Invoke();
                    }
                    break;

                case "edit_parcel:update_preview":
                    {
                        string mode = GetParamString(payload, "mode") ?? "empty_area";
                        float[][][] previewPolygons = GetParamFloat3D(payload, "preview_polygons");
                        float[][] originalPolygon = GetParamFloat2D(payload, "original_polygon");
                        Debug.Log($"IpcClient: edit_parcel:update_preview -> mode={mode}, previewCount={previewPolygons?.Length ?? 0}");
                        OnEditParcelUpdatePreview?.Invoke(mode, previewPolygons, originalPolygon);
                    }
                    break;

                case "edit_parcel:reset_selection":
                    {
                        // 作成の確定後・クリア後に届く「選択だけ捨てて同じモードで続行」指示。
                        // これがあるおかげで1回作るたびに作成モードを抜けずに済む(連続作成)。
                        Debug.Log("IpcClient: edit_parcel:reset_selection");
                        OnEditParcelResetSelection?.Invoke();
                    }
                    break;

                case "edit_parcel:set_rect_params":
                    {
                        // 回転ドラッグ中のローカル矩形プレビューに使う幅・奥行き(パネルの入力値)。
                        float width = GetParamFloat(payload, "width");
                        float height = GetParamFloat(payload, "height");
                        OnEditParcelSetRectParams?.Invoke(width, height);
                    }
                    break;

                case "edit_parcel:start_shape_edit":
                    {
                        int parcelId = GetParamInt(payload, "parcel_id");
                        float[][] polygon = GetParamFloat2D(payload, "polygon");
                        Debug.Log($"IpcClient: edit_parcel:start_shape_edit -> parcel={parcelId}, points={polygon?.Length ?? 0}");
                        OnEditParcelStartShapeEdit?.Invoke(parcelId, polygon);
                    }
                    break;

                case "edit_parcel:cancel_shape_edit":
                    {
                        Debug.Log("IpcClient: edit_parcel:cancel_shape_edit");
                        OnEditParcelCancelShapeEdit?.Invoke();
                    }
                    break;

                case "edit_parcel:finish_shape_edit":
                    {
                        // UI側が PATCH /parcels/{id} の保存に成功した後に届く終了通知。
                        Debug.Log("IpcClient: edit_parcel:finish_shape_edit");
                        OnEditParcelFinishShapeEdit?.Invoke();
                    }
                    break;

                case "edit_zone:finish_merge":
                    {
                        // UI側の地区統合の確定(送信済み)/取消の結果。
                        bool merged = GetParamInt(payload, "merged") != 0;
                        Debug.Log($"IpcClient: edit_zone:finish_merge -> merged={merged}");
                        OnEditZoneFinishMerge?.Invoke(merged);
                    }
                    break;

                case "parcel:show_front_path":
                    {
                        int parcelId = GetParamInt(payload, "parcel_id");
                        Debug.Log($"IpcClient: parcel:show_front_path -> parcel={parcelId}");
                        OnParcelShowFrontPath?.Invoke(parcelId);
                    }
                    break;

                case "parcel:hide_front_path":
                    Debug.Log("IpcClient: parcel:hide_front_path");
                    OnParcelHideFrontPath?.Invoke();
                    break;

                case "world:highlight_objects":
                    {
                        string source = GetParamString(payload, "source") ?? "default";
                        var parcelsToken = GetParamToken(payload, "parcels");
                        int[] buildingIds = GetParamInt1D(payload, "building_ids");
                        int[] collidingPathIds = GetParamInt1D(payload, "colliding_path_ids");
                        var riverIdsToken = GetParamToken(payload, "colliding_river_ids");
                        var interference = PathInterferencePayload.FromParams(
                            parcelsToken, buildingIds, collidingPathIds, riverIdsToken);
                        Debug.Log(
                            $"IpcClient: world:highlight_objects [{source}] -> " +
                            $"parcels(b={interference.BlockingParcels.Count},r={interference.RemovableParcels.Count}," +
                            $"s={interference.ShavableParcels.Count},e={interference.ExemptParcels.Count}), " +
                            $"buildings={interference.BuildingIds.Count}, paths={interference.CollidingPathIds.Count}, " +
                            $"rivers={interference.CollidingRiverIds.Count}");
                        OnWorldHighlightObjects?.Invoke(source, interference);
                    }
                    break;

                case "world:clear_highlights":
                    {
                        string source = GetParamString(payload, "source") ?? "default";
                        Debug.Log($"IpcClient: world:clear_highlights [{source}]");
                        OnWorldClearHighlights?.Invoke(source);
                    }
                    break;

                case "world:set_road_focus":
                    {
                        int roadId = GetParamInt(payload, "road_id");
                        string color = GetParamString(payload, "color");
                        int[] pathIds = GetParamInt1D(payload, "path_ids") ?? Array.Empty<int>();
                        Debug.Log($"IpcClient: world:set_road_focus -> road={roadId}, paths={pathIds.Length}");
                        OnWorldSetRoadFocus?.Invoke(roadId, color, pathIds);
                    }
                    break;

                case "world:clear_road_focus":
                    {
                        int roadId = GetParamInt(payload, "road_id");
                        Debug.Log($"IpcClient: world:clear_road_focus -> road={roadId}");
                        OnWorldClearRoadFocus?.Invoke(roadId);
                    }
                    break;

                case "world:refresh_roads":
                    Debug.Log("IpcClient: world:refresh_roads");
                    OnWorldRefreshRoads?.Invoke();
                    break;

                case "edit_road:set_members":
                    {
                        int roadId = GetParamInt(payload, "road_id");
                        int[] pathIds = GetParamInt1D(payload, "path_ids") ?? Array.Empty<int>();
                        int[] previewPathIds = GetParamInt1D(payload, "preview_path_ids") ?? Array.Empty<int>();
                        Debug.Log($"IpcClient: edit_road:set_members -> road={roadId}, paths={pathIds.Length}, preview={previewPathIds.Length}");
                        OnEditRoadSetMembers?.Invoke(roadId, pathIds, previewPathIds);
                    }
                    break;

                case "edit_road:cancel":
                    {
                        int roadId = GetParamInt(payload, "road_id");
                        Debug.Log($"IpcClient: edit_road:cancel -> road={roadId}");
                        OnEditRoadCancel?.Invoke(roadId);
                    }
                    break;

                case "session:reset":
                    TutorialRuntime.Clear();
                    Debug.Log("IpcClient: session:reset");
                    OnSessionReset?.Invoke();
                    break;

                case "session:reload":
                    Debug.Log("IpcClient: session:reload");
                    OnSessionReload?.Invoke();
                    break;

                case "building:exploded":
                    OnBuildingExploded?.Invoke(GetParamInt(payload, "agent_id"));
                    break;

                case "session:force_reload":
                    Debug.Log("IpcClient: session:force_reload");
                    OnSessionForceReload?.Invoke();
                    break;

                case "zone:updated":
                    {
                        int zoneId = GetParamInt(payload, "zone_id");
                        Debug.Log($"IpcClient: zone:updated -> zone={zoneId}");
                        OnZoneUpdated?.Invoke(zoneId);
                    }
                    break;

                case "view:query_state":
                    Debug.Log("IpcClient: view:query_state");
                    {
                        var coordinator = UnityEngine.Object.FindAnyObjectByType<IpcViewCoordinator>();
                        if (coordinator != null)
                        {
                            coordinator.QueryCurrentState();
                        }
                    }
                    break;

                case "input:query_modes":
                    Debug.Log("IpcClient: input:query_modes");
                    {
                        var debugCoordinator = UnityEngine.Object.FindAnyObjectByType<IpcInputDebugCoordinator>();
                        if (debugCoordinator != null)
                        {
                            debugCoordinator.QueryCurrentModes();
                        }
                    }
                    break;

                case "game:loading":
                    GameStateManager.Instance?.BeginLoading();
                    OnGameLoading?.Invoke();
                    break;
                case "game:reveal":
                    GameStateManager.Instance?.RevealWorld(GetParamInt(payload, "loading_id"));
                    break;
                case "game:start":
                    GameStateManager.Instance?.PrepareInitialLoad(GetParamInt(payload, "loading_id"));
                    Debug.Log("IpcClient: game:start");
                    OnGameStart?.Invoke();
                    break;

                case "game:end":
                    TutorialRuntime.Clear();
                    Debug.Log("IpcClient: game:end");
                    OnGameEnd?.Invoke();
                    break;

                case "ambience:volume":
                    _ambienceVolume = GetParamFloat(payload, "volume");
                    _onAmbienceVolume?.Invoke(_ambienceVolume.Value);
                    break;

                case "weather:query":
                    OnWeatherQuery?.Invoke();
                    break;

                case "drive:open":
                    DriveMode.Ensure().Open(GetParamsDictionary(payload));
                    break;

                case "citizen:follow":
                    CitizenFollow.Ensure().Follow(GetParamInt(payload, "citizen_id"));
                    break;

                case "citizen:unfollow":
                    CitizenFollow.Ensure().Unfollow(GetParamInt(payload, "citizen_id"));
                    break;

                case "weather:set":
                    OnWeatherSet?.Invoke(GetParamString(payload, "id"));
                    break;

                case "epoch:executing":
                    OnEpochExecuting?.Invoke(bool.TryParse(GetParamString(payload, "executing"), out bool executing) && executing);
                    break;

                case "epoch:prepare":
                    OnEpochPrepare?.Invoke(GetParamString(payload, "id"));
                    break;

                case "view:set_grid":
                    {
                        string mode = GetParamString(payload, "mode") ?? "off";
                        float size = GetParamFloat(payload, "size");
                        float angle = GetParamFloat(payload, "angle");
                        float? originX = null;
                        float? originZ = null;
                        bool hasOrigin = false;
                        if (payload.ContainsKey("origin_x")) hasOrigin = true;
                        else if (payload.TryGetValue("params", out object pObj))
                        {
                            if (pObj is Dictionary<string, object> dict && dict.ContainsKey("origin_x")) hasOrigin = true;
                            else if (pObj is Newtonsoft.Json.Linq.JObject jobj && jobj["origin_x"] != null) hasOrigin = true;
                        }
                        if (hasOrigin)
                        {
                            originX = GetParamFloat(payload, "origin_x");
                            originZ = GetParamFloat(payload, "origin_z");
                        }
                        Debug.Log($"IpcClient: view:set_grid -> mode={mode}, size={size}, angle={angle}, origin=({(originX.HasValue ? originX.Value.ToString() : "null")}, {(originZ.HasValue ? originZ.Value.ToString() : "null")})");
                        OnViewSetGrid?.Invoke(mode, size, angle, originX, originZ);
                    }
                    break;

                case "camera:query_state":
                    Debug.Log("IpcClient: camera:query_state");
                    EmitCameraState();
                    break;

                case "camera:capture_thumbnail":
                    Debug.Log("IpcClient: camera:capture_thumbnail");
                    OnCameraCaptureThumbnail?.Invoke(GetParamString(payload, "id"));
                    break;

                case "edit_connector:show":
                    {
                        int agentId = GetParamInt(payload, "agent_id");
                        int infraNodeId = GetParamInt(payload, "infra_node_id");
                        int depotNodeId = GetParamInt(payload, "depot_node_id");
                        Debug.Log($"IpcClient: edit_connector:show -> agent={agentId}, infra_node={infraNodeId}, depot_node={depotNodeId}");
                        OnEditConnectorShow?.Invoke(agentId, infraNodeId, depotNodeId);
                    }
                    break;

                case "edit_connector:hide":
                    Debug.Log("IpcClient: edit_connector:hide");
                    OnEditConnectorHide?.Invoke();
                    break;

                case "system:focus":
                    Debug.Log("IpcClient: system:focus -> BringToForeground");
                    WindowUtil.BringToForeground();
                    break;

                case "system:move_display":
                    {
                        int direction = GetParamInt(payload, "direction");
                        Debug.Log($"IpcClient: system:move_display -> direction={direction}");
                        WindowUtil.MoveToAdjacentDisplay(direction);
                    }
                    break;

                case "system:shutdown":
                    Debug.Log("IpcClient: system:shutdown");
                    OnSystemShutdown?.Invoke();
                    break;

                case "system:backend_connection":
                    ApiClient.UiBackendConnected = GetParamToken(payload, "connected")?.ToObject<bool>() == true;
                    break;

                default:
                    Debug.LogWarning($"IpcClient: 未知のui.action: {action}");
                    break;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"IpcClient: ui.action ディスパッチエラー: {e.Message}");
        }
    }

    // ======================================================================
    // Emit メソッド（graphic -> ui）
    // ======================================================================

    /// <summary>
    /// world.select_object イベントを送信する。
    /// </summary>
    public void EmitSelectObject(string windowType, string entityType, string entityId, float? screenX = null, float? screenY = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["window_type"] = windowType,
            ["entity"] = new Dictionary<string, string>
            {
                ["type"] = entityType,
                ["id"] = entityId
            }
        };

        if (screenX.HasValue && screenY.HasValue)
        {
            payload["params"] = new Dictionary<string, object>
            {
                ["preferred_position"] = new Dictionary<string, object>
                {
                    ["mode"] = "screen_point",
                    ["x"] = screenX.Value,
                    ["y"] = screenY.Value
                }
            };
        }

        SendEnvelope("world:select_object", payload);
    }

    /// <summary>
    /// building_construction:copy_source イベントを送信する。
    /// 既存建物コピー建設のコピー元選択モード(MODE_PICK_BUILDING)で建物が選ばれたことを UI へ通知する。
    /// UI はこの variant / floors / visual_variant をパネルへシードし、STATE_EDIT_BUILDING の
    /// 区画選択フローへ遷移する。実寸フットプリントはバックエンドが source_agent_id から導出する。
    /// </summary>
    public void EmitBuildingConstructionCopySource(int sourceAgentId, int variant, int floors, float vv1, float vv2, float vv3)
    {
        var payload = new Dictionary<string, object>
        {
            ["source_agent_id"] = sourceAgentId,
            ["variant"] = variant,
            ["floors"] = floors,
            ["visual_variant"] = vv1,
            ["visual_variant2"] = vv2,
            ["visual_variant3"] = vv3
        };

        SendEnvelope("building_construction:copy_source", payload);
    }

    /// <summary>
    /// edit_path:select_node イベントを送信する。
    /// </summary>
    public void EmitEditPathSelectNode(int pathId, int nodeId, float[] position, string nodeType, float height = 0f, float embankmentHeight = 0f)
    {
        var payload = new Dictionary<string, object>
        {
            ["path_id"] = pathId,
            ["node_id"] = nodeId,
            ["position"] = new List<float> { position[0], position[1], position[2] },
            ["node_type"] = nodeType,
            ["height"] = height,
            ["embankment_height"] = embankmentHeight
        };

        SendEnvelope("edit_path:select_node", payload);
    }

    /// <summary>
    /// edit_path:select_merge_target イベントを送信する。
    /// パス結合の相手選択モード(STATE_MERGE_PATH)で、結合相手のパスが選ばれたことをUIに通知する。
    /// UIはこのIDで結合プレビュー(POST /paths/merge の dry_run)を取得し、長さを表示してから確定する。
    /// </summary>
    public void EmitPathMergeTargetSelected(int pathId, string pathName)
    {
        var payload = new Dictionary<string, object>
        {
            ["path_id"] = pathId,
            ["path_name"] = pathName ?? ""
        };

        SendEnvelope("edit_path:select_merge_target", payload);
    }

    /// <summary>
    /// edit_path:select_split_point イベントを送信する。
    /// パス分割の分割点選択モード(STATE_SPLIT_PATH)で、分割点が選ばれたことをUIに通知する。
    /// 座標は対象パスの中心線へ射影済みの [x, y, z]。UIはこの地点で分割プレビュー
    /// (POST /paths/{id}/split の dry_run)を取得し、前半・後半の長さを表示してから確定する。
    /// </summary>
    public void EmitPathSplitPointSelected(Vector3 position)
    {
        var payload = new Dictionary<string, object>
        {
            ["point"] = new List<float> { position.x, position.y, position.z }
        };

        SendEnvelope("edit_path:select_split_point", payload);
    }

    /// <summary>
    /// edit_road:toggle_path イベントを送信する。
    /// 道路の区間編集モード(STATE_EDIT_ROAD_MEMBERS)で街路・高速道路の区間がクリックされたことをUIに通知する。
    /// 所属の付け外し(shift=true は直進方向へたどった区間をまとめて追加)はUIがAPIで行い、
    /// 結果の全集合を edit_road:set_members で送り返す。
    /// </summary>
    public void EmitRoadTogglePath(int pathId, string pathName, int pathType, bool shift)
    {
        var payload = new Dictionary<string, object>
        {
            ["path_id"] = pathId,
            ["path_name"] = pathName ?? "",
            ["path_type"] = pathType,
            ["shift"] = shift
        };

        SendEnvelope("edit_road:toggle_path", payload);
    }

    /// <summary>
    /// edit_road:ended イベントを送信する。
    /// 道路の区間編集モード(STATE_EDIT_ROAD_MEMBERS)が終わったことを、終了の理由を問わずUIに通知する
    /// (他ステートへの直接遷移・Esc・セッションリセットなど、パネルが関与しない終了にUIを追従させるため)。
    /// sessionId は開始時の STATE_EDIT_ROAD_MEMBERS に UI が付けた値で、型を変えずに返す
    /// (パネルは厳密比較で照合する)。null なら session_id キーを省く。
    /// モードを抜けるときに1回だけ送り、周期送信はしない。
    /// </summary>
    public void EmitRoadMemberEditEnded(int roadId, object sessionId = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["road_id"] = roadId
        };
        if (sessionId != null) payload["session_id"] = sessionId;

        SendEnvelope("edit_road:ended", payload);
    }

    /// <summary>
    /// world:roads_changed イベントを送信する。
    /// UI を通さずに道路(名前付き道路)の区間が消えた(ブルドーザでの削除)ことを UI に知らせ、
    /// 道路一覧・道路詳細パネル・都市マップの道路名を取り直させる。区間が0本になった道路は
    /// バックエンドが削除するため、開いている詳細パネルはこれで削除済みを表示できる。
    /// 削除が成功したときだけ送り、周期送信はしない。
    /// </summary>
    public void EmitRoadsChanged()
    {
        SendEnvelope("world:roads_changed", new Dictionary<string, object>());
    }

    /// <summary>
    /// edit_path:update_shape イベントを送信する。
    /// waypointControls は中間アンカー(selected_points の両端を除く各点)ごとの
    /// ベジエ制御点 {control_in, control_out}([x, 相対高さ, z]、未設定は null)。
    /// UIが確定送信の waypoints に含めることで、ドラッグ調整した曲線が永続化される。
    /// isCurved は曲線モードの現在値。真実源はUnity側(RoadPlanManager.Curved)にあり、
    /// UIは受信値を確定送信(curved)・形状プレビュー・コスト見積りに使うため、
    /// 点列が空でも常に送信する。
    /// extras は2Dパス形状編集パネルが使う追加フィールド(phase / selected_index /
    /// lateral_offsets / alignment)で、そのままトップレベルにマージする。
    /// </summary>
    public void EmitEditPathUpdateShape(
        int unsentPathsCount,
        int pathType,
        List<List<float>> selectedPoints = null,
        List<float> startControl = null,
        List<float> endControl = null,
        List<float> embankmentHeights = null,
        List<Dictionary<string, object>> waypointControls = null,
        bool isCurved = false,
        int followTerrain = Constants.VALUE_NONE,
        bool finalized = false,
        bool canConfirm = false,
        Dictionary<string, object> extras = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["unsent_paths_count"] = unsentPathsCount,
            ["path_type"] = pathType,
            ["is_curved"] = isCurved,
            // 地形追従の個別設定を常送する（真実源はUnity側）。UIは確定送信のペイロードで
            // VALUE_NONE 以外のときだけ follow_terrain を積む。
            ["follow_terrain"] = followTerrain,
            // 形状確定（Placing→Editing、右クリック）で true。UI はこのとき権威ある
            // バックエンド干渉問い合わせを1回発火する。ドラッグ中の更新では false。
            ["is_finalized"] = finalized,
            ["can_confirm"] = canConfirm,
        };

        if (selectedPoints != null)
        {
            payload["selected_points"] = selectedPoints;
        }

        if (startControl != null)
        {
            payload["start_control"] = startControl;
        }

        if (endControl != null)
        {
            payload["end_control"] = endControl;
        }

        if (embankmentHeights != null)
        {
            payload["embankment_heights"] = embankmentHeights;
        }

        if (waypointControls != null)
        {
            payload["waypoint_controls"] = waypointControls;
        }

        // 2Dパス形状編集パネル向けの追加フィールド(phase / selected_index / lateral_offsets / alignment)。
        // RoadPlanManager.BuildShapeSyncExtras が組み立てる。
        if (extras != null)
        {
            foreach (var kv in extras) payload[kv.Key] = kv.Value;
        }

        SendEnvelope("edit_path:update_shape", payload);
    }

    /// <summary>
    /// edit_building:select_parcel イベントを送信する。
    /// 土地区画を選択した際に、その区画の形状情報も含めてUIに通知する。
    /// </summary>
    public void EmitEditBuildingSelectParcel(int parcelId, float[] position, bool isEditMode, int editingBuildingId, float[][] polygon = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["parcel_id"] = parcelId,
            ["is_edit_mode"] = isEditMode,
            ["editing_building_id"] = editingBuildingId
        };

        if (position != null)
        {
            payload["position"] = new List<float> { position[0], position[1], position[2] };
        }

        if (polygon != null)
        {
            payload["polygon"] = polygon;
        }

        SendEnvelope("edit_building:select_parcel", payload);
    }

    /// <summary>
    /// edit_building:select_point イベントを送信する。
    /// 建物建設モード(直接配置)において、土地区画外の地点がクリックされたことをUIに通知する。
    /// UIはこの地点を中心とする矩形底面をバックエンドにプレビュー問い合わせして直接配置モードに入る。
    /// angle には、確定時点でUnity側が保持している直接配置の回転角(度)を含める。
    /// 中ボタンドラッグでゴーストを回転させてから確定した場合、その角度をUI側に引き継ぐために使用する。
    /// </summary>
    public void EmitEditBuildingSelectPoint(Vector3 position, int angle)
    {
        var payload = new Dictionary<string, object>
        {
            ["position"] = new List<float> { position.x, position.y, position.z },
            ["angle"] = angle
        };

        SendEnvelope("edit_building:select_point", payload);
    }

    /// <summary>配置方法と、位置・寸法・角度を同一世代の状態としてパネルへ返す。</summary>
    public void EmitEditBuildingPlacementState(Dictionary<string, object> state)
    {
        SendEnvelope("edit_building:placement_state", state);
    }

    /// <summary>
    /// edit_building:update_direct_angle イベントを送信する。
    /// 直接配置モードで中ボタンドラッグによる回転が終了(ボタンリリース)したとき、
    /// Unity側が確定した回転角(度)をUIに通知して回転ステッパー(rectAngle)を更新させる。
    /// </summary>
    public void EmitEditBuildingUpdateDirectAngle(int angle)
    {
        var payload = new Dictionary<string, object>
        {
            ["angle"] = angle
        };

        SendEnvelope("edit_building:update_direct_angle", payload);
    }

    /// <summary>
    /// edit_building:invalid_parcel イベントを送信する。
    /// 建物建設モードにおいて、選択中の建物種類と互換性のない土地区画がクリックされたことをUIに通知する。
    /// UI側で警告通知を表示するために使用する。
    /// </summary>
    public void EmitEditBuildingInvalidParcel(int parcelId)
    {
        var payload = new Dictionary<string, object>
        {
            ["parcel_id"] = parcelId,
        };

        SendEnvelope("edit_building:invalid_parcel", payload);
    }

    /// <summary>
    /// edit_parcel:select_point イベントを送信する。
    /// 土地区画作成モード（empty_area）において、3Dビュー上の地点がクリックされたことをUIに通知する。
    /// </summary>
    public void EmitEditParcelSelectPoint(Vector3 position)
    {
        var payload = new Dictionary<string, object>
        {
            ["position"] = new List<float> { position.x, position.y, position.z }
        };

        SendEnvelope("edit_parcel:select_point", payload);
    }

    /// <summary>
    /// edit_parcel:set_mode イベントを送信する。
    /// 3Dビューのラジアルメニュー(ParcelEditControlMenu)で作成モードが切り替わったことをUIに通知する。
    /// 作成モードの真実源はUnity側(ParcelEditorManager.CurrentMode)にあり、UIの作成パネルは
    /// これを受けて入力欄の構成(幅・奥行き・平均面積など)と選択状態の表示を合わせる。
    /// </summary>
    public void EmitEditParcelSetMode(string mode)
    {
        var payload = new Dictionary<string, object>
        {
            ["mode"] = mode ?? ""
        };

        SendEnvelope("edit_parcel:set_mode", payload);
    }

    /// <summary>
    /// edit_parcel:select_path イベントを送信する。
    /// 土地区画作成モード（along_path）において、パスが選択されたことをUIに通知する。
    /// position はクリック地点をパス中心線へ射影した点で、作成範囲が「クリック地点」の
    /// ときにUIがそこだけ区画を作るためのアンカーになる（「パス全体」では使われない）。
    /// </summary>
    public void EmitEditParcelSelectPath(int pathId, Vector3 position)
    {
        var payload = new Dictionary<string, object>
        {
            ["path_id"] = pathId,
            ["position"] = new List<float> { position.x, position.y, position.z }
        };

        SendEnvelope("edit_parcel:select_path", payload);
    }

    /// <summary>
    /// edit_zone:selected イベントを送信する。
    /// 地区編集モードで選択中地区が変化したことをUIに通知する。
    /// </summary>
    public void EmitEditZoneSelected(int zoneId, string zoneName)
    {
        var payload = new Dictionary<string, object>
        {
            ["zone_id"] = zoneId,
            ["zone_name"] = zoneName ?? ""
        };

        SendEnvelope("edit_zone:selected", payload);
    }

    /// <summary>
    /// edit_zone:request_merge イベントを送信する。
    /// 地区編集モードの統合ツールで「統合元」と「統合先」が決まったことをUIに通知する。
    /// 統合は地区がひとつ消える不可逆操作のため、Unityは送信せずUIの確定ボタンを待つ
    /// (実際の POST /zones/{id}/merge はUI側が行い、結果が edit_zone:finish_merge で戻る)。
    /// </summary>
    public void EmitEditZoneRequestMerge(int targetZoneId, string targetZoneName, int sourceZoneId, string sourceZoneName)
    {
        var payload = new Dictionary<string, object>
        {
            ["target_zone_id"] = targetZoneId,
            ["target_zone_name"] = targetZoneName ?? "",
            ["source_zone_id"] = sourceZoneId,
            ["source_zone_name"] = sourceZoneName ?? ""
        };

        SendEnvelope("edit_zone:request_merge", payload);
    }

    /// <summary>
    /// edit_parcel:select_parcel イベントを送信する。
    /// 土地区画作成モード（split系）において、既存土地区画が選択されたことをUIに通知する。
    /// </summary>
    public void EmitEditParcelSelectParcel(int parcelId)
    {
        var payload = new Dictionary<string, object>
        {
            ["parcel_id"] = parcelId
        };

        SendEnvelope("edit_parcel:select_parcel", payload);
    }

    /// <summary>
    /// edit_parcel:update_shape イベントを送信する。
    /// 土地区画形状編集モードにおいて、編集中のポリゴンが変更されたことをUIに通知する。
    /// </summary>
    public void EmitEditParcelUpdateShape(int parcelId, List<List<float>> polygon)
    {
        var payload = new Dictionary<string, object>
        {
            ["parcel_id"] = parcelId,
            ["polygon"] = polygon
        };

        SendEnvelope("edit_parcel:update_shape", payload);
    }

    /// <summary>
    /// edit_resource:update_shape イベントを送信する。
    /// 資源エリア編集モードにおいて、編集中のポリゴンが変更されたことをUIに通知する。
    /// 新規作成中のエリアであり既存IDを持たないため、区画形状編集と異なりIDは含まない。
    /// </summary>
    public void EmitEditResourceUpdateShape(List<List<float>> polygon)
    {
        var payload = new Dictionary<string, object>
        {
            ["polygon"] = polygon
        };

        SendEnvelope("edit_resource:update_shape", payload);
    }

    /// <summary>
    /// edit_grading:update_shape イベントを送信する。
    /// 造成範囲の作図中のポリゴンが変わったことをUIに通知する。各頂点は [x, z]。
    /// finalized は右クリックで形を閉じた後(Editingフェーズ)か。UIは閉じる前の折れ線では見積もりを取らない。
    /// </summary>
    public void EmitEditGradingUpdateShape(List<List<float>> polygon, bool finalized)
    {
        var payload = new Dictionary<string, object>
        {
            ["polygon"] = polygon,
            ["finalized"] = finalized
        };

        SendEnvelope("edit_grading:update_shape", payload);
    }

    /// <summary>
    /// edit_transit:update_points イベントを送信する。
    /// 公共交通の路線設置モードにおいて、配置中の巡回点(停留所・経由点を巡回順に並べたもの)が変更されたことを
    /// UIに通知する。各点は {kind: "stop" | "waypoint", position: [x, y, z]}(公共交通パスは相対Y=0のため y は0)。
    /// 新設・経路編集パネル(TransitCreationPanel)がこれを受けて停留所・経由点の数の表示と確定で送る巡回点を更新する。
    /// </summary>
    public void EmitEditTransitUpdatePoints(List<Dictionary<string, object>> points)
    {
        var payload = new Dictionary<string, object>
        {
            ["points"] = points
        };

        SendEnvelope("edit_transit:update_points", payload);
    }

    /// <summary>
    /// edit_prop:update_instances イベントを送信する。
    /// プロップ配置(個別モード)において、配置済みインスタンス列が変更されたことをUIに通知する。
    /// 各要素は { x, z, rotation, scale }。PropPlacementPanel がこれを受けて配置数の表示と
    /// POST用のインスタンス列を更新する。
    /// </summary>
    public void EmitEditPropUpdateInstances(List<Dictionary<string, object>> instances)
    {
        var payload = new Dictionary<string, object>
        {
            ["instances"] = instances
        };

        SendEnvelope("edit_prop:update_instances", payload);
    }

    /// <summary>
    /// edit_prop:update_shape イベントを送信する。
    /// プロップ配置(範囲モード)において、描画中の散布範囲ポリゴンが変更されたことをUIに通知する。
    /// 各頂点は [x, z]（資源エリア編集 edit_resource:update_shape と同形）。
    /// </summary>
    public void EmitEditPropUpdateShape(List<List<float>> polygon, bool finalized)
    {
        var payload = new Dictionary<string, object>
        {
            ["polygon"] = polygon,
            ["finalized"] = finalized
        };

        SendEnvelope("edit_prop:update_shape", payload);
    }

    /// <summary>
    /// edit_prop:placement_settings イベントを送信する。
    /// プロップ配置(個別モード)で3D側の操作(中ボタン回転・PageUp/PageDown の高さ変更)により
    /// 次に置くゴーストの回転角・高さが変わったことをUIへ通知し、パネルのステッパー表示を追従させる
    /// (建物直接配置の edit_building:update_direct_angle と同じ役割。UIはこれをエコーしない)。
    /// </summary>
    public void EmitEditPropPlacementSettings(float rotation, float height)
    {
        var payload = new Dictionary<string, object>
        {
            ["rotation"] = rotation,
            ["height"] = height
        };

        SendEnvelope("edit_prop:placement_settings", payload);
    }

    /// <summary>
    /// remove_object:request_confirm イベントを送信する。
    /// ブルドーザモードで「取り消せない・影響範囲が広い」対象(範囲配置プロップ=森などを範囲ごと撤去)を
    /// クリックしたとき、Unityは即削除せずUIへ確認を求める。常駐のコックピットが確認ダイアログを出し、
    /// 結果を remove_object:confirm_result で返す。object_type は "prop_scatter"。
    /// </summary>
    public void EmitRemoveObjectRequestConfirm(string objectType, int id, int propType)
    {
        var payload = new Dictionary<string, object>
        {
            ["object_type"] = objectType,
            ["id"] = id,
            ["prop_type"] = propType
        };

        SendEnvelope("remove_object:request_confirm", payload);
    }

    /// <summary>
    /// edit_parcel:update_rotation イベントを送信する。
    /// 土地区画作成モード（empty_area）において、左クリックドラッグで確定した角度をUIに通知する。
    /// </summary>
    public void EmitEditParcelUpdateRotation(float angle)
    {
        var payload = new Dictionary<string, object>
        {
            ["angle"] = angle
        };

        SendEnvelope("edit_parcel:update_rotation", payload);
    }


    public void EmitGridAngleChanged(float angle, float originX, float originZ)
    {
        var payload = new Dictionary<string, object>
        {
            ["angle"] = angle,
            ["origin_x"] = originX,
            ["origin_z"] = originZ
        };

        SendEnvelope("view:grid_angle_changed", payload);
    }

    /// <summary>
    /// view:grid_toggled イベントを送信する。
    /// ラジアルメニューからグリッド表示をトグルした際に、UIパネル(グリッド設定)の
    /// 表示状態を同期させるための通知。visible=現在の表示状態。
    /// </summary>
    public void EmitViewGridToggled(bool visible)
    {
        var payload = new Dictionary<string, object>
        {
            ["visible"] = visible
        };

        SendEnvelope("view:grid_toggled", payload);
    }

    /// <summary>
    /// citizen:follow_state を送る。追従の開始・終了(Esc・カメラ操作・編集モードへの移行など Unity 側の理由を含む)を
    /// 市民パネルと追跡リストへ知らせ、カメラボタンの表示を合わせる。
    /// </summary>
    public void EmitCitizenFollowState(int citizenId, bool following)
    {
        SendEnvelope("citizen:follow_state", new Dictionary<string, object> { ["citizen_id"] = citizenId, ["following"] = following });
    }

    /// <summary>
    /// citizen:pick_failed を送る。クリックした個体の市民の抽選や行程の取得ができなかった理由(コアの status)を UI が案内する。
    /// </summary>
    public void EmitCitizenPickFailed(string status)
    {
        SendEnvelope("citizen:pick_failed", new Dictionary<string, object> { ["status"] = status });
    }

    /// <summary>
    /// camera:state イベントを送信する(camera:query_state への応答)。
    /// 3Dカメラの現在位置と視線の方位を2D都市マップへ通知する。
    /// yaw は北(ワールド+Z)を0とする時計回りの度。真下を向いているときは視線の水平成分が
    /// 消えるため、カメラのY軸回転(=画面上方向の方位)で代用する。
    /// </summary>
    public void EmitCameraState()
    {
        Camera cam = CameraController.Instance != null
            ? CameraController.Instance.GetComponent<Camera>()
            : Camera.main;
        if (cam == null) return;

        Transform tr = cam.transform;
        Vector3 forward = tr.forward;
        Vector2 flatForward = new Vector2(forward.x, forward.z);
        float yaw = flatForward.sqrMagnitude < 1e-6f
            ? tr.eulerAngles.y
            : Mathf.Atan2(flatForward.x, flatForward.y) * Mathf.Rad2Deg;

        var payload = new Dictionary<string, object>
        {
            ["position"] = new List<float> { tr.position.x, tr.position.y, tr.position.z },
            ["yaw"] = yaw
        };

        SendEnvelope("camera:state", payload);
    }

    /// <summary>
    /// view:current_state イベントを送信する。
    /// UIからの問い合わせに対して、現在の表示状態を返す。
    /// </summary>
    public void EmitViewCurrentState(Dictionary<string, object> layers)
    {
        var payload = new Dictionary<string, object>
        {
            ["layers"] = layers
        };

        SendEnvelope("view:current_state", payload);
    }

    /// <summary>
    /// view:object_filter_state イベントを送信する。
    /// オブジェクト単位の表示フィルタの現在値(非表示にしているIDの全集合)をUIへ返す。
    /// 表示状態の正はUnity側にあり、UIはパネルを開いたときにこれを問い合わせて同期する
    /// (view:query_state / view:current_state と同じ形)。
    /// </summary>
    public void EmitViewObjectFilterState(List<int> hiddenBuildingIds, List<int> hiddenPathIds)
    {
        SendEnvelope("view:object_filter_state", new Dictionary<string, object>
        {
            ["hidden_building_ids"] = hiddenBuildingIds ?? new List<int>(),
            ["hidden_path_ids"] = hiddenPathIds ?? new List<int>()
        });
    }

    public void EmitWeatherState(string id, bool locked, int rain, int snow, object[] presets)
    {
        SendEnvelope("weather:state", new Dictionary<string, object>
        {
            ["id"] = id, ["locked"] = locked, ["rain"] = rain, ["snow"] = snow, ["presets"] = presets
        });
    }

    public void EmitEpochDarkened(string id)
    {
        SendEnvelope("epoch:darkened", new Dictionary<string, object> { ["id"] = id });
    }

    /// <summary>
    /// camera:thumbnail_captured イベントを送信する(camera:capture_thumbnail への応答)。
    /// success は撮った画像をコアへ預けられたか。UIは失敗でも保存を続けるので、撮れなくても必ず送る。
    /// </summary>
    public void EmitCameraThumbnailCaptured(string id, bool success)
    {
        SendEnvelope("camera:thumbnail_captured", new Dictionary<string, object> { ["id"] = id, ["success"] = success });
    }

    /// <summary>
    /// input:current_modes イベントを送信する。
    /// デバッグパネルからの問い合わせに対して、現在の入力/表示モードを返す。
    /// </summary>
    public void EmitInputCurrentModes(Dictionary<string, object> objectTypes)
    {
        var payload = new Dictionary<string, object>
        {
            ["object_types"] = objectTypes
        };

        SendEnvelope("input:current_modes", payload);
    }

    /// <summary>
    /// game:loaded イベントを送信する。初期描画が完了した要求世代を Tauri に通知する。
    /// </summary>
    public void EmitGameLoaded(int loadingId)
    {
        SendEnvelope("game:loaded", new Dictionary<string, object> { { "loading_id", loadingId } });
    }

    /// <summary>
    /// system:graphic_ready イベントを送信する。
    /// Unity の起動完了(初回起動・クラッシュ後の自動再起動とも)を Tauri に通知する。
    /// Tauri 側は2回目以降の受信(=クラッシュ後の再起動)でのみポップアップを一掃する
    /// (初回はユーザーが操作中のロード画面などを閉じないため表示に触れない)。
    /// </summary>
    public void EmitGraphicReady()
    {
        SendEnvelope("system:graphic_ready", new Dictionary<string, object> {
            { "frontend_version", FrontendVersion.Version },
            { "ipc_contract_version", FrontendVersion.IpcContract },
            { "core_compatible", BackendCompatibility.Compatible }
        });
    }

    /// <summary>
    /// system:graphic_stopped イベントを送信する。
    /// Unity の正常終了(Editor の Play 停止を含む)を Tauri に通知し、接続中フラグを下ろさせる。
    /// これが無いと、Tauri は開発時の Unity なし起動でも一度接続した後は描画完了を待ち続け、
    /// Play を止めた後の再ロードで Tips が「Now Loading」から戻れなくなる。
    /// </summary>
    public void EmitGraphicStopped()
    {
        SendEnvelope("system:graphic_stopped", new Dictionary<string, object>());
    }

    // ======================================================================
    // 内部ユーティリティ
    // ======================================================================

    /// <summary>
    /// エンベロープを作成してTauriに送信する。
    /// </summary>
    public void EmitTutorialApplied(string runId, int revision)
    {
        SendEnvelope("tutorial:applied", new Dictionary<string, object> { ["runId"] = runId, ["revision"] = revision });
    }

    private void SendEnvelope(string eventName, object payload)
    {
        if (_writer == null) return;

        IpcEnvelope envelope = IpcEnvelope.CreateOutgoing(eventName, payload);
        string json = envelope.ToJson();
        try
        {
            _writer.Send(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"IpcClient: 送信エラー: {e.Message}");
        }
    }

    private static string GetParamString(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue("params", out object paramsObj))
        {
            if (paramsObj is Newtonsoft.Json.Linq.JObject jParams
                && jParams.TryGetValue(key, out var jVal))
            {
                return jVal.Type == Newtonsoft.Json.Linq.JTokenType.Null ? null : jVal.ToString();
            }
            if (paramsObj is Dictionary<string, object> @params
                && @params.TryGetValue(key, out object val))
            {
                return val?.ToString();
            }
        }

        // paramsラップなしで直接キーがある場合
        if (payload.TryGetValue(key, out object directVal))
        {
            return directVal?.ToString();
        }

        return null;
    }

    /// <summary>
    /// ペイロードからparams辞書を抽出する。
    /// paramsがJObjectの場合はDictionaryに変換し、nullの場合は空のDictionaryを返す。
    /// </summary>
    private static Dictionary<string, object> GetParamsDictionary(Dictionary<string, object> payload)
    {
        if (payload.TryGetValue("params", out object paramsObj))
        {
            if (paramsObj is Newtonsoft.Json.Linq.JObject jParams)
            {
                return jParams.ToObject<Dictionary<string, object>>() ?? new Dictionary<string, object>();
            }
            if (paramsObj is Dictionary<string, object> dict)
            {
                return dict;
            }
        }
        return new Dictionary<string, object>();
    }

    private static int GetParamInt(Dictionary<string, object> payload, string key)
    {
        string str = GetParamString(payload, key);
        if (int.TryParse(str, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int result))
        {
            return result;
        }

        if (payload.TryGetValue("params", out object paramsObj))
        {
            if (paramsObj is Newtonsoft.Json.Linq.JObject jParams)
            {
                var val = jParams[key];
                if (val != null && val.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                    return (int)val;
            }
            else if (paramsObj is Dictionary<string, object> @params)
            {
                if (@params.TryGetValue(key, out object val) && val is long longVal)
                    return (int)longVal;
                if (@params.TryGetValue(key, out object val2) && val2 is int intVal)
                    return intVal;
            }
        }

        return 0;
    }

    private static float GetParamFloat(Dictionary<string, object> payload, string key)
    {
        string str = GetParamString(payload, key);
        if (float.TryParse(str, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float result))
        {
            return result;
        }

        if (payload.TryGetValue("params", out object paramsObj))
        {
            if (paramsObj is Newtonsoft.Json.Linq.JObject jParams)
            {
                var val = jParams[key];
                if (val != null)
                {
                    if (val.Type == Newtonsoft.Json.Linq.JTokenType.Float
                        || val.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                        return (float)val;
                }
            }
            else if (paramsObj is Dictionary<string, object> @params)
            {
                if (@params.TryGetValue(key, out object val) && val is double doubleVal)
                    return (float)doubleVal;
                if (@params.TryGetValue(key, out object val2) && val2 is long longVal)
                    return (float)longVal;
            }
        }

        return 0f;
    }

    /// <summary>
    /// ペイロードのparamsから任意のJSONトークン(オブジェクト等)をそのまま取得する。
    /// 効果範囲の region のように discriminated union(円/ポリゴン)を素のまま渡したい場合に使う。
    /// 見つからない/null の場合は null を返す。
    /// </summary>
    private static Newtonsoft.Json.Linq.JToken GetParamToken(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue("params", out object paramsObj))
        {
            if (paramsObj is Newtonsoft.Json.Linq.JObject jParams
                && jParams.TryGetValue(key, out var jVal))
            {
                return jVal != null && jVal.Type == Newtonsoft.Json.Linq.JTokenType.Null ? null : jVal;
            }
            else if (paramsObj is Dictionary<string, object> @params
                && @params.TryGetValue(key, out object val))
            {
                return val as Newtonsoft.Json.Linq.JToken;
            }
        }
        return null;
    }

    private static float[][] GetParamFloat2D(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue("params", out object paramsObj))
        {
            Newtonsoft.Json.Linq.JArray jArr = null;

            if (paramsObj is Newtonsoft.Json.Linq.JObject jParams
                && jParams.TryGetValue(key, out var jVal)
                && jVal is Newtonsoft.Json.Linq.JArray arr)
            {
                jArr = arr;
            }
            else if (paramsObj is Dictionary<string, object> @params
                && @params.TryGetValue(key, out object val)
                && val is Newtonsoft.Json.Linq.JArray arr2)
            {
                jArr = arr2;
            }

            if (jArr != null)
            {
                var result = new float[jArr.Count][];
                for (int i = 0; i < jArr.Count; i++)
                {
                    var inner = jArr[i] as Newtonsoft.Json.Linq.JArray;
                    if (inner != null)
                    {
                        result[i] = new float[inner.Count];
                        for (int j = 0; j < inner.Count; j++)
                        {
                            result[i][j] = (float)inner[j];
                        }
                    }
                }
                return result;
            }
        }
        return null;
    }

    /// <summary>
    /// ペイロードのparamsから1次元float配列を取得する。
    /// JObject と Dictionary&lt;string, object&gt; の両方に対応する。
    /// </summary>
    private static float[] GetParamFloat1D(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue("params", out object paramsObj))
        {
            Newtonsoft.Json.Linq.JArray jArr = null;

            if (paramsObj is Newtonsoft.Json.Linq.JObject jParams
                && jParams.TryGetValue(key, out var jVal)
                && jVal is Newtonsoft.Json.Linq.JArray arr)
            {
                jArr = arr;
            }
            else if (paramsObj is Dictionary<string, object> @params
                && @params.TryGetValue(key, out object val)
                && val is Newtonsoft.Json.Linq.JArray arr2)
            {
                jArr = arr2;
            }

            if (jArr != null)
            {
                var result = new float[jArr.Count];
                for (int i = 0; i < jArr.Count; i++)
                {
                    result[i] = (float)jArr[i];
                }
                return result;
            }
        }
        return null;
    }

    /// <summary>
    /// ペイロードのparamsから1次元int配列を取得する。
    /// JObject と Dictionary&lt;string, object&gt; の両方に対応する。
    /// </summary>
    private static int[] GetParamInt1D(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue("params", out object paramsObj))
        {
            Newtonsoft.Json.Linq.JArray jArr = null;

            if (paramsObj is Newtonsoft.Json.Linq.JObject jParams
                && jParams.TryGetValue(key, out var jVal)
                && jVal is Newtonsoft.Json.Linq.JArray arr)
            {
                jArr = arr;
            }
            else if (paramsObj is Dictionary<string, object> @params
                && @params.TryGetValue(key, out object val)
                && val is Newtonsoft.Json.Linq.JArray arr2)
            {
                jArr = arr2;
            }

            if (jArr != null)
            {
                var result = new int[jArr.Count];
                for (int i = 0; i < jArr.Count; i++)
                {
                    result[i] = (int)jArr[i];
                }
                return result;
            }
        }
        return null;
    }

    /// <summary>
    /// ペイロードのparamsから3次元float配列を取得する。
    /// 主に preview_polygons ([[[x,z], ...], ...]) のパースに使用。
    /// </summary>
    private static float[][][] GetParamFloat3D(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue("params", out object paramsObj))
        {
            Newtonsoft.Json.Linq.JArray jOuter = null;

            if (paramsObj is Newtonsoft.Json.Linq.JObject jParams
                && jParams.TryGetValue(key, out var jVal)
                && jVal is Newtonsoft.Json.Linq.JArray arr)
            {
                jOuter = arr;
            }
            else if (paramsObj is Dictionary<string, object> @params
                && @params.TryGetValue(key, out object val)
                && val is Newtonsoft.Json.Linq.JArray arr2)
            {
                jOuter = arr2;
            }

            if (jOuter != null)
            {
                var result = new float[jOuter.Count][][];
                for (int i = 0; i < jOuter.Count; i++)
                {
                    var midArr = jOuter[i] as Newtonsoft.Json.Linq.JArray;
                    if (midArr == null) continue;
                    result[i] = new float[midArr.Count][];
                    for (int j = 0; j < midArr.Count; j++)
                    {
                        var inner = midArr[j] as Newtonsoft.Json.Linq.JArray;
                        if (inner != null)
                        {
                            result[i][j] = new float[inner.Count];
                            for (int k = 0; k < inner.Count; k++)
                            {
                                result[i][j][k] = (float)inner[k];
                            }
                        }
                    }
                }
                return result;
            }
        }
        return null;
    }
}
