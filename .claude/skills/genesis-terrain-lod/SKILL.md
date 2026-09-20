---
name: genesis-terrain-lod
description: City Genesisの地形/LOD/セル分割まわり(heightmap、LODティア、セルデータ、超遠景メッシュ、Unity差分ロード)を調査・実装・修正するときに使う。「地形がガタガタ」「LODの切り替えがおかしい」「TerrainManager/CellDataLoaderを触りたい」「Z-fighting対策」といった話のときは必ずこのSkillを参照し、読むべきドキュメントと編集対象ディレクトリを確認すること。
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
- `city-genesis/unity/Assets/Scripts/DataLoaders/AGENTS.md` (LODティア表、TerrainManagerの節が特に詳細: heightmap取得、海岸線、超遠景メッシュ、Z-fighting対策)
- `genesis-core/app/services/AGENTS.md` (`terrain_registry.py`)

## 編集対象になりやすいディレクトリ/ファイル
- バックエンド: `genesis-core/app/services/terrain_registry.py`, `genesis-core/app/entities/terrain.py`, `genesis-core/app/services/geometry/geo_terrain.py`, `geo_terrain_modify.py`, `genesis-core/app/services/utility/terrain_cell_util.py`
- Unity: `city-genesis/unity/Assets/Scripts/DataLoaders/TerrainManager.cs`, `LodConfig.cs`, `CellDataLoader.cs`, `CameraCellChecker.cs`

## 進め方
1. 公開ドキュメントで地形データ構造・LOD段階の全体像を把握する
2. 詳細ロジックドキュメントとDataLoaders/AGENTS.mdでLODティア表・Z-fighting対策など細かい仕様を確認する
3. 実装ファイルを編集する
4. 仕様に影響する変更をしたら、対応するドキュメントの更新が必要になる旨をユーザーに伝える(このSkill自身はドキュメントを編集しない)
