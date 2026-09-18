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
- バッファ末尾にメッセージが収まらない場合、ラップマーカー（length = 0）を書き、メッセージを先頭から書く。読み取り側はラップマーカーを見たらReadPosを0にリセットする。

# イベント種別

## 命名規則

イベント名は全レイヤー（Unity / Rust / フロントエンド）でコロン区切りで統一する（例: `edit_path:select_node`）。

## 一覧

| イベント種類                          | コール方向            | 内容                                    |
| ------------------------------- | ---------------- | ------------------------------------- |
| `world:select_object`           | `graphic -> ui`  | オブジェクトを選択する                           |
| `world:change_state`            | `ui -> graphic`  | グラフィックのモード(通常モード、土地区画作成モード、etc.)を切り替え |
| `view:change_view`              | `ui -> graphic`  | 地形、建物、区画、道路、環境、アラートなどの表示モードを切り替え（`{layer, mode, sub_mode, flags}`）。`flags` は色モードと独立した表示トグルのCSV文字列（`plan`=計画中の表示、`public`=公共施設ハイライト。road/building レイヤーのみ使用）。**`flags` が `null`（またはキー欠落）の場合は現在のフラグを維持**し、空文字は全トグルOFFを意味する |
| `view:query_state`              | `ui -> graphic`  | グラフィックプログラムに現在の表示状態を問い合わせる            |
| `view:current_state`            | `graphic -> ui`  | `view:query_state`への応答、およびステート変更時の表示状態通知。各レイヤーは `{mode, sub_mode, flags}`（`flags` はONのトグルのCSV。無ければ空文字） |
| `view:set_grid`                 | `ui -> graphic`  | グリッドの表示有無や設定を切り替え。Unity側はグリッド表示とグリッド吸着を連動させる（表示ON=吸着ON） |
| `view:grid_toggled`             | `graphic -> ui`  | 3Dビューのラジアルメニューでグリッド表示がトグルされた（`{ visible }`）。UI側は道路建設パネルのグリッド設定UIの表示状態を追従させる |
| `camera:query_state`            | `ui -> graphic`  | 3Dカメラの現在位置と視線方位を問い合わせる。2D都市マップがカメラマーカーを描くために、開いたときと再読み込みのときに送る |
| `camera:state`                  | `graphic -> ui`  | `camera:query_state`への応答。3Dカメラの現在位置と視線方位（`{ position:[x,y,z], yaw }`）。`yaw` は北（ワールド+Z）を0とする時計回りの度。**カメラの動きへの追従配信は行わない**（問い合わせ1回につき1回だけ送る） |
| `edit_path:select_node`         | `graphic -> ui`  | パス敷設モードにおいて、敷設済みノードを選択した（`node_id`, `height`=相対標高, `embankment_height` を含む。`node_id=-1` は次回配置ノードの値）。UIパネルの標高・盛土ステッパーの表示と編集対象ノードを同期する。Unityのキー操作や吸着で値が変わったときも送られる |
| `edit_path:change_node`         | `ui -> graphic`  | パス敷設モードにおいて、パネルの標高ステッパーからノードの相対標高を変更した（`node_index`, `height`。`node_index=-1` は次回配置値）。曲線/直線・吸着はラジアルメニューが真実源だが、標高・盛土は既存ノードを選んで数値で微調整する用途のためパネルにもステッパーを残し、Unityキー操作と双方向同期する |
| `edit_path:change_embankment`   | `ui -> graphic`  | パス敷設モードにおいて、パネルの盛土ステッパーから盛土オフセット(-5〜+5m)を変更した（`node_index`, `embankment_height`。`node_index=-1` は次回配置値） |
| `edit_path:update_shape`        | `graphic -> ui`  | パス敷設モードにおいて、敷設中パスの形状が変更された。`selected_points`（全アンカー `[x, 相対高さ, z]`）、`start_control` / `end_control`（端点のベジエ制御点）、`waypoint_controls`（中間アンカーごとの `{control_in, control_out}`。未設定は `null`）、`embankment_heights`、`is_curved`（曲線モードの現在値。真実源はUnity側のラジアルメニューで、点列が空でも常に送られる）、`follow_terrain`（地形追従の個別設定。`-1`＝未設定／`0`＝非追従／`1`＝追従。真実源はUnity側で常に送られる）を含む。UIは確定送信時にこれらの制御点を `start_control` / `end_control` / `waypoints` として、`is_curved` を `curved` として、`follow_terrain`（`-1` 以外のときのみ）をそのままバックエンドへ渡し、調整した形状を永続化する。**2Dパス形状編集パネル向けの追加フィールド**: `phase`（`placing`/`editing`/`inactive`）、`selected_index`（3D側で選択中のノード添字。-1=未選択）、`lateral_offsets`（複線の横オフセット列。単線は空）、`alignment`（Unityがローカル推定した線形。`radius_limit`/`grade_limit`/`min_curve_radius`/`max_grade`、約5m間隔の `samples`=`[x, z, 曲線半径(直線はnull), 勾配%, 始点からの距離m, 路面の絶対標高, 地盤の絶対標高]`、原因別違反区間の最悪値 `labels`、アンカーと同順の `nodes`=`[始点からの距離m, アンカー直下の地盤標高]`。線形評価の対象外の種別では省略）、`height_editable`（バリアントが標高編集可能か。縦断面ストリップの表示条件）、`min_height`（相対高さの下限m）。全WebViewへブロードキャストされるため、敷設パネルと2Dパネルの両方が同じイベントを受ける |
| `edit_path:change_platform_side` | `ui -> graphic` | 駅パス敷設モードにおいて、ホーム設置側（`platform_side`=外側/内側/両側。`PLATFORM_SIDE_*`）を変更する。Unity側は敷設中プレビューメッシュのプラットフォーム張り出し側を即座に切り替える。駅バリアント（`platform_width>0`）以外では無視される。初期値は `world:change_state`（`STATE_EDIT_PATH`）の `platform_side` で渡し、以後の切り替えをこのイベントで逐次反映する |
| `edit_path:set_follow_terrain`  | `ui -> graphic`  | パス敷設モードにおいて、地形追従の個別設定（`value`=`-1`未設定/`0`非追従/`1`追従）を変更する。Unity側は敷設中プレビューの地形追従・トンネル可視化を即座に切り替え、確定送信用に `edit_path:update_shape` の `follow_terrain` としてUIへ返す。`height_editable` のバリアント以外では無視される |
| `edit_path:change_lateral_offsets` | `ui -> graphic` | パス敷設モードにおいて、複合パス（軌道複線＝単線パス群の平行敷設）の各線の横オフセット（`lateral_offsets`=float配列。m。敷設方向右が正）と進行方向（`travel_directions`=int配列。`DIR_*`。`lateral_offsets` とindex対応）を変更する。Unity側は敷設中プレビューメッシュを各オフセット位置の平行パス群として即座に描き直す（空配列は単一パス＝中心線1本のプレビューに戻す）。進行方向は駅バリアントのホーム設置側の解決と進行方向矢印の表示に使われる。UIパネルで線路数・進行方向を変えたときに送られ、配置中のノード列は維持される。初期値は `world:change_state`（`STATE_EDIT_PATH`）の `lateral_offsets` / `travel_directions` で渡し、以後の変更をこのイベントで逐次反映する。確定時の平行展開・一括作成はUI側が行うため、このイベントはプレビュー表示専用 |
| `edit_path:shape_command`       | `ui -> graphic`  | 2Dパス形状編集パネル（`path_shape_editor`）からのノード・制御点操作。`op`=`select_node`（`index`）/ `move_node`（`index`,`x`,`z`）/ `move_control`（`index`,`which`=`in`/`out`,`x`,`z`）/ `insert_node`（`edge_index`,`x`,`z`）/ `remove_node`（`index`）。XZだけを動かし、地面相対の高さ・制御点の絶対標高はUnity側が維持する（3DのXZドラッグと同じ規則）。吸着は適用しない。**ドラッグ中は送らずドラッグ終了時に1回だけ送る**。Unityは `Editing` フェーズ以外・3Dでドラッグ中は無視し、適用後は `edit_path:update_shape`（全状態）と、ノード操作なら `edit_path:select_node` を返す |
| `edit_path:query_shape`         | `ui -> graphic`  | 敷設中パスの現在形状の問い合わせ。Unityは `edit_path:update_shape` を1回返す（セッション外なら `phase=inactive`）。`update_shape` は変化時にしか送られないため、開くたびに再マウントされる2Dパス形状編集パネルがマウント時に送る（`camera:query_state` と同じ問い合わせ応答型） |
| `edit_path:select_merge_target` | `graphic -> ui`  | パス結合の相手選択モード（`STATE_MERGE_PATH`）において、結合相手のパスがクリックされた（`path_id`, `path_name`）。UI（パス詳細パネル）はこのIDで `POST /api/v1/paths/merge`（`dry_run=true`）を叩き、結合前後の長さを表示してから確定する |
| `edit_path:select_split_point`  | `graphic -> ui`  | パス分割の分割点選択モード（`STATE_SPLIT_PATH`）において、分割点がクリックされた（`point`=[x,y,z]。対象パスの中心線へ射影済み）。UIはこの地点で `POST /api/v1/paths/{id}/split`（`dry_run=true`）を叩き、前半・後半の長さを表示してから確定する |
| `edit_path:finish_topology_edit` | `ui -> graphic` | パスの結合／分割の確定送信に成功したことを通知する（`removed_path_id`＝結合で吸収されて消えたパスID。分割時は0）。Unity は「選択モード終了 → 消えたパスの即時破棄 → 通常モードへ復帰 → データ再取得」を1つのハンドラでこの順に行う。**`session:reload` と `world:change_state` を別々に送ってはいけない**——差分リロードは複数フレームにまたがるコルーチンで、後続の状態遷移が割り込むと再生成・削除が中途半端に終わり、形状が更新されない／消えたはずのパスが残る（道路敷設の `edit_path:finish` が1イベントにまとまっているのと同じ理由） |
| `edit_path:cancel_topology_edit` | `ui -> graphic` | パス詳細パネル（`path_detail`）のウィンドウが閉じられたときに Rust（`window.rs`）が送る打ち切り通知。Unity は結合／分割の対象選択モード中だった場合のみ、モードを終了して通常モードへ戻す（**ポップアップは閉じても破棄されず非表示になるだけで React のアンマウント処理が走らないため、パネル任せにすると選択モードのまま入力が固まる**）。選択モード中でなければ何もしない＝ブルドーザ等の他モードを巻き込まないための専用イベント |
| `edit_building:select_parcel`   | `graphic -> ui`  | 建物建設モードにおいて、土地区画を選択した（parcel_id, polygon, position 等を含む） |
| `edit_building:select_point` | `graphic -> ui` | 旧定義。現行の建物直接配置は`placement_command`/`placement_state`を使用する |
| `edit_building:invalid_parcel`  | `graphic -> ui`  | 建物建設モードにおいて、選択中の建物種類と互換性のない土地区画がクリックされた（parcel_id を含む） |
| `edit_building:update_direct_params` | `ui -> graphic` | 旧定義。現行の建物直接配置は`placement_command`/`placement_state`を使用する |
| `edit_building:update_direct_angle` | `graphic -> ui` | 旧定義。現行の建物直接配置は`placement_command`/`placement_state`を使用する |
| `edit_building:update_shape`    | `ui -> graphic`  | 建物建設モードにおいて、建物の底面形状を変更した              |
| `edit_building:update_floors`   | `ui -> graphic`  | 建物建設モードにおいて、建物の階数を変更した                |
| `edit_building:update_structure` | `ui -> graphic` | 建物建設モードにおいて、構造種別 `structure`（木造=1・準耐火=2・耐火=3。0=構造なし）を変更した。Unityはキャッシュ済みの底面から、構造つきパーツフォルダ（`a{variant}s{structure}` 等）でプレビューを再生成する。`STATE_EDIT_BUILDING` の遷移パラメータにも `structure` が含まれ、`building_construction:copy_source` はコピー元の `structure` を返す |
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
| `edit_transit:update_stops`     | `graphic -> ui`  | 公共交通の路線設置モードにおいて、配置中の停留所座標列が変更された（`{ stops: [[x,y,z], ...] }`。公共交通パスは相対Y=0のため y は0）。新設パネルが停留所数の表示とPOST用の停留所列を更新する |
| `edit_transit:finish`           | `ui -> graphic`  | 公共交通の路線設置モードを終了する。UI側が `POST /api/v1/transit/lines` の送信に成功した後に送信され、Unity側の停留所配置状態をリセットしてデータをリロードする（`edit_resource:finish` と同じ役割） |
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
| `system:focus_gained`           | `graphic -> ui`  | グラフィックのウィンドウがフォアグラウンドになった通知(`OnApplicationFocus(true)`)。フルスクリーン扱いのグラフィックが活性化するとシェルが最前面指定のUIを裏へ回すことがあるため、UIはこれを受けて表示中の全ウィンドウの最前面を打ち直す。UIが裏に回ったときの復帰経路 |
| `session:reset`                 | `ui -> graphic`  | 現在の3Dオブジェクトや地形のデータを全てクリアする。以後 `session:reload` が届くまでカメラ由来のロードとカメラ状態の `PATCH /api/v1/city` を止める（バックエンドが新しい都市を準備している間に中途半端なデータや前の視点を取り込まないため） |
| `game:start`                    | `ui -> graphic`  | バックエンドのセッションが作成され、ゲームが開始されたことを通知。ペイロードは空。グラフィックはこれを受けて `GET /api/v1/city` の `camera`（セーブ済みの視点、新規都市ではマップ定義の初期視点）を読み、カメラを復元する（`null` なら原点の既定視点）。既にゲーム中でも送られ、その場合もカメラだけ復元する |
| `game:end`                      | `ui -> graphic`  | セッションが破棄され、ゲームが終了したことを通知               |
| `session:reload`                | `ui -> graphic`  | バックエンドからデータを取得して、3Dを再描画する             |

## 確定送信の責務分担

建設・編集の「確定」をバックエンドへ送信する責務は、**確定操作を所有する側**が持つ。

- **パネル確定型**（建物建設・パス敷設・土地区画の作成/形状編集・資源エリア・公共交通・プロップ配置）: UI(Tauri)のパネルに確定ボタンがあるため、**UIが直接バックエンドへPOST/PATCHする**。成功後に `edit_*:finish` 系イベント（または `session:reload`）を送り、Unity側の編集状態をリセットしてデータをリロードさせる。Unityは形状の組み立てと `edit_*:update_*` 系イベントによるUIへの逐次通知のみを担う。
- **3Dジェスチャ完結型**（地区の塗り分け＝クリック/ドラッグの即時割当、接続端子ノードのドラッグ移動）: パネルの確定ボタンを持たず3D上の操作で完結するため、**Unityが直接バックエンドへ送信する**。

# グラフィックプログラムのステート

Unityのグラフィックプログラムは、いくつかのステートを持つ。各モードの実体はUnityのC#で定義された「表示モード（`VisualMode`）」「入力モード（`InputMode`）」のセットである。
ステートごとに以下のようになる。空欄は何もしないという意味。

|                         | `STATE_NORMAL` | `STATE_EDIT_PATH` | `STATE_EDIT_BUILDING` | `STATE_EDIT_ZONE` | `STATE_EDIT_PARCEL_SHAPE` | `STATE_EDIT_RESOURCE` |
| ----------------------- | -------------- | ----------------- | --------------------- | ----------------- | ------------------------- | ---------------------- |
| `Parcel - VisualMode`   | 表示しない          | 表示する              | 表示する                  | 表示する              | 境界線表示（編集対象のみ非表示）          | 境界線表示                  |
| `Parcel - Click`        | 詳細情報表示         | -                 | 建設場所選択                | -                 | -                         | -                      |
| `Parcel - Mouseover`    | ハイライト          | -                 | ハイライト                 | -                 | -                         | -                      |
| `Building - VisualMode` | 通常表示           | 計画中の建物も表示         | 計画中の建物も表示             | 計画中の建物も表示         | 計画中の建物も表示                 | 計画中の建物も表示              |
| `Building - Click`      | 詳細情報表示         | -                 | -                     | -                 | -                         | -                      |
| `Building - Mouseover`  | ハイライト          | -                 | -                     | -                 | -                         | -                      |
| `Path - VisualMode`     | 通常表示           | 計画中のパスも表示         | 計画中のパスも表示             | 地区色分け表示           | 計画中のパスも表示                 | 計画中のパスも表示              |
| `Path - Click`          | 詳細情報表示         | -                 | -                     | 選択中地区へ即時割当（Shift+クリックで新規作成） | -                         | -                      |
| `Path - Mouseover`      | ハイライト          | -                 | -                     | 操作案内ツールチップ表示      | -                         | -                      |
| `Terrain - VisualMode`  | 通常表示           | 通常表示              | 通常表示                  | 通常表示              | 通常表示                      | 通常表示                   |
| `Terrain - Click`       | -              | ノード追加             | 直接配置: 地点確定          | -                 | 頂点ドラッグ/辺上追加               | 頂点追加（Placing）/ 頂点ドラッグ・辺上追加（Editing） |
| `Terrain - Mouseover`   | -              | マーカー移動            | 直接配置: 矩形ゴースト追従       | -                 | 辺ハイライト                    | 辺ハイライト（Editingのみ）       |
| `Zone - VisualMode`     | 表示しない          | 表示する              | 表示する                  | 表示する              | 非表示                       | 非表示                    |
| `Zone - Click`          | 詳細情報表示         | -                 | -                     | -                 | -                         | -                      |
| `Zone - Mouseover`      | ハイライト          | -                 | -                     | -                 |                           |                        |

`STATE_EDIT_BUILDING` の**直接配置**（土地区画外への建設）では、底面が「回転付き矩形」でありUnity単独で生成できるため、地形ホバー中にカーソル追従のリアルタイム矩形ゴーストを表示する（`BuildingPlanManager` が `edit_building:update_direct_params` で受け取った `width`/`depth`/`angle`/`floors` からローカル生成する。バックエンド往復不要）。中ボタンドラッグで矩形を1度単位で即時回転でき（地点確定の前後どちらでも有効）、回転はリリース時に `edit_building:update_direct_angle` でUIへ1回だけ通知して回転ステッパーに反映する。左クリックで地点を確定（`edit_building:select_point` に確定時の `angle` を含めてUIへ通知）すると、以降はUIの `POST /api/v1/buildings/preview-direct` → `edit_building:update_shape` が権威プレビューとなる（回転角の正規化=mod 360 はUIの確定送信 `POST /api/v1/buildings/direct` 時のみ）。土地区画上へのホバーではゴーストを表示せず区画ハイライトへ切り替わる。土地区画への設置は回転角指定・リアルタイムゴーストの対象外（形状はバックエンド依存のため）。

`STATE_EDIT_RESOURCE` はテンプレート作成モード専用で、天然資源エリアの新規作成にのみ使用する（既存エリアの編集はまだ実装されていない）。Terrain上の操作は2フェーズに分かれる: Placing(左クリックで頂点を末尾に追加していく)→Editing(頂点数3以上で右クリックすると形状が確定し、`STATE_EDIT_PARCEL_SHAPE` と同じ操作感の頂点ドラッグ・辺クリック挿入・右クリック削除に切り替わる)。バックエンドへの確定送信(`POST /api/v1/resources`)はUI側が直接行い、成功後に `edit_resource:finish` を送信してUnity側の編集状態をリセットする。

`STATE_EDIT_TRANSIT` は公共交通の路線設置モードで、公共交通の新設パネル（`transit_creation`）で路線種別を選ぶと遷移する。`world:change_state` の params に `path_variant`（選択した種別の区間パスのバリアント＝`transit_master` の `transit_path_id`）と `stop_path_variants`（停留所を設置できる下地パスのバリアント一覧）を含める。Unity は `RoadPlanManager` と似た操作感で停留所を配置する: 左クリックで停留所を追加（**停留所は `stop_path_variants` に属する稼働中パス＝駅・街路などの上にのみ吸着でき、外れた場所には置けない**。バックエンドの `_resolve_stop_paths` 制約と一致）、右クリックで確定して編集フェーズへ、編集フェーズでは停留所ドラッグ移動・区間クリックで挿入・停留所右クリックで削除ができる。配置・編集のたびに停留所座標列を `edit_transit:update_stops` でUIへ送出し、確定時の `POST /api/v1/transit/lines`（`path_variant` と `stops` を含む）はUI側が直接行う。成功後に `edit_transit:finish` を送信してUnity側の編集状態をリセットし、新設された公共交通パスをオーバーレイに反映させる。

設置済みの公共交通パス（`PATH_TRANSPORT`）は、`view:change_view` の `transit` レイヤーで路線種別ごとに表示ON/OFFできる（`sub_mode` に表示する `transit_path_id` のCSV文字列を渡す）。Unity は種別色の帯（オーバーレイ）として下地道路の上に描画する。

`STATE_EDIT_PROP` は地形プロップ（森・岩・茂みなどの装飾）の配置モードで、プロップ配置パネル（`prop_placement`）で種別を選ぶと遷移する。`world:change_state` の params に `prop_type`・`placement_mode`（`"instance"` | `"scatter"`）・初期設定 `rotation`/`scale`（個別）・`density`/`seed`（範囲）を含める。配置には2モードある。**個別モード**は左クリックで1個ずつ配置（カーソル追従は半透明ゴースト、確定すると不透明の実モデル、右クリックで直近を取り消し）し、配置列を `edit_prop:update_instances` でUIへ送出する。**範囲モード**は左クリックで散布範囲のポリゴン頂点を置き（3頂点以上でseedベースの散布ゴーストを簡易プレビュー、右クリックで直近頂点を取り消し）、ポリゴンを `edit_prop:update_shape` でUIへ送出する。回転/スケール/密度/seed の変更は `edit_prop:change_settings` でプレビューに即反映する。個別モードでは3D側でも中ボタンドラッグで回転、PageUp/PageDown で高さを変えられ、結果は `edit_prop:placement_settings` でパネルへ書き戻す。確定送信（個別=`POST /api/v1/props/instances`、範囲=`POST /api/v1/props/scatters`）はUI側が直接行い、成功後に `edit_prop:finish` を送信してUnity側の配置状態をリセットする。既存プロップの削除は配置パネルではなく `STATE_REMOVE_OBJECTS`（ブルドーザ）で行い、範囲配置は `remove_object:request_confirm` / `remove_object:confirm_result` の確認を挟む。プロップはゼロコストの装飾のため未開放セルにも配置でき、`template_mode` ゲートはない。詳細な仕様は `genesis-docs/docs/geometries/M_地形プロップ.md` を参照。

`STATE_MERGE_PATH` / `STATE_SPLIT_PATH` は既存パスの結合・分割の「対象選択モード」で、パス詳細パネル（`path_detail`）の操作パネルにある結合／分割ボタンで遷移する。`world:change_state` の params に操作対象の `path_id`（結合では結合後に残る側＝survivor、分割では分割元）を含める。どちらも稼働中（`STATUS_ACTIVE`）のパスにのみ表示される。

Unity は `PathTopologyEditManager` が対象選択を担う。**結合**では対象パスを青、結合できる候補パスを黄で着色し（候補＝対象と端点ノードを共有し、そのノードに他のパスが繋がっておらず、同一バリアントの稼働中パス。曲線の折れ・通行方向の矛盾など残りの条件はバックエンドが判定する）、入力プリセット `PATH_MERGE`（PATH=`MODE_PATH_MERGE`、他=`MODE_NONE`）で候補のクリックだけを受け付ける。**分割**では入力プリセット `PATH_SPLIT`（PATH／TERRAIN=`MODE_PATH_SPLIT`、他=`MODE_NONE`）でカーソルのワールド座標を対象パスの中心線へ射影し、射影点にマーカー（パスを横切る棒＋円）を描く。パス脇の地形をクリックしても垂線の足が分割点になる（バックエンドの射影と同じ考え方）。**このモードでは共通吸着（ノード吸着など）を適用しない**——端点ノードへ吸着すると常に「端点に近すぎる」分割になってバックエンドに拒否されるため。

選択結果は `edit_path:select_merge_target` / `edit_path:select_split_point` でUIへ返り、UIは同じエンドポイントの `dry_run=true` で結合後・分割後の長さを取得してパネルに表示する。プレイヤーが確定するとUIが `dry_run` なしで再送し、成功後に `edit_path:finish_topology_edit` を**1発だけ**送ってUnity側の後始末（着色・マーカーのクリア、消えたパスの破棄、通常モード復帰、再描画）をまとめて行わせる。キャンセル時のみ `STATE_NORMAL` を送る。長さを確認して確定するパネルを操作し続ける必要があるため、`STATE_EDIT_CONNECTOR` と同じくこのステートでは Unity ウィンドウを最前面化しない。

`STATE_EDIT_CONNECTOR` は施設の接続端子ノード（インフラ接続点 `infra_node_id` / 走行網接続点 `depot_node_id`。[[26_インフラ網]]の接続端子ノード参照）をドラッグで移動する編集モードで、経済主体詳細パネル（`agent_detail`）の「接続点の編集」トグルボタンで遷移する。`world:change_state` の params に `agent_id` / `infra_node_id` / `depot_node_id` を含める。Unity は `ConnectorEditManager` が対象ノードを強制表示・色付きマーカーで強調し、入力プリセット `CONNECTOR_EDIT`（NODE=`MODE_EDIT`、他=`MODE_NONE`）で対象ノードのみドラッグ移動可能にする。ドラッグ確定時は `NodeInputHandler` が `PATCH /api/v1/nodes/{node_id}`（position）を送る。**ノード位置の変更はパスが1本も接続されていない場合のみ許可され**、接続済みノードの移動はバックエンドが拒否し、Unity は元位置に自動で戻す（[[26_インフラ網]] §26.3 の端子位置移動制限）。トグルボタンを持つ `agent_detail` パネルを前面に残すため、このステートでは他の編集ステートと異なり Unity ウィンドウを最前面化しない。編集を終了すると `STATE_NORMAL` を送り、パネルが開いている間は `edit_connector:show` を再送して読み取り専用ハイライトに戻す。

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
  ↓
[Rust]
  1. taskbar.hide()
  2. topmenu.show()
  3. loading.hide()
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

`edit_path:update_shape` は `can_confirm` を常送します。これはUnityの形状確定状態とローカル交差検証（立体交差と川の横断）を合わせた真偽値で、形状が未確定または干渉中ならfalseです。Tauriはこの値だけで確定を許可せず、同じ全線ペイロードの `/paths/validate` 応答も照合します。既存の `world:highlight_objects` によるパス・建物の警告は、メッシュ着色ではなく半透明の占有箱として描画します。同イベントの params には `colliding_river_ids`（地表レベルで横切っている川のID文字列の配列。`/paths/validate` と `/paths/interference` の同名フィールドをそのまま渡す）も含み、Unity は該当する川の範囲を同じ占有箱で示します。
敷設パネルは `/paths/validate` の `alignment_violations` を保持し、線番号（1始まり）と各線の始点からの距離区間を添えて曲率・勾配違反を表示します。パス干渉・川横断・形状未確定・検証待ち・検証エラーを区別し、APIエラーの理由は省略せず再試行ボタンを出します。原因不明の拒否を赤い干渉と断定しません。送信直前の再検証でも同じ説明を使い、古い形状の結果は表示しません。2D表示はUnityの推定値なので、全線検証で拒否された区間を敷設パネルの案内で確認できます。費用欄は確定不可でも2点以上あれば表示します。

# 天候の表示と選択

`ambience:volume {volume}`はTauri→Unityの音量通知（master×sfx、0〜1）。Cockpitが設定変更時と2秒ごとに送り、CameraAmbience（環境音）・ConstructionAmbience（工事音）・VehicleAmbience（走行音）・VisualEffectManager（完成・解体音）が所有するAudioSourceへ適用する。高度と天候の音響制御はUnity内で完結する。

天候の正はUnityにあり、バックエンドへの書き込みや経済パラメータは追加しない。Cockpitは購読後に問い合わせ、応答した天候に合わせて表示と環境音を更新する。

| イベント | 方向 | ペイロード・意味 |
| --- | --- | --- |
| `weather:query` | Tauri → Unity | 現在の天候を問い合わせる |
| `weather:set` | Tauri → Unity | `{id: string または null}`。有効なIDは選択して固定、nullは固定を解除。未知のIDは無視 |
| `weather:state` | Unity → Cockpit | `{id, locked, rain, presets:[{id, weight, rain}]}`。rainは0=なし、1=霧雨、2=雨、3=大雨 |

送信は`utils/ipc.ts`の`queryWeather` / `setWeather`から`send_window_action`へ、受信はRust `ipc.rs`でCockpitだけに配信する。Unityは選択・抽選・固定変更後にも状態を送る。候補とウェイトはUnityが持ち、Tauriに抽選処理を複製しない。テンプレートモードOFF時はUIから固定解除を送る。詳細は[天気と環境光](../geometries/U_天気と環境光.md)を参照。


`/paths/validate` の `alignments` は敷設する全線の設計後の線形指標で、費用カードと確定可否の表示に優先する。`/paths/earthwork-preview` の `path_profiles` は各線の `[x,絶対標高,z]` 配列で、Unityは形状確定後の3Dモデルに使用する。道路・地上線路は造成後、高架は設計縦断を表示する。操作中のHUD・2D線形はローカルの近似値である。


造成の切土・盛土を示すオレンジ／青の半透明ボリュームと体積凡例は表示しない。RoadEarthworkPreviewは造成後のpath_profiles取得だけを担い、道路・線路モデル自体の標高へ反映する。
