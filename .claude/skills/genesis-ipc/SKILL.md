---
name: genesis-ipc
description: City Genesisのモジュール間通信(HTTP API、MemoryMappedFileによるUnity⇔UI連携)を調査・実装・修正するときに使う。「Unityとの通信イベントを追加したい」「Tauri側のipc.rsを触りたい」「Envelope構造を変えたい」「新しいAPIエンドポイントを追加したい」といった話のときは必ずこのSkillを参照し、読むべきドキュメントと編集対象ディレクトリを確認すること。
---

> **パス表記の基準**: 本文中のパスはワークディレクトリ `S:/city/genesis`(`city-genesis` と `genesis-core` を並べた作業ツリー)を起点とする。`city-genesis` 単体で作業している場合は先頭の `city-genesis/` を読み替えること。`genesis-core/` 配下は非公開のコアシミュレータなので、手元に無ければその項目は読み飛ばしてよい。

このSkillはコード本体を持たない「道しるべ」です。実際の仕様・ロジックは以下のドキュメントに書かれているので、実装前に必ず読むこと。

## 読むべきドキュメント(概念、公開)
- `city-genesis/docs/docs/programs/IPC連携の整理.md`
- `city-genesis/docs/docs/programs/コンポーネント連携.md`(特に「ローカルAPIへの接続」: CORS・各クライアントの窓口、CSP)
- `city-genesis/docs/docs/programs/バージョニングと互換性.md`(コアとフロントのMAJOR規則・IPCの契約番号)
- `city-genesis/docs/docs/programs/ファイル配置.md`

## 読むべきドキュメント(非公開)
- 該当なし(IPCはUnity/UI側の契約であり、コアシミュレータの内部処理ではないため`genesis-core/docs`に対応物はない)

## 読むべきAGENTS.md
- ルート `S:/city/genesis/AGENTS.md` (通信経路の全体表)
- `city-genesis/unity/Assets/Scripts/IPC/AGENTS.md` (Envelope構造の一致要件と、揃えないと無言で壊れる契約。イベントを追加・変更するときの変更箇所は `IPC連携の整理.md` の同名の節)
- `city-genesis/tauri/src-tauri/AGENTS.md` (`ipc.rs`, `messages.rs`)
- `genesis-core/app/controllers/AGENTS.md` (HTTPエンドポイント一覧)

## 編集対象になりやすいディレクトリ/ファイル
- Unity(MMF): `city-genesis/unity/Assets/Scripts/IPC/IpcClient.cs`, `IpcTransport.cs`, `IpcEnvelope.cs`, `IpcEventHandler.cs`, `IpcStateCoordinator.cs`, `IpcViewCoordinator.cs`
- UI(MMF): `city-genesis/tauri/src-tauri/src/ipc.rs`, `messages.rs`, `city-genesis/tauri/src/utils/ipc.ts`
- 編集ステート(`STATE_EDIT_*`)に入るウィンドウを足すとき: `city-genesis/tauri/src-tauri/src/window.rs` の `EDIT_MODE_WINDOW_TYPES`(閉じたときに Rust が `STATE_NORMAL` を送り、開いたときにブルドーザ・地区編集を解除する)と、`city-genesis/tauri/src-tauri/config/window-mutex-groups.json`(一覧の全種別と双方向に排他にする。`window.rs` のテストが検査する)。確定後の `edit_*:finish` とウィンドウを閉じたときの `STATE_NORMAL` の関係は `IPC連携の整理.md` の「確定送信の責務分担」
- HTTP: `genesis-core/app/controllers/`配下、Unity側`city-genesis/unity/Assets/Scripts/API/ApiClient.cs`(`GeneratedModels.cs`は自動生成のため直接編集禁止)、UI側`city-genesis/tauri/src/utils/api.ts`(WebView)と`city-genesis/tauri/src-tauri/src/version.rs`(Rust)

## HTTPの契約(新しいクライアント・HTTP呼び出しを足すときに必ず守る)
- APIに認証・Host検査は無く、サードパーティ製プログラムからも同じAPIで読み書きできることが仕様なので、アクセス制限を足さない。コアは `127.0.0.1` にだけ bind する。
- ゲームのセッションが無い間(ゲーム開始前・シナリオの地形編集などのヘッドレス処理中)に通るのは system・sessions・saves・scenarios・parameters・templates・master と `/api/v1/terrain/maps` だけで、ほかは503 `NO_SAVE_LOADED`。トップメニューやシナリオ管理から呼ぶ新しいAPIは、この範囲に置くか、コアの `api.py` の許可一覧を見直す(契約は `コンポーネント連携.md` の「セッション状態の管理」)。
- ヘッダーは各モジュールの共通窓口が付けるので、窓口を通すことだけを守る。
  - UI(WebView): `fetchApi` か、`configureBackendRequests` が置き換えた `fetch`。APIの画像は `<img src>` で直接読まず `hooks/useApiImageUrl`(fetch→Blob URL。版ヘッダーを付けるため)。コアの版情報と照合結果は起動時にRust側で取得・共有し、WebViewごとの版取得や定期的な版確認は行わない。
  - UI(Rust): `version::client()` か、要求ごとの `.headers(version::headers())`。
  - Unity: `ApiClient`(`Get`・`GetBulk`・`Post`・`Patch`・`Request`→`ApiRequest`)。`UnityWebRequest` を直接作らない。
- コアとフロントの接続可否は、フロントのMAJORがコアのMAJOR以上であることだけで決まる(満たさなければ起動しない)。IPC契約を壊す変更(項目の削除・型や意味の変更・相手の実装を必須にする追加)では、`city-genesis/version.json` の `ipc_contract_version` を上げ、`tauri/` で `npm run sync:version` を実行する。

## 進め方
1. 公開ドキュメントで通信経路全体(HTTP/MMFのどちらを使うべきか)を把握する
2. Envelope構造は`IPC/AGENTS.md`、イベント追加時の変更箇所は`IPC連携の整理.md`を必ず確認する(UnityとUI/バックエンドで構造が一致していないと通信が壊れるため)
3. 実装ファイルを編集する(MMFの場合は送信側・受信側の両方を変更する)
4. 仕様に影響する変更をしたら、対応するドキュメントの更新が必要になる旨をユーザーに伝える(このSkill自身はドキュメントを編集しない)
