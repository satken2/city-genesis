---
name: genesis-input-mode
description: City GenesisのUnity入力モード(InputMode: クリック/ドラッグ判定、建物/区画/パス/地形の編集ハンドラ)を調査・実装・修正するときに使う。「クリックとドラッグの判定閾値を変えたい」「新しい入力ハンドラを追加したい」「道路建設モードの入力処理」「海面クリックの特殊扱い」といった話のときは必ずこのSkillを参照し、読むべきドキュメントと編集対象ディレクトリを確認すること。ヒートマップ等の見た目切り替え(VisualMode)側の話は genesis-visual-mode を使うこと。InputModeとVisualModeは別系統なので混同しないこと。
---

> **パス表記の基準**: 本文中のパスはワークディレクトリ `S:/city/genesis`(`city-genesis` と `genesis-core` を並べた作業ツリー)を起点とする。`city-genesis` 単体で作業している場合は先頭の `city-genesis/` を読み替えること。`genesis-core/` 配下は非公開のコアシミュレータなので、手元に無ければその項目は読み飛ばしてよい。

このSkillはコード本体を持たない「道しるべ」です。実際の仕様・ロジックは以下のドキュメントに書かれているので、実装前に必ず読むこと。

## 読むべきドキュメント(概念、公開)
- `city-genesis/docs/docs/programs/Unity入力制御.md`

## 読むべきドキュメント(非公開)
- 該当なし

## 読むべきAGENTS.md
- `city-genesis/unity/Assets/Scripts/InputHandlers/AGENTS.md` (最重要。基本フロー、クリック/ドラッグ判定の閾値ロジック、各ハンドラ一覧、`InputMode`定数、新規ハンドラ実装テンプレート、海面クリックの特殊扱い)
- `city-genesis/unity/Assets/Scripts/Editors/AGENTS.md` (入力モードから編集フローへの委譲の流れ)
- `city-genesis/unity/AGENTS.md` (「モード系は2系統ある」の節でVisualModeとの区別を明記)

## 編集対象になりやすいディレクトリ/ファイル
- `city-genesis/unity/Assets/Scripts/InputHandlers/InputHandler.cs`, `BuildingInputHandler.cs`, `ParcelInputHandler.cs`, `PathInputHandler.cs`, `NodeInputHandler.cs`, `TerrainInputHandler.cs`, `ZoneInputHander.cs`
- 関連(編集フローへの委譲先): `city-genesis/unity/Assets/Scripts/Editors/`配下(`RoadEditor/RoadPlanManager.cs`, `BuildingEditor/BuildingPlanManager.cs`, `ParcelManager/ParcelEditorManager.cs`)

## 進め方
1. `InputHandlers/AGENTS.md`で新規ハンドラ実装テンプレートとクリック/ドラッグ判定ロジックを把握する(既存パターンを踏襲する)
2. `Editors/AGENTS.md`で入力モードから編集フローへの委譲の流れを確認する
3. 実装ファイルを編集する
4. 仕様に影響する変更をしたら、対応するドキュメントの更新が必要になる旨をユーザーに伝える(このSkill自身はドキュメントを編集しない)
