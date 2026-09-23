# Vehicles

車両は描画専用で、経済シミュレーションや永続エンティティへ書き戻さない。仕様は `city-genesis/docs/docs/geometries/O_車両の走行描画.md`。

## 路上駐車

`ParkedVehicleManager` はシーンロード後に自己生成する。`PathSummary.parked_cars` を静止台数として扱い、稼働中の `PATH_STREET` かつ `street_parking_capacity > 0` の道路だけに表示する。`path_traffic`・走行車両の台帳・建物駐車とは独立している。

`ParkedVehicleLayout` は交差点切断済みの `VehiclePolyline` と `RoadPath.GetPathMasterParams` の寸法を使う。歩道を除いた両側車道端へ配置し、切断端から5m、縁石から0.2mを空ける。車種は乗用車60%・商用車30%・トラック10%で道路IDと候補番号から固定し、既存全16外観を使う。車幅が片側に収まらない候補、カーブで歩道・中央分離帯に張り出す候補、車体どうしが重なる候補は除く。台数は候補上限で頭打ち。整数台数を保ち、小数は道路ID固定の確率丸めとする。路駐量そのものは変更しない。

モデルは `VehicleModelResolver.CreateInstance` で接地原点・前方軸を正規化したテンプレートから `PropRenderModel(GameObject)` で抽出する。個体GO・音源・コライダーを作らず、通常モデルを511件以下のGPUインスタンシングで描く。描画半径480m、0.5秒間隔のセル逆引き走査、形状・寸法・通行方向の変更時だけ候補再生成。台数だけの変更ではモデル・候補を維持する。生成は `RenderWorkScheduler` のStart/Stopを対で使い、都市切替は `TransitDataStore.Session` でクリアする。検証は `Tests/Vehicles/run.ps1 -ParkingOnly`。

## ドライブモード

走行車両の`{part}_close`は`VehicleModelResolver`が無印と同じ変換で生成し、LODGroupで画面比率25%以上のとき表示する。無印は画面比率3.5〜25%、専用遠景は3.5%未満を担当し、車両の表示範囲はマネージャーが決める。14/20/21/23の先頭・中間・全外観に接写版がある。公称長への伸縮は全LODを含むルートへ一度だけ適用する。

`DriveMode`はCockpitの`drive:open`で自己生成され、Unity上に車種/路線選択・開始位置の選択・走行HUDを表示する。翻訳済みの文言はTauriから受け取る。自動車は`VehicleManager.Drive.cs`が通常のactive一覧へ1台追加し、前車追従を共用する。乗車中の車を沿道到着予約・範囲外削除の対象にしない。分岐候補はNPCと共通の`CollectCandidates`で進入方向を検証し、未選択時は最も直進に近い道へ進む。

鉄道は`TransitVehicleManager.Drive.cs`が既存路線の第1編成の時計位相を開始位置へ合わせる。路線の往復方向を区別して等加速度時計を逆算し、停車・編成配置・折り返しは通常描画と共用する。折り返しでも同じ車両(identity=0)を追従する。線路形状が欠けたら乗車編成の時計を戻し、最後の位置と描画を保持して待つ。

Space長押し中は`DriveMode.SpeedMultiplier`が5になり、一般車と公共交通の描画時間を一緒に進める。`Time.timeScale`・経済時間・APIポーリング周期は変えない。一般車は1/30秒以下の刻みで前車間隔を再評価する。カメラ/周辺交通/セルロードの中心は乗車位置で、通常入力とカメラ保存は乗車UI表示中に制御する。Esc・編集モード遷移・リロードで解除し、都市切替では古い視点を復元しない。新しい走行モードの定数・バックエンドAPI・セーブ項目は持たない。

- `Vehicle` と `VehiclePolyline` は移動状態と距離補間。`ProjectRange` は累積距離範囲を二分探索して局所射影し、路駐車体の四隅の検査を道路全長の走査にしない。通常の全長射影は `Project`。交差点は車線オフセット済みの点列なので二重に横へずらさない。端を越えたフレームの残り時間を捨てない。
- `VehicleManager` は道路スナップショット・分岐・密度調整・プール。ノード遷移で台数超過を理由にデスポーンしてはいけない。到着予約は交差点を跨ぎ、路肩寄せは道路内だけで行う。
- 全体枠は `VehiclePopulationBudget`。道路別の端数を切り上げず、合算後に整数化する。実在台数をObserveで返し、到着・補充の遅れを端数へ繰り越す。
- 台帳は0.25秒ごとに実体一覧から構築する。交差点中は移動先へ1回だけ数え、到着予約・フェード中も消えるまでは枠を占める。
- 建物入口の幾何(接道辺の中点→敷地内3mの点、有効性検査)は `RoadsideAccess` に置き、車と歩行者で共有する。`VehicleManager.BuildEntrances` はそのパス幾何の左右を通行側・走行方向へ読み替えるだけ。入口の条件を変えるときは `RoadsideAccess` を直し、片方に別実装を書かない。
- 車線と区画線は `RoadLaneLayout`（MeshGenerators/Paths/RoadPath.cs）を共有する。通行方向は `CityInfoStore`。交通量は型付き `PathSummary.path_traffic` を読み、utilizationで代用しない。
- 範囲候補は `GetPathsNearCircle` のセル逆引き。円内区間・発生・デスポーンはすべてrenderRadius+despawnMarginを使う。
- `AmbientVehicleManager` は空と海の環境演出(高高度の航空機3種・沖合の船舶4種)。経済と無関係で、GameObjectを持たず保存座標+`Graphics.RenderMesh`で描く。船は `CoastlineStore.SignedDistance` の先読み採点で陸地を避け、外洋(リング包含数0)にだけ発生する。距離関数は全辺走査なので毎フレーム呼ばない。飛行機雲・航跡は `AmbientTrail`。モデルは `Props/ambient_*`(固定名。prop_master登録なし)。仕様は公開 `geometries/V_環境演出の乗り物.md`。
- 走行音は `Weather/VehicleAmbience` へ通知するだけ(車両側はAudioSourceを持たない)。`VehicleManager.Place` が毎フレーム `Report(Kind.Car, go.GetInstanceID(), 位置, speed)`、`Despawn` が `Remove`。`TransitVehicleManager.ReportSound` が描画した編成ごとに中間車位置と路線時計の距離差分から出した速度を `Report`(バス=Kind.Bus、鉄道=Kind.Train)し、セッション切替で `Clear`。通知が1秒途切れた発音体は自動で外れるので、範囲外へ出た編成の明示解除は不要。
- 回帰検証は `unity/Tests/Vehicles/run.ps1`。独立Unityで本編アセンブリを実行し、本編シーンを変更しない。

- 到着時は `BeginRoadsideExit` でその場の90度旋回→実寸の横移動を行い、検証済みの敷地内退出点へ抜けた後だけフェードする。退出中は道路スナップショット更新で位置を戻さず、管理円外でも退出を完了させる。

- 沿道到着はPATH_STREETかつfrontage_allowedの建物付き接道区画だけ。高速道路・空き地・行き止まりで任意の退出位置を作らない。到着先なしの場合は実体を維持し、補充を抑える。
- UpdateAccessのコルーチンで周辺区画の入口を変更時だけ再計算し、道路方向付き逆探索表を全車で共有する。車ごとの探索や毎フレームの建物全件検索は禁止。入口探索は道路進入時・表の世代変更時のみ。
- 出口候補・道路接続・管理円外の端点が不変なら逆探索表を再構築しない。作業は約1msごとに譲る（単一区画やJITの時間を含む厳密上限ではない）。yieldを跨ぐ接道区画ID列はコピーし、ロードによる集合変更で列挙を壊さない。
- 通常補充は入口か管理円への進入境界。初期配置だけは道路途中に置ける。退出点は区画内部で検証し、退出直前にも建物と接道の存在を確認する。

## 共通時間と公共交通

- 時間・流量の契約の正は公開 `geometries/T_交通シミュレーションと走行描画の整合.md`。平均速度に独立した交差点減速・駅停車を重ねてはならない。
- `RoadMotionData` / `RoadMotionControl`: `/paths/motion` の自由秒数・混雑込み秒数を参照。`VehicleManager` は同一道路・方向・車線の前車へ追従し、締切超過時も自由走行平均速度を超えない。表示標本率は0〜1。バス・LRT(`VEHICLE_BUS`/`VEHICLE_TRAM`。路線車両として`TransitVehicleManager`が描く)のランダム生成は行わない。
- `TransitDataStore`: 色帯と車両で共有する路線一覧。5秒ポーリング＋明示Refresh。Clearで世代を進め、古い都市の応答を破棄する。色帯の表示ON/OFFで取得を止めない。
- `TransitMotionClock`: コアの `journeys.steps` の等加速度区間・停車を時刻から参照する純粋なクラス。車両需要や待ち時間と同じ時間を使う。
- `TransitVehicleManager`: 実調達編成数を位相配置し、周辺の車体だけプールから出す。鉄道は停止中心を基準に各車を距離配置し、折り返し中も個体と前後を保つ。欠けたパス・未生成レールコネクタを直線で補完しない。
- 公共交通の形状更新は0.5秒間隔で、描画可能な路線の経路に含まれる周辺パスだけを計算する。路線なし・車両不足などで描画対象がなければ、周辺パスの検索も点列生成も行わない。
- モデル対応は `Resources/Config/transit_vehicle_models.json`。外観はvehicle_model_config.yamlから路線ID＋編成番号で固定。先頭/後尾=数値バリアント、中間=middle（0）/middle_{番号}。プールキーにも外観を含める。`VehicleModelResolver` が著作原点・長手軸を接地中心/+Zへ正規化し、公共交通の車体長だけはマスターの公称車長に合わせる。幅と高さは変更しない。
- `Tests/Vehicles` は独立Unityへ実FBXとラッパーをコピーし、時間積分・停車・前車停止・編成位置・折り返しの連続性も検証する。本編シーンとモデルは変更しない。

- vehicle_model_config.yamlの簡易パーサは数値キー・数値配列と行末の#コメントを扱う。コメントをキーに含めると全候補が無視されるため、行末コメントを除いてから解析する。

## 歩行者

仕様は公開 `geometries/W_歩行者の描画.md`。台数調整の考え方は車と同じで、パスの端で消さない。

- `PedestrianManager` は `AfterSceneLoad` で自己生成する(シーンに置かない)。対象は `PATH_STREET` / `PATH_WALKWAY` のみで、交通量は `path_traffic["0"]`(VEHICLE_PEDESTRIAN)。時間API(`/paths/motion`)は呼ばず、速度は `LocalConstants.PEDESTRIAN_WALK_SPEED` の個体定数。`renderFraction` は交通量に対する表示倍率(1=等倍。2・4で増やす)で、最低密度は設けない。
- 歩行帯は `PedestrianManager.ResolveBand`(歩道あり=歩道の上、歩道なし=路肩、歩行者専用道路=全幅)。高さは `RoadPartHeights` + `LocalConstants.PEDESTRIAN_SIDEWALK_SURFACE`。
- `Pedestrian.side` / `lateral` は**パス幾何(始点→終点)に対する左右**。反転しても符号を変えない(同じ歩道に留まる)。交差点では進行方向に対する左右を保って次のパスへ写し、ベジエで結ぶ(車道を斜めに横切ってよい)。`travel_direction` は見ない。行き止まりはどこでも折り返す。
- 到着は逆探索表を持たず、到着予約中に進行方向の先へ現れた最初の入口へ入る(同じ側を優先、無ければ反対側へ横断)。閉じた網に入口も円外への出口も無ければ到着を予約しない。
- 実体はGameObjectを持たない。`PropRenderModel("pedestrian_{n}")` の `normal` パーツを `LateUpdate` で `Graphics.RenderMeshInstanced`(1023件ずつ)で描く。FBXが無ければ人型の箱。走行音の通知はしない。
- モデルは `Assets/Models/Props/pedestrian_{0..5}.fbx`(固定名。prop_master未登録。1体332三角形・400未満)。著作は genesis-gallery の `pipeline/jobs/build_pedestrians.py`。
- 駅の入口・ホーム上の待機・連絡橋の歩行は未実装(W doc §W.10)。
- 検証は `Tests/Vehicles/run.ps1` に同居(`PedestrianBands` / `PedestrianModels` / `PedestrianNetwork`)。`-PedestrianModelsOnly`でモデルだけ検査でき、取り込み後の面の閉鎖・巻き順・法線・各部位の外向きも確認する。

## 公共交通の経路共有と選択

`TransitRouteGeometry` は方向付きRunの構築・地形追従点列・生成済みレール接続・車線オフセット・距離補間を車両とハイライトで共有する。形状キャッシュは呼出側ごとに持ち、未ロードのパス・未生成接続を直線補完しない。`TransitVehicleManager.TryPick` は描画中の車体Rendererの境界とレイを交差させ、手前の地形・建物に遮られた車両を除外する。プールから取り出すたびに路線IDを更新し、非表示の車体は選択対象外。ハイライトのON/OFFに依存しない。

LRTはRoute.VehicleType=VEHICLE_TRAMで最内車線の軌道を使う（LrtRoadParts.Offsetが唯一の正）。303は中央単線、431は通常軌道。路線ハイライトも同じ経路設定を使う。交差点は描画済みRailコネクタへ接続し、踏面高さを加える。バスの外側車線配置と混同しない。

LRTのモデル対応は1003→車種14。`Vehicles/14/0`が先頭・後尾共用、`middle`が集電装置付き中間車で、外観候補は0のみ。公称車長10mの低床車体を既存の編成配置で使う。原本は`blender/vehicles.blend`。


道路モデルは10=10外観、11=4外観、12/13=各2外観。vehicle_model_config.yamlの候補数と実FBXを揃える。鉄道を含む全36モデルはProps/350〜367・370〜387の自由配置別名を持つ。形状の正はvehicles.blendで、別名は非公開制作ツールのbuild_vehicle_props.pyがprops.blendへ同期する。Tests/VehiclesのTransitModels/VehiclePropsが候補数・FBX参照・接地・別名の形状一致を確認する。


高速鉄道23は先頭2外観（0/1）と中間2外観（middle/middle_1）に接写版を持ち、自由配置別名384〜387にも同期する。通常FBXは保持する。高速車の前頭部は長い鼻先と曲面風防を一体の断面座標で著作し、メトロ20・中距離21の平面ガラスとは区別する。再生成は非公開制作ツールの`build_rail_close.py --high-speed-only`と`high_speed_surfaces.py`、比較は`render_high_speed.py`。走行車の既存LODと自由配置用PropRenderModelが接写版を選択し、路線マスターの対応は変更しない。


鉄道の専用遠景は14/20/21/22/23の全18モデルとモノレール308/309にある。非公開制作ツールの`build_rail_far.py`が車体箱と左右各一枚の窓帯を著作し、車輪・台車・屋上機器を省略する。貨車には窓を付けない。原点と水平範囲は通常版を維持し、車輪・集電装置を省くため上下の範囲は通常版の内側でよい。12〜42三角形、既存材質を共有する。`build_vehicle_props.py`は370〜387の遠景別名も同期する。走行版は画面比率3.5%未満で`_far`を選び、25%以上では存在する`_close`を選ぶ。専用版がないLODは通常へフォールバックする。自由配置はPropRenderModelの既存の投影LODで専用遠景を使う。通常・接写モデルは保持する。
