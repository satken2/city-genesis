# DataLoaders フォルダ

バックエンドから取得したデータをUnityのオブジェクトに変換する処理を定義する。
都市全体を一度に描画すると重すぎるため、マップをセル(一辺 `TerrainManager.CellSize`、バックエンドの `/terrain/settings` から取得)に区切り、カメラ周辺のセルだけを詳細に描画する。起点は `CellDataLoader.cs`。

## 地形・取得範囲・描画LOD

`LodConfig` は視点・注視点・視錐台から最大81セルを選ぶ。`farCells / nearCells / dynamicCells` は呼出し互換の名前で、現在はすべて同じ常駐セル集合。土地の開放状態で標高ソースや描画LODを変えてはいけない。

`CameraCellChecker` は0.1秒間隔で位置、向き、FOV、正投影、画面条件を調べる。視線中心はy=0平面との交点。`ResetViewState` で検出状態を未初期化に戻すと静止中でも次の判定で必ずセル通知が出る(`CellDataLoader.ClearAllManagerData` と `ReloadAllData` の現在セル未設定分岐が呼ぶ)。`session:reset` 後は `session:reload` が届くまで `OnCameraCellChanged` を無視する(`ClearAllManagerDataForSessionReset` / `loadSuspendedUntilReload`。バックエンドが新しい都市を準備中のためで、準備中のタイルを取り込むと同じ版の差分リロードで更新されない)。`CellDataLoader` は旧 `LoadBatch` を取消し、地形取得・適用→既存オブジェクトの地形追従→区画取得完了→建物・道路・地区・資源・プロップ取得の順で進める。更新セルだけを追従させ、取消しで残ったセルは次回へ引き継ぐ。版情報・タイルの取得失敗は2秒後に再試行し、必要タイルの適用前に後続生成へ進まない。

地形は `/terrain/surface` のfloat32を513格子の一時TerrainDataへ分割適用し、完了時にタイルを差し替える。途中の帯を表示中タイルへ書かない。取消しでは一時データを破棄する。`SetHeightsDelayLOD` と最後の `SyncHeightmap` を使い、隣接タイルを接続する。全域のCPU高さ配列、旧far/near/dynamic読み分け、ティア境界ブレンドは使用しない。超遠景は全域の陸地メッシュ。

## 共通生成予算・キャッシュ

生成・適用ワーカーは `RenderWorkScheduler.Start(owner, routine, category)` で起動し、停止も同クラスの `Stop` を使う。Unityの `StopCoroutine` だけではiteratorのfinallyが実行されないため、独自のStart/Stop対で待ち行列と一時メッシュを解放する。GOの無効化・破棄では `RenderWorkLifetime` が停止する。子処理は `LoadBatch.IsCanceled` も確認する。

`PerformanceConfig.FrameBudgetMs` は全生成器の同期処理に共通（目標フレーム時間の25%、最低2ms）。各処理は `SliceBudgetMs` または1件ごとの境界で `yield return RenderWorkScheduler.Continue` により協調中断する。共通ポンプが実行可能な処理を順番に進め、予算内なら同じフレームでも再開する。`yield return null` は次フレーム待ちなので、通信のポーリング・アイドル・周期待ちだけに使う。UnityのYieldInstruction/CustomYieldInstruction/AsyncOperation/Coroutineはネイティブ待機へ委譲し、その待機中の処理は実行順を塞がない。ポンプはRenderWorkLifetime.LateUpdateから1フレームに1回実行し、同期処理の合計に加えてポンプ全体の経過時間も共通予算で制限する。1回のUnity API呼び出しは中断できないため、F8の最大step時間も確認する。

`RenderObjectCache` は非表示GOのLRU。建物256MiB、道路128MiB、道路パーツ8MiB、全キャッシュ合算512MiBの推定上限。生成Meshと固定管理費を数え、表示中GO・共有FBX・GPU複製は別。`RenderCacheIdentity` は生成Meshのみ所有し、共有アセットを破棄してはいけない。セッション終了時は各キャッシュをClearする。

描画LODは `DrawLodEvaluator` で独立に決める。建物・プロップは投影サイズ（正投影対応）、道路は中心線・区間までの距離。`LodViewState` をカメラ変更検出に使う。F8はLOD個数、生成時間、p95/p99、待ち数、キャッシュ推定量、プロップの影数を表示する。

## version差分更新の慣習

建物・道路一覧は500件ずつcursorで最後まで取得してから削除差分を適用する。途中ページの失敗を空の完成一覧として扱わない。独自レスポンスの `next_cursor` を使い、自動生成 `GeneratedModels.cs` は編集しない。

建物の再利用キーには形状・外観・状態・区画と地形版を含める。道路はデータ版、描画LOD、強制再生成の形状revisionを使う。地形が変わっていない単なるカメラ移動でforce再生成してはいけない。

## 地形の関連機能



## 道路 (PathManager.cs) — 知らないと事故るポイント

- **ノードキャッシュの実変更カウンタ `NodeDataVersion`**: ノードの新規追加・位置/タイプの実変更・削除があった場合にのみインクリメントされる公開カウンタ(カメラのセル跨ぎ再取得は大半が無変更のためインクリメントしない)。ノードは編集モード進入後にも非同期ロードで到着するため、`RoadPlanManager` が背景オーバーレイ(既存ノード表示)の再構築判定にポーリング比較で使う。ノード削除時(`SyncNodesInCells` / `SyncNodesInCell`)は `nodeGrid`(空間ハッシュ)からも必ず除去すること(残すと削除済みノードへ吸着してしまう)。全消去時(`ClearAllCells`)も同様に `nodeGrid` / `segmentGrid` を `SpatialHash.Clear()` で空にし、`NodeDataVersion` をインクリメントして背景オーバーレイの再構築を促すこと(カメラ静止中はこのカウンタの変化でしか作り直されないため、通知しないと消えたはずのパス線が残る)。
- **インフラ施設の接続端子ノードの識別は BuildingManager の逆引きで行う**: ノードAPIは所属施設(owner)を公開しない「agent→nodeの片方向参照」の設計のため、`NodeData` に所属施設のプロパティは無い。端子判定は `BuildingManager.TryGetInfraTerminalOwner(nodeId)`(建物側の `infra_node_id` から構築する逆引きインデックス)を使う(`CreateOrUpdateNode` の NodeInputHandler 付与・道路エディタの端子マーカーが使用。なおノードが「インフラノードか」自体は `type` にインフラ財ビットが立っているか=`InfraNetworkUtil.IsInfraMask` で判定できる)。
- ノードとパスはマージBBOXで一括取得(`LoadMergedNodes` / `LoadMergedPaths`)。パスのティアは `DeterminePathLodTier` が「そのパスが覆う全セル」の最高ティアで決める(セル跨ぎ対応)。通過セルは登録済みキャッシュ `pathCellsById` を参照する(`GetPathCoveredCellsCached`)。**version が変わったパスは形状が変わった可能性があるため、`RegisterPathCells` でキャッシュを更新してからティア判定すること**(LoadMergedPaths のフェーズ2はこの順序になっている)。
- **交差点接続パスのO(1)ルックアップ**: 交差点更新時の全パス線形走査を避けるため、`pathsByNodeIndex`（ノードID -> 接続パスIDのHashSet）インデックスが維持されている。キャッシュへの追加・削除は必ず直接 `pathDataCache` を操作せず、`SavePathToCache` / `RemovePathFromCache` ヘルパーを通すこと。
- **地形更新時の全件走査回避**: 地形Ready時に交差するパスを再生成する `OnTerrainTileReadyHandler` では、`pathSegmentsByPathId` の全件走査を避け、すでに通過セル情報を持っている `pathIdsByCell` から影響を受けるセルのパスIDのみに絞り込んでセグメント判定を行う。
- **地形更新起因のパス再生成は視野優先・フレーム予算制のキューで行う**: `OnTerrainTileReadyHandler` はハンドラ内で同期一括再生成せず、`queuedTerrainFollowPathIds` へ積む。常駐ワーカー `TerrainFollowRebuildWorker` が「視野内かつカメラに近い」パスから順に、再生成時点の現在ティア・カメラ位置(描画LOD)で `TryRebuildPathOnce` する(ロード範囲外に出たパスは再生成しない)。描画LODの再判定(`PathDrawLodReevaluationWorker`)もこのキューに積む。地形ロード完了後の一括追従 `RebuildPathShapesForCellsCoroutine` も、対象パス・交差点ノードを視野優先スコアで昇順ソートしてから処理する(1本の再生成が重く1フレームに1〜数本しか進まないため、「カメラに映っている道路から地形に馴染ませる」順序が体感品質を決める)。
- **パスの描画LODと遠景の帯**: `DeterminePathDrawLod` が距離から決める。256m以上の通常道路は連続パーツを `SplinePartLod` が固定区間化し、区間単位でLODを選ぶ。動く詳細窓は使用せず、ルートのdetailFrom/Toは0..1。Farルートの帯と交差点の再生成規則は維持する。強制更新で形状revisionを進め、単なるLOD切替はキャッシュを使う。
- **交差点と道路端の相互依存**: ノードごとに `NodeMeshFactory`(→`RoadIntersectionMeshGenerator`)が交差点ポリゴンを生成し、`ConnectionPoints`(各接続道路の切断点)を返す。PathManager はこれを `pathStartConnectionPoints` / `pathEndConnectionPoints` にキャッシュし、`CalculatePathPoints` が道路の端点をこの切断点に差し替える。切断点が変わった道路は再生成される。
- **LOD一括更新中(`isLodPathBatchActive`)の遅延処理**: 上記の依存関係のため、一括ロード中は交差点更新を `deferredLodIntersectionNodeIds`、交差点由来の道路再生成を `deferredIntersectionPathRebuildIds` に積み、全パスのデータキャッシュが揃った後のフェーズで処理する。`lodBatchRebuiltPathIds` で同一パスの重複再生成を防ぐ。**この仕組みを迂回してロード中に直接 `CreateOrUpdatePath` を呼ばないこと。**
- 道路の形状計算(`CalculatePathPoints`): すべて三次ベジエ。制御点が未指定のアンカーは Catmull-Rom 接線(`T_i = P_{i+1} − P_{i-1}`、テンション1/6、端点は片側接線。ウェイポイントなしは1/3・2/3内分=直線)から自動計算する。**これはバックエンド `Path._auto_fill_controls` と同一ロジックであり、変える場合は両方変える**(バックエンドは作成・形状更新時に未指定の制御点を同式で埋めて永続化し、ウェイポイントの制御点はAPIの`waypoints`で、端点の制御点は`start_control`/`end_control`で返す。Unityは保存値を優先使用し、無い場合のみ自前計算にフォールバックする。端点制御点の読み取りは`PathSummaryExtensions.GetStartControlList/GetEndControlList`=リフレクション方式で、endpoint_schemasシート未更新でもコンパイルが落ちない。絶対座標化は制御点直下ではなく所属アンカー直下の地形標高を基準にする=`EndpointControlToWorld`/`PathWaypoint.GetControlInAbsolute`)。約1m間隔(`TERRAIN_SAMPLE_INTERVAL`)でサンプリングし、相対Y=0 の区間は各サンプル点を地形高さ+`RoadPartHeights.ROAD_SPLINE_OFFSET`(0.1m)に追従させる(Y座標の考え方はルートAGENTS.md参照。道路パーツの高さオフセット体系は `MeshGenerators/Paths/RoadPartHeights.cs` に集約されている)。交差点の切断点も同じ基準面のYで返ってくる。ノードの相対Yは `UpdateNodeIntersectionMesh` が `NodeMeshFactory.GenerateNodeMesh` へ渡し、本線のサンプルを渡す経路では高架も切断位置の実際の高さ・接線で接合される。サンプル未指定の仮想ノードだけが水平面モードを使用する(MeshGenerators/AGENTS.md参照)。交差点歩道コネクタの実体化は `RoadPath.CreateSidewalkConnectorObject`(共通静的メソッド)を使う。
- **線路ノード(ポイント・平面交差)のレールコネクタ**: `NodeMeshResult.RailConnectors`(転回制約を満たす通行可能な線路ペアにのみ計算される。MeshGenerators/AGENTS.md の「線路」の節参照)を `UpdateNodeRailMesh` が NodeMesh の子 `Rail` として実体化する(`TrackPath.CreateRailConnectorObject`)。歩道コネクタ(`UpdateNodeSidewalkMesh`)と同じ「全破棄→作り直し」の差分方針。
- **近接線路の結合描画(複線見え化)の隣接供給と無効化**: `Awake` が `TrackAdjacency.NeighborProvider = GetAdjacentTrackNeighbors` を設定し、TrackPath へ近接候補線路(通過セル+隣接セルの同種線路。工事中除外)の点列(`pathPointsCache`、無ければ `CalculatePathPoints`)と幅を供給する。線路の**データ変更**(LoadMergedPaths フェーズ2の version変化/新規、SyncPathsInCells の削除)時は `EnqueueAdjacentTrackNeighborRebuilds` が近接線路を `pendingTrackNeighborRebuildIds` へ積み、LODバッチ外なら即時・バッチ中なら `EndLodPathBatch` 時(このバッチで生成済みのパスは除外)に `queuedTerrainFollowPathIds` へ流して TerrainFollowRebuildWorker に再生成させる。**並走線路自身の version は変わらないため差分更新では描き直されない**のを補う仕組みで、再生成はデータを変えないため連鎖しない。削除時のエンキューは通過セルキャッシュを参照するため `UnregisterPathCells` より前に行うこと。
- **都市共通設定のストア(`CityInfoStore.cs`)**: `GET /api/v1/city` の `traffic_hand`(通行方向。ゲーム開始後不変)を保持する静的ストア。`PathManager.Start` が `EnsureLoaded(this)` で一度だけ取得する。駅パスのプラットフォーム設置側(platform_side の内側/外側→左右の解決。`TrackPath.ResolvePlatformExtents`)が参照する。取得前・失敗時は既定の左側通行として動作する(車両描画の VehicleManager は自前取得で独立)。
- `curved=false` でウェイポイントを持つ道路は、角を交差点と同じロジックで切断して別メッシュで埋める(`RoadPath.GenerateLinearPathWithCorners`)。**線路(TrackPath)は角の切断・充填を行わず、Broken点の1本スプラインとして描く簡易表現**(線路は通常 curved=true で敷設される想定)。
- セル跨ぎの道路を誤って非表示にしないため、アンロード時は `cellsToKeepVisible` / `pathCellsById` を参照する。アンロードは Destroy ではなく `SetActive(false)`(プーリング)。
- 接続パスが揃わず交差点を作れなかったノードは `awaitingIntersectionNodes` に記録され、後からパスが追加された時に自動再評価される。
- **交差点の接続パス数には防衛的上限がある (`MAX_INTERSECTION_CONNECTED_PATHS`=32)**: 交差点ポリゴン計算(`IntersectionPolygonUtil`)は接続パス全ペアのO(N²)処理のため、バックエンドのデータ異常(1ノードに大量のパスが接続)でメインスレッドがフリーズする。上限超過ノードは交差点メッシュを生成せずエラーログ(ノードごとに1回)を出し、`awaitingIntersectionNodes` にも積まない。
- **地下パス／非追従パスの描画区間分割**: `CreateOrUpdatePath` は `UsesTunnelMesh`（=`IsUndergroundPath` または「実効非追従(`GetPathFollowTerrain(path)`=false)かつ`HasTunnelSection`」）が真のパスを `GenerateUndergroundPathMesh` へ分岐し、点列を `TunnelSectionUtil.SplitDrawableRuns(points, clearance, portals, GetPathFollowTerrain(path))`（深さ=現在地形標高−道路Yがマスターの `height` を超える区間を除外）で分割して区間ごとにメッシュ生成する。トンネル区間はメッシュ・コライダーなし、境界には `portal` パーツを配置。**相対Y=0の非追従パスも丘に潜れば同じ分割対象になる**（谷を渡るだけの非追従橋はトンネル区間が無いので通常メッシュ＝柱付きで描く）。Farティアは `IsUndergroundPath` に加え `FarPathHasHiddenTunnel`（端点直線がトンネル区間を持つ非追従パス）も非表示にし、谷渡りの非追従橋は遠景の帯を描く。掘削は着工時にバックエンドが行い、地形リロード後の再生成で開削区間が自動的に描画対象へ変わる（バックエンド `geo_path_depth.py` の `path_uses_depth_classification` と同一の分類定義）。実効地形追従の解決は `PathManager.GetPathFollowTerrain(PathSummary)`（パス個別`follow_terrain`優先→バリアント既定）で、バックエンド `path_follows_terrain` と一致させる。
- **工事中(STATUS_CONSTRUCTING)パスの専用表示**: `CreateOrUpdatePath` は工事中のパス（`model_type=none` を除く全パス種。線路含む）を `GenerateConstructingPathMesh` へ分岐し、完成パーツの代わりに「土ストリップ(`ConstructionMeshUtil.BuildDirtStripMesh`。相対Yを無視して常に地表に盛る) + 高架の柱のみ(`RoadPath.GeneratePillars`) + トンネル坑口のみ(`PlaceTunnelPortals`。区間分割は地下パスと共通)」を生成する。進捗による見た目の変化はなく、status 変化でプール再利用条件が外れて必ず再生成されるため完成時は自動的に通常メッシュへ戻る。マテリアルは `Resources/ConstructionDirt`(建物の土マウンドと共用)。旧仕様の黄色マテリアル上塗り(VisualModeObject)は廃止済み。仕様の正は `genesis-docs/docs/geometries/G_道路メッシュの生成.md §15`。
- **インフラパス(PATH_ELECTRICITY/PATH_WATER)の垂直結合と垂直コネクタ**: インフラパスの端点は「垂直結合」（端点の相対YとノードYの不一致がデータ上の正規状態）を許容する。このため、(a) **インフラノード(`type`がインフラ財ビットを含む=`InfraNetworkUtil.IsInfraMask`)には交差点アスファルトを生成せず、`ConnectionPoints` によるパス端点の切断・差し替えも行わない**（`UpdateNodeIntersectionMesh` 冒頭のノードタイプ分岐。切断点で端点Yをノード平面の高さに引き戻すと垂直結合が壊れるため）。(b) 端点の絶対高さとノードの絶対高さの差が `INFRA_RISER_MIN_HEIGHT_DIFF`(0.5m) を超える場合、`CreateInfraVerticalConnectors` が高低差を繋ぐ垂直コネクタ（`Models/Paths/p{variant}/riser` プレハブ、無ければ円柱プリミティブ）を pathGO の子として生成する（SetupVisuals より前に生成し、ステータス表示の対象に含める）。ノードがパスより後からロードされた場合は、ノード到着時の `UpdateNodeIntersectionMesh` インフラ分岐 → `RefreshInfraVerticalConnectorsForNode` がコネクタだけを再評価する（パス本体は再生成しない）。

## 建物 (BuildingManager.cs)

- `center_point` が属するセルのティアで Far(箱) / 詳細候補を選び、Near/Dynamic セルでは描画LOD(上記「描画LOD」の節。`DetermineDesiredDrawLod`)で Close / Normal / Far を決める。**Far(箱)は `CreateFarBuildingObject` が `BuildingMeshBuilder.Generate(data, false, DrawLod.Far)` で実寸の箱(フットプリント×階数×階高。工事中/解体中=1階分、倒壊=0.5階分、地面アセット=0.3m板)を生成する**(`ObjectIdInfo.isFarObject=true`、`DrawLodInfo.drawLod=Far`)。Normal/Close は `CreateOrUpdateBuildingObject(data, isDynamic, forceRebuild, drawLod)`。
- **建物GameObjectの配置高さ(Y)は model_type で分岐する**: `IsBuildingApplicableForAltitude`(=`BuildingMeshBuilder.IsApplicableModelType`。MODEL_MODULAR_FLAT/HOUSE/GABLE)の建物は土地区画の基準標高 `parcel.altitude`(=接道の路面高さ)、それ以外(GROUND/PREBUILT/PORT)は `center_point` 位置の地形標高に接地する。傾斜地の谷側の隙間はメッシュ生成側のコンクリート基盤が埋める(MeshGenerators/AGENTS.md の「傾斜地対応」の節を参照)。地形追従の高さ再設定(`UpdateBuildingHeight`)も同じ分岐を通るため、altitude接地の建物は地形リロードで上下しない。
- **フル詳細メッシュには視野ゲートと個数上限(LRU)がある**: 生成/再生成が必要になった建物のうち視野外(カメラ前方75°超かつ100m超)のものは即時生成せず、遅延キュー(`deferredDetailBuildings`)へ積む。常駐ワーカー `DetailGenerationWorker` が「視野内かつカメラに近い」ものから順にフレーム予算内で生成する(視野外も上限に余裕があればアイドル生成する。振り向き時のポップイン軽減)。フル詳細オブジェクト数は `maxDetailedBuildingObjects`(既定2000)で上限管理され、超過時は「視野外で最も長く視野に入っていない」建物からGameObjectをLRU破棄する(データキャッシュは保持し、視野に入れば再生成される)。**遅延中の建物にはversionをキャッシュしない**(ワーカーが生成した時点でキャッシュ。次回マージロードでも「未生成」と判定させ、キューを最新データで更新し続けるため)。Far(箱)は軽量なので視野に関わらず即時生成し、上限の対象外。遅延リクエスト(`DeferredDetailRequest`)は描画LODを持ち、取り出し時に `closeBuildingIds` が `MAX_CLOSE_BUILDINGS` に達していれば Normal に落とす。LRU破棄は破棄時の描画LODのまま遅延キューへ戻す。
- **ティア変更時の再生成**: セルのティアが Far↔Near/Dynamic または Near↔Dynamic に変化した場合、versionが同じでも建物を再生成する(Far境界は箱⇔詳細の切り替え、Near↔Dynamicはフェンス・地面パネルの地形追従状態の切り替えのため)。PathManagerの `RebuildCachedPathsForCells` と同様の仕組み。
- **地形更新時の追従**: 常駐セルの詳細建物はメッシュを再生成し、Far輪郭は高さを再設定する。地形版を再利用キーに含め、旧地形に接地したキャッシュを使わない。対応建物の敷地装飾はBuildingExteriorRendererの別ジョブで生成する。本体は水平を維持し、地面パネルは実Terrainへ追従する。更新したセルだけを追従対象とする。
- **`CreateOrUpdateBuildingObject` の isDynamic/forceRebuild**: isDynamic はメッシュ生成器(`BuildingMeshBuilder.Generate`)に渡され、フェンス・地面パネルの地形追従を有効化する。`cellBuildingLod` はロードコルーチン完了後の `ApplyLodCellChanges` まで旧ティアのままなので、`LoadMergedBuildings` 中はティア表を参照せず desiredTier を直接渡すこと。また forceRebuild=false の場合、既存オブジェクトのプール再利用(status一致かつ非Far)で early return してメッシュは再生成されないため、メッシュの作り直しが目的の呼び出しでは必ず forceRebuild=true を渡すこと。
- メッシュコライダーは `Physics.BakeMesh` を `Task.Run` でベイクしてから割り当てる(`SetColliderAsync`)。道路も同様。
- **工事中(STATUS_CONSTRUCTING)の建物は毎エポック version が上がり再生成される**: 工事中の建物は進捗に応じた「工事現場」メッシュ(土マウンド+足場+クレーン。`ConstructingBuildingMeshBuilder`、MeshGenerators/AGENTS.md参照)で描画され、バックエンドの建設進捗フェーズ(`progress.py`)が進捗の増えたエポックで version を bump するため、既存の version 差分ロードだけで足場が毎エポック伸びる(BuildingManager側に専用処理は無い)。
- **接続端子ノードの移動に追従するのは車両基地だけ (`OnTerminalNodeMoved`)**: 端子ノードの移動確定(PATCH成功。`NodeInputHandler.SaveNodePosition`)時に呼ばれ、`TryGetInfraTerminalOwner` で所属施設を逆引きして model_type が MODEL_RAIL_YARD の建物だけを `forceRebuild: true` で再生成する(車両基地は端子位置へ線路を収束させて描画するため。versionは変わらないので差分ロードでは追従しない)。他の model_type は端子位置が見た目に影響しないため対象外。
- **インフラ接続端子ノードの逆引きインデックス (`buildingIdByInfraNodeId`)**: 端子ノードID(`agents.infra_node_id`)→建物agent_id のインデックス。ノードAPIは所属施設(owner)を公開しない片方向参照の設計のため、フロントの端子ノード識別(スナップ判定・端子マーカー・網互換判定)はこのインデックス経由の `TryGetInfraTerminalOwner` で行う。`buildingDataCache` への登録は必ず `RegisterBuildingDataToCache` を通し、削除は `SyncBuildingsInCells` / `ClearAllCells` で同期すること(`RegisterBuildingCell` 等と同じ管理規約)。
- **公共施設ハイライトの光オーバーレイは BuildingManager が起動する**: `Awake` の `PublicFacilityGlowOverlay.Ensure()`。描画本体は `VisualHandlers/PublicFacilityGlowOverlay.cs`(建物データをポーリングし、敷地の土地区画形状に黄色い光を描く。詳細は VisualHandlers/AGENTS.md)。
- **土地区画の逆引きインデックス (`buildingIdsByParcel`)**: 区画ID(`agents.parcel_id`)→その区画上の建物agent_id集合。「この区画の上に建っている建物」を全建物走査なしで解決するための汎用の逆引き(`GetBuildingIdsByParcel`)。`buildingIdByInfraNodeId` と同じ管理規約で、登録は `RegisterBuildingDataToCache`(parcel_id 変更時は旧マッピング除去)、削除は `SyncBuildingsInCells` / `ClearAllCells` で同期する。**ただし `GET /buildings/` の `multi_agent` スキーマに `parcel_id` が無いため、一括ロードされた建物の `parcel_id` は常に0でこのインデックスは実質空**(単体取得の建物だけが入る)。区画から建物を引く必要がある処理は、endpoint_schemas シートへの `parcel_id` 行追加まではこのインデックスに依存できない(`PublicFacilityGlowOverlay` は `ParcelManager.GetParcelIdAtPoint` による空間判定で回避している)。

## 区画 (ParcelManager.cs) / 地区 (ZoneManager.cs) / 天然資源 (ResourceManager.cs)

- パーセルにティアはない。上面ポリゴンは頂点ごとに地形高さへ追従させる(`BuildParcelTopLocalYOffsets` → `ParcelMeshFactory`)。
- **マウス位置からの区画検索 (`GetParcelIdAtPoint`)**: 指定ワールド座標XZを含む区画IDを2Dポリゴン内外判定で返す公開メソッド。起伏地形では区画のMeshCollider(上面はポリゴン角の頂点でしか地形をサンプルしない)が地形の下に埋まってレイキャストで拾えないため、InputHandler が「最初の有効ヒットが地形だった場合」のフォールバックとして呼ぶ(`InputHandlers/AGENTS.md` の基本フロー参照)。全件走査を避けるため、セル逆引きインデックス `parcelIdsByCell`(セル→BBOXが重なる区画ID集合)→BBOX→内外判定の順で絞り込む。BBOX・変換済みポリゴン・登録セル一覧は `parcelSpatialCache` に保持され(マウス移動フレームごとの `GetPolygonArray` アロケーション回避)、登録は `RegisterParcelDataForLines`(内部で `RegisterParcelSpatialIndex`)、削除は `RemoveParcel` / `ClearAllCells` で同期される。
- **区画内判定・区画内クランプの公開API (`IsPointInParcel` / `TryClampPointToParcel`)**: 指定区画IDに対する2Dポリゴン内外判定と、区画外の点をポリゴン境界の最近傍点(から0.05m内側へ寄せた点)へ射影するクランプ。`ConnectorEditManager`が接続端子ノードのドラッグ移動を「施設が立地する区画内のみ」に制限するために使う(境界ちょうどの座標はバックエンドの座標丸めで区画外判定に落ち得るため内側マージンを取る)。どちらも`parcelSpatialCache`ベースで、区画未ロード時はfalseを返す(呼び出し側は制限なしにフォールバック)。
- **接道街路の逆引きインデックス (`parcelIdsByFrontPath`)**: 街路ID(`front_path_id`)→その街路に面する区画ID集合。「この道路に面している区画」を全区画走査なしで解決するための汎用の逆引き(`GetParcelIdsByFrontPath`)。登録は `RegisterParcelSpatialIndex`(`ParcelSpatialEntry.frontPathId` に保持)、削除は `UnregisterParcelSpatialIndex` / `ClearAllCells` で同期する。`front_path_id` は parcels の `VERSION_TRIGGER_PROPS` に含まれるため接道変更時は version が上がり `CreateOrUpdateParcelObject` 経由で再登録される(=version一致分岐での再登録は不要)。**`front_path_id` は `ParcelSummary`(multi_parcel)のフィールドで、`ParcelSummaryExtensions.GetFrontPathId`(リフレクション)経由で読む**(endpoint_schemas シート未再生成でもコンパイルが落ちないための措置。`GetPlatformSide` と同じパターン。未再生成時は 0 を返し index は空=道路のみ色付く)。
- **環境スコアの色分け (`environmentScores`)**: 騒音(`VisualModeParcel.NOISE`)・大気汚染(`AIR_POLLUTION`)・治安(`CRIME`)の区画色分け用に、`GET /city/noise`・`/pollution`・`/crime` から取得した `parcel_id`→0〜1 の相対値を保持する。**これらのスコアは `ParcelSummary`(multi_parcel)に無い**ため、区画のbbox取得とは別経路で取る。値は都市全体分が1回のレスポンスで返るので、`ApplyVisualMode` でモードが切り替わったときに `RequestEnvironmentScores` が種別ごとに1回だけ取得し(同じモードを選び直したときはエポックで値が変わるため取り直す)、完了時に `ForceOutlineRedraw` で塗り直す。応答時に別モードへ切り替わっていたら `requestedEnvironmentMode` の不一致で破棄する。スコアを持たない区画(=エポック未実行でヒートマップ未計算、または対象外)は塗らない。**色はそのエポックの最小値〜最大値を両端まで引き伸ばした相対値**なので、濃さは都市内での相対的な悪さを表す(絶対水準はレスポンスの min/max 側にある)。
- パーセルの枠線・ホバーハイライト・データ可視化(地価等)は GameObject ではなく `OverlayMeshRenderer` で一括メッシュ描画する。カメラが1m/2°以上動いたフレームだけ再構築。距離フェードあり(480mで非表示)。
- **枠線/ハイライトは地形に埋もれさせない**: `EnsureOverlayRenderer` が `Custom/OverlayLine` マテリアルインスタンスに `_ZTest = Always(8)` を設定する(RoadPlanManager のギズモと同じ対応)。シェーダー既定の `LEqual`+ポリゴンオフセットでは傾斜地で平板ポリゴンが曲面地形の下に食い込んで埋まったように見えるため。代償として丘の裏や手前の建物を貫通してX線状に見えるが、強調表示用途(枠線・ホバー・パス干渉ハイライト)なので許容。**シェーダー本体や ZoneManager/TerrainManager のセル枠には影響しない(マテリアルインスタンス限定)。**
- **データ可視化(色分けフィル)だけは別レンダラー `dataVisRenderer`(深度テストあり+GPU地形追従)で描く**: 表示パネルの色分けモード(地価・地区色・建築可能判定等、mode>=200)のフィルを `_ZTest=Always` で描くと画面全域の塗りが建物を覆い隠してしまうため、第2の `OverlayMeshRenderer`(`ParcelDataVisMesh`、`ParcelOverlayMesh` の子で表示ON/OFFは親に連動)に分離し、専用シェーダー **`Custom/OverlayFillTerrainConform`**(ZTest=LEqual)で描く。このシェーダーはGPUテッセレーションで三角形を約2m間隔に細分化し、全頂点をグローバル高さマップ `_TerrainHeightmap`(`HeightmapTextureManager` が供給、`TerrainHeightmapConform.hlsl`)の地形高さ+`_TerrainLift`(0.3m)に張り付ける。**CPU側で頂点ごとに地形サンプリングした平板/箱を深度テストありで描く方式は禁止**: 頂点間(三角形内部)の地形の盛り上がりに勝てず、傾斜地で水たまり状の欠けが出る(既往バグ)。深度関係は「地形より上(テッセレーション追従+負のポリゴンオフセット-1,-4で常に勝つ)・建物より下(不透明の壁に自然に隠れる)・地表すれすれの floor パーツより上(リフト分手前)」。`_GpuTerrainConform` が0(高さマップ未アップロード)の間は頂点Yのフォールバック平板になる。BeginFrame/EndFrame/ClearMesh は overlayRenderer と常に対で呼ぶこと。
- ZoneManager は地区枠線(OverlayMeshRenderer)と地区名ラベル＋建物アイコン(2D UIオーバーレイ)を描画する。地区の塗りつぶし板ポリゴンは通常描画しない(枠線・名称・アイコンのみ)。地区の識別色はバックエンドの `zone.color`(HEX文字列)を `GetZoneColor(zoneId)` でパースして使う。建物/区画/道路の「地区別色塗り」モードもこの色を共有する。
- **地区枠線は内側へセットバックして描く(`BuildSetbackOutline`、`OUTLINE_SETBACK`=3m)**: 隣り合う地区は境界の街路を共有するためポリゴンの辺がぴったり重なり、そのまま描くと2本の枠線が同じ位置に乗ってどちらの地区色か判別できない。各頂点で前後の辺の内向き法線の平均方向へマイター長(`1/cos(半角)`、`OUTLINE_MITER_LIMIT`で打ち切り)を掛けてずらすことで、境界では2本の線が平行に並んで両方の地区色が読める。
- **地区編集モード中は枠線の見せ方を切り替える(`SetEditOutlineMode` / `SetSelectedZone`)**: ONの間は枠線マテリアルの `_ZTest` を `Always(8)` にして地形・建物に隠れないようにし、線も太くする(通常`LINE_THICKNESS`=5 → 編集中`EDIT_LINE_THICKNESS`=8、選択中の地区は`SELECTED_LINE_THICKNESS`=13)。**通常プレイ中は既定の `LEqual` に戻す**(全マップの枠線が山や建物を透けて見えると煩雑なため)。切替は `ZoneEditContext` の `EnterCreateMode`/`EnterEditMode`/`Exit`/`SelectZone` から呼ばれ、太さが変わるフレームは `lastCamPos` をリセットしてオーバーレイを必ず描き直す。
  - **地区のリロード経路は3つ**: ①`LoadCells`(カメラセル更新)は「未ロード時と isForce 時のみ」全件取得する省略設計。②エポック完了・編集確定の `CellDataLoader.ReloadAllData` は isForce=false のため①では再取得されず、代わりに `RequestZoneRefresh()`(version差分の全件リロード。進行中なら完了後に1回だけ追加実行)が明示的に呼ばれる。**地区はエポックごとに自動作成・統合・分離されるため、この呼び出しを外すと Unity の地区表示が起動時スナップショットのまま永久に更新されない**(枠線・ビルボードが表示されないバグの既往原因)。③地区編集確定は `ReloadAllZonesForced()`(version差分バイパス)。
  - **地区編集モードのホバーフィル (`SetHoverFillZone` / `ClearHoverFill`)**: 地区編集中にマウスが既存地区の領域内に入ったとき、その地区ポリゴンを半透明フィルでハイライトする。**枠線用の `overlayRenderer` とは別インスタンスの `hoverFillRenderer`** を使い、マテリアルの `_ZTest=Always` で地形・建物に埋もれず常に手前に塗る(ParcelManager の枠線・ホバーハイライトと同じ採用済みトレードオフ)。ポリゴンは `BuildingMeshBuilder.TriangulateOrFan`(耳切り。凹地区に対応)で三角形化し各頂点Yを地形高さに追従。**ホバー地区が変わったフレームだけ再生成**し(`OverlayMeshRenderer` は次の `BeginFrame` まで前フレームのメッシュを保持するため毎フレームコストは無い)、呼び出しは `InputHandler` のホバー判定(地区編集モード限定)。`ZoneEditContext.Exit` / `ClearAllCells` でクリアする。
- **地区内外判定の高速化**: `GetZoneIdAtPoint` は `IsPointInPolygon` の前に `zoneBboxCache` (BBOXキャッシュ) による早期リターンフィルタリングを行う。また、InputHandler はこの判定を地区編集モード中のみに限定し、通常時の毎フレームオーバーヘッドを排除している。
- ResourceManager は天然資源（油田、鉱脈、レアメタル、森林、水源の5種）の分布を、地形に追従するヒートマップ風オーバーレイとして描画する。ParcelManager/ZoneManager が使う `OverlayMeshRenderer` とは別に、**UVと頂点カラーを持つ専用の MeshFilter/MeshRenderer** と専用シェーダー `Custom/ResourceHeatmap` を使う（プロシージャルノイズで雲/ヒートマップ的な見た目）。
  - **ポリゴンのグリッド細分化**: 各資源ポリゴンのAABB内を `GRID_STEP`(10m)間隔で細分化し、各頂点で地形高さをサンプリングする。ポリゴン頂点だけで貼る板と異なり、丘や山の上でも地形に張り付いて埋もれない。
  - **輪郭はソフトフェード**: `SignedDistanceToPolygon` で各グリッド頂点のポリゴン境界からの距離を求め、`EDGE_FADE`(12m)の範囲で頂点アルファを0へ漸減させる。輪郭線は引かない。
  - **再構築タイミング**: カメラ移動では再構築しない（world-spaceメッシュなので不要）。リソースロード完了時と、`TerrainManager.OnTerrainTileReady`（資源が存在するセルのみ）で再構築。同一フレームの複数イベントは `LateUpdate` で1回にまとめる。
  - **資源名ラベル**: ZoneManager と同じ ScreenSpaceOverlay Canvas 方式で `resource_type_label`(水源・森林等)を表示。`EnsureUICanvas` で専用Canvas(sortingOrder=55, ZoneManager=60より奥)を作り、`resource_type_label` を `Text`+`Outline` で描画。位置は `Update` でカメラが 1m/2° 以上動いたフレームだけ `center_point` を `WorldToScreenPoint` して更新(ZoneManagerと同じ間引き基準)。

## 地形プロップ (PropManager / PropGeometry / PropRenderModel)

- `GET /props?bbox=…&include_obstacles=true`を`ApiClient.GetBulk`で取得する。完全スナップショットの配置情報と障害物を同時に更新し、建物のGameObject/Colliderへ依存しない。
- `LoadLodCells`は共通の常駐セル集合を使用する。個体の投影サイズでDrawLodを決め、セル由来のFar固定はしない。常駐範囲外は非表示。`CellDataLoader`から共通`LoadBatch`を渡し、旧処理の適用を防ぐ。
- セルサイズは`TerrainManager.CellSize`が正。`subdivisions`（既定8）で各セルを整数分割し、候補点・除外判定・GPUバッチを管理する。地形セルや独立したロード距離は追加しない。
- `PropGeometry`がregionの穴・複数パートと固定格子の座標ハッシュを扱う。候補点はregionのbboxや生成順、描画分割数に依存しない。プレビューもこの実装を使う。
- `PropRenderModel`は共有FBXを変更せず、instancing対応Materialの複製と任意の`_close`/`_far`を用いる。Far未提供時は複雑なサブメッシュを頂点の空間集約で簡略化し、既に軽い部品はそのまま使う。所有するMesh/Materialを全消去時に破棄する。実行時生成MaterialのためGraphicsSettingsのInstancing VariantsはKeep Allを維持する。
- `PropManager`は一本ごとのGameObjectを作らず、候補配列と小区画別バッチを保持する。描画は`Graphics.RenderMeshInstanced`（最大511個/呼び出し）。全マップを一括バッチにせずBoundsでカリングする。
- プロップID/versionの署名と障害物key/signatureの署名を分ける。障害物だけの変更では候補を再生成せず対象区画の表示可否だけ更新する。全土地区画が建物・床材の有無を問わず障害物になる（parcel:{id}、floor_variant=0）。非表示候補は復元用に残す。
- `chunksByCell`でアンロード・地形追従の対象を引く。Near/Dynamicの高さは既存`TerrainFollowUpCoroutine`で更新し、初回Far地形ロードは`OnTerrainTileReady`でも高さ更新を予約する。地形高さの共通取得処理を使用する。
- 生成・行列更新は`PerformanceConfig.FrameBudgetMs`で分散する。既定の常駐候補予算250,000を超える場合はカメラに近い小区画を優先し、区画の途中では切らない。更新中の旧/新配列の併存分は一時的に増える。
- `ResidentCandidateCount`/`ResidentChunkCount`、`SubmittedBatchCount`/`SubmittedInstanceCount`（サブメッシュごとの延べ数）、`LastRegeneratedChunks`/`LastExclusionUpdatedChunks`で負荷と差分を観測できる。
- 正式仕様は`city-genesis/docs/docs/geometries/M_地形プロップ.md`。独立Unity検証は`Tests/Props/run.ps1`。削除UI・橋下の高さ判定・UltraFar森林は対象外。

## 高さ取得APIの使い分け

`Utils.GetGroundHeight` がロード済みTerrainをサンプルし、未ロード位置は共通標高から作った全域overviewへフォールバックする。同一XZ・同一フレームだけを共有し、近傍座標へ丸めない。差し替え時は `Utils.InvalidateHeightCache()` が必要。`HeightmapTextureManager.GetTerrainHeight` もこのAPIへ委譲する。

`HeightmapTextureManager` はシェーダー用の移動窓GPUテクスチャを管理する。削除済みの `RebuildCpuHeightmap` / `WriteCellHeightRows` を復活させない。地形密着メッシュの接地判定は実地形を使う。

## アラート (AlertManager.cs)

- 役割: バックエンドの `GET /api/v1/alerts` から、alert ビットフラグが立っている建物(agent)・道路(path)の一覧を取得し、各 `center_point` 上にビット種別ごとのアラートアイコンを表示する。**alert は version 管理外のプロパティ**(VERSION_TRIGGER_PROPS に含めない)なので、BuildingManager/PathManager のような version 差分スキップは行わない。毎回フル再構築し `HashSet<string>` で前回セットとの差分を取って追加/削除する。
- **表示方式は ZoneManager / ResourceManager と同じ ScreenSpaceOverlay Canvas + Image**: 距離に依存せず常に一定のスクリーンサイズで表示する。ワールド座標は毎フレーム `Camera.WorldToScreenPoint` でスクリーン座標に変換して `RectTransform.anchoredPosition` を更新する(カメラ移動が1m/2°未満のフレームはスキップして間引き)。建物/道路の子にはしないため VisualModeObject のステータス連動や LOD アンロードの影響を受けない。
- **1オブジェクトに複数ビットが立っている場合はアイコンを水平に並べる**: 並べ方 `ZoneManager.cs:763` と同じ `(i - (count-1)/2f) * alertIconHorizontalSpacing` 式で中央揃えファンアウト。
- **アイコンSpriteの選択は AlertIconFactory が担当**: `AlertIconFactory.Instance.GetIconForAgent(bit)` / `GetIconForPath(bit)` を呼び出す。AgentとPathで同じビット値(例: CONSTRUCTION_SHORTAGE=1)でも別のアイコンを割り当てられるよう、ファクトリ内部は `Dictionary<(string entityType, int bit), Sprite>` で保持する。未設定ビットのSpriteはnullになりそのビットのアイコンはスキップされる。AlertIconFactory は BuildingIconFactory / AgentIconFactory / PathIconFactory と同じく `Utils/AlertIconFactory.cs` に配置し、Inspector から名前付き `[SerializeField] Sprite` を割り当てるパターンに従う。
- **表示フィルタのマスクは agent/path で名前空間を分ける**: UI(表示パネルのアラートタブ)から `view:change_view` の `sub_mode` にビットマスクが届き、`ApplyAlertMode` → `SetFilterMask` に渡る。`ALERT_AGENT_*` と `ALERT_PATH_*` は同じビット値(1/2/4…)を使うため、マスクでは **下位バイト=agent、上位バイト=path(`Constants.ALERT_PATH_MASK_SHIFT`=8シフト)** に分離する。`EnumerateActiveBits` は path のとき比較前にマスクを右シフトして下ろす。既定値は `AlertManager.DefaultFilterMask`(全種別ON)で、`ApplyAlertMode` の未指定時もこれを使う(定数の直書き禁止)。**アラート定数を追加したら `AGENT_ALERT_BITS` / `PATH_ALERT_BITS` の配列とUI側の `ALERT_CARDS` に足すこと**(配列に無いビットは無言で表示されない)。
- **火災系2ビットはアイコンを作らず `FireEffectManager` に委譲する**: `ALERT_AGENT_ON_FIRE` / `ALERT_AGENT_ACCIDENTAL_FIRE` は `AGENT_ALERT_BITS` ではなく `AGENT_VFX_BITS` に置く。この配列は `DefaultFilterMask` には含めるがアイコン生成には使わないため、表示パネルのトグルはそのまま炎・煙のON/OFFとして効く。`ApplyAlerts` は**アイコン用Canvas/AlertIconFactory の準備前に**火災側へ応答を渡すこと(火災エフェクトはどちらにも依存しないため、シーンにIconFactoryが無くても動く)。`ClearAll` は火災エフェクトも破棄する全消去、`ClearIcons` はアイコンのみ。
- **廃墟(倒壊)アイコンだけは alert ビットではなく status 由来**: `STATUS_COLLAPSED` の対象は `alert==0` でもバックエンドの `/alerts` が返す(`services/utility/alert.py`)。廃墟は撤去するまで消えず出っぱなしになるため、表示フィルタ専用ビット `Constants.ALERT_DISPLAY_RUIN`(65536。エンティティの `alert` には決して立たない)でON/OFFする。agent/path 共通の1ビット。
- **フィルタで表示対象が0になった対象はアイコンを破棄する**: `UpdateIcons` は「廃墟表示OFF かつ有効ビット0」なら `DestroyIcons(key)` して抜ける。`/alerts` の応答に含まれる=`newKeys` に入るため、ApplyAlerts 末尾の削除スイープでは消えない。`DestroyIcons` は `_worldPositions` / `_baseScreenPositions` / `_isBehindCache` も揃えて消すこと(`_baseScreenPositions` を残すと `_icons` との件数不一致でスクリーン座標更新が毎フレーム走る)。
- アイコンサイズは `alertIconSize`(既定100px、シーンでは65px)で Inspector 調整可能。Canvas の `sortingOrder` は `canvasSortingOrder`(既定70)で ZoneManager(60)・旧ConstructionWarning(65)より前面、Tooltip(100)より奥。
- **リフレッシュタイミング**: `CellDataLoader.ReloadAllData()` 完了後(エポック完了/編集確定後)、`ForceReloadAllDataCoroutine()` 完了後(全量再取得)、`OnCameraCellChanged()` でティア変更セルがあるとき。`Refresh()` は `refreshCooldownSeconds` で間引かれる。`forceRefresh=true` で強制可能(全量リロード時のみ使用)。
- Image の `raycastTarget=false` により、uGUIの Raycast に干渉しない。3D オブジェクト用の InputHandler には Collider/Tag を持たないのでそちらにも影響しない。
- 詳細・設計理由は `genesis-docs/docs/programs/アラートフラグ管理.md` 参照。

## 火災エフェクト (FireEffectManager.cs)

- 役割: 火災系アラートビットを3Dパーティクルで描く。`ON_FIRE`=炎+火の粉+黒煙+明滅する点光源、`ACCIDENTAL_FIRE`=薄い白煙のみ。両方立っていれば本格火災を優先するが、**`STATUS_COLLAPSED` の建物は瓦礫のくすぶり(白煙)に落とす**。
- **HTTP取得は持たない**: `AlertManager.ApplyAlerts` が `GET /api/v1/alerts` の応答をそのまま `ApplyAlerts(agents, filterMask)` で渡す(同じ情報のためにリクエストを2本投げない)。表示トグル `IsVisible` も `AlertManager` から伝播する。
- **エフェクトは建物GameObjectの子にしない**: 建物はLODの個数上限(`maxDetailedBuildingObjects`)で破棄され得るため、子にすると炎まで消える。ワールド座標の専用ルート下に置く。
- **既に燃えている建物のエフェクトは作り直さない**: `ApplyAlerts` はカメラのセル跨ぎ(0.5秒間隔)でも呼ばれる。作り直すと煙柱が根元から生え直して目立つため、再生成は種別変更時と寸法が `REBUILD_SIZE_RATIO` 倍以上変わったときだけ。
- 規模の解決は「底面ポリゴンの外接矩形(中心から遠い側の辺)」と「描画中メッシュのAABB上端 → 階数×階高 → 既定値」。建物未ロードでも既定値で成立する。
- `TransitOverlayManager` 等と同じく `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` で自己生成するため、シーンへの事前配置は不要。
- 詳細は `genesis-docs/docs/geometries/S_火災エフェクトの描画.md` 参照。

## 公共交通オーバーレイ (TransitOverlayManager.cs)

- 役割: 設置済みの公共交通パス(`PATH_TRANSPORT`)を「路線種別ごとの色帯」として下地道路の上にオーバーレイ描画する。`PATH_TRANSPORT` は `PathMeshFactory` にメッシュ生成器が無く通常描画では非表示だが、`PathManager` のキャッシュには点列込みで存在するため、**`PathManager.GetTransitPathsForOverlay()`**(path_type==`Constants.PATH_TRANSPORT` のキャッシュ済みパスを `CalculatePathPoints` で点列化して返す)で取得して `Utils/OverlayMeshRenderer`(`Custom/OverlayLine`)で細い色帯として描く。色は各パスの `path_variant`(=`transit_master` の `transit_path_id`)から黄金角ベースで安定生成する。
- 表示制御は路線種別(`transit_path_id`)単位。表示オプションの公共交通タブ → `view:change_view`(layer=`transit`, sub_mode=表示する `transit_path_id` のCSV) → `IpcViewCoordinator.ApplyTransitMode` → `SetVisibleVariants(HashSet<int>)`。空集合なら全非表示(メッシュを即クリア)。
- 再構築はカメラ移動(1m/1°)・0.5秒周期・`MarkDirty()`(路線設置確定後に `IpcEventHandler.HandleEditTransitFinish` から呼ぶ)のいずれかで行う。`AlertManager` 等と同じくシーン事前配置不要で、`[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` で自己生成する。
- 停留所の配置(路線設置エディタ)は `Editors/TransitEditor/TransitPlanManager` 側。

## その他の注意

- **HTTP取得は `ApiClient` に集約されている(UnityWebRequestを直接書かない)**。マージBBOXの一括取得やIDリストのバッチ取得など大きなレスポンスは `ApiClient.GetBulk<T>`(デシリアライズを`Task.Run`で別スレッド実行・timeout/no-cache標準装備・`UNITY_LOG_TIMING`でnetwork/deserialize分離計測)、単体取得や小さいレスポンスは `ApiClient.Get<T>`(メインスレッドのJSONパース)を使う。例外は `TerrainManager` の地形ハイトマップ取得(float16生バイナリ応答でJSONではない)のみ。**`GetBulk`はAcceptヘッダでMessagePackを要求し、バックエンドの対応エンドポイント(buildings/paths/parcels/zones/nodesのbbox一括取得)ではJSONではなくMessagePackバイナリで受信して`API/MsgPackDecoder.cs`でデコードする**(レスポンスのContent-Typeで自動判別するため、呼び出し側・未対応エンドポイントは何も変わらない)。デコード結果の中間表現(`Dictionary<string,object>`/`List<object>`/long/double)はNewtonsoftの非JToken表現と互換で、`GeneratedModelUtils`の座標変換がそのまま機能する。
- `BaseApiDataManager.LoadCells`はLOD非対応マネージャー(ZoneManager)用の仮想ディスパッチポイント。base実装は空で、LOD対応マネージャー(Building/Path/Parcel/Terrain)は`LoadLodCells`を直接オーバーライドするため通らない。(旧セル単位ロードの抽象メソッド `LoadCellData` は呼び出し元が無くなったため削除済み)
- `ApplyDepthOffsetShader` / `DestroyObjectAfterFrames` / `CalculateAverageWidth` は `Utils`(Scripts直下) の共通メソッドに切り出されている。マネージャー固有のものを新たに書かないこと。
- GameObject名にIDを埋め込む: `Building_{agentId}` / `Path_{pathId}` / `Parcel_{parcelId}` / `Node_{nodeId}`。各オブジェクトには `ObjectIdInfo` コンポーネントが付与され、`id` / `status` / `isFarObject` でメタデータを管理する。**名前の文字列パースは行わず、常に `GetComponent<ObjectIdInfo>()` で取得すること。**
- カメラ移動中に `LoadLodCells` が再度呼ばれると進行中のコルーチンは `StopCoroutine` でキャンセルされる。キャンセルで状態が壊れないよう、各マネージャーはフラグ復旧処理を持っている(例: TerrainManager の `settingsLoading` リセット、PathManager の `EndLodPathBatch`)。地形ロード完了後の追従更新コルーチン `CellDataLoader.TerrainFollowUpCoroutine` も `UpdateManagers` 冒頭でキャンセルされる。取りこぼした更新セルは `CellDataLoader.pendingFollowUpCells` に退避され、次回ロード完了時に再処理される。追従コルーチンが建物・道路の内側コルーチンを呼ぶ際は `StartCoroutine` ではなく `IEnumerator` を直接 `yield return` すること(`StartCoroutine` で起動した子は親の `StopCoroutine` では止まらないため)。
- **やむを得ず `StartCoroutine` で子コルーチンを起動する場合(TerrainManager の並行ロード等)は、`LoadBatch`(CellDataLoader が `UpdateManagers` ごとに発行し `LoadLodCells` の4引数オーバーロードで配布、`BaseApiDataManager.currentLoadBatch` に保持)の `IsCanceled` を yield をまたぐたびに確認して協調的に停止すること。** 旧バッチの子が新バッチの状態(`terrainTiles` / `cellLodLevel` 等)と競合するのを防ぐための必須パターン。
- **ロード進行ログ・計測ログは無条件 `Debug.Log` を使わない**。ロード進行は `UnityLog.If(LocalConstants.UNITY_LOG_LOADER, ...)`、`[TIMING]` 計測は `UnityLog.If(LocalConstants.UNITY_LOG_TIMING, ...)` を使う。セル移動ごとにホットパスで多数のログが出るため、無条件 `Debug.Log` はエディタのスタックトレース取得で無視できないコストになる。文字列補間が重い場合は `Func<object>` オーバーロード(`() => $"..."`)を使う。`LogWarning` / `LogError` は無条件で残してよい。

### 交差点の断面と切断距離

- 未切断の描画点列を交差点へ渡し、描画時はその点列を切断する。保存制御点やアンカーを切断点へ動かさない。single_spanは双方でアンカー間の直線を使う。
- GetIntersectionCutLimitはノード逆引きで反対側の接続状態を調べ、両端交差点なら45%、片側終端なら95%まで予約する。折れ線は最寄りアンカーの手前に制限する。
- 交差点消滅・生成失敗では切断キャッシュも解除して本線を再生成する。旧切断点を残して道路端を欠けさせない。
- Sidewalkコンテナは歩道だけでなくPartで識別される壁・中央分離帯・ラインも保持する。生成メッシュはIntersectionMeshOwnerが解放する。詳細はMeshGenerators/AGENTS.md。

## 地表メッシュとの高さ整合

- `Utils.GetGroundHeight` は同一XZ座標のみをフレームキャッシュする。Terrainの部分SetHeights、登録、除去、全消去ではキャッシュを無効化する。
- `Terrain.heightmapPixelError` は `LocalConstants.TERRAIN_HEIGHTMAP_PIXEL_ERROR`（1）。ゲーム側のティアと独立した描画簡略化でfloor・道路を突き抜ける誤差を抑える。
- `PathManager.TERRAIN_SAMPLE_INTERVAL` は1m。道路のExtrudeタイル上限と合わせ、縦断方向の微起伏を拾う。

## 道路設備のマスター

PathMasterItemの設備*_allowed/default_*はAPIのpath_masterに対応する。設置状態の正はPathSummaryの4項目で、既定値をロード時に再適用しない。設備変更はPathのversion更新で通常の差分再描画に入る。

## 駅の保存済み標高

`PathSummary.station_grade_profile` があれば `ApplyStationGrade` でXZ射影した絶対標高を適用する。通常のスプライン補正（ROAD_SPLINE_OFFSET−base_height）を加え、交差点切断より前に適用する。折れ線も先に細分化し、TrackPath側で地形再サンプリングや切断断面の上書きをしない。Far表示・交差点ノードも同じ保存基準を参照する。

GetNodeSnapPositionは接続パスに保存標高があればそれを優先し、なければノード相対YをUtilsの地形高さに加える。描画オフセットを含まない吸着用の絶対位置。

- PathManagerはTrackPath.ConnectedPathsProviderにGetPathsConnectedToNodeを提供し、線路の車止め・ホーム端の接続判定もノード逆引きで行う。破棄時にプロバイダを解除する。


TrackPath.InfrastructureConnectionProviderにはIsInfrastructureConnectionを登録し、ノードtypeのインフラビットを判定する。施設の共用接続点へ線路が1本だけ接続されていても車止めを置かない。BuildingManagerのロードを待たず判定し、OnDestroyでプロバイダを解除する。

- 車両の範囲抽出は `GetPathsNearCircle` でセル逆引きから候補を得る。外接矩形の粗い抽出なので、呼び出し側で中心線と円の交差を判定する。`CityInfoStore` の通行方向は駅と車両が共有する。

## 非同期資源の解放

`MeshBakeService.Bake` は同時2件の物理ベイクを行う。`RenderCacheIdentity` の破棄は同サービスのReleaseへ通し、ベイク中のMeshを先に破棄しない。タイル専用のMaterial・TerrainLayer・代替Textureは `TerrainTileResources` が所有する。建物の遠景Materialは全棟で共有し、Renderer.materialsで複製すると集約できなくなる。詳細建物で複製したMaterialはRenderCacheIdentityへ登録する。

## 取得と描画完了の区別

建物の遅延生成は、対象セルがロード完了集合へ登録されるまで要求を保持する。未登録セルの要求を選出してから削除してはいけない。一括取得中も生成ワーカーが動くため、背面の建物だけ永久に生成待ちから消える競合になる。次の取得範囲から外れた要求は新しいロード開始時に除く。

道路の `pathVersionCache` は受信版ではなく、生成・復元が完了した版を記録する。API取得や高優先度キューへの投入時には更新しない。同じ版でも描画オブジェクトが存在しない道路は再生成対象とする（意図的な遠景トンネル非表示は除く）。これにより建設計画の更新中にロードが中断されても、次の差分取得で生成できる。道路バッチの終了処理はfinallyで実行し、中断・例外時に地形追従を永久待機させない。

回帰検証は `Tests/Lod/run.ps1`。未ロードセルの建物キュー保持と、建設計画中の道路のHTTP取得→生成前中断→同版再取得を検証する。

プロップ障害物のholesは水域内の島を表す。PropGeometry.BlockedWithHolesを永続描画・プレビューで共用し、島内部を表示しつつ岸の境界・離隔内は除外する。

プロップは距離で本数を間引かず、極小投影サイズによるHiddenも使わない。木1/2は幹なしの専用Farを使用し、投影比1%未満でPropRenderModelの6三角形の四角錐へ替え、1.2%以上で戻す。DrawLod.Far内の切り替えで、常駐セル・候補予算は維持する。生成した最小メッシュはPropRenderModel.Disposeで解放する。

プロップの障害物除外は範囲散布のみ。Candidate.explicitInstanceは常に表示可能とし、区画・道路・水域の追加や更新でも非表示にしない。個別配置の接地・常駐セル・候補予算・LODは通常どおり適用する。


## ロード性能の計測

`UNITY_LOG_TIMING` で `LoadBatch.Trace` の工程別経過時間と、RenderWorkSchedulerの5秒単位iterator別集約(cpu/maxStep/steps/queueFrames/underBudgetQueueFrames)を出す。RenderWorkLifetime.LateUpdateから報告するため待機中も残件数を観測できる。BuildingManager.PendingDetailCountとPathManager.PendingTerrainRebuildCountは遅延生成キューのみで、全表示の完了判定ではない。詳細とログの読み方は `K_Unity差分ロード整理.md` の「ロード遅延の計測」。


区画一覧は1件ごとのContinueで予算内に複数件適用し、全件適用・削除差分の確定後にIsLoadingを解除する。区画が確定してから建物を生成する依存関係は維持する。地形の帯適用・SyncHeightmap・完成タイル公開も別ステップとし、未完成タイルを公開しない。

`Tests/Lod/run.ps1` は小さい生成880件のフレーム数・共通予算・複数ワーカーの進行に加え、null/CustomYieldInstructionの待機、ネストしたiteratorの待機中キャンセルを検証する。MoveNext内で自己取消しが起きても、実行中iteratorのDisposeはそのステップが戻ってから行う。

## 公共交通の時間付き描画

路線一覧の取得主体は `Vehicles/TransitDataStore`。`TransitOverlayManager.RefreshLines/Clear` は共有ストアへ委譲し、色帯の表示設定で車両の取得・走行を止めない。車両は `TransitVehicleManager` が管理する。`TryGetPathPolyline` の第3引数 `trimIntersection=false` は未切断の地形追従点列を返し、パス途中の停留所の距離割合の解決に使う。レール接続は実際に生成済みのSplineComputerを参照する。


## 道路の非同期生成と表示の差し替え

長い道路のSplinePartLodは本体GameObjectの作成後に固定区間を生成するため、本体の作成を表示完了とみなさない。PathPresentation.Replaceで旧道路を保持し、新しい道路の各パーツが初回LODを確定した時点で一括表示する。生成途中の新しいRendererはforceRenderingOffで伏せ、旧道路との重複描画を防ぐ。既存のforceRenderingOff値は復元する。

再生成が連続した場合は未完成の候補から表示中の旧道路を引き継ぎ、未完成の候補だけを退避する。完成時に旧道路をキャッシュへ退避し、アンロード・全消去では保持中の旧道路を非表示にして破棄する。キャッシュからの復元と遠景への切替も同じ入口を通す。建物の主メッシュは同期生成なので、この道路パーツ専用の待ち合わせには含めない。回帰検証はTests/Lod/run.ps1のRoadPresentation。

パスの表示同期: 再利用キーにstatusを含め、version一致でも表示中のObjectIdInfo.statusが異なれば再生成する。SetupVisualsはRenderer収集・Stats設定後にSetStatusを呼ぶ。SplinePartLodは生成・復元区間へRefreshMaterialsを適用してからPathPresentationをcommitし、生成途中はRenderWorkScheduler.Continueで共通予算に委譲する（区間単位の固定フレーム待ちはしない）。
