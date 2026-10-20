---
name: genesis-path-geometry
description: City Genesisのパス/ノード(道路・鉄道)のジオメトリ観点のロジック(3D形状、道路メッシュ、交差点処理、ベジエ補完)を調査・実装・修正するときに使う。「道路の形がおかしい」「交差点メッシュがずれる」「RoadPath/PathManagerを触りたい」「バックエンドとUnityでパス形状の計算がズレる」といった話のときは必ずこのSkillを参照し、読むべきドキュメントと編集対象ディレクトリを確認すること。経路探索・交通量側の話は genesis-path-simulation を使うこと。
---

> **パス表記の基準**: 公開側のパスは `city-genesis` リポジトリのルート起点。`genesis-core/`・`genesis-gallery/` は別の非公開リポジトリのルートを示し、並置を前提としない。利用できる場合だけ実際の配置に読み替え、手元に無い場合は公開側で確認できる範囲を扱う。

このSkillはコード本体を持たない「道しるべ」です。全体の考え方は以下のドキュメントに、個別の仕様と意図はソースのコメントに書かれているので、実装前に必ず読むこと。

## 読むべきドキュメント(概念、公開)
- `docs/ja/geometries/A_パス形状.md`
- `docs/ja/geometries/G_道路メッシュの生成.md`
- `docs/ja/geometries/J_パスの交差処理.md`
- 線路・LRTを触るとき: `docs/ja/geometries/N_線路メッシュの生成.md`。LRTの寸法と配置は `MeshGenerators/Paths/LrtRoadParts.cs`・`LrtPlatformParts.cs` のコメント

## 読むべきAGENTS.md
- `unity/Assets/Scripts/MeshGenerators/AGENTS.md` (`Paths/`の生成器一覧、DrawLodの経路、規約、バックエンドとの鏡写し表)
- `unity/Assets/Scripts/DataLoaders/AGENTS.md` (道路の節。`CalculatePathPoints`の制御点自動計算はバックエンド`Path._auto_fill_controls`と**同一式を維持する必要がある**ので、片方だけ直して終わらせないこと)
- `genesis-core/app/services/geometry/AGENTS.md` (`geo_bezier.py`, `geo_paths.py`)

## 編集対象になりやすいディレクトリ/ファイル
- Unity: `unity/Assets/Scripts/MeshGenerators/Paths/RoadPath.cs`, `RoadIntersectionMeshGenerator.cs`, `unity/Assets/Scripts/DataLoaders/PathManager.cs`, `Utils/IntersectionPolygonUtil.cs`
- LRT(222/242/303/431): 軌道の横位置・踏面の高さ・停留所ホームの寸法は `MeshGenerators/Paths/LrtRoadParts.cs` が唯一の正(車両・交差点コネクタ・設備・歩行帯が参照する)。LRTの線路は1本のパスで上下線を持つ複線(DIR_BOTH固定)で、通常鉄道・モノレールの軌道数2のパス(まとめた複線)と同じ方式(通常鉄道は一方通行の単線2本を並べても複線を作れる)。ノードでつながる軌道端は「パスと進む向きで走る軌道が1つに決まる」規則(`TrackPath.TrackIndexForTravel`)で決まり、交差点コネクタと TrackPath の車止めが `TrackIntersectionMeshGenerator` の同じ判定を共有する(つながり得るパス種別の組は `RailsConnect`)。停留所の相対式ホームは `MeshGenerators/Paths/LrtPlatformParts.cs` が下地パスの生成の一部として描く(停留所は路線サマリの `stop_points` を `DataLoaders/LrtStopPlatforms.cs` が索引化し、範囲が変わったパスを `PathManager.RequestLrtPlatformRebuild` で作り直す。デッキは交差点で切断済みの区間へ収め、デッキ端の外側のスロープは交差点ハル上や行き止まり端の先へはみ出してよい。範囲の決め方の詳細・431の架線柱移設と高欄省略・303の設備抑止は `LrtPlatformParts.cs`・`LrtStopPlatforms.cs` のコメント)。回帰検証は `unity/Tests/Lrt/run.ps1`
- バックエンド: `genesis-core/app/services/geometry/geo_bezier.py`, `geo_paths.py`(`create_road_polygon_from_path`, 交差判定)

## 進め方
1. 公開ドキュメントで形状・メッシュ生成パイプラインの仕様を把握する
2. 該当AGENTS.mdで「Unity側とバックエンド側のロジック一致要件」を確認する(片方だけの修正は不整合バグを生むため)
3. 実装ファイルを編集する(両側にまたがる変更の場合は両方直す)
4. 個別の仕様はソースのコメントに書く。全体の考え方やモジュール間の契約が変わる変更をしたときだけ、ドキュメントの更新が必要になる旨をユーザーに伝える(このSkill自身はドキュメントを編集しない)
