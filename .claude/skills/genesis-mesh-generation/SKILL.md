---
name: genesis-mesh-generation
description: City Genesisの3Dメッシュ生成処理全般(MeshFactoryのディスパッチ、道路/交差点/建物/土地区画/超遠景陸地メッシュのパイプライン共通部分)を調査・実装・修正するときに使う。特定の対象(建物だけ、道路だけ)ではなく「メッシュ生成の仕組み全体」や「新しいメッシュ生成器を追加したい」といった横断的な話のときはこのSkillを使う。建物専用の話は genesis-building-geometry、道路専用の話は genesis-path-geometry の方が詳しい場合がある。
---

> **パス表記の基準**: 本文中のパスはワークディレクトリ `S:/city/genesis`(`city-genesis` と `genesis-core` を並べた作業ツリー)を起点とする。`city-genesis` 単体で作業している場合は先頭の `city-genesis/` を読み替えること。`genesis-core/` 配下は非公開のコアシミュレータなので、手元に無ければその項目は読み飛ばしてよい。

このSkillはコード本体を持たない「道しるべ」です。実際の仕様・ロジックは以下のドキュメントに書かれているので、実装前に必ず読むこと。

## 読むべきドキュメント(概念、公開)
- `city-genesis/docs/docs/geometries/F_建物メッシュの生成.md` (建物の共通仕様。生成方式固有は `F1_`〜`F11_` の別ファイル)
- `city-genesis/docs/docs/geometries/G_道路メッシュの生成.md`

## 読むべきドキュメント(非公開)
- 該当なし(Unity実装のため公開ドキュメント側のみ)

## 読むべきAGENTS.md
- `city-genesis/unity/Assets/Scripts/MeshGenerators/AGENTS.md` (最重要。`MeshFactory.cs`のディスパッチ表、生成器のファイル一覧、DrawLodの経路、知らないと事故る規約)

## 編集対象になりやすいディレクトリ/ファイル
- `city-genesis/unity/Assets/Scripts/MeshGenerators/`配下全体
  - `PathMeshFactory.cs`, `NodeMeshFactory.cs`, `ParcelMeshFactory.cs`
  - `Paths/RoadPath.cs`, `Paths/RoadIntersectionMeshGenerator.cs`
  - `Buildings/BuildingMeshBuilder.cs`(+各種Body/Roofビルダー, `PortBuildingMeshBuilder.cs`)
  - `UltraFarLandMeshBuilder.cs`, `Percels/DefaultParcel.cs`
  - `Utils/IntersectionPolygonUtil.cs`

## 進め方
1. `MeshGenerators/AGENTS.md`のディスパッチ表で、どのFactory/Builderが何を担当するか全体像を把握する
2. 対象が建物/道路/区画に限定される場合は、より詳細な専用Skill(genesis-building-geometry / genesis-path-geometry / genesis-district-parcel-geometry)も併用する
3. 実装ファイルを編集する
4. 仕様に影響する変更をしたら、対応するドキュメントの更新が必要になる旨をユーザーに伝える(このSkill自身はドキュメントを編集しない)
