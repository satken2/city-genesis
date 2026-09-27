# 前提

Unityのグラフィックプログラム(以下、`graphic`)と、TauriのUIプログラム(以下、`ui`)は、WindowsのMemoryMappedFile（MMF）2本でIPC連携を行う。

## MMF構成

| MMF名 | 方向 | 書き込み | 読み取り |
|-------|------|---------|---------|
| `genesis-ipc-ui-to-graphic` | UI→Graphic | Tauri | Unity |
| `genesis-ipc-graphic-to-ui` | Graphic→UI | Unity | Tauri |

双方とも `CreateOrOpen` で共有メモリを開く。どちらが先に起動しても新規作成または既存を開くため、起動順序に依存しない。

## 同期オブジェクト

各方向にMutexとEventWaitHandle（AutoResetEvent）を1つずつ使用する。

| 名前 | 種類 | 用途 |
|------|------|------|
| `genesis-ipc-mutex-utg` | Mutex | UI→Graphic バッファの排他アクセス |
| `genesis-ipc-event-utg` | AutoResetEvent | UI→Graphic の書き込み通知 |
| `genesis-ipc-mutex-gtu` | Mutex | Graphic→UI バッファの排他アクセス |
| `genesis-ipc-event-gtu` | AutoResetEvent | Graphic→UI の書き込み通知 |

## MMFレイアウト

各MMFは1MiB（1048576バイト）。

```
Offset  0: WritePos  (int32) — 次の書き込み位置（バッファ内オフセット）
Offset  4: ReadPos   (int32) — 次の読み取り位置（バッファ内オフセット）
Offset  8: Ring buffer data (1048568 bytes)
  各メッセージ: [MessageLength (int32)][UTF-8 JSON bytes]
```

- 書き込み側がMutexを取得し、リングバッファにメッセージを書き込み、Mutexを解放してEventをSetする。
- 読み取り側がEventのWaitOneで待機し、通知されたらMutexを取得してメッセージを読み出す。
- バッファ末尾にメッセージが収まらない場合、ラップマーカー（length = 0）を書き、メッセージを先頭から書く。読み取り側はラップマーカーを見たら（または末尾にlengthが収まらなければ）ReadPosを0にリセットし、同じ通知の中で先頭のメッセージを続けて読む。
- WritePos == ReadPos は「空」を意味する。ラップ時に先頭の空きをReadPosちょうどまで埋めると満杯と空が区別できなくなるため、書き込み側はその書き込みを拒否する。
- 読み取り側はlengthが範囲外なら未読分を捨て（ReadPosをWritePosに合わせ）、以降の通信を続ける。
- 受信側のプロセスが起動していない間の送信もバッファに溜まり、受信側は起動後にまとめて読む。空きが足りない書き込みは `MMF buffer full` で失敗する。このため一定周期で送り続けるメッセージは置かない。受信側の再起動後の再同期は、グラフィックが送る `system:graphic_ready` を起点にするか、グラフィック側が起動時に自分から状態を送る。

# イベント種別

## 命名規則

イベント名は全レイヤー（Unity / Rust / フロントエンド）でコロン区切りで統一する（例: `edit_path:select_node`）。

## イベントを追加・変更するときの変更箇所

1つのイベントは Unity・Rust・フロントエンドの3箇所にまたがる。どこかが欠けても実行時エラーにはならず、単に届かない。

- **Envelope**: `unity/Assets/Scripts/IPC/IpcEnvelope.cs` と `tauri/src-tauri/src/messages.rs` の `Envelope` は同一構造（`version` / `request_id` / `timestamp` / `source` / `destination` / `event` / `payload`）に保つ。パースできない通知は両側とも捨てる。
- **UI → graphic**: フロントエンドの `src/utils/ipc.ts` から Tauri コマンド `send_window_action` を呼ぶ。Rust はこれを `ui:action` Envelope（`payload.action` にイベント名、`payload.params` に引数）として汎用転送するので、Rust 側の変更は要らない。Unity は `IpcClient` の型付きイベント（`OnXxx`）で受け、`IpcEventHandler` / `IpcStateCoordinator` / `IpcViewCoordinator` が処理する。
- **graphic → UI**: Unity は `IpcClient.EmitXxx` で `event` 直下の名前として送る。Rust `ipc.rs` の受信 match に中継の分岐（全ウィンドウへの `emit`、常駐のコックピットだけが扱うものは `emit_to`）を追加しないと、どの WebView にも届かない。フロントエンドは同名の Tauri イベントを `listen` する。
- 追加・変更したイベントは、このページの一覧と関連する機能のページに反映する。

## 一覧

| イベント種類                          | コール方向            | 内容                                    |
| ------------------------------- | ---------------- | ------------------------------------- |
| `world:select_object`           | `graphic -> ui`  | オブジェクトを選択する                           |
| `world:change_state`            | `ui -> graphic`  | グラフィックのモード(通常モード、土地区画作成モード、etc.)を切り替え |
| `view:change_view`              | `ui -> graphic`  | 地形、建物、区画、道路、環境、アラートなどの表示モードを切り替え（`{layer, mode, sub_mode, flags}`）。`flags` は色モードと独立した表示トグルのCSV文字列（`plan`=計画中の表示、`public`=公共施設ハイライト、`speed_limit`=道路・線路の制限速度標識。road/building レイヤーのみ使用）。**`flags` が `null`（またはキー欠落）の場合は現在のフラグを維持**し、空文字は全トグルOFFを意味する |
| `view:query_state`              | `ui -> graphic`  | グラフィックプログラムに現在の表示状態を問い合わせる            |
| `view:apply_preset`             | `ui -> graphic`  | ウィンドウを開いている間だけ表示を差し替える（`{preset}`）。プリセットの中身はUnity側が持ち、UIは名前だけを指定する。**専用の編集ステート（`STATE_*`）を持たないウィンドウ専用**で、ステートを持つ操作ではUnityがステート別の表示プリセットを自動適用する。元の表示への復帰は `STATE_NORMAL` への復帰が担う（[Unity入力制御 §9.1](Unity入力制御.md#91-ステート別の表示プリセット)）。現在のプリセットは `transit`（公共交通の一覧・新設） |
| `view:current_state`            | `graphic -> ui`  | `view:query_state`への応答、およびステート変更時の表示状態通知。各レイヤーは `{mode, sub_mode, flags}`（`flags` はONのトグルのCSV。無ければ空文字） |
| `view:set_object_filter`        | `ui -> graphic`  | 建物・道路を**オブジェクト単位**で非表示にする集合を送る（`{hidden_building_ids, hidden_path_ids}`）。差分ではなく毎回「今隠しているものの全集合」を送る。レイヤー単位の`view:change_view`とは直交し、こちらが立っているオブジェクトは色モード・計画中表示に関わらず消える（クリックも拾わない）。状態はUnityのセッション内だけに存在し、バックエンドにもセーブにも残らない。詳細は[建設プロジェクト管理](建設プロジェクト管理.md) |
| `view:query_object_filter`      | `ui -> graphic`  | オブジェクト単位の表示フィルタの現在値を問い合わせる（パネルを開いたときに送る） |
| `view:object_filter_state`      | `graphic -> ui`  | `view:query_object_filter`への応答、およびフィルタ適用後の通知（`{hidden_building_ids, hidden_path_ids}`）。表示状態の正はUnity側にあり、複数のパネルがこれを受けて表示を揃える |
| `view:set_grid`                 | `ui -> graphic`  | グリッドの表示有無や設定を切り替え。Unity側はグリッド表示とグリッド吸着を連動させる（表示ON=吸着ON） |
| `view:grid_toggled`             | `graphic -> ui`  | 3Dビューのラジアルメニューでグリッド表示がトグルされた（`{ visible }`）。UI側は道路建設パネルのグリッド設定UIの表示状態を追従させる |
| `camera:query_state`            | `ui -> graphic`  | 3Dカメラの現在位置と視線方位を問い合わせる。2D都市マップがカメラマーカーを描くために、開いたときと再読み込みのときに送る |
| `camera:state`                  | `graphic -> ui`  | `camera:query_state`への応答。3Dカメラの現在位置と視線方位（`{ position:[x,y,z], yaw }`）。`yaw` は北（ワールド+Z）を0とする時計回りの度。**カメラの動きへの追従配信は行わない**（問い合わせ1回につき1回だけ送る） |
| `edit_path:select_node`         | `graphic -> ui`  | パス敷設モードにおいて、敷設済みノードを選択した（`node_id`, `height`=相対標高, `embankment_height` を含む。`node_id=-1` は次回配置ノードの値）。UIパネルの標高・盛土ステッパーの表示と編集対象ノードを同期する。Unityのキー操作や吸着で値が変わったときも送られる |
| `edit_path:change_node`         | `ui -> graphic`  | パス敷設モードにおいて、パネルの標高ステッパーからノードの相対標高を変更した（`node_index`, `height`。`node_index=-1` は次回配置値）。曲線/直線・吸着はラジアルメニューが真実源だが、標高・盛土は既存ノードを選んで数値で微調整する用途のためパネルにもステッパーを残し、Unityキー操作と双方向同期する |
| `edit_path:change_embankment`   | `ui -> graphic`  | パス敷設モードにおいて、パネルの盛土ステッパーから盛土オフセット(-5〜+5m)を変更した（`node_index`, `embankment_height`。`node_index=-1` は次回配置値） |
| `edit_path:update_shape`        | `graphic -> ui`  | パス敷設モードにおいて、敷設中パスの形状が変更された。`selected_points`（全アンカー `[x, 相対高さ, z]`）、`start_control` / `end_control`（端点のベジエ制御点）、`waypoint_controls`（中間アンカーごとの `{control_in, control_out}`。未設定は `null`）、`embankment_heights`、`is_curved`（曲線モードの現在値。真実源はUnity側のラジアルメニューで、点列が空でも常に送られる）、`follow_terrain`（地形追従の個別設定。`-1`＝未設定／`0`＝非追従／`1`＝追従。真実源はUnity側で常に送られる）を含む。UIは確定送信時にこれらの制御点を `start_control` / `end_control` / `waypoints` として、`is_curved` を `curved` として、`follow_terrain`（`-1` 以外のときのみ）をそのままバックエンドへ渡し、調整した形状を永続化する。**2Dパス形状編集パネル向けの追加フィールド**: `phase`（`placing`/`editing`/`inactive`）、`selected_index`（3D側で選択中のノード添字。-1=未選択）、`lateral_offsets`（複線の横オフセット列。単線と、1本で複線を持つLRT専用軌道は空）、`alignment`（検証済み線形、取得待ちではUnityのローカル推定。`radius_limit`/`grade_limit`/`min_curve_radius`/`max_grade`、約5m間隔の `samples`=`[x, z, 曲線半径(直線はnull), 勾配%, 始点からの距離m, 路面の絶対標高, 地盤の絶対標高]`、原因別違反区間の最悪値 `labels`、アンカーと同順の `nodes`=`[始点からの距離m, アンカー直下の地盤標高]`。線形評価の対象外の種別では省略）、`height_editable`（バリアントが標高編集可能か。縦断面ストリップの表示条件）、`min_height`（相対高さの下限m）。全WebViewへブロードキャストされるため、敷設パネルと2Dパネルの両方が同じイベントを受ける |
| `edit_path:change_platform_side` | `ui -> graphic` | 駅パス敷設モードにおいて、ホーム設置側（`platform_side`=外側/内側/両側。`PLATFORM_SIDE_*`）を変更する。Unity側は敷設中プレビューメッシュのプラットフォーム張り出し側を即座に切り替える。駅バリアント（`platform_width>0`）以外では無視される。初期値は `world:change_state`（`STATE_EDIT_PATH`）の `platform_side` で渡し、以後の切り替えをこのイベントで逐次反映する |
| `edit_path:set_follow_terrain`  | `ui -> graphic`  | パス敷設モードにおいて、地形追従の個別設定（`value`=`-1`未設定/`0`非追従/`1`追従）を変更する。Unity側は敷設中プレビューの地形追従・トンネル可視化を即座に切り替え、確定送信用に `edit_path:update_shape` の `follow_terrain` としてUIへ返す。`height_editable` のバリアント以外では無視される |
| `edit_path:change_lateral_offsets` | `ui -> graphic` | パス敷設モードにおいて、複合パス（軌道複線＝単線パス群の平行敷設）の各線の横オフセット（`lateral_offsets`=float配列。m。敷設方向右が正）と進行方向（`travel_directions`=int配列。`DIR_*`。`lateral_offsets` とindex対応）を変更する。Unity側は敷設中プレビューメッシュを各オフセット位置の平行パス群として即座に描き直す（空配列は単一パス＝中心線1本のプレビューに戻す）。進行方向は駅バリアントのホーム設置側の解決と進行方向矢印の表示に使われる。UIパネルで線路数・進行方向を変えたときに送られ、配置中のノード列は維持される。初期値は `world:change_state`（`STATE_EDIT_PATH`）の `lateral_offsets` / `travel_directions` で渡し、以後の変更をこのイベントで逐次反映する。確定時の平行展開・一括作成はUI側が行うため、このイベントはプレビュー表示専用。1本で複線を持つ線路バリアント（線路・モノレールで `lanes`≥2。現行はLRT専用軌道431）と人員搬送（`PATH_CONVEYOR`）は平行展開の対象外で、UIは空の `lateral_offsets` / `travel_directions` を送り、Unityは単一パスでプレビューする（431の2軌道はそのパスの中に描かれる）。確定時も1本のパスを `travel_direction`=`DIR_BOTH` で作成する |
| `edit_path:shape_command`       | `ui -> graphic`  | 2Dパス形状編集パネル（`path_shape_editor`）からのノード・制御点操作。`op`=`select_node`（`index`）/ `move_node`（`index`,`x`,`z`）/ `move_control`（`index`,`which`=`in`/`out`,`x`,`z`）/ `insert_node`（`edge_index`,`x`,`z`）/ `remove_node`（`index`）。XZだけを動かし、地面相対の高さ・制御点の絶対標高はUnity側が維持する（3DのXZドラッグと同じ規則）。吸着は適用しない。**ドラッグ中は送らずドラッグ終了時に1回だけ送る**。Unityは `Editing` フェーズ以外・3Dでドラッグ中は無視し、適用後は `edit_path:update_shape`（全状態）と、ノード操作なら `edit_path:select_node` を返す |
| `edit_path:query_shape`         | `ui -> graphic`  | 敷設中パスの現在形状の問い合わせ。Unityは `edit_path:update_shape` を1回返す（セッション外なら `phase=inactive`）。`update_shape` は変化時にしか送られないため、開くたびに再マウントされる2Dパス形状編集パネルがマウント時に送る（`camera:query_state` と同じ問い合わせ応答型） |
| `edit_path:select_merge_target` | `graphic -> ui`  | パス結合の相手選択モード（`STATE_MERGE_PATH`）において、結合相手のパスがクリックされた（`path_id`, `path_name`）。UI（パス詳細パネル）はこのIDで `POST /api/v1/paths/merge`（`dry_run=true`）を叩き、結合前後の長さを表示してから確定する |
| `edit_path:select_split_point`  | `graphic -> ui`  | パス分割の分割点選択モード（`STATE_SPLIT_PATH`）において、分割点がクリックされた（`point`=[x,y,z]。対象パスの中心線へ射影済み）。UIはこの地点で `POST /api/v1/paths/{id}/split`（`dry_run=true`）を叩き、前半・後半の長さを表示してから確定する |
| `edit_path:finish_topology_edit` | `ui -> graphic` | パスの結合／分割の確定送信に成功したことを通知する（`removed_path_id`＝結合で吸収されて消えたパスID。分割時は0）。Unity は「選択モード終了 → 消えたパスの即時破棄 → 通常モードへ復帰 → データ再取得」を1つのハンドラでこの順に行う。**`session:reload` と `world:change_state` を別々に送ってはいけない**——差分リロードは複数フレームにまたがるコルーチンで、後続の状態遷移が割り込むと再生成・削除が中途半端に終わり、形状が更新されない／消えたはずのパスが残る（道路敷設の `edit_path:finish` が1イベントにまとまっているのと同じ理由） |
| `edit_path:cancel_topology_edit` | `ui -> graphic` | パス詳細パネル（`path_detail`）のウィンドウが閉じられたときに Rust（`window.rs`）が送る打ち切り通知。Unity は結合／分割の対象選択モード中だった場合のみ、モードを終了して通常モードへ戻す（**ポップアップは閉じても破棄されず非表示になるだけで React のアンマウント処理が走らないため、パネル任せにすると選択モードのまま入力が固まる**）。選択モード中でなければ何もしない＝ブルドーザ等の他モードを巻き込まないための専用イベント |
| `edit_station:placement_command` | `ui -> graphic` | 駅パッケージ配置モード（`STATE_EDIT_STATION`）において、設定を変更する。変更したキーだけを送る（`platform_variant` / `house_variant` / `platform_length`（m） / `track_spacing`（m） / `height`（相対m） / `angle`（度） / `house_ends` / `travel_directions`）。`reset: true` は地点の固定だけを解除する（寸法・角度・高さは維持。パネルのキャンセル用）。キーを含まない呼び出しは現在の配置状態の問い合わせになる。Unity はゴーストへ即反映し、`edit_station:placement_state` で全状態を返す（建物直接配置の `placement_command`/`placement_state` と同じ構図） |
| `edit_station:placement_state`  | `graphic -> ui`  | 駅パッケージ配置の現在の状態（`{revision, position, angle, height, platform_length, track_spacing, platform_variant, house_variant, house_ends, platforms, house}`）。`position` は固定済みの配置地点 `[x, z]` で、カーソル追従中は `null`（確定できない）。`platforms` は複線の各線（index は横オフセットの左→右）、`house` は駅舎の中心線で、どちらも `[始点, 終点]` の `[x, 相対高さ, z]`。**確定送信にそのまま使える値をUnityが解決して渡す**（幾何計算をUIに二重に持たせないため）。ホバー移動では送らず、地点の固定・回転のリリース・高さ変更・`placement_command` の受信でのみ送る |
| `edit_building:select_parcel`   | `graphic -> ui`  | 建物建設モードにおいて、土地区画を選択した（parcel_id, polygon, position 等を含む） |
| `edit_building:select_point` | `graphic -> ui` | 旧定義。現行の建物直接配置は`placement_command`/`placement_state`を使用する |
| `edit_building:invalid_parcel`  | `graphic -> ui`  | 建物建設モードにおいて、選択中の建物種類と互換性のない土地区画がクリックされた（parcel_id を含む） |
| `building_construction:pick_source` | `ui -> graphic` | 既存の建物を1つ選ぶ選択モード（`MODE_PICK_BUILDING`。ステートは変えず入力プリセットだけ上書き）を開始する。建物建設パネルの「既存建物をコピー」と、テンプレートモードの経済主体作成パネル（入居先建物の選択）が使う。送信元ウィンドウ種別はUnityの動作に影響しないため、両パネルは `window-mutex-groups.json` で同時に開けない |
| `building_construction:copy_source` | `graphic -> ui` | 選択モードで建物が選ばれた（`{source_agent_id, variant, floors, structure, visual_variant, visual_variant2, visual_variant3}`）。建設パネルはこれをシードして `STATE_EDIT_BUILDING` へ遷移し、経済主体作成パネルは `source_agent_id` だけを使って `STATE_NORMAL` へ戻す |
| `edit_building:update_direct_params` | `ui -> graphic` | 旧定義。現行の建物直接配置は`placement_command`/`placement_state`を使用する |
| `edit_building:update_direct_angle` | `graphic -> ui` | 旧定義。現行の建物直接配置は`placement_command`/`placement_state`を使用する |
| `edit_building:update_shape`    | `ui -> graphic`  | 建物建設モードにおいて、建物の底面形状を変更した              |
| `edit_building:update_floors`   | `ui -> graphic`  | 建物建設モードにおいて、建物の階数を変更した。Unityは階数の保持・効果音・直接配置のローカル矩形ゴーストの作り直しだけを行い、バックエンドへ形状を問い合わせない。土地区画上のプレビュー形状は、UIが同じ階数変更で `POST /api/v1/buildings/preview` を取り直し `edit_building:update_shape` で届ける |
| `edit_building:update_structure` | `ui -> graphic` | 建物建設モードにおいて、構造種別 `structure`（木造=1・準耐火=2・耐火=3。0=構造なし）を変更した。Unityはキャッシュ済みの底面から、構造つきパーツフォルダ（`a{variant}s{structure}` 等）でプレビューを再生成する。`STATE_EDIT_BUILDING` の遷移パラメータにも `structure` が含まれ、`building_construction:copy_source` はコピー元の `structure` を返す |
| `edit_building:update_landmark` | `ui -> graphic` | 建物建設モードにおいて、観光名所 `landmark`（`landmark_master` のID。0=観光名所ではない）を変更した。Unityは名所専用モデル（`Resources/Models/Buildings/landmark-{model_prefix}`）でプレビューを再生成する。`STATE_EDIT_BUILDING` の遷移パラメータにも `landmark` が含まれる |
| `edit_building:update_visual_variant` | `ui -> graphic` | 建物建設モードにおいて、外観バリアント(visual_variant1/2/3)を変更した |
| `edit_building:update_effect_range` | `ui -> graphic` | 建物建設モードにおいて、インフラ供給施設(変電所・下水処理場・電波塔など)の効果範囲を3D上に可視化する。`region`(新設プレビューの領域。円 `{"type":"circle","center":[x,z],"radius":r}` または不定形 `{"type":"polygon","polygons":[{"exterior":[[x,z]...],"holes":[...]}...]}`)と `existing`(同じサービス財を供給する既存施設のカバー済み領域の配列。各要素は region と同形式)を含む。両方が null/空 のときは表示クリア。値はバックエンドの `POST /api/v1/buildings/preview-effect-range`(プレビュー)と `GET /api/v1/buildings/effect-ranges`(既存)から取得する。効果範囲はプレビュー=緑・既存=青に色分けした面(フィル)と境界線で可視化し、効果範囲内の土地区画のハイライトは行わない |
| `edit_zone:selected`            | `graphic -> ui`  | 地区編集モードで選択中地区（拡張先・統合先）が変化した（右クリック選択 / 新規作成後の自動選択 / 選択解除）。`zone_id`=0 は選択なし |
| `edit_zone:request_merge`       | `graphic -> ui`  | 地区編集モードの「地区の統合」で統合元と統合先が決まった（`target_zone_id`, `target_zone_name`, `source_zone_id`, `source_zone_name`）。地区がひとつ消える不可逆操作のためUnityは送信せず、常駐する `icon-panel` ウィンドウがこれを受けて確定専用の小ウィンドウ（`zone_merge`）を開き、そこの確定ボタンでUIが `POST /api/v1/zones/{id}/merge` を送る |
| `edit_zone:finish_merge`        | `ui -> graphic`  | 地区統合の確定／取消の結果（`merged`=1/0）。1 のときUIは送信済みで、Unityは地区・道路を再取得して統合結果を反映する。0 は取消で確定待ちを解除するだけ（確定ウィンドウが×で閉じられたときもRust側が 0 を送る）。いずれも地区編集モードは継続する |
| `input:query_modes`             | `ui -> graphic`  | デバッグ用: 各オブジェクト種別の入力/表示モードを問い合わせる       |
| `input:current_modes`           | `graphic -> ui`  | `input:query_modes`への応答、現在の入力/表示モード情報を返す      |
| `edit_parcel:select_point`      | `graphic -> ui`  | 土地区画作成モードにおいて、3Dビュー上の地点がクリックされた       |
| `edit_parcel:select_path`       | `graphic -> ui`  | 土地区画作成モードにおいて、パスが選択された（`path_id`, `position`）。`position` はクリック地点をパス中心線へ射影した `[x, y, z]` で、作成範囲が「クリック地点」のときに「どこに区画を作るか」のアンカーになる（「パス全体」のときは使われない）。射影はUnity側でも行う＝3Dビューに出すマーカーの位置とバックエンドが作る位置を一致させるため |
| `edit_parcel:select_parcel`     | `graphic -> ui`  | 土地区画作成モードにおいて、既存土地区画が選択された            |
| `edit_parcel:set_mode`          | `graphic -> ui`  | 土地区画作成モードにおいて、3DビューのCtrlラジアルメニューで作成方法（`mode`＝`empty_area` / `along_path` / `tile` / `random` / `block` / `manual` / `split_rectangle` / `split_random`）が切り替わった。**作成方法の真実源はUnity側**で、UIの作成パネルはこれを受けて入力欄の構成と選択済み表示を合わせる。**現在と同じ `mode` で届いた場合は「選択のリセット」**（3DビューでEscを押した／同じ方法を選び直した）を意味し、パネルは選択状態だけを捨てて幅・奥行き等の入力値は保持する。パネルのタイルから切り替えたときは `world:change_state` で依頼するためこのイベントは返らない |
| `edit_parcel:reset_selection`   | `ui -> graphic`  | 土地区画作成モードにおいて、作成方法は維持したまま3Dビュー側の選択・プレビュー・描きかけのポリゴンだけを捨てる。UIが作成を確定した直後と「クリア」ボタンで送る。**確定してもモードから抜けないため**、ユーザーはパネルへ戻らずそのまま3Dビューをクリックして連続作成できる。確定・クリアのボタンはパネル側にありフォーカスがパネルへ移っているので、Unityは受信時に自分のウィンドウを前面へ戻す |
| `edit_parcel:set_rect_params`   | `ui -> graphic`  | 土地区画作成モード（地点指定系）において、矩形の `width` / `height` をUnityへ同期する。用途は「地点を選んだあとの角度ドラッグ中にUnityがローカル計算で描く矩形プレビュー」をパネルの入力値と一致させることだけで、確定形状は `edit_parcel:update_preview` が正 |
| `edit_parcel:start_shape_edit`  | `ui -> graphic`  | 土地区画形状編集モードを開始する（parcel_id, polygon）       |
| `edit_parcel:cancel_shape_edit` | `ui -> graphic`  | 土地区画形状編集モードをキャンセルして通常モードに戻す          |
| `edit_parcel:finish_shape_edit` | `ui -> graphic`  | 土地区画形状編集モードを終了する。UI側が `PATCH /api/v1/parcels/{id}` でポリゴンの保存に成功した後に送信され、Unity側の編集状態をリセットしてデータをリロードする（`edit_path:finish` と同じ役割） |
| `edit_parcel:update_shape`      | `graphic -> ui`  | 土地区画形状編集モードでポリゴンが変更された                   |
| `edit_resource:update_shape`    | `graphic -> ui`  | 資源エリア編集モード(新規作成)において、編集中のポリゴンが変更された(polygon のみ、新規エリアのためIDは含まない) |
| `edit_resource:finish`          | `ui -> graphic`  | 資源エリア編集モードを終了する。UI側が `POST /api/v1/resources` の送信に成功した後に送信され、Unity側の編集状態をリセットしてデータをリロードする（`edit_path:finish` と同じ役割） |
| `edit_grading:update_shape`     | `graphic -> ui`  | 造成（陸地の整地・海の埋立）の範囲作図モード（`STATE_EDIT_GRADING`）において、作図中の範囲ポリゴンが変わった（`{polygon: [[x, z], ...], finalized}`）。`finalized` は右クリックで形を閉じて頂点編集（Editing）に移った後だけ `true` で、閉じる前の折れ線は `false`。Unity は頂点のドラッグ中に毎フレーム送るので、造成パネル（`grading`）は `finalized` かつ3点以上のときだけ、入力が落ち着くのを待ってから見積もり（`POST /api/v1/grading/preview`）を取り、古い要求は中断する。整地か埋立か・目標の高さ・見積もりはバックエンドが範囲から決め、パネルは範囲を Unity へ送り返さない。Rust `ipc.rs` の中継が必要なイベントで、全ウィンドウへ送られる |
| `edit_grading:finish`           | `ui -> graphic`  | 造成範囲の作図モードを終える（`{}`）。UI側が `POST /api/v1/grading`（発注）に成功した後に送る。Unity は「作図の片付け → 通常ステートへの復帰 → データの再取得」を1つのハンドラでこの順に行う（作図中でなければ、ほかの編集モードを巻き込まないよう再取得だけを行う）。発注の時点では地形も海岸線も変わらないので、海岸線は取り直さない。`ui:action` の汎用転送なので Rust の変更は要らない |
| `edit_transit:update_points`    | `graphic -> ui`  | 公共交通の路線設置モードにおいて、配置中の巡回点（停留所・経由点を巡回順に並べたもの）が変更された（`{ points: [{ kind: "stop" | "waypoint", position: [x,y,z] }, ...] }`。公共交通パスは相対Y=0のため y は0）。新設・経路編集パネルが停留所・経由点の数の表示と確定で送る巡回点を更新する |
| `edit_transit:finish`           | `ui -> graphic`  | 公共交通の路線設置モードを終了する。UI側が `POST /api/v1/transit/lines`（新設）または `PUT /api/v1/transit/lines/{id}/route`（経路編集）の送信に成功した後に送信され、Unity側の巡回点の配置状態をリセットしてデータをリロードする（`edit_resource:finish` と同じ役割） |
| `edit_prop:change_settings`     | `ui -> graphic`  | プロップ配置モードにおいて、回転角/スケール(個別)・密度/seed(範囲)を変更する（送られてきたキーのみを含む `{ rotation?, scale?, density?, seed? }`）。Unity は配置中のゴースト/散布プレビューに即反映する |
| `edit_prop:update_instances`    | `graphic -> ui`  | プロップ配置(個別モード)において、配置済みインスタンス列が変更された（`{ instances: [{x, z, rotation, scale}, ...] }`）。配置パネルが配置数の表示とPOST用のインスタンス列を更新する |
| `edit_prop:update_shape`        | `graphic -> ui`  | プロップ配置(範囲モード)において、散布範囲ポリゴンが変更された（`{ polygon: [[x,z], ...] }`。`edit_resource:update_shape` と同形） |
| `edit_prop:finish`              | `ui -> graphic`  | プロップ配置モードを終了する。UI側が `POST /api/v1/props/instances`(個別) または `POST /api/v1/props/scatters`(範囲) の送信に成功した後に送信され、Unity側の配置状態をリセットしてデータをリロードする（`edit_transit:finish` と同じ役割） |
| `edit_prop:placement_settings`  | `graphic -> ui`  | プロップ配置(個別モード)において、3D側の操作（中ボタンドラッグ回転・PageUp/PageDown の高さ変更）で次に置くゴーストの設定が変わった（`{ rotation, height }`。回転は0〜360度）。配置パネルはステッパー表示を追従させるだけで `edit_prop:change_settings` へエコーしない（`edit_building:update_direct_angle` と同じ扱い） |
| `remove_object:request_confirm` | `graphic -> ui`  | ブルドーザモードで、即削除せず確認を要する対象がクリックされた（`{ object_type, id, prop_type }`。`object_type` は現状 `"prop_scatter"`＝範囲配置プロップを散布範囲ごと撤去する不可逆操作のみ）。Rust は常駐する `cockpit` ウィンドウだけへ中継し、コックピットが確認ダイアログを出す。Unity は回答が届くまでその対象を削除せず、別の範囲配置クリックも受け付けない |
| `remove_object:confirm_result`  | `ui -> graphic`  | 上記の確認ダイアログの回答（`{ object_type, id, accepted }`。accepted は 1/0）。承諾なら Unity が `DELETE /api/v1/props/scatters/{id}` を発行して差分リロードする。取消でも必ず返し、Unity 側の確認待ちを解除する |
| `edit_connector:show`           | `ui -> graphic`  | 経済主体詳細パネル(AgentDetailPanel)の表示中、施設の接続端子ノード（インフラ接続点 `infra_node_id` / 走行網接続点 `depot_node_id`）を Unity 上に色付きマーカーで読み取り専用ハイライトする。`{ agent_id, infra_node_id, depot_node_id }` を含む。`infra_node_id == depot_node_id`（共用接続ノード）ならマーカーは1つ。0 のノードIDは表示しない |
| `edit_connector:hide`           | `ui -> graphic`  | 接続端子ノードのハイライト表示をクリアする。パネルを閉じた／別エージェントへ遷移したときに送信する（`parcel:hide_front_path` と同じ役割） |
| `game:time_update`              | `graphic -> ui`  | ゲーム内時間（エポック・日付）の更新通知                  |
| `system:shutdown`               | `ui -> graphic`  | グラフィックプログラムを終了する                      |
| `system:ready`                  | `ui -> graphic`  | UIプログラムの準備完了通知                        |
| `system:ping`                   | `ui <-> graphic` | 疎通確認用                                 |
| `system:pong`                   | `ui -> graphic`  | pingへの応答                              |
| `system:graphic_ready`          | `graphic -> ui`  | グラフィックの起動とIPC初期化の完了通知。毎起動時に送られ、2回目以降(クラッシュ後の自動再起動)ではUIがポップアップを一掃する |
| `system:graphic_stopped`        | `graphic -> ui`  | グラフィックの正常終了通知。`OnApplicationQuit`(EditorのPlay停止でも発火)でMMFを閉じる直前に送る。UIはグラフィックを未接続扱いに戻し、開発時のUnityなし起動でTipsが描画完了を待っていればその場で開始ボタンへ切り替える。クラッシュ時は送られない |
| `system:focus_gained`           | `graphic -> ui`  | グラフィックのウィンドウがフォアグラウンドになった通知(`OnApplicationFocus(true)`)。フルスクリーン扱いのグラフィックが活性化するとシェルが最前面指定のUIを裏へ回すことがあるため、UIはこれを受けて表示中の全ウィンドウの最前面を打ち直す。UIが裏に回ったときの復帰経路 |
| `session:reset`                 | `ui -> graphic`  | 現在の3Dオブジェクトや地形のデータを全てクリアする。以後 `session:reload` が届くまでカメラ由来のロードとカメラ状態の `PATCH /api/v1/city` を止める（バックエンドが新しい都市を準備している間に中途半端なデータや前の視点を取り込まないため） |
| `game:start`                    | `ui -> graphic`  | バックエンドのセッションが作成され、ゲームが開始されたことを通知。ペイロードは空。グラフィックはこれを受けて `GET /api/v1/city` の `camera`（セーブ済みの視点、新規都市ではマップ定義の初期視点）を読み、カメラを復元する（`null` なら原点の既定視点）。既にゲーム中でも送られ、その場合もカメラだけ復元する |
| `game:end`                      | `ui -> graphic`  | セッションが破棄され、ゲームが終了したことを通知               |
| `session:reload`                | `ui -> graphic`  | バックエンドからデータを取得して、3Dを再描画する             |
| `building:exploded`             | `ui -> graphic`  | 爆破HTTP APIの成功通知（`{agent_id}`）。既存の個別取得経路で対象建物を瓦礫に更新し、炎・煙・爆発音を一度だけ再生する |
| `world:set_road_focus`          | `ui -> graphic`  | 道路詳細パネル（`road_detail`）が開いている道路の区間を、3Dビューに道路色の帯で強調する（`{road_id, color, path_ids}`）。帯は区間の中心線に沿って最前面に、画面上で一定の幅で描く。送るたびに全置換する。干渉の警告用の `world:highlight_objects` とは別の仕組み。詳細は[道路の詳細と道路名](道路の詳細と道路名.md#強調帯) |
| `world:clear_road_focus`        | `ui -> graphic`  | 道路の強調を消す（`{road_id?}`）。`road_id` があれば強調中の道路と同じときだけ消し（道路詳細は道路ごとに同時に開けるため、別のパネルの強調を消さない）、無ければ無条件に消す。道路詳細のウィンドウが閉じられたときは Rust（`window.rs`）が閉じた道路の `road_id` を載せて送る |
| `world:refresh_roads`           | `ui -> graphic`  | 道路（名前付き道路）の作成・改名・所属変更・削除のあとに送り、Unity に道路一覧（`GET /api/v1/roads/`）を取り直させる（`{}`）。一覧の `version` が同じなら道路名ラベルは作り直さない |
| `edit_road:set_members`         | `ui -> graphic`  | 道路の区間編集モード（`STATE_EDIT_ROAD_MEMBERS`）で、所属区間と追加候補の全集合を送る（`{road_id, path_ids, preview_path_ids}`）。差分ではなく毎回全集合。Unity は所属区間を道路色、候補を半透明の道路色の帯で示す。モード中でない・別の道路宛ての通知は無視する |
| `edit_road:cancel`              | `ui -> graphic`  | 道路の区間編集モードを終える（`{road_id}`）。Unity はその道路の区間編集モード中のときだけ終了して通常モードへ戻し、モード外・別の道路の編集中・`road_id` の無い通知は無視する（`STATE_NORMAL` と違い、道路の敷設など他のモードを巻き込まない）。道路詳細パネルの「完了」と、そのウィンドウが閉じられたときの Rust（`window.rs`）が送る |
| `edit_road:toggle_path`         | `graphic -> ui`  | 道路の区間編集モードで、街路・高速道路の区間がクリックされた（`{path_id, path_name, path_type, shift}`。`shift` は Shift キーの押下で、その区間から直進方向へたどった区間をまとめて追加する操作を表す）。所属の付け外しは道路詳細パネルが道路APIで行い、結果を `edit_road:set_members` で送り返す（`edit_path:select_merge_target` と同じ責務分担）。Rust は全ウィンドウへ中継する |
| `edit_road:ended`               | `graphic -> ui`  | 道路の区間編集モードが終わった（`{road_id, session_id?}`）。完了・Esc・他のステートへの遷移・閉じたパネルの `edit_road:cancel`・セッションのリセットなど理由を問わず、モードを抜けるたびに1回だけ送る。道路詳細パネルは自分の道路なら何も送り返さずに編集中の表示を落とす。`session_id` は開始時の `STATE_EDIT_ROAD_MEMBERS` の `session_id` を Unity が型を変えずにそのまま返したもので、開始時に無かった（または整数でも文字列でもなかった）ときはキーごと省く。パネルは正の整数の `session_id` が含まれていれば開始時の値と一致するときだけ受け入れ（遅れて届いた前回の終了で新しい編集を落とさないため）、含まれていなければ道路IDだけで判断する。Rust は全ウィンドウへ中継する |
| `world:roads_changed`           | `graphic -> ui`  | UI を通さずに道路（名前付き道路）の区間が消えたことを知らせる（`{}`）。送り手はブルドーザでのパスの削除で、削除したパスが道路の区間だったときだけ、削除の成功後、差分リロードのまとめ待ちが明けた時点で1回送る（続けて削除しても1通にまとまる。周期送信はしない）。区間が0本になった道路はバックエンドが削除するので、道路詳細パネルはこれを受けて取り直し、削除済みを表示できる。Rust は Tauri のグローバルイベント `roads:changed`（UI の道路の変更通知と同じもの。ペイロードはそのまま）として全ウィンドウへ流す。Unity は差分リロードで自分の道路一覧を取り直すので、`world:refresh_roads` は送り返さない |

## `world:select_object` の選択対象

ペイロードは `{window_type, entity: {type, id}}`。UIは `window_type` のウィンドウを `entity.id` を対象に開く。`id` は文字列で送る。

| 3D上の選択元 | `window_type` | `entity.type` | `entity.id` |
|---|---|---|---|
| 建物 | `agent_detail` | `agent` | 建物（経済主体）ID |
| 公共交通の路線 | `transit_detail` | `agent` | 路線の経済主体ID |
| 道路・線路などのパス | `path_detail` | `path` | パスID |
| 土地区画 | `parcel_detail` | `parcel` | 区画ID |
| 地区名ラベル | `zone_detail` | `zone` | 地区ID |
| 天然資源ラベル | `resource_detail` | `resource` | 資源エリアID |
| インターチェンジ名ラベル | `intersection_detail` | `interchange` | インターチェンジID（`GET /api/v1/interchanges/{id}` の `interchange_id`） |
| 道路名ラベル | `road_detail` | `road` | 道路（名前付き道路）ID（`GET /api/v1/roads/{id}` の `road_id`） |

インターチェンジ（IC/JCT）はバックエンドがランプから自動検出するもので（[道路網のインターチェンジとジャンクション](../economics/3_道路網.md#インターチェンジとジャンクション)）、Unityは道路の閲覧状態（道路・地形の入力モードがともに通常）のときだけ中心にラベルを出す。編集中はラベルを隠すので、このイベントが道路編集のクリックと競合することはない。`intersection_detail` は名前と種別、どの道路からどの道路へ行けるかの接続図を表示するパネルで、カメラ移動などのアクションは持たない。

## 確定送信の責務分担

### 建物の爆破

`building:exploded`（UI → graphic、params: `{agent_id: 整数}`）は、`AgentOperationPanel` が確認ダイアログを経て `POST /api/v1/agents/{id}/explode` に成功した後だけ送る。爆破の確定はHTTP側で完了しており、IPCは描画更新の通知である。

転送は既存の `send_window_action` → `send_ui_action` → `ui:action` Envelope（`payload.action` / `payload.params`）を使用する。Rustの専用コマンドや別のEnvelopeは設けない。Unityの `IpcClient.OnBuildingExploded` → `IpcEventHandler` → `CellDataLoader.FetchBuilding(id, playExplosion: true)` → `BuildingManager.FetchBuilding` と、既存の個別取得・共通描画スケジューラを通る。

Unityは `GET /buildings/{id}` の最新状態が `STATUS_COLLAPSED` であることを確認し、既存の建物メッシュ生成経路で瓦礫へ切り替える。同じ処理で `VisualEffectManager.PlayExplosion` を一度だけ呼ぶ。再取得中の重複通知と同じversionの再通知は演出を重ねず、セッション切替中に完了した取得結果も破棄する。爆破前の一括取得が後着した場合、その古いversionで表示を戻さない。

この操作では `session:reload` を併送しない。全体リロードが行う海岸線・路線などの取得は不要で、対象1棟の更新と演出だけを同じハンドラで開始する。通常のエポック更新・保存データのロード・解体による状態変化では爆発音を鳴らさない。エフェクトの寿命と音源は [火災エフェクトの描画](../geometries/S_火災エフェクトの描画.md#爆破操作の単発エフェクト) を参照。

### 建設・編集

建設・編集の「確定」をバックエンドへ送信する責務は、**確定操作を所有する側**が持つ。

- **パネル確定型**（建物建設・パス敷設・土地区画の作成/形状編集・資源エリア・造成・公共交通・プロップ配置）: UI(Tauri)のパネルに確定ボタンがあるため、**UIが直接バックエンドへPOST/PATCHする**。成功後に `edit_*:finish` 系イベント（または `session:reload`）を送り、Unity側の編集状態をリセットしてデータをリロードさせる。Unityは形状の組み立てと `edit_*:update_*` 系イベントによるUIへの逐次通知のみを担う。
- **3Dジェスチャ完結型**（地区の塗り分け＝クリック/ドラッグの即時割当、接続端子ノードのドラッグ移動）: パネルの確定ボタンを持たず3D上の操作で完結するため、**Unityが直接バックエンドへ送信する**。

確定後の後始末（編集状態のリセット・消えたオブジェクトの破棄・通常モードへの復帰・データの再取得）は `edit_*:finish` 系の1イベントにまとめ、Unity がその中で順序を持って実行する。`session:reload` と `world:change_state` を別々に送ってはいけない。差分リロードは複数フレームにまたがる処理で、後続の状態遷移が割り込むと再生成・削除が中途半端に終わる。確定しないキャンセルは `STATE_NORMAL` の送信で足りる。

`edit_*:finish` 系を送ったパネル自身は、続けて `STATE_NORMAL` を送らない。ただし確定後にウィンドウを閉じるパネル（造成など）では、閉じる処理が×ボタンと同じ Rust の経路（`window.rs` の `close_window_by_label`）を通るので、そのウィンドウ種別が `EDIT_MODE_WINDOW_TYPES` に載っていれば、ほかに編集モードのウィンドウが表示されていない限り Rust が `STATE_NORMAL` を送る。Unity は finish の中で既に通常ステートへ戻しており、`STATE_NORMAL` の処理は入力と表示の適用し直しだけで差分リロードを取り消さない（取り消すのは新しい再取得・カメラのセル移動・セッションの切り替え）。この `STATE_NORMAL` は冗長だが、再取得を妨げない。

# グラフィックプログラムのステート

Unityのグラフィックプログラムは、いくつかのステートを持つ。各モードの実体はUnityのC#で定義された「表示モード（`VisualMode`）」「入力モード（`InputMode`）」のセットである。
ステートごとに以下のようになる。空欄は何もしないという意味。

|                         | `STATE_NORMAL` | `STATE_EDIT_PATH` | `STATE_EDIT_BUILDING` | `STATE_EDIT_ZONE` | `STATE_EDIT_PARCEL_SHAPE` | `STATE_EDIT_RESOURCE` | `STATE_EDIT_ROAD_MEMBERS` | `STATE_EDIT_GRADING` |
| ----------------------- | -------------- | ----------------- | --------------------- | ----------------- | ------------------------- | ---------------------- | ---------------------- | ---------------------- |
| `Parcel - VisualMode`   | 表示しない          | 表示する              | 表示する                  | 表示する              | 境界線表示（編集対象のみ非表示）          | 境界線表示                  | 表示する | 境界線表示 |
| `Parcel - Click`        | 詳細情報表示         | -                 | 建設場所選択                | -                 | -                         | -                      | - | - |
| `Parcel - Mouseover`    | ハイライト          | -                 | ハイライト                 | -                 | -                         | -                      | - | - |
| `Building - VisualMode` | 通常表示           | 計画中の建物も表示         | 計画中の建物も表示             | 計画中の建物も表示         | 計画中の建物も表示                 | 計画中の建物も表示              | 計画中の建物も表示 | 計画中の建物も表示 |
| `Building - Click`      | 詳細情報表示         | -                 | -                     | -                 | -                         | -                      | - | - |
| `Building - Mouseover`  | ハイライト          | -                 | -                     | -                 | -                         | -                      | - | - |
| `Path - VisualMode`     | 通常表示           | 計画中のパスも表示         | 計画中のパスも表示             | 地区色分け表示           | 計画中のパスも表示                 | 計画中のパスも表示              | 計画中のパスも表示（所属区間を道路色の帯で強調） | 計画中のパスも表示 |
| `Path - Click`          | 詳細情報表示         | -                 | -                     | 選択中地区へ即時割当（Shift+クリックで新規作成） | -                         | -                      | 街路・高速道路の区間の付け外しをUIへ通知（`edit_road:toggle_path`） | - |
| `Path - Mouseover`      | ハイライト          | -                 | -                     | 操作案内ツールチップ表示      | -                         | -                      | 街路・高速道路の区間をハイライト＋操作案内ツールチップ（他の種別は拒否のツールチップ） | - |
| `Terrain - VisualMode`  | 通常表示           | 通常表示              | 通常表示                  | 通常表示              | 通常表示                      | 通常表示                   | 通常表示 | 通常表示 |
| `Terrain - Click`       | -              | ノード追加             | 直接配置: 地点確定          | -                 | 頂点ドラッグ/辺上追加               | 頂点追加（Placing）/ 頂点ドラッグ・辺上追加（Editing） | - | 頂点追加（Placing）/ 頂点ドラッグ・辺上追加（Editing）。吸着なし |
| `Terrain - Mouseover`   | -              | マーカー移動            | 直接配置: 矩形ゴースト追従       | -                 | 辺ハイライト                    | 辺ハイライト（Editingのみ）       | - | 辺ハイライト（Editingのみ） |
| `Zone - VisualMode`     | 表示しない          | 表示する              | 表示する                  | 表示する              | 非表示                       | 非表示                    | 表示する | 表示する |
| `Zone - Click`          | 詳細情報表示         | -                 | -                     | -                 | -                         | -                      | - | - |
| `Zone - Mouseover`      | ハイライト          | -                 | -                     | -                 |                           |                        | - | - |

`STATE_EDIT_BUILDING` の**直接配置**（土地区画外への建設）では、底面が「回転付き矩形」でありUnity単独で生成できるため、地形ホバー中にカーソル追従のリアルタイム矩形ゴーストを表示する（`BuildingPlanManager` が `edit_building:update_direct_params` で受け取った `width`/`depth`/`angle`/`floors` からローカル生成する。バックエンド往復不要）。中ボタンドラッグで矩形を1度単位で即時回転でき（地点確定の前後どちらでも有効）、回転はリリース時に `edit_building:update_direct_angle` でUIへ1回だけ通知して回転ステッパーに反映する。左クリックで地点を確定（`edit_building:select_point` に確定時の `angle` を含めてUIへ通知）すると、以降はUIの `POST /api/v1/buildings/preview-direct` → `edit_building:update_shape` が権威プレビューとなる（回転角の正規化=mod 360 はUIの確定送信 `POST /api/v1/buildings/direct` 時のみ）。土地区画上へのホバーではゴーストを表示せず区画ハイライトへ切り替わる。土地区画への設置は回転角指定・リアルタイムゴーストの対象外（形状はバックエンド依存のため）。

`STATE_EDIT_RESOURCE` はテンプレート作成モード専用で、天然資源エリアの新規作成にのみ使用する（既存エリアの編集はまだ実装されていない）。Terrain上の操作は2フェーズに分かれる: Placing(左クリックで頂点を末尾に追加していく)→Editing(頂点数3以上で右クリックすると形状が確定し、`STATE_EDIT_PARCEL_SHAPE` と同じ操作感の頂点ドラッグ・辺クリック挿入・右クリック削除に切り替わる)。バックエンドへの確定送信(`POST /api/v1/resources`)はUI側が直接行い、成功後に `edit_resource:finish` を送信してUnity側の編集状態をリセットする。

`STATE_EDIT_GRADING` は造成（陸地の整地・海の埋立。[造成](../economics/34_造成.md)）の範囲を描くモードで、造成ウィンドウ（`grading`）を開くと遷移する（params は無い。送信元の `window_type` は `grading`）。Unity は `GradingAreaEditManager` が担い、Terrain上の操作は `STATE_EDIT_RESOURCE` と同じ2フェーズ（Placing: 左クリックで頂点を末尾に追加 → 頂点数3以上で右クリックすると形が閉じて Editing: 頂点ドラッグ・辺クリックでの挿入・右クリックでの削除）になる。範囲が変わるたびに `edit_grading:update_shape` を送るだけで、整地か埋立か・目標の高さ・見積もりはバックエンドが範囲から決める。資源エリアの作図と違う点は次の2つ。

- **吸着しない。** 頂点の配置・移動・挿入とも道路・ノード・グリッドへ吸着しない。道路の中心線へ吸い付くと範囲がパスに掛かり、発注で必ず拒否されるため（頂点は道路の縁の外に置く）。
- **Esc で抜けない。** Esc で Unity の入力だけが通常に戻ると（ステート遷移もUIへの通知も伴わない）、ウィンドウが開いたまま編集用の表示プリセットも残るため。抜けるのはウィンドウを閉じたとき（Rust が `STATE_NORMAL` を送る）と、確定後の `edit_grading:finish` だけ。

表示はステート別の表示プリセット（区画の境界線・地区・等高線・計画中の道路と建物を表示、プロップを非表示。[Unity入力制御 §9.1](Unity入力制御.md#91-ステート別の表示プリセット)）。埋立地の岸壁の天端と面は地形としてクリックでき、その上にも頂点を置ける。確定の送信（`POST /api/v1/grading`）はUI側が直接行い、成功後に造成工事の主体の詳細ウィンドウ（`agent_detail`）を開いてから `edit_grading:finish` を送り、造成ウィンドウを閉じる（閉じたときの `STATE_NORMAL` の扱いは[建設・編集](#建設編集)）。発注が拒否されたら見積もりを取り直す。パネルのキャンセルはウィンドウを閉じず、`STATE_EDIT_GRADING` を送り直して作図を初めからやり直させる。

`STATE_EDIT_TRANSIT` は公共交通の路線設置モードで、公共交通の新設パネル（`transit_creation`）で路線種別を選ぶと遷移する。路線詳細の「経路を編集」も同じパネルを経路編集モード（ウィンドウを開くときの `params.extra.editLineId`）で開いて遷移する。`world:change_state` の params は次のとおり。

- `path_variant` … 路線種別の区間パスのバリアント（＝`transit_master` の `transit_path_id`）
- `stop_path_variants` … 停留所を設置できる下地パスのバリアント一覧
- `route_path_variants` … 車両が走行できる下地パスのバリアント一覧。経由点は `stop_path_variants` とこれの和に属するパスの上に置ける
- `points` … 始めの巡回点（`edit_transit:update_points` と同じ形）。新設では空、経路編集では路線の現在の巡回点（路線詳細 API の `route_points`）
- `line_agent_id` … 経路を編集する路線（新設は null）。Unity はこの路線の路線色帯を編集中だけ隠し、プレビューと重ならないようにする

路線は巡回点を順にたどり、最後の点から先頭の点へ戻る閉じたループである（[公共交通機関](../economics/6_公共交通機関.md) 6.2）。Unity の操作は2フェーズに分かれる。配置フェーズでは左クリックで停留所を末尾に追加し、既存の停留所をクリックすると同じ停留所を再び訪れる停留所として追加し、先頭の停留所をクリックする（停留所2つ以上）か右クリックで編集フェーズへ移る。編集フェーズでは停留所・経由点のドラッグで移動、区間（最後の点から先頭の点への区間を含む）のクリックで停留所の挿入、区間のドラッグで経由点の挿入、停留所・経由点の右クリックで削除ができる。**停留所は `stop_path_variants`、経由点は走行網に属する稼働中パスの上にのみ吸着でき、外れた場所には置けない**（路線の作成・経路の更新時にバックエンドが検証する設置制約（[公共交通機関](../economics/6_公共交通機関.md) 6.4）と一致）。停留所・経由点を置くと効果音を鳴らす（音量はUIの効果音設定 `ambience:volume` に従う）。

巡回点が変わるたびに Unity は `edit_transit:update_points` でUIへ送り、あわせて `POST /api/v1/transit/lines/route-preview`（都市を変えない経路の試算。[公共交通機関](../economics/6_公共交通機関.md) 6.8）を少し待ってから呼んで、返った区間ごとの走行データを路線色帯と同じ見た目の帯として実際の道路・線路に沿って描く。下地でつながらない区間は、その区間の巡回点を結ぶ赤い破線で示す。確定時の `POST /api/v1/transit/lines`（新設。`path_variant` と `points` を含む）と `PUT /api/v1/transit/lines/{id}/route`（経路編集。`points` を含む）はUI側が直接行う。成功後に `edit_transit:finish` を送信してUnity側の編集状態をリセットし、新しい公共交通パスをオーバーレイに反映させる。LRT路線の停留所のホームは配置中には描かず、作成後に路線一覧（`GET /api/v1/transit/lines`）の `stop_points` から下地パスの再生成で現れる（[道路メッシュの生成](../geometries/G_道路メッシュの生成.md#lrt停留所のホーム)）。

設置済みの公共交通パス（`PATH_TRANSPORT`）は、`view:change_view` の `transit` レイヤーで路線種別ごとに表示ON/OFFできる（`sub_mode` に表示する `transit_path_id` のCSV文字列を渡す）。Unity は種別色の帯（オーバーレイ）として下地道路の上に描画する。

`STATE_EDIT_PROP` は地形プロップ（森・岩・茂みなどの装飾）の配置モードで、プロップ配置パネル（`prop_placement`）で種別を選ぶと遷移する。`world:change_state` の params に `prop_type`・`placement_mode`（`"instance"` | `"scatter"`）・初期設定 `rotation`/`scale`（個別）・`density`/`seed`（範囲）を含める。配置には2モードある。**個別モード**は左クリックで1個ずつ配置（カーソル追従は半透明ゴースト、確定すると不透明の実モデル、右クリックで直近を取り消し）し、配置列を `edit_prop:update_instances` でUIへ送出する。**範囲モード**は左クリックで散布範囲のポリゴン頂点を置き（3頂点以上でseedベースの散布ゴーストを簡易プレビュー、右クリックで直近頂点を取り消し）、ポリゴンを `edit_prop:update_shape` でUIへ送出する。回転/スケール/密度/seed の変更は `edit_prop:change_settings` でプレビューに即反映する。個別モードでは3D側でも中ボタンドラッグで回転、PageUp/PageDown で高さを変えられ、結果は `edit_prop:placement_settings` でパネルへ書き戻す。確定送信（個別=`POST /api/v1/props/instances`、範囲=`POST /api/v1/props/scatters`）はUI側が直接行い、成功後に `edit_prop:finish` を送信してUnity側の配置状態をリセットする。既存プロップの削除は配置パネルではなく `STATE_REMOVE_OBJECTS`（ブルドーザ）で行い、範囲配置は `remove_object:request_confirm` / `remove_object:confirm_result` の確認を挟む。プロップはゼロコストの装飾のため `template_mode` ゲートはない。詳細な仕様は `genesis-docs/docs/geometries/M_地形プロップ.md` を参照。

`STATE_EDIT_STATION` は駅パッケージ配置モードで、軌道建設パネル（`track_build`）の**簡易モードの「駅」タブ**が使う。プラットフォーム（複線の相対式＝対面ホーム）と駅舎をひとまとめにして1クリックで置くためのモードで、`world:change_state` の params に `platform_variant` / `house_variant` / `platform_length`（m） / `track_spacing`（m） / `height`（相対m） / `angle`（度。北＝ワールド+Zが0の時計回り） / `travel_directions`（各線の `DIR_*`） / `house_ends` を含める。

Unity は `StationPlanManager` が担う。カーソル追従の半透明ゴースト（プラットフォーム2本＋駅舎1本を実パスと同じメッシュ生成器で描く）→ 左クリックで配置地点を固定 → パネルの確定ボタンで発注、という流れで、**建物の自由配置・プロップの個別配置と同じ操作**（中ボタンドラッグで回転、PageUp/PageDown で高さ、Esc または右クリックで地点の固定解除）を共通部品 `Editors/PlacementInput.cs` で提供する。形状は固定なので折れ線の編集・曲線・吸着・グリッドは持たない（**共通吸着も適用しない**——中心線は複線の「間」を通るため、既存の線路やノードへ吸着すると半間隔ぶんずれる）。プラットフォームは中心点の地盤標高＋指定の高さで水平に揃え、駅舎の線は地面に沿わせる（連絡橋のデッキ高さは駅舎プレハブの造形が持つため）。駅舎の線の長さはプラットフォームの外縁より外で終わる長さを自動で取る。建屋は線路に重なると外側へスライドするが、敷設中の線路はまだ存在せずスライド計算に入らないため、最初から外側で終わらせないとプレビューと確定後で建屋の位置がずれる。

確定送信は UI 側が `POST /api/v1/paths`（プラットフォーム2本＋駅舎1本、`align_station_tracks: true`）で1回にまとめて行い、成功後に `edit_path:finish` で Unity 側の状態をリセットしてから `STATE_EDIT_STATION` を再送して連続設置に戻す。干渉・川/海・湖の横断・線形の検証も UI 側の `POST /paths/validate`（全3本）が権威で、Unity 側はローカル判定を持たない。

`STATE_MERGE_PATH` / `STATE_SPLIT_PATH` は既存パスの結合・分割の「対象選択モード」で、パス詳細パネル（`path_detail`）の操作パネルにある結合／分割ボタンで遷移する。`world:change_state` の params に操作対象の `path_id`（結合では結合後に残る側＝survivor、分割では分割元）を含める。どちらも稼働中（`STATUS_ACTIVE`）のパスにのみ表示される。

Unity は `PathTopologyEditManager` が対象選択を担う。**結合**では対象パスを青、結合できる候補パスを黄で着色し（候補＝対象と端点ノードを共有し、そのノードに他のパスが繋がっておらず、同一バリアントの稼働中パス。曲線の折れ・通行方向の矛盾など残りの条件はバックエンドが判定する）、入力プリセット `PATH_MERGE`（PATH=`MODE_PATH_MERGE`、他=`MODE_NONE`）で候補のクリックだけを受け付ける。**分割**では入力プリセット `PATH_SPLIT`（PATH／TERRAIN=`MODE_PATH_SPLIT`、他=`MODE_NONE`）でカーソルのワールド座標を対象パスの中心線へ射影し、射影点にマーカー（パスを横切る棒＋円）を描く。パス脇の地形をクリックしても垂線の足が分割点になる（バックエンドの射影と同じ考え方）。**このモードでは共通吸着（ノード吸着など）を適用しない**——端点ノードへ吸着すると常に「端点に近すぎる」分割になってバックエンドに拒否されるため。

選択結果は `edit_path:select_merge_target` / `edit_path:select_split_point` でUIへ返り、UIは同じエンドポイントの `dry_run=true` で結合後・分割後の長さを取得してパネルに表示する。プレイヤーが確定するとUIが `dry_run` なしで再送し、成功後に `edit_path:finish_topology_edit` を**1発だけ**送ってUnity側の後始末（着色・マーカーのクリア、消えたパスの破棄、通常モード復帰、再描画）をまとめて行わせる。キャンセル時のみ `STATE_NORMAL` を送る。長さを確認して確定するパネルを操作し続ける必要があるため、`STATE_EDIT_CONNECTOR` と同じくこのステートでは Unity ウィンドウを最前面化しない。

`STATE_EDIT_CONNECTOR` は施設の接続端子ノード（インフラ接続点 `infra_node_id` / 走行網接続点 `depot_node_id`。[[26_インフラ網]]の接続端子ノード参照）をドラッグで移動する編集モードで、経済主体詳細パネル（`agent_detail`）の「接続点の編集」トグルボタンで遷移する。`world:change_state` の params に `agent_id` / `infra_node_id` / `depot_node_id` を含める。Unity は `ConnectorEditManager` が対象ノードを強制表示・逆四角錐マーカーで強調し、入力プリセット `CONNECTOR_EDIT`（TERRAIN=`MODE_EDIT_CONNECTOR`、他=`MODE_NONE`）でマーカーを地形ドラッグとして掴んで移動する（マーカーはノード実体の上空に浮くため、ノードのコライダーへのレイキャストは使わない。[Unity入力制御](Unity入力制御.md) §5.3）。ドラッグ中のXZは施設が立地する土地区画の内側へクランプし、ドラッグ確定時は `NodeInputHandler` が `PATCH /api/v1/nodes/{node_id}`（position）を送る。**ノード位置の変更はパスが1本も接続されていない場合のみ許可され**、接続済みノードの移動はバックエンドが拒否し、Unity は元位置に自動で戻す（[[26_インフラ網]] §26.3 の端子位置移動制限）。トグルボタンを持つ `agent_detail` パネルを前面に残すため、このステートでは他の編集ステートと異なり Unity ウィンドウを最前面化しない。編集を終了すると `STATE_NORMAL` を送り、パネルが開いている間は `edit_connector:show` を再送して読み取り専用ハイライトに戻す。

# 起動フロー

## ポートの動的割り当てと連携

genesis-core は起動時にポート5000から順に空きポートを探し、最初に確保できたポートで FastAPI を起動する。ポートが占有されていた場合は5001、5002…と順次試行する。

この動的に決定したポート番号は、以降の UI および Graphic プログラムに対して正しく伝達される必要がある。伝達の経路は以下の通り。

1. **genesis-core → city-genesis/tauri**: 環境変数 `GENESIS_UI_API_BASE_URL` に `http://localhost:{ポート番号}` を設定してから `config.toml` の `[ui] launch_command` を実行する。
2. **city-genesis/tauri → city-genesis/unity**: `graphic-launch.json` の `launch_command` の末尾に `--api-base-url http://localhost:{ポート番号}` を追加してから Unity を起動する。
3. **city-genesis/unity**: `AppRoot.Start()` でコマンドライン引数 `--api-base-url` をパースし、`ApiClient.BASE_URL` を上書きする。以降、全ての HTTP 通信はこの `BASE_URL` を参照する。

## 各プログラムの起動シーケンス

```
genesis-core (main.py)
  │
  ├─ 空きポートを探す（5000→5001→5002…）
  ├─ FastAPI を見つかったポートで起動（デーモンスレッド）
  ├─ スプラッシュスクリーン表示（tkinter）
  ├─ サーバー_ready をポーリング（500ms間隔）
  │
  └─ サーバー起動完了後:
      ├─ スプラッシュ破棄
      └─ config.toml の [ui] launch_command から city-genesis/tauri を起動
         └─ 環境変数 GENESIS_UI_API_BASE_URL = http://localhost:{ポート}
         （異常終了時は3秒後に自動再起動。正常終了(code 0)は監視終了）
```

```
city-genesis/tauri (Tauri/Rust)
  │
  ├─ 環境変数 GENESIS_UI_API_BASE_URL を読み込み、以降の API 通信先とする
  ├─ 設定読み込み: window-registry.json, window-mutex-groups.json, graphic-launch.json
  ├─ ウィンドウ作成:
  │   ├─ "main" ウィンドウ → 即座に非表示（常駐ホスト）
  │   ├─ "topmenu" ウィンドウ → 画面中央に表示（800x600、最前面）
  │   ├─ "taskbar" ウィンドウ → 画面下部に非表示
  │   └─ "loading" ウィンドウ → 必要時に画面中央に生成（400x200）
  ├─ MMF作成（CreateOrOpen）:
  │   ├─ genesis-ipc-ui-to-graphic（ライター: Tauri、リーダー: Unity）
  │   └─ genesis-ipc-graphic-to-ui（リーダー: Tauri、ライター: Unity）
  ├─ system.ready を UI→Graphic MMF に書き込み
  ├─ リーダースレッド起動（Graphic→UI MMF からUnityのメッセージを待機）
  └─ セッションポーリングスレッド起動
  │   ├─ 5秒ごとに GET /api/v1/sessions と /api/v1/city を取得
  │   └─ 結果を `session-state` イベントとして全WebViewにブロードキャスト
  │
  └─ ゲーム開始/ロード時:
      └─ launch_command の末尾に --api-base-url {URL} を付与して Unity を起動
```

```
city-genesis/unity (Unity/C#)
  │
  ├─ AppRoot.Start():
  │   ├─ コマンドライン引数 --api-base-url をパースし、ApiClient.BASE_URL を上書き
  │   └─ IPC初期化:
  │       ├─ MmfReader 起動（genesis-ipc-ui-to-graphic を読み取り、Tauriのメッセージを待機）
  │       └─ MmfWriter 準備（genesis-ipc-graphic-to-ui に書き込み、Tauriにメッセージを送信）
  │
  └─ Tauriの system.ready を受信 → 双方向通信開始
```

### MMF接続の確立シーケンス

#### 基本原則

双方とも `CreateOrOpen` でMMFと同期オブジェクトを開く。接続の概念がなく、どちらが先に起動しても即座に通信可能。

#### 起動順序

1. **Tauri起動** — 2つのMMFをCreateOrOpen（新規作成）。UI→Graphic方向のMMFをリセットし、`system.ready` を書き込む。Graphic→UI方向のリーダースレッドを起動。
2. **Unity起動** — 2つのMMFをCreateOrOpen（既存を開く）。リーダーを起動してTauriのメッセージを待機。ライター（Graphic→UI方向）のMMFをリセット。
3. **Unityが `system.ready` を読み出す** — 双方向通信開始。Unityからの最初のメッセージがTauriのリーダースレッドに届き、`connected = true` となる。

起動順序が逆（Unityが先）でも、UnityはMMFをCreateOrOpenしてリーダーを起動する。Tauriが後から起動すると、既存のMMFを開き、`system.ready` を書き込む。Unityがそれを読み出し、通信が開始される。

#### ライターのバッファリセット

各方向のライターは起動時にバッファをリセットする（WritePos = 0, ReadPos = 0）。これにより前回セッションの残骸を消去し、クリーンな状態で通信を開始する。

- UI→Graphic方向: Tauriのライターがリセット
- Graphic→UI方向: Unityのライターがリセット

#### Tauri側（Rust）の送信メカニズム

Tauri→Unity方向の送信は、MMFに直接書き込む。

- Tauriコマンドハンドラ（メインスレッド）から `send_to_parent()` を呼ぶ
- Mutexを取得 → リングバッファに書き込み → Mutex解放 → EventをSet
- 書き込みはMutex取得〜解放までマイクロ秒レベルで完了するため、メインスレッドをブロックしない
- 非同期チャンネル（mpsc）は使用しない

### セッションポーリングとウィンドウ表示制御

Tauri 側の常駐スレッドが 5 秒ごとにバックエンドをポーリングし、全 WebView ウィンドウに `session-state` イベントをブロードキャストする。各ウィンドウはこのイベントを受信して自らの表示/非表示を制御する。

#### ポーリング内容

- `GET /api/v1/sessions` — `current_save_id` の有無でセッション状態を判定
- `GET /api/v1/city` — 都市名・日付（Taskbar 表示用）

#### 配信イベント: `session-state`

```json
{
  "connected": true,
  "current_save_id": "10376dfa-ec45-4cbb-bbb0-9e192aa4baae",
  "session": { ... },
  "city": { ... },
  "apiBaseUrl": "http://localhost:5000"
}
```

#### 各ウィンドウの表示条件

| ウィンドウ | 表示条件 |
|-----------|----------------|
| topmenu | `current_save_id == null` |
| taskbar | `current_save_id != null` |
| loading | ゲーム開始/ロード処理中（Rust 側で制御） |

### ゲーム開始フロー（新規）

```
[TopMenu] 「新規ゲーム」ボタン押下
  ↓
invoke("start_new_game")
  ↓
[Rust]
  1. launch_graphic() → Unity 起動
  2. topmenu.hide()
  3. loading.show()
  4. POST /api/v1/sessions
  5. ポーリングスレッドが current_save_id の変化を検知
  6. loading.hide()
  7. taskbar.show()
  8. Unity に session:reload + game:start を送信
```

### ゲーム開始フロー（ロード）

```
[TopMenu] 「ロード」ボタン押下
  ↓
invoke("open_registered_window", { windowType: "save_list" })
  ↓
[SaveListWindow] セーブファイルを選択
  ↓
invoke("start_load_game", { saveId })
  ↓
[Rust]
  1. launch_graphic() → Unity 起動
  2. topmenu.hide()
  3. loading.show()
  4. POST /api/v1/sessions/load
  5. ポーリングスレッドが current_save_id の変化を検知
  6. loading.hide()
  7. taskbar.show()
  8. Unity に session:reload + game:start を送信
```

### ゲーム終了フロー

```
ポーリングスレッドが current_save_id: non-null → null の変化を検知
(初期ロード中は、バックエンドの準備完了を確認した後の null も含む)
  ↓
[Rust]
  1. 全ポップアップを閉じ、HUD・ヒントバーを隠す
  2. Tips・初期設定ウィンドウを閉じ、ロード状態を解除する
  3. topmenu.show()
  4. Unity に game:end を送信
```

## 敷設設定の継続

新規敷設後の `STATE_EDIT_PATH` は `initial_height`（小数m）、`initial_embankment_height`、`initial_curved`、`initial_follow_terrain` を任意パラメータとして受け取る。UIの `continuePlacement` が敷設設定を明示送信し、Unityの `InitializeRoadPlacement` が最初の形状通知より前に適用する。線路間隔・進行方向・ホーム側は既存の各パラメータで引き継ぐ。座標・制御点と見積りだけをクリアする。未指定の新規開始は既定値、既存パス編集は保存値を使う。

## 土地区画作成のモード契約

`STATE_EDIT_PARCEL` の `parcel_creation_mode` は、新規作成では `along_path` / `block` / `random` / `empty_area` を使う。TauriのボタンとUnityのラジアルメニューはこの順で表示する。既存区画の分割モードは維持する。`block` は地点選択と従来のプレビューイベントを使い、UIからバックエンドへ探索範囲・間口・奥行き・ばらつきを送る。新規の任意ポリゴン作図は提供しない。既存区画の形状編集イベント（`edit_parcel:update_shape` と `edit_parcel:finish_shape_edit`）は引き続き使用する。

## 建物直接配置の状態同期

配置方法の初期値は `STATE_EDIT_BUILDING` の遷移パラメータ `placement_mode`（parcel/free/roadside。省略時はparcel）で指定する。既存建物編集・忠実コピーでは無視される。都市創設時の市庁舎はUIが `free` を指定する。以後の切り替えはUnityのCtrlラジアルメニューが担い、UIは `placement_state` の `mode` に追従する。

`edit_building:placement_command` (ui → graphic)のparams.commandには`variant`と変更したフィールドだけを渡す。任意フィールドは`width`/`depth`/`angle`、`reset=true`(位置選択のみ取消し)、`labels`(翻訳済みCtrlメニュー/HUD文言)。寸法を含まない呼び出しは状態問い合わせとなる。パネルは購読完了後に問い合わせ、初期化通知を取りこぼしても復元できる。

`edit_building:placement_state` (graphic → ui)は`{variant, revision, mode, position, width, depth, angle, parcel_id}`を返す。modeはparcel/free/roadside、positionは`[x,0,z]`または未固定を示すnull、parcel_id=-1は区画未選択。キーボード寸法変更・Ctrl切替・位置固定・回転終了・Escも同じ状態通知を使う。ホバー移動では常時送信しない。UIはこの通知をパラメータ変更として再送しない。

直接配置の`edit_building:update_shape`には要求元の`placement_revision`を付ける。Unityは現在の世代・階数・位置固定状態と一致する場合だけ描画する。パネルも要求キーで設置可否の鮮度を確認する。既存区画からの建設・編集は世代を付けない従来経路。

`select_point`/`update_direct_angle`/`update_direct_params`は旧イベント定義として残るが、現行の建設パネルは直接配置にこれらを使用しない。配置方法を位置確定前から明示選択するため、以前の「区画外をクリックしたら直接配置へ入る」自動判定は行わない。

パネルの寸法コマンドには`request_id`を付け、Unityは対応する`placement_state.request_id`にそのまま返す。パネルは最後に送った数値変更への応答が届くまで確定を待つ。これにより連続入力中に先の変更への応答だけで確定可能になることを防ぐ。

## 駅舎の建屋配置端

`edit_path:change_house_ends`（UI→Unity）のparamsは`house_ends`（both/start/end）。建設パネルが真実源で、IpcClient→IpcEventHandler→RoadPlanManager.SetHouseEnds→RoadRendererの仮PathSummaryへ伝える。初期値はSTATE_EDIT_PATHの同名params。既存形状編集は保存値をロードして使い、配置端の変更は駅詳細のPATCHから行う。Rustの汎用send_window_action転送を使うため専用Rust分岐は不要。

## プロップ個別配置の高さ

world:change_stateのSTATE_EDIT_PROPとedit_prop:change_settingsはheight（地面からの相対高さm）を受け取る。edit_prop:update_instancesの要素はx/y/z/rotation/scaleで、yにその配置時の相対高さを載せる。Tauriは各点のyをPOST /api/v1/props/instancesへ送り、GETでも同じyを受け取る。高さ指定は全種別共通、0は地表、負値は地中。描画では地形標高へ加算し、モデルの拡大率を掛けない。

3D側のPageUp/PageDown（0.1m刻み、Shiftで1m）と中ボタンドラッグ回転で変わった値は、Unityがedit_prop:placement_settings（rotation/height）でパネルへ返す。UIはこれをchange_settingsへエコーせず、以後パネルのステッパーを操作したときだけchange_settingsを送る（UnityとUIの往復ループを作らない）。

## 2Dパス形状編集パネルの同期

`path_shape_editor` ウィンドウ（`PathShapeEditorPanel`）は、敷設中パスのノードとベジエ制御点を都市マップ（`CityMapCanvas`）の上で編集する。形状の真実源はUnity（`RoadPlanManager`）のままで、パネルは `edit_path:update_shape` を受けて描き、操作の結果だけを `edit_path:shape_command` で送る（コマンド入力・全状態出力。建物直接配置の `placement_command`/`placement_state` と同じ構図）。UIは受信値を再送しないためループしない。

- 開く: `edit_path:update_shape` の `is_finalized && is_curved`（曲線モードでの右クリック確定）を受けた敷設パネル（`usePathConstruction`）が `open_registered_window("path_shape_editor")` で自動的に開く。直線モードや配置中は敷設パネルの「2Dで編集」ボタンで手動で開く。
- 閉じる: 敷設パネルの確定ボタン（`close_registered_window("path_shape_editor")`）、敷設パネル（`path_build`/`track_build`/`infra_build`）のクローズ（Rust `window.rs` の後始末）、`update_shape` の `phase=inactive` 受信（パネルが自分を閉じる）。**2Dパネルを閉じても敷設セッションは続く**（Unityへは何も送らない）。
- 編集できるのは `phase=editing` のときだけ。配置中（`placing`）は表示のみ。標高・盛土は2Dでは編集せず、ノード選択（`select_node`）で敷設パネルのステッパーの対象を切り替える。
- 曲率・勾配はUnityのローカル推定（`alignment`）をそのまま色分け・区間ラベルに使う。権威値は従来どおり `/paths/construction-cost` を敷設パネルの費用カードが表示する。
- 高架・地下（`height_editable`）のバリアントでは地図の下に**縦断面ストリップ**（`PathProfileStrip`）を出す。横軸は曲線を伸ばした始点からの距離、縦軸は標高で、`alignment.samples` の路面・地盤プロファイルと `alignment.nodes` のアンカー位置から描く（線形評価の対象外の種別では折れ線長と相対高さだけの相対表示）。ここではノードを**上下ドラッグで高さ（地面相対）だけ**変更し、ドラッグ終了時に既存の `edit_path:change_node` を1回送る（Unityが `min_height` でクランプして適用し `update_shape`／`select_node` で返す）。横方向には動かせない。勾配の区間ラベル（`labels` の勾配側）も同じ位置に描く。
- ノード番号（1始まり）は地図・縦断面ストリップ・Unityの3Dビュー（`RoadPlanManager.NodeLabels` のIMGUIラベル。選択中は強調色）で共通にし、どのノードを指しているかを対応付ける。

### パスの確定可否

`edit_path:update_shape` は `can_confirm` を常送します。これはUnityの形状確定状態とローカル交差検証（立体交差と川・海・湖の横断）を合わせた真偽値で、形状が未確定または干渉中ならfalseです。Tauriはこの値だけで確定を許可せず、同じ全線ペイロードの `/paths/validate` 応答も照合します。既存の `world:highlight_objects` によるパス・建物の警告は、メッシュ着色ではなく半透明の占有箱として描画します。同イベントの params には `colliding_river_ids`（地表レベルで横切っている川のID文字列の配列。`/paths/validate` と `/paths/interference` の同名フィールドをそのまま渡す）も含み、Unity は該当する川の範囲を同じ占有箱で示します。
敷設パネルは `/paths/validate` の `alignment_violations` を保持し、線番号（1始まり）と各線の始点からの距離区間を添えて曲率・勾配違反を表示します。パス干渉・川横断・形状未確定・検証待ち・検証エラーを区別し、APIエラーの理由は省略せず再試行ボタンを出します。原因不明の拒否を赤い干渉と断定しません。送信直前の再検証でも同じ説明を使い、古い形状の結果は表示しません。2D表示はUnityの推定値なので、全線検証で拒否された区間を敷設パネルの案内で確認できます。費用欄は確定不可でも2点以上あれば表示します。

# 天気の表示と選択

`ambience:volume {volume}`はTauri→Unityの音量通知（master×sfx、0〜1）。Cockpitが設定変更時とグラフィックの起動通知（`system:graphic_ready`）の受信時に送り、CameraAmbience（環境音）・ConstructionAmbience（工事音）・VehicleAmbience（走行音）・VisualEffectManager（完成・解体音）が所有するAudioSourceへ適用する。高度と天気の音響制御はUnity内で完結する。

天気と時刻の正はUnityにあり、バックエンドへの書き込みや経済パラメータは追加しない。マップの気候区分だけはUnityが `GET /api/v1/city` の `climate` から読む。Cockpitは購読後に問い合わせ、応答した天気に合わせて表示を更新する。

| イベント | 方向 | ペイロード・意味 |
| --- | --- | --- |
| `weather:query` | Tauri → Unity | 現在の天気を問い合わせる |
| `weather:set` | Tauri → Unity | `{id: string または null}`。有効なIDは選択して固定、nullは固定を解除。未知のIDは無視 |
| `weather:state` | Unity → Cockpit | `{id, locked, rain, snow, presets:[{id, weight, rain, snow}]}`。rainは0=なし、1=霧雨、2=雨、3=大雨。snowは0=なし、1=小雪、2=雪、3=吹雪。weightは今の気候・季節での出現割合(%) |
| `epoch:prepare` | Tauri → Unity | `{id: string}`。エポック演出の開始。1.5秒の日没と0.5秒の画面フェードを開始し、結果画面を閉じるまで黒幕を保持する |
| `epoch:darkened` | Unity → Tauri | `{id: string}`。黒幕描画後の通知。Rustは現在の演出IDのみUIへ中継し、UIは処理の完了状況に応じて結果またはTipsを表示する |
| `epoch:executing` | Tauri → Unity | `{executing: bool}`。falseは結果画面のクローズ・結果なし・失敗による演出終了。都市を読み直し、0.5秒のフェードインと1.5秒の日の出へ進む。trueはIDなしの日没開始にも対応する |

天気の送信は`utils/ipc.ts`の`queryWeather` / `setWeather`から`send_window_action`へ、`weather:state`はRust `ipc.rs`でCockpitだけに配信する。Unityは天気の切り替わり・固定変更・出現割合の変化のたびに状態を送る。候補と出現割合はUnityが持ち、Tauriに抽選処理を複製しない。テンプレートモードOFF時はUIから固定解除を送る。

エポック演出は`utils/epochPresentation.ts`から`begin_epoch_presentation`を呼び、RustがIDを保持して開始を送る。HTTP実行は日没と並行し、`utils/epochFlow.ts`で暗転完了まで応答の表示を保留する。`set_epoch_tips_visible`は初期ロードと独立したTipsを開閉する。結果画面の`params.extra.epochPresentationId`を共通クローズ処理が読み、`finish_epoch_presentation`で演出を終了する。Tauri内の`epoch:executing`はHUDの無効化に使い、開始から結果画面のクローズまでtrueを維持する。詳細は[天気と環境光](../geometries/U_天気と環境光.md)を参照。


`/paths/validate` の `alignments` は敷設する全線の設計後の線形指標で、費用カードと確定可否の表示に優先する。`/paths/earthwork-preview` の `path_profiles` は各線の `[x,絶対標高,z]` 配列で、Unityは形状確定後の3Dモデルに使用する。道路・地上線路は造成後、高架は設計縦断を表示する。操作中のHUD・2D線形はローカルの近似値である。


造成の切土・盛土を示すオレンジ／青の半透明ボリュームと体積凡例は表示しない。RoadEarthworkPreviewは造成後のpath_profiles取得だけを担い、道路・線路モデル自体の標高へ反映する。

## 経路提案

経路提案はUnityの編集世代付き絶対座標をTauriから読み取り専用HTTP APIへ送り、成功した候補を既存shape_commandで一括反映する。イベント、固定条件、取消しの詳細は[経路提案](経路提案.md)を参照。

### ノード間距離の上限警告

`edit_path:update_shape.max_span_violations` は `[{segment_index, distance, limit}]`。添字は区間の始点ノードの0始まり、距離と上限はm単位。Unityの赤線と同じ `max_span` 判定を使い、曲線長や高低差ではなく隣接アンカーのXZ直線距離を比較する。上限と等しい距離は許可する。違反解消時・制限なし・点不足では空配列を送る。

違反があるとUnityの `can_confirm` はfalse。Tauriの形状編集パネルは対象区間の中央に、距離・上限を示す引出線付き吹き出しを描く。カーブ・勾配と同じ描画処理を使い、ドラッグ中は再評価前の警告を非表示にする。道路・軌道建設の確定欄は「形状にエラーがあります」の簡潔な表示とし、パネル共通の0.72remの文字サイズを使う。共通の `usePathConstruction` は違反を受けた時点で確定を禁止し、送信直前のガードにも反映する。

## 線形警告と検証結果の共有

道路・軌道の建設パネルが確定送信と同じ全線を `/paths/validate` へ渡し、返された線形指標と描画用の違反区間をUnityへ共有する。形状編集パネルは独自に検証を問い合わせず、Unityから再配信された同じ結果を表示する。単線・複線とも、建設パネルと2Dの要約は全線の最悪値、3Dと2Dの吹き出しは各違反区間の最悪値となる。

`edit_path:update_shape` の `revision` は形状・設定変更で更新される。建設パネルは検証時の世代を付けた `edit_path:shape_command`（`op=apply_alignment`、`alignment_json` に `alignments` のJSON）を送り、Unityは世代が一致する編集状態でだけ採用する。nullは検証待ちへの切替を表す。応答待ちの操作・取消し・別セッションへの切替後に、以前の検証を採用しない。

再配信の `alignment` は `authoritative=true`、バックエンドと同じ違反フラグ `curve_violated` / `grade_violated`、全線の警告折れ線群 `warnings` を含む。各点は `[x, 絶対標高, z]`。`samples` は先頭線の縦断表示用で、警告位置と区間の最悪値は間引き前の評価から作る。上限付近の数値誤差の扱いもサーバーに従う。

操作中・結果待ちはUnityの暫定推定を使う。造成後縦断の非同期受信でも3Dと2Dを再通知するが、同じ世代の全線検証がすでにあればその結果を維持する。選択や警告だけの通知では建設パネルの検証を再起動せず、往復ループを防ぐ。

駅舎通路のpassage_mode（elevated/ground/underground）はSTATE_EDIT_PATH/STATE_EDIT_STATIONと、edit_path:change_passage_mode、edit_station:placement_command/stateで共有する。プレビューと保存で同じ方式を使う。

## ドライブの開始

`drive:open`はCockpitのブルドーザー右隣からUnityへ送る。paramsは`src/i18n/<言語>/drive.json`の`drive`辞書（キー→表示文言）。`IpcClient`は`DriveMode.Ensure().Open`へ渡す。Unity側が車種/路線・開始位置・乗車HUDを保持し、表示中の再送は終了として扱う。転送には既存Rustの`send_window_action`を使い、新しいEnvelopeやバックエンドAPIは持たない。仕様は[車両の走行描画](../geometries/O_車両の走行描画.md#o12-ドライブモード)を参照。

## 初期ロードの表示契約

UI→Unityは既存の `ui:action` Envelopeの `payload.action` / `payload.params` を使う。`game:loading` は黒画面と入力抑制を開始する。`game:start` のparamsは `{loading_id: 整数}` で、描画準備を開始するが黒画面は維持する。Unity→UIの `game:loaded` はpayload直下の `{loading_id: 整数}` を返す。初期描画完了後、表示解禁まで1秒間隔で再送し、Rustは現在の要求世代だけを受理する。UI→Unityの `game:reveal` は同じloading_idをparamsに載せ、`game:loaded` を通知済みの世代だけを表示する。受信した瞬間の準備状態は再判定しない（Tipsは既に閉じているため、ここで捨てると黒画面から戻れなくなる）。

`system:graphic_ready` はプロセスの起動完了であり、都市の描画完了ではない。遅延起動・再接続ではRustが開始要求を再送する。ゲーム中の再接続でもTipsを使い、初期設定は再表示しない。


開発時のUnityなし起動: 通常の `npm run tauri dev`（Rustの `cfg(all(debug_assertions, dev))`）では、Unityが接続中でない場合に限り、バックエンドの開始要求成功とセッション確認後に描画待ちを省略し、Tipsを開始ボタンへ切り替える。接続中かどうかは `system:graphic_ready`（接続）と `system:graphic_stopped`（正常終了・EditorのPlay停止）で追跡し、「一度でも接続したか」では判定しない（それだとPlayを止めた後の再ロードで永遠に待つ）。接続中は描画完了を待ち、待機中にUnityが停止した場合はその時点で開始ボタンへ切り替える。待機中にUnityが接続した場合は開始要求を再送して描画待ちに戻る。`tauri build`（`--debug` を含む）およびリリースビルドには省略処理を含めない。環境変数やバックエンドのNuitka／uvicorn判定は使用しない。

## 道路・線路の制限速度標識

表示設定の道路タブの「制限速度標識を表示」は独立したON/OFFボタンで、`view:change_view`の`layer=road`、`flags`の`speed_limit`を使う。既定はOFF。通常表示・交通量などの色分けと併用でき、状態照会と編集モード終了時の復元は他の表示トグルと同じ経路を使う。

Unityの`PathSpeedLimitOverlay`は、一覧APIの`PathSummary.max_speed`をkm/hのまま表示する。対象は街路・高速道路・鉄道・モノレールで、公共交通の路線区間は表示しない。都市の規制から適用された値もパス個別に指定された値も、バックエンドが保持する同じフィールドを読む。線形制約後の`base_speed`や混雑込みの走行速度は使わず、欠損した値を推定しない。

標識は道路・線路の中心を指す、赤い円・白地・青い数字の画面アイコン。`PathManager.TryGetPathLabelPosition`が`center_point`を実際の描画中心線へ射影し、高架・勾配の標高を保持する。カメラ正面を向き、上下アニメーションは付けない。アラートと同じCanvas生成・多言語フォントを使い、基準サイズ100pxを100m基準で0.3〜1.5倍に縮尺する。マウス入力は遮らない。

標識は接写・通常モデルが表示される範囲だけで表示する。長い道路は中心区間の描画LODを参照し、遠景・Hidden・差し替え生成待ちでは隠す（[LOD処理](../geometries/E_LOD処理.md)）。道路の非表示・個別非表示・計画中表示OFF・アンロードにも追従し、都市切り替えで消去する。近傍候補は既存セル逆引きから0.5秒ごとに更新する。表示中のパスの速度変更も通常のデータ再取得後に反映する。

意匠は`SpeedLimitSignStyle`として、数値・位置・表示状態から分離している。現在は全言語で日本式を用い、言語別の意匠はStyle実装と`SetSignStyle`で差し替えられる。写真・画像アセットは使用しない。実装検証は`unity/Tests/SpeedLimitSigns/run.ps1`、APIの値の検証はコア側のスキーマ直列化テストで行う。

## 道路の区間編集モード

`STATE_EDIT_ROAD_MEMBERS` は道路（名前付き道路。[道路網](../economics/3_道路網.md#35-道路名前付き道路)）の区間を3Dビューのクリックで付け外しするモードで、道路詳細パネル（`road_detail`）の設定タブの「区間を編集」で遷移する。`world:change_state` の params は `{state, road_id, color, path_ids, session_id}`（`path_ids` は開始時点の所属区間、`session_id` は開始のたびにパネルが採番する番号で、`edit_road:ended` の照合に使う）。

Unity は `RoadMemberEditManager` が担う。入力プリセット `ROAD_MEMBER_EDIT`（PATH=`MODE_EDIT_ROAD_MEMBERS`、他=`MODE_NONE`）で街路・高速道路の区間のクリックだけを受け付け、`edit_road:toggle_path` を UI へ送る。表示は `STATE_EDIT_PATH` と同じ建設系の表示プリセットで、計画中の区間も選べる。所属の付け外しは UI が `POST /api/v1/roads/{id}/members` で行い、結果の全集合を `edit_road:set_members` で送り返すので、Unity は所属を先回りして書き換えない。詳細パネルで結果を見ながら続けて操作するため、`STATE_MERGE_PATH` と同じくこのステートでは Unity ウィンドウを最前面化しない。

終了はパネルの「完了」の `edit_road:cancel {road_id}` で行い、`STATE_NORMAL` は送らない（UI の知らないうちに Unity が別のモードへ移っていた場合に、それを打ち切らないため）。`STATE_NORMAL`・他のステートへの直接の遷移・Unity にフォーカスがあるときの Esc・セッションのリセット・別の道路（または同じ道路の別の `session_id`）での開始でもモードは終わり、どの場合も Unity が `edit_road:ended` を1回送る。`edit_road:ended` には、終わったモードの開始時の `session_id` を型を変えずに添える（開始時に無ければ省く）。モードが終わっても `world:set_road_focus` の強調は残る。

道路詳細のウィンドウが閉じられたときは、Rust（`window.rs` の `close_window_by_label`）が閉じた道路の `road_id` を載せて `world:clear_road_focus` と `edit_road:cancel` を送る（ポップアップは非表示になるだけでアンマウント処理が走らないため。`edit_path:cancel_topology_edit` と同じ理由）。`road_detail` は `EDIT_MODE_WINDOW_TYPES` に入れない。パネル・3Dの強調帯・道路名ラベルを含む全体の仕様は[道路の詳細と道路名](道路の詳細と道路名.md)を参照。

## 起動・再接続時の版照合

`system:graphic_ready` のペイロードは `frontend_version`、`api_contract_version`、`ipc_contract_version`、`core_compatible`。UIは自身の契約番号との一致を検査し、`system:ready` に自身の同3項目と `accepted` を返す。Envelope自体の `version: "1.0"` は変更しない。

UnityはコアとのHTTP契約とUIとのIPC契約が成立するまでマスター取得・ゲーム用操作・セッションポーリングによる開始を解禁しない。不一致のUnityを未接続として扱い、開発用の描画待ち省略に流さない。コアのAPI照合は再起動に備えて再実行する。詳細は[バージョニングと互換性](バージョニングと互換性.md)。
