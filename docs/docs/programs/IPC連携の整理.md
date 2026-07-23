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

各MMFは8KB（8192バイト）。

```
Offset  0: WritePos  (int32) — 次の書き込み位置（バッファ内オフセット）
Offset  4: ReadPos   (int32) — 次の読み取り位置（バッファ内オフセット）
Offset  8: Ring buffer data (8184 bytes)
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
| `view:change_view`              | `ui -> graphic`  | 地形、建物、区画、道路、環境、アラートなどの表示モードを切り替え |
| `view:query_state`              | `ui -> graphic`  | グラフィックプログラムに現在の表示状態を問い合わせる            |
| `view:current_state`            | `graphic -> ui`  | `view:query_state`への応答、およびステート変更時の表示状態通知 |
| `view:set_grid`                 | `ui -> graphic`  | グリッドの表示有無や設定を切り替え。Unity側はグリッド表示とグリッド吸着を連動させる（表示ON=吸着ON） |
| `view:grid_toggled`             | `graphic -> ui`  | 3Dビューのラジアルメニューでグリッド表示がトグルされた（`{ visible }`）。UI側は道路建設パネルのグリッド設定UIの表示状態を追従させる |
| `edit_path:select_node`         | `graphic -> ui`  | パス敷設モードにおいて、敷設済みノードを選択した（`node_id`, `height`=相対標高, `embankment_height` を含む。`node_id=-1` は次回配置ノードの値）。UIパネルの標高・盛土ステッパーの表示と編集対象ノードを同期する。Unityのキー操作や吸着で値が変わったときも送られる |
| `edit_path:change_node`         | `ui -> graphic`  | パス敷設モードにおいて、パネルの標高ステッパーからノードの相対標高を変更した（`node_index`, `height`。`node_index=-1` は次回配置値）。曲線/直線・吸着はラジアルメニューが真実源だが、標高・盛土は既存ノードを選んで数値で微調整する用途のためパネルにもステッパーを残し、Unityキー操作と双方向同期する |
| `edit_path:change_embankment`   | `ui -> graphic`  | パス敷設モードにおいて、パネルの盛土ステッパーから盛土オフセット(-5〜+5m)を変更した（`node_index`, `embankment_height`。`node_index=-1` は次回配置値） |
| `edit_path:update_shape`        | `graphic -> ui`  | パス敷設モードにおいて、敷設中パスの形状が変更された。`selected_points`（全アンカー `[x, 相対高さ, z]`）、`start_control` / `end_control`（端点のベジエ制御点）、`waypoint_controls`（中間アンカーごとの `{control_in, control_out}`。未設定は `null`）、`embankment_heights`、`is_curved`（曲線モードの現在値。真実源はUnity側のラジアルメニューで、点列が空でも常に送られる）、`follow_terrain`（地形追従の個別設定。`-1`＝未設定／`0`＝非追従／`1`＝追従。真実源はUnity側で常に送られる）を含む。UIは確定送信時にこれらの制御点を `start_control` / `end_control` / `waypoints` として、`is_curved` を `curved` として、`follow_terrain`（`-1` 以外のときのみ）をそのままバックエンドへ渡し、調整した形状を永続化する |
| `edit_path:change_platform_side` | `ui -> graphic` | 駅パス敷設モードにおいて、ホーム設置側（`platform_side`=外側/内側/両側。`PLATFORM_SIDE_*`）を変更する。Unity側は敷設中プレビューメッシュのプラットフォーム張り出し側を即座に切り替える。駅バリアント（`platform_width>0`）以外では無視される。初期値は `world:change_state`（`STATE_EDIT_PATH`）の `platform_side` で渡し、以後の切り替えをこのイベントで逐次反映する |
| `edit_path:set_follow_terrain`  | `ui -> graphic`  | パス敷設モードにおいて、地形追従の個別設定（`value`=`-1`未設定/`0`非追従/`1`追従）を変更する。Unity側は敷設中プレビューの地形追従・トンネル可視化を即座に切り替え、確定送信用に `edit_path:update_shape` の `follow_terrain` としてUIへ返す。`height_editable` のバリアント以外では無視される |
| `edit_building:select_parcel`   | `graphic -> ui`  | 建物建設モードにおいて、土地区画を選択した（parcel_id, polygon, position 等を含む） |
| `edit_building:select_point`    | `graphic -> ui`  | 建物建設モード（直接配置）において、土地区画外の地点がクリックされた（`position`=[x,y,z]、および確定時点の回転角 `angle`=度 を含む）。`angle` は中ボタンドラッグでゴーストを回した角度を確定地点へ引き継ぐためのもの。UIはこの地点を中心とする矩形底面をバックエンドの `POST /api/v1/buildings/preview-direct` にプレビュー問い合わせして直接配置モードに入る。確定はUIが `POST /api/v1/buildings/direct` を直接呼ぶ |
| `edit_building:invalid_parcel`  | `graphic -> ui`  | 建物建設モードにおいて、選択中の建物種類と互換性のない土地区画がクリックされた（parcel_id を含む） |
| `edit_building:update_direct_params` | `ui -> graphic` | 建物建設モード（直接配置）において、矩形底面の `width`・`depth`・`angle`(度)・`floors` をUnityへ同期する。Unityはこれらでカーソル追従のリアルタイム矩形ゴーストを描画する（直接配置の底面は「回転付き矩形」でありバックエンド往復なしにUnity単独生成できる）。バリアント選択時および各値の変更時に送信する（地点確定前後どちらでも）。`angle` はマイナス・360超も生値で扱い、正規化(mod 360)はUIの確定送信時のみ行う |
| `edit_building:update_direct_angle` | `graphic -> ui` | 建物建設モード（直接配置）において、中ボタンドラッグによる回転が終了(ボタンリリース)したとき、Unityが確定した回転角 `angle`(度)を通知する。UIは回転ステッパー(`rectAngle`)を更新する。ドラッグ中は送られず、リリース時に1回だけ届く |
| `edit_building:update_shape`    | `ui -> graphic`  | 建物建設モードにおいて、建物の底面形状を変更した              |
| `edit_building:update_floors`   | `ui -> graphic`  | 建物建設モードにおいて、建物の階数を変更した                |
| `edit_building:update_visual_variant` | `ui -> graphic` | 建物建設モードにおいて、外観バリアント(visual_variant1/2/3)を変更した |
| `edit_building:update_effect_range` | `ui -> graphic` | 建物建設モードにおいて、選択中インフラ供給施設(変電所・下水処理場・電波塔など)の効果範囲を3D上に可視化する。`region`(効果範囲の領域。円 `{"type":"circle","center":[x,z],"radius":r}` または不定形 `{"type":"polygon","polygons":[{"exterior":[[x,z]...],"holes":[...]}...]}`。null のときは表示クリア)を含む。値はバックエンドの `POST /api/v1/buildings/preview-effect-range` から取得する。効果範囲は面(フィル)と境界線で可視化し、効果範囲内の土地区画のハイライトは行わない |
| `edit_zone:selected`            | `graphic -> ui`  | 地区編集モードで選択中地区が変化した（右クリック選択 / Shift+左クリック新規作成 / 選択解除）。`zone_id`=0 は選択なし |
| `input:query_modes`             | `ui -> graphic`  | デバッグ用: 各オブジェクト種別の入力/表示モードを問い合わせる       |
| `input:current_modes`           | `graphic -> ui`  | `input:query_modes`への応答、現在の入力/表示モード情報を返す      |
| `edit_parcel:select_point`      | `graphic -> ui`  | 土地区画作成モードにおいて、3Dビュー上の地点がクリックされた       |
| `edit_parcel:select_path`       | `graphic -> ui`  | 土地区画作成モードにおいて、パスが選択された                |
| `edit_parcel:select_parcel`     | `graphic -> ui`  | 土地区画作成モードにおいて、既存土地区画が選択された            |
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
| `edit_connector:show`           | `ui -> graphic`  | 経済主体詳細パネル(AgentDetailPanel)の表示中、施設の接続端子ノード（インフラ接続点 `infra_node_id` / 走行網接続点 `depot_node_id`）を Unity 上に色付きマーカーで読み取り専用ハイライトする。`{ agent_id, infra_node_id, depot_node_id }` を含む。`infra_node_id == depot_node_id`（共用接続ノード）ならマーカーは1つ。0 のノードIDは表示しない |
| `edit_connector:hide`           | `ui -> graphic`  | 接続端子ノードのハイライト表示をクリアする。パネルを閉じた／別エージェントへ遷移したときに送信する（`parcel:hide_front_path` と同じ役割） |
| `game:time_update`              | `graphic -> ui`  | ゲーム内時間（エポック・日付）の更新通知                  |
| `system:shutdown`               | `ui -> graphic`  | グラフィックプログラムを終了する                      |
| `system:ready`                  | `ui -> graphic`  | UIプログラムの準備完了通知                        |
| `system:ping`                   | `ui <-> graphic` | 疎通確認用                                 |
| `system:pong`                   | `ui -> graphic`  | pingへの応答                              |
| `session:reset`                 | `ui -> graphic`  | 現在の3Dオブジェクトや地形のデータを全てクリアする            |
| `game:start`                    | `ui -> graphic`  | バックエンドのセッションが作成され、ゲームが開始されたことを通知   |
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

`STATE_EDIT_PROP` は地形プロップ（森・岩・茂みなどの装飾）の配置モードで、プロップ配置パネル（`prop_placement`）で種別を選ぶと遷移する。`world:change_state` の params に `prop_type`・`placement_mode`（`"instance"` | `"scatter"`）・初期設定 `rotation`/`scale`（個別）・`density`/`seed`（範囲）を含める。配置には2モードある。**個別モード**は左クリックで1個ずつ配置（カーソル追従は半透明ゴースト、確定すると不透明の実モデル、右クリックで直近を取り消し）し、配置列を `edit_prop:update_instances` でUIへ送出する。**範囲モード**は左クリックで散布範囲のポリゴン頂点を置き（3頂点以上でseedベースの散布ゴーストを簡易プレビュー、右クリックで直近頂点を取り消し）、ポリゴンを `edit_prop:update_shape` でUIへ送出する。回転/スケール/密度/seed の変更は `edit_prop:change_settings` でプレビューに即反映する。確定送信（個別=`POST /api/v1/props/instances`、範囲=`POST /api/v1/props/scatters`）はUI側が直接行い、成功後に `edit_prop:finish` を送信してUnity側の配置状態をリセットする（散布の永続レンダラおよび既存プロップの削除UIは別途）。プロップはゼロコストの装飾のため未開放セルにも配置でき、`template_mode` ゲートはない。詳細な仕様は `genesis-docs/docs/geometries/M_地形プロップ.md` を参照。

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
