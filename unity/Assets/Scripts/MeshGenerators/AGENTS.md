# MeshGenerators フォルダ

建物・土地区画・パス(道路/線路/駅舎/送電線)・交差点・超遠景陸地のメッシュ生成器。
`DataLoaders/` のマネージャー群から呼ばれる純粋な生成器で、呼び出し側のフロー・キャッシュ・再生成トリガーは `DataLoaders/AGENTS.md` を参照。
仕様の正は公開ドキュメント `city-genesis/docs/docs/geometries/`(以下 F・G 等の頭文字と節番号で示す)。ここは入り口であり、個別生成器の寸法・部材名・造形の細部はドキュメント側に書く。

## ディスパッチ (MeshFactory.cs)

| 入口 | 振り分け |
| --- | --- |
| `PathMeshFactory.GeneratePathMesh` | `path_variant` 固有辞書(送電線1101/1102→`PowerLinePath`) → `path_master.model_type`(`default`→`RoadPath`、`track`→`TrackPath`、`station_house`→`StationHousePath`、`none`→null)。`path_type` では選ばない |
| `NodeMeshFactory.GenerateNodeMesh` | 常に `RoadIntersectionMeshGenerator`(接続2本未満は null)。線路2本以上なら `TrackIntersectionMeshGenerator` がレールコネクタを追加。インフラノードは呼び出し側が除外 |
| `ParcelMeshFactory.GenerateParcelMesh` | usage_id 辞書、未登録は `DefaultParcel`(現状は常に Default) |
| 建物 | ファクトリを介さず `BuildingMeshBuilder.Generate(data, isDynamic, drawLod)`。status(工事中/解体中→Constructing、倒壊→Collapsed)で先に分岐し、次に `model_type`。該当なしは白い箱 |

交差点の生成器は PATH_CONVEYOR/ELECTRICITY/PIPELINE を接続から外すが、導水管(PATH_WATER)は地表の管路として路面を作る。`InfraNetworkUtil.IsInfraPathType` で一括判定しないのはこのため。

## ファイル一覧

| 直下 | 役割 |
| --- | --- |
| `MeshFactory.cs` | ファクトリと結果型。`PathMeshResult.Mesh` はコライダー用ストリップで描画用ではない |
| `AgentModelConfig.cs` | `agent_model_config.yaml` のローダー。使うのはエージェントIDの有無・`structure_prefixes`・`landmark_prefixes` だけ |
| `UltraFarLandMeshBuilder.cs` | 海岸線ポリゴンから超遠景の陸地シルエットを純データで返す(E「超遠景」) |
| `Percels/DefaultParcel.cs` | 区画の上面(ファン分割。凹ポリゴンでは破綻し得る)+押し出し側面 |
| `Test/` | 確認用シーン専用。本編からは参照されない |

| `Paths/` | 役割(参照先) |
| --- | --- |
| `RoadPath.cs` | 道路。線路・駅舎・工事中とも共用する共通ヘルパーの置き場: `CreateSplineMesh` / `BuildSpline` / `SealSplineMesh` / `BuildColliderMeshFromSpline` / `CreateFallbackRoadBody` / `GeneratePillars` / `GetOrCreateRoadMaterial(ByName)` / `GetPathMasterParams` / `RoadLaneLayout` / `IntersectionMeshOwner`。再実装しない(G) |
| `RoadPartHeights.cs` | パーツ高さオフセット定数の唯一の正(G§4.4) |
| `PathDrawContext.cs` | 描画LOD・詳細区間・曲率適応タイル長・`_close` 解決(`ResolvePrefab`)の静的コンテキスト |
| `RoadAdjacency.cs` / `MotorwayMergeLayout.cs` | 高速道路の本線↔ランプ結合・板なし合流ノード(G§11.7) |
| `PillarClearance.cs` | 橋脚と他パス通行空間の干渉判定。道路・線路・工事中・レンガ高架で共用(G§8.7) |
| `RoadEquipmentGenerator.cs` | 街灯・電柱・街路樹・柵・ベンチ等の点設備(G§8.6・§5.5) |
| `RoadMarkingContext.cs` / `RoadMarkingPlan.cs` / `RoadMarkingSurface.cs` | 路面標示(G§8.3、E「路面標示のLODと画像予算」) |
| `SuspensionBridgeGenerator.cs` / `BrickArcadeGenerator.cs` / `PathStructureMesh.cs` | 吊り橋251〜256・レンガアーチ高架404/414と、その結合器(G「植栽中央分離帯と道路吊り橋」、N「レンガアーチ高架」) |
| `PowerLinePath.cs` / `PowerLineSupport.cs` | 送電線1101/1102 の専用生成器と共有ノードの鉄塔所有権(G「送電線の専用描画」) |
| `ConstructionPathBuilder.cs` / `ConstructionMeshUtil.cs` | 工事中パス(G§15) |
| `RoadIntersectionMeshGenerator.cs` | 交差点(G§11、J「Unity側の交差点メッシュの再計算」) |
| `TrafficSignalLayout.cs` / `TrafficSignalBuilder.cs` / `TrafficSignalStyle.cs` | 交差点の信号機。描画専用(G§11.8) |
| `TrackPath.cs` | 線路・モノレール・LRT専用軌道431(N) |
| `TrackIntersectionMeshGenerator.cs` | ポイント・平面交差のレールコネクタ。`RailsConnect` は車止めとも共有(N§4) |
| `TrackAdjacency.cs` | 近接線路の結合(複線見え化)・島式ホーム(N§7.4・§8) |
| `TrackDirectionArrows.cs` / `PlatformFoundationGenerator.cs` / `FarPlatformPath.cs` | 一方通行線路の矢印(敷設モードのみ。N§9)・高架ホーム外壁下部(N「高規格高架PF」)・駅と乗り場の遠景簡易立体(N§6。対象バリアントは `FarPlatformPath.Handles` に列挙。コンテナ貨物PF 463 は上屋の無い荷役床だけ) |
| `LrtRoadParts.cs` / `LrtPlatformParts.cs` | LRT の軌道位置・踏面・ホーム寸法の唯一の正と、併用軌道・停留所ホーム(G「LRT付き道路」「LRT停留所のホーム」、N「LRT専用軌道431」) |
| `StationHousePath.cs` | 駅舎パス(連絡橋+両終端の建屋)(Q) |

| `Buildings/` | 役割(参照先) |
| --- | --- |
| `BuildingMeshBuilder.cs` | 入口。static 幾何ユーティリティ(`GenerateColliderMesh` / `MergeSubMeshes` / `TriangulateOrFan` / `TriangulateEarClip`=失敗時は空 / `SubdivideTrianglesByEdgeLength` / `FlipToDelaunay` / `InsertInteriorPoints` / `GenerateTerrainSurfaceMesh` / `GenerateFoundationMesh` / `GenerateEntrance` / `GenerateRoofprop`)(F§15) |
| `BuildingMeshUtils.cs` | パーツ解決(prefix・`_close`・common)・`CombineModuleMeshes`・`AddStackedModule`・`ResolveParcelContext`・`SanitizeRingPolygon`・屋根共通幾何 |
| `ModularBuildingMeshBuilder.cs` / `ModularBodyBuilder.cs` / `DefaultBodyBuilder.cs` | モジュラー躯体(壁・角の敷き詰め `BuildWallRun`、敷地装飾 `GenerateSiteDecorations`)と白箱フォールバック(F・F1) |
| `FlatRoofBuilder.cs` / `HouseRoofBuilder.cs` / `GableRoofBuilder.cs` / `ShedRoofBuilder.cs` / `InteriorFloorBuilder.cs` / `ExteriorStairBuilder.cs` | 屋根4種・各階の床(F1)、辺ごとの外階段(F§11.4) |
| `PreBuiltBuildingMeshBuilder.cs` / `MashupBuildingMeshBuilder.cs` / `PortBuildingMeshBuilder.cs` / `RailYardBuildingMeshBuilder.cs` | 既成・観光名所(F3)、複数棟(F6。割り付けはバックエンド解決済み)、港湾岸壁(F4)、車両基地(F9) |
| `OpenSpaceLayout.cs` / `OpenSpaceBuildingMeshBuilder.cs` | 駐車場・駅前ロータリー・公園(F10) |
| `ConstructingBuildingMeshBuilder.cs` / `ConstructionSiteProfile.cs` / `CollapsedBuildingMeshBuilder.cs` | 工事中・解体中(profile 差分で共用。F5・F7)、倒壊(F8) |

## 描画LOD(DrawLod)が生成器に届く経路

- 生成器はセルのティア表を見ない。DrawLod は呼び出し側が決め、建物は `Generate` の引数、パスは `PathDrawContext.Begin/End` で渡る。コンテキスト外の生成は Normal・全長扱い(E「物体の描画LOD」)。
- Far は専用経路。建物は `GenerateFarBuilding`(`*-prebuilt_far` が無ければフットプリント押し出しの箱)、パスは PathManager 側の帯で生成器のパーツ組み立ては走らない(例外: `FarPlatformPath`・`PowerLinePath`)。
- `_close` の解決点は2箇所だけ。建物は `BuildingMeshUtils.GetModelForDrawLod`、パスは `PathDrawContext.ResolvePrefab`(`CreateSplineMesh` で敷くパーツと送電鉄塔のみ。Instantiate 配置の tree/pillar/pole/設備やコネクタは常に無印)。新しいパーツ取得をこの2つを迂回して書かない。
- `CreateSplineMesh` はクリップ区間と `PathDrawContext` の詳細区間の共通部分にだけ敷く。セグメントごとに別スプラインを作る生成器は割合基準が合わないので冒頭で `SuppressDetailRange = true` にする(`GenerateLinearPathWithCorners` が例)。複数スプラインの生成器を新設するときも同じ。
- 曲率適応タイル長は `BuildSpline` 直後に `SetAdaptiveTileLength(ComputeAdaptiveTileLength(...))` を呼んで有効化する。`maxTileLength` に明示 `0`(著作長)を渡すパーツには適用されない。

## 知らないと事故る規約

- 隣接情報は PathManager.Awake が差す静的プロバイダ経由(`PillarClearance` / `TrackAdjacency` / `RoadAdjacency` の `NeighborProvider`、`TrackPath.ConnectedPathsProvider` / `InfrastructureConnectionProvider`、`LrtPlatformParts.StopProvider` / `PolylineProvider`、`PowerLinePath.ConnectedPaths`)。
  - 未設定のテストシーンでは結合・干渉回避が黙って無効になる。結合描画は描画専用でバックエンドに影響しない。
- SplineMesh の頂点予算 `RoadPath.SPLINE_MESH_VERTEX_BUDGET`(60000)を超えると Dreamteck が毎フレームエラーを出し続ける。新しい SplineMesh 生成経路は同じガードを入れ、構築後に `RoadPath.SealSplineMesh` を呼ぶ。封印後は点や Transform を変えても再構築されない(形を変えるなら作り直す)。
- `IntersectionMeshOwner` は手続き生成メッシュと SplineMesh の生成メッシュだけを所有し(共有プレハブは登録しない)、`OnDestroy` で解放する。一度もアクティブにならなかった GO では `OnDestroy` が呼ばれないので、`SetActive(false)` より先に付ける。
- 高さオフセットは `RoadPartHeights` に集約し、ハードコードの高さ値を書かない。body の幅・横中心は `GetBodySurfaceBounds`(最上面)で測る。
- `GetBodySurfaceBounds` / `CreateSidewalkConnectorObject` / `MirroredMeshCache` / `MaterialCache` は static キャッシュ。引数は共有プレハブ由来の不変メッシュに限る(可変メッシュを渡すと古い結果が返る)。
- マテリアルはパス系が `RoadPath.GetOrCreateRoadMaterial`、建物系がパーツプレハブの `sharedMaterials`(R§7)。生成器ごとの `Resources.Load<Material>` / `new Material` を増やさず、URP なので Built-in 用 `Standard` を生成しない。
  - 特定バリアントだけ専用シェーダーにする唯一の口は `Resources/{マテリアル名}.mat`(例: G§16 の `Custom/PathDirt`。頂点α規約を持つメッシュ専用で他の body に流用しない)。
- 車線中心・区画線境界は `RoadLaneLayout`、LRT の軌道位置・踏面・ホーム寸法は `LrtRoadParts` が唯一の正。車両・交差点・設備側に独立した横オフセット式を書かない。
- 建物は mid レベルだけ Y を階高スケールし、base/top/fence/mashup は著作寸法のまま。配置は階高グリッドなので著作高さとのズレは隙間として出るのが仕様(F§6.3・§6.4)。`ResolveParcelContext` の区画ポリゴンは center_point 基準のローカルXZ(ワールドで扱うと敷地装飾が建物から離れる)。
- 建物のパーツが無言で消えるときは FBX の Read/Write Enabled を疑う(壁・角・prebuilt は頂点を読み、ceil/floor は参照専用なので屋根と地面だけ残る)。強制は `Assets/Editor/ModelImportSettings.cs`(`unity/AGENTS.md`「モデルアセットの構成」)。
- `a{variant}` / `a{v}s{structure}` / `landmark-{prefix}` フォルダを足したら `genesis-core/tools/generate_agent_model_config.py` を再実行する。`agent_model_config.yaml` に無いフォルダは無印・`b{bd_type}` へ黙ってフォールバックし、プレハブがあっても白い箱になる(F§3.1・§4)。
- 交差点・信号機の配置入力は PathManager の交差点署名に含まれるものだけにする(署名外の入力は変えても再生成されない)。
- `UltraFarLandMeshBuilder` は格子点ごとに `CoastlineStore.SignedDistance` を呼ばず一括版 `SampleSignedDistanceGrid` を使う。初期ロードで地形より先に走るので、遅いと `game:loaded` が出ず Tips が止まる。Mesh の組み立てはメインスレッド専用で、ビルダーは純データを返す。
- 道路系の冗長ログは `UnityLog.If(LocalConstants.UNITY_LOG_ROAD, ...)`。常時 `Debug.Log` しない。

## バックエンドとの鏡写し(片方だけ変えない)

| Unity | genesis-core(非公開) |
| --- | --- |
| `TrackPath.ResolvePlatformExtents`(`platform_side`→左右。`DIR_BACKWARD` は反転) | `geo_paths.get_platform_extension_widths`(ホームの敷地) |
| `StationHousePath` の専有エリア(`ComputeEndFootprints` / `ResolveHousePosition`) | `geo_station_house.py`(建屋の占有) |
| `TrackIntersectionMeshGenerator` の転回閾値 `RAIL_MAX_TURN_ANGLE_DEG` | `parameters.yaml` の同名値と経路探索の転回制約 |
| `Test/TestSubBuildingLayout`(テスト専用の棟配置) | `crud_buildings.generate_sub_building_layout` + `geo_buildings.generate_mashup_cells` |

## 参照先

- 仕様: `A_パス形状` / `C_建物形状` / `E_LOD処理` / `F_建物メッシュの生成` と `F1`〜`F10` / `G_道路メッシュの生成` / `J_パスの交差処理` / `N_線路メッシュの生成` / `Q_駅舎パスのメッシュ生成` / `R_マテリアルの解決` / `P_マテリアル共有とエクスポート`
- Skill: `genesis-mesh-generation`(横断)、`genesis-building-geometry`、`genesis-path-geometry`。FBX制作は `genesis-building-parts` / `genesis-path-parts` / `genesis-prop-parts`
- 実FBX回帰検証: `city-genesis/unity/Tests/`(生成器ごとのサブフォルダと README)
- `unity/AGENTS.md`「Y座標の考え方」(パスの Y は地面からの相対高さ)・「モデルアセットの構成」(FBX とミラープレハブ)
