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
| `ReclaimedLandMeshBuilder.cs` | 埋立地の岸壁を純データで返す。天端=帯の多角形(穴あり。分割は `OpenSpaceBuildingMeshBuilder.Triangulate` を共用)、面=壁の区間に沿って外側 `QUAY_PROBE_DISTANCE` の地面−1m まで下ろす縦の帯(2m 間隔)。地面の高さは関数で受け取り、シーンの状態に依存しない(D) |
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
| `RoadAccessPlan.cs` | 施設の間口を道路片側の切断区間へ変換し、接続舗装を生成する(G「施設への接続」) |
| `RoadMarkingContext.cs` / `RoadMarkingPlan.cs` / `RoadMarkingSurface.cs` | 路面標示(G§8.3、E「路面標示のLODと画像予算」) |
| `SuspensionBridgeGenerator.cs` / `BrickArcadeGenerator.cs` / `PathStructureMesh.cs` | 吊り橋251〜256・レンガアーチ高架404/414と、その結合器(G「植栽中央分離帯と道路吊り橋」、N「レンガアーチ高架」) |
| `PowerLinePath.cs` / `PowerLineSupport.cs` | 送電線1101/1102 の専用生成器と共有ノードの鉄塔所有権(G「送電線の専用描画」) |
| `ConstructionPathBuilder.cs` / `ConstructionMeshUtil.cs` | 工事中パス(G§15) |
| `RoadIntersectionMeshGenerator.cs` | 交差点(G§11、J「Unity側の交差点メッシュの再計算」) |
| `TrafficSignalLayout.cs` / `TrafficSignalBuilder.cs` / `TrafficSignalStyle.cs` | 交差点の信号機。描画専用(G§11.8) |
| `TrackPath.cs` | 線路・モノレール・LRT専用軌道431(N)。軌道の横位置 `GetTrackCenterOffset`(中央の間隔は `GetTrackCenterWidth`)、進む向きで走る軌道を決める `TrackIndexForTravel`、ホームの基準になる軌道の `PlatformTrackOffset` / `PlatformEdgeDistance`、島式ホームの駅の判定 `IsIslandPlatform` と淵ライン `IslandEdgeDistance`(N§1.1・§7.3) |
| `TrackIntersectionMeshGenerator.cs` | ポイント・平面交差のレールコネクタ。つながる軌道端の判定(`ResolveConnectedTrackEnds`)は車止めとも共有(N§4.3) |
| `TrackAdjacency.cs` | 近接線路の結合(複線見え化)・島式ホーム(N§7.4・§8) |
| `TrackDirectionArrows.cs` / `PlatformFoundationGenerator.cs` / `FarPlatformPath.cs` | 一方通行線路の矢印(敷設モードのみ。N§9)・高架ホーム外壁下部(N「高規格高架PF」)・駅と乗り場の遠景簡易立体(N§6。対象バリアントは `FarPlatformPath.Handles` に列挙。島式ホームの駅は左右の軌道から中央へ向けて組む。貨物PF 463〜465 は上屋の無い荷役床に品目別の設備を載せ、コンテナは通常パーツの16mタイルと同じ区間に並べる) |
| `LrtRoadParts.cs` / `LrtPlatformParts.cs` | LRT の軌道位置・踏面・ホーム寸法の唯一の正と、併用軌道・停留所ホーム(G「LRT付き道路」「LRT停留所のホーム」、N「LRT専用軌道431」) |
| `StationHousePath.cs` | 駅舎パス(連絡橋+両終端の建屋)(Q) |

| `Buildings/` | 役割(参照先) |
| --- | --- |
| `BuildingMeshBuilder.cs` | 入口。static 幾何ユーティリティ(`GenerateColliderMesh` / `MergeSubMeshes` / `TriangulateOrFan` / `TriangulateEarClip`=失敗時は空 / `SubdivideTrianglesByEdgeLength` / `FlipToDelaunay` / `InsertInteriorPoints` / `GenerateTerrainSurfaceMesh` / `GenerateFoundationMesh` / `GenerateEntrance` / `GenerateRoofprop`)(F§15) |
| `BuildingMeshUtils.cs` | パーツ解決(prefix・`_close`・common)・`CombineModuleMeshes`・`AddStackedModule`・`ResolveParcelContext`・`SanitizeRingPolygon`・屋根共通幾何 |
| `ModularBuildingMeshBuilder.cs` / `ModularBodyBuilder.cs` / `DefaultBodyBuilder.cs` | モジュラー躯体(壁・角の敷き詰め `BuildWallRun`、敷地装飾 `GenerateSiteDecorations`)と白箱フォールバック(F・F1) |
| `FlatRoofBuilder.cs` / `HouseRoofBuilder.cs` / `GableRoofBuilder.cs` / `ShedRoofBuilder.cs` / `InteriorFloorBuilder.cs` / `ExteriorStairBuilder.cs` | 屋根4種・各階の床(F1)、辺ごとの外階段(F§11.4) |
| `PreBuiltBuildingMeshBuilder.cs` / `MashupBuildingMeshBuilder.cs` / `PortBuildingMeshBuilder.cs` / `RailYardBuildingMeshBuilder.cs` | 既成・観光名所(F3)、複数棟(F6。割り付けはバックエンド解決済み。`openPit` のインスタンスが露天掘りの前庭の設備を描く=F11)、港(F4。区画が埋立地の岸壁の陸側で面していれば(`FindFacingReclaimedQuays`)スラブを作らずその岸壁区間を水際に、面していなければ区画の中の水際の両端を結ぶ弦を20m海側へ移した直線でカットしたスラブの辺を水際にし、`PortLayout` の計画どおり水際に荷役設備・道路側に施設モジュール・間に港ごとのヤードの荷(403 はコンテナ `port_container` の段積み、401・402・1001 は `port_cargo` を積まずに並べる。荷のパーツは各港の `a{variant}` にだけ置き、`b128` に置くと他の港へフォールバックする)を置く)、車両基地(F9。引き込み線の単線・複線は端子へつながる線路の軌道数で決まり、`ResolveLeadHalfSpacing` を BuildingManager が再利用キーに混ぜる。構内の設備は材質ごとに結合してから頂点を地形へ沿わせ、敷地全面は `a{variant}` 自身の floor で覆う=`ResolveFloorMaterialFromMaster(ownPartOnly)`) |
| `PortLayout.cs` | 港の配置計画(施設モジュール・荷役設備・ヤードの荷の置き場)。通常モデルの寸法と入力の形だけで決まる純データ(F4 §5)。荷の置き方は `CargoSpec`(コンテナは `ContainerSpec`、並べる荷は `GroundCargoSpec` がパーツの外接箱から作る) |
| `OpenSpaceLayout.cs` / `OpenSpaceBuildingMeshBuilder.cs` | 駐車場・駅前ロータリー・公園(F10) |
| `BuildingRoadAccess.cs` | 施設側の決定論的な間口(ロータリーの舗装交線・既成モデルのEmpty)。描画GOを間口の正にしない(F§16) |
| `ConstructingBuildingMeshBuilder.cs` / `ConstructionSiteProfile.cs` / `CollapsedBuildingMeshBuilder.cs` | 工事中・解体中(profile 差分で共用。F5・F7)、倒壊(F8) |

## 描画LOD(DrawLod)が生成器に届く経路

- 生成器はセルのティア表を見ない。DrawLod は呼び出し側が決め、建物は `Generate` の引数、パスは `PathDrawContext.Begin/End` で渡る。コンテキスト外の生成は Normal・全長扱い(E「物体の描画LOD」)。
- Far は専用経路。建物は `GenerateFarBuilding`(`*-prebuilt_far` が無ければフットプリント押し出しの箱。露天掘りは穴に蓋をしないよう何も描かない。港は海へはみ出す区画の箱にせず、港の生成器が岸壁と `_far` パーツを同じ配置で組み立てる)、パスは PathManager 側の帯で生成器のパーツ組み立ては走らない(例外: `FarPlatformPath`・`PowerLinePath`)。
- `_close` の解決点は2箇所だけ。建物は `BuildingMeshUtils.GetModelForDrawLod`、パスは `PathDrawContext.ResolvePrefab`(`CreateSplineMesh` で敷くパーツと送電鉄塔のみ。Instantiate 配置の tree/pillar/pole/設備やコネクタは常に無印)。新しいパーツ取得をこの2つを迂回して書かない。
- `CreateSplineMesh` はクリップ区間と `PathDrawContext` の詳細区間の共通部分にだけ敷く。セグメントごとに別スプラインを作る生成器は割合基準が合わないので冒頭で `SuppressDetailRange = true` にする(`GenerateLinearPathWithCorners` が例)。複数スプラインの生成器を新設するときも同じ。
- 曲率適応タイル長は `BuildSpline` 直後に `SetAdaptiveTileLength(ComputeAdaptiveTileLength(...))` を呼んで有効化する。`maxTileLength` に明示 `0`(著作長)を渡すパーツには適用されない。

## 知らないと事故る規約

- 隣接情報は PathManager.Awake が差す静的プロバイダ経由(`PillarClearance` / `TrackAdjacency` / `RoadAdjacency` の `NeighborProvider`、`TrackPath.ConnectedPathsProvider` / `InfrastructureConnectionProvider`、`LrtPlatformParts.StopProvider` / `PolylineProvider`、`PowerLinePath.ConnectedPaths`)。
  - 未設定のテストシーンでは結合・干渉回避が黙って無効になる。結合描画は描画専用でバックエンドに影響しない。
- SplineMesh の頂点予算 `RoadPath.SPLINE_MESH_VERTEX_BUDGET`(60000)を超えると Dreamteck が毎フレームエラーを出し続ける。新しい SplineMesh 生成経路は同じガードを入れ、構築後に `RoadPath.SealSplineMesh` を呼ぶ。封印後は点や Transform を変えても再構築されない(形を変えるなら作り直す)。
- `CreateSplineMesh` の連続する `body`・`sidewalk`・`lrt_track` は、`RemoveInternalTileCaps` が内部端面だけを除いて斜光の縞状の影を抑える。両外端は保持する。Dreamteck のタイル順・元頂点順を使うので、メッシュ結合や頂点最適化の順序を変える場合はこの前提も確認する。
- `IntersectionMeshOwner` は手続き生成メッシュと SplineMesh の生成メッシュだけを所有し(共有プレハブは登録しない)、`OnDestroy` で解放する。一度もアクティブにならなかった GO では `OnDestroy` が呼ばれないので、`SetActive(false)` より先に付ける。
- 高さオフセットは `RoadPartHeights` に集約し、ハードコードの高さ値を書かない。body の幅・横中心は `GetBodySurfaceBounds`(最上面)で測る。
- `GetBodySurfaceBounds` / `CreateSidewalkConnectorObject` / `MirroredMeshCache` / `MaterialCache` は static キャッシュ。引数は共有プレハブ由来の不変メッシュに限る(可変メッシュを渡すと古い結果が返る)。
- マテリアルはパス系が `RoadPath.GetOrCreateRoadMaterial`、建物系がパーツプレハブの `sharedMaterials`(R§7)。生成器ごとの `Resources.Load<Material>` / `new Material` を増やさず、URP なので Built-in 用 `Standard` を生成しない。
  - 特定バリアントだけ専用シェーダーにする唯一の口は `Resources/{マテリアル名}.mat`(例: G§16 の `Custom/PathDirt`。頂点α規約を持つメッシュ専用で他の body に流用しない)。
- 車線中心・区画線境界は `RoadLaneLayout`、LRT の軌道位置・踏面・ホーム寸法は `LrtRoadParts` が唯一の正。車両・交差点・設備側に独立した横オフセット式を書かない。
- 軌道数2のパス(まとめた複線・431)で走る軌道は「パスと進む向き」だけで決まる(`TrackPath.TrackIndexForTravel`。左側通行なら進行方向の左)。交差点のレールコネクタと車止めは `TrackIntersectionMeshGenerator` の同じ判定(通行方向を見る。駅のホームは両方向扱い)を使い、車両の走行描画(`Vehicles/TransitRouteGeometry`)も同じ規則で軌道を選んでレールを端点の近さで拾う。どれか1つだけ規則を変えると列車がレールの無い所を走る。
- 軌道の横位置は `TrackPath.GetTrackCenterOffset(width, lanes, i, centerWidth)` の1つの式で、中央の間隔(マスターの `center_width`。島式ホームの駅で軌道を外へ開く)は `GetTrackCenterWidth` でパスのマスターから引く。レール・架線・車止め・レールコネクタ(`LrtRoadParts.TrackLaterals`)・車両・遠景・引き込み線・敷設プレビューはすべて中央の間隔を渡してこの式を使う。1か所でも渡し忘れると、その描画だけ軌道が内側にずれる。
- ホームの部品(淵・外縁・下部外壁・端壁・遠景)は、その側で最も外側の軌道の中心線を原点に置く(`PlatformTrackOffset`)。パスの中心線基準で書くと軌道数2の駅で淵が軌道の間に出る。島式ホームの駅(`IsIslandPlatform`)は外側に張り出さず(`ResolvePlatformExtents` が0)、淵を左右の軌道から中央へ向けて置いてデッキを両淵の間に1枚張る(`CreateIslandPlatformParts`)。
- `TrackPath.CreatePlatformSide`・`CreateTerminations` は `Tests/Intersections`・`Tests/Lrt`・gallery の `PlatformCloseQA` がリフレクションで引数の数まで合わせて呼ぶ。引数を足さず、必要な値はパスのマスターから関数の中で引く。
- 建物は mid レベルだけ Y を階高スケールし、base/top/fence/mashup は著作寸法のまま。配置は階高グリッドなので著作高さとのズレは隙間として出るのが仕様(F§6.3・§6.4)。`ResolveParcelContext` の区画ポリゴンは center_point 基準のローカルXZ(ワールドで扱うと敷地装飾が建物から離れる)。
- 建物のパーツが無言で消えるときは FBX の Read/Write Enabled を疑う(壁・角・prebuilt は頂点を読み、ceil/floor は参照専用なので屋根と地面だけ残る)。強制は `Assets/Editor/ModelImportSettings.cs`(`unity/AGENTS.md`「モデルアセットの構成」)。
- `Mesh.CombineMeshes` はサブメッシュ単位の `CombineInstance` でもインデックスの最小〜最大の頂点を丸ごと写す。FBX 取り込みの溶接で別材質の面が頂点を共有するパーツでは範囲が重なり、未参照の頂点まで複製される。プレハブを事前結合するときは `CombineModuleMeshes`(サブメッシュごとに参照頂点だけを連続区画へ詰める)を通す。壁・角・エントランス・塔屋・フェンス・タイルは元メッシュを直接積むので詰め直しを通らない(F§12.1)。
- `a{variant}` / `a{v}s{structure}` / `landmark-{prefix}` フォルダを足したら `genesis-core/tools/generate_agent_model_config.py` を再実行する。`agent_model_config.yaml` に無いフォルダは無印・`b{bd_type}` へ黙ってフォールバックし、プレハブがあっても白い箱になる(F§3.1・§4)。
- 交差点・信号機の配置入力は PathManager の交差点署名に含まれるものだけにする(署名外の入力は変えても再生成されない)。
- 港が埋立地の岸壁に面しているかは `PortBuildingMeshBuilder.FindFacingReclaimedQuays` の1つの規則(区画を0.1m膨らませた範囲に掛かる岸壁区間が1mより長く、区画がその岸壁の陸側にある=区間から内向き `QUAY_PROBE_DISTANCE` の帯の半分以上を覆う)で決め、岸壁区間は `ReclaimedLandStore.GetQuaySegments`(バックエンドの派生値)から取る。港側で岸壁を求め直さない。既存の港の隣の埋立(岸壁の海側にある区画)は面していないので直線カットのスラブのまま。
  - 直線カットの弦の端は、底面外周と海岸線の交点ではなく区画の中の水際が外周に届く点(`CollectQuayEndCandidates`)。隣の埋立地が区画の辺や角に外から接すると、その辺上の交点で弦が斜めになるため。区画の中に陸が無い(海岸線に外から接するだけの)区画だけ交点をそのまま使う。
  - `FindFacingReclaimedQuays`・`BuildQuayFootprint`(private。`CoastlineStore` に海岸線を入れて呼ぶ)・`ReclaimedLandMeshBuilder.Build` は `Tests/Quay` がリフレクションで名前で呼ぶので、改名・多重定義しない。
  - 入港船(`Vehicles/AmbientVehicleManager.PortCalls.cs`)は `ResolveWorldWaterlines` で同じ規則の水際をワールド座標で受け取る。水際の決め方を変えたら両方に効くので、描画側だけの分岐を足さない。
- 港の配置(`PortLayout`)は通常モデル(無印)の寸法で決め、`_close`/`_far` のパーツは同じ位置に置くだけにする。施設モジュール(`port_building`)の置き方は区画と通常モデルだけから決め、海岸線・埋立地に依存させない。`BuildingRoadAccess.Resolve` が建物データの登録時に同じ `PortLayout.PlaceModule` でマーカーを建物中心基準へ移して間口の索引を作るため、描画だけ別の置き方にすると道路の接続面と建物がずれる。
  - 施設モジュールの接地と開口の舗装高の下限はどちらも海面+2m(`PortBuildingMeshBuilder.QUAY_TOP_HEIGHT`)。片方だけ変えると、道路の接続面と施設の舗装に段差ができる。
- `UltraFarLandMeshBuilder` は格子点ごとに `CoastlineStore.SignedDistance` を呼ばず一括版 `SampleSignedDistanceGrid` を使う。初期ロードで地形より先に走るので、遅いと `game:loaded` が出ず Tips が止まる。Mesh の組み立てはメインスレッド専用で、ビルダーは純データを返す。
- 道路系の冗長ログは `UnityLog.If(LocalConstants.UNITY_LOG_ROAD, ...)`。常時 `Debug.Log` しない。

## バックエンドとの鏡写し(片方だけ変えない)

| Unity | genesis-core(非公開) |
| --- | --- |
| `TrackPath.ResolvePlatformExtents`(`platform_side`→左右。`DIR_BACKWARD` は反転。軌道数2以上は両側、島式ホームの駅は0) | `geo_paths.get_platform_extension_widths`(ホームの敷地) |
| `TrackPath.IsIslandPlatform`(駅・軌道数2以上・`center_width` が正) | `MasterData.is_island_platform_variant` |
| `StationHousePath` の専有エリア(`ComputeEndFootprints` / `ResolveHousePosition`) | `geo_station_house.py`(建屋の占有) |
| `TrackIntersectionMeshGenerator` の転回閾値 `RAIL_MAX_TURN_ANGLE_DEG` | `parameters.yaml` の同名値と経路探索の転回制約 |
| `RailYardBuildingMeshBuilder.TryGetRectangleShortSideMidpoint`・`ResolveTerminalNodeLocal`(端子未ロード時の初期位置。線路の向きの基準にもなる) | `crud_agents._rectangle_short_side_midpoint`・`_connection_node_position`(接続端子の初期配置) |
| `Test/TestSubBuildingLayout`(テスト専用の棟配置。露天掘りの前庭の絞り込みを含む) | `crud_buildings.generate_sub_building_layout` + `geo_buildings.generate_mashup_cells` + `geo_open_pit.open_pit_yard_cells` |
| `Tests/Quay/QuayQA.cs` の造成の書き込み規則(埋立地の中で岸壁から `QUAY_KEEP_WIDTH` を超える1m格子点を天端に)と地形タイルの再標本化(500/512m 間隔の双線形) | `geo_grading.compute_grading_heights` + `geo_reclaimed_land` の keep 領域、`terrain_surface.tile` |

## 参照先

- 仕様: `A_パス形状` / `C_建物形状` / `E_LOD処理` / `F_建物メッシュの生成` と `F1`〜`F11` / `G_道路メッシュの生成` / `J_パスの交差処理` / `N_線路メッシュの生成` / `Q_駅舎パスのメッシュ生成` / `R_マテリアルの解決` / `P_マテリアル共有とエクスポート`
- Skill: `genesis-mesh-generation`(横断)、`genesis-building-geometry`、`genesis-path-geometry`。FBX制作は `genesis-building-parts` / `genesis-path-parts` / `genesis-prop-parts`
- 実FBX回帰検証: `city-genesis/unity/Tests/`(生成器ごとのサブフォルダと README)
- `unity/AGENTS.md`「Y座標の考え方」(パスの Y は地面からの相対高さ)・「モデルアセットの構成」(FBX とミラープレハブ)
