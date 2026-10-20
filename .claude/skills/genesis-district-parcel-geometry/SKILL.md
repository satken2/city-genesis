---
name: genesis-district-parcel-geometry
description: City Genesisの地区/土地区画のジオメトリ観点のロジック(土地形状、ボロノイ分割、海岸線処理、ParcelManager/ZoneManagerの描画)を調査・実装・修正するときに使う。「区画の形がおかしい」「地区の再分割ロジックを変えたい」「DefaultParcel/ParcelMeshFactoryを触りたい」といった話のときは必ずこのSkillを参照し、読むべきドキュメントと編集対象ディレクトリを確認すること。所有権・賃料など経済側の話は genesis-district-parcel-simulation を使うこと。
---

> **パス表記の基準**: 公開側のパスは `city-genesis` リポジトリのルート起点。`genesis-core/`・`genesis-gallery/` は別の非公開リポジトリのルートを示し、並置を前提としない。利用できる場合だけ実際の配置に読み替え、手元に無い場合は公開側で確認できる範囲を扱う。

このSkillはコード本体を持たない「道しるべ」です。全体の考え方は以下のドキュメントに、個別の仕様と意図はソースのコメントに書かれているので、実装前に必ず読むこと。

## 読むべきドキュメント(概念、公開)
- `docs/ja/geometries/B_土地形状.md` (区画分割・ボロノイ分割の手順はコアの `geo_parcels.py` の docstring)
- `docs/ja/geometries/H_バックエンドデータロード.md` (ParcelManager / ZoneManager の描画)

## 読むべきドキュメント(非公開)
- `genesis-core/docs/game-logic/地区の処理ロジック.md`

## 読むべきAGENTS.md
- `genesis-core/app/services/geometry/AGENTS.md` (海岸線ユーティリティの使い分け、`geo_parcels.py` / `geo_zones.py` の入口関数)
- `unity/Assets/Scripts/MeshGenerators/AGENTS.md` (`Percels/DefaultParcel.cs`)
- `unity/Assets/Scripts/DataLoaders/AGENTS.md` (区画・地区の節の規約)

## 編集対象になりやすいディレクトリ/ファイル
- バックエンド: `genesis-core/app/services/geometry/geo_parcels.py`, `geo_zones.py`
- Unity: `unity/Assets/Scripts/MeshGenerators/Percels/DefaultParcel.cs`, `ParcelMeshFactory.cs`, `unity/Assets/Scripts/DataLoaders/ParcelManager.cs`, `ZoneManager.cs`

## 進め方
1. 公開ドキュメントで土地形状の生成方針を把握する
2. 該当AGENTS.mdで海岸線ユーティリティの使い分けと既存の入口関数を確認する(二重実装を防ぐ)
3. 実装ファイルを編集する
4. 個別の仕様はソースのコメントに書く。全体の考え方やモジュール間の契約が変わる変更をしたときだけ、ドキュメントの更新が必要になる旨をユーザーに伝える(このSkill自身はドキュメントを編集しない)
