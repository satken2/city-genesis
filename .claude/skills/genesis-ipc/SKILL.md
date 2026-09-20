---
name: genesis-ipc
description: City Genesisのモジュール間通信(HTTP API、MemoryMappedFileによるUnity⇔UI連携)を調査・実装・修正するときに使う。「Unityとの通信イベントを追加したい」「Tauri側のipc.rsを触りたい」「Envelope構造を変えたい」「新しいAPIエンドポイントを追加したい」といった話のときは必ずこのSkillを参照し、読むべきドキュメントと編集対象ディレクトリを確認すること。
---

> **パス表記の基準**: 本文中のパスはワークディレクトリ `S:/city/genesis`(`city-genesis` と `genesis-core` を並べた作業ツリー)を起点とする。`city-genesis` 単体で作業している場合は先頭の `city-genesis/` を読み替えること。`genesis-core/` 配下は非公開のコアシミュレータなので、手元に無ければその項目は読み飛ばしてよい。

このSkillはコード本体を持たない「道しるべ」です。実際の仕様・ロジックは以下のドキュメントに書かれているので、実装前に必ず読むこと。

## 読むべきドキュメント(概念、公開)
- `city-genesis/docs/docs/programs/IPC連携の整理.md`
- `city-genesis/docs/docs/programs/コンポーネント連携.md`
- `city-genesis/docs/docs/programs/ファイル配置.md`

## 読むべきドキュメント(非公開)
- 該当なし(IPCはUnity/UI側の契約であり、コアシミュレータの内部処理ではないため`genesis-core/docs`に対応物はない)

## 読むべきAGENTS.md
- ルート `S:/city/genesis/AGENTS.md` (通信経路の全体表)
- `city-genesis/unity/Assets/Scripts/IPC/AGENTS.md` (MMF通信の詳細、Envelope構造の一致要件、新規イベント追加手順)
- `city-genesis/tauri/src-tauri/AGENTS.md` (`ipc.rs`, `messages.rs`)
- `genesis-core/app/controllers/AGENTS.md` (HTTPエンドポイント一覧)

## 編集対象になりやすいディレクトリ/ファイル
- Unity(MMF): `city-genesis/unity/Assets/Scripts/IPC/IpcClient.cs`, `IpcTransport.cs`, `IpcEnvelope.cs`, `IpcEventHandler.cs`, `IpcStateCoordinator.cs`, `IpcViewCoordinator.cs`
- UI(MMF): `city-genesis/tauri/src-tauri/src/ipc.rs`, `messages.rs`, `city-genesis/tauri/src/utils/ipc.ts`
- HTTP: `genesis-core/app/controllers/`配下、Unity側`city-genesis/unity/Assets/Scripts/API/ApiClient.cs`(`GeneratedModels.cs`は自動生成のため直接編集禁止)、UI側`city-genesis/tauri/src/utils/api.ts`

## 進め方
1. 公開ドキュメントで通信経路全体(HTTP/MMFのどちらを使うべきか)を把握する
2. Envelope構造やイベント追加手順は`IPC/AGENTS.md`を必ず確認する(UnityとUI/バックエンドで構造が一致していないと通信が壊れるため)
3. 実装ファイルを編集する(MMFの場合は送信側・受信側の両方を変更する)
4. 仕様に影響する変更をしたら、対応するドキュメントの更新が必要になる旨をユーザーに伝える(このSkill自身はドキュメントを編集しない)
