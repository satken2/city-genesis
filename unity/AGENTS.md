あなたは都市シミュレーションゲームを開発するプロジェクトのAI開発エージェントです。以下のルールを守って開発してください。

# レポジトリ概要

このリポジトリは、シミュレーションゲーム、"City Genesis"のUnity部分のグラフィックプログラムである。

## プログラムの構造

バックエンド(FastAPI)とはHTTPで連携する。
このUnityプログラムとバックエンドはともにユーザーのPCで稼働し、localhost内部でしか通信しない。

```
- Assets/Scripts
  - API                 ... バックエンドと通信するクライアント(ApiClient.cs等)とスキーマ。GeneratedModels.csはバックエンドのレスポンススキーマ定義(endpoints.yaml)から`tools/generate_models.py`で自動生成される(生成物の冒頭に記載)。HTTP通信は必ずApiClientを経由すること(単体取得=`Get`/大きな一括取得=`GetBulk`。例外は地形バイナリ等の非JSON応答のみ)。`GetBulk`はAcceptヘッダでMessagePackを要求し、対応エンドポイント(bbox一括取得系)ではバイナリで受信して`MsgPackDecoder.cs`でデコードする(Content-Typeで自動判別。呼び出し側は形式を意識しない)。
  - DataLoaders         ... バックエンドから取得したデータを元にUnity空間にオブジェクトを作成するプログラム。実際の描画には`MeshGenerators`のプログラムが使われる。(詳細はDataLoaders/AGENTS.md)
  - MeshGenerators      ... 建物や道路の具体的なメッシュ生成アルゴリズム。バリアント→タイプ→デフォルトの順にフォールバックする。(詳細はMeshGenerators/AGENTS.md)
  - Editors             ... 3D空間内で道路・建物・土地区画・地区を編集する際の状態管理や確定後のバックエンド送信。(詳細はEditors/AGENTS.md)
  - InputHandlers       ... ユーザーのクリックやキー入力イベントの処理。状況によってイベントハンドラを切り替えられる。(詳細はInputHandlers/AGENTS.md)
  - VisualHandlers      ... 3D空間内の建物・道路等の「見せ方」(表示/非表示/ハイライト等)の一括管理。(詳細はVisualHandlers/AGENTS.md)
  - IPC                 ... Tauri製UIとのMemoryMappedFile通信。(詳細はIPC/AGENTS.md)
  - Utils               ... 各種ユーティリティ。新しい汎用処理を書く前に必ず確認。(詳細はUtils/AGENTS.md)
  - UI                  ... 3D側のUI部品(ツールチップ、モード表示、汎用ラジアルメニュー`RadialMenu/RadialMenuController`=トリガーキー押下でカーソル位置から項目が円形展開しキー離しでトグル、汎用カーソル追従HUD`CursorStatusHud`=現在設定のアイコン列+テキストを常時表示(アイコンは`tint`指定で任意色に塗れる=地区編集の色チップ。テキストは`UiFontProvider`の多言語フォントで日本語も表示可)、多言語フォント供給`UiFontProvider`=OSの多言語フォントを優先しキャッシュして返す(RadialMenu・CursorStatusHud・地区名ラベルが共用)、立体方位磁針`CompassPanel`+メッシュ生成`CompassNeedleMesh`=専用カメラでレンダーテクスチャに描いてRawImageへ貼る方式。実体はワールド上空(0,100000,0)の専用レイヤー`Compass`(6)に置き本編カメラから隔離する。表示物は北を指す赤い四角錐と「N」の文字だけで、四角錐が本編カメラのヨーの逆に回り俯角も追従する(「N」は先端に付いて回りつつ常にカメラ正面を向く)。カメラ角が変わったフレームだけ描き直す)。※ゲームのメインUIはこのレポジトリではなくcity-genesis/tauri(Tauri)にある
  - Grid                ... グリッドオーバーレイ表示
  - GameStateManager.cs ... グローバルなゲーム状態の管理(シングルトン)。状態変化イベントの購読。
  - MasterDataStore.cs  ... バックエンドから取得したマスターデータの保持。道路幅・車線数などの描画パラメータはここを正とする。
  - AppRoot.cs          ... アプリ起動処理。
  - Vehicles            ... 描画専用の交通演出。一般車(`VehicleManager`)・公共交通の編成(`TransitVehicleManager`)・歩行者(`PedestrianManager`。GameObjectなしのinstanced描画)・空と海の環境演出(`AmbientVehicleManager`)。経済へ書き戻さない。(詳細はVehicles/AGENTS.md)
  - Weather             ... Unity専用の天気・環境光。AppRootがWeatherControllerを生成する。既存session:reload/force_reloadでcity.epochを読み、増分がある場合だけ抽選する(編集リロードでは不変)。WeatherPresetの重み付き11種類、Resources/Weather/Atmosphere.shader、太陽・雲影Cookie・霧・空だけの反射プローブを管理する。F9で撮影用キーボードパネル、F6/F7で選択固定、F10で固定切替、F11で自動再抽選。経済・マスター・セーブには書き込まない。詳細は公開U_天気と環境光.md、検証はTests/Weather/run-unity.ps1。
  - MainMenu.cs         ... メインメニューのボタンイベントなどを管理するプログラム。
  - Utils.cs            ... ApplyDepthOffsetShader / DestroyObjectAfterFrames / CalculateAverageWidth / GetCameraHorizontalForward / IsInHorizontalView(視野優先処理の水平視野判定) など各マネージャー間で共有する汎用処理。新しい汎用処理を書く前に必ず確認。
  - Constants.cs / LocalConstants.cs ... 定数定義。
  - CameraCellChecker.cs... カメラの視野中心がどのセルにいるのかを毎フレーム判定し、変化したらCellDataLoaderに通知する(ロードの起点)。`ResetViewState`で視点検出を未初期化へ戻すと、カメラが静止していても次の判定で必ず通知が走る(セッション切り替えで`CellDataLoader.ClearAllManagerData`が現在セルを未設定に戻した後、カメラを動かさなくてもロードが始まるための入口)。
  - CameraController.cs ... カメラの移動などを担うプログラム。**セッションのカメラ状態(バックエンドの`City.camera`={x,y,z,yaw,pitch})の同期も担う**: ゲーム開始(`game:start`/Menu→Game遷移)で`ApplySessionCamera`が`GET /api/v1/city`を読んで`SetView`で視点を復元し(null=原点の既定視点)、以後は`LateUpdate`でカメラが1m/0.5°以上動いて1秒静止するたびに`PATCH /api/v1/city`へ送る。`session:reset`/`game:end`では`SuspendCameraSync`で送信と適用を止める(新しい都市の準備中に前の視点で保存値を上書きしないため)。
  - ZoneEditContext.cs  ... 地区編集の状態保持。
```

# AGENTS.mdの運用ルール

各主要ディレクトリに、役割・既存ユーティリティ・注意点を書いた`AGENTS.md`がある(`CLAUDE.md`は`AGENTS.md`へのシンボリックリンク)。**作業対象ディレクトリのAGENTS.mdを必ず先に読むこと。**
開発によってAGENTS.mdの記載内容に影響が出た場合や、記載と実装の矛盾を発見した場合は、必ず該当するAGENTS.mdを更新すること。

# 水面の検証

水面(`Assets/Shaders/Water.hlsl`)のフォグはピクセルのワールド位置から計算する。巨大な海面の頂点フォグを補間すると、カメラ回転で近景全体が白くなる。検証は `Tests/WaterFog/run-unity.ps1`（独立環境 `Library/WaterFogQA`）。光沢・反射の明るさを制限してフォグの不具合を隠さないこと。

# 交差点の検証

交差点の回帰検証は `Tests/Intersections` にある。`run.ps1` は実アセンブリの幾何計算、`run-unity.ps1` は独立した検証用Unityで実FBXの接合端を検査する。生成物は `Temp/IntersectionUnityQA` に置き、本編シーン・マスター・モデルを書き換えない。詳細は同ディレクトリのREADMEを参照する。

# 編集してはいけないファイル

`GeneratedModels.cs`はプログラムによって自動生成されるスキーマファイルなので、編集しないように。
`Constants.cs`はバックエンドのソースコードから自動生成されるフロント・バック共通定数なので、編集しないように。
これらのファイルを編集する必要がある場合は、ユーザーに相談すること。

生成元と生成器はどちらも**非公開のコアシミュレータ側**にある(`Constants.cs`の正は`genesis-core/consts.py`、
生成器は`genesis-core/tools/sync_consts.py`)。このリポジトリ単体では再生成できないので、
定数の追加・値変更が必要になったら自分で書き足さずユーザーに依頼すること。

# モデルアセットの構成(FBXとミラープレハブ)

モノレール501/502・手動配置車両308/309の実FBX検証は`Tests/Monorail/run.ps1`。独立プロジェクトでUnityのsharedMesh座標・ホームの右側・桁とキャップの接合・車両の接地と連結ピッチ・自動プレハブ生成を確認する。制作仕様は公開N/M文書、著作ジョブはgenesis-galleryのbuild_monorail.py。

- `Assets/Models` ... FBXソース(Buildings / Paths / Props)。
- `Assets/Resources/Models` ... 上記と同一相対パスのラッパープレハブ。ランタイム(`Utils/ModelManager`)が `"Models/Buildings/{型}/{variant}/{パーツ}"` の文字列パスで `Resources.Load` するために必要。
- **このミラーは手動で維持しない。** `Assets/Models` にFBXを追加・移動すると、エディタ拡張(`Assets/Editor/ModelPrefabGenerator.cs`)が対応プレハブを自動生成する。一括同期はメニュー `Tools/Model Prefabs/Sync All`、FBXの削除・移動で孤児化したプレハブは `Tools/Model Prefabs/Delete Orphans` で削除する(自動削除はしない)。
- 自動生成されるプレハブはFBXをオーバーライドなしで包む純粋なラッパー。プレハブ側にコンポーネント追加やマテリアル差し替え等の手動編集を行った場合、同期ツールはそのプレハブを上書きせずスキップして警告する。
- **`Assets/Models` 配下のモデルは Read/Write Enabled(`ModelImporter.isReadable`)が必須。** ランタイムのメッシュ生成はパーツの頂点を読み出して配置行列を焼き込むため(`ModularBodyBuilder.BuildWallRun` / `BuildingMeshUtils.CombineModuleMeshes` / Dreamteck SplineMesh)、無効だと頂点が取れずエラーログだけ出して**そのパーツが無言で消える**(屋根・地面パネルはマテリアルしか使わないので残り、「壁だけ描画されない」という部分欠けになる)。Unityの既定値は無効なので、エディタ拡張(`Assets/Editor/ModelImportSettings.cs`)がインポート時に一律有効化する。既存アセットの一括修正はメニュー `Tools/Models/Enable Read-Write (Assets/Models)`、確認だけなら `Tools/Models/List Non-Readable Models`。
- **接写用モデルの規約 `{パーツ}_close.fbx`**: 無印 `{パーツ}.fbx` が Normal(制作の基準)で、同じフォルダに任意で `{パーツ}_close.fbx` を置くと描画LODが Close のときだけ優先して使われる(建物: `BuildingMeshUtils.GetPart*` 経由の全パーツ、パス: `PathDrawContext.ResolvePrefab` を通る `CreateSplineMesh` 配置パーツのみ。送電鉄塔はInstantiate配置でもClose対応。他のtree/pillar/pole/設備等は無印)。**原点と配置寸法は無印と一致させ、材質は既存名で共有すること**。建物の壁(base/mid/top-w)と玄関だけはBlender -Yへ最大1.5m追加で張り出せるが、幅X・高さZ・背面+Yを維持する。他のパーツは全bbox一致(生成側は配置計算を変えずに差し替える)。無印が無い `_close` は使われない(存在判定は無印名で行う)。Far はコード生成(建物=フットプリント押し出しの箱、パス=帯、送電線=簡略鉄塔と電線)なのでモデル不要。ミラープレハブは無印と同じ仕組みで自動生成される。

# アーキテクチャの要点(まずこれを把握する)

## データロードとLODの全体像

`LodConfig` が最大81の常駐セルを選ぶ。旧far/near/dynamic集合はすべて同じ常駐集合であり、地形の正とは独立している。地形→既存物の追従→区画→建物・道路などの順で取得する。地形APIは `/terrain/surface` のfloat32で、一時TerrainDataへ完成させてから差し替える。

描画LODは建物・プロップの投影サイズ、道路の距離で決まる。生成の共通フレーム予算は `RenderWorkScheduler`、生成済みGOの退避は `RenderObjectCache`。必ずschedulerのStart/Stopを対で使用し、取消し時のiteratorとメッシュを解放する。詳細は `Assets/Scripts/DataLoaders/AGENTS.md` と `../docs/docs/geometries/E_LOD処理.md`。

F8は描画数、生成予算、フレームp95/p99、キャッシュ推定量を表示する。検証は `Tests/Lod/run.ps1`、追加ソースを含むビルドは `Tests/BuildRuntime.ps1`。Unity生成csprojが古いときも新しいソースを漏らさない。

## 座標系とセルインデックス

- Unityワールドはマップ中心が原点で、**Unityセルインデックスは負値を取る**。バックエンドのセルは0-based(左下原点)。変換は必ず `TerrainManager.UnityCellToBackendCell` / `BackendCellToUnityCells` を使うこと。
- Unityワールド座標はバックエンドと同じ固定座標を使う。カメラ距離に応じた原点シフトは行わない。Transformだけを移動するとセル判定・高さ参照・保存座標が不整合になる。

## 地形高さの取得

`Utils.GetGroundHeight` を使う(別名のラッパーは置かない。同じ処理に複数の名前があると読み手が別物だと誤認する)。ロード済みTerrainを正とし、未ロードでは現在標高の全域overviewを参照する。同一XZだけのフレームキャッシュで、タイル差し替え時は `Utils.InvalidateHeightCache()` が必要。全域CPU高さ配列は保持しない。

## オブジェクトの命名規則とタグ(変更禁止級の規約)

- GameObject名にIDを埋め込む: `Building_{agentId}` / `Path_{pathId}` / `Parcel_{parcelId}` / `Node_{nodeId}`。遠景の簡易オブジェクトも同じ命名で、`ObjectIdInfo`コンポーネントの `isFarObject` フィールドで区別する。
- 各GameObjectには `ObjectIdInfo` コンポーネントが付与され、`id` / `status` / `isFarObject` / `role` フィールドでメタデータを管理する。**名前の文字列パースは行わず、常に `GetComponent<ObjectIdInfo>()` でデータを取得すること。**
- マウス入力のタイプ判定は**タグ**で行う: `BuildingObject` / `ParcelObject` / `PathObject` / `PathNode` / `ZoneObject` / `SeaObject`(地形はTerrainコンポーネントで判定するが、海面は `Terrain` コンポーネントを持たないため `SeaObject` タグで代用し `InputObjectType.TERRAIN` に振り分ける)。コライダーとタグがないとクリックできない。

## モード系は2系統ある

ドライブは`Vehicles/DriveMode`による描画専用の乗車操作で、InputMode/VisualModeの新規定数を持たない。選択UI表示中は通常のオブジェクト入力とカメラ保存を止め、乗車中はカメラを車両へ追従する。`CameraCellChecker`のロード中心も車両位置へ切り替える。`CameraController.SetView`はドラッグと慣性を破棄してから視点を置く。仕様は公開`O_車両の走行描画.md`のドライブモード節。

- **入力モード**(`InputSystemManager` / `InputHandlers/`): クリック時に何が起きるか。
- **ビジュアルモード**(`VisualModeHandler` / `VisualHandlers/`): どう見えるか(非表示/ハイライト/データ可視化)。
- 編集モードに入るときは通常この2つをセットで切り替える(トリガーはUI(Tauri)からのIPCイベント)。

## ログ

冗長ログは `UnityLog.If(LocalConstants.UNITY_LOG_*, ...)` を使う。ホットパスで無条件に `Debug.Log` を呼ばないこと(ビルドでもコストがかかり、特にエディタではスタックトレース取得が重い)。用途別フラグ: 道路系は `UNITY_LOG_ROAD`、建物系は `UNITY_LOG_BUILDING`、地形系は `UNITY_LOG_TERRAIN`、ロード進行(CellDataLoader/各Managerのセル分類・リロード開始等)は `UNITY_LOG_LOADER`、`[TIMING]` 計測ログは `UNITY_LOG_TIMING`。文字列補間が重い場合は `Func<object>` オーバーロード(`() => $"..."`)を使い、無効時に補間自体を回避する。`LogWarning` / `LogError` は無条件で残してよい。

# パフォーマンス設計 (全件走査の回避)

開発時は、オブジェクト数が増えた際にもフレームレートやロード時間を維持するため、以下の全件走査回避パターンを厳守すること。

- **接続パスの逆引きインデックス (`PathManager.pathsByNodeIndex`)**:
  交差点を1つ更新するたびに全パスキャッシュをループ走査するのを避けるため、ノードIDから接続パスIDの `HashSet` を引くインデックスを維持している。パスのキャッシュ更新・削除時は必ず `SavePathToCache` / `RemovePathFromCache` を用いてこのインデックスを同期すること。
- **地形更新時のパス検索最適化 (`PathManager.OnTerrainTileReadyHandler`)**:
  地形タイル（セル）が準備できた際に、交差するパスを再生成する処理では、全パスのセグメント走査を避け、すでにセル通過情報を持っている `pathIdsByCell` から対象セルおよび隣接セルに含まれるパスIDのみを抽出して交差判定を行う。
- **静止時のレイキャスト/地区判定のスキップ (`InputHandler.Update`)**:
  毎フレーム `RaycastAll` や地区ポリゴン判定 `GetZoneIdAtPoint` を実行すると重いため、Rayの変化（マウスやカメラの移動）と入力イベントがないフレームは処理を即座にスキップする。
- **地区判定の限定とBBOXによる高速化 (`ZoneManager.GetZoneIdAtPoint`)**:
  通常プレイ中の無駄な毎フレーム地区内外判定を排除するため、地区判定は地区編集モード中（あるいは地形モードが `MODE_ZONE_EDIT` のとき）のみ実行する。また、多角形内外判定（`IsPointInPolygon`）の前に、`zoneBboxCache` を用いた AABB (BBOX) 判定による早期リターンフィルタリングを実行する。
- **建物のセル逆引きインデックス (`BuildingManager.buildingIdsByCell`)**:
  セルから建物IDの `HashSet` を引くインデックスにより、`UnloadCell` や `SyncBuildingsInCells` で全建物キャッシュの線形走査を避ける。建物の登録・削除時は必ず `RegisterBuildingCell` / `UnregisterBuildingCell` を通してこのインデックスを同期すること。
- **区画のセル逆引きインデックス (`ParcelManager.parcelIdsByCell`)**:
  マウス位置から区画を解決する `ParcelManager.GetParcelIdAtPoint`(起伏地形で区画コライダーが地形に埋まった場合の InputHandler のフォールバック)が、マウス移動フレームごとに全区画のポリゴン内外判定を線形走査しないための逆引きインデックス。区画の登録は `RegisterParcelDataForLines`(内部で `RegisterParcelSpatialIndex`)、削除は `RemoveParcel` / `ClearAllCells` を通して同期すること。
- **接道街路の逆引きインデックス (`ParcelManager.parcelIdsByFrontPath`)** と **区画の逆引きインデックス (`BuildingManager.buildingIdsByParcel`)**:
  「この道路に面している区画」「この区画の上に建っている建物」を全件走査なしで解決するための逆引きインデックス。前者は `RegisterParcelSpatialIndex` / `UnregisterParcelSpatialIndex` / `ClearAllCells`、後者は `RegisterBuildingDataToCache`(parcel_id変更時は旧マッピング除去) / `SyncBuildingsInCells` / `ClearAllCells` を通して同期すること。
- **カメラ非依存のオーバーレイメッシュは毎フレーム再構築しない (`ZoneManager.hoverFillRenderer`)**:
  ワールド固定のオーバーレイジオメトリはカメラに依存しない静的メッシュなので、毎フレーム `BeginFrame`〜`EndFrame` で作り直すと、カメラ静止・入力なしのアイドル時でも無駄なCPU/GPU(頂点構築＋メッシュ再アップロード)を消費する。`OverlayMeshRenderer` は次の `BeginFrame` まで前フレームのメッシュを保持するので、内容が変わったフレーム(地区ホバーフィルなら対象地区が変わったフレーム)だけ再構築し、それ以外は既存メッシュをそのまま描画すること。カメラに追従してビルボードやスクリーン幅換算が必要なオーバーレイ(パーセル枠線等)はこの限りではなく、カメラ移動閾値でガードする。

# 重要な考え方

## Y座標の考え方

バックエンドのデータから受け取られるノードとかパスのＹ座標っていうのは、これは全て地面からの相対高さです。
地面がぐちゃぐちゃのボコボコなら、その上に引かれる道路もぐちゃぐちゃボコボコになりますが、データ上Y座標が0ならば、それは高さ0、つまり完全に地面に沿っている道路です。 その道路のどこの部分をとっても、高さは0とみなしてください。ポリゴンの粗さとか、計算誤差により表示上はもしかしたら地面から浮いているかもしれない。でもデータ上は、その道路は地面に完全に張り付いていると考えます。 Y座標0の道路を、3D上は若干浮いてるからと言って高架道路とみなすようなことは、してはいけない。  
つまり地形に影響されて道路が曲がったりするのは、完全に3Dの表示の話であって、内部の様々な計算上は、道路というのは仮想的にすべて完全に平らな平面上にあるとみなして処理すること。

## 地形プロップの描画

プロップは既存farCells全体にロードし、セル内を整数分割してGPUインスタンシングで描画する。Near/Dynamicは個体別DrawLod、FarセルはFar以下、UltraFarは非表示。開発による除外は保存regionを変更せず、同梱された障害物情報で表示可否を更新する。共通LoadBatchと地形追従に組み込まれている。詳細はDataLoaders/AGENTS.mdと公開M_地形プロップ.md。


単独起動時のAPI既定URLは `http://127.0.0.1:5000/api/v1`。IPv4だけで待受ける開発サーバーに対してlocalhostのIPv6接続からのフォールバック待ちを避ける。明示的な `--api-base-url` は指定値を優先する。

建物直接配置の初期寸法・固定可否は`MasterDataStore.AgentMasterItem`の`default_width`/`default_height`/`resizable`で受け取り、Ctrl配置メニューとパネルで共有する。配置前から数値キーで変更可能。詳細はEditors/AGENTS.mdと公開Unity入力制御.md。

- カメラの可動範囲は詳細地形と任意の背景地形の外接範囲。背景はTerrainManager.BackgroundBoundsで取得する。背景追加によって都市の建設可能範囲は変えない。

## 初期画面の準備完了

`GameStateManager.IsGameStarted()` はデータ取得の許可、`IsWorldVisible` はユーザーへの表示許可として分離する。`LoadingOverlay` は後者まで黒を維持し、`CameraController` と `InputUtil` は表示前の操作を抑制する。ロード中もLOD判定に必要な `Camera.main` を利用できるよう、カメラを有効に保つ。

`CellDataLoader.IsInitialSceneReady` は初回地形処理だけでなく、後続の区画・建物・道路・地区・資源・プロップ、敷地装飾・道路区間・コライダーの描画準備を待つ。`RenderWorkScheduler.Start` の任意 `hasPendingWork` は常駐ワーカーの未処理判定で、省略した有限コルーチンは通信待ちを含めて全期間待機対象になる。常駐ワーカーを追加するときは、無限ループ自身を完了条件にしないようこの判定を指定する。建物の上限により意図的に保留している視野外の詳細と、元の表示を維持する遠景結合処理は待ち続けない。

保存カメラの復元と生成完了を連続3フレーム確認して `game:loaded` を送る。初期ロードとは初期視点の常駐セルと全景の描画準備であり、都市全域の詳細モデルを常駐させる意味ではない。開始後のカメラ移動には通常のLODストリーミングを使う。

初期表示ゲートと常駐ワーカーの待機判定の回帰確認は `Tests/InitialLoading/run.ps1`。本編のUnity生成レスポンスファイルで別DLLをコンパイルし、独立したUnityで世代違いの表示解除とロード待機を検証する。
