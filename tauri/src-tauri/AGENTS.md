# src-tauri フォルダ

Tauri(Rust)側のプログラム。ウィンドウ管理とUnityとのMMF(MemoryMappedFile) IPCを担当する。

## ファイル一覧

| ファイル | 内容 |
| --- | --- |
| `src/lib.rs` | Tauriコマンド定義の集約と起動処理。ウィンドウレジストリ・ミューテックスグループのロード、バックエンド/グラフィックの起動コマンド解決 |
| `src/window.rs` | ウィンドウ管理の本体。HUD(常駐パネル)の生成・配置・表示切替(`create_hud_windows` / `layout_hud_windows` / `set_hud_visible` / `resize_icon_panel` / `resize_system_tray` / `reassert_topmost`)、`open_window`(レジストリ定義に基づくポップアップ生成、single/multiple/single_per_entityのインスタンスポリシー、ミューテックスルール適用)、通知キュー、Tips兼ローディングウィンドウ、`launch_graphic`、`record_window_geometry`(起動中の位置・サイズ記憶)、`close_window_by_label`(ウィンドウを閉じた際の後始末処理 - 編集モードから通常モードへの復帰、接続端子・接道・パス干渉ハイライトの消去、パス結合/分割の対象選択モードの打ち切り`edit_path:cancel_topology_edit`など) |
| `src/ipc.rs` | UnityとのMMF IPC実装。リーダーループ、書き込み(Mutex/Event同期)、`send_to_parent`(Unity宛て送信)。容量1MiBの固定バッファ |
| `src/messages.rs` | `Envelope`等のメッセージ型定義。**Unity側`Assets/Scripts/IPC/IpcEnvelope.cs`と構造を一致させる必要がある** |
| `src/state.rs` | プロセス全体の共有状態 |
| `src/settings.rs` | アプリ設定の永続化 |
| `config/window-registry.json` | 全ポップアップウィンドウの定義(タイトル、サイズ、ポリシー)。**新しいウィンドウを追加したらここに定義を足す** |
| `config/window-mutex-groups.json` | 同時に開けないウィンドウのグループ定義 |

## 知らないとわからないこと

`ambience:volume {volume}`はTauri→Unityの音量通知（master×sfx、0〜1）。Cockpitが読み込み直後・設定変更時・Unity起動時(`graphic-reconnected`)に送り、Unity側の`IpcClient`が最新値を保持して購読者へ渡す。読み込み直後の1通を「IPC not initialized」で失わないよう、`setup`では`ipc::start_ipc`をウィンドウ生成より先に呼ぶ。高度と天候の音響制御はUnity内で完結する。

- **ゲーム中に常駐するHUDは4枚のウィンドウ(`icon-panel` / `cockpit` / `system-tray` / `display-tray`)に分かれている。** Unityの3Dビューに重ねる透明ウィンドウは、見た目が透明な領域でもクリックをWebViewが吸ってしまうため、パネルの実面積ぴったりのウィンドウに分割する必要がある(1枚の全画面ウィンドウにはできない)。位置とサイズの定数は`window.rs`冒頭にまとまっている。**新しいウィンドウラベルを足したら`capabilities/default.json`の`windows`にも必ず追加すること**(漏れるとそのウィンドウからTauriコマンドを呼べない)。ウィンドウのtopmost(最前面)指定は生成時の一度きりでは維持されない(Unityがフルスクリーン扱い=モニタ全面を覆うボーダーレスでフォアグラウンドになるとシェルがUIを裏へ回す・WebView2はhide→showでtopmostが外れることがある)ため、`reassert_topmost`が**表示中の全ウィンドウ**へ`SetWindowPos(HWND_TOPMOST)`を直接発行して打ち直す(現在のZオーダーの相対順は保つ)。**`set_always_on_top(true)`で打ち直してはいけない**: taoは新旧フラグの差分が無いと`SetWindowPos`を発行しないため、既にtopmost指定済みのウィンドウには何も起きず、裏に回った状態から復帰できない。呼び出し箇所は`set_hud_visible(true)`の表示直後、`lib.rs`の`WindowEvent::Focused(false)`(=Unityクリックでフォーカスが移った瞬間)、`ipc.rs`の`system:focus_gained`(Unityの`OnApplicationFocus(true)`。シェルの持ち上げはUnity活性化中に起きるため、Unity側が焦点を得た後のこの通知が本命の復帰経路)と`system:graphic_ready`。
- **ポップアップの配置計算はHUDの占有領域を避ける。** `HUD_LEFT_RESERVED`(左端のアイコンパネル幅)だけ左ドック(`left_panel` / `left_panel_narrow` / `left_top`)を右へずらし(左ドックの既定幅はモニター幅の40% / 20%で、定義が`width_ratio`を持てばそれが優先する。例: 3タブが窮屈な`track_build`は0.26)、`right_top`(右上固定。位置は常に右上でサイズだけ記憶。既定サイズは`width_ratio`×モニター幅、`height_ratio`が無ければ正方形〔作業領域の高さでクランプ〕。2Dパス形状編集パネル用)は画面右端からマージンだけ空け、丈と中央寄せは`HUD_BOTTOM_RESERVED`(下部中央のコックピット高)を引く。右サイドパネルと通知トーストだけは中央のコックピットと重ならないため、薄い`HUD_RIGHT_BOTTOM_RESERVED`(右下のシステムトレイ高)で足りる。HUDのサイズを変えたらこれらの定数だけ直せば全ポップアップに反映される。
- **アイコンパネルの高さとシステムトレイの幅はRust側で決められない**(アイコン数・ボタン数がテンプレートモードで変わる)。React側が実測して`resize_icon_panel` / `resize_system_tray`コマンドで通知し、`layout_hud_windows`がそれぞれ上下中央寄せ・右下寄せで再配置する。**表示トレイ(`display-tray`)はボタン数が固定なので実測通知を持たず、`DISPLAY_TRAY_WIDTH`/`HEIGHT`が幅・高さの正**(`DisplayTray.css`と食い違うとボタンが切れる/透明領域がクリックを吸う)。配置は画面左下(アイコンパネルと同じく左端から`HUD_MARGIN`だけ空ける)。左ドックのポップアップは下端から`HUD_BOTTOM_RESERVED`+マージンを空けるので縦に重ならない。
- **ウィンドウを生成する(`WebviewWindowBuilder::build()`に行き着く)Tauriコマンドは必ず`async fn`にすること。** Tauriは同期コマンドをメインスレッドで実行するが、`build()`はウィンドウ生成をメインスレッドのイベントループへ投げて完了を待つため、同期コマンドから呼ぶとイベントループを自分で塞いだまま待ち続けてアプリ全体がフリーズする(Windows/WebView2)。コマンド以外の文脈(イベントハンドラなど、メインスレッドで走る箇所)からも同様に直接呼べないので、`std::thread::spawn`で逃がす(`enqueue_notification`が実例)。
- **`window.rs`の`EDIT_MODE_WINDOW_TYPES`は「閉じたらUnityを`STATE_NORMAL`へ戻すウィンドウ」の一覧**で、Unityの編集ステートに入るものだけでなく、**開いている間だけ3Dビューの表示を差し替えるウィンドウ(公共交通の一覧・新設)も含める**。Unityは`STATE_NORMAL`復帰で表示を元へ戻すため、ここに載せ忘れると差し替えた表示が残り続ける(ウィンドウは非表示になるだけでReact側のアンマウント処理が走らず、パネル任せにできない)。他に編集モードのウィンドウが表示中なら送らない(道路敷設中に別パネルを開閉しても敷設が中断されないようにするため)。
- Reactコンポーネントから別ウィンドウを開く処理は、Tauriコマンド`open_window`の呼び出しに行き着く。ウィンドウの実体管理(ラベル採番、二重起動防止)はすべてRust側にある。また、ウィンドウが閉じられる際の後始末（エディタを閉じたときに `STATE_NORMAL` に戻す、詳細パネルを閉じたときにインフラ接続マーカーや接道表示を消去するなど）も `window.rs` の `close_window_by_label` で自動的に行う。これにより Webview が突然シャットダウンしても Unity 側の状態が取り残されるのを防ぐ。
- **ポップアップウィンドウのクローズは「破棄」ではなく「非表示化して再利用待ち」が既定動作**(`MAX_HIDDEN_WINDOWS=10` の LRU)。`close_window_by_label` はWebView2を破棄せず `hide()` し、`OpenWindowState.hidden=true` + `AppState.hidden_lru` に追加する。これによりユーザが同じウィンドウ種別を開き直したとき、新規WebView2起動(約0.4秒)を回避して `show()` + `set_focus()` + `window-context-updated` emit だけで済み、体感的に瞬時に開く。非表示ウィンドウが上限を超えると最古のものから本当に破棄される。`open_window` の再利用優先度は (1) 同一 `instance_key` の非表示ウィンドウ → (2) 任意の非表示ポップアップ(window_type/entity/params/title を上書きして転用) → (3) 新規生成。Tauriはラベル変更不可なので、(2)の転用ではラベルは不変(Reactは `window-context-updated` イベントで新しいcontextを受けて再レンダリングする)。クローズ時の副作用(STATE_NORMAL復帰など)は非表示化の時点で実行済みで、LRU上限による破棄時には再実行されない。付随ウィンドウを別ウィンドウの操作から閉じるにはTauriコマンド`close_registered_window(window_type)`(`close_windows_by_type`。開いていなければ何もしない)を使う(敷設パネルの確定で`path_shape_editor`を閉じる)。敷設パネル(`path_build`/`track_build`/`infra_build`)のクローズ時は`close_window_by_label`の後始末で`path_shape_editor`も閉じる(逆に2Dパネルを閉じても敷設セッションは続く)。**再利用でもReactパネルの状態は必ずリセットされる**: `OpenWindowState.open_seq`(世代番号)を非表示→再表示(1)と転用(2)のたびにインクリメントして `WindowContextDto` で配信し、React側(`App.tsx`)が再マウントキーに含めているため。表示中ウィンドウへの再オープン要求(単なるフォーカス移動)ではインクリメントしない。これが無いと、マウント時に1回だけフェッチ/初期化するパネル(例: 建物建設パネルの市庁舎ゲート判定)が前回オープン時の古いstateを引きずる。
- ポップアップウィンドウの位置・サイズは**プロセス起動中のみ**`AppState.saved_geometries`(window_type別)に記憶され、再オープン時に復元される。`WindowEvent::Resized`/`Moved`でリアルタイム保存。ドック配置(layout指定)ウィンドウはサイズのみ、自由配置ウィンドウは位置とサイズの両方が復元される。永続化はされない。新規・再利用の両方で同じレイアウト計算(`compute_window_geometry`)を通る。**記憶するサイズは内側(クライアント領域)基準**: 復元側の `inner_size` / `set_size` が内側基準のため、`outer_size` で記憶すると開き直すたびに枠の厚み分ウィンドウが膨張する(装飾なしでもリサイズ可能ウィンドウは不可視のリサイズ枠を持つ)。位置は `outer_position` / `set_position` のどちらも外枠基準で一致している。
- Unityとの通信内容を変えるときは、Rust側(`ipc.rs`/`messages.rs`)・TS側(`src/utils/ipc.ts`)・Unity側(`IpcClient.cs`)の三箇所と、`genesis-docs`のIPC仕様ドキュメントを揃えて更新する。
- `weather:state`はCockpitだけへ中継する。逆方向の`weather:query` / `weather:set`は共通の`send_window_action`を通る。天候表示を含むCockpitは800×76px。天候候補はWebView2の標準selectポップアップを使うため、候補分の透明なウィンドウ領域を作らない。
- **MMFの容量`CAPACITY`(1MiB)はUnity側`Assets/Scripts/IPC/IpcTransport.cs`の同名定数と必ず一致させること。** 共有メモリは両側とも`CreateOrOpen`で開くため、先に起動した側のサイズが採用され、食い違うと片側だけが範囲外を読む。1メッセージがバッファに収まらないと`write_message`が`Message too large`で失敗し、**呼び出し側が握り潰すと「UI側は動くがUnityだけ更新が止まる」というサイレント故障になる**(土地区画の一括作成プレビューは数百区画ぶんのポリゴンを載せるため、旧8KBでは広い範囲を指定すると必ず溢れていた)。大きくなり得るペイロードを送る箇所は送信失敗をユーザーへ通知すること。
- ゲーム開始時のカメラ位置はUnityが `GET /api/v1/city` の `camera` から復元する。`game:start` のparamsは `loading_id` のみ。`awaiting_parameter_setup` はTips終了後の初期設定待ちを示す。
- 地形エディタは `copy_terrain_for_editor` / `create_new_terrain_for_editor` で毎回初期データを作り、`load_terrain_editor_data` で取り込む。AppData/UrbanGenesis/terrain_editor/{editor_id}/ の meta.json / near.bin はウィンドウ間の初期データ受け渡し専用。編集途中の保存・一覧・再開・削除コマンドは持たない。編集結果は `export_terrain_as_npy` でゲーム用マップへ書き出す。

- `export_terrain_as_npy` は編集中の標高データを `.npy` として書き出す。**変換はRust側の `build_npy_float16` が自前で行い、Pythonなどの外部プロセスには一切依存しない**(以前は同梱のPythonスクリプトを`python`コマンドで実行していたが、Windowsの`python`がMicrosoft Storeエイリアスに化けている環境や numpy 未導入の環境で失敗するため廃止した)。フロントから届く標高データは既に `.npy` のデータ部と同じ float16 リトルエンディアンの生バイト列なので、`{'descr': '<f2', 'fortran_order': False, 'shape': (rows, cols), }` のヘッダ(データ部が64バイト境界から始まるよう空白で詰める。numpy自身の書き出しとバイト単位で一致する)を被せるだけでよい。負の標高(海底)もそのまま保持され、バックエンド `terrain_registry.build_near_map` が `np.load` → float32 キャストで読む。出力先パスはフロント側で `@tauri-apps/plugin-dialog` の保存ダイアログにより選択させる。変換成功後、出力先(`out_path`)と同じディレクトリに `terrain_maps.yaml` があれば、`upsert_terrain_yaml_entry` がそのマップ(引数`map_id`)のエントリをテキストベースで追記/上書きする(同じ`id`が既にあれば上書き)。戻り値の`yaml_updated`(bool)で見つかったかどうかをフロントに伝え、`terrain_maps.yaml`が見つからなかった場合はフロント側(`TerrainEditorCanvas.tsx`)が`showNotification`で通知する。`upsert_terrain_yaml_entry`はserde_yamlでパース→再シリアライズする方式ではなく、行単位のテキスト置換で対象ブロック(`{map_id}:`行と、直前の連続するコメント行〜次のトップレベルキーの手前まで)だけを差し替えるため、既存の他エントリのコメントや空行区切りを保持する。
- サムネイル画像(160x120 PNG)は `export_terrain_as_npy` の `thumbnail_base64` で受け取り、`.npy` と同名の `.png` へ書く。生成は `terrainThumbnail.ts` の `captureTerrainThumbnail` が行う。
- 水面は `export_terrain_as_npy` の `water_surfaces` を `.water.json`（version 1）へ書く。空でも書き出して古い水面を残さない。取り込み時は既存マップの `/features` から meta.json へ持ち込む。仕様は `city-genesis/docs/docs/geometries/D_地形形状.md §D.10`。
- カメラ初期状態は `export_terrain_as_npy` の `camera` を `terrain_maps.yaml` のエントリへ書く。既存マップの再編集では `/features` から取り込む。未設定は null。仕様は `city-genesis/docs/docs/geometries/D_地形形状.md §D.13`。
- 川は `export_terrain_as_npy` の `rivers`（id/kind/polygon/profile）を `.rivers.json`（version 2）へ書く。川床は標高へ焼き込む。既存マップの再編集では `/features` から取り込む。仕様は `city-genesis/docs/docs/geometries/D_地形形状.md §D.11`。
- 天然資源は `export_terrain_as_npy` の `resources` を `.resources.json`（version 1）へ書く。取り込みは `/resources` からID・GeoJSONをそのまま保持する。ゲームでは `init_game` が資源エンティティを生成する。仕様は `city-genesis/docs/docs/geometries/D_地形形状.md §D.12`。

- `ipc.rs`は建物の`edit_building:placement_state`をパネルへ中継する。配置方法・中心・寸法・角度・世代を同じpayloadで返すため、個別イベントに分解しない。
- `ipc.rs`はプロップの`edit_prop:placement_settings`を全ウィンドウへ、ブルドーザの`remove_object:request_confirm`は`emit_to(LABEL_COCKPIT)`でコックピットだけへ中継する(確認ダイアログを出すのは常駐1枚のコックピットに限るため。`weather:state`と同じ宛先指定)。回答の`remove_object:confirm_result`はUI→Unityの汎用転送で足りる。

- 地形エディタのプロップは取り込み時に `/props` から取得し、エクスポート時に `.props.json`（version 1、props.instances/scatters）へ空配列も含めて書く。ゲーム中のプロップCRUDとは独立している。

- 基盤マップからの資源取り込みは `copy_terrain_for_editor` が `GET /terrain/maps/{map_id}/resources` を取得し、`terrain_editor_resources` がresources配列（またはルート配列）を検証してmeta.jsonへ保存する。取得・JSON解析・外形検証の失敗時は作業コピーを書き換える前に中断する。資源IDとGeoJSON形状は加工しない。

## 地形原本の保護とエクスポート先

`copy_terrain_for_editor` の `original=true` はバックエンドの標高原本を取得し、川・水面・資源・プロップ・カメラの配置を含まない新規IDの作業コピーを作る。戻り値は取り込みごとに作る editor_id で、UIはこのIDを開く。meta.json の map_id はエクスポート用IDで、既存マップは元のIDを保持する。原本からは新しいマップIDを作る。既存マップは /features から川・水面・カメラも取り込み、原本は配置情報を一切取得しない。

ゲーム用地形と `terrain_maps.yaml` は `genesis-core/assets/terrains/` にある。`export_terrain_as_npy` は `terrain_export_paths.rs` で出力先を検証し、`terrains/original/` 配下を拒否する。親ディレクトリ・既存ファイルのリンク先も検証し、付随JSONを含め最初の書き込みより前に止める。登録表の `file_name` は assets 基準なので、terrains への出力は `terrains/ファイル名.npy` として登録する。原本の寸法・解像度は別ファイル `original_terrains.yaml` にあり、エクスポートで変更しない。

- 任意の50m背景地形参照 `background` は原本・既存マップの `/features` からmeta.jsonへ取り込み、TerrainEditorDataとexport_terrain_as_npyを通じて登録表へ保持する。背景ファイル自体を編集・複製しない。原本のfeaturesは `original=true` を指定し、背景以外の配置情報は空となる。

### 初期ロードとTips

初期ロードは `tips` ウィンドウに統合する。バックエンドへの開始要求中からTipsと回転アイコン付きの `Now Loading...` を表示し、文字は20px・太字で左から右へ光が移動する。動きを減らす設定ではアニメーションを停止する。

`begin_initial_loading` は要求ごとの `loading_id` と、空の都市を新規作成したかを保持する。POST成功後に `session:reload` と `game:start {loading_id}` を送り、同じセーブIDを再ロードした場合も初期描画を開始する。Unityの `game:loaded {loading_id}` が現在の世代と一致したら、Tipsを「ゲームを開始する」ボタンへ切り替える。TipsはRustの保持状態を問い合わせるので、WebView起動前の完了通知も取りこぼさない。

開始ボタンは `game:reveal {loading_id}` で黒画面を解除する。通常ロード・シナリオ・チュートリアル・テンプレート編集では初期設定を自動表示しない。空の都市を新規作成した場合だけTipsの次に `ParameterSetupWindow` を表示し、設定完了までHUDを抑制する。設定完了時に都市を再ロードしない。テンプレート編集から手動で開くパラメータ編集は従来どおり使用できる。

HUDとヒントバーは `current_save_id != null && !isLoading` を表示条件とする。`isLoading` は描画準備・開始ボタン待ち・初期設定中を含む。TipsのAlt+F4では待機を解除しない。開始APIの失敗時はTipsを破棄し、ロード状態を解除する。


開発時のUnityなし起動: 通常の `npm run tauri dev`（Rustの `cfg(all(debug_assertions, dev))`）では、Unityが接続中でない場合に限り、バックエンドの開始要求成功とセッション確認後に描画待ちを省略し、Tipsを開始ボタンへ切り替える。接続中かどうかは `graphic_connected`（`system:graphic_ready` で立ち、`system:graphic_stopped`＝正常終了・EditorのPlay停止で下りる）で判定し、再接続時のポップアップ一掃に使う `graphic_ready_seen`（一度立ったら戻さない）とは別に持つ。「一度でも接続したか」で判定するとPlayを止めた後の再ロードで永遠に待つ。接続中は描画完了を待ち、待機中にUnityが停止した場合はその時点で開始ボタンへ切り替える。待機中にUnityが接続した場合は開始要求を再送して描画待ちに戻る。`tauri build`（`--debug` を含む）およびリリースビルドには省略処理を含めない。環境変数やバックエンドのNuitka／uvicorn判定は使用しない。
