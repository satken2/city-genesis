---
name: genesis-visual-mode
description: City GenesisのUnity表示モード(VisualMode: ヒートマップ表示、地区色分け、建物/区画/パスの見た目切り替え)を調査・実装・修正するときに使う。「VisualModeHandlerを触りたい」「ヒートマップの色分けを変えたい」「新しい表示モードを追加したい」といった話のときは必ずこのSkillを参照し、読むべきドキュメントと編集対象ディレクトリを確認すること。クリック等の入力操作(InputMode)側の話は genesis-input-mode を使うこと。VisualModeとInputModeは別系統なので混同しないこと。
---

> **パス表記の基準**: 本文中のパスはワークディレクトリ `S:/city/genesis`(`city-genesis` と `genesis-core` を並べた作業ツリー)を起点とする。`city-genesis` 単体で作業している場合は先頭の `city-genesis/` を読み替えること。`genesis-core/` 配下は非公開のコアシミュレータなので、手元に無ければその項目は読み飛ばしてよい。

このSkillはコード本体を持たない「道しるべ」です。全体の考え方は以下のドキュメントに、個別の仕様と意図はソースのコメントに書かれているので、実装前に必ず読むこと。

## 読むべきドキュメント(概念、公開)
- `city-genesis/docs/docs/programs/Unity表示モード.md` (最重要。レイヤー・色モード・表示フラグの考え方、個体の見え方の優先順位、編集ステートの表示の記憶と復帰。モード定数の値域や個別のモードの色分けは `VisualHandlers/` のコードのコメント)
- `city-genesis/docs/docs/programs/Unity入力制御.md` (別系統である入力モードとの関係)

## 読むべきAGENTS.md
- `city-genesis/unity/Assets/Scripts/VisualHandlers/AGENTS.md` (ファイル一覧と、知らないと事故る規約: 色モード×フラグの直交と一括通知、マテリアルの初期化と更新、一時色の明示復帰など)
- `city-genesis/unity/AGENTS.md` (「モード系は2系統ある」の節でVisualModeとInputModeの区別を明記)
- UI側の連動: `city-genesis/tauri/src/components/AGENTS.md` (`components/display/`配下、ヒートマップ切替の操作)

## 編集対象になりやすいディレクトリ/ファイル
- `city-genesis/unity/Assets/Scripts/VisualHandlers/VisualModeHandler.cs`, `VisualModeObject.cs`, `VisualModeEx.cs`, `VisualBuildingObject.cs`, `VisualParcelObject.cs`
- 各種Stats(`VisualPathStats`/`VisualBuildingStats`/`VisualParcelStats`)は各Managerが生成
- UI側: `city-genesis/tauri/src/components/display/`配下

## 進め方
1. `Unity表示モード.md`で全体像を、`VisualHandlers/` のコードで`IVisualModeHandler`パターンとモード定数の値域を把握し、`VisualHandlers/AGENTS.md`で規約を確認する(新モード追加時は既存パターンに沿わせる)
2. `unity/AGENTS.md`の「モード系は2系統」でInputModeとの違いを再確認する(取り違えるとバグの元)
3. 実装ファイルを編集する
4. 個別の仕様はソースのコメントに書く。全体の考え方やモジュール間の契約が変わる変更をしたときだけ、ドキュメントの更新が必要になる旨をユーザーに伝える(このSkill自身はドキュメントを編集しない)
