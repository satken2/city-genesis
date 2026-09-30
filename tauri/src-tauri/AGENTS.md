# src-tauri フォルダ

Tauri(Rust)側。ウィンドウ管理(HUD・ポップアップ・Tips)、UnityとのMMF IPC、バックエンドへのHTTP(版ヘッダー)、地形エディタの作業コピーとシナリオへの保存を担当する。React側は`../src`(`../AGENTS.md`)。

プロトコル・起動フロー・ウィンドウ管理の仕様の正は公開docs(`docs/docs/programs/IPC連携の整理.md`・`コンポーネント連携.md`)。ここにはRust実装の不変条件と罠だけを書く。

## ファイル一覧

| ファイル | 内容 |
| --- | --- |
| `src/main.rs` | エントリ。`lib.rs`の`run()`を呼ぶだけ |
| `src/lib.rs` | Tauriコマンドの集約と`setup`。ゲーム開始/ロード(`begin_initial_loading`)、セッションポーリング、地形エディタのコマンド群(`prepare_scenario_terrain_editor`・`load_terrain_editor_data`・`save_scenario_terrain`) |
| `src/version.rs` | `../../version.json` の埋め込み定義、コアの版情報の取得 `core_info()` と照合 `check_frontend_version()`、ユーザーフォルダ `user_data_dir()`、ロード事前検証 |
| `src/window.rs` | ウィンドウ管理の本体。モニター上への配置(`Placement`・`build_placed`)、HUD4枚の生成・配置(`create_hud_windows`/`layout_hud_windows`/`set_hud_visible`)、ポップアップの表示先(`resolve_popup_monitor`・`update_display_target`)、`open_window`(レジストリ駆動のポップアップ生成と再利用)、`close_window_by_label`(閉じたときの後始末)、`reassert_topmost`、通知キュー、`launch_graphic` |
| `src/ipc.rs` | MMF IPC。リーダースレッド、`send_to_parent`(Unity宛て送信)、Unity→UIイベントをWebViewへ中継するmatch。`CAPACITY`=1MiB |
| `src/messages.rs` | `Envelope`・`WindowContextDto`等の型 |
| `src/state.rs` | プロセス全体の共有状態`AppState`(開いているウィンドウ、非表示LRU、ジオメトリ記憶、接続フラグ) |
| `src/settings.rs` | アプリ設定の永続化 |
| `config/window-registry.json` | 全ポップアップの定義(サイズ・layout・インスタンスポリシー)。新しいウィンドウはここに定義を足す |
| `config/window-mutex-groups.json` | 同時に開けないウィンドウのグループ |
| `capabilities/default.json` | Tauriコマンドを呼べるウィンドウラベルの許可リスト |
| `tauri.conf.json` / `build.rs` | Tauri設定(WebViewのCSPを含む)とビルドスクリプト |

## 知らないと危険なこと

### IPC

- `CAPACITY`(1MiB)はUnity側`Assets/Scripts/IPC/IpcTransport.cs`と、`Envelope`は`IpcEnvelope.cs`と同一に保つ。ズレると通知が黙って捨てられる。
- Unity→UIのイベントは`ipc.rs`の受信matchで`app.emit`(全ウィンドウ)しないとどのWebViewにも届かない。常駐1枚だけが扱うもの(`remove_object:request_confirm`・`weather:state`)は`emit_to(LABEL_COCKPIT)`。UI→Unityは`send_window_action`→`ui:action`の汎用転送で専用分岐は要らない。
- 1通が`CAPACITY`に収まらないと`send_to_parent`が`Message too large`で失敗する。握り潰すと「UIは動くがUnityだけ更新が止まる」になるので、大きくなり得るペイロード(区画一括プレビュー等)は失敗をユーザーへ通知する。周期送信は置かない(Unity不在中に溜まって`MMF buffer full`になる)。
- 保存直前の撮影 `request_thumbnail_capture` は、Unity接続中(`graphic_connected`)のときだけ `camera:capture_thumbnail` を送り、未接続なら送らずに false を返す(不在の間の要求をMMFへ溜めない。UIは false なら撮影を待たずに保存する)。
- `setup`では`ipc::start_ipc`をウィンドウ生成より先に呼ぶ。WebView起動直後の送信(`ambience:volume`)が`IPC not initialized`で失われるため。
- Unity接続中の判定は`graphic_connected`(`system:graphic_ready`で立ち`system:graphic_stopped`で下りる)。再接続時のポップアップ一掃用`graphic_ready_seen`(一度立ったら戻さない)を流用すると、EditorのPlay停止後の再ロードで描画完了を永遠に待つ。

### ウィンドウ

- `close_window_by_label` は `tutorial:window_visibility` をTutorialへ送る。非表示WebViewは再利用されアンマウントされないので、この通知とUI側の実表示判定を併用してチュートリアルのウィンドウ条件を失効させる。

- エポック演出は`epoch_presentation_id`で開始から結果画面クローズまで保持する。`epoch_result`の`params.extra.epochPresentationId`を`close_window_by_label`で照合して終了する(Reactのアンマウントに頼らない)。Tipsは`open_tips_window_with_mode`の引数で初期ロード/エポックを切り替え、エポック側は処理完了時に閉じる。

- ウィンドウを生成する(`WebviewWindowBuilder::build()`に至る)コマンドは必ず`async fn`。同期コマンドはメインスレッドで走り、`build()`はイベントループへ投げて完了を待つので自分で塞いだままフリーズする。コマンド以外(イベントハンドラ等)から生成するときは`std::thread::spawn`で逃がす(`enqueue_notification`が実例)。
- 新しいウィンドウラベルは`capabilities/default.json`の`windows`にも追加する(漏れるとそのウィンドウからTauriコマンドを呼べない。ポップアップは`popup-*`で網羅済み)。
- topmostの打ち直しは`reassert_topmost`(HWNDへ直接`SetWindowPos(HWND_TOPMOST)`)で行う。`set_always_on_top(true)`ではtaoが新旧フラグに差分が無いと`SetWindowPos`を発行せず、裏に回った状態から戻れない。呼び出し箇所は`set_hud_visible(true)`直後・`WindowEvent::Focused(false)`・`system:focus_gained`・`system:graphic_ready`。
- ポップアップのクローズは破棄ではなく非表示+LRU再利用(`MAX_HIDDEN_WINDOWS`)。閉じたときの後始末(`STATE_NORMAL`復帰、ハイライト消去、`edit_path:cancel_topology_edit`・`edit_road:cancel`・`edit_zone:finish_merge {merged:0}`等)はReactのアンマウントでは走らないので`close_window_by_label`に書く。LRU上限での本破棄時には再実行しない。
- `EDIT_MODE_WINDOW_TYPES`は「閉じたらUnityを`STATE_NORMAL`へ戻すウィンドウ」の一覧。編集ステートに入るもの(`resource_edit`・`prop_placement`を含む)に加え、`view:apply_preset`で表示を差し替えるもの(`transit_management`・`transit_creation`)や入力プリセットを上書きするもの(`agent_creation`)も含める。パネルのアンマウント時の`STATE_NORMAL`は×では走らないので、編集ステートに入るウィンドウを足し忘れるとUnityが編集ステートのまま固まる。
  - Unityの編集ステートは1つなので、一覧の種別は互いに同時に開かない。足す種別は`window-mutex-groups.json`で一覧の全種別と双方向に排他にする(キーは開く側の種別なので両方向に書く。`window.rs`のテストが検査する)。併存させると先に開いた側のパネルが別のステートに上書きされたまま残る。
  - 閉じると常に`STATE_NORMAL`を送る。別の編集ウィンドウを開いたことによる排他クローズでも、新しいウィンドウの表示より先に届くので、新しいパネルの編集ステートを上書きしない。
  - 一覧の種別を開くと`apply_mutex_rules`が`hud:mode_changed {mode: null}`を全ウィンドウへ送り、HUDの常駐モード(ブルドーザ・地区編集)を解除する。詳細パネルの「編集」等どの入口から開いても解除されるよう、UI側の開く処理ごとには持たない。
- 排他グループは新規に開くときだけでなく、非表示(再利用待ち)ウィンドウの再表示でも適用する。
- 他のモードを巻き込んではいけないパネル(`road_detail`・`path_detail`・`agent_detail`)はこの一覧に入れず、専用cancelを送る(`edit_road:cancel {road_id}`・`edit_path:cancel_topology_edit`・`edit_connector:hide`)。Unityは自分のモード中のときだけ通常へ戻す。編集ウィンドウと併存する詳細パネルの後始末に素の`STATE_NORMAL`を送ると、後から開いた編集ウィンドウのステートまで打ち切る。
- 再利用でもReactの状態は`open_seq`(世代番号)で必ずリセットされる。非表示→再表示と転用でインクリメントして`WindowContextDto`で配り、`App.tsx`が再マウントキーに含める。表示中への再オープン要求では増やさない。ラベルは不変で、転用は`window-context-updated`で新contextを配る。
- ジオメトリ記憶(`saved_geometries`)のサイズは内側(クライアント領域)基準。`outer_size`で記憶すると復元側の`set_size`が内側基準なので開き直すたびに枠の分だけ膨張する。位置は物理座標で記憶する。
- モニター上への配置は`Placement`(位置は物理座標)を通し、生成は`build_placed`(非表示で生成→配置→表示)で行う。`set_position(LogicalPosition)`やビルダーの`.position()`は使わない。taoは論理座標を、移動では今いるモニターの倍率で、生成では座標を含む最初のモニターの倍率で換算するので、倍率の違うモニターがあると別の場所・別のモニターに出る。
- ポップアップ(レジストリ駆動)の表示先は`popup_target`が決め、マルチディスプレイモードではHUDの`current_monitor_index`とは別のモニターになる。HUD側に置くRust直接生成のウィンドウ(Tips・通知など)と取り違えない。表示先に関わる設定(HUDのモニター・モード・ポップアップのモニター)は`update_display_target`で書き換える。表示先が変わったときの開いているポップアップの移動と記憶の破棄を、この関数がまとめて行う。
  - 最大化中のウィンドウはtaoの最大化解除(`set_position`・`set_size`の内部でも起きる)が`ShowWindow`を伴うので、非表示でも表示されてしまう。再利用待ちの最大化ウィンドウは表示先の変更時に動かさず、`open_window`で開き直すときに表示先へ置き直す。
- ポップアップ配置は、HUDのあるモニターに置くときだけHUDの占有領域を避ける。定数は`window.rs`冒頭(`HUD_LEFT_RESERVED`/`HUD_BOTTOM_RESERVED`/`HUD_RIGHT_BOTTOM_RESERVED`)。アイコンパネルの高さとシステムトレイの幅はReactが実測して`resize_icon_panel`/`resize_system_tray`で通知する。
- 表示トレイは`DISPLAY_TRAY_WIDTH/HEIGHT`が正で、`DisplayTray.css`と食い違うとボタンが切れる/透明領域がクリックを吸う。
- Unity→UIイベントを受けてウィンドウを開くときは、内容をウィンドウコンテキスト(`params.extra`)に載せる(`zone_merge`の`pendingMerge`)。イベントを投げてからウィンドウを開くと購読が間に合わず届かない。

### HTTP

- ユーザーフォルダ(`<ベース>/UrbanGenesis`)は `setup` の先頭で `version::init_user_data_dir` が一度だけ決め、`version::user_data_dir()` で引く。ベースはコア・Unityと同じ決め方で、Windowsは環境変数 `LOCALAPPDATA` → `APPDATA` → 既知フォルダ(LocalAppData)、それ以外は `app_local_data_dir()` の親(Linuxは `XDG_DATA_HOME` を反映)。既知フォルダを先に使うと、`LOCALAPPDATA` の無い環境でコアとフォルダがずれる。セーブの置き場(`open_save_directory`)と地形エディタの作業コピー(`terrain_editor_dir`)もここを基準にし、パスを各所で組み立てない。
- `tauri.conf.json` の `csp` はスクリプトを `'self'` に限り、`eval` を許さない。`dangerousDisableAssetCspModification: ["style-src"]` は、Tauri が style-src へハッシュ・nonce を足すと `'unsafe-inline'` が無効になり、React の `style` 属性が本番だけ効かなくなるのを防ぐため。デスクトップの `tauri dev` は `devUrl`(Vite)を直接読むため、`csp` は適用されない(Tauri がCSPを付けるのは tauri プロトコルで組み込みアセットを配るときだけ)。そのため `devCsp` は置かず、CSP違反は `tauri build` の成果物で確認する。画像・通信の許可は `http://127.0.0.1:*`・`http://localhost:*` なので、接続先に `[::1]` は使えない。
- `Cargo.toml` の tauri は `devtools` 機能を持たない(本番ビルドに開発者ツールを含めない)。

### 地形エディタ

- 地形エディタはシナリオの地形を編集する。`prepare_scenario_terrain_editor(scenario_id)` が `GET /api/v1/scenarios/{id}/terrain` から作業コピー(`terrain_editor/<editor_id>/meta.json`・`near.bin`)を作って `editor_id` を返し、`save_scenario_terrain(editor_id, …)` が `PUT /api/v1/scenarios/{id}/terrain` へ保存する。形式の正は公開docs `geometries/D_地形形状.md` §D.14.3。
- `save_scenario_terrain` は応答を `ScenarioTerrainSaveResult`(`content_revision`・`locks`・`resource_ids`)で読んでUIへ返す。`resource_ids`(新しく作った資源の {仮ID: DB ID})を欠く応答は形が合わないので失敗にする(地形エディタのUIは資源を編集せず、読み込んだ資源を送り返すだけなので、通常は空)。成功したら作業コピー(`meta.json`、標高を送ったときは `near.bin`)を保存した内容に合わせ、資源のIDも置き換える(`saved_terrain_editor_meta`)。水面・川は null(変更しない)なら書かない。書き込みの失敗は保存を失敗にせずログに残す。
- `editor_id` は `Uuid::to_string()` と同じ正規形(小文字・ハイフン区切り)だけを通す(`is_terrain_editor_id`)。区切り文字や `..` で `terrain_editor/` の外を指させない。
- 保存先のシナリオはUIから受け取らず、作業コピーの `meta.json` の `scenario_id` から引く。
- 作業コピーの `locks.heights_locked` が真(欠けている・形が崩れているときも真とみなす)なら、標高・水面・川は `None` で送る決まり。値が来たらコアへ送る前に `TERRAIN_LOCKED:` で拒否する。
- 作業コピーを作るときは、標高のバイト数が `grid_rows × grid_cols × 2` と一致しなければ拒否する。GET 応答の `locks` は必須。
- 同じシナリオの地形エディタが表示中なら、新しい作業コピーを作らずにその `editor_id` を返す(開き直しで未保存の編集を捨てず、2枚の保存が上書きし合わないため)。準備のたびに、どのウィンドウにも結び付かず作成から1時間を過ぎた作業コピーを削除する。
- コアのエラーは「`CODE`: 本文」の文字列で返す(`scenario_api_error`)。UIは先頭のコードで翻訳文を引く。コードの無い失敗(通信・ファイル・形式の検査)は本文が日本語固定なので、UIは汎用の翻訳文(`scenarios.terrainOpenFailed`・`terrainEditor.saveFailed`)を出し、本文は `console.error` でログにだけ残す。
- `load_terrain_editor_data` は大きな `near.bin` を読むので `async fn`。
- 地形エディタ・3Dプレビューのウィンドウは `window-registry.json` の `entity_type` が `scenario_terrain_editor` で、対象IDは `editor_id`。
- `AppData/UrbanGenesis/terrain_editor/{editor_id}/`はウィンドウ間の初期データ受け渡し専用。書き換えるのは保存の成功時(保存した内容に合わせる)だけで、編集途中の保存・再開・一覧のコマンドは持たない。

## 参照

- docs: `programs/IPC連携の整理.md`(イベント一覧・起動フロー・初期ロードの表示契約)、`programs/コンポーネント連携.md`(プロセス構成・セッション・CSP・UIのウィンドウ管理)、`geometries/L_地形データ作成マニュアル.md`と`D_地形形状.md` §D.10〜D.16(地形エディタの入出力)
- Skill: `genesis-ipc`
- React側のウィンドウ追加手順・HUD構成: `../AGENTS.md`。Unity側の契約: `unity/Assets/Scripts/IPC/AGENTS.md`

## 版の照合

- `src/version.rs` は `../../version.json` の埋め込み定義、HTTPの共通ヘッダー(版)、コア照合、ロード事前検証の共通窓口。
- コアの版情報は `AppState.core_info` の `OnceCell` に保持し、全WebView・開始・ロード・セッション監視が共有する。同時取得は1本にまとまり、未取得時の通信失敗だけは再試行できる。取得できたらセッション監視の開始前に `check_frontend_version` で照合し、フロントのMAJORがコアのMAJORより小さければ `request_app_exit` で終了する(待っても版は変わらないので再試行しない)。コアの再起動・版の差し替えを前提にした定期確認は置かない。開発中にコアやマスターデータを変更したときはフロントも起動し直す。
- `get_core_version` は共有した版情報を返す。接続表示はセッション監視の `session-state` に含める `coreVersion`・`backendConnected` を使い、版情報APIを死活監視に使わない。セッション消失・開発版の2回連続の通信失敗時の後始末は `return_to_topmenu` に集約する。製品版は2回連続で失敗したらUnityへ終了を通知して終了する。1回目は表示状態を保持し、`backend-connection` イベントで操作を止める。`AppState.backend_health` は連続失敗と接続状態を保持し、成功時に失敗回数を解除する。
- Unityは `system:graphic_ready` にフロント版・IPC契約・コア照合結果を付ける。`system:ready` で照合結果を返す。不一致は `graphic_contract_error` に保持し、Unityなしの開発起動として迂回しない。
- ゲーム開始コマンドは、ポップアップや現セッションを閉じる前に版・保存データ・確認済みフラグを検査する。
