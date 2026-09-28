# Vehicles

描画専用の交通演出。一般車・公共交通の編成・歩行者・路上駐車・索道の搬器・空と海の環境演出を、バックエンドの集計値(交通量・走行時間・路線)から再現する。経済シミュレーション・永続エンティティ・セーブへは書き戻さない。

- 時間と流量の契約の正は `geometries/T_交通シミュレーションと走行描画の整合.md`(平均速度に独立した交差点減速や駅停車を重ねない)。
- docs パスは `city-genesis/docs/docs/` 起点。

## ファイル

| ファイル | 役割 |
| --- | --- |
| `Vehicle.cs` | 車1台の状態と移動。`VehiclePolyline`(累積距離付き点列・円内区間・射影・軌道オフセット)も同居 |
| `VehicleManager.cs` / `.Drive.cs` | 一般車の道路スナップショット・分岐・密度調整・プール・沿道到着。`VehiclePopulationBudget`(全体枠)も同居。Drive はドライブモードの1台 |
| `RoadMotionData.cs` | `/paths/motion` の自由秒数・混雑込み秒数と、前車追従の速度制御 `RoadMotionControl` |
| `RoadsideAccess.cs` | 建物入口の幾何と有効性検査。車と歩行者で共有 |
| `Pedestrian.cs` / `PedestrianManager.cs` | 歩行者(GameObject なしの instanced 描画) |
| `ParkedVehicleManager.cs` / `ParkedVehicleLayout.cs` | 路上駐車(`PathSummary.parked_cars` の静止台数を GPU インスタンシング) |
| `TransitDataStore.cs` | 路線一覧の共有ストア(5秒ポーリング+明示 Refresh)。`Session` が都市切替の世代 |
| `TransitMotionClock.cs` | コアの `journeys.steps` を時刻から参照する純粋な時計 |
| `TransitRouteGeometry.cs` | 方向付き Run・地形追従点列・レール接続・車線オフセット・編成配置 `SampleConsist`。車両とハイライトで共有 |
| `TransitVehicleManager.cs` / `.Drive.cs` | 公共交通の編成描画・選択 `TryPick`・走行音通知。Drive は乗車編成の時計合わせ |
| `TransitLivery.cs` | 路線カラー(`line_color`)の塗装 |
| `VehicleModelConfig.cs` / `VehicleModelResolver.cs` | `vehicle_model_config.yaml` の解析と、モデル解決・接地原点/前方軸の正規化・LODGroup 構成 |
| `DriveMode.cs` | ドライブモードの選択UI・開始位置・走行HUD |
| `RopewayNetworkStore.cs` / `RopewayNetwork.cs` / `RopewayGeometry.cs` / `RopewayCabinManager.cs` | 索道(PATH_CONVEYOR)の駅間抽出・ケーブル形状・搬器の循環描画 |
| `AmbientVehicleManager.cs` / `AmbientTrail.cs` | 空と海の環境演出(航空機3種・船舶5種)と飛行機雲・航跡。船は専用の接写・遠景を投影比で切り替える |
| `AmbientVehicleManager.PortCalls.cs` / `SeaRoute.cs` | 稼働中の港へ向かう入港船(抽選・接岸位置・入港の減速と横寄せ) / その航路(海岸線までの距離の格子上の最短経路。バックグラウンドスレッドで動く純データ) |

## 知らないと事故る不変条件

- ドライブと撮影の開始可否は`InputHandler.IsNormalMode`で編集との排他を判定する。ドライブ開始時の`CameraController.SetView`は撮影とマウス操作を終了し、カメラを同時に更新させない。
- `VehiclePolyline.points` は構築後に書き換えない(`ShapeHash` のキャッシュ・`SameSource` の比較・Road の使い回しの前提)。交差点の点列は車線オフセット済みなので二重に横へずらさない。
  - 四隅検査など局所射影は `ProjectRange`(累積距離範囲の二分探索)、全長射影は `Project`。
- 道路スナップショット(`vehicleRoads` / `pedestrianWalks` ワーカー)は共通予算で分割構築し、完成後に一括交換する。`roads` / `walks` への代入は Commit と Clear に限る(途中の集合を車に見せない)。乗車開始 `StartDrive` だけ同期版 `RefreshRoads` を使う。
- 管理中心は `VehicleManager.ResolveTrafficCenter`(走行車300m・歩行者180m・路駐が使う。公共交通・索道は従来の中心)。大移動は `ClearVehicles` ではなく `Recenter`。`accessCache.Clear()` は全消去のときだけ。
- 入口表(`vehicleAccess` / `pedestrianAccess` ワーカー)は変更時だけ再計算し、逆探索表を全車で共有する。車ごとの探索や毎フレームの建物全件検索は禁止。
  - entrances を差し替えたら必ず `accessGeneration` を進める(`SpawnWeight.HasSpawnPoint` が `spawnPointGeneration` との比較でキャッシュしている)。
- ノード遷移で台数超過を理由にデスポーンしない。到着予約は交差点を跨ぎ、路肩寄せは道路内だけ。
  - 沿道到着は `PATH_STREET` かつ `frontage_allowed` の建物付き接道区画だけ(高速道路・空き地・行き止まりに退出位置を作らない)。入口の条件を変えるときは `RoadsideAccess` を直し、車・歩行者の片方に別実装を書かない。
- `RenderWorkScheduler` のワーカーは `hasPendingWork = () => false` で登録し、ハンドルと実行中フラグは iterator の finally で戻す。初回 Step 前に破棄されたワーカーは finally が走らないので、各 Manager は `OnDisable` でも戻す。
- 車線・区画線は `RoadLaneLayout`(MeshGenerators/Paths/RoadPath.cs)、通行方向は `CityInfoStore`、高速道路の合流は `PathManager.GetMotorwayMerge`、進入矢印との整合は `RoadMarkingRules` を共有する。車両側で輪郭・合流・車線を再実装しない。
  - 交通量は型付き `PathSummary.path_traffic` を読み、`utilization` で代用しない。
- 公共交通: 欠けたパス・未生成のレールコネクタを直線で補完しない(表示しない)。公称車長はワールド座標の弦長で満たし、時計の論理距離に車長を足さない。
  - LRT・鉄道の交差点レールは `PathManager.GetNodeIntersectionObject`(`NodeMesh_*`)の子 `Rail` から読む(`GetNodeObject` のノード円柱とは別物)。軌道の横位置と踏面高さの正は `LrtRoadParts`。
  - 停止中心はコアと同じ「割合×未切断3D点列長」で求め、片方だけ別の式にしない。
- 路線カラーは `MaterialPropertyBlock` を使わない(SRP Batcher から外れる)。塗る材質の唯一の正は `Resources/Config/transit_vehicle_livery.json`。新しい外観は帯を専用材質で著作して表に登録する。
- モデル対応は `Resources/Config/transit_vehicle_models.json`（路線種別→`vehicle_type`・任意の`wagon`）、候補番号は Tauri の `tauri/src/data/transitVehicleModels.json` と揃える。編成内の実体モデル・塗装対象・後尾反転は `VehicleModelConfig.GetTransitCarModel` を描画と事前生成で共有する。貨物は機関車1両＋路線種別の貨車で、総両数と公称車長は路線サマリに従う。折り返しで機関車を先頭へ移さない。
  - モノレール(車種24)の FBX は手動配置プロップ308/309と同一ファイルのコピー。308/309を書き出し直したら24にも置き直す(`Tests/Vehicles` がバイト一致を検査)。
- 歩行者の `side` / `lateral` はパス幾何(始点→終点)に対する左右で、反転しても符号を変えない。`PedestrianManager.ResolveBand` は `Tests/Vehicles/VehicleQA.cs` がリフレクションで同じ引数列を呼ぶので、シグネチャを変えたら QA も直す。
- 索道の body Renderer の抑制・復元は `PathPresentation.SetRendererForceOff` を介し、生成待ちの抑制を解除しない。搬器モデルは吊り点原点なので `VehicleModelResolver` で接地補正しない。
- 走行音は `Weather/VehicleAmbience.Report` へ通知するだけ(車両側に AudioSource を持たない)。都市切替は `TransitDataStore.Session` でクリアする。
- 入港船はエポックが進んだときだけ入れ替える。海岸線の世代(`CoastlineStore.Version`)の変化で消すのは沖合の船と航空機だけで、入港船まで消すと建物の編集のたびに船が入れ替わる。港は `TerrainManager.ShoreDataVersion`(埋立地まで取り終えた版)で読み直す。水際は `PortBuildingMeshBuilder.ResolveWorldWaterlines` を使い、別の規則で求め直さない。
- `SeaRoute` は Unity の Random・地形の高さ・GameObject を使わない(バックグラウンドスレッドで動く)。乱数は要求の `seed` から作る。
- 環境演出の描画LODは、専用の `_close` / `_far` を持つモデル(`PropRenderModel.DedicatedClose` / `DedicatedFar`)だけを切り替える。専用モデルの無い航空機に `PropRenderModel` の自動集約の遠景を使わない(大きな機体の輪郭が崩れる)。`SelectLod` は `Tests/AmbientShips/AmbientShipsQA.cs` がリフレクションで呼ぶので、シグネチャを変えたら QA も直す。

## 参照

- 仕様: `geometries/O_車両の走行描画.md`(一般車・路駐・公共交通・車両アセット・路線カラー・LRT軌道・ドライブ・索道・路面標示との共有)
- 関連: `geometries/W_歩行者の描画.md`、`V_環境演出の乗り物.md`、`T_交通シミュレーションと走行描画の整合.md`、`programs/サウンドシステム.md`(走行音)、`programs/公共交通ハイライト.md`
- 検証: `unity/Tests/Vehicles/run.ps1`(`-TransitOnly` / `-DriveOnly` / `-ParkingOnly` / `-PedestrianModelsOnly`)、`unity/Tests/Ropeway/animation.ps1`、`unity/Tests/AmbientShips/run.ps1`(環境演出の船のFBXと描画LOD)
