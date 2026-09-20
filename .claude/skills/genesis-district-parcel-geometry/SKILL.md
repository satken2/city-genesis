---
name: genesis-district-parcel-geometry
description: City Genesisの地区/土地区画のジオメトリ観点のロジック(土地形状、ボロノイ分割、海岸線処理、ParcelManager/ZoneManagerの描画)を調査・実装・修正するときに使う。「区画の形がおかしい」「地区の再分割ロジックを変えたい」「DefaultParcel/ParcelMeshFactoryを触りたい」といった話のときは必ずこのSkillを参照し、読むべきドキュメントと編集対象ディレクトリを確認すること。所有権・賃料など経済側の話は genesis-district-parcel-simulation を使うこと。
---

> **パス表記の基準**: 本文中のパスはワークディレクトリ `S:/city/genesis`(`city-genesis` と `genesis-core` を並べた作業ツリー)を起点とする。`city-genesis` 単体で作業している場合は先頭の `city-genesis/` を読み替えること。`genesis-core/` 配下は非公開のコアシミュレータなので、手元に無ければその項目は読み飛ばしてよい。

このSkillはコード本体を持たない「道しるべ」です。実際の仕様・ロジックは以下のドキュメントに書かれているので、実装前に必ず読むこと。

## 読むべきドキュメント(概念、公開)
- `city-genesis/docs/docs/geometries/B_土地形状.md`

## 読むべきドキュメント(非公開)
- 該当なし(ボロノイ分割等はAGENTS.mdに記載のみ)

## 読むべきAGENTS.md
- `genesis-core/app/services/geometry/AGENTS.md` (`geo_parcels.py`: 海岸線ユーティリティ、`subdivide_parcels`、`get_path_parcels`; `geo_zones.py`: `recalculate_zone_geometry`)
- `city-genesis/unity/Assets/Scripts/MeshGenerators/AGENTS.md` (`Percels/DefaultParcel.cs`)
- `city-genesis/unity/Assets/Scripts/DataLoaders/AGENTS.md` (ParcelManager/ZoneManagerの`OverlayMeshRenderer`描画節)

## 編集対象になりやすいディレクトリ/ファイル
- バックエンド: `genesis-core/app/services/geometry/geo_parcels.py`, `geo_zones.py`
- Unity: `city-genesis/unity/Assets/Scripts/MeshGenerators/Percels/DefaultParcel.cs`, `ParcelMeshFactory.cs`, `city-genesis/unity/Assets/Scripts/DataLoaders/ParcelManager.cs`, `ZoneManager.cs`

## 進め方
1. 公開ドキュメントで土地形状の生成方針を把握する
2. 該当AGENTS.mdでボロノイ分割・海岸線処理・オーバーレイ描画の既存実装を確認する
3. 実装ファイルを編集する
4. 仕様に影響する変更をしたら、対応するドキュメントの更新が必要になる旨をユーザーに伝える(このSkill自身はドキュメントを編集しない)
