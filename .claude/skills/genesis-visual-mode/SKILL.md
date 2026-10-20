---
name: genesis-visual-mode
description: "City GenesisのUnity表示モード(VisualMode: ヒートマップ表示、地区色分け、建物/区画/パスの見た目切り替え)を調査・実装・修正するときに使う。「VisualModeHandlerを触りたい」「ヒートマップの色分けを変えたい」「新しい表示モードを追加したい」といった話のときは必ずこのSkillを参照し、読むべきドキュメントと編集対象ディレクトリを確認すること。クリック等の入力操作(InputMode)側の話は genesis-input-mode を使うこと。VisualModeとInputModeは別系統なので混同しないこと。"
---

> **パス表記の基準**: 公開側のパスは `city-genesis` リポジトリのルート起点。`genesis-core/`・`genesis-gallery/` は別の非公開リポジトリのルートを示し、並置を前提としない。利用できる場合だけ実際の配置に読み替え、手元に無い場合は公開側で確認できる範囲を扱う。

このSkillはコード本体を持たない「道しるべ」です。全体の考え方は以下のドキュメントに、個別の仕様と意図はソースのコメントに書かれているので、実装前に必ず読むこと。

## 読むべきドキュメント(概念、公開)
- `docs/ja/programs/Unity表示モード.md` (レイヤー・色モード・表示フラグ・個体のフィルタと、通常表示・一時表示の考え方。見え方の優先順位、モード定数の値域、個別の色分けは `VisualHandlers/` のコードのコメント)
- `docs/ja/programs/Unity入力制御.md` (別系統である入力モードとの関係)

## 読むべきAGENTS.md
- `unity/Assets/Scripts/VisualHandlers/AGENTS.md` (ファイル一覧と、知らないと事故る規約: 色モード×フラグの直交と一括通知、マテリアルの初期化と更新、一時色の明示復帰など)
- `unity/AGENTS.md` (「モード系は2系統ある」の節でVisualModeとInputModeの区別を明記)
- UI側の連動: `tauri/src/components/AGENTS.md` (`components/display/`配下、ヒートマップ切替の操作)

## 編集対象になりやすいディレクトリ/ファイル
- `unity/Assets/Scripts/VisualHandlers/VisualModeHandler.cs`, `VisualModeObject.cs`, `VisualModeEx.cs`, `VisualBuildingObject.cs`, `VisualParcelObject.cs`
- 各種Stats(`VisualPathStats`/`VisualBuildingStats`/`VisualParcelStats`)は各Managerが生成
- UI側: `tauri/src/components/display/`配下

## 進め方
1. `Unity表示モード.md`で全体像を、`VisualHandlers/` のコードで`IVisualModeHandler`パターンとモード定数の値域を把握し、`VisualHandlers/AGENTS.md`で規約を確認する(新モード追加時は既存パターンに沿わせる)
2. `unity/AGENTS.md`の「モード系は2系統」でInputModeとの違いを再確認する(取り違えるとバグの元)
3. 実装ファイルを編集する
4. 個別の仕様はソースのコメントに書く。全体の考え方やモジュール間の契約が変わる変更をしたときだけ、ドキュメントの更新が必要になる旨をユーザーに伝える(このSkill自身はドキュメントを編集しない)
