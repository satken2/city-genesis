---
name: genesis-path-geometry
description: City Genesisのパス/ノード(道路・鉄道)のジオメトリ観点のロジック(3D形状、道路メッシュ、交差点処理、ベジエ補完)を調査・実装・修正するときに使う。「道路の形がおかしい」「交差点メッシュがずれる」「RoadPath/PathManagerを触りたい」「バックエンドとUnityでパス形状の計算がズレる」といった話のときは必ずこのSkillを参照し、読むべきドキュメントと編集対象ディレクトリを確認すること。経路探索・交通量側の話は genesis-path-simulation を使うこと。
---

> **パス表記の基準**: 本文中のパスはワークディレクトリ `S:/city/genesis`(`city-genesis` と `genesis-core` を並べた作業ツリー)を起点とする。`city-genesis` 単体で作業している場合は先頭の `city-genesis/` を読み替えること。`genesis-core/` 配下は非公開のコアシミュレータなので、手元に無ければその項目は読み飛ばしてよい。

このSkillはコード本体を持たない「道しるべ」です。実際の仕様・ロジックは以下のドキュメントに書かれているので、実装前に必ず読むこと。

## 読むべきドキュメント(概念、公開)
- `city-genesis/docs/docs/geometries/A_パス形状.md`
- `city-genesis/docs/docs/geometries/G_道路メッシュの生成.md`
- `city-genesis/docs/docs/geometries/J_パスの交差処理.md`
- 線路・LRTを触るとき: `city-genesis/docs/docs/geometries/N_線路メッシュの生成.md`(LRT専用軌道431の節)と G文書の「LRT付き道路222・242・歩行者道303」節

## 読むべきAGENTS.md
- `city-genesis/unity/Assets/Scripts/MeshGenerators/AGENTS.md` (RoadPath/RoadIntersectionMeshGeneratorの節)
- `city-genesis/unity/Assets/Scripts/DataLoaders/AGENTS.md` (PathManagerの節。`CalculatePathPoints`とバックエンド`PathEntity._auto_fill_controls`は**同一ロジックを維持する必要がある**仕様が明記されているので、片方だけ直して終わらせないこと)
- `genesis-core/app/services/geometry/AGENTS.md` (`geo_bezier.py`, `geo_paths.py`)

## 編集対象になりやすいディレクトリ/ファイル
- Unity: `city-genesis/unity/Assets/Scripts/MeshGenerators/Paths/RoadPath.cs`, `RoadIntersectionMeshGenerator.cs`, `city-genesis/unity/Assets/Scripts/DataLoaders/PathManager.cs`, `Utils/IntersectionPolygonUtil.cs`
- LRT(222/242/303/431): 軌道の横位置・踏面の高さ・停留所ホームの寸法は `MeshGenerators/Paths/LrtRoadParts.cs` が唯一の正(車両・交差点コネクタ・設備・歩行帯が参照する)。LRTの線路は1本のパスで上下線を持つ複線(DIR_BOTH固定)で、通常鉄道の「一方通行の単線2本」とは別方式。レールがつながる組の判定は `TrackIntersectionMeshGenerator.RailsConnect`(交差点コネクタと TrackPath の車止めが共有)。停留所の相対式ホームは `MeshGenerators/Paths/LrtPlatformParts.cs` が下地パスの生成の一部として描く(停留所は路線サマリの `stop_points` を `DataLoaders/LrtStopPlatforms.cs` が索引化し、範囲が変わったパスを `PathManager.RequestLrtPlatformRebuild` で作り直す。範囲の決め方・431の架線柱移設と高欄省略・303の設備抑止は MeshGenerators/AGENTS.md の LRT の節)。回帰検証は `city-genesis/unity/Tests/Lrt/run.ps1`
- バックエンド: `genesis-core/app/services/geometry/geo_bezier.py`, `geo_paths.py`(`create_road_polygon_from_path`, 交差判定)

## 進め方
1. 公開ドキュメントで形状・メッシュ生成パイプラインの仕様を把握する
2. 該当AGENTS.mdで「Unity側とバックエンド側のロジック一致要件」を確認する(片方だけの修正は不整合バグを生むため)
3. 実装ファイルを編集する(両側にまたがる変更の場合は両方直す)
4. 仕様に影響する変更をしたら、対応するドキュメントの更新が必要になる旨をユーザーに伝える(このSkill自身はドキュメントを編集しない)
