---
name: genesis-mesh-generation
description: City Genesisの3Dメッシュ生成処理全般(MeshFactoryのディスパッチ、道路/交差点/建物/土地区画/超遠景陸地メッシュのパイプライン共通部分)を調査・実装・修正するときに使う。特定の対象(建物だけ、道路だけ)ではなく「メッシュ生成の仕組み全体」や「新しいメッシュ生成器を追加したい」といった横断的な話のときはこのSkillを使う。建物専用の話は genesis-building-geometry、道路専用の話は genesis-path-geometry の方が詳しい場合がある。
---

> **パス表記の基準**: 公開側のパスは `city-genesis` リポジトリのルート起点。`genesis-core/`・`genesis-gallery/` は別の非公開リポジトリのルートを示し、並置を前提としない。利用できる場合だけ実際の配置に読み替え、手元に無い場合は公開側で確認できる範囲を扱う。

このSkillはコード本体を持たない「道しるべ」です。全体の考え方は以下のドキュメントに、個別の仕様と意図はソースのコメントに書かれているので、実装前に必ず読むこと。

## 読むべきドキュメント(概念、公開)
- `docs/ja/geometries/F_建物メッシュの生成.md` (建物の共通仕様)
- `docs/ja/geometries/F1_建物メッシュの生成方式.md` (生成方式ごとの違いと、工事中・倒壊時の描画)
- `docs/ja/geometries/G_道路メッシュの生成.md`

## 読むべきドキュメント(非公開)
- 該当なし(Unity実装のため公開ドキュメント側のみ)

## 読むべきAGENTS.md
- `unity/Assets/Scripts/MeshGenerators/AGENTS.md` (最重要。`MeshFactory.cs`のディスパッチ表、生成器のファイル一覧、DrawLodの経路、知らないと事故る規約)

## 編集対象になりやすいディレクトリ/ファイル
- `unity/Assets/Scripts/MeshGenerators/`配下全体
  - `PathMeshFactory.cs`, `NodeMeshFactory.cs`, `ParcelMeshFactory.cs`
  - `Paths/RoadPath.cs`, `Paths/RoadIntersectionMeshGenerator.cs`
  - `Buildings/BuildingMeshBuilder.cs`(+各種Body/Roofビルダー, `PortBuildingMeshBuilder.cs`)
  - `UltraFarLandMeshBuilder.cs`, `Percels/DefaultParcel.cs`
  - `Utils/IntersectionPolygonUtil.cs`

## 進め方
1. `MeshGenerators/AGENTS.md`のディスパッチ表で、どのFactory/Builderが何を担当するか全体像を把握する
2. 対象が建物/道路/区画に限定される場合は、より詳細な専用Skill(genesis-building-geometry / genesis-path-geometry / genesis-district-parcel-geometry)も併用する
3. 実装ファイルを編集する
4. 個別の仕様はソースのコメントに書く。全体の考え方やモジュール間の契約が変わる変更をしたときだけ、ドキュメントの更新が必要になる旨をユーザーに伝える(このSkill自身はドキュメントを編集しない)
