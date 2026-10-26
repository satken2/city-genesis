---
name: genesis-prop-parts
description: City Genesisの地形プロップ3Dモデル(森の木・岩・茂みなど装飾オブジェクトのFBX)をヘッドレスBlenderで制作・編集・検証するときに使う。「木のモデルを作って/直して」「岩のプロップを追加したい」「プロップの遠景(_far)モデルを用意したい」「prop_masterに種別を足したい」など、Assets/Models/Props配下のFBXを新規作成・修正する作業では必ずこのSkillに従うこと。Unity側の散布・描画コード(PropManager等)の話はM_地形プロップ.mdとDataLoaders/AGENTS.mdを読むこと。建物パーツは genesis-building-parts、道路・線路パーツは genesis-path-parts を使うこと。
---

> **パス表記の基準**: 公開側のパスは `city-genesis` リポジトリのルート起点。`genesis-core/`・`genesis-gallery/` は別の非公開リポジトリのルートを示し、並置を前提としない。利用できる場合だけ実際の配置に読み替え、手元に無い場合は公開側で確認できる範囲を扱う。

# 地形プロップの制作・編集・検証

制作基盤(ヘッドレスBlender・ライブラリ.blend・バリデータ・ギャラリー)は建物パーツと共通なので、
**まず [[genesis-building-parts]] の「実行環境」と「鉄則」を読むこと**
(Blenderの起動方法はOSと配布形式に応じて確認する。WindowsでStore版を使う場合は実行エイリアスとログ取得にも注意する)。
本Skillはプロップ固有の差分を記述する。非公開制作ツールを使う手順は、そのツールが利用できる場合にだけ適用する。`pipeline/`・`validator/`・`out/` と制作コマンドの実行場所は `genesis-gallery` のルートで、公開側ファイルを渡す引数は絶対パスに読み替える。

## 鉄則(プロップ固有)

1. **規約の正本**は、プロップを描く Unity のコード(`unity/Assets/Scripts/DataLoaders/PropRenderModel.cs`・`PropGeometry.cs` など)のコメントとバリデータ。
   制作前に、全体像として `docs/ja/geometries/M_地形プロップ.md` を読む。
2. **1種別=1FBX。パーツ分割も閾値フォルダも無い。** `Assets/Models/Props/{prop_type}.fbx` だけで
   描画が成立する。`_close` / `_far` は任意のオプトイン。
3. **原点=接地点、XZ中心=原点。** Unityは原点を地表に置き、原点まわりにY軸回転と0.8〜1.2倍の
   一様スケールを掛ける。通常モデルの底面を浮かせない(地中へ延ばすのは可)。幹を省くFarは原点を地表に保ったまま底面を樹冠の高さへ上げてよい。
4. **軽く作る。** 常駐候補は最大25万本をGPUインスタンシングする。無印は数百頂点以内が目安
   (木は46〜58共有頂点、Unity向け推定126〜150頂点)。細部は `_close` に寄せる。
5. **マテリアルは `PROP_*` を既存から流用する**(`pipeline/materials.py --category Props`)。
   既存: `PROP_BARK` `PROP_PINE_GREEN` `PROP_LEAF_GREEN`。
6. モデルの編集は `blender/props.blend`。**コレクション名=種別ID**(`1`, `2`, …)。
   書き戻しは `pipeline/library.py export --category props --only <ID>`。

---

## 既存アセットと種別の対応

| prop_type | display_name | ライブラリのコレクション/オブジェクト | FBX |
|---|---|---|---|
| 1 | 針葉樹 | `1` / `Tree_1`(46頂点・高さ12m・半幅2m) | `Props/1.fbx` |
| 2 | 広葉樹 | `2` / `Tree_2`(58頂点・高さ9m・半幅3m) | `Props/2.fbx` |
| 3 | 岩 | **無し** | **無し**(選べるが何も描画されない) |
| 4 | 草（近景専用） | `4` / `Grass_4`（18枚の両面葉、108三角形） | `Props/4.fbx` |
| 101〜104 | 駅階段 | `101`〜`104` | `Props/101.fbx`〜 |
| 111・112 | 屋根付き歩道橋（24m・32m道路用、コの字階段） | `111`・`112`と専用Far | `Props/111.fbx`・`112.fbx`と`_far` |
| 121〜126 | 片持ち青・緑・電光看板（各4/6車線用） | `121`〜`126` | `Props/121.fbx`〜。面は共有画像 |
| 201〜204 | 工事車両 | `construction_*` のメッシュを共有する別名 `201`〜`204` | `Props/201.fbx`〜 |
| 301〜307 | 航空機・船舶 | `ambient_*` のメッシュを共有する別名 `301`〜`307` | `Props/301.fbx`〜。船304〜307は `_close`・`_far` も |

- 種別の一覧は `genesis-core/assets/master.yaml` の `prop_master`(`display_name` のみ)。
  **`prop_master` はスプレッドシート由来ではなく master.yaml に直書き**されており、生成スクリプトは無い
  (ほかのマスターの取り込みは対象のセクションだけを書き換えるので、このセクションは残る)。
  種別を足すときは `prop_master` に `ID: {display_name: …}` を追記する
  ([[master-data]] Skillの「yamlを直接編集しない」はこのセクションには当たらない。2026-09-08時点)。
- 木2種は `pipeline/jobs/build_prop_trees.py` で原点に著作する。通常72/96三角形、専用Far14/20三角形（幹なしの八角錐/丸い葉塊）。
  広葉樹は4個の葉塊、針葉樹は細身の4段樹冠。両種の再生成は同ジョブを一度実行する。
  葉はスムーズ法線と粗さ0.9、共有256×256色画像（Props/Textures）を使う。画像は外部参照でFarも共有し、FBX拡散色は白にする。
- **樹木・植え込みの葉**(森の木・街路樹・公園の木・戸建ての植え込みと庭木)は、葉の材質の面へ樹冠全体に寄せた法線と陰り(頂点色R)を焼き、
  UnityではFBXの葉材質を取り込み設定で `Assets/Models/Materials/` の共有材質(`CityGenesis/Foliage`)へ割り当てる。
  この材質は頂点色Rを樹冠の陰りとして読むので、焼いていないFBXを割り当てない。焼き方・対象・再生成順は非公開制作ツールの `pipeline/jobs/foliage.md`。
- **固定名モデル**: `construction_*`(工事中のパスに置く工事車両)と `ambient_*`(空と海の環境演出の航空機・船舶。V文書)は
  同じ `props.blend` / `Props/` に置く。固定名のFBXは描画コードが直接読み、手動配置用には数値ID別名を `prop_master` に登録する。
  `ambient_*` は船が喫水線原点(船体は原点より下)なので、バリデータは接地検査をせず水平中心と専用予算だけを見る。
  船5種は通常・接写・遠景を持ち、非公開制作ツールの `pipeline/jobs/ambient_ships.py` が同じ寸法表から著作する(航空機は通常だけ)。
  Unityは専用の `_close` / `_far` を持つモデルだけを投影比で切り替える。検証は `unity/Tests/AmbientShips/run.ps1`。
  再生成は `pipeline/jobs/ambient_vehicles.md`。手動配置用の数値ID別名(201〜204・301〜307。船は `_close` / `_far` も)は
  `pipeline/jobs/build_prop_aliases.py` がメッシュ共有で作る。**元モデルを再著作したら別名も再実行して書き出す**。
- **交差点の信号機** `traffic_signal_{様式}_{pole|arm|head}`(`TrafficSignalBuilder.cs`)も固定名モデルで、`prop_master` に登録しない。
  Unityが交差点ごとに3部品を1メッシュへ結合するので、原点は3部品とも柱の接地面の高さに揃え、取付高さはモデルが持つ。
  アームはBlender Y+0.5(柱の軸)〜-0.5(先端)の単位長で長手だけ伸縮され、灯器は吊り金具の真下が原点で表示面をBlender -Yへ向ける。
  バリデータは柱の軸・アームの単位長・灯器の下端4.5m以上を検査し、アームと灯器には接地検査をしない。
  様式を足すときは同じ3部品を `traffic_signal_{新様式}_*` で作り、Unityの `TrafficSignalStyle` に様式を登録する。
  著作は非公開制作ツールの `pipeline/jobs/build_prop_traffic_signals.py`、寸法・再生成は同じ場所の `traffic_signals.md`。

---

## 描画LODとファイル名

**制作時に通常・接写・遠景の役割を必ず決める。** 既存の通常モデルを基準に、種類を識別できる輪郭・比率・向き・接地位置を保つ。LOD追加だけを理由に別の樹形や別の設備へ作り替えない。`_close` と `_far` のファイル自体は、必要な種類だけに追加する。

| ファイル | 役割 | 規約 |
|---|---|---|
| `{id}.fbx` | Normal(必須) | 制作の基準。これだけで成立する |
| `{id}_close.fbx` | Close(任意) | 無印と同じ原点・bbox(許容0.05m)。接写時に差し替え |
| `{id}_far.fbx` | Far(任意) | 無印と同じ原点・水平bbox・天端(許容0.3m)。幹の省略で底面を上げるのは可。無いと**Unityが自動生成**: 128三角形を超えるサブメッシュだけ4×8×4格子で頂点集約。軽いモデルは元形状のまま |

`_far` を著作するのは「自動集約で輪郭が崩れる目立つ種類」だけでよい。
草（4）は`PropRenderPolicy`で近景専用に指定され、100mで非表示・90m未満で復帰する。通常モデルだけを著作し、遠景版は作らない。範囲散布は水平125m以内の小区画のみ生成・保持する。再生成は非公開制作ツールの`pipeline/jobs/build_prop_grass.py`。既存の葉材質・画像を共有する。
建物・パスのFarは無印と対にしない`far`1体で、規約が別（建物は `FarBuildingMeshBuilder.cs`、パスは `PathFarModel.cs` のコメント）。プロップと鉄道車両は専用`_far`を持てる。

### 造形の選び方と比較検証

- **通常は大量配置を前提に軽くする。** 輪郭や用途を決める幹・樹冠・岩塊・支持構造を残し、小枝・細い桟・取付金物は接写へ寄せる。通常の簡略化を依頼された場合も、外形と接地・配置の意味を保つ。
- **接写には対象に合う細部を加える。** 樹木なら枝分かれや葉塊の奥行き、岩なら稜線、設備なら継ぎ目・枠・支持金物などを選ぶ。細分割で頂点を増やすだけにせず、近距離で形や陰影の差として見えることを確認する。全種類に同じ装飾を足さない。
- 樹皮・葉の模様・広告・文字など平面的な情報はテクスチャを優先する。看板の文字や葉の1枚1枚を無条件に立体化しない。既存材質・画像を共有し、新しい画像は `asset-credits` に従って出所または自作・AI制作の記録を残す。
- **Closeは上表の原点・bboxを守る。** 建物の前方張り出し例外はプロップに適用しない。接地・水平中心・向きを維持し、細部追加でLOD判定や障害物との離隔が変わらないようにする。Farの幹省略は上表の許可範囲だけで行う。
- 変更前／通常／接写／著作した遠景を同じ視点・スケールで比較する。ギャラリーの単体画像だけでなく、Unityで複数配置時の密度・向き・接地とLOD切替を確認する。通常・接写・遠景の三角形数とUnity推定頂点数、バリデータの結果を記録する。実機確認ができない場合は、単体検査と区別して未確認範囲を報告する。

---

## 制作の標準手順

1. `master.yaml` の `prop_master` で種別IDを確認する(新種別なら未使用IDを決める。1〜99=自然物、101〜=駅設備、201〜=工事車両、301〜=航空機・船舶)
2. 使うマテリアルを決める(既存 `PROP_*` 優先。新規は `PROP_` 接頭辞)
3. Blender用スクリプトで `blender/props.blend` を開き、コレクション `{id}` にメッシュを著作する。
   `pipeline/jobs/_common.py`(`open_library` / `link_into(["{id}"], obj)` / `get_material` / `save_library`)を使い、
   手著作でないモデルは `pipeline/jobs/build_prop_{id}.py` として残す(寸法調整を何度でも回せる)。
   回転・スケールは適用済みにし、オブジェクトは原点に置く。
   WindowsでStore版を使う場合は実行エイリアスで起動し、ランチャーから完了を確認できない場合はログ/DONEマーカーで確認する
4. `pipeline/library.py export --category props --only {id}` でFBX化
   (Windowsで制作ツールのラッパーとPythonランチャーが利用できる場合は `py -3 pipeline/windows_run.py pipeline/library.py export --category props --only {id}`)。
   `--only` は部分一致なので、`1` は `10`〜`19` にも当たる。種別が増えたら書き出し結果の `EXPORTED` 行を確認する
5. 公開側の `unity/Assets/Models/Props` を対象に `python validator/validate.py <Propsフォルダの絶対パス>` → error 0 まで修正ループ
   (Windowsで制作ツールのラッパーとPythonランチャーが利用できる場合は `py -3 pipeline/windows_run.py validator/validate.py <パス>`)
6. `pipeline/build_gallery.py --only Props` でサムネイル更新 → ユーザーが目視。
   **プロップに組み立てプレビューは無い**(単体サムネイルのみ。散布の見え方は実機でしか確認できない)
7. 新種別なら `prop_master` に追記する(上記)。Tauriの配置パネルは `/api/v1/master/prop_types` から一覧を作るので
   バックエンド再起動で選択肢に出る
8. Unityを利用できる環境で開き、`ModelPrefabImportWatcher` が `Resources/Models/Props/{id}.prefab` を自動生成したことを
   確認して実機で散布・個別配置を目視する。Windows環境でUnity単体の回帰を行う場合は `unity/Tests/Props/run.ps1`
   (`Assets/Models/Props` 直下の全FBXを独立プロジェクトへコピーして検証する)

---

## バリデータが検査すること(`validator/rules.py` の Props 分岐)

- マテリアル命名(`PROP_*` 推奨・`.001` サフィックス検出)
- **接地**: ルートノード基準で bbox 底面が原点より0.05m以上浮いていれば error(`prop-floating`)
- **水平中心**: bbox のXZ中心が原点から0.25m以上ずれていれば warn(`prop-off-center`)
- **`_close` / `_far` の対**: 基準モデルの存在と bbox 一致(`far-without-base` / `far-bbox-mismatch`)
- **頂点予算**: Unity推定頂点数が Normal 600 / Close 4000 を超えると warn(`vertex-budget`)

規約を新たに知った・増やしたら rules.py に追加すること。

## 知らないと事故る仕様

- **障害物との離隔は bbox から決まる**: 水平半径(X/Z半幅の大きい方)×0.15×スケール(上限8m)。
  bbox が実体より大きい(離れた小片・不可視ジオメトリ)と、建物や道路から不自然に離れて生える。
- **投影サイズによるLOD判定も bbox の半径**(`extents.magnitude`)で行う。細長い飾りが1本あるだけで
  遠くまで Normal で描かれ続ける。
- **ルートノードの変換だけ無視され、子ノードのローカル変換は残る**。複数オブジェクトで組む場合は
  親子関係とローカル位置に意味がある(単一メッシュに結合するのが安全)。
- **非対称形状は Blender(x,y,z)→Unity(-x,z,-y)**。散布の向きは乱数なので通常は気にしなくてよいが、
  個別配置で rotation を指定する用途(看板・ベンチ等)では前面の向きを決めておく。
- マテリアルはUnityが実行時に複製して instancing を有効化する。**GPUインスタンシング非対応のシェーダーだと描画されない**。
  GraphicsSettings の Instancing Variants は Keep All を維持する(プロジェクト設定側。モデル側の作業ではない)。
- **一本ごとの GameObject / Collider は生成されない**。クリック判定やコライダー前提の設計はできない。
- 経済・コスト・資源とは一切接続しない純装飾なので、`agent_master` / `path_master` のような寸法マスターは無い。
  必要なのは `prop_master` の表示名だけ。

## 道路・鉄道を走る車両モデル

鉄道の接写版は14（トラム）・20（メトロ）・21（中距離）・22（貨物）・23（高速鉄道）の全外観と中間車・貨車、およびモノレール308/309にある。非公開制作ツールの`pipeline/jobs/build_rail_close.py`が、トラム14は通常の車体断面から曲面外板と細部を著作し、20/21/22/23は外観モジュールが、モノレール308/309は`monorail_vehicles.py`が通常と同じ寸法表から作る。通常FBXは保持する。再生成・比較・専用頂点予算は同ディレクトリの`rail_close.md`を参照する。走行版はVehicleModelResolverのLODGroup、自由配置別名はPropRenderModelで接写を選ぶ。`build_vehicle_props.py`は接写も同期する。

道路車両・編成車両の制作にも共通のBlender基盤を使うが、保存先は `blender/vehicles.blend` と `Assets/Models/Vehicles/{車種}/{モデル名}.fbx`。走行用の全44モデルは自由配置用の数値ID 350〜367・370〜395でも `prop_master` に登録する。制作前に公開 `geometries/O_車両の走行描画.md` と `unity/Assets/Scripts/Vehicles/AGENTS.md`、モデルの前提を書いた `Vehicles/VehicleModelResolver.cs` のコメントを読む。

- 原点は接地面・水平中心、Blender -Yが前方。1FBX=1メッシュで既存の車両用 `PROP_*` 材質を共有する。
- 先頭・後尾は数値バリアント、中間は `middle` / `middle_1` / `middle_2`(外観0が `middle`)。LRT車種14は `0` と `middle` の1外観。貨物22の貨車は `wagon` / `wagon_1` / `wagon_2`。最後尾の反転はUnityが行う。
- 外観候補は `Resources/Config/model_weights.json`(Unity の `Tools/Asset Packs/Update Standard Model Weights` で実在のモデルから作り直す)、路線種別との対応は `transit_vehicle_models.json`。既存IDを確認して登録する。
- exportのカテゴリは `vehicles`、検証対象は `Assets/Models/Vehicles/{車種}`、ギャラリーは `Vehicles`。Unity回帰は `Tests/Vehicles/run.ps1 -TransitOnly`。自動生成されたResourcesラッパーとFBXのGUIDが対応することを確認する。
- 再生成ジョブは非公開制作ツール `genesis-gallery/pipeline/jobs/vehicles.md`、LRTは同ディレクトリの `lrt_vehicles.md` を参照する。

手動配置用モノレール308/309は `props.blend` の例外的な車両プロップ。原点は走行タイヤと桁上面の接点で、床面は原点の0.30m上(桁の天端スプライン+1.2mに置くとホーム面に揃う)。床下カバーは全長で高さ-0.38m、前後の台車部で-0.90mまで桁の両脇へ下がって桁をまたぐ。bbox底面を原点に揃え直さない。負の高さでは左右0.51mの桁用離隔を維持し、`Tests/Monorail/run.ps1`で確認する。形状は非公開制作ツールの`pipeline/jobs/monorail_vehicles.py`が通常・接写・遠景を同じ寸法表から作る(寸法・再生成は同じ場所の`monorail.md`)。


走行車両の自由配置別名は、非公開制作ツール `genesis-gallery/pipeline/jobs/vehicle_prop_catalog.py` が対応表を持つ。`build_vehicle_props.py -- --blend <props.blend>` は隣のvehicles.blendから全モデルの形状・UV・材質を同期する。道路車両は `build_vehicles.py --road-only` で18外観（10種/4種/2種/2種）の通常・接写・遠景を同じ寸法表から一度に再生成し(外観は `road_vehicles.py`・`road_buses.py`・`road_trucks.py`、形状の道具は `road_shapes.py`)、その後に別名も同期する。同期は折った稜線も写す(写さないと別名だけUnity取り込み後の頂点数が変わる)。道路車両のLODと制作規約は `Vehicles/VehicleModelResolver.cs` のコメント、制作記録・再生成手順は非公開制作ツールの `pipeline/jobs/vehicles.md`。車両のプレビューは裏面を描かない材質で確認する(Blenderの既定表示は裏返った面も描くが、UnityのURPは描かない)。鉄道/LRTを再著作した場合も同期が必要。書き出しはvehiclesとpropsの両カテゴリへ行い、Unityの `Tests/Vehicles/run.ps1 -TransitOnly` で別名も確認する。

メトロ20・中距離21・高速鉄道23は各3外観(0=スタンダード・1=近未来・2=レトロ、各1配色)。メトロと中距離は路線ごとにプレイヤーが選ぶ(API `vehicle_model`)。高速鉄道は公共交通の路線種別が無く、自由配置と候補だけを持つ。非公開制作ツールの外観モジュール`metro_vehicles.py`・`intercity_vehicles.py`・`high_speed_vehicles.py`が通常・接写・遠景を同じ寸法表から著作する(`build_vehicles.py`/`build_rail_close.py`/`build_rail_far.py`の`--metro-only`・`--intercity-only`・`--high-speed-only`)。共通の組み立て手順は`rail_design.py`(中距離・高速は断面を長手に並べるロフト外板の`LoftDesign`)、形状の道具は`rail_shapes.py`。外観を直すときは外観モジュールだけを編集し、旧来の車体を後から張り直す処理を足さない。自由配置別名はメトロ372〜375・388・389、中距離376〜379・390・391、高速384〜387・392・393。仕様・再生成・制作記録は非公開制作ツールの`pipeline/jobs/metro_vehicles.md`・`rail_designs.md`。外観を増やすときは、Unityの`model_weights.json`(メニューで作り直す)、UIの`transitVehicleModels.json`と7言語の表示名、`prop_master`、`vehicle_prop_catalog.py`、バリデータの別名範囲、`Tests/Vehicles`の候補数と別名IDを揃える。

貨物22は機関車3外観(0=JR貨物風・1=アメリカ風・2=インド国鉄風)と貨車3種(`wagon`=2段積みコンテナ車・`wagon_1`=石炭ホッパ車・`wagon_2`=タンク車)。貨車は機関車の外観と独立に選ぶ前提で、middle系の名前を持たず、機関車の路線色の材質を使わない。非公開制作ツールの外観モジュール`freight_vehicles.py`が通常・接写・遠景を同じ寸法表から著作する(各ジョブの`--freight-only`)。全モデルの連結面は±10m・連結器の中心高さ0.88mで揃え、天端は架線のトロリ線(レール面上約5.1m)より低く保つ(2段積みのコンテナは背高でない高さで5.05m)。路線色の部位は機関車の車体の青・オレンジとインドの赤の帯(`PROP_FrJrBlue`・`PROP_FrUsOrange`・`PROP_FrInRed`)で、`transit_vehicle_livery.json`に登録してある。自由配置別名は機関車380・381・394、貨車382・383・395。試作は`render_freight_vehicles.py`がライブラリへ保存せずに外観モジュールを組み立てて撮る。仕様・再生成・制作記録は非公開制作ツールの`pipeline/jobs/freight_vehicles.md`。


鉄道の専用遠景は14/20/21/22/23の全26モデルとモノレール308/309にある。非公開制作ツールの`build_rail_far.py`が車体箱と左右各一枚の窓帯を著作し、車輪・台車・屋上機器を省略する。貨物22は外観モジュールが運転台・フード・コンテナ・タンクの側面形の柱と台枠の帯で作る。原点と水平範囲は通常版を維持し、車輪・集電装置を省くため上下の範囲は通常版の内側でよい。鉄道12〜42三角形・モノレール64三角形以内、既存材質を共有する。`build_vehicle_props.py`は370〜395の遠景別名も同期する。走行版は画面比率3.5%未満で`_far`を選び、25%以上では存在する`_close`を選ぶ。専用版がないLODは通常へフォールバックする。自由配置はPropRenderModelの既存の投影LODで専用遠景を使う。通常・接写モデルは保持する。
