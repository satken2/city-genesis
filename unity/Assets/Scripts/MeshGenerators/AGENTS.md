# MeshGenerators フォルダ

建物・土地区画・パス(道路)・交差点の具体的なメッシュ生成アルゴリズム。
DataLoaders のマネージャー群から呼ばれる(呼び出し側のフローは `DataLoaders/AGENTS.md` 参照)。

## ディスパッチの仕組み (MeshFactory.cs)

| ファクトリ | ディスパッチ | 備考 |
| --- | --- | --- |
| `PathMeshFactory.GeneratePathMesh` | `path_variant` 固有辞書 → `model_type`(`path_master`のフィールド) | **ジェネレータの選択は `model_type` で行う**(`path_type` ではない)。`"default"`→`RoadPath`、`"track"`→`TrackPath`、`"none"`→メッシュ生成なし(null返却)。`model_type` 未設定時は `path_type` から推論(PATH_TRACK/MONORAIL→track、それ以外→default)する過渡措置あり。variant 辞書は将来用で現状空。インフラパス(送電線・導水管)は `model_type=default` でRoadPath扱い、body モデル未配置なら白ストリップフォールバックで描画される |
| `NodeMeshFactory.GenerateNodeMesh` | 常に `RoadIntersectionMeshGenerator` | 接続パスが2本未満なら null。**インフラノード(`type`がインフラ財ビットを含む=`InfraNetworkUtil.IsInfraMask`)は呼び出し側(PathManager.UpdateNodeIntersectionMesh)が事前に除外する**ため交差点アスファルトは生成されない(DataLoaders/AGENTS.md の垂直コネクタの節を参照)。線路が2本以上接続するノードでは `TrackIntersectionMeshGenerator` がレールコネクタを追加計算する(下記「線路」の節参照) |
| `ParcelMeshFactory.GenerateParcelMesh` | usage_id で辞書検索、未登録は `DefaultParcel` | 現状は常に Default |
| 建物 | ファクトリを介さず `BuildingMeshBuilder.Generate(buildingData)` を直接呼ぶ | type/variant に応じた Body/Roof ビルダーへ内部で振り分け、該当なしは Default〜 |

## 道路 (Paths/RoadPath.cs) — Dreamteck Splines ベース

- 道路は1枚のメッシュではなく、**プレハブパーツを SplineMesh でスプラインに沿って曲げた子オブジェクトの集合**として生成される。
  - パーツ: `body` / `line`(実線・点線) / `sidewalk` / `wall` / `center` / `tree`(Instantiate配置) / `pillar`(高架用、Instantiate配置) / `pillar_cap`(柱の上部キャップ用、Y方向非伸縮、Instantiate配置)
  - プレハブは `ModelManager` 経由で `Models/Paths/p{path_variant}/{パーツ名}` をロードし、無ければ `Models/Paths/p{path_type}/{パーツ名}` にフォールバックする。**新しいバリアントを追加するときはモデルを置くだけでよい**(コード変更不要)。道路本体（`body`）のモデルがない場合は、スプラインに沿った白い板（ストリップメッシュ）を動的に生成してフォールバックする。
  - 幅はマスターデータ(`MasterDataStore.GetPathMaster(path_variant)`)の width / sidewalk_width / lanes / center_width 等から決まり、プレハブメッシュの `bounds.size.x` との比率でスケールされる。
  - **横方向の配置は「パーツ中心の位置」で指定し、プレハブのピボット位置(中心/端)の差は `CreateSplineMesh` が bounds 中心基準に自動補正する**。プレハブの高さ方向のピボット規約(body=路面上面、line/sidewalk/wall=下端、pillar/pillar_cap=上端(路面原点))は `RoadPartHeights.cs` と `genesis-docs/docs/geometries/G_道路メッシュの生成.md §6.4` を参照。
  - 左右非対称パーツの向き規約はパーツごとに異なる: **wall は「右側用」(右側=原型・左側=Xミラー)、sidewalk は「左側用」(縁石＝道路中心側の面が +X＝進行方向右側。左側=原型・右側=Xミラー)**。交差点内の歩道コネクタ/エンドキャップ(`RoadIntersectionMeshGenerator`)もこの sidewalk 規約でスプラインの進行方向を選ぶ。ミラーは `CreateMirroredMeshInternal` でX反転メッシュを複製(法線反転+三角形インデックス逆順)。**ミラーメッシュは静的キャッシュ(`MirroredMeshCache`)でメッシュ名単位に再利用される**ため、同じプレハブから2回目以降は複製が発生しない。
  - **高さオフセットは `RoadPartHeights` の定数に集約されている**(スプライン点=地形+0.1、body=スプライン+0.1で路面上面=地形+0.2、line=+0.12 等)。ハードコードの高さ値を書かないこと。
  - **Extrudeタイル長は `MAX_EXTRUDE_TILE_LENGTH`(2.5m)で上限を切る**(autoCountは使わない)。Z分割のないプレハブがタイル単位で弦近似され、縦カーブで他パーツと上下にずれるのを防ぐため。**`single_span=true`のバリアントは例外**: body をタイル化せず `count=1` で1枚引き伸ばし、アンカー点(柱)間の直線スパンにする（地形追従しない高架橋・桁橋用）。スプラインも地形追従細分化せずアンカー点のみで構築する。
- 車線数・中央分離帯の有無でラインの本数/実線/点線が変わる(`CreateRoadParts`)。点線は SplineMesh の Place モードで等間隔配置。**ダッシュはZピボット補正+clipFrom/clipToで両端から半ダッシュ分内側に制限**し、道路端からの突き出しを防ぐ。
- `PathMeshResult` の意味: `Mesh`=**コライダー用ストリップメッシュ**(描画用ではない)、`GeneratedObject`=描画用パーツ群のコンテナ、`ModulesCreated`=パーツ生成済みフラグ。コライダーは PathManager 側で `Physics.BakeMesh` を別スレッド実行してから装着される。
- `curved=false`+ウェイポイントありの道路は `GenerateLinearPathWithCorners`: セグメントごとに独立スプラインを作り、角はダミーの PathSummary 2本を `NodeMeshFactory` に渡して交差点と同じロジックで切断・充填する。
- マスターデータ取得(`GetPathMaster`)は `GetPathMasterParams` 静的メソッドに集約されている。`Generate` / `GenerateLinearPathWithCorners` 両方から呼ばれる。
- 高さ(Y)は呼び出し元(PathManager.CalculatePathPoints)で既に地形追従済みの点列を受け取る。相対Y制御の考え方はルートAGENTS.mdの「Y座標の考え方」参照。
- **地下パス（負の相対Yを持つ）**: 点列の分割は呼び出し元(`PathManager.GenerateUndergroundPathMesh` + `Utils/TunnelSectionUtil`)が行い、RoadPath/TrackPath は描画区間ごとに呼ばれる。トンネル区間の境界に置く `portal` パーツ（`Models/Paths/p{variant}/portal`、ローカル+Zがトンネル内部向き）の配置も PathManager 側。RoadPath 側では地下パスに柱（pillar）を配置しない（`PlacePillar` は路面のワールドYをそのままスケールに使うため地下では破綻する）。
- **RoadPath は線路と共用する共通ヘルパーの置き場でもある**: `CreateSplineMesh`（`stretchToWidth=false` でXスケール1の実寸配置になる。レール・架線用）/ `BuildSpline`（点列→SplineComputer）/ `BuildColliderMeshFromSpline` / `CreateFallbackRoadBody` / `GeneratePillars` / `GetOrCreateRoadMaterial` / `GetPathMasterParams` は public static で、`TrackPath` や PathManager（工事中パス生成）からも呼ばれる。**同等の処理を再実装しないこと。**
- **工事中(STATUS_CONSTRUCTING)のパスは完成パーツを描画しない**: 分岐は `PathManager.CreateOrUpdatePath`（DataLoaders/AGENTS.md参照）にあり、`Paths/ConstructionMeshUtil.BuildDirtStripMesh`（点列に沿った土の台形マウンド。高さは相対Yを無視して常に地表）+ `RoadPath.GeneratePillars`（高架の柱のみ）+ portal（トンネル坑口のみ）で「工事現場」を構成する。`model_type=none` のパスは工事中も非表示。仕様の正は `genesis-docs/docs/geometries/G_道路メッシュの生成.md §15`。

## 線路 (Paths/TrackPath.cs / Paths/TrackIntersectionMeshGenerator.cs)

- 線路(PATH_TRACK)・モノレール(PATH_MONORAIL)は `TrackPath` が生成する。RoadPath と同じ SplineMesh パーツ組み立て方式で、パーツ構成が異なる: `body`(道床。全幅に引き伸ばし。高架/橋梁はバリアント専用bodyで桁断面を表現) / `track`(1軌道分=レール2本+枕木。**Xスケールせず実寸**で lanes 本を等間隔配置。ゲージ維持のため) / `wire`・`pole`・`beam`(架線系。マスターの `wire_spacing` が null なら非電化でスキップ) / `wall`・`pillar`(道路と共通) / `platform`(駅パスのプラットフォーム。下記)。仕様の正は `genesis-docs/docs/geometries/N_線路メッシュの生成.md`。
- 軌道オフセットは `TrackPath.GetTrackCenterOffset`(width/lanes等分の帯の中央)に集約されており、ノード内レールコネクタの軌道位置もこれを共有する。
- 道床(body)と軌道(track)が両方無いときだけ白ストリップにフォールバックする。**モノレールは body 無し(track=桁のみ)が正常構成**。
- **駅パスのプラットフォーム**: 駅パスの width は通常線路と同じ「線路コリドーの幅」で、線路部分は通常線路と同一描画(前後の線路とシームレス)。ホームは `platform` パーツで、**プレハブ原点を線路中心線に合わせモデル実寸のまま**配置する(`CreateSplineMesh(stretchToWidth:false, compensatePivot:false)`。X引き伸ばし・ピボット補正なし=張り出し幅はプレハブ形状が持つ)。マスターの `platform_width`(PathMasterItem。null=なし)はホームの**論理幅**で、敷地・コライダー・柱/壁位置の算出にのみ使う(描画幅には使わない=モデル実寸を `platform_width` に合わせて作る)。設置側はパスの `platform_side`(共有定数 `PLATFORM_SIDE_OUTSIDE/INSIDE/BOTH`。**PathSummaryへのアクセスは `GetPlatformSide()` 拡張メソッド=リフレクション経由**。スキーマ未再生成でも落ちない)で、内側/外側→左右の解決は `TrackPath.ResolvePlatformExtents`(`CityInfoStore.TrafficHand` 基準。左側通行=内側は進行方向右)。**この解決はバックエンドの敷地ポリゴン算出(geo_paths.get_platform_extension_widths)の鏡写しで、片方だけ変えると敷地と見た目が食い違う。**ホーム側では①架線柱をホーム外縁+マージンへ外出し、②防音壁をホームの外側へ、③近接線路の結合描画を`SuppressCorridorSide`で無効化、④コライダーをホーム込みの非対称幅(`BuildColliderMeshFromSpline` の lateralOffset)にする。仕様の正は `genesis-docs/docs/geometries/N_線路メッシュの生成.md §7`。
- **近接線路の結合描画(複線見え化)**: 内部データに「複線」の単位は無く、複線は一方通行の単線線路2本で表現される。XZ・Yともに近接して並走する**同じpath_type**の線路は `TrackAdjacency.Compute`(Paths/TrackAdjacency.cs。閾値=`LocalConstants.TRACK_MERGE_*`、エッジギャップのチェーン連結で3本以上もコリドー化)が結合コリドーを計算し、TrackPath が①道床ギャップのバラスト帯充填(**path_id小の側が担当**して二重生成を防ぐ。道床天端-2cm・両側に0.5m食い込み)、②結合側の防音壁の区間クリップ省略、③架線柱/ビームの**コリドー内最小path_idの線路(主線路)のみ**によるコリドー全幅配置を行う。合流・分岐で収束中の線路(接線が揃う前)もエッジギャップが閾値以下なら結合を開始する(`TRACK_MERGE_MAX_CONVERGE_ANGLE_DEG`=45°。20°に揃うのを待つと複線合流で壁が相手の本体に被る)。防音壁は結合判定と独立に、他線路の道床が壁の位置に物理的に掛かる区間でも省略される(角度不問。`Left/RightWallBlocked`)。相手線路の端点(交差点ハルの切断点)への最近接クランプが道床の重なる距離で起きた場合は虚像として隣接に採用しない(外側の壁が数m抜け落ちる誤判定の防止)。隣接線路の点列は `TrackAdjacency.NeighborProvider`(PathManager.Awake が設定)から取得し、未設定(テストシーン等)なら単独描画。結合は描画専用でバックエンドには影響しない。隣接線路の変更・撤去時の再生成はPathManager側(DataLoaders/AGENTS.md参照)。仕様の正は `genesis-docs/docs/geometries/N_線路メッシュの生成.md §8`。
- **ポイント(分岐器)・平面交差**: 交差点の凸包・切断点は道路と共通(`RoadIntersectionMeshGenerator`)。`TrackIntersectionMeshGenerator.BuildRailConnectorSpecs` が「偏向角 ≤ `LocalConstants.RAIL_MAX_TURN_ANGLE_DEG`(45°) の通行可能な線路ペア」にのみレールコネクタ(`RailConnectorSpec`)を計算し、`NodeMeshResult.RailConnectors` として返す。実体化は `TrackPath.CreateRailConnectorObject`(PathManager.UpdateNodeRailMesh から呼ばれる)。**この閾値と判定はバックエンドの転回制約(parameters.yaml の RAIL_MAX_TURN_ANGLE_DEG、transit_route のエッジベースDijkstra)の鏡写しであり、片方だけ変えると見た目と経路探索が食い違う。**

## 交差点 (Paths/RoadIntersectionMeshGenerator.cs)

- 各接続道路の幅(マスターデータを正とする)から `IntersectionPolygonUtil.ComputeIntersectionPolygon`(Utils/、NetTopologySuite使用)で交差点ポリゴン(凸包)を計算する。
- 戻り値 `NodeMeshResult` の `ConnectionPoints`(pathId→切断点)が重要: **PathManager がこれを使って各道路の端点を交差点の縁まで切り詰める。**交差点形状を変えると道路端の再生成がセットで走る。**切断点のYは「スプライン点の基準面」(地形+`ROAD_SPLINE_OFFSET`)で返す**(道路の他のスプライン点と同じ基準。路面上面の高さで返すと道路始端が盛り上がる)。
- 歩道は一枚メッシュではなく `SidewalkConnectorSpec`(縁石ラインの点列+幅)として幾何情報だけ返し、表示側が共通の `RoadPath.CreateSidewalkConnectorObject` で通常道路と同じ sidewalk プレハブを SplineMesh で曲げて配置する(マテリアルも `RoadPath.GetOrCreateRoadMaterial` のキャッシュ経由で通常道路と統一)。**歩道の有無に関わらず全接続道路を角度順に並べ、隣接ペアの両方が歩道持ちの場合のみコネクタで接続、間に歩道なし道路が挟まる場合は歩道端をエンドキャップ(先細りの丸め終端)で終端する**(歩道あり道路だけでペアを作ると、対向の歩道同士が交差点を横断して直結される)。
- 高さは `RoadPartHeights` の体系に従う: アスファルト=基準面+`BODY_ABOVE_SPLINE`(路面上面と同一平面)、歩道コネクタ=基準面+`SIDEWALK_ABOVE_SPLINE`(道路の歩道下端と面一)。地形サンプリングは `HeightmapTextureManager.GetTerrainHeight`(道路側と同一API)。
- **相対Y≠0のノード(高架同士の平面交差)は水平面モード**: `Generate` の `nodeRelativeY` 引数(PathManager が `nodeData.GetRelativeHeight()` を渡す)が0以外なら、地形をサンプリングせずノード絶対Yの水平面を基準にする。地形基準だと空中の交差点が地面に描かれ道路端も引き下げられる。`RoadPath.GenerateLinearPathWithCorners` の折れ点充填も同じ引数にウェイポイントの相対Yを渡す。
- 交差点アスファルトのマテリアルは代表バリアントの body プレハブから解決し(`GetOrCreateRoadMaterial`経由)、無ければ `PATH_ASPHALT`→`NodeMaterial` にフォールバックする。代表バリアントの選択は**道路と線路の混在ノード(踏切)では道路側を優先**し、同カテゴリ内では最大variant(線路バリアントは番号が大きいため、単純な最大値だと踏切が道床の見た目になる)。線路のみのノードでは body=道床プレハブが選ばれ、自動的に道床マテリアルのハルになる。

## 建物 (Buildings/)

- `BuildingMeshBuilder` が入口。`BuildingMeshBuilder.Generate` は**まず status で分岐し(`STATUS_CONSTRUCTING` → `ConstructingBuildingMeshBuilder`。model_type 分岐より先)**、次にマスターデータの `model_type` で分岐する: `MODEL_MODULAR_*`→`ModularBuildingMeshBuilder`、`MODEL_PREBUILT`→`PreBuiltBuildingMeshBuilder`、`MODEL_PORT`→`PortBuildingMeshBuilder`、該当なし→白い箱。`ModularBuildingMeshBuilder` は Body(`ModularBodyBuilder` 等) と Roof(`FlatRoofBuilder` / `GableRoofBuilder` / `HouseRoofBuilder`) を組み合わせる。
- 戻り値 `BuildingMeshResult` は描画用 `Mesh` + `Materials` + コライダー用 `ColliderMesh`。
- 遠景(Far)の白い箱も同じ `BuildingMeshBuilder` で生成され、マテリアルだけ `GetDefaultBuildingMaterial()` になる。
- **パーツのスケール規則は「Y方向のみ階高スケール、XZは実寸」**: 壁・角・prebuiltのbase/midは高さが階高(`height_per_floor`)になるようY方向のみスケールされ、幅・奥行きは著作サイズ(メートル実寸)のまま使われる。**等方スケールではない**。パーツはBlenderで実寸(1ユニット=1m)でモデリングすること。XZ実寸が0.3m未満のパーツは旧規格(単位スケール)とみなして従来の等方スケールで互換描画される(`ModularBodyBuilder.LEGACY_UNIT_PART_MAX_SIZE`、警告ログ付き)。
- **top系パーツはY方向にもスケールしない**: (a) prebuiltの `top-prebuilt` は完全実寸で最上部に配置される。(b) モジュラーのtop階(top-w1が実在する場合)は最上階がパーツの著作高さのまま描画され、屋根・塔屋・コライダーのYは `BodyMeshData.BuildingHeight`((floors-1)×階高+top実高)に追従する。`floors × floorHeight` をハードコードしないこと。
- **prebuilt(`PreBuiltBuildingMeshBuilder`)の積み上げ規則**: base-prebuilt / mid-prebuilt / top-prebuilt を下から積む。baseとtopは論理階数(floors)のうち各1階分を消費し、midは残り階数分繰り返す。**各パーツのY位置は「実際に描画したパーツの累積高さ」で決まる**ため、base/midファイルが無い建物ではtopが接地する(完成済みモデルをtop-prebuilt 1つで表現できる)。topの実寸が大きく論理高さをはみ出すのは許容(コライダーは描画実高に合わせる)。積み上げの天面を塞ぐパネルは生成しない(上から見える面はパーツのモデル側で閉じる)。フェンス(fence-w/c)・タイル(tile)・地面パネル(floor)は `ModularBodyBuilder.GenerateSiteDecorations`(モジュラーと共通の敷地装飾処理)で土地区画に配置される(フェンス・タイルは該当パーツがある場合のみ。**地面パネルはマスターの `draw_floor=yes` の建物だけ描画**し、マテリアルは `BuildingMeshUtils.ResolveFloorMaterialFromMaster`→`SelectFloorMaterialWithFallback`で floor パーツを a{variant}→b{bd_type}→common で解決する=無ければ描画なし・デフォルトフォールバックなし。呼び出し側で解決した `Material floorMaterial` を `GenerateSiteDecorations` に渡す方式で、モジュラー/prebuilt 共通。prebuilt は地形接地なので isDynamic で地形追従)。FBXの読み込みは `BuildingMeshUtils.CombineModuleMeshes`(全MeshFilter/SkinnedMeshRendererをノードTransform込みで結合)を使うため複数オブジェクト構成のFBXも欠けない。
- **バリアントの定義とプレフィックス決定 (`agent_model_config.yaml`)**: `a{variant}` 形式のプレハブフォルダを prefix として使用するためには、 `city-genesis/unity/Assets/Resources/Config/agent_model_config.yaml` に該当するバリアントIDが定義されている必要がある。定義がない場合は `b{bd_type}` にフォールバックしてしまい、プレハブが存在していても見つからずに白い箱になる。新しく建物モデル（`a{variant}`フォルダ）を追加・変更した際は、必ず `genesis-core/tools/generate_agent_model_config.py` を実行して設定ファイルを再生成・同期すること。
- **傾斜地対応(接地高さとコンクリート基盤)**: `BuildingMeshBuilder.IsApplicableModelType`(MODEL_MODULAR_FLAT/HOUSE/GABLE)の建物は、土地区画の基準標高 `parcel.altitude`(=接道の路面高さ。配置は BuildingManager 側)の水平面に接地し、建物底面フットプリントをY方向に押し出したコンクリート基盤(`BuildingMeshBuilder.GenerateFoundationMesh`、マテリアルは共通の `Resources/COMMON_CONCRETE`→`GetCommonConcreteMaterial`)で谷側の隙間を埋める。**土地区画ポリゴンは押し出さない**。GROUND/PREBUILT/PORT は対象外(中心位置の地形標高に直接接地・基盤なし)。工事中(`ConstructingBuildingMeshBuilder`)は同形状の基盤を土マテリアルで生成する。仕様の正は `genesis-docs/docs/geometries/F_建物メッシュの生成.md §2.4 / §9.2`。
- `BuildingMeshBuilder` は static 幾何ユーティリティ(`GenerateSimpleBuilding` / `GenerateColliderMesh` / `GenerateHorizontalPolygonMesh` / `MergeSubMeshes` / `TriangulateOrFan`(耳切り+ファンフォールバック) / `SubdivideTrianglesByEdgeLength`(長辺を中点分割して内部頂点を作る細分化。中点は無向`EdgeKey`で溶接=T字接合の隙間なし。起伏地形へ追従する面メッシュ用) / `EdgeKey` / `GenerateRoofprop` / `GenerateFoundationMesh`)も提供する。各ビルダーから再利用する。地面パネルの地形追従(`ModularBodyBuilder.GenerateTerrainConformingFloorMesh`)と工事中の土マウンド(`ConstructingBuildingMeshBuilder.GenerateDirtMoundMesh`)がこの細分化を共有する。
- **工事中の建物 (`ConstructingBuildingMeshBuilder.cs`, STATUS_CONSTRUCTING)**: model_type に関わらず「土マウンド(敷地全体を覆う厚み+Perlin凹凸の土。細分化は長辺の中点分割) + 足場(scaffold-wall/scaffold-corner。**通常建物の壁・角と全く同じ配置ロジック**=`GenerateWallSegments`/`GenerateCornerSegment`をフットプリント上でそのまま使う。角ヒンジ=フットプリント頂点。オフセットなし。フェンスと違い各階をYのみ階高スケールし進捗分の階数生成) + クレーン(crane-base/mid/top) + ceil天面(施工済み階を footprint パネルで塞ぐ)」を生成する。進捗は `construction_progress`(%)→`builtFloors=ceil(進捗×floors)`(最低1)。工事系パーツは `BuildingMeshUtils.GetPartWithCommonFallback` で **a{variant}→b{bd_type}→`Models/Buildings/common/`(標準パーツ。suffixフォルダなし)** の順に解決され、無ければスキップされる(オプショナル)。土マテリアルは `BuildingMeshBuilder.GetConstructionDirtMaterial()`(`Resources/ConstructionDirt`→`ConstructionMaterial`→デフォルトのフォールバック)。足場の生成は `ModularBodyBuilder.GenerateScaffoldSegments`(通常建物の壁・角ループの流用)、クレーン等の積み上げモジュールは `BuildingMeshUtils.AddStackedModule`(旧 PreBuilt の `AddPrebuiltModule` を共通化したもの)。**クレーンは `AddStackedModule(alignByOrigin: true)` で「モデル原点(y=0)=接続点」基準で積む**(prebuilt の bounds.min.y 基準と異なり、原点より下に張り出す top でも段間の隙間が出ない)。敷地ポリゴン+centerPointWorld の解決は `BuildingMeshUtils.ResolveParcelContext`(Modular/PreBuilt と共通。**返り値の区画ポリゴンは center_point 基準のローカルXZ座標**=footprint と同じ規約。ワールド座標のまま扱うと敷地装飾が建物から離れた位置に生成される)。MODEL_MODULAR_GROUND / MODEL_PORT は土のみ(足場・クレーン・ceilなし)。仕様の正は `genesis-docs/docs/geometries/F_建物メッシュの生成.md §19`。
- **港湾の岸壁 (`PortBuildingMeshBuilder.cs`, MODEL_PORT)**: 海岸土地区画(PARCEL_SHORE)の港湾(BUILDING_PORT)用。`SHAPE_FULL` の底面ポリゴン(=海岸線でクリップ済みの区画形状)を海岸線でカットし、厚みのある板(岸壁スラブ)を上下に押し出してプロシージャル生成する。モジュラーの壁・角は使わない。`visual_variant1/2` で左右端の突き出し量(海岸線↔区画外周の弦の補間)、`visual_variant3` で高さ(0=海面0m, 1=10m)を決める。上面=ceil、側面/下面=floor マテリアル。下面は各頂点の地形(海底)高さに合わせる(`HeightmapTextureManager.GetTerrainHeight`)。塔屋は岸壁中央に海側境界の向きで配置。**どの辺が海岸線に接するかの判定には `CoastlineStore`(`DataLoaders/`)を参照する**(セッション開始時に `TerrainManager.FetchCoastline` がロード済み)。仕様の正は `genesis-docs/docs/geometries/F_建物メッシュの生成.md §18`。

## 超遠景の陸地メッシュ (UltraFarLandMeshBuilder.cs)

- 超遠景(UltraFar)の「陸地シルエット」を、海岸線ポリゴン(`CoastlineStore`)から直接メッシュ化する。`TerrainManager.BuildUltraFarTerrain` から呼ばれる(従来の Unity Terrain ハイトマップ方式を置き換えたもの)。
- 陸地AABBを `gridStep` 間隔のグリッドに切り、各セルを「辺をたどり陸側コーナー+海岸線交差点を拾う」マーチングスクエア的クリップで陸地ポリゴンに切り出す。交差点は `CoastlineStore.SignedDistance` の符号反転位置の線形補間で求めるため、海岸線はグリッド解像度より細かく滑らかに出る。海(陸地外)はメッシュを作らない(海面板が担う)。
- 頂点Yは渡された `sampleHeight`(far_map 由来)で粗い起伏を付け、`heightOffset` 持ち上げる。各三角形は `isAcquired`(セル開放判定)で開放/未開放の2サブメッシュに振り分ける(未開放セルの暗色表示用。呼び出し側がサブメッシュ別マテリアルで通常/暗めティントに塗り分ける)。
- **スムース法線**: `MeshData.normals` に位置溶接済みのスムース法線を返す(`BuildSmoothNormals`)。頂点はグリッドセルごとに個別追加され境界に重複頂点ができるため、`Mesh.RecalculateNormals` だと境界でファセット状の陰影になる。面積重み付き面法線を同一ワールドXZ位置(量子化キー)へ集約して正規化することで、頂点を溶接せず法線値だけをセルをまたいで共有する。呼び出し側(`TerrainManager.BuildUltraFarTerrain`)は `md.normals` を `Mesh.SetNormals` する。
- **Mesh の組み立てはメインスレッド専用**。本ビルダーは純粋なデータ(`MeshData`: 頂点 + スムース法線 + サブメッシュ別三角形)のみ返すので、重い符号付き距離計算・法線集約を含む呼び出しは Task.Run でバックグラウンド実行してよい(`CoastlineStore` と渡すデリゲートがビルド中に不変であること)。

## 土地区画 (Percels/DefaultParcel.cs)

- ポリゴンを重心原点のローカル座標でファン分割した上面+押し出し側面のメッシュ。`topVertexLocalY`(頂点ごとの地形高さオフセット)を渡すと上面が地形の傾斜に沿う。
- 凹ポリゴンはファン分割では破綻し得る点に注意。

## 注意点

- `Test/` はメッシュ生成の確認用シーン専用(MeshTestController 等)。ゲーム本編からは参照されない。
- RoadPath のマテリアルとミラーメッシュは**静的キャッシュ**(`MaterialCache` / `MirroredMeshCache`)でマテリアル名・メッシュ名単位に管理される。同じバリアントの道路パーツはキャッシュ済みインスタンスを共有するため、再生成時のメモリリークやドローコール増加は起きない。Zファイティング回避の `renderQueue + 1` もキャッシュ生成時に一度だけ適用される。
- 道路系の冗長ログは `UnityLog.If(LocalConstants.UNITY_LOG_ROAD, ...)` を使うこと(常時 `Debug.Log` を呼ばない)。
