---
name: genesis-draw-lod
description: City Genesisの描画LOD(建物・パス1つごとの詳細度 Close/Normal/Far、接写パーツ `_close`、詳細描画区間、曲率適応タイル、描画統計オーバーレイ)を調査・実装・修正するときに使う。「近づいても細かいモデルに切り替わらない」「遠くの建物が重い」「_closeパーツが使われない」「DrawLodのしきい値を変えたい」「線路の頂点数警告」といった話のときは必ずこのSkillを参照し、読むべきドキュメントと編集対象を確認すること。セル単位の地形データティア(Dynamic/Near/Far/UltraFar)や地形ストリーミングの話は genesis-terrain-lod を使うこと。両者は独立した仕組みなので混同しないこと。
---

> **パス表記の基準**: 本文中のパスは `city-genesis` リポジトリのルート起点。

このSkillはコード本体を持たない「道しるべ」です。全体の考え方は以下のドキュメントに、個別の仕様と意図はソースのコメントに書かれているので、実装前に必ず読むこと。

## まず押さえる区別

| 仕組み | 単位 | 決めること | Skill |
|---|---|---|---|
| データティア `LodTier` | セル | どの地形データを取得・保持するか、地形追従(isDynamic)するか | genesis-terrain-lod |
| **描画LOD `DrawLod`** | 建物1棟 / パス1本(+詳細区間) | どの細かさで描くか(Close/Normal/Far) | **このSkill** |

描画LODはセルのデータティアでは固定しない。建物は投影比(画面上の大きさ)、パスはアンカー点列までの
最近接距離で Close/Normal/Far が決まる。

## 読むべきドキュメント(概念、公開)
- `docs/ja/geometries/E_LOD処理.md`(「物体の描画LOD」の節。しきい値などの値は `DrawLodConfig`)
- `docs/ja/geometries/F_建物メッシュの生成.md`(`_close` の命名と解決順)
- `docs/ja/geometries/G_道路メッシュの生成.md`(LOD・パーツの命名・フォールバック。曲率適応タイルは `PathDrawContext` のコメント)
- `docs/ja/geometries/N_線路メッシュの生成.md`

## 読むべきAGENTS.md
- `unity/Assets/Scripts/DataLoaders/AGENTS.md`(建物・パスの描画LODワーカー、PathDrawContextの使い方)
- `unity/Assets/Scripts/MeshGenerators/AGENTS.md`(生成器がDrawLodを受け取る経路)

## 編集対象になりやすいファイル
- しきい値・上限: `unity/Assets/Scripts/DataLoaders/DrawLod.cs`(`DrawLodConfig` / `DrawLodEvaluator` / `DrawLodInfo`)
- 建物側: `DataLoaders/BuildingManager.cs`(`DetermineDesiredDrawLod` / `DrawLodReevaluationWorker`)、`MeshGenerators/Buildings/BuildingMeshBuilder.cs`(`GenerateFarBuilding`)、`MeshGenerators/Buildings/FarBuildingMeshBuilder.cs`(遠景モデルの引き伸ばし)、`MeshGenerators/Buildings/BuildingMeshUtils.cs`(`CurrentDrawLod` と `_close` 解決)
- パス側: `DataLoaders/PathManager.cs`(`DeterminePathDrawLod` / `PathDrawLodReevaluationWorker` / `AddFarRemainders`)、`MeshGenerators/Paths/PathFarModel.cs`(遠景モデルを並べる)、`MeshGenerators/Paths/PathDrawContext.cs`、`MeshGenerators/Paths/RoadPath.cs`(`CreateSplineMesh`)
- 計測: `DataLoaders/DrawLodStats.cs`(F8 でオーバーレイ表示。描画LOD別の個数・生成ms・頂点数)

## 知らないと事故る仕様
- 生成器はセルのティア表を参照してはいけない。描画LODは `BuildingMeshBuilder.Generate(data, isDynamic, drawLod)` の引数と、パスは `PathDrawContext.Begin/End`(PathManager が生成の前後で呼ぶ)で渡す。
- `_close` パーツは無印と bbox・原点・マテリアルが一致していることが前提。Unityは配置計算を変えずに差し替える。制作側の規約と検査は genesis-building-parts / genesis-path-parts を参照。
- パスの詳細区間(長いパスの近い部分だけ詳細)は単一スプラインの生成にだけ効く。折れ角付き道路(セグメントごとに別スプライン)・トンネル分割・工事中は全長を同じ詳細度で描く。区間外は `FarRemainder_*` の遠景モデルで埋める。
- 接写の同時保持数は `DrawLodConfig.MAX_CLOSE_BUILDINGS` / `MAX_CLOSE_PATHS` で上限がある。上限に達すると昇格せず Normal で生成する(降格は投影比が決める)。
- 再判定はカメラが `REEVALUATE_CAMERA_MOVE_DISTANCE` 以上動いたときだけ走る。詳細化は遅延キュー(建物)/再生成キュー(パス)経由なので、切り替わるまで旧オブジェクトが表示され続ける(消えてから出るのではない)。
- 遠景から詳細(Normal/Close)へ上げるのは、接地・通過する常駐セルの地形タイルが公開されてから(建物は遅延キューの選出 `SelectTopDeferredBuildings`、パスは `DeterminePathDrawLod` が `TerrainManager.IsDetailTerrainReady` を見る)。タイルの公開で各マネージャーの `OnTerrainTileReady` が詳細化を再開させる。
- 常駐範囲の外は都市全域の超遠景(`DataLoaders/CityOverviewManager.cs`)が箱と帯で埋め、実物を表示している建物・パスは結合メッシュから除く。遠景の箱・帯の作り方を変えたら、超遠景が共用する `BuildingMeshBuilder.AppendExtrudedFootprint`・`GetFarBoxHeight`・`PathFarModel.Build` との整合を確かめる。

## 進め方
1. E_LOD処理.md で「データティア/描画LOD/予算」の役割分担を把握する
2. しきい値の変更だけなら `DrawLodConfig` を編集し、F8 オーバーレイで個数・生成時間・頂点数を確認する
3. 生成内容の変更は建物なら `BuildingMeshBuilder`、パスなら `RoadPath.CreateSplineMesh`/`PathDrawContext` を編集する
4. 個別の仕様はソースのコメントに書く。全体の考え方やモジュール間の契約が変わる変更をしたときだけ、ドキュメント(E/F/G/N)と AGENTS.md の更新が必要になる旨をユーザーに伝える
