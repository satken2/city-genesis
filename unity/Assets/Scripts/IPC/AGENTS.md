# IPC フォルダ

Tauri製UI(`city-genesis/tauri`)とのMemoryMappedFile(MMF)通信を担当する。バックエンドとのHTTP(`API/`)とは別系統で、UnityとUIの間のやり取り(選択したオブジェクトの詳細を開く、編集モードや表示モードの切り替え等)はすべてここを通る。

プロトコルの正(MMFレイアウト・イベント一覧・ステート表・起動フロー・機能ごとの契約)は公開docs `docs/docs/programs/IPC連携の整理.md`。このファイルにはUnity実装側の不変条件と罠だけを書く。

## ファイル一覧

| ファイル | 内容 |
| --- | --- |
| `IpcClient.cs` | シングルトンの高レベルクライアント。型付きの`On*`イベントと`Emit*`送信API。受信はMMF読み取りスレッドから`Update()`でメインスレッドへディスパッチされるので、購読ハンドラからUnity APIを直接呼べる |
| `IpcTransport.cs` | `MmfReader`/`MmfWriter`。リングバッファの低レベル読み書き |
| `IpcEnvelope.cs` | メッセージの封筒。tauri側`src-tauri/src/messages.rs`の`Envelope`と同一構造 |
| `IpcEventHandler.cs` | 受信イベントを各Manager(RoadPlanManager等)へ橋渡しする |
| `IpcStateCoordinator.cs` | `world:change_state`。`STATE_*`ごとにInputModeプリセットと表示プリセットをまとめて切り替える |
| `IpcViewCoordinator.cs` | `view:*`。レイヤーごとの表示状態(mode/sub_mode/flags)の記憶と復元 |
| `IpcInputDebugCoordinator.cs` | `input:query_modes`(デバッグ用) |
| `HighlightManager.cs` | `world:highlight_objects`の干渉表示(区画の塗り・建物/パス/川の占有箱)。発信元(source)ごとに独立レイヤー |
| `PathInterferencePayload.cs` | `world:highlight_objects`のペイロード型 |

## 契約(揃えないと無言で壊れる)

- Envelope(`version`/`request_id`/`timestamp`/`source`/`destination`/`event`/`payload`)は`IpcEnvelope.cs`と`messages.rs`で同一に保つ。パースできない通知は両側とも黙って捨てるので、ズレは「届かない」としか見えない。
- UI→Unityは全て`ui:action` Envelopeの`payload.action`/`payload.params`に乗る(Rustは汎用転送で専用分岐不要)。Unity→UIは`event`直下の名前で送り、Rust `ipc.rs`の受信matchに中継分岐が無いとどのWebViewにも届かない。変更箇所の一覧は上記docsの「イベントを追加・変更するときの変更箇所」。
- MMFの容量`CAPACITY`(1MiB)は`IpcTransport.cs`と`ipc.rs`で必ず同値にする。両側`CreateOrOpen`なので先に起動した側のサイズが採用され、食い違うと片側だけが範囲外を読む。1通が収まらないと送信側だけが失敗し、受信側には何も起きない(「UIは動くがUnityだけ更新されない」)。
- 周期的に送り続けるメッセージを置かない。Unity不在中もUI→Unityの送信はMMFに溜まり、満杯になると以後の送信が`MMF buffer full`で失敗し続ける。再同期は`system:graphic_ready`起点か、Unityが起動時に自分から状態を送る(`weather:state`等)。カメラ等の連続値は問い合わせ方式(`camera:query_state`)。
- 用語: Envelope上Unityは"parent"。Rustの`send_to_parent`はUnity宛て送信のこと。

## 実装側の規約

- 表示状態の正は`IpcViewCoordinator`のレイヤー記憶(`_layerModes`/`_layerSubModes`/`_layerFlags`)だけ。編集ステート中の表示も`SyncFromState`のプリセットが`ForceLayer`で適用するので、`IpcStateCoordinator.ApplyState*`から`VisualModeHandler`を直接触らない(UIへ通知する状態と実際の見え方がズレる)。
- プリセットで強制するレイヤーは`NORMAL_DEFAULTS`にも載せる(無いと`STATE_NORMAL`復帰で編集中の表示が残る)。`view:change_view`の`flags`はnull=維持・空文字=全OFF。
- Unity→UIの状態通知(`*:placement_state`・`edit_path:update_shape`・`edit_prop:placement_settings`等)は「コマンド入力・全状態出力」。UIは受信値をエコーしない前提なので、Unity側でも受信をトリガに同じ値を送り返さない(往復ループ)。
- 確定後の後始末は`edit_*:finish`系の1イベントにまとめ、順序は`IpcEventHandler`/`IpcStateCoordinator`のハンドラが持つ。UIに`session:reload`と`world:change_state`を別々に送らせない: 差分リロードは複数フレームのコルーチンで、後続の状態遷移が割り込むと再生成・削除が中途半端に終わる。
- 特定ウィンドウ由来のモードの打ち切りは対象IDを持つ専用イベント(`edit_path:cancel_topology_edit`・`edit_road:cancel {road_id}`・`edit_zone:finish_merge`)で受ける。素の`STATE_NORMAL`はブルドーザ等ウィンドウを持たないモードや別の道路の編集まで巻き込む。
- ポップアップは閉じても非表示になるだけでReactのアンマウントが走らないため、上記の打ち切りはRust `window.rs`(`close_window_by_label`)が送る。新しい編集モードには`EDIT_MODE_WINDOW_TYPES`か専用cancelのどちらかで必ず戻り道を用意する(無いと3Dビューの入力が固まる)。
- 起動直後に1回だけ届く設定通知(`ambience:volume`)は`IpcClient`が最新値を保持し、購読時に即時再通知する。購読前の1通を取りこぼすと次の変更まで反映されないため、同種の通知を足すときも同じ形にする。
- 曲線/直線・吸着の正はUnity(`Editors/RoadEditor/RoadEditControlMenu`のCtrlラジアルメニュー)で、UIへは`edit_path:update_shape`の`is_curved`で逆流する。UI側にトグルを足して正を二重にしない。
- 詳細パネルを操作し続けるステート(`STATE_MERGE_PATH`/`STATE_SPLIT_PATH`/`STATE_EDIT_CONNECTOR`/`STATE_EDIT_ROAD_MEMBERS`)では`WindowUtil.BringToForeground()`を呼ばない。

## 参照

- docs: `programs/IPC連携の整理.md`(プロトコルの正)、`programs/コンポーネント連携.md`(起動順序・セッション・UIのウィンドウ管理)、`programs/Unity入力制御.md`、機能別ページ(`経路提案.md`・`道路の詳細と道路名.md`・`建設プロジェクト管理.md`・`公共交通ハイライト.md`)
- Skill: `genesis-ipc`(イベント追加の進め方)、`genesis-input-mode`/`genesis-visual-mode`(InputMode/VisualMode)
- Rust側の契約: `city-genesis/tauri/src-tauri/AGENTS.md`
