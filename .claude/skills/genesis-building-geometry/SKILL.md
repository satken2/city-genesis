---
name: genesis-building-geometry
description: City Genesisの建物のジオメトリ観点のロジック(3D形状生成、建物メッシュ、フットプリント)を調査・実装・修正するときに使う。「建物の形がおかしい」「建物メッシュを追加/変更したい」「BuildingMeshBuilderを触りたい」など、UnityのMeshGenerators/BuildingsやバックエンドのgeoBuildingsを触るときは必ずこのSkillを参照し、読むべきドキュメントと編集対象ディレクトリを確認すること。経済的な建設ロジック側の話は genesis-building-simulation を使うこと。
---

> **パス表記の基準**: 公開側のパスは `city-genesis` リポジトリのルート起点。`genesis-core/`・`genesis-gallery/` は別の非公開リポジトリのルートを示し、並置を前提としない。利用できる場合だけ実際の配置に読み替え、手元に無い場合は公開側で確認できる範囲を扱う。

このSkillはコード本体を持たない「道しるべ」です。全体の考え方は以下のドキュメントに、個別の仕様と意図はソースのコメントに書かれているので、実装前に必ず読むこと。

## 読むべきドキュメント(概念、公開)
- `docs/ja/geometries/C_建物形状.md`
- `docs/ja/geometries/F_建物メッシュの生成.md` (メッシュ生成の共通仕様。まずこれを読む)
- `docs/ja/geometries/F1_建物メッシュの生成方式.md` (生成方式ごとの違いと、方式より先に分岐する建設中・解体中・倒壊の描画)

## 読むべきドキュメント(非公開)
- 該当なし(バックエンド側のフットプリント生成はAGENTS.mdに記載のみ)

## 読むべきAGENTS.md
- `unity/Assets/Scripts/MeshGenerators/AGENTS.md` (`BuildingMeshBuilder`, `ModularBuildingMeshBuilder`, `PortBuildingMeshBuilder`等)
- `genesis-core/app/services/geometry/AGENTS.md` (建物のフットプリント生成)

## 編集対象になりやすいディレクトリ/ファイル
- Unity: `unity/Assets/Scripts/MeshGenerators/Buildings/`配下(`BuildingMeshBuilder.cs`, 各種Body/Roofビルダー, `PortBuildingMeshBuilder.cs`)
- コア側のフットプリント生成・形状プレビューを変える必要がある場合は、非公開コアの担当者と契約を揃える。

## 進め方
1. 公開ドキュメントで形状・メッシュ生成パイプラインの仕様を把握する
2. 該当AGENTS.mdで実装済みビルダーの一覧と拡張パターンを確認する(似たビルダーの二重実装を防ぐため)
3. 実装ファイルを編集する
4. 個別の仕様はソースのコメントに書く。全体の考え方やモジュール間の契約が変わる変更をしたときだけ、ドキュメントの更新が必要になる旨をユーザーに伝える(このSkill自身はドキュメントを編集しない)
