# DataLoaders フォルダ

バックエンドから取得した都市データ(地形・区画・建物・道路・地区・資源・プロップ)をUnityのオブジェクトへ変換し、差分更新・描画LOD・寿命を管理する。
マップをセル(一辺 `TerrainManager.CellSize`)に区切り、`LodConfig` が選ぶ最大81の常駐セルだけを詳細に描く。起点は `CellDataLoader.cs`。メッシュ形状は `MeshGenerators/`、見た目の切替は `VisualHandlers/` の責務。

## ファイル一覧

| ファイル | 役割 |
| --- | --- |
| `CellDataLoader.cs` | 入口。カメラセル変更・`ReloadAllData`(エポック完了/編集確定)・全消去を受け、`LoadBatch` を各マネージャへ配る |
| `BaseApiDataManager.cs` / `LoadBatch.cs` | マネージャ基底 / ロード1回分の取消しトークン |
| `LodConfig.cs` / `LodViewState.cs` / `DrawLod.cs` / `DrawLodStats.cs` | 常駐セル集合 / カメラ変化検出 / 描画LOD判定 / F8統計 |
| `RenderWorkScheduler.cs` / `PerformanceConfig.cs` / `RenderObjectCache.cs` / `MeshBakeService.cs` | 共通フレーム予算ポンプ / 予算値 / 非表示GOのLRU退避 / 非同期コライダーベイク |
| `TerrainManager.cs` / `TerrainManager.Background.cs` | 地形タイルのストリーミングとLRU・超遠景・海岸線と水面の取得・地形の版API / 都市外の背景地形 |
| `TerrainTileResources.cs` / `HeightmapTextureManager.cs` / `GroundCoverPalette.cs` | タイル専用資源 / GPU高さ窓 / 地表種別の見た目 |
| `CoastlineStore.cs` / `RiverStore.cs` / `WaterSurfaceStore.cs` / `WaterSurfaceManager.cs` / `RiverSurfaceManager.cs` | 海岸線・川・湖の静的ストア(湖は `WaterSurfaceManager` が水面の生成時に登録) / 湖の水面 / 川の水面と川床マスク |
| `ReclaimedLandStore.cs` / `ReclaimedLandManager.cs` | 埋立地の静的ストア(帯の天端 `TryGetBandTop`・港向けの岸壁区間 `GetQuaySegments`・中身の版 `ContentVersion`) / 岸壁(天端と垂直の面)の GameObject・地形の版による作り直し・`OnContentChanged` |
| `PathManager.cs` / `SplinePartLod.cs` / `RoadMarkingAtlas.cs` | 道路・線路・ノード・交差点 / 長い道路の区間LOD / 路面標示画像の予算 |
| `BuildingManager.cs` / `BuildingExteriorRenderer.cs` / `FarBuildingBatcher.cs` / `ConstructionCraneAnimator.cs` | 建物 / 敷地装飾 / 遠景結合 / クレーン演出 |
| `BuildingRoadAccessIndex.cs` | 建物データに同期する道路間口と配置計画の索引。GOのアンロードでは消さず、実削除・全消去で解除。変更時はPathManagerがセル逆引きで道路の描画版と再生成キューを更新する |
| `ParcelManager.cs` / `ZoneManager.cs` / `ResourceManager.cs` | 区画 / 地区 / 天然資源ヒートマップ |
| `PropManager.cs` ほか `Prop*.cs` / `StableSample.cs` / `ForestRenderer.cs` / `ForestOverview.cs` | プロップ(下記) |
| `AlertManager.cs` / `FireEffectManager.cs` | アラートアイコン / 火災パーティクル |
| `TransitOverlayManager.cs` / `BusStopManager.cs` / `LrtStopPlatforms.cs` | 路線帯と停留所名 / バス停上屋 / LRTホームの索引 |
| `InterchangeManager.cs` / `RoadManager.cs` / `CityInfoStore.cs` | IC名ラベル / 道路名ラベル(道路自体は PathManager) / `traffic_hand` |

## ロードの流れ(順序を変えない)

1. `TerrainManager.FetchCoastline` が `/terrain/settings` → 地表種別・海岸線・湖・川・埋立地を読む(初回・強制リロード・毎回の `session:reload`。海岸線は埋立の完了で広がる)。マップ寸法より先に常駐集合を算出するとマップ外セルを要求し、`/terrain/surface` の400再試行から抜けられない。`CoastlineStore.Version` は海岸線を入れた時点で進み、埋立地まで取り終えた版は `TerrainManager.ShoreDataVersion`。
2. 地形タイル → 既存物の地形追従 → 区画(完了待ち) → 建物・道路 → 地区・資源・プロップ(`LoadObjectManagers`)。海上区画の建物の接地高が区画に依存する。
3. `session:reset` から `session:reload` までカメラ由来のロードは無視する(準備中の都市のタイルは同版の差分で更新されない)。

## 知らないと事故るポイント

### 共通
- 生成コルーチンは `RenderWorkScheduler.Start/Stop` で起動・停止する(素の `StopCoroutine` は finally を走らせず資源が漏れる)。協調中断は `yield return RenderWorkScheduler.Continue`、`yield return null` は通信ポーリング等の次フレーム待ちだけ。
- `StartCoroutine` の子は yield ごとに `LoadBatch.IsCanceled` を確認する。追従コルーチンの子は `IEnumerator` を直接 `yield return`。例外は地形タイルのジョブ(寿命はセルの常駐)。
- 常駐ワーカーの `hasPendingWork` は「今選出できる仕事」だけを返す。省略・過大申告で `IsInitialSceneReady` が偽のままになり `game:loaded` が出ない。周期的に再起動する有限コルーチンは `() => false`。
- `LodViewState` は1インスタンス1メソッド(`Update` / `UpdateProjection` / `UpdatePosition`)。混ぜると変化を取りこぼす。
- 地形の版: 生成時に `TerrainManager.GetTerrainSignature` / `GetSurfaceStamp` / `GetAttachedTerrainVersion` を記録し、`OnTerrainTileReady` で一致すれば作り直さない(LRU復元でも通知は来る)。版が不明なら省略しない。
- `RenderCacheIdentity` には生成メッシュだけを所有させる(共有アセットは退避の追い出しで壊れる)。
- 大量の描画物で `Renderer.material(s)` を個別複製しない。建物は `BuildingManager.GetSharedDepthOffsetMaterials`、道路の帯・ノード円柱は `GetFarRoadDepthMaterial` / `GetNodeDepthMaterial`。色替えは `VisualModeObject`。
- 地形マテリアルを `new Material(既存)` で複製しない(Properties 未宣言の手続き色が落ちる)。遠景は `ApplyFarTerrainMaterialSettings` で直接設定する。
- HTTPは `ApiClient`(`GetBulk`=別スレッドデコード+MessagePack / `Get`=小さい単体)。`GetBulk` は `isCanceled` が真になるとコールバックを呼ばないので、取得中フラグは finally で外す。
- 地形バイナリ(`/terrain/far`・`/terrain/surface`・`/terrain/background`)も `ApiClient.Request` → `ApiRequest.Send(isCanceled)` で送り、応答ヘッダーと本体は `.Web` から読む。取消しは `Aborted` で判定する。`UnityWebRequest` を直接作ると認証ヘッダーが付かず401になる。
- `GeneratedModels.cs` は自動生成物なので編集しない。
- `LodConfig.CalculateCellSets` にオーバーロードを足さない(`Tests/Lod/LodQA.cs` がリフレクションで呼ぶ)。`ParcelManager.RegisterParcelDataForLines` も VehicleQA が名前で引くので多重定義にしない。

### 地形 (TerrainManager)
- `terrainTiles` を書き換えたら `loadedCellMaskDirty` を立てる(UltraFarの沈み込みマスクはこれでしか作り直さない)。LRU保持中のタイルは `terrainTiles`・`HasSurfaceTile`・`GetAttachedTerrainVersion` に現れない。
- `OnAllTerrainLodLoaded` は全タイルの `OnTerrainTileReady` の後に発火する契約。
- 地表種別の値の正は `Resources/Terrain/GroundCoverPalette.asset`、反映は `GroundCoverProfile.ApplyTo` だけ。
- 川の「範囲内の水面標高 = プロファイル線上の最近点のy」は地形エディタ・バックエンドと共通の契約(`RiverStore`)。
- 埋立地の天端の高さ・岸壁区間・壁・帯は `GET /terrain/reclaimed-lands` の派生値をそのまま使い、Unity で判定しない(`CoastlineStore.IsLand` で岸壁を求め直さない)。取得のたびに中身を比べて変わった埋立地だけ作り直し、先に消さない(湖・川の「消してから取得」を写すとエポックごとにちらつく)。`ContentVersion` は中身が変わったときだけ進む(`CoastlineStore.Version` は取得のたびに進むので版に使えない)。
- 帯の中の接地は `Utils.GetGroundHeight` が max(地形, 天端) で返す。ストアの更新・消去(`ReclaimedLandStore.Replace` / `Clear`)は `Utils.InvalidateHeightCache()` を伴う。岸壁の GameObject は背景地形のチャンクと同じ構成(Ground レイヤー、`MeshFilter`→`MeshRenderer`→`MeshCollider`→`VisualTerrainObject`→`TerrainInputHandler` の順、タグ無し)。

### 道路 (PathManager)
- キャッシュ操作はヘルパー経由: パス=`SavePathToCache` / `RemovePathFromCache`(`pathsByNodeIndex`・通過セル・工事音を同期)、GO=`SetPathObject` / `RemovePathObject`、ノード=`nodeDataCache` 書込み時に `IndexNodeCell`。
- `SavePathToCache` 内は「`pathDataCache` 更新 → `RegisterPathCells` → `SyncConstructionSound`」の順(工事音の先端点は新しい通過セルの地形署名で計算する)。version が変わったパスは `RegisterPathCells` 後にティア判定。隣接再生成(近接線路・高速ランプ)のエンキューは `UnregisterPathCells` より前。
- ノード削除時は `nodeGrid` からも除去する(残すと削除済みノードへ吸着)。`ClearAllCells` では `nodeGrid` / `segmentGrid` を `SpatialHash.Clear()` し `NodeDataVersion` を進める(道路エディタの背景オーバーレイはこのカウンタでしか作り直されない)。
- ロード中(`isLodPathBatchActive`)に `CreateOrUpdatePath` を直接呼ばない。交差点更新と交差点由来の再生成は延期集合へ、切断点の変化は `OnIntersectionCutChanged` から再生成キューへ(必ず `DeterminePathDrawLod` を通る)。
- 地形追従・隣接通知・切断点変化による再生成の投入は `EnqueueTerrainFollowPath(id, serial, reason)` に一本化(視野優先ワーカー)。ハンドラ内で同期一括再生成しない。
- 交差点ノード取得中フラグ `fetchingIntersectionNodeIds` は取得コルーチンの finally で外す(残ると後続バッチが永久に待ち、地形追従が止まる)。
- 交差点生成器(`NodeMeshFactory.GenerateNodeMesh` / `GetIntersectionCutLimit`)に外部依存を足したら入力署名 `ComputeNodeIntersectionSignature` にも足す。接続数が `MAX_INTERSECTION_CONNECTED_PATHS`(32)超のノードはO(N²)フリーズ防止で生成しない。
- 線路端の車止めは線路のメッシュの一部で、ノードでつながる軌道端(接続パスの種別・状態・通行方向・向き)で決まる。切断点が変わらなくても、標示の署名 `RoadMarkingRules.JunctionSignature` が変わると `markingsChanged` で接続パスを作り直すので、これが車止めの作り直しも担う(署名から通行方向などを外さない)。
- 道路GOを `SetActive(true)` に戻す経路を足したら `EnsureEndIntersectionsVisible` を呼ぶ(交差点メッシュは道路の子ではない)。
- `CalculatePathPoints` の制御点自動計算(Catmull-Rom、テンション1/6)はバックエンド `Path._auto_fill_controls` と同一式。変えるなら両方。
- インフラノード(`InfraNetworkUtil.IsInfraMask`)には交差点アスファルトも端点切断もしない(端点Yとノードの不一致=垂直結合が正規状態)。端子ノードの所属施設は `BuildingManager.TryGetInfraTerminalOwner`(ノードAPIは owner を持たない)。
- 仮の `PathSummary` を生成器へ渡すときは `follow_terrain = Constants.VALUE_NONE` を明示する(int既定の0は「非追従」)。`station_grade_profile` は駅以外にも付くので `ApplyStationGrade` をパス種別で限定しない。

### 建物 (BuildingManager)
- 見た目に効くフィールドを足したら `HashBuildingShape` にも足す(再利用キー `BuildingRenderKey` の形状ハッシュ)。
- 車両基地(MODEL_RAIL_YARD)の引き込み線は端子へつながる線路の軌道数で単線・複線が変わるが、建物の version は変わらない。再利用キーの詳細LODに `RailYardBuildingMeshBuilder.ResolveLeadHalfSpacing` を混ぜ、PathManager が線路パスの登録・version 変化・削除のたびに両端の端子へ `OnDepotTerminalPathsChanged` を呼ぶ(キーが変わった表示中の基地だけ作り直す。F9§8)。
- 港(MODEL_PORT)は区画が埋立地の岸壁に面するかで形が変わるが、埋立で version は変わらない。再利用キーは港だけ全LODで `ReclaimedLandStore.ContentVersion` を混ぜ(遠景も岸壁と `_far` パーツの組み立て)、`ReclaimedLandManager.OnContentChanged` を購読する `OnReclaimedLandsChanged` が逆引き `portBuildingIds` から表示中の港を作り直す(遠景は即時、詳細の視野外は遅延キュー)。`portBuildingIds` の登録・削除は他の逆引きと同じ経路(`RegisterBuildingDataToCache` / `SyncBuildingsInCells` / `ClearAllCells`)。
- 配置Yは `ResolveBuildingBaseHeight` の1箇所。陸上は model_type によらず `center_point` の地形標高、海上区画(`PARCEL_WATER`)だけ `parcel.altitude`(地形をサンプルすると海底が返る)。区画側の同じ入口は `ParcelManager.GetParcelBaseY`。
- `parcel.altitude` は海面基準の絶対標高(接道の路面標高、接道なしは区画中心の地形標高)。パス・ノードのYは地面からの相対高さなので混ぜない。陸上の建物の接地に使うと、区画が分かる経路(プレビュー等)と分からない一括ロードで高さが食い違う。
- `buildingDataCache` への登録は `RegisterBuildingDataToCache`(逆引きと工事音を同期)、表示物セルは `RegisterBuildingCell(id, pos, shown)`(データだけなら `shown:false`)、詳細化の遅延投入は `QueueDeferredDetail`(遅延中は version をキャッシュしない)。
- メッシュを作り直す目的なら `CreateOrUpdateBuildingObject(..., forceRebuild: true)`(false はプール再利用で早期return)。`LoadMergedBuildings` 中は `cellBuildingLod` でなく desiredTier を渡す。
- 詳細数上限(`maxDetailedBuildingObjects`)のLRU破棄では、表示可能セルの建物を必ず Far の箱に置き換える(置かないと再入場まで消える)。
- `buildingIdsByParcel` は一括ロード・単体再取得(`FetchBuilding` → `GET /buildings/{id}`。一覧の1要素と同形式)の `multi_agent` スキーマに `parcel_id` が無いため実質空。区画→建物は `ParcelManager.GetParcelIdAtPoint` などの空間判定で解く。

### 区画・地区 (ParcelManager / ZoneManager)
- 区画の `land_price` / `front_path_id` は version が同じでも変わる(`VERSION_TRIGGER_PROPS` 外)。版一致時も `RefreshCachedSummary` で更新する。
- 枠線・ホバーは `_ZTest=Always` の `OverlayMeshRenderer`、色分けフィルは別レンダラー `dataVisRenderer`(`Custom/OverlayFillTerrainConform`、GPU地形追従)。CPU頂点サンプルの平板を深度テストありで描く方式は禁止(傾斜地で欠ける)。`BeginFrame/EndFrame/ClearMesh` は2レンダラー対で呼ぶ。
- 地区はカメラ由来のロードでは未ロード時と強制時しか取らない。`ReloadAllData` からの `RequestZoneRefresh()` を外すと地区表示が永久に更新されない。編集確定は `ReloadAllZonesForced()`。

### プロップ
- 仕様の正は `M_地形プロップ.md`。分担: `PropManager`(候補・小区画バッチ・`RenderMeshInstanced`)、`PropGeometry`(穴付き領域と固定格子ハッシュ。`ForestPlacement.compute` と同一規則)、`ForestRenderer`(種類1/2のGPU散布)、`ForestOverview`(全域被覆マスク)。
- `PropRenderPolicy`(種類別の距離・影)、`PropRenderModel`(共有FBXから複製材質と `_close` / `_far` を抽出。専用モデルを読んだかは `DedicatedClose` / `DedicatedFar`)、`StableSample`(固定順位抽出)。
- `Forest.shader` は Forward+ の `_CLUSTER_LIGHT_LOOP` バリアント必須(欠くと太陽光の陰影が消える)。GraphicsSettings の Instancing Variants は Keep All を維持する。

### その他
- アラート: `alert` は version 管理外なので毎回全件走査。表示マスクは下位バイト=agent、上位バイト=path(`ALERT_PATH_MASK_SHIFT`)、廃墟は表示専用ビット `ALERT_DISPLAY_RUIN`。
- アイコン対象ビットの正は `consts.py` の `ALERT_*_ICON_BITS`(Unity側で再宣言しない)。火災2ビットは `FireEffectManager` へ委譲し、応答をアイコン準備より先に渡す。火災エフェクトは建物GOの子にしない(詳細数上限で建物GOが消える)。
- `InterchangeManager` / `RoadManager` は `ReloadAllData` の `RequestRefresh` で取り直す(外すと竣工ランプのICが出ない)。`GET /roads/` は末尾スラッシュ必須。カメラ移動では取らない。
- 公共交通の路線一覧の取得主体は `Vehicles/TransitDataStore`(`TransitOverlayManager` / `LrtStopPlatforms` は購読側)。

## 参照先(`city-genesis/docs/docs/` 配下)

- 差分ロード・地形の版・計測カウンタ・`game:loaded`: `geometries/K_Unity差分ロード整理.md`。更新トリガー・マネージャ別動作: `geometries/H_バックエンドデータロード.md`
- 常駐範囲・地形ストリーミング・描画LOD・共通予算・地表の見た目: `geometries/E_LOD処理.md`。地形・湖・川・地表種別・背景: `geometries/D_地形形状.md`
- 道路: `geometries/G_道路メッシュの生成.md`、`J_パスの交差処理.md`、`N_線路メッシュの生成.md`、`Q_駅舎パスのメッシュ生成.md`。プロップ・バス停: `geometries/M_地形プロップ.md`
- 車両・公共交通・索道・路駐: `geometries/O_車両の走行描画.md`、`programs/公共交通ハイライト.md`。火災・アラート: `geometries/S_火災エフェクトの描画.md`、`programs/アラートフラグ管理.md`
- IC・道路名・IPC・制限速度標識: `programs/道路の詳細と道路名.md`、`programs/IPC連携の整理.md`
- 隣接: `MeshGenerators/AGENTS.md`(生成器・信号機・LRT・高速合流)、`VisualHandlers/AGENTS.md`(表示フィルタ・標識)、`Weather/AGENTS.md`(工事音)、`Vehicles/`、`InputHandlers/`。命名・ログ規約は `unity/AGENTS.md`
- Skill: genesis-terrain-lod、genesis-path-geometry、genesis-building-geometry、genesis-district-parcel-geometry、genesis-prop-parts、genesis-visual-mode、genesis-mesh-generation、genesis-ipc
- 検証: `Tests/Lod/run.ps1`、`Tests/InitialLoading`、`Tests/TerrainAppearance`、`Tests/Props`、`Tests/Intersections`、`Tests/Lrt`
