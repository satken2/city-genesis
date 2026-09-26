# DataLoaders フォルダ

`PropRenderModel(GameObject)` は `VehicleModelResolver` で原点・向きを解決済みの車両テンプレートから、LODごとのメッシュとインスタンシング用材質を抽出する。路駐はこの入口を使う。共有FBXメッシュは所有せず、複製材質だけをDisposeで解放する。既存のPropsパス指定コンストラクタと抽出・bounds計算を共有する。

PathManagerの完成した乗り場451/461・462・502・602のFarはFarPlatformPathの簡易立体を使う。屋根・壁・床の色帯を通常と同じ共有材質で保つ。通常道路の帯、施工中、地下非表示の規則は共通のまま。生成メッシュは子のIntersectionMeshOwnerが所有し、遠景キャッシュの退避・復元も通常の経路を使う。

バックエンドから取得したデータをUnityのオブジェクトに変換する処理を定義する。
都市全体を一度に描画すると重すぎるため、マップをセル(一辺 `TerrainManager.CellSize`、バックエンドの `/terrain/settings` から取得)に区切り、カメラ周辺のセルだけを詳細に描画する。起点は `CellDataLoader.cs`。

## 地形・取得範囲・描画LOD

森林の地形下地は800〜1,100mで完成させ、実モデルを1,200〜1,500mで減らす。常駐境界も下地が完成してからモデルを削減する（ForestCoverage.hlsl）。下地を単純に`1-modelWeight`に戻すと、間引いた木の隙間から薄い下地越しに裸地が透けて帯になる。

森林シェーダーの霧はTerrain/Litと同じ`InitializeInputDataFog`を使う。クリップZを`ComputeFogFactor`へ渡す実装では、正投影の木だけ霧が掛からず遠景森林と色が分離する。`Tests/Props`の同一平面・材質による透視/正投影の標準Lit比較を維持する。

`ForestOverview`は範囲配置の木（1・2）の全域被覆マスクを所有する。`PropManager`が`/props/forest-overview`を初回・編集時に取得し、バックグラウンドで最大2048²にラスタ化する。個別の木やセルごとの水域ポリゴンは全域取得しない。`CellDataLoader.ReloadAllData`で無効化し、カメラ移動では再取得しない。生成済み森林小区画から境界距離マスクも作り、`ForestCoverage.hlsl`を実モデルと通常/UltraFar地形で共有する。1,200〜1,500mと生成済み範囲の縁約100mでモデルから地形の樹冠表現へ移す。clear時は通信・Taskを取消し、旧結果を公開せずグローバル描画も無効化する。近傍の木のFBX・Forward+照明は変更しない。

`station_grade_profile`は駅だけでなく、既存パスへの接続勾配を設計した道路・線路にも付く。`ApplyStationGrade`と吸着用の保存標高参照をパス種別で限定しない。独立設計の`station_grade_id="connection"`は共通駅グループではない。

`LodConfig` は視点・注視点・視錐台から最大81セルを選ぶ。`farCells / nearCells / dynamicCells` は呼出し互換の名前で、現在はすべて同じ常駐セル集合。候補セルはマップ寸法でクランプするため、常駐セルの算出前に `/terrain/settings` を取得しておく必要がある(セッション開始・全量リロード・`session:reload` の入口である `TerrainManager.FetchCoastline` が先頭で `EnsureSettingsLoaded` を呼ぶ)。既定値のまま算出するとマップ外セルを要求し、`/terrain/surface` の400応答で地形ロードが2秒間隔の再試行から抜けられなくなる。第9引数 `previousResident`(`CellDataLoader.currentCellTiers.Keys`=現在の常駐集合)はヒステリシスで、常駐中のセルは視錐台・注視半径・近傍500mの判定に `RESIDENT_HYSTERESIS_MARGIN`(250m)の余裕が付き、スコアも0.8倍になる(回転・ズームの微小な変化で縁のセルが出入りを繰り返さないため)。`LodQA` が省略可能引数を既定値で補ってリフレクションで呼ぶので、`CalculateCellSets` にオーバーロードを足さないこと。

`CameraCellChecker` は0.1秒間隔で位置、向き、FOV、正投影、画面条件を調べる。視線中心はy=0平面との交点。`ResetViewState` で検出状態を未初期化に戻すと静止中でも次の判定で必ずセル通知が出る(`CellDataLoader.ClearAllManagerData` と `ReloadAllData` の現在セル未設定分岐が呼ぶ)。`session:reset` 後は `session:reload` が届くまで `OnCameraCellChanged` を無視する(`ClearAllManagerDataForSessionReset` / `loadSuspendedUntilReload`。バックエンドが新しい都市を準備中のためで、準備中のタイルを取り込むと同じ版の差分リロードで更新されない)。`CellDataLoader` は旧 `LoadBatch` を取消し、地形取得・適用→既存オブジェクトの地形追従→区画取得完了→建物・道路・地区・資源・プロップ取得の順で進める。更新セルだけを追従させ、取消しで残ったセルは次回へ引き継ぐ。版情報・タイルの取得失敗は2秒後に再試行し、必要タイルの適用前に後続生成へ進まない。**カメラ由来のバッチ発行は `CAMERA_BATCH_MIN_INTERVAL`(0.25秒)以上の間隔に合流させる**: 間隔が空いた後の最初の変化はすぐ発行し、間隔内の通知は最後の1件だけを `CellDataLoader.Update` が期限後に発行する(集合は発行時点のカメラから計算し直す)。保留中も `currentCenterCell` は更新する(間に来た `ReloadAllData` が最新の視点で集合を作るため)。保留した回数は `camera.batchDeferred`。地形の版一覧 `/terrain/cell-versions` は、isForce・無効化済み・未取得・前回取得から `CELL_VERSIONS_MAX_AGE`(5秒)超のときだけ取り直し、それ以外はキャッシュを再利用する(`terrain.cellVersionsFetched` / `terrain.cellVersionsReused`)。無効化は `ReloadAllData`(`TerrainManager.InvalidateCellVersions`)と `ClearAllCells` で、取得中に無効化されたらその応答では無効化を解除しない。都市外の背景地形は独立したルーチン(`BuildBackground`)で取得・生成し、バッチの取消しでは止めない(止めるのは `ClearAllCells` の `ClearBackground` だけ。詳細は下記「都市外背景」)。

地形は `/terrain/surface` のfloat32を513格子の一時TerrainDataへ分割適用し、完了時にタイルを差し替える。途中の帯を表示中タイルへ書かない。`SetHeightsDelayLOD` と最後の `SyncHeightmap` を使い、隣接タイルを接続する。全域のCPU高さ配列、旧far/near/dynamic読み分け、ティア境界ブレンド、タイル縁の高さブレンドは使用しない(隣接タイルの境界は同じ原本を補間するので一致する)。超遠景は全域の陸地メッシュ。
- **タイルの取得はセル単位のジョブ(`TileJob`)で、寿命はロードバッチではなく「セルが常駐集合にいる間」**。バッチが取消されてもジョブは止まらず、後続のバッチが必要版以上の実行中ジョブを再利用する(`terrain.tileJobReused`)。取消し(通信の Abort と未開始リクエストの破棄を含む)は常駐外になったとき(`UnloadCell`・`LoadLodCells`)と `ClearAllCells`(epoch)だけ。取得から公開・通知までを同時に進めるジョブは `MAX_PARALLEL_TILE_DOWNLOADS`(4)までで、空いた取得スロットは `GrantTileSlots` が視野優先(`ScoreStreamCell`)の順に1件ずつ割り当てる(各ジョブは自分の割り当てだけを待つ)。初回の Step より前に取消されたジョブは LRU に触れない。
- 分割できない `SyncHeightmap`・タイル公開・`OnTerrainTileReady` の通知はそれぞれ別ステップで、共通予算の使用が25%以下になるまで最大8フレーム待ってから始める(`FRESH_BUDGET_RATIO` / `FRESH_BUDGET_MAX_WAIT_FRAMES`)。版(`dynamicCellVersions`)・`loadedCells`・更新セルの記録はタイル公開と同じステップで行い、通知の時点で購読者が新しい版を読めるようにする。通知は `TileNotifyWorker` が別の操作として公開順に出し、`HeightStreamWorker` は通知を待たずに次のタイルへ進む。通知の前に epoch と表示中タイルの同一性を確かめ、差し替わっていれば通知しない。`EnqueueAndWait`(ひいては `OnAllTerrainLodLoaded`)は全タイルの通知の完了まで待つので、`OnAllTerrainLodLoaded` は常に全タイルの `OnTerrainTileReady` の後に発火する。`ClearAllCells` は通知待ちを送らずに完了扱いにする。
- **常駐外タイルの LRU(`tileCache`、`TILE_CACHE_CAPACITY`=24枚。1枚あたり推定2〜4MB)**: 常駐外になったタイルは破棄せず、非表示のまま版と一緒に保持する(`ParkTile`)。再入場時に必要版以上なら(`TryTakeCachedTile`)、取得・帯の適用・SyncHeightmap・コライダー生成をせず、`RestoreCachedTile` で SetActive と隣接の再接続だけを行って表示へ戻す(`terrain.tileCacheHit`。追い出しは `terrain.tileCacheEvicted`)。**復元でも `OnTerrainTileReady` は1回発火し、updated セルに含まれる**ので、依存側は地形の版で作り直しを省く(下記「version差分更新の慣習」)。ForceReload では LRU を使わない。新しいデータを公開したら同じセルの保持分は破棄する。地表種別の変更は保持中のタイルにも反映する。
- **保持中のタイルは `terrainTiles`・`LoadedTerrainTiles`・`HasSurfaceTile`・`GetAttachedTerrainVersion` に現れない**(接地・GPU高さ窓・UltraFar マスクは非表示タイルを参照しない)。`terrainTiles` を書き換えるときは必ず `loadedCellMaskDirty` を立てること(UltraFar のマスクはこのフラグが立ったときだけ作り直す)。受信高さ配列(約1MB)は `heightBufferPool` で再利用し、プールへの返却は `request.heights = null` と対にする。
- `TerrainManager.GetTerrainAtPosition` はセルの負側の縁(min 辺)を含む。点がちょうど min 辺にあり、自セルが常駐していなければ常駐中の負側隣接タイルを返す。

ワールド座標はバックエンドと一致する固定座標で、原点シフトは行わない。`TerrainManager.UpdateCameraClipDistance` はマップ寸法と現在のカメラ位置・高度から全景を含むfarClipPlaneを確保する。シーンの固定距離を描画上限にせず、常駐セル数や詳細オブジェクトのLODは既存の予算内に保つ。

超遠景は `overviewDataRevision`（標高受信済み）と `overviewRevision`（メッシュ完成済み）を分ける。`EnsureOverview` は生成中断後も同じ受信データから再開し、生成成功または陸地なしの確定時だけ完成版を進める。取得・生成失敗は2秒後に再試行する。`ClearAllCells` は進行中バッチとロードを止め、両方の版を未設定へ戻す。バックグラウンド結果はclearEpochとバッチ取消しを検査し、旧セッションの全景を公開しない。回帰検証は `Tests/Lod/run.ps1` のOverview。

## 共通生成予算・キャッシュ

生成・適用ワーカーは `RenderWorkScheduler.Start(owner, routine, category)` で起動し、停止も同クラスの `Stop` を使う。Unityの `StopCoroutine` だけではiteratorのfinallyが実行されないため、独自のStart/Stop対で待ち行列と一時メッシュを解放する。GOの無効化・破棄では `Start` が owner の GO に付ける `RenderWorkLifetime` が停止する。停止は GameObject の instanceID 索引を引き、その GO が owner の operation だけを止める。owner(コンポーネント)だけが破棄された operation は、`Pump` が走査の途中で Finish する。初回の Step より前に Stop された iterator は finally を実行しないので、ハンドルや実行中フラグを finally だけで戻すワーカーは owner の `OnDisable` でも戻すこと(VehicleManager / PedestrianManager / TransitVehicleManager)。**例外: `BuildingExteriorRenderer` は共通ホスト GO `BuildingExteriorWork` を owner にし、建物GOには `RenderWorkLifetime` を付けない**(停止は自分の `OnDisable` で行い、再表示で未完了のジョブから再開する)。子処理は `LoadBatch.IsCanceled` も確認する(地形タイルジョブは例外で、寿命をセルの常駐に合わせる。上記「地形」)。

`PerformanceConfig.FrameBudgetMs` は全生成器の同期処理に共通（目標フレーム時間の25%、最低2ms）。各処理は `SliceBudgetMs` または1件ごとの境界で `yield return RenderWorkScheduler.Continue` により協調中断する。共通ポンプが実行可能な処理を順番に進め、予算内なら同じフレームでも再開する。`yield return null` は次フレーム待ちなので、通信のポーリング・アイドル・周期待ちだけに使う。UnityのYieldInstruction/CustomYieldInstruction/AsyncOperation/Coroutineはネイティブ待機へ委譲し、その待機中の処理は実行順を塞がない。ポンプは `RenderWorkScheduler.Start` が自動生成する常駐 GO `RenderWorkPump`(DontDestroyOnLoad)の `LateUpdate` から1フレームに1回 `Pump` と `ReportTimings` を実行し(`Reset` の後も生きているポンプは再利用し、再生の終了処理中=`Application.quitting` 以降は作らない)、同期処理の合計に加えてポンプ全体の経過時間も共通予算で制限する。1回のUnity API呼び出しは中断できないため、F8の最大step時間も確認する。

`RenderObjectCache` は非表示GOのLRU。建物256MiB、道路128MiB、道路パーツ8MiB、全キャッシュ合算512MiBの推定上限。推定量は1件あたり固定費4096Bに、子の MeshFilter / MeshCollider のメッシュ(重複除外)ごとの `EstimateMeshBytes`(頂点ストライド×頂点数+インデックス数×幅。isReadable なら×2)を足した値で、Profiler を使わないので製品ビルドでも上限が効く。表示中GO・GPU複製は数えない。建物はセル離脱時にもここへ Park される(下記「建物」)。追い出しは `renderCache.localEvicted` / `renderCache.globalEvicted`。`RenderCacheIdentity` は生成Meshのみ所有し、共有アセットを破棄してはいけない。セッション終了時は各キャッシュをClearする。

描画LODは `DrawLodEvaluator` で独立に決める。建物・プロップは投影サイズ（正投影対応）、道路は中心線・区間までの距離。`LodViewState` をカメラ変更検出に使い、用途で3メソッドを使い分ける: `Update`(位置・回転・FOV/正射影サイズ・画面比・投影方式。取得範囲と視野判定=LRU スイープ用)、`UpdateProjection`(4m 移動・FOV/正射影サイズ・投影方式だけを見て回転・画面比は見ない。建物の投影比LOD用)、`UpdatePosition`(移動だけ。道路の距離LOD用)。**1つのインスタンスでは1つのメソッドだけを使うこと**(記録する項目が違うので、混ぜると変化を取りこぼす)。F8はLOD個数、生成時間、p95/p99、待ち数、キャッシュ推定量、プロップの影数を表示する。

## version差分更新の慣習

地形追従の取消しでは `PathManager.terrainFollowCompletedPaths/Nodes` を保持する。本線は版・状態と表現の存在を確認して省略し、交差点は省略した本線の端点も含めて残作業を回収する。`OnTerrainTileReadyHandler` はdefer判定前に周囲3×3の道路と端点ノードの記録を無効化する。道路キャッシュの変更・削除は既存のSave/Removeヘルパーを通す。ノード版変更時は追従開始で全記録を破棄し、全体完了・強制更新・ClearAllCellsでも `ClearTerrainFollowProgress` する。区間の非同期表示完了は別で、PathPresentationの待ち合わせを維持する。

`CellDataLoader.CollectDeferredTerrainNotifications` は全地形ロード完了時にdefer中の通知をpendingへ先に統合する。完了後の回収で同じセルを二度追従させない。`RebuildCachedPathsForCells` は常駐セルへの再入場だけでforce再生成しない。地形・データ・交差点の変更は各更新経路で無効化する。検証はTests/LodのTerrainFollowResume／TerrainNotificationConsumption、観測は `terrainFollow.pathCompleted/pathResumeSkipped/pathInvalidated` とノード側の対応カウンタ。

建物・道路一覧は500件ずつcursorで最後まで取得してから削除差分を適用する。途中ページの失敗を空の完成一覧として扱わない。独自レスポンスの `next_cursor` を使い、自動生成 `GeneratedModels.cs` は編集しない。

建物の再利用キーは `agent_id:形状ハッシュ16進:形状版:区画トークン:描画LOD:地形スタンプ`(`BuildingRenderKey`)。道路はデータ版、描画LOD、強制再生成の形状revisionを使う。地形が変わっていない単なるカメラ移動でforce再生成してはいけない。
- 形状ハッシュは `HashBuildingShape` が見た目に効く項目(variant・bd_type・階数・状態・工事進捗・ポリゴン・中心・外観バリアント・サブ建物・端子/基地ノード)から作る。**見た目に効くフィールドを足したら `HashBuildingShape` にも足すこと。** `BuildingSummary` と `ParcelSummary` は受信後に書き換えない前提で、`ConditionalWeakTable` にインスタンスごとにキャッシュする。区画トークンはポリゴンと基準標高だけで、地価や接道は入れない(区画の再取得のたびに建物を作り直さないため)。形状版は `buildingGeometryRevisions`(全体の通し番号)で、版の変わらない見た目の変化(車両基地の端子移動)で進める。地形スタンプは `TerrainManager.GetSurfaceStamp`(建物の境界円+敷地の外接矩形)。
- **地形の版API**: `TerrainManager.GetAttachedTerrainVersion(cell)` は表示中タイルの版(タイルが無い・常駐外・非アクティブなら -1)、`GetTerrainSignature(Rect / セル集合)` はその範囲の版・全景データ版・`TerrainEpoch`(=clearEpoch)を混ぜた64bit署名、`GetSurfaceStamp(Rect)` は同じ内容の文字列版(再利用キー用)。同じ版・同じ全景・同じ世代なら再入場後も同じ値になる。**依存側(建物・道路・交差点・プロップ)は生成時にこの値を記録し、`OnTerrainTileReady` や地形追従で現在値と比べて一致すれば作り直さない**(LRU の復元でも通知が来るため)。`TerrainManager` が無いときは「不明」として扱い、省略しないこと。

## 地形の関連機能

UltraFar のロード済みマスクは `terrainTiles` が変わったとき(`loadedCellMaskDirty`)だけ作り直し、内容が同じならGPUへ送らない。GPU 高さ窓(`HeightmapTextureManager`)は窓に重なるセルの到着・除去(`MarkSurfaceChanged(Vector2Int)`)と再センタリングのときだけ作り直す(引数なしの `MarkSurfaceChanged()` は無条件)。

遠景の沈み込みは `UltraFarTerrainSink.hlsl` をForward・Shadow・GBuffer・Depth・DepthNormalsで共用する。`_FarGridStep` はビルダーと同じ格子間隔（最低1m）を渡す。頂点の周囲1格子分が全てロード済みの場合だけ沈め、境界をまたぐ面は元の高さに残す。頂点自身のセルだけで沈めると三角形の補間が未ロード側も引き下げ、海が見える裂け目を作る。UltraFarメッシュは都市の範囲にしか面を持たないので、`UpdateLoadedCellMask` が渡す都市範囲(`_FarCityRect`)の外側の近傍は境界上のセルとして判定する(マスク外を未ロード扱いにすると、陸地が都市端まで続くマップで境界から1格子の帯が沈まず、+`ultraFarHeightOffset` 浮いた遠景が詳細地形の上に明るい帯として出る)。`Tests/TerrainAppearance/run-unity.ps1` が境界・アンロード・領域移動・都市境界の帯と内部沈み込みを描画検証する。

通常TerrainとUltraFarメッシュは `Assets/Shaders/TerrainSurfaceAppearance.hlsl` の手続き地表色・ディテール法線・岩肌・PBR照明を共用する。設定は両方とも `TerrainManager.ApplyTerrainMaterialSettings` から渡す。遠景マテリアル(UltraFar・都市外背景)は `ApplyFarTerrainMaterialSettings` で値を直接書き込む。**`new Material(既存マテリアル)` で複製しないこと**: 手続き色(`_ProceduralGroundBlend`・`_TerrainGroundColorA〜C`)・色被せ(`_TerrainColorGrade*`)・ディテール法線などはシェーダーの Properties に宣言していないので複製で落ち、草テクスチャそのままのオリーブ色になる(`CopyPropertiesFromMaterial` は保持するが、複製元の状態に依存させない)。

**地表種別(マップ単位の地表の見た目)は `GroundCoverPalette.cs` が持つ。** 種別(`Constants.GROUND_COVER_*`)ごとの `GroundCoverProfile` に、地面テクスチャ(Resources パスとタイル寸法)・テクスチャの使い方(色そのまま/濃淡だけ)・スムースネス・手続きの下地色3色・凹凸と風紋・照明後の大きな濃淡と色補正の色・岩の色味と砂浜がまとまっている。値の正は `Resources/Terrain/GroundCoverPalette.asset` で、無い種別はコード既定値 `GroundCoverProfile.CreateDefaults` を使う(草原の既定値は調整済みだったシーンの値そのもの)。マテリアルへ値を移すのは `GroundCoverProfile.ApplyTo` だけで、`TerrainManager` と `Tests/TerrainAppearance` が共用する(対応づけを書き写さない)。種別に依存しない値(大きな濃淡の周期・色補正の標高範囲・ディテール法線の周期・崖の判定)は `TerrainManager` の Inspector に残る。都市の種別は `FetchCoastline` が `GET /api/v1/city` の `ground_cover` から地形タイル生成より前に読み、変わっていれば `RefreshGroundCoverMaterials` で表示中のタイル・超遠景・都市外背景へ `ApplyFarTerrainMaterialSettings` で反映する。Inspector の `groundCoverPreview` を0以外にすると、その種別で描く(Play中の見比べ用)。パレットや Inspector の編集は `groundCoverMaterialsDirty` で次の Update に1回だけ反映する(ドラッグ中の連続通知でタイルを作り直し続けないため)。地面は法線マップを使わない。UltraFarのシェーダー名 `Custom/DepthOffsetSimpleLitFar` は既存参照のため維持しているが、Forwardの照明はPBRである。マクロバリエーション・カラーグレード・等高線は両方とも照明・霧の後に掛ける。遠景だけ `_BaseColor` で色を変えると霧・手続き色への作用が変わるため使わない。地形の色を調整するときは遠景にも反映し、`Tests/TerrainAppearance/run-unity.ps1` で比較描画を確認する。



等高線も `TerrainSurfaceAppearance.hlsl` で共用する。各地表点からカメラまで150m以内は2m補助線と濃い青灰色の縁取り付き線、600m以上は10m/50mの茶色の線で、間は固定標高の線をフェードする。表示トグルは `SetContourLinesVisible` の `_ShowContour`。描画検証は `Tests/TerrainAppearance/run-unity.ps1`、詳細は公開E文書「等高線」。

## 水面と川 (WaterSurfaceManager / RiverSurfaceManager / RiverStore)

湖(`GET /terrain/water`)と川(`GET /terrain/rivers`)はマップ固有の静的データで、`TerrainManager.FetchCoastline` が海岸線に続けて一度だけ取得し、セッション終了(`CellDataLoader.ClearAllManagerData`)で破棄する。湖は `water_level` の水平メッシュ、川は範囲ポリゴンを覆う4m格子面(頂点Y=水面プロファイル線上の最近点標高+0.05m、UV2=流下方向)で、どちらも `SeaObject` タグ+`VisualSeaObject` を付けて海面の表示切替に追従させる。川のマテリアルは海面材質の複製に `_FlowEnabled=1` を設定したもの(海面・湖面の共有材質に流れを付けないため複製する)。

`RiverStore` は川の静的ストア(海岸線の `CoastlineStore` と同じ位置付け)。`RiverRecord` が範囲ポリゴンの内外判定(全リングの even-odd)、プロファイル線の最近線分探索(格子バケット)、水面標高・流下方向、4m格子のラスタマスク(`CornerMask`。水面メッシュと占有箱で共有)を持つ。**「範囲内の水面標高＝プロファイル線上の最近点のy」は地形エディタ・バックエンドと共通の契約**なので変えないこと。`RiverStore.AddOccupancyBox` は川の範囲(または指定点群の近傍だけ)を半透明の占有箱にする。敷設中の川横断判定(`RoadPlanManager.CheckRiverCrossings`)と権威ハイライト(`HighlightManager`)が使う。仕様の正は `D_地形形状.md §D.11`。

川床・川岸の地表は `RiverSurfaceManager.BuildRiverMask` が全域マスク `_RiverMask`(RG8、R=川床/G=内岸の砂地。各川の `CornerMask` を同じ格子線上へ整数オフセットで転記し、外側へ4近傍の幅優先で岸帯の距離を広げる)をグローバルテクスチャで渡し、`Assets/Shaders/TerrainRiverBank.hlsl` が `TerrainSurfaceAppearance.hlsl` の照明直前フックで参照する(川床は `ApplyCliffRock` の `extraRockMask` で岩化+濡れの暗色、砂地はベース色を砂色へ)。色・帯幅・曲率基準は Inspector の `River Bank Appearance`。マスク未生成は `_RiverMaskParams.w=0` で無効。

## 道路 (PathManager.cs) — 知らないと事故るポイント

- **工事音の現場登録は `SavePathToCache` の変更検知内 (`SyncConstructionSound`)**: 工事中(STATUS_CONSTRUCTING)のパスだけ、`CalculatePathPoints` の中心線上で施工の先端にあたる1点(`ConstructionPathBuilder.FrontPoint`=工事車両が並ぶ位置と同じ割合)を `ConstructionAmbience.Register` し、それ以外・`RemovePathFromCache`・`ClearAllCells` で解除する。version/status/端点ノードが変わらない再登録では走らないが、進捗(`construction_progress`)だけが変わった場合は先端が動くので再登録する(Weather/AGENTS.md参照)。`SavePathToCache` の中の順序は「`pathDataCache` 更新 → `RegisterPathCells`(changed 時)→ `SyncConstructionSound`」。工事音の点列を新しい通過セルの地形署名で計算するため、入れ替えないこと。

- **橋脚と周辺道路の依存**: `Awake` が `PillarClearance.NeighborProvider = GetPillarObstacles` を設定する。相手の最新データから切断前中心線・全幅（駅はホーム込み）・通行高さを供給する。コライダーや描画用の `pathPointsCache` には依存せず、地形署名で検証する未切断点列キャッシュ(下記「点列」)を共有で渡し、路面の持ち上げ(`BODY_ABOVE_SPLINE`)は `Corridor.YOffset` で表す(共有の `Points` は書き換えない)。`SavePathToCache` は実変更時に通過セルも先行登録し、変更前後の周辺にある橋脚付きパス（pillar_spacingが0でもBrickArcadeGenerator.HasArcadeが真のレンガ高架を含む）を `pendingPillarRebuildIds` へ積む。`RemovePathFromCache` も削除前に同じ通知を行う。描画revisionと地形追従の完了記録を無効化し、`LateUpdate` がLODバッチ外で既存の再生成キューに渡す。通知の時点の生成通し番号を `pendingPillarRebuildSerials` に記録し、ワーカーは取り出し時に `PathBuildStamp.buildSerial` がそれより大きい GO(通知の後に作り直し済み)を省く(`path.queueRebuildSkipped`)。橋脚だけが理由の項目は、遠景または GO が無ければ省く。送電線の隣接通知(`pendingPowerRebuildIds`)は遠景でも作り直す。曲線の通過セルは `IntersectionPolygonUtil.SamplePlan` の平面点列を使用する（single_spanはアンカー直線）。検証は `Tests/Pillars`。

- **ノードキャッシュの実変更カウンタ `NodeDataVersion`**: ノードの新規追加・位置/タイプの実変更・削除があった場合にのみインクリメントされる公開カウンタ(カメラのセル跨ぎ再取得は大半が無変更のためインクリメントしない)。ノードは編集モード進入後にも非同期ロードで到着するため、`RoadPlanManager` が背景オーバーレイ(既存ノード表示)の再構築判定にポーリング比較で使う。ノード削除時(`SyncNodesInCells`)は `nodeGrid`(空間ハッシュ)からも必ず除去すること(残すと削除済みノードへ吸着してしまう)。全消去時(`ClearAllCells`)も同様に `nodeGrid` / `segmentGrid` を `SpatialHash.Clear()` で空にし、`NodeDataVersion` をインクリメントして背景オーバーレイの再構築を促すこと(カメラ静止中はこのカウンタの変化でしか作り直されないため、通知しないと消えたはずのパス線が残る)。
- **インフラ施設の接続端子ノードの識別は BuildingManager の逆引きで行う**: ノードAPIは所属施設(owner)を公開しない「agent→nodeの片方向参照」の設計のため、`NodeData` に所属施設のプロパティは無い。端子判定は `BuildingManager.TryGetInfraTerminalOwner(nodeId)`(建物側の `infra_node_id` から構築する逆引きインデックス)を使う(`CreateOrUpdateNode` の NodeInputHandler 付与・道路エディタの端子マーカーが使用。なおノードが「インフラノードか」自体は `type` にインフラ財ビットが立っているか=`InfraNetworkUtil.IsInfraMask` で判定できる)。
- **セル逆引きの規約**: セル同期・アンロードは、ノードのセル逆引き `nodeIdsByCell`(`nodeDataCache` の追加・座標変更・削除時に `IndexNodeCell` / `UnindexNodeCell` で更新)と `unregisteredPathObjectIds`(通過セル未登録のパスGO)を使い、プール全体を走査しない。**`nodeDataCache` に書くときは `IndexNodeCell` を呼び、`pathObjectsCache` への登録・削除は `SetPathObject` / `RemovePathObject` を通すこと。** 空間ハッシュ `segmentGrid` は分割点ごとの登録を連続する同一セルで重複排除し、登録と削除を同じ点列(`ApplySegmentToGrid`)で行う。
- ノードとパスはマージBBOXで一括取得(`LoadMergedNodes` / `LoadMergedPaths`)。パスのティアは `DeterminePathLodTier` が「そのパスが覆う全セル」の最高ティアで決める(セル跨ぎ対応)。通過セルは登録済みキャッシュ `pathCellsById` を参照する(`GetPathCoveredCellsCached`)。**version が変わったパスは形状が変わった可能性があるため、`RegisterPathCells` でキャッシュを更新してからティア判定すること**(LoadMergedPaths のフェーズ2はこの順序になっている)。`RegisterPathCells` は同じ version で登録済みなら省略する(`pathCellsVersion`。形状は version と一緒にしか変わらない前提)。
- **交差点接続パスのO(1)ルックアップ**: 交差点更新時の全パス線形走査を避けるため、`pathsByNodeIndex`（ノードID -> 接続パスIDのHashSet）インデックスが維持されている。キャッシュへの追加・削除は必ず直接 `pathDataCache` を操作せず、`SavePathToCache` / `RemovePathFromCache` ヘルパーを通すこと。
- **地形更新時の全件走査回避**: 地形Ready時に交差するパスを再生成する `OnTerrainTileReadyHandler` では、`pathSegmentsByPathId` の全件走査を避け、すでに通過セル情報を持っている `pathIdsByCell` から影響を受けるセルのパスIDのみに絞り込んでセグメント判定を行う。
- **地形更新起因のパス再生成は視野優先・フレーム予算制のキューで行う**: `OnTerrainTileReadyHandler` はハンドラ内で同期一括再生成せず、`queuedTerrainFollowPathIds` へ積む。常駐ワーカー `TerrainFollowRebuildWorker` が「視野内かつカメラに近い」パスから順に、再生成時点の現在ティア・カメラ位置(描画LOD)で `TryRebuildPathOnce` する(ロード範囲外に出たパスは再生成しない)。**投入は必ず `EnqueueTerrainFollowPath(id, changeSerial, reason)` を通す**(投入元は地形タイル到着・橋脚/送電線の隣接通知・近接線路・交差点の切断点変化・LRT停留所のホーム範囲の変化)。処理順は `RebuildTerrainFollowOrder` がまとめて決め、並べ替えるのは末尾に達したとき・stale のまま10フレーム経ったとき・カメラが50m以上動いたとき・向きが20度以上変わったときだけ。LODバッチ中は `lodBatchPendingBuildIds` と `deferredIntersectionPathRebuildIds` に含まれるパスを取り出さない。ワーカーの再生成は `batchMember:false` で、バッチの重複除外に数えない。道路GOのルートには `PathBuildStamp`(生成時の地形署名・version・status・進捗・描画revision・生成通し番号 `buildSerial`)が付き、タイル到着だけが理由のキュー項目(`terrainOnlyQueuedPathIds`)は `IsTerrainCurrent` が真なら作り直さない(`terrainFollow.pathTerrainUnchanged`)。地形ロード完了後の一括追従 `RebuildPathShapesForCellsCoroutine` も、対象パス・交差点ノードを視野優先スコアで昇順ソートしてから処理し(1本の再生成が重く1フレームに1〜数本しか進まないため、「カメラに映っている道路から地形に馴染ませる」順序が体感品質を決める)、`IsTerrainCurrent` が真の道路は作り直さない。
- **パスの描画LODと遠景の帯**: `DeterminePathDrawLod` がデータティアに依らず、アンカー点列(始点・経由点・終点)への3D最近接距離だけで決める。アンカー点列と長さは `anchorPolylineCache` にキャッシュし、`pathDataCache` の同一インスタンスのときだけ有効(読み取り専用で共有する)。無効化は `InvalidateTerrainFollowPath` に集約され、データ変更・地形タイル到着(3×3)・アンロード・`ClearAllCells` で捨てる。版が同じ再受信では新しいインスタンスへ引き継ぐ。256m以上の通常道路は連続パーツを `SplinePartLod` が固定区間化し、区間単位でLODを選ぶ。路面本体を固定区間化した道路は親を Normal に固定する(親を作り直すと全区間・設備・橋脚を再生成するため)。区間化されているかは、生成済みなら実物(路面本体の `SplinePartLod` の有無=`HasBodySegments`)で、未生成なら `IsSegmentedLongRoad` の推定(model_type=default・送電線/索道でない・工事中でない・single_span でない・base_height<=0・曲線または経由点なし)とアンカー長256m以上で判定する。推定が外れても再判定で1回作り直して収束する。線路(track)は親単位でLODを切り替える。動く詳細窓は使用せず、ルートのdetailFrom/Toは0..1。Farルートの帯と交差点の再生成規則は維持する。強制更新で形状revisionを進め、単なるLOD切替はキャッシュを使う。
- **描画LODの再判定(`PathDrawLodReevaluationWorker`)**: `LodViewState.UpdatePosition` でカメラが4m以上動いたら、`pathObjectsCache` のキーの写しを共通予算(`PerformanceConfig.SliceBudgetMs`)のスライスに分けて走査し、LODが食い違うパスをその場で `TryRebuildPathOnce` する(再生成キューには積まない)。LODバッチが始まったら走査・再生成ループを打ち切り、基準位置を捨てて次の周期で走査し直す(バッチ中に作ると、交差点の切断点が確定する前の形がバッチの生成済みとして数えられ、バッチ側の再生成が重複スキップされるため)。カメラ消失で打ち切ったときも同じ。
- **交差点と道路端の相互依存**: ノードごとに `NodeMeshFactory`(→`RoadIntersectionMeshGenerator`)が交差点ポリゴンを生成し、`ConnectionPoints`(各接続道路の切断点)を返す。PathManager はこれを `pathStartConnectionPoints` / `pathEndConnectionPoints` にキャッシュし、`CalculatePathPoints` が道路の端点をこの切断点に差し替える。切断点・セットバックが変わった道路は `OnIntersectionCutChanged` が `connectionRevisions` と `geometryRevisions`(描画キー)を進め、LODバッチ中は `deferredIntersectionPathRebuildIds`、それ以外は再生成キュー(`EnqueueTerrainFollowPath`、理由 Other)で作り直す。直接 `CreateOrUpdatePath` を呼ばないので、必ず `DeterminePathDrawLod` を通る。
- **LOD一括更新中(`isLodPathBatchActive`)の遅延処理**: 上記の依存関係のため、一括ロード中は交差点更新を `deferredLodIntersectionNodeIds`、交差点由来の道路再生成を `deferredIntersectionPathRebuildIds` に積み、全パスのデータキャッシュが揃った後のフェーズで処理する。`lodBatchRebuiltPathIds` で同一パスの重複再生成を防ぐ。**この仕組みを迂回してロード中に直接 `CreateOrUpdatePath` を呼ばないこと。** フェーズ1とフェーズ2は共通予算で区切る(変化なしの continue も予算判定を通る)。`lodBatchPendingBuildIds` はフェーズ1で全受信IDを登録し、フェーズ2で変化なしを外し、`EndLodPathBatch` でクリアする(再生成ワーカーはこの集合のパスを取り出さない)。バッチ中に新規作成したノードの交差点は即時に計算せず `deferredLodIntersectionNodeIds` に積む(`awaitingIntersectionNodes` と `deferredNewNodeIntersectionIds` にも入れる)。`EndLodPathBatch` は延期した切断点の変化を、変化した時点の通し番号で再生成キューへ移す(`RequeueUnfinishedIntersectionRebuilds`。バッチ内で作り直し済みのものはワーカーが省く)。取り消されたバッチの一括取得は `lodFetchGeneration` と `GetBulk` の `isCanceled` で打ち切る。交差点用ノードの取得中フラグ `fetchingIntersectionNodeIds` は、取得コルーチン(`FetchNodesBatchCoroutine` / `FetchNodeAndUpdateIntersection`)の finally で必ず外す(取消しではコールバックが呼ばれない。残ると後続バッチがそのノードを永久に待ち、`IsLodBatchActive` が下りず地形追従が止まる)。
- **未切断点列のキャッシュ**: `CalculatePathPoints` は、`pathDataCache` の現在のインスタンスで通過セルも登録済みのパスについて、未切断点列を `untrimmedPointsCache` に地形署名(`TerrainManager.GetTerrainSignature(通過セル)`)付きでキャッシュし、呼び出し側には常に新しいリストを返す。地形署名が不明(TerrainManager 不在)のときはキャッシュしない。捨てるのは `SavePathToCache` の changed・`RemovePathFromCache`・アンロードで隠した道路・`ClearAllCells`。版が同じ再受信と進捗だけの変化では source を付け替える。本体は `ComputeUntrimmedPathPoints` で、バックエンドの `_auto_fill_controls` と同じ式を保つ(下記)。公開 API `GetPathGeometryRevision(pathId)` は `((long)untrimmed.revision<<32) | (uint)connectionRevisions[id]` で、キャッシュ対象外なら -1(呼び出し側は点列比較にフォールバックする)。切断点・セットバックの変化(`UpdateNodeIntersectionMesh`)と切断点の削除(`RemoveNodeIntersectionMesh`)で下位が、点列の再計算とアンロードで上位が変わる。車両・路駐・公共交通ハイライトが形状の変化検出に使う。
- 道路の形状計算(`CalculatePathPoints`): すべて三次ベジエ。制御点が未指定のアンカーは Catmull-Rom 接線(`T_i = P_{i+1} − P_{i-1}`、テンション1/6、端点は片側接線。ウェイポイントなしは1/3・2/3内分=直線)から自動計算する。**これはバックエンド `Path._auto_fill_controls` と同一ロジックであり、変える場合は両方変える**(バックエンドは作成・形状更新時に未指定の制御点を同式で埋めて永続化し、ウェイポイントの制御点はAPIの`waypoints`で、端点の制御点は`start_control`/`end_control`で返す。Unityは保存値を優先使用し、無い場合のみ自前計算にフォールバックする。端点制御点の読み取りは`PathSummaryExtensions.GetStartControlList/GetEndControlList`=リフレクション方式で、endpoint_schemasシート未更新でもコンパイルが落ちない。絶対座標化は制御点直下ではなく所属アンカー直下の地形標高を基準にする=`EndpointControlToWorld`/`PathWaypoint.GetControlInAbsolute`)。約1m間隔(`TERRAIN_SAMPLE_INTERVAL`)でサンプリングし、相対Y=0 の区間は各サンプル点を地形高さ+`RoadPartHeights.ROAD_SPLINE_OFFSET`(0.1m)に追従させる(Y座標の考え方はルートAGENTS.md参照。道路パーツの高さオフセット体系は `MeshGenerators/Paths/RoadPartHeights.cs` に集約されている)。交差点の切断点も同じ基準面のYで返ってくる。ノードの相対Yは `UpdateNodeIntersectionMesh` が `NodeMeshFactory.GenerateNodeMesh` へ渡し、本線のサンプルを渡す経路では高架も切断位置の実際の高さ・接線で接合される。サンプル未指定の仮想ノードだけが水平面モードを使用する(MeshGenerators/AGENTS.md参照)。交差点歩道コネクタの実体化は `RoadPath.CreateSidewalkConnectorObject`(共通静的メソッド)を使う。
- **線路ノード(ポイント・平面交差)のレールコネクタ**: `NodeMeshResult.RailConnectors`(転回制約を満たす通行可能な線路ペアにのみ計算される。MeshGenerators/AGENTS.md の「線路」の節参照)を `UpdateNodeRailMesh` が NodeMesh の子 `Rail` として実体化する(`TrackPath.CreateRailConnectorObject`。次フレームの Update を待たず、その場で構築して SplineMesh を無効化する)。歩道コネクタ(`UpdateNodeSidewalkMesh`)と同じ「全破棄→作り直し」の差分方針。
- **交差点の信号機**: `NodeMeshResult.TrafficSignals`(設置条件と配置は MeshGenerators/AGENTS.md の「交差点の信号機」)を `UpdateNodeSlab` が `TrafficSignalBuilder.Apply` で NodeMesh の子 `Signals`(全信号機を結合した1メッシュ)として作り直す。交差点の板と同じく入力署名が変わったときだけ更新され、非表示・破棄も NodeMesh に従う。
- **近接線路の結合描画(複線見え化)の隣接供給と無効化**: `Awake` が `TrackAdjacency.NeighborProvider = GetAdjacentTrackNeighbors` を設定し、TrackPath へ近接候補線路(通過セル+隣接セルの同種線路。LRT(`LrtRoadParts.IsLrt`)と通常鉄道は互いに候補にしない。工事中除外)の点列(`pathPointsCache`、無ければ `CalculatePathPoints`)と幅を供給する。LRTの線路には停留所のホーム区間(`TrackNeighborInfo.LrtPlatformZones`。相手の点列を描画点列として `LrtPlatformParts.Resolve`→`ZoneRanges` で求めた3D累積距離)とホーム外縁(`LrtPlatformOuter`)も載せ、結合コリドーの主線路が相手のホームの外に架線柱を立てられるようにする。線路の**データ変更**(LoadMergedPaths フェーズ2の version変化/新規、SyncPathsInCells の削除)時は `EnqueueAdjacentTrackNeighborRebuilds` が近接線路を `pendingTrackNeighborRebuildIds` へ積み、LODバッチ外なら即時・バッチ中なら `EndLodPathBatch` 時(このバッチで生成済みのパスは除外)に `queuedTerrainFollowPathIds` へ流して TerrainFollowRebuildWorker に再生成させる。**並走線路自身の version は変わらないため差分更新では描き直されない**のを補う仕組みで、再生成はデータを変えないため連鎖しない。削除時のエンキューは通過セルキャッシュを参照するため `UnregisterPathCells` より前に行うこと。
- **高速道路の本線とランプの結合描画の隣接供給と無効化**(MeshGenerators/AGENTS.md「高速道路の本線とランプ」、G文書§11.7): `Awake` が `RoadAdjacency.NeighborProvider = GetAdjacentRoadNeighbors`(本線にはランプ、ランプには本線の、通過セル+隣接セルの稼働中の近接候補。点列は `GetUntrimmedPoints` の切断前点列)と `RoadAdjacency.EndJoinProvider = GetRoadEndJoin` を設定する。**ランプの切断前点列は本線へ寄せた描画高さ**で、`ComputeUntrimmedPathPoints` が保存縦断どおりの点列(`ComputeDesignedPathPoints`)に `RoadAdjacency.ApplyRampHeights` を掛ける。本線の点列はランプに依存しないので再帰しない。ランプの点列キャッシュの署名と、ランプが接続する交差点の入力署名には、近くの稼働中の本線の版・地形署名(`ComputeRoadMergeSignature`)を混ぜ、本線の変化で自動的に計算し直す。高速道路の**データ変更**(線路と同じ3か所)時は `EnqueueAdjacentRoadMergeRebuilds` が相手側を `pendingTrackNeighborRebuildIds` へ積んで描画revisionを進め、本線の変更なら近くのランプの両端ノードを呼び出し側のノード集合へ積む(パスの登録・削除を終えてから交差点を更新するため)。
- **板を持たない合流ノード**: `MotorwayMergeLayout` が本線をノードでつなぐと判定したノードは `NodeMeshResult.Mesh` が null で `MotorwayMerge` を持つ。`UpdateNodeIntersectionMesh` はこれを失敗扱いにせず、板のGOを破棄して `slablessMergeNodes` に記録し(入力署名が一致すれば再計算しない)、切断点(本線はノード端、ランプは本線の路面へ入りきる位置)を通常どおり反映する。本線の端は `roadEndJoins`(`RoadEndJoin`: 分離帯の端を丸めない・つなぎ目の橋脚はpath_idの小さい方だけ)に記録し、変化したら道路を作り直す。`RemoveNodeIntersectionMesh`・パス削除・`ClearIntersectionMeshes` で解除する。車両は `GetMotorwayMerge(node).TargetLane` で進入車線を決める。
- **都市共通設定のストア(`CityInfoStore.cs`)**: `GET /api/v1/city` の `traffic_hand`(通行方向。ゲーム開始後不変)を保持する静的ストア。`PathManager.Start` が `EnsureLoaded(this)` で一度だけ取得する。駅パスのプラットフォーム設置側(platform_side の内側/外側→左右の解決。`TrackPath.ResolvePlatformExtents`)が参照する。取得前・失敗時は既定の左側通行として動作する(車両描画の VehicleManager は自前取得で独立)。
- `curved=false` でウェイポイントを持つ道路は、角を交差点と同じロジックで切断して別メッシュで埋める(`RoadPath.GenerateLinearPathWithCorners`)。**線路(TrackPath)は角の切断・充填を行わず、Broken点の1本スプラインとして描く簡易表現**(線路は通常 curved=true で敷設される想定)。
- セル跨ぎの道路を誤って非表示にしないため、アンロード時は `cellsToKeepVisible` / `pathCellsById` を参照する。アンロードは Destroy ではなく `SetActive(false)`(プーリング)。`UnloadCellsObjects` は離脱セルを待ち行列 `pendingUnloadCells`(世代付き)へ積むだけで、常駐ワーカー `PathUnloadWorker` がフレーム予算内で1セルずつ `nodeIdsByCell` / `pathIdsByCell` の逆引きからIDを写し取って隠す(通過セル未登録のGOだけは `UnloadCellsObjects` が同期に隠す)。再開のたびにセルが常駐へ戻っていないか・再登録されていないかを確かめ、パスは隠す直前にその時点の `cellsToKeepVisible` に跨っていないかを見る。`cellsToKeepVisible` に入っている待ちセルは処理せずに残し、`ApplyLodCellChanges` でロード済みへ戻ったときに外す(戻ったバッチが適用前に取り消されても、再び常駐外になれば隠し残しを回収するため)。道路GOに加えて、接続道路がすべて非アクティブになった交差点メッシュ(`NodeMesh_*`。道路GOの子ではない)も SetActive(false) する。道路GOを表示に戻す経路と `CreateOrUpdatePath` / `CreateOrUpdateFarPath` の入口で `EnsureEndIntersectionsVisible` が両端の交差点を再表示する。**新しく道路GOを SetActive(true) する経路を足すときは、必ず `EnsureEndIntersectionsVisible` を呼ぶこと。**
- 接続パスが揃わず交差点を作れなかったノードは `awaitingIntersectionNodes` に記録され、後からパスが追加された時に自動再評価される。
- **交差点の入力署名(`nodeIntersectionSignatures` / `ComputeNodeIntersectionSignature`)**: 交差点メッシュ生成の入力を署名し、前回と一致すれば再計算しない(`intersection.inputUnchanged`)。含めるものはノード座標・種別・駅パス、左右通行、ノード周囲±150mと接続道路の地形署名、接続道路ごとの版・状態・工事進捗・反対端の接続数、接続数。署名を捨てるのは失敗時・交差点削除・道路削除(両端)・全消去。地形署名が不明、またはマスターが未初期化のときは省略しない。**交差点生成器(`NodeMeshFactory.GenerateNodeMesh` / `GetIntersectionCutLimit`)に新しい外部依存を足したら、署名にも足すこと**(足さないと、依存先が変わっても交差点が古いまま残る)。
- **交差点の接続パス数には防衛的上限がある (`MAX_INTERSECTION_CONNECTED_PATHS`=32)**: 交差点ポリゴン計算(`IntersectionPolygonUtil`)は接続パス全ペアのO(N²)処理のため、バックエンドのデータ異常(1ノードに大量のパスが接続)でメインスレッドがフリーズする。上限超過ノードは交差点メッシュを生成せずエラーログ(ノードごとに1回)を出し、`awaitingIntersectionNodes` にも積まない。
- **地下パス／非追従パスの描画区間分割**: `CreateOrUpdatePath` は `UsesTunnelMesh`（=`IsUndergroundPath` または「実効非追従(`GetPathFollowTerrain(path)`=false)かつ`HasTunnelSection`」）が真のパスを `GenerateUndergroundPathMesh` へ分岐し、点列を `TunnelSectionUtil.SplitDrawableRuns(points, clearance, portals, GetPathFollowTerrain(path))`（深さ=現在地形標高−道路Yがマスターの `height` を超える区間を除外）で分割して区間ごとにメッシュ生成する。トンネル区間はメッシュ・コライダーなし、境界には `portal` パーツを配置。**相対Y=0の非追従パスも丘に潜れば同じ分割対象になる**（谷を渡るだけの非追従橋はトンネル区間が無いので通常メッシュ＝柱付きで描く）。Farティアは `IsUndergroundPath` に加え `FarPathHasHiddenTunnel`（端点直線がトンネル区間を持つ非追従パス）も非表示にし、谷渡りの非追従橋は遠景の帯を描く。掘削は着工時にバックエンドが行い、地形リロード後の再生成で開削区間が自動的に描画対象へ変わる（バックエンド `geo_path_depth.py` の `path_uses_depth_classification` と同一の分類定義）。実効地形追従の解決は `PathManager.GetPathFollowTerrain(PathSummary)`（パス個別`follow_terrain`優先→バリアント既定）で、バックエンド `path_follows_terrain` と一致させる。パス個別の `follow_terrain` は生成モデル上 `int`（-1=未設定/0=非追従/1=追従。`PathSummaryExtensions.GetFollowTerrain` で読む）なので、**プレビュー等で仮の `PathSummary` を組み立てて生成器へ渡すときは `follow_terrain = Constants.VALUE_NONE` を明示する**（intの既定値0は「非追従」を意味し、指定漏れのスタブは地形に沿わない直線スパンで描かれる）。
- **工事中パスの表示**: `GenerateConstructingPathMesh` は `ConstructionPathBuilder` に委譲する。進捗で土工・橋脚・施工済み路面を切り替え、工事車両を固定配置する。地上は始点から終点への舗装、高架は0〜25%橋脚・25〜90%桁・90%以降は固定。高架には土を生成せず、車両はパス中心線上の地表に置く。土は地表に追従し、ConstructionPathDirt材質を使う。単体・一覧ともConstructionPathSummaryで進捗を取得する。仕様は公開G文書§15。
- **インフラパス(`InfraNetworkUtil.IsInfraPathType`=送電/導水/パイプライン)の垂直結合と垂直コネクタ**: インフラパスの端点は「垂直結合」（端点の相対YとノードYの不一致がデータ上の正規状態）を許容する。このため、(a) **インフラノード(`type`がインフラ財ビットを含む=`InfraNetworkUtil.IsInfraMask`)には交差点アスファルトを生成せず、`ConnectionPoints` によるパス端点の切断・差し替えも行わない**（`UpdateNodeIntersectionMesh` 冒頭のノードタイプ分岐。切断点で端点Yをノード平面の高さに引き戻すと垂直結合が壊れるため）。(b) 端点の絶対高さとノードの絶対高さの差が `INFRA_RISER_MIN_HEIGHT_DIFF`(0.5m) を超える場合、`CreateInfraVerticalConnectors` が高低差を繋ぐ垂直コネクタ（`Models/Paths/p{variant}/riser` プレハブ、無ければ円柱プリミティブ）を pathGO の子として生成する（SetupVisuals より前に生成し、ステータス表示の対象に含める）。ノードがパスより後からロードされた場合は、ノード到着時の `UpdateNodeIntersectionMesh` インフラ分岐 → `RefreshInfraVerticalConnectorsForNode` がコネクタだけを再評価する（パス本体は再生成しない）。

## 建物 (BuildingManager.cs)

- `ConstructionCraneAnimator` は工事中クレーン頂部だけの描画演出。`BuildingManager` が `animateCrane: true` で受け取った配置情報を、`VisualModeObject` の初期化前に子Rendererへ変換する。接続点のY軸まわりの旋回は建物IDと非スケール時刻から決まり、再生成・LOD復帰でも連続する。頂部の結合メッシュと DepthOffset マテリアルは頂部プレハブ単位で static に共有し(セッションを跨いで保持する)、子に `RenderCacheIdentity` は付けない(建物GOの破棄やキャッシュの追い出しで共有物を解放させないため)。コライダーは持たない。Far・静的プレビューでは動かさない。仕様はF5文書§5。

- `CellDataLoader.FetchBuilding` / `BuildingManager.FetchBuilding` は単体取得の共通入口。`playExplosion: true` は爆破成功IPCだけが指定し、取得した倒壊状態の瓦礫への更新と `VisualEffectManager.PlayExplosion` を同時に行う。通信中の重複と同じversionの通知は演出を重ねず、旧versionの一括取得で表示を巻き戻さない。`ClearAllCells`は未完了取得の世代を無効化し、単発演出も全消去する。フットプリント半径は `FireEffectManager.ResolveFootprintRadius` を火災・爆破で共有する。

- 描画LOD(Close / Normal / Far)は `DetermineDesiredDrawLod` が建物ごとの投影比だけで決め、セルのティアでは Far に固定しない(既存オブジェクトの描画LODをヒステリシスの基準にする)。**Far(箱)は `CreateFarBuildingObject` が `BuildingMeshBuilder.Generate(data, false, DrawLod.Far)` で実寸の箱(フットプリント×階数×階高。工事中/解体中=1階分、倒壊=0.5階分、地面アセット=0.3m板)を生成する**(`ObjectIdInfo.isFarObject=true`、`DrawLodInfo.drawLod=Far`)。Normal/Close は `CreateOrUpdateBuildingObject(data, isDynamic, forceRebuild, drawLod)`。
- **建物GameObjectの配置高さ(Y)の分岐は `ResolveBuildingBaseHeight` の1箇所に集約する**: `IsBuildingApplicableForAltitude`(=`BuildingMeshBuilder.IsApplicableModelType`。MODEL_MODULAR_FLAT/HOUSE/GABLE)の建物は土地区画の基準標高 `parcel.altitude`(=接道の路面高さ)、それ以外(GROUND/PREBUILT/PORT)は `center_point` 位置の地形標高に接地し、どちらも `BuildingBaseLift`(0.01m)だけ浮かせてZファイティングを避ける。**海上区画(`PARCEL_WATER`)の建物だけは model_type によらず必ず `parcel.altitude` を使う**——海上で地形をサンプルすると海底(負のワールドY)が返り、洋上風力発電所などが海面下へ沈むため。海上区画の `altitude` はバックエンドが海面基準の0で持つ。傾斜地の谷側の隙間はメッシュ生成側のコンクリート基盤が埋める(MeshGenerators/AGENTS.md の「傾斜地対応」の節を参照)。初期配置・更新・地形追従の高さ再設定(`UpdateBuildingHeight`)はすべてこの共通メソッドを通るため、altitude接地の建物は地形リロードで上下しない。
- **取得範囲**: カメラ由来のバッチ(`LoadLodCells`)は「未ロード・要再取得(`staleBuildingCells`)・ティア変化セル」だけを取得し、それ以外のロード済みセルは受信済みのデータとオブジェクトをそのまま使う(`building.fetchSkippedCells`)。要再取得は `CellDataLoader.ReloadAllData` が要求時に同期で呼ぶ `BuildingManager.MarkLoadedCellsStale` が記録し、取得に成功して `ApplyLodCellChanges` まで進んだときにだけ消える(取消されたバッチでは残る)。取得中に再取得要求を跨いだバッチ(`staleMarkRevision` の不一致)は、取得したセルを要再取得に残す。常駐外へ出たセルの記録は捨てる(再入場時は未ロードとして取り直すため)。取得セルは行ごとの連続区間から作った矩形群(`SplitFetchRects`、最大12個。超えたら外接矩形1つ)に分けて要求し、境界の重複はIDで除く。どれか1つの矩形が失敗したら全体を適用しない。**取得対象外セル(外接矩形に入っただけの常駐外セルと、取り直さないロード済みセル)の建物は生成しない**(`building.outsideFetchSkipped`)。`ClearAllCells` は進行中の建物取得バッチを止める(旧データが後から適用されるとロード済み扱いになり、差分取得で取り直されなくなるため)。
- **セル離脱の分割アンロード**: `UnloadCell` は待ち行列 `pendingUnloadCells`(世代付き)へ積むだけで、常駐ワーカー `CellUnloadWorker` がフレーム予算内で1セルずつIDを写し取り、建物GOを `lodCache`(`RenderObjectCache`)へ Park する。その際 `buildingObjectsCache`・version・詳細台帳(`detailedBuildingIds` など)・地形署名から外すが、データ・セル逆引き・端子/区画インデックス・工事音はデータの生存期間に従うので残す。再入場時は version が無いので needsRebuild になり、`RestoreBuildingLod` がキーでキャッシュから復元する(メッシュは作り直さない。視野外の詳細は遅延キュー経由)。アンロード途中の再入場は `LoadLodCells` の冒頭で中断し、表示物セルに戻す。このためロード済みセルの建物GOは通常常にアクティブで、`activeSelf` / `activeInHierarchy` の分岐は防御的なもの。
- **表示物セル `cellsWithShownObjects`**: 表示中の建物オブジェクトを持ちうる、アンロード中でないセル。`RegisterBuildingCell(id, pos, shown)` で記録し、データだけの登録(遅延・範囲外)は `shown:false` で呼ぶこと。アンロード対象は「`loadedCells` ∪ `cellsWithShownObjects`」のうち常駐外のもので、取消されたバッチが適用前に表示した建物も次のバッチで回収される。
- **フル詳細メッシュには視野ゲートと個数上限(LRU)がある**: 生成/再生成が必要になった建物のうち視野外(カメラ前方75°超かつ100m超)のものは即時生成せず、遅延キュー(`deferredDetailBuildings`)へ積む。常駐ワーカー `DetailGenerationWorker` が「視野内かつカメラに近い」ものから順にフレーム予算内で生成する(視野外も上限に余裕があればアイドル生成する。振り向き時のポップイン軽減)。フル詳細オブジェクト数は `maxDetailedBuildingObjects`(既定2000)で上限管理され、超過時は「視野外で最も長く視野に入っていない」建物からGameObjectをLRU破棄する(データキャッシュは保持し、視野に入れば再生成される)。**遅延中の建物にはversionをキャッシュしない**(ワーカーが生成した時点でキャッシュ。次回マージロードでも「未生成」と判定させ、キューを最新データで更新し続けるため)。Far(箱)は軽量なので視野に関わらず即時生成し、上限の対象外。遅延リクエスト(`DeferredDetailRequest`)は描画LODを持ち、取り出し時に `closeBuildingIds` が `MAX_CLOSE_BUILDINGS` に達していれば Normal に落とす。**遅延キューへの投入は必ず `QueueDeferredDetail` を通す**(`deferredRevision` でワーカーの待機を解くため)。ワーカーは1回の走査で上位32件(`DEFERRED_TOP_CANDIDATES`)の候補群を選び、使い切るか視点が変わるまで使い回す。上限到達中の視野外の詳細化は保留(continue)して後続の候補を見続け、Far で足りる要求は処理する。進展が無ければ、署名(キュー版・件数・詳細数・ロード済みセル版・上限)とカメラが変わるまで走査しない。LRU破棄の候補は、最大15フレーム(`VISIBILITY_SWEEP_INTERVAL_FRAMES`)ごとに作り直す `evictionOrder`(最終可視フレームの昇順)から取り出し、取り出すときに視野内かどうかを確かめ直す。**LRU破棄は、表示可能なセル(アンロード待ちでなく、`loadedCells` か `cellsWithShownObjects` に含まれる)の表示中の建物なら Far(箱)に置き換え**(詳細メッシュは退避キャッシュへ移る。オブジェクトを失った台帳残骸も箱にする)、それ以外(範囲外セル・非アクティブ)は `ParkBuildingObject` で退避して version を消す。差分取得ではロード済みセルを取り直さないので、表示可能なセルでは必ず箱を置くこと(置かないと再入場まで建物が消えたままになる)。
- **描画LODの再判定(`DrawLodReevaluationWorker`)**: `LodViewState.UpdateProjection` で位置・FOV・投影方式の変化を検出し、回転だけでは走査しない。対象はロード済みセルの建物(`buildingIdsByCell` の写し)だけで、共通予算(`SliceBudgetMs`)で分割する(`building.lodScanned` はその延べ数)。箱化(Far)はその場で生成・復元し、詳細化は遅延キューへ積む。待機中の遅延要求のうち Far で足りるものは、再判定の時点で箱にして消化する。
- **ティア変更時の再生成**: セルのティアが Far との間で変化した場合、versionが同じでも建物を再生成する(ティア変化セルは取得対象にも入る)。現在は常駐セルがすべて同じ集合(Dynamic)なので、ロード済みセルでは通常起きない。PathManagerの `RebuildCachedPathsForCells` と同様の仕組み。
- **地形更新時の追従**: 常駐セルの詳細建物はメッシュを再生成し、Far輪郭は高さを再設定する。地形版を再利用キーに含め、旧地形に接地したキャッシュを使わない。`OnTerrainTileReady` のハンドラは同期で再生成しない。建物ごとに記録した地形署名(`buildingTerrainSignatures`。`TerrainManager.GetTerrainSignature(建物の境界円+敷地の外接矩形)`。生成・復元・再利用の時点で記録)と比べ、違うものだけを `UpdateBuildingHeight` で即時に高さ補正し、非Far・Dynamic のものは `QueueDeferredDetail` で遅延キューへ積む(タイルの LRU 復元でも通知が来るため)。TerrainManager が無いときは不一致として扱う。対応建物の敷地装飾はBuildingExteriorRendererの別ジョブで生成する。本体は水平を維持し、地面パネルは実Terrainへ追従する。更新したセルだけを追従対象とする。
- **`CreateOrUpdateBuildingObject` の isDynamic/forceRebuild**: isDynamic はメッシュ生成器(`BuildingMeshBuilder.Generate`)に渡され、フェンス・地面パネルの地形追従を有効化する。`cellBuildingLod` はロードコルーチン完了後の `ApplyLodCellChanges` まで旧ティアのままなので、`LoadMergedBuildings` 中はティア表を参照せず desiredTier を直接渡すこと。また forceRebuild=false の場合、既存オブジェクトのプール再利用(status一致かつ非Far)で early return してメッシュは再生成されないため、メッシュの作り直しが目的の呼び出しでは必ず forceRebuild=true を渡すこと。
- メッシュコライダーは `Physics.BakeMesh` を `Task.Run` でベイクしてから割り当てる(`SetColliderAsync`)。道路も同様。
- **工事中(STATUS_CONSTRUCTING)の建物は毎エポック version が上がり再生成される**: 工事中の建物は進捗に応じた「工事現場」メッシュ(土マウンド+足場+クレーン。`ConstructingBuildingMeshBuilder`、MeshGenerators/AGENTS.md参照)で描画され、バックエンドの建設進捗フェーズ(`progress.py`)が進捗の増えたエポックで version を bump するため、既存の version 差分ロードだけで足場が毎エポック伸びる(BuildingManager側に専用処理は無い)。
- **接続端子ノードの移動に追従するのは車両基地だけ (`OnTerminalNodeMoved`)**: 端子ノードの移動確定(PATCH成功。`NodeInputHandler.SaveNodePosition`)時に呼ばれ、`TryGetInfraTerminalOwner` で所属施設を逆引きして model_type が MODEL_RAIL_YARD の建物だけを `forceRebuild: true` で再生成する(車両基地は端子位置へ線路を収束させて描画するため。versionは変わらないので差分ロードでは追従しない)。このとき `buildingGeometryRevisions`(全体の通し番号)を進めて再利用キーを変え、`RestoreBuildingLod` による旧形状の再利用を外す。他の model_type は端子位置が見た目に影響しないため対象外。電力ビットを持たない車両基地の端子(LRT車両基地)はノードtypeで判別できないので、別の逆引き`buildingIdByDepotNodeId`(agents.depot_node_id。管理規約はinfraと同じ)を`IsDepotTerminalNode`で引き、線路の終端判定(`PathManager.IsInfrastructureConnection`)で車止めを置かない。
- **インフラ接続端子ノードの逆引きインデックス (`buildingIdByInfraNodeId`)**: 端子ノードID(`agents.infra_node_id`)→建物agent_id のインデックス。ノードAPIは所属施設(owner)を公開しない片方向参照の設計のため、フロントの端子ノード識別(スナップ判定・端子マーカー・網互換判定)はこのインデックス経由の `TryGetInfraTerminalOwner` で行う。`buildingDataCache` への登録は必ず `RegisterBuildingDataToCache` を通し、削除は `SyncBuildingsInCells` / `ClearAllCells` で同期すること(`RegisterBuildingCell` 等と同じ管理規約)。
- **工事音の現場登録も同じ管理規約**: `RegisterBuildingDataToCache` 内の `SyncConstructionSound` が建設中・解体中の建物だけ `ConstructionAmbience.Register`(中心点の地表座標)し、それ以外・削除・`ClearAllCells` で解除する。GameObjectやLODとは無関係にデータキャッシュの生存期間で決まる(Weather/AGENTS.md参照)。
- **詳細建物の DepthOffset マテリアルは共有物**: 建物(本体・クレーン頂部)は `BuildingManager.GetSharedDepthOffsetMaterials` を使う。元マテリアルごとに1つの `DepthOffsetLit` 複製を全建物で共有し(セッションを跨いで保持)、`RenderCacheIdentity` には所有させない。広告・看板の共有材質(`Models/Buildings/Materials` の BD_MALL_ADS・BD_SIGN_ATLAS・BD_PARKING_SIGN)は組み込みStandardで画像を `_MainTex` に持つため、複製時に `_BaseMap`/`_BaseColor` へ写す(写さないと DepthOffsetLit が白い板で描く)。**建物レンダラーの `.material` / `.materials` を個別に書き換えたり、`sharedMaterials` の要素の色を直接変えたりしてはいけない**(全建物に波及する)。色替えは `VisualModeObject` の sharedMaterials 差し替えとプロパティブロックで行う。遠景メッシュ(prebuilt_far を含む)は共有マテリアル1つで描くため `CreateFarBuildingObject` で単一サブメッシュに畳む。遠景GOはコライダーを持たないので、生成されたコライダー用メッシュはその場で解放する。
- **公共施設ハイライトの光オーバーレイは BuildingManager が起動する**: `Awake` の `PublicFacilityGlowOverlay.Ensure()`。描画本体は `VisualHandlers/PublicFacilityGlowOverlay.cs`(建物データをポーリングし、敷地の土地区画形状に黄色い光を描く。詳細は VisualHandlers/AGENTS.md)。
- **土地区画の逆引きインデックス (`buildingIdsByParcel`)**: 区画ID(`agents.parcel_id`)→その区画上の建物agent_id集合。「この区画の上に建っている建物」を全建物走査なしで解決するための汎用の逆引き(`GetBuildingIdsByParcel`)。`buildingIdByInfraNodeId` と同じ管理規約で、登録は `RegisterBuildingDataToCache`(parcel_id 変更時は旧マッピング除去)、削除は `SyncBuildingsInCells` / `ClearAllCells` で同期する。**ただし `GET /buildings/` の `multi_agent` スキーマに `parcel_id` が無いため、一括ロードされた建物の `parcel_id` は常に0でこのインデックスは実質空**(単体取得の建物だけが入る)。区画から建物を引く必要がある処理は、endpoint_schemas シートへの `parcel_id` 行追加まではこのインデックスに依存できない(`PublicFacilityGlowOverlay` は `ParcelManager.GetParcelIdAtPoint` による空間判定で回避している)。

## 区画 (ParcelManager.cs) / 地区 (ZoneManager.cs) / 天然資源 (ResourceManager.cs)

- パーセルにティアはない。上面ポリゴンは頂点ごとに地形高さへ追従させる(`SampleParcelGround` が各頂点と中心を1回ずつ `GetParcelBaseY` でサンプルし、`ApplyParcelGeometry` が position・上面メッシュ・コライダー・枠線をまとめて設定する)。**メッシュと枠線は常に同じサンプルから作る**(下記の高さ一致判定の前提)。同じ usage のまま版が上がった区画は `RefreshParcelGeometry` を通り、形状か高さが違えばメッシュ・コライダー・枠線を作り直す。区画 GO の破棄は `DestroyParcelObject`(sharedMesh も破棄する)に一本化し、マテリアルは sharedMaterial で共有する。
- **取得範囲**: 取得は `cellsNeedingFetch`(新規に常駐へ入ったセル・`MarkLoadedCellsStale`・isForce・常駐集合が変わらないときの全セル)の外接矩形だけで行い、応答は重心セル(`center_point`→`WorldToCell`)で絞り込む(外接矩形は常駐外のセルも含み得るため)。`PARCEL_FETCH_LIMIT`(5000)に達したら、打ち切られた応答は適用せずセル集合を長辺軸で二分して取り直す。単一セルで上限に達した場合は、適用はするが同期(応答に無い区画の削除)はしない。`MarkLoadedCellsStale` は `CellDataLoader.ReloadAllData` の先頭で同期に呼ぶ契約で、記録は取得が成功して適用されたときにだけ消える。取得中に `MarkLoadedCellsStale` が来たら、そのセルは要再取得のまま残す(`staleMarkSerial`)。
- **重心セル逆引きとアンロード**: `parcelIdsByCenterCell` / `parcelCenterCellById`(登録は `RegisterParcelDataForLines`→`RegisterConvertedParcelData`、削除は `RemoveParcel` / `ClearAllCells`)。アンロードは `UnloadCell` で `pendingDeactivateCells` に積むだけで、`UnloadWorker` が32件ずつ予算分割で `SetActive(false)` する。処理中のセルが中断中に再入場した場合は(再び離脱していても)打ち切り、pending に残っていれば次の周回で先頭からやり直す。非表示化を終えたセルは `retainedUnloadedCells` に古い順で積み、`MAX_RETAINED_UNLOADED_CELLS`(=`LodConfig.MAX_RESIDENT_CELLS`=81)を超えたら古いセルの区画を `RemoveParcel` する(16件ごとに予算を返し、再入場したら中止する)。`SyncParcelsInCells` は重心セル逆引きでセル内の区画だけを見る。
- **地形タイル到着(`OnTerrainTileReady`)**: `parcelIdsByCell`→`ParcelTouchesCell` で候補を絞って `terrainRefreshQueue` に積み、`TerrainRefreshWorker` が予算内で処理する。再サンプルした高さと XZ が枠線の保持値と同じなら(高さ5mm・XZ 1e-4 以内)、メッシュ・コライダー・枠線を作り直さない(LRU の復元や版の変わらない再アタッチでも通知が来るため)。区画のどのセルにも地表タイルが無くなっていれば飛ばす。
- **版一致時の更新**: version が一致したときも `RefreshCachedSummary` で `parcelCache`・`VisualParcelStats`・接道インデックスを最新にする(`land_price` と `front_path_id` は `VERSION_TRIGGER_PROPS` に含まれず、version が同じでも変わり得るため)。usage が同じまま版が上がったときも Stats を更新する。`FetchParcel` は `parcelVersionCache` も更新する。
- **区画が載る面のワールドYの解決は `GetParcelBaseY(parcelData, x, z)` の1箇所に集約する**: 通常の区画は `Utils.GetGroundHeight`、**海上区画(`PARCEL_WATER`)はバックエンドが海面基準で持つ `altitude` に `SeaSurfaceClearance`(0.05m)を足した値**を返す。海上で地形をサンプルすると海底(負のワールドY)が返るうえ、海面板はワールドY=0.001 にあるので地形用の微小オフセット(`ParcelSurfaceOffset`=0.002m)だけでは海面より確実に上へ出せない。上面メッシュ・枠線(`CreateParcelOutline`)・中心点・建設完了エフェクトの発生位置はすべてこの入口を通す(**地形の再サンプルを個別に書かないこと**。海上で海底を拾って区画が海面下へ沈む)。上面メッシュと枠線のサンプルは `SampleParcelGround` で1回だけ取り、両者で共有する。海上区画は基準面が水平なので上面頂点のローカルYは全て0になる。ホバーハイライトは枠線頂点が既に区画の基準面に載っているため、オフセットを `HighlightHeightOffset − OutlineHeightOffset` だけ差し替えるだけにする。
- **マウス位置からの区画検索 (`GetParcelIdAtPoint`)**: 指定ワールド座標XZを含む区画IDを2Dポリゴン内外判定で返す公開メソッド。起伏地形では区画のMeshCollider(上面はポリゴン角の頂点でしか地形をサンプルしない)が地形の下に埋まってレイキャストで拾えないため、InputHandler が「最初の有効ヒットが地形だった場合」のフォールバックとして呼ぶ(`InputHandlers/AGENTS.md` の基本フロー参照)。全件走査を避けるため、セル逆引きインデックス `parcelIdsByCell`(セル→BBOXが重なる区画ID集合)→BBOX→内外判定の順で絞り込む。BBOX・変換済みポリゴン・登録セル一覧は `parcelSpatialCache` に保持され(マウス移動フレームごとの `GetPolygonArray` アロケーション回避)、登録は `RegisterParcelDataForLines`(内部で `RegisterParcelSpatialIndex`)、削除は `RemoveParcel` / `ClearAllCells` で同期される。**`RegisterParcelDataForLines` は VehicleQA がリフレクションで名前だけで引くので、多重定義にしないこと**(変換済み座標版は `RegisterConvertedParcelData`)。変換済みポリゴンは `TryGetParcelPolygon` で確保なしに読める(共有配列なので書き換えない)。
- **区画内判定・区画内クランプの公開API (`IsPointInParcel` / `TryClampPointToParcel`)**: 指定区画IDに対する2Dポリゴン内外判定と、区画外の点をポリゴン境界の最近傍点(から0.05m内側へ寄せた点)へ射影するクランプ。`ConnectorEditManager`が接続端子ノードのドラッグ移動を「施設が立地する区画内のみ」に制限するために使う(境界ちょうどの座標はバックエンドの座標丸めで区画外判定に落ち得るため内側マージンを取る)。どちらも`parcelSpatialCache`ベースで、区画未ロード時はfalseを返す(呼び出し側は制限なしにフォールバック)。
- **接道街路の逆引きインデックス (`parcelIdsByFrontPath`)**: 街路ID(`front_path_id`)→その街路に面する区画ID集合。「この道路に面している区画」を全区画走査なしで解決するための汎用の逆引き(`GetParcelIdsByFrontPath`)。登録は `RegisterParcelSpatialIndex`(`ParcelSpatialEntry.frontPathId` に保持)、削除は `UnregisterParcelSpatialIndex` / `ClearAllCells` で同期する。`front_path_id` は parcels の `VERSION_TRIGGER_PROPS` に含まれず接道だけが変わっても version は上がらないので、version 一致時も `RefreshCachedSummary` で付け替える。**`front_path_id` は `ParcelSummary`(multi_parcel)のフィールドで、`ParcelSummaryExtensions.GetFrontPathId`(リフレクション)経由で読む**(endpoint_schemas シート未再生成でもコンパイルが落ちないための措置。`GetPlatformSide` と同じパターン。未再生成時は 0 を返し index は空=道路のみ色付く)。
- **環境スコアの色分け (`environmentScores`)**: 騒音(`VisualModeParcel.NOISE`)・大気汚染(`AIR_POLLUTION`)・治安(`CRIME`)の区画色分け用に、`GET /city/noise`・`/pollution`・`/crime` から取得した `parcel_id`→0〜1 の相対値を保持する。**これらのスコアは `ParcelSummary`(multi_parcel)に無い**ため、区画のbbox取得とは別経路で取る。値は都市全体分が1回のレスポンスで返るので、`ApplyVisualMode` でモードが切り替わったときに `RequestEnvironmentScores` が種別ごとに1回だけ取得し(同じモードを選び直したときはエポックで値が変わるため取り直す)、完了時に `ForceOutlineRedraw` で塗り直す。応答時に別モードへ切り替わっていたら `requestedEnvironmentMode` の不一致で破棄する。スコアを持たない区画(=エポック未実行でヒートマップ未計算、または対象外)は塗らない。**色はそのエポックの最小値〜最大値を両端まで引き伸ばした相対値**なので、濃さは都市内での相対的な悪さを表す(絶対水準はレスポンスの min/max 側にある)。
- パーセルの枠線・ホバーハイライト・データ可視化(地価等)は GameObject ではなく `OverlayMeshRenderer` で一括メッシュ描画する。カメラが1m/2°以上動いたフレームだけ再構築。距離フェードあり(480mで非表示)。データ可視化の塗りは `ParcelOutlineData.fillTriangles`(遅延生成・不変)を `AddPreTriangulated` に渡し、色は `ResolveDataVisColor` が決める(区画個体の `VisualParcelObject` は Stats を渡すだけで VisualModeHandler に登録しない)。`ZONE_COLOR` の所属地区は区画ごとに `ZoneManager.ZoneRevision` と組でキャッシュし、地区データが変わったときだけ `GetZoneIdAtPoint` で再判定する。枠線データは作り直すたびに新しいインスタンスになる(中身は書き換えない)ので、これらのキャッシュは無効化が要らない。
- **枠線/ハイライトは地形に埋もれさせない**: オーバーレイは `OverlayMeshRenderer.Create(..., alwaysOnTop: true)` で生成し、そのレンダラー専用マテリアルだけ `_ZTest = Always(8)` にする(RoadPlanManager のギズモと同じ対応)。シェーダー既定の `LEqual`+ポリゴンオフセットでは傾斜地で平板ポリゴンが曲面地形の下に食い込んで埋まったように見えるため。代償として丘の裏や手前の建物を貫通してX線状に見えるが、強調表示用途(枠線・ホバー・パス干渉ハイライト)なので許容。**シェーダー本体や ZoneManager/TerrainManager のセル枠には影響しない(マテリアルインスタンス限定)。**
- **データ可視化(色分けフィル)だけは別レンダラー `dataVisRenderer`(深度テストあり+GPU地形追従)で描く**: 表示パネルの色分けモード(地価・地区色・建築可能判定等、mode>=200)のフィルを `_ZTest=Always` で描くと画面全域の塗りが建物を覆い隠してしまうため、第2の `OverlayMeshRenderer`(`ParcelDataVisMesh`、`ParcelOverlayMesh` の子で表示ON/OFFは親に連動)に分離し、専用シェーダー **`Custom/OverlayFillTerrainConform`**(ZTest=LEqual)で描く。このシェーダーはGPUテッセレーションで三角形を約2m間隔に細分化し、全頂点をグローバル高さマップ `_TerrainHeightmap`(`HeightmapTextureManager` が供給、`TerrainHeightmapConform.hlsl`)の地形高さ+`_TerrainLift`(0.3m)に張り付ける。**CPU側で頂点ごとに地形サンプリングした平板/箱を深度テストありで描く方式は禁止**: 頂点間(三角形内部)の地形の盛り上がりに勝てず、傾斜地で水たまり状の欠けが出る(既往バグ)。深度関係は「地形より上(テッセレーション追従+負のポリゴンオフセット-1,-4で常に勝つ)・建物より下(不透明の壁に自然に隠れる)・地表すれすれの floor パーツより上(リフト分手前)」。`_GpuTerrainConform` が0(高さマップ未アップロード)の間は頂点Yのフォールバック平板になる。BeginFrame/EndFrame/ClearMesh は overlayRenderer と常に対で呼ぶこと。
- ZoneManager は地区枠線(OverlayMeshRenderer)と地区名ラベル＋建物アイコン(2D UIオーバーレイ)を描画する。地区の塗りつぶし板ポリゴンは通常描画しない(枠線・名称・アイコンのみ)。地区の識別色はバックエンドの `zone.color`(HEX文字列)を `GetZoneColor(zoneId)` で使う(パース結果は `zoneColorCache`)。建物/区画/道路の「地区別色塗り」モードもこの色を共有する。地区データが変わるたびに `ZoneRevision` が進む。
- **地区枠線の視錐台カリング**: 枠線は32本ごとのチャンク(`zoneLineChunks`)で視錐台カリングする。チャンクの AABB は固定20mに加えて、カメラからの距離×2°(描き直しの回転しきい値)に比例して広げるので、描き直しの間の小回転で視野に入る線分も必ず描かれる。ラベルは `zoneLabelAnchors`(生成時と高さ再計算時の地形Y+2m)を投影するだけで、カメラ移動のたびに地形を引かない。
- **枠線・ラベルの高さの再計算**: 地形ロードバッチの完了(`OnAllTerrainLodLoaded`)では、変化のあったセル(updatedCells と、タイルが外れたセル)に BBOX かラベルアンカーが掛かる地区だけを `zoneHeightDirtyIds` に積む。表示中なら `RefreshZoneLineHeightsWorker`(RenderWorkScheduler、256サンプルごとに Continue)が予算内で処理し、実行中なら再起動せず集合が空になるまで続ける。非表示中は積むだけで、`ApplyVisualMode` で表示ONになったときに始める。完了時に `lastCamPos` をリセットして描き直させる。
- **地区枠線は内側へセットバックして描く(`BuildSetbackOutline`、`OUTLINE_SETBACK`=3m)**: 隣り合う地区は境界の街路を共有するためポリゴンの辺がぴったり重なり、そのまま描くと2本の枠線が同じ位置に乗ってどちらの地区色か判別できない。各頂点で前後の辺の内向き法線の平均方向へマイター長(`1/cos(半角)`、`OUTLINE_MITER_LIMIT`で打ち切り)を掛けてずらすことで、境界では2本の線が平行に並んで両方の地区色が読める。
- **地区編集モード中は枠線の見せ方を切り替える(`SetEditOutlineMode` / `SetSelectedZone`)**: ONの間は枠線マテリアルの `_ZTest` を `Always(8)` にして地形・建物に隠れないようにし、線も太くする(通常`LINE_THICKNESS`=5 → 編集中`EDIT_LINE_THICKNESS`=8、選択中の地区は`SELECTED_LINE_THICKNESS`=13)。**通常プレイ中は既定の `LEqual` に戻す**(全マップの枠線が山や建物を透けて見えると煩雑なため)。切替は `ZoneEditContext` の `EnterCreateMode`/`EnterEditMode`/`Exit`/`SelectZone` から呼ばれ、太さが変わるフレームは `lastCamPos` をリセットしてオーバーレイを必ず描き直す。
  - **地区のリロード経路は3つ**(全経路は `StartZoneLoad` に一本化。進行中フラグは起動時に立て、`LoadAllZones` の finally で下ろす。`LoadAllZones` は版が変わった地区を1地区ずつ Continue をはさんで適用する。`ClearAllCells` は進行中のロードと高さ再計算を止め、`RemoveZone` は `zoneVersionCache` も消す): ①`LoadCells`(カメラセル更新)は「未ロード時と isForce 時のみ」全件取得する省略設計。②エポック完了・編集確定の `CellDataLoader.ReloadAllData` は isForce=false のため①では再取得されず、代わりに `RequestZoneRefresh()`(version差分の全件リロード。進行中なら完了後に1回だけ追加実行)が明示的に呼ばれる。**地区はエポックごとに自動作成・統合・分離されるため、この呼び出しを外すと Unity の地区表示が起動時スナップショットのまま永久に更新されない**(枠線・ビルボードが表示されないバグの既往原因)。③地区編集確定は `ReloadAllZonesForced()`(version差分バイパス。ロード中なら完了後の全再描画付き再取得として予約する=`zoneForcedReloadQueued`)。
  - **地区編集モードのホバーフィル (`SetHoverFillZone` / `ClearHoverFill`)**: 地区編集中にマウスが既存地区の領域内に入ったとき、その地区ポリゴンを半透明フィルでハイライトする。**枠線用の `overlayRenderer` とは別インスタンスの `hoverFillRenderer`** を使い、マテリアルの `_ZTest=Always` で地形・建物に埋もれず常に手前に塗る(ParcelManager の枠線・ホバーハイライトと同じ採用済みトレードオフ)。ポリゴンは `BuildingMeshBuilder.TriangulateOrFan`(耳切り。凹地区に対応)で三角形化し各頂点Yを地形高さに追従。**ホバー地区が変わったフレームだけ再生成**し(`OverlayMeshRenderer` は次の `BeginFrame` まで前フレームのメッシュを保持するため毎フレームコストは無い)、呼び出しは `InputHandler` のホバー判定(地区編集モード限定)。`ZoneEditContext.Exit` / `ClearAllCells` でクリアする。
- **地区内外判定の高速化**: `GetZoneIdAtPoint` は `PolygonUtils.Contains`(多角形内外判定の共通実装)の前に `zoneBboxCache` (BBOXキャッシュ) による早期リターンフィルタリングを行い、内外判定には変換済みのポリゴン(`zonePolygonCache`)を使う。また、InputHandler はこの判定を地区編集モード中のみに限定し、通常時の毎フレームオーバーヘッドを排除している。
- ResourceManager は天然資源（油田、鉱脈、レアメタル、森林、水源の5種）の分布を、地形に追従するヒートマップ風オーバーレイとして描画する。ParcelManager/ZoneManager が使う `OverlayMeshRenderer` とは別に、**UVと頂点カラーを持つ専用の MeshFilter/MeshRenderer** と専用シェーダー `Custom/ResourceHeatmap` を使う（プロシージャルノイズで雲/ヒートマップ的な見た目）。
  - **ポリゴンのグリッド細分化**: 各資源ポリゴンのAABB内を `GRID_STEP`(10m)間隔で細分化し、各頂点で地形高さをサンプリングする。ポリゴン頂点だけで貼る板と異なり、丘や山の上でも地形に張り付いて埋もれない。
  - **輪郭はソフトフェード**: `SignedDistanceToPolygon` で各グリッド頂点のポリゴン境界からの距離を求め、`EDGE_FADE`(12m)の範囲で頂点アルファを0へ漸減させる。輪郭線は引かない。
  - **再構築タイミング**: カメラ移動では再構築しない（world-spaceメッシュなので不要）。リソースロード完了時と、`TerrainManager.OnTerrainTileReady`（資源が存在するセルのみ）で再構築。同一フレームの複数イベントは `LateUpdate` で1回にまとめる。
  - **資源名ラベル**: ZoneManager と同じ ScreenSpaceOverlay Canvas 方式で `resource_type_label`(水源・森林等)を表示。`Utils.CreateOverlayCanvas` で専用Canvas(sortingOrder=55, ZoneManager=60より奥)を作り、`resource_type_label` を `Text`+`Outline` で描画。位置は `Update` でカメラが 1m/2° 以上動いたフレームだけ `center_point` を `WorldToScreenPoint` して更新(ZoneManagerと同じ間引き基準)。

## 地形プロップ (PropManager / PropGeometry / PropRenderModel / ForestRenderer)

- `PropRenderPolicy`は種類ごとの描画方針。草（4）は実距離100mでHidden、90m未満でCloseへ復帰し、投影サイズで遠景モデルへ切り替えない。影を送信しない。`RefreshDetail`が水平125m以内の小区画だけ散布候補を生成し、離れると解放する。通常候補`basePoints`と結合した`points`を既存の除外・地形追従・選択・描画に使う。近景散布には別枠65,536候補を固定配分する。草だけの遠方小区画も範囲定義は保持し、セル切替を待たずカメラ移動で復元する。GPU森林・全景森林被覆には草を含めない。

- 種類1/2の範囲配置は既定でForestRendererを使う。GPUが固定格子・seedによる散布、穴付き領域・障害物の判定、Terrain.heightmapTextureからの接地を行う。CPU個体配列・行列は保持しない。通常モデルは約100m以内、90〜110mで木ごとに切替距離を分散し、以遠はFar樹冠、1500mで非表示。カメラ移動では再散布しない。
- Forest.shaderはURP LitのUniversalFragmentPBRを使う。本編PC_RendererはForward+なので_CLUSTER_LIGHT_LOOPのバリアントが必須。これを省くと主光源の減衰係数を通常Forward用のunity_LightData.zから読み、太陽光による陰影が消え得る。材質の基色・画像・Smoothness・Metallicを引き継ぐ。Tests/PropsはForward+で、同じシーンからクラスタ対応だけを抜いた検証用シェーダーとの輝度差も検査する。
- ForestPlacement.computeのハッシュとポリゴン判定はPropGeometryと同じ規則。GPU候補は2,000,000個まで（32バイト/個）で、小区画×散布へ配分する。超過時のみ格子を均等抽出する。個別配置・他の種別・GPU非対応時はCPU候補経路を使う。近景もGPU配置なのでモデル切替で座標がずれない。
- 森林の地形参照は所属Terrainタイルそのもの。1536m移動窓を使わない。タイル完成・地形編集でdirtyを立て、未完成タイルでは待つ。バッファは小区画に所有させ、置換・アンロード・ClearAllCellsで解放する。TryPickはレイが通る小区画だけCPUで再現し、GPU読み戻しは行わない。ForestCandidateCountとSubmittedForestBatchesをF8で観測する。GPU 森林の `Generate` は1フレームあたり262,144点(`ForestDispatchBudget`)までに抑え、超えた分は次のフレームへ持ち越す。署名には予算ではなく `ForestRenderer.PatchCount(Rect, float, int)`(`Create` と同じ式の公開API)による実効本数だけを入れる。生成済み森林の詳細マスクは、森林パッチの版(`forestStateVersion`。追加・削除・生成・非表示・全消去で進む)が変わったフレームだけ作り直し、寸法が同じなら同じテクスチャへ書き直す(読み取り可能のまま。最大1MB)。全景被覆は `Raster.hash`(FNV-1a)が前回の公開と同じなら `Publish` を省く。

以下は主に個別配置・森林以外・GPU非対応時のCPU候補経路の説明。

- `GET /props?bbox=…&include_obstacles=true`を`ApiClient.GetBulk`で取得する。完全スナップショットの配置情報と障害物を同時に更新し、建物のGameObject/Colliderへ依存しない。ただし散布プレビューの需要が無い間は、先に `/props?bbox=…&limit=1` でプローブし、scatters と instances が0件なら障害物を取得せず、全小区画を外して `obstacleReadyCells` を空にする(`CanPreview` は false)。需要は `PreviewRects` の呼び出しと、`PropPlacementManager` が散布モードの間に毎フレーム読む `PreviewRevision` の読み取りで2秒ずつ延長される(生存確認)。需要が来ると `EnsurePreviewObstacles` が常駐セル全体を障害物付きで取り直す。プロップがある範囲では従来どおり完全スナップショットを使う。
- `LoadLodCells`は共通の常駐セル集合を使用する。個体の投影サイズでDrawLodを決め、セル由来のFar固定はしない。常駐範囲内でもカメラから一定距離以上はHidden(後述)。常駐範囲外は非表示。`CellDataLoader`から共通`LoadBatch`を渡し、旧処理の適用を防ぐ。
- セルサイズは`TerrainManager.CellSize`が正。`subdivisions`（既定8）で各セルを整数分割し、候補点・除外判定・GPUバッチを管理する。地形セルや独立したロード距離は追加しない。
- `PropGeometry`がregionの穴・複数パートと固定格子の座標ハッシュを扱う。候補点はregionのbboxや生成順、描画分割数に依存しない。プレビューもこの実装を使う。
- `PropRenderModel`は種別ID(int)のほか固定名(string。`ambient_*`・`pedestrian_*`など)でも `Models/Props/{名前}` を読める。最遠景樹冠の生成は森林2種だけ。歩行者(`Vehicles/PedestrianManager`)は `normal` パーツを `RenderMeshInstanced` で描く。
- `PropRenderModel`は共有FBXを変更せず、instancing対応Materialの複製と任意の`_close`/`_far`を用いる。Far未提供時は複雑なサブメッシュを頂点の空間集約で簡略化し、既に軽い部品はそのまま使う。所有するMesh/Materialを全消去時に破棄する。実行時生成MaterialのためGraphicsSettingsのInstancing VariantsはKeep Allを維持する。
- `PropManager`は一本ごとのGameObjectを作らず、候補配列と小区画別バッチを保持する。描画は`Graphics.RenderMeshInstanced`（最大511個/呼び出し）。全マップを一括バッチにせずBoundsでカリングする。
- プロップID/versionの署名と障害物key/signatureの署名を分ける。障害物だけの変更では候補を再生成せず対象区画の表示可否だけ更新する。全土地区画が建物・床材の有無を問わず障害物になる（parcel:{id}、floor_variant=0）。非表示候補は復元用に残す。
- `chunksByCell`でアンロード・地形追従の対象を引く。Near/Dynamicの高さは既存`TerrainFollowUpCoroutine`で更新し、初回Far地形ロードは`OnTerrainTileReady`でも高さ更新を予約する。地形高さの共通取得処理を使用する。`OnTerrainTileReady` では `GetAttachedTerrainVersion` と `TerrainEpoch` から作る stamp を `Chunk.terrainStamp` と比べ、同じ版の再到着(LRU の復元)では高さの再サンプルも森林の再生成もしない。版が不明(-1)なら常に更新する。プロップの無いセルでも、障害物を取得済みなら `PreviewRevision` を上げる。
- 生成・行列更新は`PerformanceConfig.FrameBudgetMs`で分散する。既定の常駐候補予算250,000を有効小区画へ均等配分し、固定順位で候補を抽出する（個別配置優先）。更新中の旧/新配列の併存分は一時的に増える。予算(quota / forestQuota)は `sourceSignature` に含めない。予算が減ったら points の先頭を切り詰め(`StableSample` は順位の昇順なので、結果は再生成と同じ)、1.25倍未満の増加では本数を据え置き、それ以上で再生成する。`Chunk.offered` は抽出に投入した総数。
- `RebuildBatches` は点ごとの LOD/distant/Hidden を評価し、変化が無く点(dirty)と原点(offset)も変わっていなければ、既存バッチを使い回して影の距離判定(shadowEligible && 150m)だけを更新する。変化があれば `chunk.dirty` を即座に立ててから再構築する。バッチの作業領域は DrawWorker 専用で共有している。カメラ移動による再評価のしきい値は max(`REEVALUATE_CAMERA_MOVE_DISTANCE`=4m, 小区画までの距離×2%)(1,000m先では20m)。`DrawWorker` は、前回完了した周回とカメラ位置・投影・正射影・原点が同じで `HasPendingDrawWork` も偽なら周回を省く。周回中に generation が進んだら、その周回は完了扱いにしない。`RefreshDetail` は近景の常駐判定(`DetailWanted`)が変わる小区画だけで呼ぶ。
- `ResidentCandidateCount`/`ResidentChunkCount`、`SubmittedBatchCount`/`SubmittedInstanceCount`（サブメッシュごとの延べ数）、`LastRegeneratedChunks`/`LastExclusionUpdatedChunks`で負荷と差分を観測できる。
- 候補は`sourceId`(explicitInstance ? prop_instance_id : prop_scatter_id)を持ち、`TryPick(ray, maxDistance)`が表示中候補のワールドAABB(モデルbounds×TRS)とレイの交差で最近傍1本を返す(ブルドーザ用。小区画は描画バッチのBoundsで粗く絞る)。`TryGetScatterRegions`は常駐セルが参照する散布範囲、`HideSource(scatter,id)`はDELETE成功直後の暫定非表示(正はリロード後のスナップショット)。
- 正式仕様は`city-genesis/docs/docs/geometries/M_地形プロップ.md`。独立Unity検証は`Tests/Props/run.ps1`。橋下の高さ判定・UltraFar森林は対象外。削除UIは`InputHandlers/RemovalManager`(ブルドーザ)。

## 高さ取得APIの使い分け

`Utils.GetGroundHeight` がロード済みTerrainをサンプルし、未ロード位置は共通標高から作った全域overview(`TerrainManager.TrySampleOverview`)へフォールバックする。都市内の未常駐セルでは `Terrain.activeTerrains` を走査しない(走査するのは都市外とマネージャー不在のときだけ)。同一XZ・同一フレームだけを共有し、近傍座標へ丸めない。差し替え時は `Utils.InvalidateHeightCache()` が必要。`HeightmapTextureManager.GetTerrainHeight` もこのAPIへ委譲する。

`HeightmapTextureManager` はシェーダー用の移動窓GPUテクスチャを管理する。削除済みの `RebuildCpuHeightmap` / `WriteCellHeightRows` を復活させない。地形密着メッシュの接地判定は実地形を使う。

## アラート (AlertManager.cs)

- 役割: バックエンドの `GET /api/v1/alerts` から、alert ビットフラグが立っている建物(agent)・道路(path)の一覧を取得し、各 `center_point` 上にビット種別ごとのアラートアイコンを表示する。**alert は version 管理外のプロパティ**(VERSION_TRIGGER_PROPS に含めない)なので、BuildingManager/PathManager のような version 差分スキップは行わない。応答は毎回全件走査し、表示ビット構成(フィルタ適用後のビット和)と廃墟表示が同じ対象はアイコン GO を使い回して座標だけ更新し、応答に無い対象は破棄する。取得は `GetBulk`(JSON をバックグラウンドで解析、タイムアウト10秒)。
- **表示方式は ZoneManager / ResourceManager と同じ ScreenSpaceOverlay Canvas + Image**: 距離に依存せず常に一定のスクリーンサイズで表示する。スクリーン座標(`Camera.WorldToScreenPoint`)の再計算は、カメラ位置・回転・FOV・画面サイズの変化時(しきい値 0.005m / 0.01°)か、応答の適用直後だけ行う。カメラ背後・画面外(余白 margin=(size+spacing×個数)×max(1,scale)+ボビング振幅)のアイコンは SetActive(false) にして位置更新の対象から外す。建物/道路の子にはしないため VisualModeObject のステータス連動や LOD アンロードの影響を受けない。
- **1オブジェクトに複数ビットが立っている場合はアイコンを水平に並べる**: 並べ方 `ZoneManager.cs:763` と同じ `(i - (count-1)/2f) * alertIconHorizontalSpacing` 式で中央揃えファンアウト。
- **アイコンSpriteの選択は AlertIconFactory が担当**: `AlertIconFactory.Instance.GetIconForAgent(bit)` / `GetIconForPath(bit)` を呼び出す。AgentとPathで同じビット値(例: CONSTRUCTION_SHORTAGE=1)でも別のアイコンを割り当てられるよう、ファクトリ内部は `Dictionary<(string entityType, int bit), Sprite>` で保持する。未設定ビットのSpriteはnullになりそのビットのアイコンはスキップされる。AlertIconFactory は BuildingIconFactory / AgentIconFactory / PathIconFactory と同じく `Utils/AlertIconFactory.cs` に配置し、Inspector から名前付き `[SerializeField] Sprite` を割り当てるパターンに従う。
- **表示フィルタのマスクは agent/path で名前空間を分ける**: UI(表示パネルのアラートタブ)から `view:change_view` の `sub_mode` にビットマスクが届き、`ApplyAlertMode` → `SetFilterMask` に渡る。`ALERT_AGENT_*` と `ALERT_PATH_*` は同じビット値(1/2/4…)を使うため、マスクでは **下位バイト=agent、上位バイト=path(`Constants.ALERT_PATH_MASK_SHIFT`=8シフト)** に分離する。`ActiveBitMask`(有効ビットの和を int で返す)は path のとき比較前にマスクを右シフトして下ろす。既定値は `AlertManager.DefaultFilterMask`(全種別ON)で、`ApplyAlertMode` の未指定時もこれを使う(定数の直書き禁止)。**アイコン対象ビットの一覧は `consts.py` の `ALERT_AGENT_ICON_BITS` / `ALERT_PATH_ICON_BITS`(生成物 `Constants.cs`)が正**で、`AGENT_ALERT_BITS` / `PATH_ALERT_BITS` はその参照にすぎない。AlertManager 側で配列を再宣言しないこと(consts.py に足して `sync_consts.py` を回せばアイコン列挙に反映され、バックエンドの `/alerts` 配信対象とも自動で一致する)。UI側の `ALERT_CARDS` と `AlertIconFactory` の Sprite は別途足す必要がある。アラート全体の表示ON/OFF(`IsVisible` / `ToggleVisibility`)にはIPCの入口がなく、UIからの全消しは全種別OFF(マスク0)で行う。
- **火災系2ビットはアイコンを作らず `FireEffectManager` に委譲する**: `ALERT_AGENT_ON_FIRE` / `ALERT_AGENT_ACCIDENTAL_FIRE` は `consts.py` 側で `ALERT_AGENT_ICON_BITS` ではなく `ALERT_AGENT_VFX_BITS` に置く(AlertManager の `AGENT_VFX_BITS` はその参照)。この配列は `DefaultFilterMask` には含めるがアイコン生成には使わないため、表示パネルのトグルはそのまま炎・煙のON/OFFとして効く。`ApplyAlerts` は**アイコン用Canvas/AlertIconFactory の準備前に**火災側へ応答を渡すこと(火災エフェクトはどちらにも依存しないため、シーンにIconFactoryが無くても動く)。`ClearAll` は火災エフェクトも破棄する全消去、`ClearIcons` はアイコンのみ。
- **廃墟(倒壊)アイコンだけは alert ビットではなく status 由来**: `STATUS_COLLAPSED` の対象は `alert==0` でもバックエンドの `/alerts` が返す(`services/utility/alert.py`)。廃墟は撤去するまで消えず出っぱなしになるため、表示フィルタ専用ビット `Constants.ALERT_DISPLAY_RUIN`(65536。エンティティの `alert` には決して立たない)でON/OFFする。agent/path 共通の1ビット。
- **フィルタで表示対象が0になった対象はアイコンを破棄する**: `UpdateIcons` は「廃墟表示OFF かつ有効ビット0」なら `DestroyIcons(key)` して抜ける。`/alerts` の応答に含まれるため、ApplyAlerts 末尾の削除スイープでは消えない。アイコンとスクリーン配置のキャッシュは `_entries`(`AlertIcon`: icons/bits/ruin/world/screen/scale/phase/shown)に1件にまとまっているので、`DestroyIcons` はエントリを1回削除すればキャッシュも揃う。
- アイコンサイズは `alertIconSize`(既定100px、シーンでは65px)で Inspector 調整可能。Canvas の `sortingOrder` は `canvasSortingOrder`(既定70)で ZoneManager(60)・旧ConstructionWarning(65)より前面、Tooltip(100)より奥。
- **リフレッシュタイミング**: `CellDataLoader.ReloadAllData()`(エポック完了/編集確定後)と `ForceReloadAllDataCoroutine()` 完了後(全量再取得)に `Refresh()`、表示フィルタ変更時に `Refresh(forceRefresh: true)`。カメラセル変更(ティア変更セルがあるバッチの発行時)と建物バッチの適用時(`BuildingManager.ApplyLodCellChanges` の末尾。発行時点では新しい常駐セルの建物GOが未生成でアイコンが地面高さになるため)は `RefreshPositions()` で、HTTP は取らず最後の `/alerts` 応答(`_lastResponse`)をクールダウン単位で再適用する(アイコン高さの再解決と `FireEffectManager.ApplyAlerts`)。応答が未取得なら代わりに取得を要求する。`Refresh` / `RefreshPositions` はクールダウン中・取得中・無効中の要求を捨てずに保留し、`Update` で1回にまとめて実行する(force はクールダウンを無視)。`ClearAll` は世代番号を進め、取得中の応答(`GetBulk` の `isCanceled` で Abort)と保留中の要求を捨てる。
- Image の `raycastTarget=false` により、uGUIの Raycast に干渉しない。3D オブジェクト用の InputHandler には Collider/Tag を持たないのでそちらにも影響しない。
- 詳細・設計理由は `genesis-docs/docs/programs/アラートフラグ管理.md` 参照。

## 火災エフェクト (FireEffectManager.cs)

- 役割: 火災系アラートビットを3Dパーティクルで描く。`ON_FIRE`=炎+火の粉+黒煙+明滅する点光源、`ACCIDENTAL_FIRE`=薄い白煙のみ。両方立っていれば本格火災を優先するが、**`STATUS_COLLAPSED` の建物は瓦礫のくすぶり(白煙)に落とす**。
- **HTTP取得は持たない**: `AlertManager.ApplyAlerts` が `GET /api/v1/alerts` の応答をそのまま `ApplyAlerts(agents, filterMask)` で渡す(同じ情報のためにリクエストを2本投げない)。表示トグル `IsVisible` も `AlertManager` から伝播する。
- **エフェクトは建物GameObjectの子にしない**: 建物はLODの個数上限(`maxDetailedBuildingObjects`)で破棄され得るため、子にすると炎まで消える。ワールド座標の専用ルート下に置く。
- **既に燃えている建物のエフェクトは作り直さない**: `ApplyAlerts` はカメラのセル跨ぎ(`AlertManager.RefreshPositions` による HTTP を伴わない再適用)でも呼ばれる。作り直すと煙柱が根元から生え直して目立つため、再生成は種別変更時と寸法が `REBUILD_SIZE_RATIO` 倍以上変わったときだけ。
- 規模の解決は「底面ポリゴンの外接矩形(中心から遠い側の辺)」と「描画中メッシュのAABB上端 → 階数×階高 → 既定値」。建物未ロードでも既定値で成立する。
- `TransitOverlayManager` 等と同じく `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` で自己生成するため、シーンへの事前配置は不要。
- 詳細は `genesis-docs/docs/geometries/S_火災エフェクトの描画.md` 参照。

## 公共交通ハイライト (TransitOverlayManager.cs)

- `TransitDataStore.Lines` の `journeys.steps` を `TransitRouteGeometry` で補間し、車両と同じ方向・停止中心・レール接続に沿う帯を描く。`route_path_ids` の端点推測や停留所間の直線フォールバックは使わない。未ロード・未生成接続部分は切る。環状運転の継ぎ目用に `BuildRuns` が足す走行区間の写し(`Run.Wrapped`)は描かない。
- `HiddenLineId` に路線IDを入れるとその路線の帯と停留所名を出さない。経路編集中(`Editors/TransitEditor/TransitPlanManager` が設定・解除)に、編集中の新しい経路のプレビューと古い経路の帯が重ならないようにするため。
- `OverlayMeshRenderer` を輪郭12px・路線色8pxの2枚で描き、`_ZTest=Always`・描画順3200/3201で建物・地形より前面に表示する。地下区間も見える。
- 公共交通タブの `mode=hidden` は全体OFF、`default` はON。`sub_mode` は種別IDのCSVで、OFF中もフィルターを保持する。OFF時は帯・停留所マーカー・吹き出し・選択候補を即座に消す。車両は止めない。
- 名前は表示対象路線の詳細APIを10秒間隔・逐次取得する(`GetBulk`。セッションの不一致で取消し)。新しい路線の詳細は応答ごとに再描画し、stops の内容が変わった再取得は取得ループの最後に1回だけまとめて再構築する。停留所の下地パスへXZ射影して高さを合わせ、同じ駅舎のラベルを集約する。`WorldCallout` が引出線と配置を担当し、近景を優先して最大80ラベルを表示する。無名はノードIDで表示する。
- 経路形状は変化駆動で作り直す: 0.5秒ごとに路線一覧の版・表示種別・監視パス(経路+停留所の下地)の署名(version・データ参照・`PathManager.GetPathGeometryRevision`)を確認し、変化したときだけ再構築する。変化が無くても5秒ごとに安全再構築する。地形タイルの到着で作り直すのは、形状の版が -1 のパスがあるときだけ。描画は32本単位の外接箱で視錐台カリングし、余白は `OverlayMeshRenderer` と同じピクセル換算の12px。ラベルの距離ソートは `Draw`(カメラが変わったとき・再構築時)で行う。カメラの変化では帯の画面幅だけを更新する。セッション変更では取得中のコルーチンと名前キャッシュを破棄する。選択は `InputHandlers/TransitSelection` が処理する。

## インターチェンジ (InterchangeManager.cs)

- IC/JCTはバックエンドがランプ(`PathMasterItem.ramp`=true のバリアント)の連結成分から自動検出する。**道路そのものは普通のパスとして PathManager が描く**ので、このマネージャーが持つのは中心 `center` に置く名前ラベルだけ。プレイヤーがICを作る操作は無い。
- `GET /interchanges/`(手書きDTO `API/InterchangeSummary.cs`)で都市全体を一度に取る。応答の `version` が前回反映値と同じなら何もしない(レコード集合・名前・構成が変わると上がる)。
- **取得の入口は2つ**: ①`CellDataLoader.LoadObjectManagers` の `EnsureLoaded`(未要求のときだけ。初回ロード・全量リロード)。②`CellDataLoader.ReloadAllData` の `RequestRefresh`(session:reload・エポック完了・編集確定)。**カメラ移動では取り直さない**。バックエンドの再検出はAPI呼び出し時に遅延実行されるため、②を外すと竣工したランプのICが永久に出ない。取得中の要求は完了後に1回だけ追加実行する。
- `CellDataLoader.ClearAllManagerData`(セッション切り替え・全量リロード・ゲーム終了)で `Clear` する。取得中の通信は止めず世代番号を進め、古い都市の応答を捨てる。
- 表示は ZoneManager の地区名と同じ ScreenSpaceOverlay Canvas(sortingOrder=62。地区60より前、アラート70より奥)+ `Text`/`Outline`/`Button`。クリックで `EmitSelectObject("intersection_detail", "interchange", id)`。名前が空なら `IC {id}` / `JCT {id}`(Unityに多言語リソースが無いので略号)。ボタンのRectは文字列幅に詰める(下の道路・建物のクリックを奪わないため)。
- **通常(閲覧)状態のときだけ表示する**: 道路・地形の入力モードがともに `MODE_NORMAL`、`IsWorldVisible`、ドライブUI非表示、道路の表示モードが `MODE_HIDDEN` でないこと。編集ステートは道路か地形を NORMAL 以外にするので、この判定でラベルが消える(uGUIのラベルを編集中に出すと道路編集のクリックを奪う)。
- 位置はカメラが1m/2°以上動いたフレームと、一覧更新・地形ロード完了・表示再開の直後だけ、`center` の地面高さ+6mを `WorldToScreenPoint` して更新する。カメラ背面と水平距離6,000mより遠いICは隠し、遠いICは地形をサンプルしない。地面の高さは IC ごとに `labelGroundY` へキャッシュ(`Utils.GetGroundHeight`)し、地形ロード完了(`OnAllTerrainLodLoaded`)・一覧の version 更新・`Clear` で捨てる。

## 道路名ラベル (RoadManager.cs)

- 道路(名前付き道路)は区間(パス)をまとめる名札で、所属の正は区間側の `PathSummary.road_id`(未所属は -1)。**区間そのものは普通のパスとして PathManager が描く**ので、このマネージャーが持つのは一覧と道路名のラベルだけ。3Dの強調帯は `VisualHandlers/RoadFocusOverlay`、区間編集モードは `Editors/RoadEditor/RoadMemberEditManager`。
- `GET /roads/`(**末尾スラッシュ必須**。手書きDTO `API/RoadSummary.cs`)で都市全体を一度に取る。応答の `version`(2^48未満の整数なので long)が前回反映値と同じなら何もしない。取得の入口は InterchangeManager と同じ `CellDataLoader` の `EnsureLoaded`(初回・全量リロード)と `ReloadAllData` の `RequestRefresh`、加えてUIの `world:refresh_roads`(道路の作成・改名・所属変更・削除の直後)。ブルドーザで道路の区間を消したときは `RemovalManager` が `world:roads_changed` をUIへ送り、一覧は削除後の `ReloadAllData` で取り直す。カメラ移動では取り直さない。`ClearAllManagerData` で `Clear`(世代番号で古い都市の応答を捨てる)。
- ラベルは `label_points` ごと(塊ごとの中点+長い塊は約1500mおき)の ScreenSpaceOverlay Canvas(sortingOrder=61。地区60とIC 62の間)+ `Text`/`Outline`/`Button`。クリックで `EmitSelectObject("road_detail", "road", id)`。**名前が空の道路はラベルを出さない**(UIは翻訳済みの「道路 #id」を補うが、Unityは多言語リソースを持たない)。ボタンのRectは文字列幅に詰める(回転込みで当たり判定されるので、下の道路・建物のクリックを奪う範囲を最小にする)。
- 文字は `angle_deg`(ワールドXZの `atan2(dz, dx)`)を画面へ投影した向きに回転し、逆さにならないよう (-90°, 90°] に収める。配置はカメラが1m/1°以上動いたフレームだけ行い、画面外・カメラ背面・4,000mより遠いものを除いてから**近い順に置き、既に置いたラベルの外接矩形(回転込み+余白)と重なるものは隠す**(最大60件)。表示条件は IC ラベルと同じ(世界表示中・通常モード・道路レイヤーが非表示でない)。
- ラベルの高さは近く(30m以内)のロード済みの所属区間の中心線(`TryGetPathPolyline(trim:false)`)から拾う(高架・橋でも路面に沿わせるため)。区間が未ロードなら地表高さで仮置きし、2秒後に取り直す。地形ロード完了・一覧更新で捨てる。
- `GetRoadIdOfPath(pathId)` は一覧から引いた所属(区間編集モードの案内文に使う)。パスの `road_id` はパスの再取得まで古いことがあるので、所属の表示には一覧側を使う。

## その他の注意

- **HTTP取得は `ApiClient` に集約されている(UnityWebRequestを直接書かない)**。マージBBOXの一括取得やIDリストのバッチ取得など大きなレスポンスは `ApiClient.GetBulk<T>`(デシリアライズを`Task.Run`で別スレッド実行・timeout/no-cache標準装備・`UNITY_LOG_TIMING`でnetwork/deserialize分離計測)、単体取得や小さいレスポンスは `ApiClient.Get<T>`(メインスレッドのJSONパース)を使う。例外は `TerrainManager` の地形ハイトマップ取得(float16生バイナリ応答でJSONではない)のみ。**`GetBulk`はAcceptヘッダでMessagePackを要求し、バックエンドの対応エンドポイント(buildings/paths/parcels/zones/nodesのbbox一括取得)ではJSONではなくMessagePackバイナリで受信して`API/MsgPackDecoder.cs`でデコードする**(レスポンスのContent-Typeで自動判別するため、呼び出し側・未対応エンドポイントは何も変わらない)。デコード結果の中間表現(`Dictionary<string,object>`/`List<object>`/long/double)はNewtonsoftの非JToken表現と互換で、`GeneratedModelUtils`の座標変換がそのまま機能する。
- `BaseApiDataManager.LoadCells`はLOD非対応マネージャー(ZoneManager)用の仮想ディスパッチポイント。base実装は空で、LOD対応マネージャー(Building/Path/Parcel/Terrain)は`LoadLodCells`を直接オーバーライドするため通らない。(旧セル単位ロードの抽象メソッド `LoadCellData` は呼び出し元が無くなったため削除済み)
- `ApplyDepthOffsetShader` / `DestroyObjectAfterFrames` / `CalculateAverageWidth` は `Utils`(Scripts直下) の共通メソッドに切り出されている。マネージャー固有のものを新たに書かないこと。**例外**: `Utils.ApplyDepthOffsetShader` は `Renderer.materials` でレンダラーごとに複製する(`onInstantiated` で所有者へ渡さない限り解放されない。現在は呼び出し元が無い)。建物・道路は共有版を使う: 建物(本体・クレーン頂部)は `BuildingManager.GetSharedDepthOffsetMaterials`(上記「建物」)、PathManager の遠景の帯・残り帯・ノード円柱は `GetFarRoadDepthMaterial` / `GetNodeDepthMaterial`。大量に作る描画物で `Renderer.materials` による個別複製をしないこと(解放されずにリークする)。通常パスのルートには MeshRenderer を付けない(コライダー用の MeshFilter だけを持つ)。
- GameObject名にIDを埋め込む: `Building_{agentId}` / `Path_{pathId}` / `Parcel_{parcelId}` / `Node_{nodeId}`。各オブジェクトには `ObjectIdInfo` コンポーネントが付与され、`id` / `status` / `isFarObject` でメタデータを管理する。**名前の文字列パースは行わず、常に `GetComponent<ObjectIdInfo>()` で取得すること。**
- カメラ移動中に `LoadLodCells` が再度呼ばれると進行中のコルーチンは `StopCoroutine` でキャンセルされる。キャンセルで状態が壊れないよう、各マネージャーはフラグ復旧処理を持っている(例: TerrainManager の `settingsLoading` リセット、PathManager の `EndLodPathBatch`)。地形ロード完了後の追従更新コルーチン `CellDataLoader.TerrainFollowUpCoroutine` も `UpdateManagers` 冒頭でキャンセルされる。取りこぼした更新セルは `CellDataLoader.pendingFollowUpCells` に退避され、次回ロード完了時に再処理される。追従コルーチンが建物・道路の内側コルーチンを呼ぶ際は `StartCoroutine` ではなく `IEnumerator` を直接 `yield return` すること(`StartCoroutine` で起動した子は親の `StopCoroutine` では止まらないため)。
- **やむを得ず `StartCoroutine` で子コルーチンを起動する場合は、`LoadBatch`(CellDataLoader が `UpdateManagers` ごとに発行し `LoadLodCells` の4引数オーバーロードで配布、`BaseApiDataManager.currentLoadBatch` に保持)の `IsCanceled` を yield をまたぐたびに確認して協調的に停止すること。** 旧バッチの子が新バッチの状態と競合するのを防ぐための必須パターン。例外は地形タイルのジョブで、バッチの取消しでは止めず、セルの常駐で寿命を決める(上記「地形」)。`ApiClient.GetBulk` の `isCanceled` 引数は、真になったら通信を Abort し、デコードもコールバックもログも出さない。呼び出し側は yield の後に自分の取消しを確認すること。
- **ロード進行ログ・計測ログは無条件 `Debug.Log` を使わない**。ロード進行は `UnityLog.If(LocalConstants.UNITY_LOG_LOADER, ...)`、`[TIMING]` 計測は `UnityLog.If(LocalConstants.UNITY_LOG_TIMING, ...)` を使う。セル移動ごとにホットパスで多数のログが出るため、無条件 `Debug.Log` はエディタのスタックトレース取得で無視できないコストになる。文字列補間が重い場合は `Func<object>` オーバーロード(`() => $"..."`)を使う。道路系のデバッグログも `UnityLog.If(LocalConstants.UNITY_LOG_ROAD, () => $"…")` の遅延評価版で書く(ラムダは out・ref 引数を捕捉できないので、それらを補間に使うときは `if (LocalConstants.UNITY_LOG_ROAD) Debug.Log(…)` にする)。`UnityLog.If` は `NoStacktrace` で出力するので、[TIMING] 行にスタックトレースは付かない。`LogWarning` / `LogError` は無条件で残してよい。

### 交差点の断面と切断距離

- `PathManager.motorwayMerges` は描画専用の1車線流入ランプ計画。`NodeMeshResult.MotorwayMerge` を交差点と同時に反映し、交差点削除・全消去で解除する。完成済み道路形状はエポック中に変わり、エポック後の既存ロードで反映する。操作フェーズ用の形状監視は追加しない。`GetMotorwayMerge` を一般車・バスが共用する。拡幅面の `MergeSelection` は下流本線のクリック代理で、既存の `PathInputHandler` と非同期コライダーベイクを使う。建設時の論理占有は不変。

- 未切断の描画点列を交差点へ渡し、描画時はその点列を切断する。保存制御点やアンカーを切断点へ動かさない。single_spanは双方でアンカー間の直線を使う。
- GetIntersectionCutLimitはノード逆引きで反対側の接続状態を調べ、両端交差点なら45%、片側終端なら95%まで予約する。折れ線は最寄りアンカーの手前に制限する。
- 交差点消滅・生成失敗では切断キャッシュも解除して本線を再生成する。旧切断点を残して道路端を欠けさせない。再生成は `OnIntersectionCutChanged` 経由のキュー(LODバッチ中は延期集合)で行う。
- Sidewalkコンテナは歩道だけでなくPartで識別される壁・中央分離帯・ラインも保持する。生成メッシュはIntersectionMeshOwnerが解放する。詳細はMeshGenerators/AGENTS.md。

## 地表メッシュとの高さ整合

- `Utils.GetGroundHeight` は同一XZ座標のみをフレームキャッシュする。Terrainの部分SetHeights、登録、除去、全消去ではキャッシュを無効化する。
- `Terrain.heightmapPixelError` は `LocalConstants.TERRAIN_HEIGHTMAP_PIXEL_ERROR`（1）。ゲーム側のティアと独立した描画簡略化でfloor・道路を突き抜ける誤差を抑える。
- `PathManager.TERRAIN_SAMPLE_INTERVAL` は1m。道路のExtrudeタイル上限と合わせ、縦断方向の微起伏を拾う。

## 道路設備のマスター

PathMasterItemの設備*_allowed/default_*はAPIのpath_masterに対応する。設置状態の正はPathSummaryの8項目で、既定値をロード時に再適用しない。設備変更はPathのversion更新で通常の差分再描画に入る。

## 駅の保存済み標高

`PathSummary.station_grade_profile` があれば `ApplyStationGrade` でXZ射影した絶対標高を適用する。通常のスプライン補正（ROAD_SPLINE_OFFSET−base_height）を加え、交差点切断より前に適用する。折れ線も先に細分化し、TrackPath側で地形再サンプリングや切断断面の上書きをしない。Far表示・交差点ノードも同じ保存基準を参照する。

GetNodeSnapPositionは接続パスに保存標高があればそれを優先し、なければノード相対YをUtilsの地形高さに加える。描画オフセットを含まない吸着用の絶対位置。

- PathManagerはTrackPath.ConnectedPathsProviderにGetPathsConnectedToNodeを提供し、線路の車止め・ホーム端の接続判定もノード逆引きで行う。破棄時にプロバイダを解除する。


TrackPath.InfrastructureConnectionProviderにはIsInfrastructureConnectionを登録し、ノードtypeのインフラビットを判定する。施設の共用接続点へ線路が1本だけ接続されていても車止めを置かない。BuildingManagerのロードを待たず判定し、OnDestroyでプロバイダを解除する。

- 車両の範囲抽出は `GetPathsNearCircle` でセル逆引きから候補を得る。外接矩形の粗い抽出なので、呼び出し側で中心線と円の交差を判定する。`CityInfoStore` の通行方向は駅と車両が共有する。

## オブジェクト単位の表示フィルタ

建物・道路は `ObjectVisibilityFilter`（`VisualHandlers/`）でID単位に非表示にできる。差分ロードで後から生成されるオブジェクトにも効かせるため、**生成直後に `IsHidden` を引くのは生成側（`BuildingManager.CreateBuilding` / `CreateFarBuildingObject` / `PathManager` の各生成点）の責務**で、集合が変わったときの反映は各マネージャーの `RefreshVisibilityFilter()`（キャッシュ済みオブジェクトだけを走査する）。退避キャッシュからの復元(`RestoreBuildingLod` / `PathManager.RestorePathLod`)や遠景箱の再利用も、生成と同じく `ApplyToObject` を引き直す。**遠景LODの建物は `VisualModeObject` を持たない**ため `MeshRenderer.enabled` を直接落とす。`FarBuildingBatcher` は変化を自分では検知しない(毎フレームのポーリングは無い)ので、遠景建物の `Renderer.enabled`・Transform・sharedMaterial・プロパティブロックを変えた側が直後に `FarBuildingMember.RefreshObject(go)` で結合バッチへ通知する。現在の通知点は `ApplyToObject` の直後(生成・復元・再利用・`RefreshVisibilityFilter`)と `BuildingManager.UpdateBuildingHeight` の末尾。有効化・無効化(SetActive / Destroy / Park)は `OnEnable` / `OnDisable` で自動的に伝わる。

## 非同期資源の解放

`MeshBakeService.Bake` は同時2件の物理ベイクを行う。`RenderCacheIdentity` の破棄は同サービスのReleaseへ通し、ベイク中のMeshを先に破棄しない。タイル専用のMaterial・TerrainLayer・代替Textureは `TerrainTileResources` が所有する。建物の遠景Materialは全棟で共有し、Renderer.materialsで複製すると集約できなくなる。詳細建物の DepthOffset マテリアルも元マテリアルごとの共有物で、`RenderCacheIdentity` には所有させない(上記「建物」)。

## 取得と描画完了の区別

建物の遅延生成は、対象セルがロード完了集合へ登録されるまで要求を保持する。未登録セルの要求を選出してから削除してはいけない。一括取得中も生成ワーカーが動くため、背面の建物だけ永久に生成待ちから消える競合になる。次の取得範囲から外れた要求は新しいロード開始時に除く。一括取得の矩形(`SplitFetchRects`)は取得セルより広いことがあるが、取得対象外セルの建物は生成も遅延キューへの投入もしない。遅延キューに残った常駐外セルの要求は、`LoadLodCells` の冒頭(常駐集合)と登録集合を確定する `ApplyLodCellChanges` の末尾(`loadedCells`)で除く(`RemoveDeferredRequestsOutside`)。

道路の `pathVersionCache` は受信版ではなく、生成・復元が完了した版を記録する。API取得や高優先度キューへの投入時には更新しない。同じ版でも描画オブジェクトが存在しない道路は再生成対象とする（意図的な遠景トンネル非表示は除く）。これにより建設計画の更新中にロードが中断されても、次の差分取得で生成できる。道路バッチの終了処理はfinallyで実行し、中断・例外時に地形追従を永久待機させない。

回帰検証は `Tests/Lod/run.ps1`。未ロードセルの建物キュー保持(完了判定を塞がないこと)・適用時の常駐外セル要求の除去と、建設計画中の道路のHTTP取得→生成前中断→同版再取得を検証する。

プロップ障害物のholesは水域内の島を表す。PropGeometry.BlockedWithHolesを永続描画・プレビューで共用し、島内部を表示しつつ岸の境界・離隔内は除外する。

CPU候補経路のプロップは距離で本数を間引かず、極小投影サイズによるHiddenも使わない。木1/2は幹なしの専用Farを使用し、投影比1%未満でPropRenderModelの6三角形の四角錐へ替え、1.2%以上で戻す。DrawLod.Far内の切り替えで、常駐セル・候補予算は維持する。生成した最小メッシュはPropRenderModel.Disposeで解放する。

CPU候補経路のプロップのHiddenは**カメラからの実距離だけ**で決める(`DrawLodConfig.PROP_HIDDEN_ENTER_DISTANCE`=1,500m以上で隠す/`PROP_HIDDEN_EXIT_DISTANCE`=1,350m未満で戻す。`DrawLodEvaluator.PropHiddenByDistance`)。投影LOD(`EvaluateBySize`)より先に判定し、Hiddenの点はバッチに載せない(=`TryPick`でも拾わない)。個別配置も対象。`RebuildBatches`は小区画矩形までのXZ距離(`ChunkDistance`。各点の実距離の下限)が進入距離以上なら個体判定を省いて全点Hidden+`Chunk.hidden=true`にし、`DrawWorker`はその小区画が進入距離の内側へ戻るまでカメラ移動による再構築をスキップする(dirty/heightDirty/原点変更は通常どおり処理)。正射影でも実距離のまま(トップダウンで高く引くほど消える)。F8の「距離非表示」は`DistanceHiddenCount`。

プロップの障害物除外は範囲散布のみ。Candidate.explicitInstanceは常に表示可能とし、区画・道路・水域の追加や更新でも非表示にしない。PropInstanceItem.yは地面からの相対高さ(m)でCandidate.heightOffsetへ保持する。初期生成・スナップショット再利用・タイル完成・地形編集の全経路で「地形標高+heightOffset」を使う。常駐セル・候補予算・LODは通常どおり適用する。


## ロード性能の計測

カメラ移動の調査にはSchedulerの5秒窓の `count/` 集約も使う。再取得セル・受信件数・変更なし件数・LOD走査・生成要求／生成計測件数・現在GO／退避キャッシュ再利用・容量超過破棄・地形追従中断を区別する。追加カウンタは `RenderWorkScheduler.CountEvent` に固定名で渡し、物体IDをキーにしない。無効時は蓄積しない。名前と解釈は `K_Unity差分ロード整理.md` の「カメラ移動時の再取得・再生成件数」を参照する。主なカウンタ: 建物 `building.fetchNewCells` / `fetchExistingCells`(取得したロード済みセル=stale・ティア変化)/ `fetchSkippedCells` / `outsideFetchSkipped` / `received`(全矩形のページ受信件数の合計。重複除外前)/ `lodScanned`(ロード済みセルの建物の延べ数)、道路 `path.queueRebuildSkipped` / `terrainFollow.pathTerrainUnchanged` / `intersection.inputUnchanged`、地形 `terrain.tileDownload` / `tileDownloadAborted` / `tileJobReused` / `tileCacheHit` / `tileCacheEvicted` / `cellVersionsFetched` / `cellVersionsReused`、カメラ `camera.batchDeferred`、通信 `api.bulkCanceled`(`GetBulk` の取消し)、退避キャッシュ `renderCache.localEvicted` / `globalEvicted`。

建物の遅延生成は最新キャッシュと生成直前のカメラでLODを再評価する。Farで足りる要求は詳細保持上限の待機ゲートを通さず処理する。道路本体・区間の距離LODは `LodViewState.UpdatePosition` で移動だけを検出する。建物の投影LODは `UpdateProjection`、取得範囲・視野判定には通常の `Update` を使う。道路区間はLOD変更対象だけソートし、生成前にも現在位置で判定する。区間のマテリアル更新と表示完了待ちは維持する。回帰検証は `Tests/Lod/run.ps1` の移動中生成と遅延建物テスト。

`UNITY_LOG_TIMING` で `LoadBatch.Trace` の工程別経過時間と、RenderWorkSchedulerの5秒単位iterator別集約(cpu/maxStep/steps/queueFrames/underBudgetQueueFrames)を出す。常駐GO `RenderWorkPump` の LateUpdate から報告するため待機中も残件数を観測できる。集約キーは iterator の Type で、出力は FullName(`BuildingManager+<CellUnloadWorker>` など)。`LocalConstants.UNITY_LOG_TIMING` は現在 true(計測に使用中)で、出荷前に false に戻す必要がある。BuildingManager.PendingDetailCountとPathManager.PendingTerrainRebuildCountは遅延生成キューのみで、全表示の完了判定ではない。詳細とログの読み方は `K_Unity差分ロード整理.md` の「ロード遅延の計測」。


区画一覧は要取得セル(`cellsNeedingFetch`)の外接矩形で取得し、重心セルで絞り込んだ区画を1件ごとのContinueで予算内に複数件適用する。件数上限に達した応答は適用せずセル集合を二分して取り直し、単一セルの上限到達では削除同期をしない(上記「区画」)。全件適用・削除差分の確定後にIsLoadingを解除する。区画が確定してから建物を生成する依存関係は維持する。地形の帯適用・SyncHeightmap・完成タイル公開も別ステップとし、未完成タイルを公開しない。

`Tests/Lod/run.ps1` は小さい生成880件のフレーム数・共通予算・複数ワーカーの進行に加え、null/CustomYieldInstructionの待機、ネストしたiteratorの待機中キャンセルを検証する。MoveNext内で自己取消しが起きても、実行中iteratorのDisposeはそのステップが戻ってから行う。

## 公共交通の時間付き描画

路線一覧の取得主体は `Vehicles/TransitDataStore`。`TransitOverlayManager.RefreshLines/Clear` は共有ストアへ委譲し、色帯の表示設定で車両の取得・走行を止めない。車両は `TransitVehicleManager` が管理する。`TryGetPathPolyline` の第3引数 `trimIntersection=false` は未切断の地形追従点列を返し、パス途中の停留所の距離割合の解決に使う。レール接続は実際に生成済みのSplineComputerを参照する。


## 道路の非同期生成と表示の差し替え

長い道路のSplinePartLodは本体GameObjectの作成後に固定区間を生成するため、本体の作成を表示完了とみなさない。PathPresentation.Replaceで旧道路を保持し、新しい道路の各パーツが初回LODを確定した時点で一括表示する。生成途中の新しいRendererはforceRenderingOffで伏せ、旧道路との重複描画を防ぐ。既存のforceRenderingOff値は復元する。

再生成が連続した場合は未完成の候補から表示中の旧道路を引き継ぎ、未完成の候補だけを退避する。完成時に旧道路をキャッシュへ退避し、アンロード・全消去では保持中の旧道路を非表示にして破棄する。キャッシュからの復元と遠景への切替も同じ入口を通す。道路の再利用キーは地形の版を含まず、地形追従は表示中のGOしか見ないので、`RestorePathLod` は退避キャッシュから取り出したGOの `PathBuildStamp` の地形署名を現在の通過セルの署名と比べ、違えば(不明を含む)破棄して作り直す(`path.cachedTerrainStale`)。建物の主メッシュは同期生成なので、この道路パーツ専用の待ち合わせには含めない。回帰検証はTests/Lod/run.ps1のRoadPresentation。

パスの表示同期: 再利用キーにstatusを含め、version一致でも表示中のObjectIdInfo.statusが異なれば再生成する。SetupVisualsはRenderer収集・Stats設定後にSetStatusを呼ぶ。SplinePartLodは生成・復元区間へRefreshMaterialsを適用してからPathPresentationをcommitし、生成途中はRenderWorkScheduler.Continueで共通予算に委譲する（区間単位の固定フレーム待ちはしない）。ワーカーは全パーツ共有のカメラ移動世代(4m。`LodViewState.UpdatePosition`)が変わるまで `WaitUntil`(スケジューラ予算外のネイティブ待機)で眠るので、静止中は予算を消費しない。起動・再開時は世代に関係なく必ず1回評価する。PathPresentation / VisualModeObject は遅延キャッシュする。

PathMasterItem.house_placementはoutside/insideを受信する。駅舎の個別house_endsはPathSummaryに載り、通常のversion差分で再描画する。

工事中パスの進捗は `ConstructionPathSummary` が一覧・単体取得の `construction_progress` を保持する。`PagedPathListResponse` は独自の型付き一覧。エポック取得時はversion/statusに加えて進捗を比較し、再利用キーにも含める。進捗だけの変化でも地形追従の完了記録を無効化する。生成はConstructionPathBuilder、公開G文書§15とTests/ConstructionPaths参照。

`PathMasterItem.smooth_factor` はマスターAPIの標高平滑化距離を受信する。RoadPlanManagerの勾配推定ではバックエンド同様に最小10mへ制限して用い、型から省略して固定10mで代用しない。


## 川の水面の岸への埋め込み

`RiverRecord.CellIncluded`は川の占有範囲（ポリゴンを覆う格子セル）、`SurfaceCellIncluded`は描画専用（占有セルと隣接1セル）。`RiverSurfaceManager.BuildOneRiver`は後者を使い、地形と水面の格子・三角形の違いで曲線の岸から水の板の端が露出しないよう、余白を岸へ埋める。`BuildGrid`は外側2セルの余白を確保する。川床マスク・干渉の占有箱・保存範囲をこの余白で広げない。Tauriの`riverGeometry.ts/buildRiverSurfaceMesh`も同じ規則。生成側の水位と地形補正の詳細は公開D_地形形状.md §D.11。


## バス停の上屋

`BusStopManager`は固定名Props/bus_stopをPropRenderModelでインスタンシング描画する。0.5秒間隔で注視点600m以内のQueryNearbyNodesを使い、接続PATH_TRANSPORTの車種がバスの停留所だけを抽出する。NodeData.station_path_idから下地道路を直接解決し、実中心線の接線・標高で路肩へ配置する。路線の運休・車両数・色帯設定では隠さない。都市切替はTransitDataStore.Sessionでクリアする。NodeDataの下地ID変更もCreateOrUpdateNodeでキャッシュへ反映する。制作規約はM文書§M.7.5、検証はTests/BusStops/run.ps1。

TransitOverlayManagerはRoute.VehicleTypeを設定し、LRTのハイライトをLrtRoadPartsの軌道位置へ合わせる。バスとLRTが同じ道路を使っても走行車線を混同しない。

## LRT停留所のホーム

LRT停留所の相対式ホームは、下地パス(431=TrackPath、222/242/303=RoadPath→LrtRoadParts)のメッシュ生成の一部として `MeshGenerators/Paths/LrtPlatformParts` が描く(Far・工事中・トンネルの埋設区間には出ない)。固定プロップのインスタンシング(バス停)ではない。

- **索引 `LrtStopPlatforms`**(AfterSceneLoadで自己生成): `TransitDataStore.Revision` の変化で、車種が `VEHICLE_TRAM`(`VehicleModelConfig.GetTransitVehicleType(line.path_variant)`)の路線の `stop_points` を下地パスごとに集め、`{停止中心の割合, デッキ長=cars_per_train×length_per_vehicle+2×PLATFORM_END_MARGIN}` の昇順リストにする。`LrtPlatformParts.StopProvider` として生成器へ渡す。`stop_points` は journeys と独立に返るので運休中の路線の停留所にもホームが出る。`station_path_id`・`fraction` が null の停留所は除く。停留所ノードのYや journeys からは位置を取らない。
- **再生成の契機**: 前回の索引と比べて変わったパスだけ `PathManager.RequestLrtPlatformRebuild(ids)` へ渡す。PathManager はそのパスと近接線路(`CollectNearbyTrackPathIds`。結合コリドーの主線路の柱位置が相手のホーム区間で変わるため)の描画キーの revision(`geometryRevisions`)を進めて退避中の古いGOを再利用させず、生成済みGOのあるパスだけを通知時点の通し番号で `pendingLrtPlatformRebuildSerials` に記録し、LODバッチ外の `LateUpdate` で `EnqueueTerrainFollowPath`(理由 Other)へ移す(近接線路の再生成と同じワーカー。通知より後に作り直したGOは省かれる)。未生成のパスは次の生成で索引を読む。都市切替(`TransitDataStore.Session`)では索引を差し替えるだけで再生成は要求しない(パスも全て作り直される)。`ClearAllCells` は保留を捨てる。
- **点列の供給**: `PathManager.Awake` が `LrtPlatformParts.PolylineProvider`(`TryGetPathPolyline` の未切断/切断済み)を設定する。割合→距離は車両と同じ「fraction×未切断3D点列長」、切断位置は未切断点列への切断済み端点の射影(TransitRouteGeometry と同じ定義)。デッキはこの切断済み区間へ収め、デッキ端の外側のスロープは交差点ハル上へはみ出してよい(交差点メッシュ側では何も避けない)。ただし431で他の線路の道床がホームの帯に掛かる側は、その区間のデッキとスロープを描かない(MeshGenerators/AGENTS.md の LRT の節)。
- ホームの範囲・パーツ規約・柱や設備との干渉回避は MeshGenerators/AGENTS.md の LRT の節。検証は `Tests/Lrt/run.ps1`。

PathMasterItemの設備項目は街灯・柵・街路樹・配線方式に加えbench_type/bin_type/planter_type/advert_typeの各*_allowed/default_*を持つ。個別Pathの値はAPI生成型で受け、描画設定はRoadEquipmentGeneratorが解釈する。

## 都市外背景

`TerrainManager.Background.cs` は任意の50m格子をチャンクメッシュで描画し、都市外設備の接地標高も提供する。詳細地形の常駐セルは都市内に限るが、LodConfigの設備取得範囲とカメラの可動範囲・遠方クリップ距離には背景を含める。背景は編集しない。背景は独立したルーチン(`BuildBackground`)で取得・生成し、バッチの取消しでは止めない(止めるのは `ClearAllCells` の `ClearBackground` だけ)。バッチ(`EnsureBackground`)は高さ配列の受信(または204)までだけ待ち、チャンクメッシュは共通予算で並行して作る。取得に失敗したら次のバッチで再試行する。背景のマテリアルは UltraFar と同じシェーダーを新規に作って `ApplyFarTerrainMaterialSettings` で設定し、沈み込みだけ0にする。都市範囲の境界線は `VisualHandlers/CityBoundaryOverlay`(TerrainManager.Awake が同じGameObjectに追加する)が描き、`OnAllTerrainLodLoaded` で境界に接するセルの高さ変化を拾う。仕様は `docs/docs/geometries/D_地形形状.md`。

PropScatterItemのedge_fade_widthは外周・穴から内側への密度フェード幅(m)。PropGeometry.Accept/EdgeWeightとForestPlacement.computeは固定格子のUnit(h+6)で同じ間引きを行う。allow_water=trueはwater:障害物だけを除外対象から外し、道路・区画は除外する。CPU候補、GPU森林、TryPick、ForestOverviewの被覆率すべてに適用する。

## 初期画面の準備完了

`GameStateManager.IsGameStarted()` はデータ取得の許可、`IsWorldVisible` はユーザーへの表示許可として分離する。`LoadingOverlay` は後者まで黒を維持し、`CameraController` と `InputUtil` は表示前の操作を抑制する。ロード中もLOD判定に必要な `Camera.main` を利用できるよう、カメラを有効に保つ。

`CellDataLoader.IsInitialSceneReady` は初回地形処理だけでなく、後続の区画・建物・道路・地区・資源・プロップ、敷地装飾・道路区間・コライダーの描画準備を待つ。0.25秒の間引きで保留中のカメラバッチ(`hasDeferredCameraCell`)も未完了として数える(保留中は `pendingObjectLoad` が空なので、数えないと最終視点のバッチ発行前に `game:loaded` が出る)。`RenderWorkScheduler.Start` の任意 `hasPendingWork` は常駐ワーカーの未処理判定で、省略した有限コルーチンは通信待ちを含めて全期間待機対象になる。常駐ワーカーを追加するときは、無限ループ自身を完了条件にしないようこの判定を指定する。**一定間隔で起動し直す有限コルーチン（駐車車両・ロープウェイ搬器の0.5秒走査、車両・歩行者の道路走査と入口表 `vehicleRoads` / `pedestrianWalks` / `vehicleAccess` / `pedestrianAccess`、車両・公共交通の事前生成 `vehicleWarmup` / `transitWarmup` など）も常駐扱いで `() => false` を渡す。** 省略すると走査のたびに `IsInitialSceneReady` が数フレーム false へ落ち、初期描画の完了判定が明滅する。建物の上限により意図的に保留している視野外の詳細と、元の表示を維持する遠景結合処理は待ち続けない。**常駐ワーカーの未処理判定は「ワーカーが今選出できる仕事」だけを数えること。** 建物の遅延キューは未登録セルの要求を判定から外す(取得中のセルは一括取得のコルーチン自体が待機対象になる)。選出できない要求を数えると `game:loaded` が永久に送られず、Tipsの「ゲームを開始」が出ないまま止まる。

保存カメラの復元と生成完了を連続3フレーム確認して `game:loaded` を送る。初期ロードとは初期視点の常駐セルと全景の描画準備であり、都市全域の詳細モデルを常駐させる意味ではない。開始後のカメラ移動には通常のLODストリーミングを使う。

## 送電線の共有ノード

PathManagerはPowerLinePath.ConnectedPathsへGetPathsConnectedToNodeを渡し、破棄時に解除する。1101/1102は通常・接写・遠景・施工とも専用生成器を使う。登録・接続変更・削除は接続ノードの隣接送電線だけをEnqueuePowerNeighborsで再生成し、キャッシュキーのgeometryRevisionsも更新する。遠景をBuildStripMeshへ戻さない。共有鉄塔の表示所有権はPowerLineSupportが表示中の候補間で引き継ぐ。

## 索道演出との共有

PathManager.CalculatePathPoints(PathSummary,bool)は、未常駐区間を含む索道スナップショットからも既存の座標・地形高さ・single_spanを使う公開入口。RopewayGeometryはfalseを渡し、道路交差点で切断しない。地形詳細化後は点列を再評価する。

PathPresentation.IsReadyは元のパスを補完する演出の表示条件。SetRendererForceOffは生成待ちの非表示を維持したまま、表示再開時に復元する値を更新する。索道の置き換えケーブルがbody抑制を解除するときに使い、生成途中のRendererを先に表示しない。

## 制限速度標識のデータと位置

`SplinePartLod.GetDrawLodAtPosition`は指定位置に表示中の区間LODを返す。射影結果は照会位置が変わるまで保持し、LODは区間の現在値を読む。未生成・退避中はHidden。`IsBody`で道路本体の区間を選び、親がNormalのままでも中心区間がFarなら標識を隠す。標識側で距離LODを再判定しない。

`PathSummary.max_speed`はendpoint_schemas由来の一覧フィールドで、設定された制限速度(km/h)を保持する。通常の一覧/単体取得とSavePathToCacheで更新され、描画versionが変わらなくても標識は0.5秒周期で最新値を読む。`PathSpeedLimitOverlay`はGetPathsNearCircleのセル逆引きで周辺候補を取得し、追加HTTP要求を出さない。全消去はPathManager.ClearAllCellsからClearを呼び、道路削除・アンロードでも標識を消す。

`TryGetPathLabelPosition`はcenter_pointを描画中心線へXZ射影して高さを補間する。既存TryGetPathPolylineで最新データの点列を取得する（Far切替中に残る旧描画点列は使わない）。結果のキャッシュは形状・地形の無効化、SetupWaypoints、削除、全消去で破棄する。曲線・高架・勾配のために標識側で独自の形状生成をしない。標識の描画契約はVisualHandlers/AGENTS.md。

## 路面標示画像

RoadMarkingAtlasは常駐ワーカーをRenderWorkSchedulerへ登録し、hasPendingWork=falseで初期ロードを永久待機させない。SpatialHashによる可視候補から、固定容量Texture2DArrayへ標示を焼く。全ミップ・生成用画像・ページ表を含む128MiBの画像予算と1フレーム2ページ/1M画素の投入上限を持ち、RenderObjectCacheの退避メッシュ予算には頼らない。プール不足時は低解像度、全プール不足時は解析的な基本線だけを描く。登録解除時にページ表を解放し、ClearAllCells/破棄で都市の画像・派生材質をクリアする。

PathManagerはRoadMarkingRulesへ接続道路と実切断端の照合を提供する。交差点の入力署名が一致する場合は標示署名の計算も省略する。接続ID・方向・状態等の標示署名が変われば切断位置が同じでも隣接道路を再生成する。LODだけの更新では内容署名から画像を再利用する。交差点Rendererの基底材質を設定した後でVisualModeObject.InitializeMaterialsを呼ぶ。
