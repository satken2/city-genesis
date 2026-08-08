# IPC フォルダ

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

1. `IpcClient.cs`に型付きイベント(`event Action<...> OnXxx`)または送信メソッドを追加
2. city-genesis/tauri側にも対応する送受信を追加(`city-genesis/tauri/src/utils/ipc.ts`と`city-genesis/tauri/src-tauri/src/`)
3. IPCプロトコルは両コンポーネントの開発契約なので、`genesis-docs`のprograms配下のIPC仕様ドキュメントも更新する

## 編集モードのステート同期(参考)

- `world:change_state` は `IpcStateCoordinator.HandleChangeState` が受け、`STATE_*` ごとに InputMode プリセット + VisualMode をまとめて切り替える。`STATE_EDIT_TRANSIT`(公共交通の路線設置)は params の `path_variant` と `stop_path_variants`(int配列。`ReadIntArray` で JArray→int[] 変換)を読み、`TransitPlanManager` を動的生成・初期化する。
- `view:change_view` は `IpcViewCoordinator` が受け、レイヤーごとに VisualMode を切り替える。`transit` レイヤーは `ApplyTransitMode` が sub_mode(表示する `transit_path_id` のCSV)をパースして `TransitOverlayManager.SetVisibleVariants` に渡す。
- `edit_transit:update_stops`(Unity→UI, `IpcClient.EmitEditTransitUpdateStops`)は路線設置中の停留所座標列を通知する。`edit_transit:finish`(UI→Unity)は `IpcEventHandler.HandleEditTransitFinish` が受け、`TransitPlanManager.FinishPlacement` + `CellDataLoader.ReloadAllData` + `TransitOverlayManager.MarkDirty` を実行する(`edit_resource:finish` と同じ枠組み)。
- `edit_parcel:update_creation_shape`(Unity→UI, `IpcClient.EmitEditParcelUpdateCreationShape`)は土地区画作成モードの**任意ポリゴン**(`parcel_creation_mode="manual"`)で編集中のポリゴン頂点列と仮確定フラグ(`{polygon, finalized}`)を作成パネルへ通知する。**既存区画の形状編集で使う `edit_parcel:update_shape`(`EmitEditParcelUpdateShape(parcelId, polygon)`。ParcelDetailPanel が使用)とは用途もペイロードも異なる別イベント**なので混同しないこと(名前が紛らわしいが意図的に分けている)。確定は資源・公共交通と同じくUI側が直接POSTするため、専用の finish イベントは持たず `STATE_NORMAL` への遷移で `ParcelAreaEditManager.FinishEditMode` によりオーバーレイをクリアする。**既存区画の形状編集の確定(PATCH /parcels/{id})もUI側が直接行い**、保存成功後にUIが `edit_parcel:finish_shape_edit`(ペイロードなし)を送り、`IpcStateCoordinator` 経由で `ParcelShapeEditManager.CompleteEditMode`(編集終了+リロード+枠線再描画)と `ApplyStateNormal` が実行される。Rust側 `ipc.rs` の受信match・TS側 `ipc.ts` の定数(`IPC_EVENT_EDIT_PARCEL_UPDATE_CREATION_SHAPE`)も揃えて追加済み。

## 知らないとわからないこと

- イベント名は`source`/`destination`と`event`文字列で識別される。エンベロープの用語上、Unity側は"parent"と呼ばれる(Tauri側の`send_to_parent`はUnity宛て送信のこと)。
- **曲線/直線・吸着の設定系イベントは廃止済み**: `edit_path:change_curved` / `view:set_snapping` は存在しない。曲線/直線・吸着の真実源はUnity側(Ctrlラジアルメニュー、`Editors/RoadEditor/RoadEditControlMenu`)にあり、曲線モードは `edit_path:update_shape` の `is_curved`(点列が空でも常送)としてUIへ逆流する。
- **標高・盛土はパネルにもステッパーが残る**: `edit_path:change_node`(標高) / `edit_path:change_embankment`(盛土)はUIパネルのステッパー変更をUnityへ送るイベントとして現存(`OnEditPathChangeNode`/`OnEditPathChangeEmbankment`→`RoadPlanManager.OnChangeNodeFromIpc`/`OnChangeEmbankmentFromIpc`)。node_index=-1 は次回配置ノードの値を指す。逆にUnityのキー操作や選択で値が変わると `edit_path:select_node`(標高・盛土を含む)でパネルへ返す双方向同期。既存ノードを選んで数値で微調整する用途のため両方に口を残している。
- **地形追従の個別設定もUI→Unityのライブ設定イベントを持つ**: `edit_path:set_follow_terrain`(`IpcClient.OnEditPathSetFollowTerrain` → `IpcEventHandler.HandleEditPathSetFollowTerrain` → `RoadPlanManager.SetFollowTerrain`。params `value`=-1未設定/0非追従/1追従)。ホーム設置側と同じくパネルのトグルが真実源で、切り替えを逐次Unityへ送って敷設中プレビューの地形追従・トンネル可視化に反映し、確定送信用に `edit_path:update_shape` の `follow_terrain`(常送)としてUIへ返す。`height_editable` バリアント以外では無視される。
- **軌道複線の横オフセット列・進行方向列もUI→Unityのライブ設定イベントを持つ**: `edit_path:change_lateral_offsets`(`IpcClient.OnEditPathChangeLateralOffsets` → `IpcEventHandler.HandleEditPathChangeLateralOffsets` → `RoadPlanManager.SetLateralOffsets`。params `lateral_offsets`=float配列(m、敷設方向右が正)と `travel_directions`=int配列(`DIR_*`。`lateral_offsets`とindex対応))。複線(=単線パス群の平行敷設)はUIパネルの線路数・進行方向設定が真実源で、敷設中プレビュー(RoadRenderer)を各オフセット位置の平行パス群として描き直す。進行方向は各線の仮PathSummaryの`travel_direction`に載り、駅バリアントのホーム設置側の解決(`TrackPath.ResolvePlatformExtents`)と進行方向矢印の表示に使われる。空配列=単一パス。初期値は `world:change_state`(`STATE_EDIT_PATH`)の `lateral_offsets` / `travel_directions` params(`IpcStateCoordinator.ApplyStateEditPath` が `ReadFloatArray`/`ReadIntArray` で読み `RoadPlanManager.LateralOffsets`/`TravelDirections` へ設定。無指定は必ず空へ戻す)。確定時の平行展開はUI側が行い、Unityはプレビュー描画にのみ使う。
- **ホーム設置側もUI→Unityのライブ設定イベントを持つ**: `edit_path:change_platform_side`(`IpcClient.OnEditPathChangePlatformSide` → `IpcEventHandler.HandleEditPathChangePlatformSide` → `RoadPlanManager.SetPlatformSide`)。ホーム設置側(`platform_side`)は上記の吸着・標高等と違いUnityの操作系に無くUIパネルのトグルが真実源のため、切り替えを逐次Unityへ送って敷設中プレビューメッシュ(RoadRenderer→TrackPath)のプラットフォーム張り出し側に反映する。初期値は `world:change_state`(`STATE_EDIT_PATH`)の `platform_side` params で渡す(`IpcStateCoordinator.ApplyStateEditPath` が `RoadPlanManager.PlatformSide` へ設定)。駅バリアント以外では無視される。
- **パスの結合・分割の対象選択は専用ステート**: `STATE_MERGE_PATH` / `STATE_SPLIT_PATH`(params `path_id`=パス詳細パネルで開いている操作対象)を `IpcStateCoordinator.ApplyStatePathTopologyEdit` が受け、入力プリセット `PATH_MERGE`/`PATH_SPLIT` と `PathTopologyEditManager`(動的生成)を初期化する。選択結果は `edit_path:select_merge_target`(`IpcClient.EmitPathMergeTargetSelected`。`{path_id, path_name}`)/`edit_path:select_split_point`(`EmitPathSplitPointSelected`。`{point:[x,y,z]}`=中心線へ射影済み)でUIへ返り、UIが `POST /paths/merge`・`POST /paths/{id}/split` を dry_run→確定の順で直接呼ぶ。**`STATE_EDIT_CONNECTOR` と同じくこの2ステートでは `WindowUtil.BringToForeground()` を呼ばない**(長さを確認して確定するパス詳細パネルが操作できなくなるため)。**確定時は `edit_path:finish_topology_edit`(params `removed_path_id`=結合で消えたパスID/分割は0)を1発だけ送る**。`HandleEditPathFinishTopologyEdit` が「選択モード終了 → 消えたパスの即時破棄(`PathManager.RemovePathLocally`) → `ApplyStateNormal` → `CellDataLoader.ReloadAllData`」をこの順で行う。**`session:reload` と `world:change_state` を別々に送ってはいけない**: 差分リロードは複数フレームにまたがるコルーチンで、後続の状態遷移が割り込むと再生成・削除が中途半端に終わる(道路敷設の `edit_path:finish` が1イベントにまとまっているのと同じ理由)。キャンセル時のみ `STATE_NORMAL` を送る。

終了は `STATE_NORMAL` で、`ApplyStateNormal` と `HandleChangeState` 冒頭のガードの双方が `PathTopologyEditManager.EndMode` を呼ぶ(別の編集モードへ直接遷移しても着色が残らない)。加えて **`edit_path:cancel_topology_edit`(Rust の `window.rs` がパス詳細パネルのクローズ時に送る)** を `HandleEditPathCancelTopologyEdit` が受け、選択モード中だったときだけ終了＋通常モードへ復帰する。**ポップアップは閉じても破棄されず非表示になるだけで React のアンマウント処理が走らないため、この専用イベントが無いと選択モードのまま3Dビューの入力が固まる**(素の `STATE_NORMAL` を送らないのは、ウィンドウを持たないブルドーザ等のトグルモードを巻き込まないため)。
- `view:grid_toggled`(Unity→UI)は、ラジアルメニューでグリッド表示をトグルしたときに `GridOverlay.ToggleVisibility` が送る(`{visible}`)。UI側は道路建設パネルのグリッド設定UIの表示状態を追従させるだけで、グリッド表示⇔グリッド吸着の連動はUnity側(`GridOverlay.HandleViewSetGrid` / `ToggleVisibility`)が行う。
