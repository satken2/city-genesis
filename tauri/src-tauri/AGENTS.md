# src-tauri フォルダ

Tauri(Rust)側。ウィンドウ管理(HUD・ポップアップ・Tips)、UnityとのMMF IPC、地形エディタのファイル入出力を担当する。React側は`../src`(`../AGENTS.md`)。

プロトコル・起動フロー・ウィンドウ管理の仕様の正は公開docs(`docs/docs/programs/IPC連携の整理.md`・`コンポーネント連携.md`)。ここにはRust実装の不変条件と罠だけを書く。

## ファイル一覧

| ファイル | 内容 |
| --- | --- |
| `src/main.rs` | エントリ。`lib.rs`の`run()`を呼ぶだけ |
| `src/lib.rs` | Tauriコマンドの集約と`setup`。ゲーム開始/ロード(`begin_initial_loading`)、地形エディタのコマンド群(`copy_terrain_for_editor`・`export_terrain_as_npy`等) |
| `src/window.rs` | ウィンドウ管理の本体。HUD4枚の生成・配置(`create_hud_windows`/`layout_hud_windows`/`set_hud_visible`)、`open_window`(レジストリ駆動のポップアップ生成と再利用)、`close_window_by_label`(閉じたときの後始末)、`reassert_topmost`、通知キュー、`launch_graphic` |
| `src/ipc.rs` | MMF IPC。リーダースレッド、`send_to_parent`(Unity宛て送信)、Unity→UIイベントをWebViewへ中継するmatch。`CAPACITY`=1MiB |
| `src/messages.rs` | `Envelope`・`WindowContextDto`等の型 |
| `src/state.rs` | プロセス全体の共有状態`AppState`(開いているウィンドウ、非表示LRU、ジオメトリ記憶、接続フラグ) |
| `src/settings.rs` | アプリ設定の永続化 |
| `src/terrain_export_paths.rs` | 地形エクスポート先の検証(原本ディレクトリの拒否) |
| `config/window-registry.json` | 全ポップアップの定義(サイズ・layout・インスタンスポリシー)。新しいウィンドウはここに定義を足す |
| `config/window-mutex-groups.json` | 同時に開けないウィンドウのグループ |
| `capabilities/default.json` | Tauriコマンドを呼べるウィンドウラベルの許可リスト |
| `tauri.conf.json` / `build.rs` | Tauri設定とビルドスクリプト |

## 知らないと危険なこと

### IPC

- `CAPACITY`(1MiB)はUnity側`Assets/Scripts/IPC/IpcTransport.cs`と、`Envelope`は`IpcEnvelope.cs`と同一に保つ。ズレると通知が黙って捨てられる。
- Unity→UIのイベントは`ipc.rs`の受信matchで`app.emit`(全ウィンドウ)しないとどのWebViewにも届かない。常駐1枚だけが扱うもの(`remove_object:request_confirm`・`weather:state`)は`emit_to(LABEL_COCKPIT)`。UI→Unityは`send_window_action`→`ui:action`の汎用転送で専用分岐は要らない。
- 1通が`CAPACITY`に収まらないと`send_to_parent`が`Message too large`で失敗する。握り潰すと「UIは動くがUnityだけ更新が止まる」になるので、大きくなり得るペイロード(区画一括プレビュー等)は失敗をユーザーへ通知する。周期送信は置かない(Unity不在中に溜まって`MMF buffer full`になる)。
- `setup`では`ipc::start_ipc`をウィンドウ生成より先に呼ぶ。WebView起動直後の送信(`ambience:volume`)が`IPC not initialized`で失われるため。
- Unity接続中の判定は`graphic_connected`(`system:graphic_ready`で立ち`system:graphic_stopped`で下りる)。再接続時のポップアップ一掃用`graphic_ready_seen`(一度立ったら戻さない)を流用すると、EditorのPlay停止後の再ロードで描画完了を永遠に待つ。

### ウィンドウ

- `close_window_by_label` は `tutorial:window_visibility` をTutorialへ送る。非表示WebViewは再利用されアンマウントされないので、この通知とUI側の実表示判定を併用してチュートリアルのウィンドウ条件を失効させる。

- エポック演出は`epoch_presentation_id`で開始から結果画面クローズまで保持する。`epoch_result`の`params.extra.epochPresentationId`を`close_window_by_label`で照合して終了する(Reactのアンマウントに頼らない)。Tipsは`open_tips_window_with_mode`の引数で初期ロード/エポックを切り替え、エポック側は処理完了時に閉じる。

- ウィンドウを生成する(`WebviewWindowBuilder::build()`に至る)コマンドは必ず`async fn`。同期コマンドはメインスレッドで走り、`build()`はイベントループへ投げて完了を待つので自分で塞いだままフリーズする。コマンド以外(イベントハンドラ等)から生成するときは`std::thread::spawn`で逃がす(`enqueue_notification`が実例)。
- 新しいウィンドウラベルは`capabilities/default.json`の`windows`にも追加する(漏れるとそのウィンドウからTauriコマンドを呼べない。ポップアップは`popup-*`で網羅済み)。
- topmostの打ち直しは`reassert_topmost`(HWNDへ直接`SetWindowPos(HWND_TOPMOST)`)で行う。`set_always_on_top(true)`ではtaoが新旧フラグに差分が無いと`SetWindowPos`を発行せず、裏に回った状態から戻れない。呼び出し箇所は`set_hud_visible(true)`直後・`WindowEvent::Focused(false)`・`system:focus_gained`・`system:graphic_ready`。
- ポップアップのクローズは破棄ではなく非表示+LRU再利用(`MAX_HIDDEN_WINDOWS`)。閉じたときの後始末(`STATE_NORMAL`復帰、ハイライト消去、`edit_path:cancel_topology_edit`・`edit_road:cancel`・`edit_zone:finish_merge {merged:0}`等)はReactのアンマウントでは走らないので`close_window_by_label`に書く。LRU上限での本破棄時には再実行しない。
- `EDIT_MODE_WINDOW_TYPES`は「閉じたらUnityを`STATE_NORMAL`へ戻すウィンドウ」の一覧。編集ステートに入るものに加え、`view:apply_preset`で表示を差し替えるもの(`transit_management`・`transit_creation`)や入力プリセットを上書きするもの(`agent_creation`)も含める。他の編集ウィンドウが表示中なら送らない。
- 他のモードを巻き込んではいけないパネル(`road_detail`・`path_detail`)はこの一覧に入れず、対象IDを載せた専用cancelを送る。
- 再利用でもReactの状態は`open_seq`(世代番号)で必ずリセットされる。非表示→再表示と転用でインクリメントして`WindowContextDto`で配り、`App.tsx`が再マウントキーに含める。表示中への再オープン要求では増やさない。ラベルは不変で、転用は`window-context-updated`で新contextを配る。
- ジオメトリ記憶(`saved_geometries`)は内側(クライアント領域)基準。`outer_size`で記憶すると復元側の`set_size`が内側基準なので開き直すたびに枠の分だけ膨張する。
- ポップアップ配置はHUDの占有領域を避ける。定数は`window.rs`冒頭(`HUD_LEFT_RESERVED`/`HUD_BOTTOM_RESERVED`/`HUD_RIGHT_BOTTOM_RESERVED`)。アイコンパネルの高さとシステムトレイの幅はReactが実測して`resize_icon_panel`/`resize_system_tray`で通知する。
- 表示トレイは`DISPLAY_TRAY_WIDTH/HEIGHT`が正で、`DisplayTray.css`と食い違うとボタンが切れる/透明領域がクリックを吸う。
- Unity→UIイベントを受けてウィンドウを開くときは、内容をウィンドウコンテキスト(`params.extra`)に載せる(`zone_merge`の`pendingMerge`)。イベントを投げてからウィンドウを開くと購読が間に合わず届かない。

### 地形エディタ

- 出力先は`terrain_export_paths.rs`が検証し、`terrains/original/`配下(リンク先を含む)を拒否する。付随JSONを含め最初の書き込みより前に止める。
- `.npy`は`build_npy_float16`が自前でヘッダを組む。外部の`python`に依存させない(Storeエイリアスやnumpy未導入で失敗する)。
- `terrain_maps.yaml`への追記(`upsert_terrain_yaml_entry`)は行単位のテキスト置換。serde_yamlで再シリアライズすると他エントリのコメント・空行が消える。
- `AppData/UrbanGenesis/terrain_editor/{editor_id}/`はウィンドウ間の初期データ受け渡し専用。編集途中の保存・再開・一覧のコマンドは持たない。

## 参照

- docs: `programs/IPC連携の整理.md`(イベント一覧・起動フロー・初期ロードの表示契約)、`programs/コンポーネント連携.md`(プロセス構成・セッション・UIのウィンドウ管理)、`geometries/L_地形データ作成マニュアル.md`と`D_地形形状.md` §D.10〜D.14(地形エディタの入出力)
- Skill: `genesis-ipc`
- React側のウィンドウ追加手順・HUD構成: `../AGENTS.md`。Unity側の契約: `unity/Assets/Scripts/IPC/AGENTS.md`
