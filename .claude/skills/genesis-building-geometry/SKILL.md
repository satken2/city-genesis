---
name: genesis-building-geometry
description: City Genesisの建物のジオメトリ観点のロジック(3D形状生成、建物メッシュ、フットプリント)を調査・実装・修正するときに使う。「建物の形がおかしい」「建物メッシュを追加/変更したい」「BuildingMeshBuilderを触りたい」など、UnityのMeshGenerators/BuildingsやバックエンドのgeoBuildingsを触るときは必ずこのSkillを参照し、読むべきドキュメントと編集対象ディレクトリを確認すること。経済的な建設ロジック側の話は genesis-building-simulation を使うこと。
---

> **パス表記の基準**: 本文中のパスはワークディレクトリ `S:/city/genesis`(`city-genesis` と `genesis-core` を並べた作業ツリー)を起点とする。`city-genesis` 単体で作業している場合は先頭の `city-genesis/` を読み替えること。`genesis-core/` 配下は非公開のコアシミュレータなので、手元に無ければその項目は読み飛ばしてよい。

このSkillはコード本体を持たない「道しるべ」です。実際の仕様・ロジックは以下のドキュメントに書かれているので、実装前に必ず読むこと。

## 読むべきドキュメント(概念、公開)
- `city-genesis/docs/docs/geometries/C_建物形状.md`
- `city-genesis/docs/docs/geometries/F_建物メッシュの生成.md` (メッシュ生成の共通仕様。まずこれを読む)
- 生成方式(model_type)固有のロジックは別ファイルなので、対象の方式のものを併せて読む
  - `F1_モジュラー建物のメッシュ生成.md` (1=MODEL_MODULAR_FLAT / 2=HOUSE / 3=GABLE / 8=SHED の屋根)
  - `F2_地面アセットのメッシュ生成.md` (4=MODEL_MODULAR_GROUND。建物本体は描かない)
  - `F3_既成建物のメッシュ生成.md` (5=MODEL_PREBUILT)
  - `F4_港湾岸壁のメッシュ生成.md` (6=MODEL_PORT)
  - `F6_マッシュアップ建物のメッシュ生成.md` (7=MODEL_MASHUP。複数棟の複合施設)
  - `F9_車両基地のメッシュ生成.md` (9=MODEL_RAIL_YARD)
  - `F11_露天掘りのメッシュ生成.md` (13=MODEL_OPEN_PIT。穴は地形で、前庭の設備だけをマッシュアップと同じ規則で描く)
- **statusはmodel_typeより先に分岐する**別軸なので、該当statusを扱うなら必ず読む
  - `F5_工事中建物のメッシュ生成.md` (STATUS_CONSTRUCTING)
  - `F7_解体中建物のメッシュ生成.md` (STATUS_DEMOLISHING)
  - `F8_倒壊建物のメッシュ生成.md` (STATUS_COLLAPSED)
- `city-genesis/docs/docs/geometries/I_建物パーツ作成マニュアル.md`

## 読むべきドキュメント(非公開)
- 該当なし(バックエンド側のフットプリント生成はAGENTS.mdに記載のみ)

## 読むべきAGENTS.md
- `city-genesis/unity/Assets/Scripts/MeshGenerators/AGENTS.md` (`BuildingMeshBuilder`, `ModularBuildingMeshBuilder`, `PortBuildingMeshBuilder`等)
- `genesis-core/app/services/geometry/AGENTS.md` (`geo_buildings.py`のフットプリント生成)

## 編集対象になりやすいディレクトリ/ファイル
- Unity: `city-genesis/unity/Assets/Scripts/MeshGenerators/Buildings/`配下(`BuildingMeshBuilder.cs`, 各種Body/Roofビルダー, `PortBuildingMeshBuilder.cs`)
- バックエンド: `genesis-core/app/services/geometry/geo_buildings.py`(`generate_building`), `genesis-core/app/services/crud/crud_buildings.py`(`preview_building_shape`)

## 進め方
1. 公開ドキュメントで形状・メッシュ生成パイプラインの仕様を把握する
2. 該当AGENTS.mdで実装済みビルダーの一覧と拡張パターンを確認する(似たビルダーの二重実装を防ぐため)
3. 実装ファイルを編集する
4. 仕様に影響する変更をしたら、対応するドキュメントの更新が必要になる旨をユーザーに伝える(このSkill自身はドキュメントを編集しない)
