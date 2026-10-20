---
name: genesis-input-mode
description: "City GenesisのUnity入力モード(InputMode: クリック/ドラッグ判定、建物/区画/パス/地形の編集ハンドラ)を調査・実装・修正するときに使う。「クリックとドラッグの判定閾値を変えたい」「新しい入力ハンドラを追加したい」「道路建設モードの入力処理」「海面クリックの特殊扱い」といった話のときは必ずこのSkillを参照し、読むべきドキュメントと編集対象ディレクトリを確認すること。ヒートマップ等の見た目切り替え(VisualMode)側の話は genesis-visual-mode を使うこと。InputModeとVisualModeは別系統なので混同しないこと。"
---

> **パス表記の基準**: 公開側のパスは `city-genesis` リポジトリのルート起点。`genesis-core/`・`genesis-gallery/` は別の非公開リポジトリのルートを示し、並置を前提としない。利用できる場合だけ実際の配置に読み替え、手元に無い場合は公開側で確認できる範囲を扱う。

このSkillはコード本体を持たない「道しるべ」です。全体の考え方は以下のドキュメントに、個別の仕様と意図はソースのコメントに書かれているので、実装前に必ず読むこと。

## 読むべきドキュメント(概念、公開)
- `docs/ja/programs/Unity入力制御.md`

## 読むべきドキュメント(非公開)
- 該当なし

## 読むべきAGENTS.md
- `unity/Assets/Scripts/InputHandlers/AGENTS.md` (最重要。ハンドラ一覧、クリック/ドラッグ判定と入力ゲートの罠、海面クリックの特殊扱い、背景地形・埋立地の岸壁を地形として扱う判定。`InputMode`定数の値域とプリセットは `InputHandlers/InputHandler.cs` の定義が正で、新しいモード・プリセットの値はそこで空きを確かめる)
- `unity/Assets/Scripts/Editors/AGENTS.md` (入力モードから編集フローへの委譲の流れ)
- `unity/AGENTS.md` (「モード系は2系統ある」の節でVisualModeとの区別を明記)

## 編集対象になりやすいディレクトリ/ファイル
- `unity/Assets/Scripts/InputHandlers/InputSystemManager.cs`, `IInputHandler.cs`, `InputHandler.cs`, `BuildingInputHandler.cs`, `ParcelInputHandler.cs`, `PathInputHandler.cs`, `NodeInputHandler.cs`, `TerrainInputHandler.cs`, `RemovalManager.cs`, `TransitSelection.cs`
- 関連(編集フローへの委譲先): `unity/Assets/Scripts/Editors/`配下(`RoadEditor/RoadPlanManager.cs`, `BuildingEditor/BuildingPlanManager.cs`, `ParcelManager/ParcelEditorManager.cs`, 任意ポリゴン編集の共通基底 `PolygonAreaEditManager.cs` とその派生の資源エリア・区画形状・造成範囲(`GradingEditor/GradingAreaEditManager.cs`)の作図)

## 進め方
1. `InputHandlers/AGENTS.md`でハンドラ一覧と判定の罠を把握する。新規ハンドラは既存ハンドラ(`IInputHandler`実装。コライダーとタグが必須)のパターンを踏襲する
2. `Editors/AGENTS.md`で入力モードから編集フローへの委譲の流れを確認する
3. 実装ファイルを編集する
4. 個別の仕様はソースのコメントに書く。全体の考え方やモジュール間の契約が変わる変更をしたときだけ、ドキュメントの更新が必要になる旨をユーザーに伝える(このSkill自身はドキュメントを編集しない)
