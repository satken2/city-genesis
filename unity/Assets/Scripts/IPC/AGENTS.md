# IPC フォルダ

プロップ個別配置の高さはworld:change_stateのparams.heightとedit_prop:change_settingsのheightで送る。地面からの相対高さ(m)で、全種別に適用する。edit_prop:update_instancesの各要素はx/y/z/rotation/scaleを持ち、yは相対高さ、XZはSceneOffsetを除いた保存座標。TauriはyをそのままPOST /props/instancesへ渡す。

Tauri製UI(city-genesis/tauriレポジトリ)とのMemoryMappedFile(MMF)によるプロセス間通信を担当する。
HTTP(バックエンド通信)とは別系統であることに注意。UnityとUIの間のやり取り(クリックした建物の詳細ウィンドウを開く、編集モードの切り替え等)はすべてここを通る。

## ファイル一覧

| ファイル | 内容 |
| --- | --- |
| `IpcClient.cs` | 高レベルクライアント(シングルトン)。型付きのemit/subscribe APIを提供。受信イベントはUpdate()でメインスレッドにディスパッチされる(**MMF読み取りは別スレッドなので、購読ハンドラ内からUnity APIを直接呼んで良いのはこのディスパッチのおかげ**) |
| `IpcTransport.cs` | MmfReader/MmfWriter。MMFの低レベル読み書き |
| `IpcEnvelope.cs` | メッセージの封筒構造。**city-genesis/tauri側の`src-tauri/src/messages.rs`のEnvelopeと同一構造を保つ必要がある** |
| `IpcEventHandler.cs` | 受信イベントの処理 |
| `IpcStateCoordinator.cs` | ゲーム状態(編集モード等)の同期 |
| `IpcViewCoordinator.cs` | ビュー(表示モード)の同期 |
| `IpcInputDebugCoordinator.cs` | 入力デバッグ用 |

## 新しいIPCイベントを追加する手順

`building:exploded {agent_id}` は爆破HTTP API成功後だけUIから送る描画通知。既存の`send_window_action`/`ui:action`を使い、`IpcClient.OnBuildingExploded`→`IpcEventHandler`→`CellDataLoader.FetchBuilding(id, playExplosion: true)`へ渡す。対象1棟の取得・瓦礫への切替・単発演出をまとめ、`session:reload`は併送しない。詳細は公開`programs/IPC連携の整理.md`。

`drive:open`(UI→Unity)はCockpitのブルドーザー右隣のボタンが送る。paramsは`drive.json`の現在言語のキー→文言の辞書。`IpcClient`が`DriveMode.Ensure().Open`へ渡し、選択パネルとHUDはUnityが保持する。同イベントを表示中に受けると終了する。Rustの`send_window_action`による汎用転送を使い、逆方向の状態配信・新規ウィンドウは持たない。

`ambience:volume {volume}`はTauri→Unityの音量通知（master×sfx、0〜1）。Cockpitが設定変更時とUnity起動時(`graphic-reconnected`)に送り、CameraAmbience・ConstructionAmbience・VehicleAmbience・VisualEffectManagerが所有するAudioSourceへ適用する。`IpcClient`は最新の受信値を保持し、`OnAmbienceVolume`へ購読した時点で即時通知する(購読前に届いた1通を取りこぼすと次の設定変更まで無音になるため)。高度と天候の音響制御はUnity内で完結する。

天気は`weather:query`/`weather:set`（UI→Unity、setはidまたはnull=自動）と`weather:state`（Unity→Cockpit、id/locked/rain/snow/presets。presetsの各要素はid/weight/rain/snowで、weightは今の気候・季節での出現割合）で同期する。WeatherControllerが正を保持し、RustはCockpitに中継する。表示名はTauriのweather.json。経済APIへの書き込みは行わない。

`epoch:executing {executing: bool}`（UI→Unity）はエポック実行確認ウィンドウが実行の開始(true)と終了(false)に1通ずつ送る（成功時は`session:reload`の後に終了）。`IpcClient.OnEpochExecuting`→`WeatherController`が、開始で日を早送りで沈めて暗転し、終了で都市を読み直してから日の出へ進める。周期送信ではないのでUnity不在中に溜まっても2通ずつに留まる。

1. `IpcClient.cs`に型付きイベント(`event Action<...> OnXxx`)または送信メソッドを追加
2. city-genesis/tauri側にも対応する送受信を追加(`city-genesis/tauri/src/utils/ipc.ts`と`city-genesis/tauri/src-tauri/src/`)
3. IPCプロトコルは両コンポーネントの開発契約なので、`genesis-docs`のprograms配下のIPC仕様ドキュメントも更新する

## 編集モードのステート同期(参考)

- `world:change_state` は `IpcStateCoordinator.HandleChangeState` が受け、`STATE_*` ごとに InputMode プリセット + VisualMode をまとめて切り替える。`STATE_EDIT_TRANSIT`(公共交通の路線設置・経路編集)は params の `path_variant`・`stop_path_variants`・`route_path_variants`(int配列。`ReadIntArray` で JArray→int[] 変換)・`points`(始めの巡回点 [{kind, position}]。`ReadTransitPoints`。新設は空)・`line_agent_id`(経路編集の対象路線。新設はnull)を読み、`TransitPlanManager` を動的生成・初期化する。
- `view:change_view` は `IpcViewCoordinator` が受け、レイヤーごとに VisualMode を切り替える。params は `{layer, mode, sub_mode, flags}`。`flags` は色モードと独立した表示トグル(CSV。`"plan"`=計画中表示 / `"public"`=公共施設ハイライト。road/building レイヤーのみ使用)で、**null は「現在のフラグを維持」**(建設パネル等がモードだけ切り替える呼び出しでトグルを壊さないため)・空文字は「全OFF」を意味する。`ApplyRoadMode`/`ApplyBuildingMode` が `ParseFlags` で `VisualFlag` ビットへ変換し `VisualModeHandler.SetFlags` に渡す。`view:current_state`(Unity→UI)の各レイヤーにも `flags` キーが載る。編集ステートのプリセット(`SyncFromState`)は road/building を「default+plan」に強制し(地区編集は flags 空)、STATE_NORMAL 復帰時に `_normalStateMemory`(mode/subMode/flags の3要素)から復元する。`transit` レイヤーは `ApplyTransitMode` が sub_mode(表示する `transit_path_id` のCSV)をパースして `TransitOverlayManager.SetVisibleVariants` に渡す。
- **表示状態の正は `IpcViewCoordinator` のレイヤー記憶(`_layerModes`/`_layerSubModes`/`_layerFlags`)に一本化されている。** 編集ステート中の表示も `SyncFromState` のステート別プリセット(`ApplyConstructionPreset` / `ApplyLandEditPreset` / `ApplyTransitPreset`)が `ForceLayer` 経由で適用するので、**`IpcStateCoordinator` の `ApplyState*` から `VisualModeHandler` を直接触ってはいけない**(記憶とUIへ通知する表示状態が実際の見え方とズレる。実際、地区編集では「UIには建物=地区色分けと表示されるのに建物は通常表示」という食い違いが起きていた)。`SyncFromState` が扱わないステート(`STATE_EDIT_RESOURCE`/`STATE_EDIT_PROP`/`STATE_EDIT_PARCEL_SHAPE`/`STATE_EDIT_CONNECTOR`/`STATE_SET_GRID`)だけは従来どおり `ApplyState*` 側で表示を設定している。
- **ステートを抜けると表示は元に戻る。** `ForceLayer` の変更は記憶(`_normalStateMemory`)に載せず、`STATE_NORMAL` 復帰時に `RestoreNormalLayerModes` が「通常モード中にユーザーが `view:change_view` で選んだ表示」へ戻す(記憶が無いレイヤーは `NORMAL_DEFAULTS`=UI側 `DISPLAY_LAYERS` の初期値)。**プリセットで強制するレイヤーは必ず `NORMAL_DEFAULTS` にも載せること**(載せ忘れると「記憶が無ければ現在値を維持」になり編集中の表示が通常モードへ残る)。地形だけは色モードを強制せず等高線サブモードのみ触るので個別扱い。
- **専用の編集ステートを持たないウィンドウ用に `view:apply_preset`(UI→Unity, params `preset`)がある。** `IpcClient` が `IpcViewCoordinator.ApplyPreset` へ渡し、ステート別プリセットと同じ経路で表示を差し替える。現在のプリセットは `PRESET_TRANSIT`(公共交通の一覧`transit_management`・新設`transit_creation`が使う。全路線種別+計画中の道路・建物)。**復帰は `STATE_NORMAL` 頼みなので、使うウィンドウ種別をTauri側 `window.rs` の `EDIT_MODE_WINDOW_TYPES` に載せること**(×で閉じてもRustが `STATE_NORMAL` を送るようにする)。
- **オブジェクト単位の表示フィルタは `view:change_view` とは別のイベント系統**: `view:set_object_filter`(UI→Unity, params `hidden_building_ids`/`hidden_path_ids`。`IpcClient.OnViewSetObjectFilter` → `IpcViewCoordinator.HandleSetObjectFilter` → `ObjectVisibilityFilter.SetHidden`)で建物・道路をIDで隠す。**差分ではなく毎回「今隠しているものの全集合」が届く**契約で、UIはタグ名ではなくIDを送る(グラフィック側にタグの概念を持ち込まない)。現在値は `view:query_object_filter`(UI→Unity)→`view:object_filter_state`(Unity→UI, `EmitViewObjectFilterState`)で返し、複数のパネルが同じ状態を見る(`view:query_state`/`view:current_state` と同じ形)。**この状態はバックエンドにもセーブにも残らず、セッションリセットで捨てる**。詳細は genesis-docs/docs/programs/建設プロジェクト管理.md
- `edit_transit:update_points`(Unity→UI, `IpcClient.EmitEditTransitUpdatePoints`)は路線設置中の巡回点(停留所・経由点を巡回順に並べた {kind, position} の列)を通知する。`edit_transit:finish`(UI→Unity)は `IpcEventHandler.HandleEditTransitFinish` が受け、`TransitPlanManager.FinishPlacement` + `CellDataLoader.ReloadAllData` + `TransitOverlayManager.MarkDirty` を実行する(`edit_resource:finish` と同じ枠組み)。
- `edit_prop:placement_settings`(Unity→UI, `IpcClient.EmitEditPropPlacementSettings(rotation, height)`)はプロップ個別配置の3D側操作(中ボタン回転・PageUp/PageDown)で変わった設定をパネルへ書き戻す(UIはエコーしない)。ブルドーザの確認付き削除は `remove_object:request_confirm`(Unity→UI, `EmitRemoveObjectRequestConfirm(object_type, id, prop_type)`。Rustはコックピット宛にだけ中継)と `remove_object:confirm_result`(UI→Unity, `OnRemoveObjectConfirmResult(object_type, id, accepted)` → `IpcEventHandler.HandleRemoveObjectConfirmResult` → `RemovalManager.OnConfirmResult`)の往復。現状の object_type は `"prop_scatter"` のみ。
- 既存区画の形状編集は `edit_parcel:update_shape`（`EmitEditParcelUpdateShape(parcelId, polygon)`）で `ParcelDetailPanel` へ通知する。確定のPATCHはUIが送り、成功後の `edit_parcel:finish_shape_edit` で `ParcelShapeEditManager.CompleteEditMode` と通常モードへの復帰を行う。新規区画の任意ポリゴン作図用イベントは持たない。

## 知らないとわからないこと

- **MMFの容量`CAPACITY`(1MiB)は`MmfReader`/`MmfWriter`とtauri側`src-tauri/src/ipc.rs`の同名定数の3箇所を必ず揃えること。** 共有メモリは両プロセスとも`CreateOrOpen`で開くため先に起動した側のサイズが採用され、食い違うと片側だけが範囲外を読む。1メッセージがバッファに収まらないと送信側が失敗し、**受信側は何も起きないだけなので「UIは動くがUnityだけ更新が止まる」というサイレント故障になる**(土地区画の一括作成プレビューは数百区画ぶんのポリゴンを載せるため、旧8KBでは広い範囲を指定すると必ず溢れていた)。
- **Unity不在の間もUI→Unityの送信はMMFに溜まり、次に起動したUnityがまとめて読む。** 満杯になるとTauri側の送信が`MMF buffer full`で失敗し続ける(Editorで再生を止めたままTauriを起動しておくと、周期送信が約2時間で1MiBを埋める)。**このため周期的に送り続けるメッセージは置かない。** Unity再起動後の再同期はUnityが送る`system:graphic_ready`(UIでは`graphic-reconnected`)を起点にするか、Unity側が起動時に自分から状態を送る(`weather:state`など)。
- 読み取り側はラップマーカーを越えて先頭のメッセージまで同じ通知の中で読み進める(1通知で読み切らないと、次の書き込みまでメッセージが取り残される)。長さが壊れていたら例外で読み取りスレッドを止めず、未読分を捨てて通信を続ける。
- イベント名は`source`/`destination`と`event`文字列で識別される。エンベロープの用語上、Unity側は"parent"と呼ばれる(Tauri側の`send_to_parent`はUnity宛て送信のこと)。
- **曲線/直線・吸着の設定系イベントは廃止済み**: `edit_path:change_curved` / `view:set_snapping` は存在しない。曲線/直線・吸着の真実源はUnity側(Ctrlラジアルメニュー、`Editors/RoadEditor/RoadEditControlMenu`)にあり、曲線モードは `edit_path:update_shape` の `is_curved`(点列が空でも常送)としてUIへ逆流する。
- **標高・盛土はパネルにもステッパーが残る**: `edit_path:change_node`(標高) / `edit_path:change_embankment`(盛土)はUIパネルのステッパー変更をUnityへ送るイベントとして現存(`OnEditPathChangeNode`/`OnEditPathChangeEmbankment`→`RoadPlanManager.OnChangeNodeFromIpc`/`OnChangeEmbankmentFromIpc`)。node_index=-1 は次回配置ノードの値を指す。逆にUnityのキー操作や選択で値が変わると `edit_path:select_node`(標高・盛土を含む)でパネルへ返す双方向同期。既存ノードを選んで数値で微調整する用途のため両方に口を残している。
- **地形追従の個別設定もUI→Unityのライブ設定イベントを持つ**: `edit_path:set_follow_terrain`(`IpcClient.OnEditPathSetFollowTerrain` → `IpcEventHandler.HandleEditPathSetFollowTerrain` → `RoadPlanManager.SetFollowTerrain`。params `value`=-1未設定/0非追従/1追従)。ホーム設置側と同じくパネルのトグルが真実源で、切り替えを逐次Unityへ送って敷設中プレビューの地形追従・トンネル可視化に反映し、確定送信用に `edit_path:update_shape` の `follow_terrain`(常送)としてUIへ返す。`height_editable` バリアント以外では無視される。
- **軌道複線の横オフセット列・進行方向列もUI→Unityのライブ設定イベントを持つ**: `edit_path:change_lateral_offsets`(`IpcClient.OnEditPathChangeLateralOffsets` → `IpcEventHandler.HandleEditPathChangeLateralOffsets` → `RoadPlanManager.SetLateralOffsets`。params `lateral_offsets`=float配列(m、敷設方向右が正)と `travel_directions`=int配列(`DIR_*`。`lateral_offsets`とindex対応))。複線(=単線パス群の平行敷設)はUIパネルの線路数・進行方向設定が真実源で、敷設中プレビュー(RoadRenderer)を各オフセット位置の平行パス群として描き直す。進行方向は各線の仮PathSummaryの`travel_direction`に載り、駅バリアントのホーム設置側の解決(`TrackPath.ResolvePlatformExtents`)と進行方向矢印の表示に使われる。空配列=単一パス。初期値は `world:change_state`(`STATE_EDIT_PATH`)の `lateral_offsets` / `travel_directions` params(`IpcStateCoordinator.ApplyStateEditPath` が `ReadFloatArray`/`ReadIntArray` で読み `RoadPlanManager.LateralOffsets`/`TravelDirections` へ設定。無指定は必ず空へ戻す)。確定時の平行展開はUI側が行い、Unityはプレビュー描画にのみ使う。
- **2Dパス形状編集パネル(Tauri `path_shape_editor`)との同期は「コマンド入力・全状態出力」**: `edit_path:shape_command`(`IpcClient.OnEditPathShapeCommand` → `IpcEventHandler.HandleEditPathShapeCommand` → `RoadPlanManager.ApplyShapeCommand`。params `op`=select_node/move_node/move_control/insert_node/remove_node と `index`/`edge_index`/`which`/`x`/`z`。`RoadPlanManager.ShapeCommand` 構造体)でXZだけを動かし、結果は既存の `edit_path:update_shape` で全状態を返す。`edit_path:query_shape`(`OnEditPathQueryShape` → `EmitShapeUpdateOnRequest`)は現在形状の問い合わせ。`update_shape` には2D向けに `phase`/`selected_index`/`lateral_offsets`/`height_editable`/`min_height`/`alignment`(検証済み線形または待機中のローカル推定。各サンプルに累積距離・路面標高・地盤標高、アンカーごとの `nodes` も含み、縦断面ストリップの高さ編集に使う)を `RoadPlanManager.BuildShapeSyncExtras` が追加し、`EmitEditPathUpdateShape` の `extras` 引数でトップレベルにマージする。吸着は適用しない。Editingフェーズ以外・3Dドラッグ中は無視する。Rust側の受信matchは既存の `update_shape` 転送で足りる(UI→Unityは汎用転送)。
- **ホーム設置側もUI→Unityのライブ設定イベントを持つ**: `edit_path:change_platform_side`(`IpcClient.OnEditPathChangePlatformSide` → `IpcEventHandler.HandleEditPathChangePlatformSide` → `RoadPlanManager.SetPlatformSide`)。ホーム設置側(`platform_side`)は上記の吸着・標高等と違いUnityの操作系に無くUIパネルのトグルが真実源のため、切り替えを逐次Unityへ送って敷設中プレビューメッシュ(RoadRenderer→TrackPath)のプラットフォーム張り出し側に反映する。初期値は `world:change_state`(`STATE_EDIT_PATH`)の `platform_side` params で渡す(`IpcStateCoordinator.ApplyStateEditPath` が `RoadPlanManager.PlatformSide` へ設定)。駅バリアント以外では無視される。
- **パスの結合・分割の対象選択は専用ステート**: `STATE_MERGE_PATH` / `STATE_SPLIT_PATH`(params `path_id`=パス詳細パネルで開いている操作対象)を `IpcStateCoordinator.ApplyStatePathTopologyEdit` が受け、入力プリセット `PATH_MERGE`/`PATH_SPLIT` と `PathTopologyEditManager`(動的生成)を初期化する。選択結果は `edit_path:select_merge_target`(`IpcClient.EmitPathMergeTargetSelected`。`{path_id, path_name}`)/`edit_path:select_split_point`(`EmitPathSplitPointSelected`。`{point:[x,y,z]}`=中心線へ射影済み)でUIへ返り、UIが `POST /paths/merge`・`POST /paths/{id}/split` を dry_run→確定の順で直接呼ぶ。**`STATE_EDIT_CONNECTOR` と同じくこの2ステートでは `WindowUtil.BringToForeground()` を呼ばない**(長さを確認して確定するパス詳細パネルが操作できなくなるため)。**確定時は `edit_path:finish_topology_edit`(params `removed_path_id`=結合で消えたパスID/分割は0)を1発だけ送る**。`HandleEditPathFinishTopologyEdit` が「選択モード終了 → 消えたパスの即時破棄(`PathManager.RemovePathLocally`) → `ApplyStateNormal` → `CellDataLoader.ReloadAllData`」をこの順で行う。**`session:reload` と `world:change_state` を別々に送ってはいけない**: 差分リロードは複数フレームにまたがるコルーチンで、後続の状態遷移が割り込むと再生成・削除が中途半端に終わる(道路敷設の `edit_path:finish` が1イベントにまとまっているのと同じ理由)。キャンセル時のみ `STATE_NORMAL` を送る。

終了は `STATE_NORMAL` で、`ApplyStateNormal` と `HandleChangeState` 冒頭のガードの双方が `PathTopologyEditManager.EndMode` を呼ぶ(別の編集モードへ直接遷移しても着色が残らない)。加えて **`edit_path:cancel_topology_edit`(Rust の `window.rs` がパス詳細パネルのクローズ時に送る)** を `HandleEditPathCancelTopologyEdit` が受け、選択モード中だったときだけ終了＋通常モードへ復帰する。**ポップアップは閉じても破棄されず非表示になるだけで React のアンマウント処理が走らないため、この専用イベントが無いと選択モードのまま3Dビューの入力が固まる**(素の `STATE_NORMAL` を送らないのは、ウィンドウを持たないブルドーザ等のトグルモードを巻き込まないため)。
- **駅パッケージ配置は専用ステート + コマンド/状態の往復**: `STATE_EDIT_STATION`(params `platform_variant` / `house_variant` / `platform_length` / `track_spacing` / `height` / `angle` / `travel_directions` / `house_ends`)を `IpcStateCoordinator.ApplyStateEditStation` が受け、入力プリセット `STATION_PLACEMENT`(TERRAIN=`MODE_EDIT_STATION`、他=`MODE_NONE`)と `StationPlanManager`(動的生成)を初期化する。**進入時に `RoadPlanManager.FinishRoadPlacement()` を呼び、逆に `STATE_EDIT_STATION` 以外への遷移では `StationPlanManager.FinishPlacement()` を呼ぶ**(軌道タブ⇔駅タブの直接切替で前のプレビューが残らないようにする相互の後始末)。設定変更は `edit_station:placement_command`(UI→Unity。`IpcClient.OnEditStationPlacementCommand` → `IpcEventHandler.HandleEditStationPlacementCommand` → `ApplyPlacementCommand`。変更キーのみの辞書。`reset:true`=地点の固定だけ解除)、結果は `edit_station:placement_state`(Unity→UI。`IpcClient.EmitEditStationPlacementState`)で全状態を返す(建物直接配置の `placement_command`/`placement_state` と同じ構図で、UIは受信値をエコーしない)。**`placement_state` の `platforms`/`house` は確定送信にそのまま使える中心線**(`[始点, 終点]` の `[x, 相対高さ, z]`)で、幾何計算をUIに二重実装しないためUnityが解決して渡す。確定後の後始末は既存の `edit_path:finish` に相乗りする(`HandleEditPathFinish` が `StationPlanManager.FinishPlacement` も呼ぶ)ため、専用の finish イベントは持たない。Rust側 `ipc.rs` には `edit_station:placement_state` の中継 match のみ追加(UI→Unity は汎用転送)。
- **地区編集の「地区の統合」だけはUIの確定を挟む**: 作成・拡張は3Dジェスチャ完結でUnityが直接バックエンドへ送るが、統合は地区がひとつ消える不可逆操作なので、Unityは統合元・統合先が決まった時点で `edit_zone:request_merge`(Unity→UI, `IpcClient.EmitEditZoneRequestMerge`。`{target_zone_id, target_zone_name, source_zone_id, source_zone_name}`)を送るだけにする。地区編集モードは自前のウィンドウを持たない常駐モードのため、**常駐している `icon-panel` ウィンドウがこれを受けて確定専用の小ウィンドウ(`zone_merge` = `ZoneMergeConfirmWindow`)を `params.extra.pendingMerge` 付きで開く**(イベントを先に投げてからウィンドウを開くと、パネル側の購読が間に合わないため確定内容はウィンドウコンテキストに載せて渡す)。確定ボタンでUI側が `POST /zones/{id}/merge` を送り、結果を `edit_zone:finish_merge`(UI→Unity, params `merged`=1/0)で返す。ウィンドウが×で閉じられた場合は Rust 側(`window.rs`)が `merged:0` を送って確定待ちを解除する。`IpcStateCoordinator.HandleEditZoneFinishMerge` → `ZoneEditContext.OnMergeFinished` が確定待ちを解除し、merged=1 なら地区・道路を再取得する(**地区編集モードはそのまま継続する**。統合はモードを抜ける操作ではないため `STATE_NORMAL` は送らない)。Rust側 `ipc.rs` の受信match・TS側 `ipc.ts` の定数(`IPC_EVENT_EDIT_ZONE_REQUEST_MERGE`)と送信関数(`sendEditZoneFinishMerge`)も揃えて追加済み。
- **3Dカメラの状態は問い合わせ方式で、追従配信はしない**: `camera:query_state`(UI→Unity)を受けて `IpcClient.EmitCameraState` が `camera:state`(`{position, yaw}`)を1回だけ返す(`view:query_state`/`view:current_state` と同じ形)。2D都市マップがカメラマーカーを描くためのもので、開いたときと再読み込みのときにUIが問い合わせる。**カメラの動きに追従させる常時配信は意図的に持たない**(毎フレーム動くためMMFのリングバッファを無駄に埋める)。`yaw` は北=ワールド+Zを0とする時計回りの度で、真下を向いていて視線の水平成分が消える場合はカメラのY軸回転で代用する。
- `view:grid_toggled`(Unity→UI)は、ラジアルメニューでグリッド表示をトグルしたときに `GridOverlay.ToggleVisibility` が送る(`{visible}`)。UI側は道路建設パネルのグリッド設定UIの表示状態を追従させるだけで、グリッド表示⇔グリッド吸着の連動はUnity側(`GridOverlay.HandleViewSetGrid` / `ToggleVisibility`)が行う。
- **既存建物コピー建設のコピー元選択は専用の入力モードで行う**(ステートは切り替えない): `building_construction:pick_source`(UI→Unity, `IpcClient.OnBuildingConstructionPickSource`)を建物建設パネルの「既存建物をコピー」ボタンが送り、`IpcStateCoordinator.HandleBuildingConstructionPickSource` が入力プリセット `PICK_BUILDING`(建物だけクリック可能な `MODE_PICK_BUILDING`)へ切り替える。建物が選ばれると `BuildingInputHandler`(`MODE_PICK_BUILDING` 分岐)が `BuildingManager.GetBuildingSummary` から variant/floors/visual_variant を読み、`building_construction:copy_source`(Unity→UI, `IpcClient.EmitBuildingConstructionCopySource`。`{source_agent_id, variant, floors, visual_variant, visual_variant2, visual_variant3}`)で通知する。**実寸フットプリントは送らずバックエンドが `copy_source_agent_id` から導出する**(`crud_buildings.generate_copied_building`)。UIはこれをパネルへシードし、通常の `STATE_EDIT_BUILDING`(新規建設。区画選択フロー)へ遷移する。以降 preview/construction-cost/POST は `copy_source_agent_id` を付けて呼び、忠実コピー時のみ建設体積が割引かれる。Rust側 `ipc.rs` の受信match(`building_construction:copy_source`)・TS側 `ipc.ts` の送信関数 `sendPickCopySource` と定数 `IPC_EVENT_BUILDING_CONSTRUCTION_COPY_SOURCE` も揃えて追加済み。**この選択モードはUnity側では「既存の建物を1つ選ぶ」だけの汎用動作**で、テンプレートモードの経済主体作成パネル(`agent_creation`。入居先建物の選択)も同じ `pick_source` / `copy_source` を使い、`source_agent_id` だけを読む。Unityはイベント名以外(送信元 `window_type`)を見ないので、この経路を使うウィンドウ同士はUI側の `window-mutex-groups.json` で同時に開けないようにして通知の取り合いを防ぐ。

STATE_EDIT_PATHの任意initial_height/initial_embankment_height/initial_curved/initial_follow_terrainは継続配置設定。InitializeRoadPlacementへ渡し、形状通知前に反映する。確定終了と初期化の間のUnity状態には依存しない。

## 建物の配置状態

`edit_building:placement_command`(UI→Unity)はparams.command={variant, width?, depth?, angle?, reset?, labels?}。指定した寸法だけ変更し、状態を`edit_building:placement_state`(Unity→UI)で返す。空の変更差分は状態問い合わせ。labelsはパネルの選択言語で翻訳したCtrlメニュー/HUD文言。`placement_state`はvariant/revision/mode(parcel|free|roadside)/position(null=未固定)/width/depth/angle/parcel_idを一括通知し、UIは受信値を再送しない。Ctrl切替・位置固定・寸法キー・回転リリース・Escも同じ通知経路。直接配置の権威プレビュー`update_shape`には要求元のplacement_revisionを付け、Unityが現在世代・階数と照合する。既存建物編集は従来の世代なし区画プレビュー経路。`STATE_EDIT_BUILDING`のcopy_source_agent_idがある忠実コピーでは直接配置を無効化する。`STATE_EDIT_BUILDING`の任意params`placement_mode`(parcel|free|roadside)は配置方法の初期値で、`ApplyStateEditBuilding`が`DirectAllowed`確定後に`SetPlacementMode`で適用する(それより前に呼ぶとガードで無視される)。省略時は区画配置。都市創設時の市庁舎はUIが`free`を渡す。

寸法コマンドの任意request_idはplacement_stateへ返す。パネルは最後の要求IDへの応答だけで数値変更待ちを解除する。

edit_path:change_house_endsのhouse_ends（both/start/end）をRoadPlanManager.SetHouseEndsへ渡す。STATE_EDIT_PATHも同名paramsで初期化する。

`edit_path:update_shape` の `can_confirm` は形状確定かつローカル交差なしのときだけtrue。Rustは既存イベントのparamsを透過転送する。HighlightManagerは建物・パスを着色せず、OccupancyVolumeUtilの半透明箱を合成する。`colliding_river_ids`(川ID文字列の配列。`PathInterferencePayload.CollidingRiverIds`)の川は範囲全体を `RiverStore.AddOccupancyBox` の箱で示す。path_build由来の交差ではRoadPlanManagerにも通知して敷設中の全線を箱で示し、解除時に消す。

## 経路提案のIPC

`edit_path:update_shape.route_proposal`は絶対標高の点と制御点、盛土量、補助点フラグ、revision、can_undoを持つ。既存の相対座標フィールドとは別に扱う。`edit_path:shape_command`のapply_route(revision/route_json)とundo_route(revision)を`RoadPlanManager`へ渡す。適用はEditingかつ非ドラッグ中で、世代と固定点が一致するときのみ。詳細は公開`programs/経路提案.md`。

## パス線形検証の同期

`edit_path:update_shape.revision` はUnityの編集世代。UIは全線validateの `alignments` を `edit_path:shape_command` の `{op: "apply_alignment", revision, alignment_json}` で返す。nullは待機・再試行時の破棄。Unityは同じ世代のEditingだけ適用し、`alignment` に `authoritative`・`curve_violated`・`grade_violated`・全線の `warnings`（各点 `[x, 絶対標高, z]` の折れ線群）を追加して再配信する。UIは選択・警告だけの再通知で検証を再起動しない。Rustは既存の汎用転送を使う。

公共交通ハイライトは既存view:change_viewのtransitレイヤーでmode=hidden/defaultを全体OFF/ONとして扱う（既定hidden）。sub_modeの種別CSVはOFF中も保持する。Unityの路線・車両選択は既存EmitSelectObject("transit_detail", "agent", 路線ID)を使い、新しいIPCイベントは持たない。

edit_path:change_passage_modeのpassage_mode（elevated/ground/underground）はRoadPlanManager.SetPassageModeへ渡す。STATE_EDIT_PATHとSTATE_EDIT_STATIONの初期値、edit_station:placement_command/stateにも同名属性を含む。

## 初期ロードの表示契約

UI→Unityは既存の `ui:action` Envelopeの `payload.action` / `payload.params` を使う。`game:loading` は黒画面と入力抑制を開始する。`game:start` のparamsは `{loading_id: 整数}` で、描画準備を開始するが黒画面は維持する。Unity→UIの `game:loaded` はpayload直下の `{loading_id: 整数}` を返す。初期描画完了後、表示解禁まで1秒間隔で再送し、Rustは現在の要求世代だけを受理する。UI→Unityの `game:reveal` は同じloading_idをparamsに載せ、`game:loaded` を通知済みの世代だけを表示する。受信した瞬間の準備状態は再判定しない（Tipsは既に閉じているため、ここで捨てると黒画面から戻れなくなる）。

`system:graphic_ready` はプロセスの起動完了であり、都市の描画完了ではない。遅延起動・再接続ではRustが開始要求を再送する。ゲーム中の再接続でもTipsを使い、初期設定は再表示しない。

`system:graphic_stopped` は `IpcClient.Disconnect()` がMMFを閉じる直前に送る正常終了通知で、`AppRoot.OnApplicationQuit`（EditorのPlay停止でも発火）と `IpcClient.OnDestroy` から届く。Rustはこれで接続中フラグを下ろし、開発時のUnityなし起動でTipsが描画完了を待っていればその場で開始ボタンへ切り替える。クラッシュでは送られない（その場合はスーパーバイザが再起動したUnityの `system:graphic_ready` で整合する）。

## 制限速度標識トグル

`view:change_view`のroadレイヤーの`flags`は`speed_limit`を受け付ける（`VisualFlag.SPEED_LIMIT_SIGNS`）。通常表示・色分け・planと独立し、既存のcurrent_state・通常状態記憶・編集プリセットからの復元で同期する。標識の数字はHTTPのPathSummary.max_speedから読み、IPCへ速度値を複製しない。

## 道路(名前付き道路)のIPC

送信側はTauriの `utils/ipc.ts` の「道路(名前付き道路)」節。UI→Unity はすべて既存の `send_window_action`/`ui:action` の汎用転送で、Rust の `ipc.rs` には Unity→UI の `edit_road:toggle_path` の中継だけがある。いずれも操作時に1通ずつ送るイベントで、周期送信はしない。

- `world:set_road_focus {road_id, color, path_ids}`(UI→Unity): 道路詳細パネルが開いている道路の区間を道路色の帯で強調する。送られるたびに全置換。`IpcClient.OnWorldSetRoadFocus` → `IpcEventHandler` → `RoadFocusOverlay.SetFocus`。`world:clear_road_focus {}` で消す(パネルのクローズ時は Rust の `window.rs` も送る)。干渉表示の `world:highlight_objects` とは別系統。
- `world:refresh_roads {}`(UI→Unity): 道路の作成・改名・所属変更・削除の直後に送られ、`RoadManager.RequestRefresh` で一覧(道路名ラベル)を取り直す。version が同じならラベルは作り直さない。
- `world:change_state` の `STATE_EDIT_ROAD_MEMBERS`(params `{state, road_id, color, path_ids}`): 区間編集モード。`IpcStateCoordinator.ApplyStateEditRoadMembers` が入力プリセット `ROAD_MEMBER_EDIT` と `RoadMemberEditManager.Begin` を行い、表示は `SyncFromState` の建設系プリセット(計画中の区間も選べるよう plan 表示)。**`WindowUtil.BringToForeground()` は呼ばない**(詳細パネルを操作し続けるため)。終了は `STATE_NORMAL`(`sendEndRoadMemberEdit`)。
- `edit_road:set_members {road_id, path_ids, preview_path_ids}`(UI→Unity): 区間編集中の所属区間と自動延長の追加候補の全集合。`RoadMemberEditManager.SetMembers`(モード中でない・別の道路宛ては無視)。
- `edit_road:toggle_path {path_id, path_name, path_type, shift}`(Unity→UI, `IpcClient.EmitRoadTogglePath`): 区間編集中に街路・高速道路の区間がクリックされた。shift=Shift キー押下。付け外しはUIがAPIで行い、結果を `set_members` で返す。
- `edit_road:cancel {}`(UI→Unity。パネルのクローズ時に Rust の `window.rs` が送る): 区間編集モード中なら `HandleEditRoadCancel` が終了して通常モードへ戻す。モード中でなければ何もしない(`edit_path:cancel_topology_edit` と同じ理由と方式)。
- 道路名ラベルのクリックは `world:select_object {window_type:"road_detail", entity:{type:"road", id:"<road_id>"}}`(`EmitSelectObject`)。
- セッションリセットでは区間編集を終了し、強調も消す(パスIDの集合は別の都市では別の区間を指すため)。
