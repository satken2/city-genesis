# Vehicles

車両は描画専用で、経済シミュレーションや永続エンティティへ書き戻さない。仕様は `city-genesis/docs/docs/geometries/O_車両の走行描画.md`。

- `Vehicle` と `VehiclePolyline` は移動状態と距離補間。交差点は車線オフセット済みの点列なので二重に横へずらさない。端を越えたフレームの残り時間を捨てない。
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
- `RoadMotionData` / `RoadMotionControl`: `/paths/motion` の自由秒数・混雑込み秒数を参照。`VehicleManager` は同一道路・方向・車線の前車へ追従し、締切超過時も自由走行平均速度を超えない。表示標本率は0〜1。バスのランダム生成は行わない。
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
