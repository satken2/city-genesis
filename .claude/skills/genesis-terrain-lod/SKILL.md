---
name: genesis-terrain-lod
description: City Genesisの地形/LOD/セル分割まわり(heightmap、LODティア、セルデータ、超遠景メッシュ、Unity差分ロード、海岸線と埋立地の岸壁・帯の接地)を調査・実装・修正するときに使う。「地形がガタガタ」「LODの切り替えがおかしい」「TerrainManager/CellDataLoaderを触りたい」「Z-fighting対策」といった話のときは必ずこのSkillを参照し、読むべきドキュメントと編集対象ディレクトリを確認すること。
---

> **パス表記の基準**: 本文中のパスはワークディレクトリ `S:/city/genesis`(`city-genesis` と `genesis-core` を並べた作業ツリー)を起点とする。`city-genesis` 単体で作業している場合は先頭の `city-genesis/` を読み替えること。`genesis-core/` 配下は非公開のコアシミュレータなので、手元に無ければその項目は読み飛ばしてよい。

このSkillはコード本体を持たない「道しるべ」です。実際の仕様・ロジックは以下のドキュメントに書かれているので、実装前に必ず読むこと。

## 読むべきドキュメント(概念、公開)
- `city-genesis/docs/docs/geometries/D_地形形状.md`
- `city-genesis/docs/docs/geometries/E_LOD処理.md`
- `city-genesis/docs/docs/geometries/K_Unity差分ロード整理.md`

## 読むべきドキュメント(詳細ロジック、非公開)
- `genesis-core/docs/game-logic/LOD地形データ管理詳細.md`

## 読むべきAGENTS.md
- `city-genesis/unity/AGENTS.md` (ルートのデータロード全体像・座標系変換の要点)
- `city-genesis/unity/Assets/Scripts/DataLoaders/AGENTS.md` (地形の節: 知らないと事故る順序制約と規約。ティア表・海岸線・超遠景メッシュ・Z-fighting対策の仕様は D / E / K 文書。海岸線は埋立の完了で広がり `session:reload` のたびに取り直す。埋立地の岸壁・帯・天端と帯の中の接地 max(地形, 天端) は D 文書の §D.17)
- `genesis-core/app/services/AGENTS.md` (`terrain_registry.py`)

## 編集対象になりやすいディレクトリ/ファイル
- バックエンド: `genesis-core/app/services/terrain_registry.py`, `genesis-core/app/entities/terrain.py`, `genesis-core/app/services/geometry/geo_terrain.py`, `geo_terrain_modify.py`, `genesis-core/app/services/utility/terrain_cell_util.py`
- バックエンド(埋立地の幾何): `genesis-core/app/services/geometry/geo_reclaimed_land.py`(岸壁・帯・天端の唯一の窓口。Unity は判定を再実装しない)
- Unity: `city-genesis/unity/Assets/Scripts/DataLoaders/TerrainManager.cs`, `LodConfig.cs`, `CellDataLoader.cs`, `CameraCellChecker.cs`, `ReclaimedLandStore.cs` / `ReclaimedLandManager.cs`(埋立地の岸壁), `city-genesis/unity/Assets/Scripts/MeshGenerators/ReclaimedLandMeshBuilder.cs`, `city-genesis/unity/Assets/Scripts/Utils.cs`(`GetGroundHeight`)

## 進め方
1. 公開ドキュメントで地形データ構造・LOD段階の全体像を把握する
2. 詳細ロジックドキュメントで細かい仕様を、DataLoaders/AGENTS.mdで順序制約と罠を確認する
3. 実装ファイルを編集する
4. 仕様に影響する変更をしたら、対応するドキュメントの更新が必要になる旨をユーザーに伝える(このSkill自身はドキュメントを編集しない)
