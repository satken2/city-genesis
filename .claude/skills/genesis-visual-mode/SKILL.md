---
name: genesis-visual-mode
description: City GenesisのUnity表示モード(VisualMode: ヒートマップ表示、地区色分け、建物/区画/パスの見た目切り替え)を調査・実装・修正するときに使う。「VisualModeHandlerを触りたい」「ヒートマップの色分けを変えたい」「新しい表示モードを追加したい」といった話のときは必ずこのSkillを参照し、読むべきドキュメントと編集対象ディレクトリを確認すること。クリック等の入力操作(InputMode)側の話は genesis-input-mode を使うこと。VisualModeとInputModeは別系統なので混同しないこと。
---

> **パス表記の基準**: 本文中のパスはワークディレクトリ `S:/city/genesis`(`city-genesis` と `genesis-core` を並べた作業ツリー)を起点とする。`city-genesis` 単体で作業している場合は先頭の `city-genesis/` を読み替えること。`genesis-core/` 配下は非公開のコアシミュレータなので、手元に無ければその項目は読み飛ばしてよい。

このSkillはコード本体を持たない「道しるべ」です。実際の仕様・ロジックは以下のドキュメントに書かれているので、実装前に必ず読むこと。

## 読むべきドキュメント
- city-genesis/docs側に専用の対応ドキュメントが見当たらない(調査時点で未整備の可能性がある)。まずAGENTS.mdを読み、実装から仕様を把握すること。もしドキュメントの不在に気づいたらAGENTS.mdの運用ルールに従って追記を検討する(ただしこのSkill自身は編集しない)。

## 読むべきAGENTS.md
- `city-genesis/unity/Assets/Scripts/VisualHandlers/AGENTS.md` (最重要。`VisualModeHandler`、`IVisualModeHandler`の2実装パターン、`VisualModeObject`のステータス連動、モード定数`VisualMode`/`VisualModeEx`の値域分類、地区色分けの仕組み)
- `city-genesis/unity/AGENTS.md` (「モード系は2系統ある」の節でVisualModeとInputModeの区別を明記)
- UI側の連動: `city-genesis/tauri/src/components/AGENTS.md` (`components/display/`配下、ヒートマップ切替の操作)

## 編集対象になりやすいディレクトリ/ファイル
- `city-genesis/unity/Assets/Scripts/VisualHandlers/VisualModeHandler.cs`, `VisualModeObject.cs`, `VisualModeEx.cs`, `VisualBuildingObject.cs`, `VisualParcelObject.cs`
- 各種Stats(`VisualPathStats`/`VisualBuildingStats`/`VisualParcelStats`)は各Managerが生成
- UI側: `city-genesis/tauri/src/components/display/`配下

## 進め方
1. `VisualHandlers/AGENTS.md`で`IVisualModeHandler`パターンとモード定数の値域を把握する(新モード追加時は既存パターンに沿わせる)
2. ルートAGENTS.mdでInputModeとの違いを再確認する(取り違えるとバグの元)
3. 実装ファイルを編集する
4. 仕様に影響する変更をしたら、対応するドキュメント(不足していれば新設も含め)の更新が必要になる旨をユーザーに伝える(このSkill自身はドキュメントを編集しない)
