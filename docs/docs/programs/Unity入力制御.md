# Unity入力制御

地下表示(`terrain=transparent`)では、レイが地下専用のパスコライダーに当たれば、地形・海面・地上の建物より優先してパスへ配送する。複数の地下パスに当たる場合はレイ原点から最も近いパスを選ぶ。非表示レイヤー・個別フィルタ・計画中表示で隠れたモデルはコライダーも無効になる。

通常クリックは従来どおりパス詳細を開く。ブルドーザも同じ判定で地下パスを拾い、その手前の地上プロップを誤って削除しない。通常ブルドーザの削除対象は既存の計画中ステータスだけで、稼働中パスの撤去は詳細パネルの取り壊し操作を使う。敷設などパスの入力モードが `MODE_NONE` の場合は地下モデルを貫通し、地形との交点を使う。地形コライダー自体は無効化しないため、カメラ・敷設・地形編集の基準面は変わらない。見た目は [Unity表示モード](Unity表示モード.md#地下表示) を参照。

## 1. 概要

Unityプログラムにおけるユーザーのマウス・キーボード入力は、`InputHandler` が中央管理している。
毎フレーム、カメラからRayを飛ばしてマウス直下のオブジェクトを検出し、オブジェクトの種類と現在の入力モードに応じて適切なハンドラにイベントをディスパッチする。
カメラのドラッグ移動は `CameraController` が独自に処理し、`InputHandler` との競合を回避する仕組みを持つ。

線分への射影・距離計算は `PolylineUtils` が担う。道路・停留所の水平吸着は `ClosestPointOnSegmentXZ(point, start, end)` でXZだけから位置を決め、Yは端点間で補間する。保存基準の標高や地形追従は呼び出し元が別途解決する。画面上のマーカー・線分のヒット判定はVector2の `DistanceToSegmentSq`、高さを含む最近接点が必要な処理はVector3の `ClosestPointOnSegment` を使う。引数はすべて点・始点・終点の順で、座標系を混在させない。射影する座標系で線分長の二乗が `1e-8` 以下の場合は始点として扱う（ワールドでは0.1mm、画面では0.0001px以下）。入力ハンドラ独自の幾何関数は持たない。

バックエンドの接続確認が失敗している間は `ApiClient.BackendConnected` による入力ゲートでクリック・ホバー・ドラッグを止める。モードや都市表示は保持する。進行中のドラッグは打ち切り、復旧後に遅れて確定しない。HTTP更新の共通窓口でも送信を拒否し、操作を保留・自動再送しない。

## 2. オブジェクトタイプと入力モード

### 2.1. オブジェクトタイプ

Rayが命中したオブジェクトはTagまたはコンポーネントの有無によって6種類に分類される。

| 定数 | 値 | 判定方法 |
|------|------|----------|
| `BUILDING` | 1 | Tag `BuildingObject` |
| `PARCEL` | 2 | Tag `ParcelObject` |
| `PATH` | 3 | Tag `PathObject` |
| `NODE` | 4 | Tag `PathNode` |
| `TERRAIN` | 5 | Tag `SeaObject`、`Terrain` コンポーネントの有無、都市外の背景地形のチャンク(`TerrainManager.IsBackgroundTerrain`)、または埋立地の岸壁(`ReclaimedLandManager.IsQuayObject`) |
| `ZONE` | 6 | Tag `ZoneObject` |
| `PROP` | 7 | 地形プロップ（個体ごとのColliderを持たないためレイキャストでは判定しない。配置は地形クリック、ブルドーザの削除は `RemovalManager` が画面レイで `PropManager.TryPick` のAABB判定を行い、この値は削除済み集合のキーにだけ使う） |

`MODE_NONE` に設定されたオブジェクトタイプはRayを貫通し、背後にあるオブジェクトが選択対象となる。

海面は `TerrainManager` が管理する地形タイルではなく、`Terrain` コンポーネントを持たない板ポリゴンである。そのため `SeaObject` タグで `TERRAIN` に分類し、`InputHandler` の初期化時に海面へ `TerrainInputHandler` を付与する（既に付いていれば付与しない）。これにより**海面のクリック・ホバーは地形と完全に同じ経路**を流れ、地形と同じモード別の挙動になる。土地区画作成（点選択）も海の上で成立し、そこに作られた区画は海上区画（`PARCEL_WATER`）になる（[土地形状](../geometries/B_土地形状.md) B.5）。

都市外の背景地形（[地形形状](../geometries/D_地形形状.md)「都市外の背景地形」）のチャンクも `Terrain` コンポーネントを持たない板ポリゴンである。`TerrainManager` がチャンクの生成時に `TerrainInputHandler` を付与し、`TerrainManager.IsBackgroundTerrain` で `TERRAIN` に分類する。カーソル位置（`posX` / `posZ`）も地形タイルと同じく背景の起伏の上で解決し、ホバーも地形と同じ経路を流れる。テンプレートモードで都市外に道路・線路・建物を置く操作と、都市外のコンテナ貨物PFに国際貨物輸送路線の停留所を置く操作がこの経路を通る。

埋立地の岸壁（帯を覆うコンクリートの天端と、外周の垂直の面。[地形形状](../geometries/D_地形形状.md) D.17）も `Terrain` コンポーネントを持たないメッシュで、背景地形のチャンクと同じ構成（Ground レイヤー、`MeshCollider`、`TerrainInputHandler`。タグは付けない）で作られる。天端の上ではレイが最初に当たるのは岸壁のコライダーなので、これを地形として扱わないとカーソルのレイが天端を越えて海底に当たり、指している位置が大きくずれる。背景地形と岸壁の判定は `InputHandler.IsTerrainSurfaceObject` の1つにまとめ、カーソル位置の解決（`UpdateTerrainPosition`）・地形ホバー（`HandleTerrainHover`）・タイプ判定（`GetObjectType`）の3か所がこれを使う。岸壁の上のクリック・ホバーは地形と同じモード別の挙動になり、カーソルの高さ（`Utils.GetGroundHeight`）は天端の高さになる。

### 2.2. 入力モード

各オブジェクトタイプには独立して入力モードを設定できる。
同じオブジェクトであっても、モードが異なればクリック時の挙動が変わる。

| 定数 | 値 | 意味 |
|------|------|------|
| `MODE_NONE` | 0 | そのオブジェクトタイプの選択を無効化する。Rayを貫通する |
| `MODE_NORMAL` | 1 | 通常の情報表示モード。クリックで詳細パネルを開く |
| `MODE_ROAD_CONSTRUCTION` | 2 | 道路建設モード |
| `MODE_BUILDING_CONSTRUCTION` | 3 | 建物建設モード |
| `MODE_PARCEL_CREATION_POINT` | 4 | 土地区画作成（点選択）モード |
| `MODE_PARCEL_CREATION_PATH` | 5 | 土地区画作成（道路選択）モード |
| `MODE_PARCEL_CREATION_SPLIT` | 6 | 土地区画分割モード |
| `MODE_EDIT` | 7 | 既存オブジェクトの編集モード |
| `MODE_ZONE_EDIT` | 8 | 地区編集モード |
| `MODE_PARCEL_SHAPE_EDIT` | 10 | 土地区画形状編集モード |
| `MODE_SET_GRID` | 11 | グリッド角度設定モード |
| `MODE_EDIT_RESOURCE` | 12 | 資源エリア編集モード |
| `MODE_EDIT_TRANSIT` | 13 | 公共交通設置モード |
| `MODE_EDIT_PROP` | 14 | プロップ配置モード（個別配置・範囲散布） |
| `MODE_EDIT_CONNECTOR` | 15 | 施設の接続端子ノードの移動編集モード。TERRAIN に適用し、端子マーカーを地形ドラッグとして掴む（§5.3） |
| `MODE_REMOVE` | 17 | ブルドーザ（空き地区画・計画中の建物と道路・地形プロップの削除。神モードでは稼働中の建物・道路も即時削除）。実処理は `RemovalManager` |
| `MODE_PATH_MERGE` | 18 | パス結合の相手選択。候補パスのクリックだけを受け付ける |
| `MODE_PATH_SPLIT` | 19 | パス分割の分割点選択。PATH と TERRAIN に適用し、どちらのクリックも対象パスの中心線へ射影する |
| `MODE_PICK_BUILDING` | 20 | 既存建物のコピー建設・経済主体作成の入居先の選択。建物だけをクリック可能にする |
| `MODE_EDIT_ROAD_MEMBERS` | 22 | 道路（名前付き道路）の区間編集モード。街路・高速道路の区間のクリックをUIへ通知する |
| `MODE_EDIT_GRADING` | 23 | 造成（整地・埋立）の範囲の作図モード。TERRAIN に適用し、資源エリアの作図と同じ地形クリック・頂点ドラッグで範囲を描く。共通吸着を通さない（§10） |

接続点編集（`STATE_EDIT_CONNECTOR`）の端子マーカー（逆四角錐）はノード実体の上空に浮くオーバーレイで、ノードのコライダーとは位置がずれる。そのためノードへのレイキャストではなく TERRAIN の `MODE_EDIT_CONNECTOR` の地形ドラッグとして受け、`ConnectorEditManager` がスクリーン座標のマーカー当たり判定で掴む対象を決める（NODE は `MODE_NONE` で貫通させる）。

### 2.3. モードプリセット

複数のオブジェクトタイプのモードを一括で設定するプリセット。
Tauri UIプロセスからの `world.change_state` イベントに応じて、`IpcStateCoordinator` が対応するプリセットを適用する。

| プリセット | TERRAIN | PATH | NODE | PARCEL | BUILDING | ZONE |
|-----------|---------|------|------|--------|----------|------|
| `ALL_NORMAL` | NORMAL | NORMAL | NORMAL | NORMAL | NORMAL | NORMAL |
| `ROAD_CONSTRUCTION` | ROAD_CONSTRUCTION | NONE | NONE | NONE | NONE | — |
| `BUILDING_CONSTRUCTION` | — | NONE | — | BUILDING_CONSTRUCTION | NONE | — |
| `PARCEL_CREATION_POINT` | PARCEL_CREATION_POINT | NONE | — | NONE | NONE | — |
| `PARCEL_CREATION_PATH` | — | PARCEL_CREATION_PATH | — | NONE | NONE | — |
| `PARCEL_CREATION_SPLIT` | — | NONE | — | PARCEL_CREATION_SPLIT | NONE | — |
| `ZONE_EDIT` | ZONE_EDIT | ZONE_EDIT | NONE | NONE | NONE | ZONE_EDIT |
| `PARCEL_SHAPE_EDIT` | PARCEL_SHAPE_EDIT | NONE | NONE | NONE | NONE | NONE |
| `SET_GRID` | SET_GRID | NONE | NONE | NONE | NONE | NONE |
| `EDIT_RESOURCE` | EDIT_RESOURCE | NONE | NONE | NONE | NONE | NONE |
| `TRANSIT_CONSTRUCTION` | EDIT_TRANSIT | NONE | NONE | NONE | NONE | NONE |
| `PROP_PLACEMENT` | EDIT_PROP | NONE | NONE | NONE | NONE | NONE |
| `CONNECTOR_EDIT` | EDIT_CONNECTOR | NONE | NONE | NONE | NONE | NONE |
| `REMOVE_OBJECTS` | REMOVE | REMOVE | NONE | REMOVE | REMOVE | NONE |
| `PATH_MERGE` | NONE | PATH_MERGE | NONE | NONE | NONE | NONE |
| `PATH_SPLIT` | PATH_SPLIT | PATH_SPLIT | NONE | NONE | NONE | NONE |
| `PICK_BUILDING` | NONE | NONE | NONE | NONE | PICK_BUILDING | NONE |
| `ROAD_MEMBER_EDIT` | NONE | EDIT_ROAD_MEMBERS | NONE | NONE | NONE | NONE |
| `EDIT_GRADING` | EDIT_GRADING | NONE | NONE | NONE | NONE | NONE |

表の「—」はプリセットで明示的に設定されないオブジェクトタイプを示す（前回の設定が維持される）。

`CONNECTOR_EDIT` は `STATE_EDIT_CONNECTOR`（接続点編集）で適用され、TERRAIN を `MODE_EDIT_CONNECTOR` にして施設の接続端子ノード（`infra_node_id` / `depot_node_id`）のマーカーを地形ドラッグで掴めるようにする（§2.2）。対象ノードの強調表示・掴む対象の決定・立地区画内へのクランプは `ConnectorEditManager` が担い、実移動と `PATCH /api/v1/nodes/{node_id}` の送信は `NodeInputHandler` に委譲する（端子ノードに `NodeInputHandler` が無ければ後付けする）。パスが接続済みのノードはバックエンドが移動を拒否し、元位置に戻る。詳細は `IPC連携の整理.md` の `STATE_EDIT_CONNECTOR` を参照。

`PROP_PLACEMENT` は `STATE_EDIT_PROP`（プロップ配置）で適用され、地形クリックでプロップを配置する。`IpcStateCoordinator.ApplyStateEditProp` が `PropPlacementManager` を動的生成し、個別配置（`edit_prop:update_instances`）／範囲散布（`edit_prop:update_shape`）のモードごとに処理する。詳細は `IPC連携の整理.md` の `STATE_EDIT_PROP` を参照。

`ROAD_MEMBER_EDIT` は `STATE_EDIT_ROAD_MEMBERS`（道路の区間編集）で適用され、PATH だけを `MODE_EDIT_ROAD_MEMBERS` にして他のオブジェクトタイプはクリックを無効化する（パス結合の相手選択と同型）。`IpcStateCoordinator.ApplyStateEditRoadMembers` が `RoadMemberEditManager` を生成して開始し、区間のクリックは `RoadMemberEditManager.OnPathClicked` が `edit_road:toggle_path` として UI へ送る。所属の付け外しは UI が API で行うので、Unity は所属を書き換えない。詳細は[道路の詳細と道路名の区間編集モード](道路の詳細と道路名.md#区間編集モード)を参照。

`EDIT_GRADING` は `STATE_EDIT_GRADING`（造成範囲の作図）で適用され、TERRAIN だけを `MODE_EDIT_GRADING` にして他のオブジェクトタイプを貫通させる（資源エリアの作図 `EDIT_RESOURCE` と同型）。区画・道路・建物の上でも、その下の地面（埋立地の岸壁の天端を含む）に頂点を置ける。`IpcStateCoordinator.ApplyStateEditGrading` が `GradingAreaEditManager`（任意ポリゴン編集の共通基底 `PolygonAreaEditManager` の派生。§8）を生成し、Placing から作図を始める。Placing 中は次の左クリックで頂点が置かれる位置にポインタマーカーを描く（§12「土地区画の形状編集」）。範囲の変化は `edit_grading:update_shape` でUIへ送るだけで、見積もりと発注はUIとバックエンドが行う。資源エリアの作図と違い、頂点は吸着せず（§10）、Esc でもモードから抜けない（`CancelOnEscape=false`）。共通基底の Esc は入力プリセットを `ALL_NORMAL` に戻すだけでステート遷移もUIへの通知も伴わず、ウィンドウが開いたまま編集用の表示も残るためである。抜けるのはウィンドウを閉じたとき（Rust が `STATE_NORMAL` を送る）と確定後の `edit_grading:finish` だけ（[IPC連携の整理](IPC連携の整理.md)）。

## 3. 入力フロー

### 3.1. マウス左クリックとドラッグの判定

`InputHandler` はマウスダウン時に即座にクリックを発火せず、「ペンディングクリック」として記録する。
マウスアップ時にマウスの移動距離が閾値（5ピクセル）未満であればクリックとして発火し、閾値以上であればドラッグとして処理する。
これにより、クリックとドラッグが正しく区別される。

### 3.2. カメラドラッグとの競合回避

`CameraController` は地形を左ドラッグでカメラ移動する機能を持つ。
`InputHandler` と `CameraController` は以下の仕組みで競合を回避する。

- `CameraController` にもマウスダウンからドラッグ確定までの閾値判定がある。
- `InputHandler` は `CameraController.IsMouseDragging` がtrueの間、ホバーをクリアし、ペンディング中のクリック・ドラッグを破棄する。
- `CameraController` は `IsPendingDrag` の間は `InputHandler` 側のクリック候補記録を許容する（`IsPendingDrag` は `IsMouseDragging` ではない）。
- 編集ハンドル（道路の制御点や土地区画の頂点）上でドラッグを開始した場合、`CameraController` はドラッグを開始しない。

右クリックも同様に、マウスダウン時には発火しない。マウスアップまでの移動が閾値未満なら `onRightClick`、閾値以上なら右ドラッグ（カメラ回転）とみなして発火しない。`CameraController` 側の右回転も同じ閾値の保留方式（`isPendingRotate`）で始まる。`CameraController` は `[DefaultExecutionOrder(-100)]` で先に走るため、押下フレームで `IsMouseRotating` を見て判定すると右クリックが必ず回転に食われる。判定は移動量で行う。

UI上にポインタがある間・カメラドラッグ中・フライ/撮影モード中（`CameraController.BlocksWorldInput`）・ドライブ中（`DriveMode.IsOpen`）・ラジアルメニュー表示中（11章）は、`InputHandler.Update` 先頭のゲートで入力処理全体を非破壊的に止める。入力プリセットは変えないので、抜ければ元の入力状態がそのまま復帰する。撮影を終了したフレームも遮断し、Escを背後の選択解除へ渡さない。

3Dに追従するクリック可能なラベル（地区名・天然資源名）もUIとして判定され、その矩形上ではこのゲートとカメラ操作が止まる。そのためラベルのクリック判定は文字列を囲む矩形に限り、ラベルの Canvas は入力モードが通常のときだけクリックを受ける（`Utils.SyncOverlayCanvasClickable`）。編集中はラベルを表示したまま、クリック・ホバー・ホイール操作を下の3Dへ通す。インターチェンジ名・道路名のラベルは編集中は非表示にする。

### 3.3. ドラッグを持たないモード

ブルドーザ（`MODE_REMOVE`）、パス結合・分割の対象選択（`MODE_PATH_MERGE` / `MODE_PATH_SPLIT`）、道路の区間編集（`MODE_EDIT_ROAD_MEMBERS`）、地区編集（`MODE_ZONE_EDIT`）はクリックのみで操作する。
ブルドーザ中のクリックは、レイキャストのヒット物へ配送する前に地形プロップを判定する（`InputHandler.TryRemovePropAtScreenPoint` → `RemovalManager.TryRemovePropAt`。プロップはColliderを持たないため `PropManager.TryPick` のAABB判定で、ヒット物より手前にある最も近い1本を選ぶ）。プロップを拾えた場合は背後の建物・区画へは配送しない。ホバーも同様に `RemovalManager.UpdatePropHover` で赤い枠を出す。
これらのモードでは `StartDrag` / `StartTerrainDrag` の冒頭で即座に return し、ドラッグ状態に入らない
（入らないと既定の経路で道路エディタのドラッグ処理へフォールバックしてしまう）。
地区編集の左ドラッグは通常どおりカメラの移動になる。

### 3.4. 起伏地形での土地区画選択（地形ヒットの区画への差し替え）

土地区画のコライダー上面はポリゴンの角の頂点でしか地形高さをサンプルしないため、起伏のある地形では頂点間の地形の盛り上がりにコライダーが埋まり、レイキャストでは手前の地形が必ず勝って区画を選択できなくなる。

これを解決するため、`InputHandler` はRayの最初の有効ヒットが地形（`TERRAIN`）で、かつ土地区画（`PARCEL`）の入力モードが `MODE_NONE` でない場合、ヒット点のXZ座標を含む土地区画ポリゴンを2Dの内外判定（`ParcelManager.GetParcelIdAtPoint`）で探し、見つかればホバー・クリックのディスパッチ先をその区画オブジェクトに差し替える。

- 土地区画は概念的に「地面上の領域」であるため、選択の解決を2D判定で行ってよい（[Y座標の考え方]と整合する）。
- 区画コライダーが地形より上に出ている平地では、従来どおりレイキャストが先に区画を拾うため挙動は変わらない。
- 判定はセル単位の逆引きインデックスとBBOXで候補を絞り込むため、区画数が増えても全件走査は発生しない。

### 3.5. 中ボタンドラッグによる建物の回転（直接配置）

建物建設モード（`MODE_BUILDING_CONSTRUCTION`）の**直接配置**では、マウス中ボタンのドラッグで矩形ゴーストを1度単位で回転できる。

- 中ボタンの入力は `InputUtil` のヘルパ（`MiddleMouseWasPressedThisFrame` / `MiddleMouseIsPressed` / `MiddleMouseWasReleasedThisFrame`。いずれも新Input Systemの `Mouse.current.middleButton`）で取得する。
- 回転の処理は `BuildingPlanManager.Update()` が担う。押下時にマウスX座標を記録し、ドラッグ中は水平移動量（px）×感度（`MiddleDragDegreesPerPixel`、既定0.5°/px）を1度単位に量子化して回転角へ加算し、ローカルで矩形を即時再描画する（バックエンド往復なし）。端数は次フレームへ持ち越す。
- 回転は地点確定の**前後どちらでも**有効。ドラッグ中はバックエンドへ問い合わせず、ボタンリリース時に確定角度を `edit_building:update_direct_angle` でUIへ1回だけ通知する（UIは回転ステッパーを更新し、確定後=フェーズBでは `preview-direct` を再取得して権威プレビューを更新する）。
- 中ボタンは他モード・カメラ操作では未使用のため競合しない（カメラは左ドラッグ=移動／右ドラッグ=回転、§5.4 参照）。
- 回転中はホバーによるゴースト中心の追従を止め、その場で回転する（`OnTerrainHoverDirect` が `isMiddleDragging` 中は中心を更新しない）。

## 4. 各オブジェクトタイプのハンドラとモード別挙動

すべてのハンドラは `IInputHandler` インターフェースを実装し、`onClick`、`onRightClick`、`onHoverStart`、`onHover`、`onHoverEnd` を持つ。

### 4.1. TerrainInputHandler（地面）

地形のクリック・ホバーを処理する。スナップ機能を内包し、他のハンドラからも参照される。

`TerrainInputHandler` は地形タイルや海面へ付与されるため、起動直後には存在しない場合がある。`RoadPlanManager` は吸着を実行するときに参照を取得し、タイルの破棄で参照が失われたら再取得する。起動時の未生成はエラーとして扱わない。

| モード | 左クリック | 右クリック | ホバー |
|-------|-----------|-----------|-------|
| `MODE_NORMAL` | ログ出力 | — | スナップ位置計算・マウス位置上書き |
| `MODE_ROAD_CONSTRUCTION` | `RoadPlanManager.OnTerrainLeftClick` に委譲 | 同右 | `RoadPlanManager.OnTerrainHover` に委譲 |
| `MODE_ZONE_EDIT` | Mergeツールのときだけカーソル下の地区を統合元にする（7章） | カーソル下の地区を選択（地区外なら選択解除） | カーソル下の地区の半透明フィル表示 |
| `MODE_BUILDING_CONSTRUCTION` | `BuildingPlanManager.OnTerrainClickDirect` に委譲（直接配置=区画外地点の確定） | — | `BuildingPlanManager.OnTerrainHoverDirect` に委譲（直接配置=カーソル追従の矩形ゴースト表示）。ホバー離脱時 `HideDirectGhost` |
| `MODE_PARCEL_CREATION_POINT` | 土地区画エディタに点を通知 | — | 配置マーカー表示 |
| `MODE_PARCEL_SHAPE_EDIT` | `ParcelShapeEditManager.OnTerrainLeftClick` に委譲 | 同右 | 同 `OnTerrainHover` に委譲（常に Editing なのでポインタマーカーは出ない。頂点・辺のホバー判定は毎フレームの更新で行う）。離脱時 `OnTerrainHoverEnd`。共通吸着なし |
| `MODE_EDIT_RESOURCE` | `ResourceAreaEditManager.OnTerrainLeftClick` に委譲 | 同右 | 同 `OnTerrainHover` に委譲（Placing 中は次のクリックで置かれる位置にポインタマーカー）。離脱時 `OnTerrainHoverEnd` |
| `MODE_EDIT_GRADING` | `GradingAreaEditManager.OnTerrainLeftClick` に委譲（Placing=頂点を末尾に追加／Editing=頂点の選択・辺上への頂点挿入）。共通吸着なし | 同右（Placing=頂点3以上で形を閉じて Editing へ／Editing=頂点削除。3頂点は残す）。共通吸着なし | 同 `OnTerrainHover` に委譲（Placing 中は次のクリックで頂点が置かれる位置にポインタマーカー。Editing の頂点・辺のホバー判定は毎フレームの更新で行う）。離脱時 `OnTerrainHoverEnd`。共通吸着なし |
| `MODE_EDIT_TRANSIT` | `TransitPlanManager.OnTerrainLeftClick` に委譲 | 同右 | 同 `OnTerrainHover` に委譲 |
| `MODE_EDIT_PROP` | `PropPlacementManager.OnTerrainLeftClick` に委譲（個別=1個配置／範囲=頂点追加） | 同右（個別=直近取消／範囲=直近頂点取消） | 同 `OnTerrainHover` に委譲（個別=ゴースト追従。中ボタン回転中は位置を固定） |
| `MODE_REMOVE` | 何もしない（地形に埋もれた空き区画は `InputHandler` の地形→区画置換で区画へ配送される。地形プロップは配送前に `InputHandler` が判定する） | — | — |

海面もこのハンドラで処理される（§2.1）。ただしヒット点のワールドYは海底の地形標高（負）になるため、配置マーカーはそのまま置くと海面板の下に隠れてしまう。マーカーは海面板より上を下限として表示し、どこを指しているかが見えるようにする。土地区画作成のプレビュー枠線も同じく海面を下限に置き、完成後の海上区画（基準標高＝海面）と高さが揃うようにする。

### 4.2. PathInputHandler（道路）

道路オブジェクトのクリック・ホバーを処理する。

| モード | 左クリック | 右クリック | ホバー |
|-------|-----------|-----------|-------|
| `MODE_NORMAL` | 道路詳細パネルをIPC通知 | — | ハイライト表示 |
| `MODE_ZONE_EDIT` | 現在ツールの適用（Create=その街路で新規地区を作成／Expand=選択中地区へ追加／Merge=統合の要求。7章） | 対象地区の選択（その道路の `zone_id`） | 操作案内ツールチップ表示 |
| `MODE_PARCEL_CREATION_PATH` | 土地区画エディタに道路選択を通知 | — | 選択可能な道路のみハイライト |
| `MODE_ROAD_CONSTRUCTION` | 地形クリックとして処理 | 同右 | ハイライトをスキップ |
| `MODE_EDIT_ROAD_MEMBERS` | `RoadMemberEditManager.OnPathClicked` に委譲（街路・高速道路なら `edit_road:toggle_path` をUIへ送る。それ以外の種別はツールチップで拒否） | — | 街路・高速道路の区間だけハイライトし、操作案内のツールチップを出す（それ以外の種別は拒否のツールチップのみ） |

### 4.3. NodeInputHandler（ノード）

ノードのクリック・ドラッグを処理する。`InputHandler` からドラッグイベントを直接受ける。

| モード | 左クリック | ドラッグ |
|-------|-----------|--------|
| `MODE_NORMAL` | ログ出力 | — |
| `MODE_ROAD_CONSTRUCTION` | 地形クリックとして処理 | — |
| `MODE_EDIT` | 選択状態の記録 | 開始時：元位置を記録、更新時：ノードを移動、終了時：バックエンドにPATCH送信 |

### 4.4. ParcelInputHandler（土地区画）

土地区画オブジェクトのクリック・ホバーを処理する。

| モード | 左クリック | ホバー |
|-------|-----------|-------|
| `MODE_NORMAL` | 土地区画詳細パネルをIPC通知 | 2Dオーバーレイでハイライト |
| `MODE_BUILDING_CONSTRUCTION` | `BuildingPlanManager.OnParcelClick` に委譲 | 同上 |
| `MODE_PARCEL_CREATION_SPLIT` | 土地区画エディタに選択を通知 | 同上 |

`MODE_BUILDING_CONSTRUCTION` では区画の実データを添えて委譲し、区画の種別（`parcel_type`）が建設中のバリアントの要求する種別と一致しない場合はクリックを無視してUIへ警告を送る。要求する種別は選択中バリアントのマスターデータ（`agent_master` の `parcel_type`。未設定なら陸地）で、建設可能な区画のハイライトも同じ条件で色分けする（区画の種別と建物の関係は [土地形状](../geometries/B_土地形状.md) B.5）。

### 4.5. BuildingInputHandler（建物）

建物オブジェクトのクリック・ホバーを処理する。

| モード | 左クリック | ホバー |
|-------|-----------|-------|
| `MODE_NORMAL` | エージェント詳細パネルをIPC通知 | 3Dメッシュのハイライト |
| `MODE_BUILDING_CONSTRUCTION` | 建物の解体をバックエンドにPOST | — |

## 5. ドラッグの種類と処理フロー

`InputHandler` はドラッグの種類を以下の優先度で判定する。

### 5.1. 地区編集にドラッグは無い

地区編集（`MODE_ZONE_EDIT`）はクリックだけで操作し（7章）、左ドラッグは通常どおりカメラの移動になる（§3.3）。

### 5.2. ノードドラッグ

条件：ノードオブジェクト上でドラッグ開始。

1. `InputHandler.StartDrag()` で `NodeInputHandler.onDragStart` を呼び出し
2. ドラッグ中、`InputHandler.UpdateDrag()` で `NodeInputHandler.onDragUpdate` を呼び出し
3. ドラッグ終了時、`InputHandler.EndDrag()` で `NodeInputHandler.onDragEnd` を呼び出し

### 5.3. 地形ドラッグ

条件：地形上でドラッグ開始、かつ地形モードがドラッグを許可するモード。

地形モードに応じて異なる処理系に委譲する。

| 地形モード | 委譲先 | 処理内容 |
|-----------|--------|---------|
| `MODE_ROAD_CONSTRUCTION` / `MODE_EDIT` | `RoadPlanManager` | 道路のポイント・制御点のドラッグ移動 |
| `MODE_PARCEL_CREATION_POINT` | `ParcelEditorManager` | 土地区画の回転操作 |
| `MODE_PARCEL_SHAPE_EDIT` | `ParcelShapeEditManager` | 土地区画頂点のドラッグ移動 |
| `MODE_EDIT_RESOURCE` | `ResourceAreaEditManager` | 資源エリア頂点のドラッグ移動 |
| `MODE_EDIT_GRADING` | `GradingAreaEditManager` | 造成範囲の頂点のドラッグ移動（Editing のみ。吸着なし） |
| `MODE_EDIT_TRANSIT` | `TransitPlanManager` | 停留所・経由点の移動、区間のドラッグでの経由点の挿入 |
| `MODE_EDIT_CONNECTOR` | `ConnectorEditManager` | 接続端子マーカーのドラッグ移動（XZは施設が立地する土地区画の内側へクランプ） |

道路編集中の場合、ドラッグ開始前に `RoadPlanManager.IsMouseOverEditHandle` で制御点上かどうかを判定する。
制御点上でなければ地形ドラッグとして道路のポイント移動を開始する。

地形コライダー（Terrain／海面）にヒットした通常のケースでは、ドラッグはオブジェクトクリック候補（type=TERRAIN）から `StartDrag()` → `UpdateDrag()` と流れ、各マネージャの掴み開始処理（`StartDrag`）は呼ばれずに `ProcessMouseDrag(worldPos)` だけが毎フレーム届く。`InputHandler.StartTerrainDrag()` はレイキャストが何にもヒットしなかった場合のフォールバックでしかない。そのため地形ドラッグを受けるマネージャ（`RoadPlanManager`・`ConnectorEditManager`・`TransitPlanManager`）は、`ProcessMouseDrag` の初回呼び出しで現在のマウス位置から掴む対象を決める（遅延グラブ）。掴みたい対象の上でカメラドラッグが先に始まらないよう、`CameraController.IsMouseOverEditHandle` にもその対象の当たり判定を加える。

造成範囲の作図（`MODE_EDIT_GRADING`）は遅延グラブを使わず、`InputHandler.StartDrag()` がドラッグ開始の時点でマウスが頂点の上か（`GradingAreaEditManager.IsMouseOverVertex`。Editing のときだけ真）を調べ、頂点の上なら `GradingAreaEditManager.StartDrag` で掴む。造成範囲の頂点は `CameraController.IsMouseOverEditHandle` の判定にも含まれ、頂点のドラッグがカメラのパンに奪われない。

### 5.4. カメラドラッグ

`CameraController.HandleMouseDragPan` が地形を左ドラッグでカメラ移動を行う。
以下の条件ではカメラドラッグを開始しない。

- UI上をクリックした場合
- 右クリック回転中の場合
- 編集ハンドル上をクリックした場合

カメラドラッグではドラッグ開始時のカメラ位置・回転を保存し、現在のマウス位置を保存したカメラ状態で地面に投影して差分を計算する。
これによりカメラの移動と地面座標の計算の間にフィードバックループが生じるのを防ぐ。
ドラッグ終了後は慣性によりカメラが減速しながら停止する。

## 6. 道路エディタの状態遷移

`RoadPlanManager` は `RoadEditorPhase` により3つのフェーズを持つ。

| フェーズ | 意味 |
|---------|------|
| `Inactive` | 道路エディタが非アクティブ |
| `Placing` | 左クリックで始点・ウェイポイントを配置するモード |
| `Editing` | 配置済みのポイント・制御点をドラッグで調整するモード |

`Placing` フェーズでは左クリックでポイントを追加し、右クリックで形状を確定して `Editing` フェーズに移行する。
`Editing` フェーズでは、ポイントのドラッグ移動、制御点（`control_in` / `control_out`）のドラッグ移動、辺上クリックでのウェイポイント挿入、右クリックでのポイント削除が可能。
ポイントのドラッグには2つの移動軸がある。ポイントをクリックして選択した直後は **XZ（地面に沿った移動）** で、選択中のポイントをもう一度クリックすると **Y（地面からの高さ）** に切り替わり、以後はクリックのたびにトグルする。別のポイントを選ぶか空クリックで選択解除するとXZに戻る。標高編集不可のバリアント（街路など）はXZ固定。現在の軸はカーソル脇のHUDに両向き矢印（横=XZ、縦=Y）で表示される。
Y軸ドラッグは、ポイントを通りカメラ正面を向いた鉛直平面とマウスレイの交点の高さをドラッグ開始時からの差分で適用する（斜め視点ではカーソルの上下とポイントの上下が一致する）。ほぼ真上からの視点では平面とレイが平行に近づくため、ポイント奥行きでの1ピクセルあたりの長さを画面縦座標に掛けた換算に切り替わる。下限はキー操作と同じくバリアントの `min_height`。XZ吸着は適用しない。
制御点は Catmull-Rom スプラインから自動計算されるが、ドラッグした制御点は平面位置（XZ）が手動調整済みとして扱われ、以後のポイント移動・挿入・削除・標高変更で自動計算に戻されない。手動調整済みの制御点は、その制御点が属するポイントを動かしたときだけアンカーと一緒に平面上で平行移動する（隣接ポイントの移動には追従しない）。曲線/直線モードの切り替えでは手動調整は破棄され、全制御点が自動計算し直される。
制御点の高さはドラッグでは固定されず、ポイントの縦断勾配から決める（規則と例外は「高架の制御点と縦断形状」）。
既存パスの編集再開では、保存済みの制御点と自動計算値の平面位置が一定以上（0.05m）ずれている制御点だけを手動調整済みとして復元する。確定送信には自動計算値も明示的に載るため、保存値をすべて手動扱いにすると、再開後にポイントを動かしても制御点が一切追従しなくなる。高さが縦断勾配の規則から0.05m以上ずれる制御点（経路提案の結果など）は、そのポイントの制御点の高さを固定して保存値を再現する。

端点が既存パスの端点に接続していて直線延長吸着（10章）がONのときは、端点の制御点を既存パスの延長線方向へ整列させ、接続部で道路が折れないようにする。整列は自動接線との角度差が `PATH_TANGENT_ALIGN_MAX_ANGLE_DEG`（20°。`consts.py` から同期）以内のときだけ行い、明らかに別方向へ引いた曲線は尊重する。交差点接続など候補が複数あるときは角度差が最小のものを採り、手動調整済みの端点制御点は整列しない。バックエンドも制御点が省略された登録に同じ整列を行うが、UIは整列済みの制御点を明示送信するため二重には適用されない。

確定ボタンでバックエンドにデータを送信する。

`Editing` フェーズの操作はUIの2Dパス形状編集パネル（`path_shape_editor`）からも行える。パネルは `edit_path:shape_command`（ノード移動・制御点移動・辺への挿入・削除・選択。XZのみ、吸着なし）を送り、`RoadPlanManager.ApplyShapeCommand` が3Dと同じロジックで適用して `edit_path:update_shape` で全状態を返す。3Dでドラッグ中のコマンドは無視される。高架・地下では2Dパネルの縦断面ストリップから既存の `edit_path:change_node` で高さだけを変えられる。各ノードには1始まりの番号ラベル（IMGUI。選択中は強調）を3Dビューに出し、2Dパネルと同じ番号で対応付ける。

道路エディタは `InputHandler` から入力イベントを受け取るほか、独自の `Update` ループでホバー状態の更新と高さ入力を処理し、`LateUpdate` でオーバーレイの描画を行う。

標高（PageUp/PageDown または 1/2 キー、±1m）と盛土（3/4 キー、±1m）はキーボードで変更する（標高は上記のY軸ドラッグでも変更できる）。
`Editing` フェーズで選択中ノードがあればそのノードの値を、選択がない場合（`Placing` フェーズを含む）は
「次に配置するノード」に適用される値（`currentHeight` / `currentEmbankmentHeight`）を変更する。
標高・盛土はパス建設パネルのステッパーからも変更でき（`edit_path:change_node` / `edit_path:change_embankment`）、
既存ノードを選んで数値で微調整する用途に使う。キー操作・選択で変わった値は `edit_path:select_node` で
パネルへ返り、双方向に同期する。曲線/直線と吸着の切り替えはCtrlラジアルメニュー（後述の11章）で行う。

## 7. 地区編集の状態管理

`ZoneEditContext` は地区編集モードの状態を管理するシングルトンである。
ツールは3つあり、Ctrlラジアルメニュー（`ZoneEditControlMenu`。後述の11章と同じ仕組み）で切り替える。

| ツール | 右クリック | 左クリック |
|-------|-----------|-----------|
| 新規地区の作成（`Create`） | 対象地区を選択（拡張ツールへ切替） | クリックした街路を含む新規地区を作成する |
| 既存地区の拡張（`Expand`） | 拡張先の地区を選択 | クリックした街路を選択中の地区へ取り込む |
| 地区の統合（`Merge`） | 統合先の地区を選択 | クリックした地区を選択中の地区へ統合する（UIの確定を挟む） |

**3ツール共通で「右クリックで対象地区を選ぶ → 左クリックで適用」**という操作モデルになっている。
右クリックの対象は道路（その道路の `zone_id`）と地区の面（カーソル下の地区ポリゴン）の両方で、
地区の外で右クリックするか Esc を押すと選択が解除される。

選択中の地区は次の2か所で常に示される。

- **カーソル脇のHUD**（`CursorStatusHud`）: 現在ツールのアイコン＋選択中地区の識別色チップ＋地区名。
  新規作成ツールのときは「これから作る地区の色」を表示する（色はツールを選び直すたびに引き直される）。
- **3Dビューの枠線**: 地区編集中は地区枠線を太く・地形や建物に隠れないように描き、選択中の地区はさらに太くする。

新規作成の識別色はUnityが決めて `POST /api/v1/zones/` の `color` として送るため、
カーソル脇に提示した色がそのまま作られる地区の色になる。

作成と拡張はUnityが直接バックエンドへ送信する（3Dジェスチャで完結する操作）。
統合だけは地区がひとつ消える不可逆操作のため、Unityは `edit_zone:request_merge` をUIへ送るだけにし、
確定専用の小ウィンドウ（`zone_merge`）の確定ボタンでUI側が `POST /api/v1/zones/{id}/merge` を送る
（結果は `edit_zone:finish_merge` で戻る）。

Unity側の実装で守る点は次の3つ。

- **拡張の連続性はフロントで確認する。** `POST /api/v1/paths/batch-zone` は選択が1本だけだと連続性を検証しないため、そのまま送ると離れた道路が地区に混ざる。`PathInputHandler.IsConnectedToZone` が選択中地区の道路網と繋がっている街路だけを送る。
- **適用直後の先行着色は明示的に戻す。** 応答を待たずにクリックした街路へ適用先の色（新規作成はプレビュー色）を仮に乗せ、送信結果（成功・失敗とも）とモード終了で `ZoneEditContext.ClearTempVisuals` が戻す。失敗時はデータの版が変わらずリロードが再描画しないため、リロード任せにはできない。
- **既存地区のホバーフィル。** 地区編集中にカーソルが既存地区の領域に入ると `ZoneManager.SetHoverFillZone` が半透明フィルを描く。判定結果は `InputHandler.HoveredZoneId` として公開され、地形のクリック・右クリックからカーソル下の地区を解決するのに使う。地区のホバー判定は地区編集モード中だけ行う（通常プレイの毎フレーム負荷を避ける）。

## 8. 土地区画形状編集のドラッグ処理

`ParcelShapeEditManager` は既存の土地区画のポリゴン頂点をドラッグで編集する機能を提供する。
頂点編集そのもの（ヒット判定・ドラッグ・辺への頂点挿入・削除・オーバーレイ描画）は任意ポリゴン編集の共通基底
`PolygonAreaEditManager`（天然資源エリアの作成・造成範囲の作図と共用）にあり、`ParcelShapeEditManager` は保存済みポリゴンを読み込んで
編集フェーズから始めることと、`edit_parcel:update_shape` への通知だけを担う。
`InputHandler` からのドラッグイベントを以下のように処理する。

1. ドラッグ開始時：`IsMouseOverVertex` で頂点上かを判定し、頂点上なら `StartDrag` を呼び出す
2. ドラッグ中：`ProcessMouseDrag` で頂点位置を更新
3. ドラッグ終了時：`ProcessMouseDragEnd` でドラッグ終了処理

`CameraController` は `IsMouseOverEditHandle` で頂点上かを判定し、頂点上ではカメラドラッグを開始しない。

## 9. 入力状態の遷移とIPC連携

`IpcStateCoordinator` はTauri UIプロセスからの `world.change_state` イベントを受け取り、対応するモードプリセットを適用する。

| IPC状態文字列 | プリセット | 追加の初期化 |
|-------------|-----------|-------------|
| `STATE_NORMAL` | `ALL_NORMAL` | 各エディタの終了処理 |
| `STATE_EDIT_PATH` | `ROAD_CONSTRUCTION` | `RoadPlanManager.InitializeRoadPlacement` |
| `STATE_EDIT_BUILDING` | `BUILDING_CONSTRUCTION` | `BuildingPlanManager` の初期化 |
| `STATE_EDIT_ZONE` | `ZONE_EDIT` | `ZoneEditContext.EnterCreateMode` または `EnterEditMode`（道路のみ地区色分け表示にし、地区枠線を強調） |
| `STATE_EDIT_PARCEL` | 作成方法に応じて切替 | `ParcelEditorManager.EnterCreationMode`（作成方法の切り替えはUnity側のCtrlラジアルメニュー`ParcelEditControlMenu`が担い、プリセットも同メソッドが切り替える） |
| `STATE_EDIT_PARCEL_SHAPE` | `PARCEL_SHAPE_EDIT` | `ParcelShapeEditManager.InitializeEditMode` |
| `STATE_EDIT_PROP` | `PROP_PLACEMENT` | `PropPlacementManager` の初期化（個別配置／範囲散布） |
| `STATE_EDIT_CONNECTOR` | `CONNECTOR_EDIT` | `ConnectorEditManager.EnterEditMode`（接続端子ノードの強調＋ドラッグ有効化） |
| `STATE_EDIT_ROAD_MEMBERS` | `ROAD_MEMBER_EDIT` | `RoadMemberEditManager.Begin`（所属区間を道路色の帯で表示。詳細パネルを操作し続けるため Unity ウィンドウは最前面化しない） |
| `STATE_EDIT_RESOURCE` | `EDIT_RESOURCE` | `ResourceAreaEditManager` の初期化（天然資源エリアの新規作成） |
| `STATE_EDIT_GRADING` | `EDIT_GRADING` | `GradingAreaEditManager` を動的生成して `InitializeCreateMode`（造成範囲の作図。Placing から開始。再送されると描いた範囲を捨てて描き直す） |
| `STATE_EDIT_TRANSIT` | `TRANSIT_CONSTRUCTION` | `TransitPlanManager` を動的生成して `InitializeTransitPlacement`（`points` があれば経路編集として Editing から開始） |
| `STATE_MERGE_PATH` / `STATE_SPLIT_PATH` | `PATH_MERGE` / `PATH_SPLIT` | `PathTopologyEditManager.BeginMergeMode` / `BeginSplitMode`（Unity ウィンドウは最前面化しない） |
| `STATE_REMOVE_OBJECTS` | `REMOVE_OBJECTS` | ブルドーザ（`RemovalManager.BeginRemovalMode`。`instant_remove` を渡すと建物・道路を状態で絞らない即時削除モード） |
| `STATE_SET_GRID` | `SET_GRID` | `GridSetManager`（吸着フラグを退避して「ノード吸着のみON」にする。10章） |

### 9.1. ステート別の表示プリセット

編集ステートに入ると、その作業に必要なものが見える表示へ自動で切り替わり、**通常モードへ戻ると元の表示へ復帰する**。
表示状態の正は `IpcViewCoordinator` のレイヤー記憶で、プリセットの適用も復帰もここに集約されている
（`IpcStateCoordinator` の各 `ApplyState*` は入力モードとマネージャの初期化だけを担い、`VisualModeHandler` を直接触らない）。

| ステート（操作） | 強制する表示 |
|---|---|
| `STATE_EDIT_PATH` / `STATE_MERGE_PATH` / `STATE_SPLIT_PATH` / `STATE_EDIT_ROAD_MEMBERS` / `STATE_EDIT_GRADING` / `STATE_EDIT_BUILDING`（道路・線路・インフラ・建物の設置、道路の区間編集、造成範囲の作図） | 区画（建物建設は建設可能な区画の色分け、他は境界線）・地区・等高線を表示、計画中の道路と建物を表示、プロップを非表示 |
| `STATE_EDIT_PARCEL`（土地区画編集） | 区画・地区を表示、計画中の道路と建物を表示 |
| `STATE_EDIT_ZONE`（地区編集） | 区画・地区を表示、道路のみ地区色分け、計画中表示と公共施設ハイライトはOFF |
| `STATE_EDIT_TRANSIT` ＋ プリセット `transit`（公共交通の一覧・新設） | 公共交通路線を全種別表示、計画中の道路と建物を表示 |
| プリセット `parcel_detail`（土地区画詳細） | 土地区画の境界線を表示 |
| `STATE_REMOVE_OBJECTS`（ブルドーザ） | 区画の境界線・計画中の道路と建物を表示、地区は非表示（プロップは削除対象なので消さない） |

復帰の規則は次の2つ。

- **通常モードでユーザーが選んだ表示だけを記憶する。** `view:change_view` による変更は `STATE_NORMAL` のときだけ記憶され、編集ステート中やプリセット適用中の変更は記憶しない。
- **`STATE_NORMAL` へ戻るかプリセットを解除すると記憶を復元する。** 記憶が無いレイヤーは既定値（UI側 `DISPLAY_LAYERS` の初期値）に戻る。編集中に強制したレイヤーは必ず復元対象に含める。

新しい都市の読み込み(`game:loading`。新規ゲーム・チュートリアル・ロードのすべて)では、表示パネルで選べる全レイヤー(地形・道路・建物・区画・地区・天然資源・公共交通・プロップ・アラート)の色モード・サブモード・フラグと通常モードの記憶を、UI側 `DISPLAY_LAYERS` の初期値へ戻して描画へ反映し、`view:current_state` でUIへ通知する。前の都市で選んだ表示(資源の表示・アラートの絞り込みなど)は次の都市へ持ち越されない。グリッドと、オブジェクト単位の表示・非表示(`view:set_object_filter`)はこのリセットの対象外(後者は `session:reset` で捨てる)。

専用の編集ステートを持たないウィンドウは `view:apply_preset` で同じ仕組みに乗る。復帰はウィンドウを閉じたときにRust側が送る `STATE_NORMAL`（排他ウィンドウの場合）、または `view:restore_preset`（編集ステートと併存しうる詳細パネルの場合）が担う（ウィンドウは非表示になるだけでReact側のアンマウント処理が走らないため、Rust側のクローズ処理でも確実に送る）。

**UI側のパネルが一時的な表示変更を `view:change_view` で行ってはいけない。** 通常モードで送ると「ユーザーが選んだ表示」として記憶され、元の表示に戻せなくなる。
例外は、建設の確定後に計画中表示をONにする場合のように、**意図的にユーザーの既定を書き換えたいとき**だけ。

## 10. スナップ

`TerrainInputHandler.ApplySnapping` はマウス位置に対して以下の優先度でスナップを適用する。
スナップが有効な場合、`InputHandler` の `posX` / `posZ` がスナップ後の座標で上書きされる。

1. **グリッドスナップ** — 設定されたグリッド間隔に吸着
2. **地区頂点スナップ** — 地区ポリゴンの頂点に吸着
3. **ノードスナップ** — 既存のノード位置に吸着
4. **道路セグメントスナップ** — 既存の道路セグメント上の最寄り点に吸着

パス建設中のスナップには**インフラ網の互換性フィルタ**が適用される
（`InfraNetworkUtil`、建設中の `path_variant` を `ApplySnapping` に渡す）。
道路・線路の建設中はインフラノード／インフラパスに吸着せず、インフラパスの建設中は
同じ網（`path_master` の `infra_products` に共通の製品を持つ）のノード・パスと、
互換なインフラ施設の接続端子ノード（`owner_agent_id` 付きノード）にのみ吸着する。
道路編集の背景ガイド（既存ノード・辺の表示）も同じ判定で色分けされ、
吸着できない相手は減色、吸着先となる接続端子ノードは強調表示される。

`RoadPlanManager` は TerrainInputHandler の共通スナップに加えて、直角スナップを独自に実装している。
直角スナップは、ノードまたは道路セグメント上に配置されたポイントから既存の道路に直角に交わる方向へのスナップを提供する。

共通吸着は `TerrainInputHandler.onClick/onRightClick/onHover` の冒頭で全モードに掛かる（既定半径10m・バリアントフィルタ無し）が、次のモードは通さない。道路・線路の建設（`MODE_ROAD_CONSTRUCTION`）は `RoadPlanManager` が自前の吸着半径 `snapThreshold` と網互換フィルタで一度だけ吸着する（共通吸着も掛けると二重吸着になり、`snapThreshold` を変えても吸着範囲が変わらない）。接続点編集（`MODE_EDIT_CONNECTOR`）はドラッグ中の端子が自分の旧位置へ吸着し続けるのを防ぐため、パス分割（`MODE_PATH_SPLIT`）は端点へ引き寄せられると常に「端点に近すぎる」分割になるため、プロップ配置（`MODE_EDIT_PROP`）は任意の地点に置くため、土地区画の形状編集（`MODE_PARCEL_SHAPE_EDIT`）と造成範囲の作図（`MODE_EDIT_GRADING`）は頂点を吸着なしで置くため（§12）、それぞれ通さない。造成範囲の頂点が道路の中心線へ吸い付くと範囲がパスに掛かり、発注で必ず拒否される。

吸着で引き継ぐ相対高さは、バックエンドが保存している相対Yから取る（ノード＝ノードの相対高さ、パス区間＝アンカー相対YのXZ最近傍区間での線形補間、建設中の端点＝その端点の相対Y。保存設計標高のある線路だけは基準線の絶対標高から地形を引いて求める）。描画用点列の絶対Yから逆算してはならない。描画点列には地形起伏・描画オフセット・高さ源の違いによる残差が乗るため、地面のパスへ吸着しただけで端点が地下／空中扱いになり、見た目は繋がっているのにインフラのT字タップがバックエンドの高さ判定で成立しない「幽霊接続」になる。

複線（単線パス群の平行敷設。`lateral_offsets` に0以外がある間）の吸着はオフセット考慮版で行う。確定後は平行な単線パス群に分解されてグループの実体が残らないため、グループ中心線を既存線路の中心線へ素朴に吸着させると全線路が横にずれて1本も接続できない。吸着対象（ノード・建設中端点・区間）の位置と接線から「グループ内のいずれかの線路がその位置に乗る中心線位置」を候補として列挙し、カーソル最寄りの候補へ吸着する。同構成の延長では全線路の端点が一致する。

吸着のON/OFFフラグ（`TerrainInputHandler.Enable*Snapping` の static 変数群）の真実源はUnity側にあり、
切り替えは後述のCtrlラジアルメニューで行う（UIパネルからの設定イベントは存在しない）。
グリッド吸着はグリッド表示と連動しており、UIパネルの `view:set_grid`・ラジアルメニューのグリッド項目の
どちらで表示を切り替えても、`GridOverlay` が表示状態に合わせて吸着を自動でON/OFFする。
グリッド角度設定モード（`STATE_SET_GRID`）中は `GridSetManager` が吸着フラグを退避して
「ノード吸着のみON」に切り替え、モード終了時（確定・キャンセルとも）に復元する。

**直線延長吸着は敷設セッションの開始時に必ずOFFへ戻る。** 道路・軌道・インフラのどれでも、
曲線／直線のどちらでも、新規敷設・既存パス編集の開始時点ではOFFであり、使いたいときだけ
ラジアルメニューでONにする（連続設置で次の1本へ移るときは、セッション中の設定をそのまま保つ）。

**駅舎パス（`path_master` の type が `PATH_STATION_HOUSE`）の敷設中は、既存の地物への吸着を
一切行わない。** ノード吸着・パス吸着・直線延長・直角・並行/終端揃えのすべてが実効OFFになり、
ラジアルメニューでも該当項目がグレー表示、カーソルHUDのアイコンもOFFで描かれる（フラグ自体は
書き換えないので、他のバリアントに戻せばプレイヤーの設定がそのまま復帰する）。駅舎の線は
線路・道路と交差してもノードを作らず接続もしない（[Q_駅舎パスのメッシュ生成](../geometries/Q_駅舎パスのメッシュ生成.md) §3）
ため、吸着すると端点が線路上に乗って建屋が線路を避けきれなくなる。グリッド吸着だけは
プレイヤーが明示的に出したグリッドへの整列なので有効なまま。

### 10.1. 並行スナップと終端揃え

道路編集の直線モードでは、Ctrlメニューの **Parallel** で近隣パスとの並行補助を有効にできる。
配置済みの直前の点を通り、基準パスの区間と同じ方向の直線へカーソルを射影する。
編集中の端点ドラッグは隣接点、中間点ドラッグは前後の隣接点から候補を選ぶ。
曲線モードでは無効で、基準パスも直線・折れ線のみを対象とする。曲線の等距離複製は行わない。

**Align Ends** はParallelが有効なときだけ使用できる。基準パスの始端・終端からの垂線と
並行線との交点へ吸着し、横方向の間隔を保ったまま端を揃える。交差点に接続している端も対象だが、
途中のウェイポイントや描画用の細分化点は対象外。始点をまだ置いていない場合は端からの垂線へ吸着し、
両端を揃えた並行道路の始点を置ける。

- 検索は `PathManager.QueryNearbySegments` でカーソルと基準点の周囲100m（`parallelSearchRadius`）を調べる。
  空間インデックスの候補をXZの線分距離で絞り、編集中のパス自身・曲線・網互換でないパスを除外する。
- 吸着距離は `RoadPlanManager.snapThreshold`。並行線への横方向と、終端揃えの縦方向で個別に判定する。
  候補の評価は吸着移動距離と基準道路までの距離を使い、直前と同じ候補を僅かに優遇して切替のちらつきを抑える。
- 並行モードの優先順位は、接続吸着（ノード・計画中端点・パス、単線パスを並べる複線の横オフセットを含む。1本で複線を持つ線路（線路・モノレールで `lanes`≥2。LRT専用軌道を含む）は中心線1本で吸着する）→並行・終端揃え→
  グリッド→直線延長→直角。接続済みの位置・高さを角度補助で変更しない。並行OFF時は従来の順序を保つ。
  共通吸着の `skipGrid` 引数でグリッドを後回しにし、共有フラグを一時的に書き換えない。
- 並行・終端揃えはXZのみを変更し、高さ・盛土は敷設設定またはドラッグ中の点の相対高さに従う。
- ガイドは基準区間が黄、並行線が水色、端揃えが黄緑。吸着成立時に円を表示する。
  `overlayRenderer` に地形に沿う補助線を描き、道路本体の標高とは分離する。
- 2設定の初期値はOFF。`RoadPlanManager` のstaticプロパティでセッション間に保持し、曲線中や並行OFF時は
  設定を残して実効動作のみ停止する。候補・ガイドは初期化時に破棄する。

計算は `RoadParallelSnapGeometry`、候補検索・状態・描画は `RoadPlanManager.ParallelSnap.cs` に分離している。
配置・ホバー・右クリック確定・点ドラッグは共通の `ApplySnapping` を通る。メニューを閉じたときも
生のホバー座標から再計算するため、静止Rayを省略する入力最適化があってもプレビューが切り替わる。
幾何計算の回帰検証はUnityディレクトリの `Tests/RoadSnapping/run.ps1`。

## 11. Ctrlラジアルメニューとカーソル設定HUD

パス敷設中（`RoadPlanManager` のセッション中）の設定操作は、3Dビュー内で完結する。

- **ラジアルメニュー（`UI/RadialMenu/RadialMenuController`）**: Ctrl押下でカーソル位置から
  項目アイコンが円形に展開する。マウスを動かして項目をハイライトし、Ctrlを離すとその項目をトグルする。
  中心付近（デッドゾーン）で離した場合や無効項目（曲線不可バリアントの曲線トグル等）は何もしない。
  道路編集の項目は8個: 直線/曲線・ノード吸着・パス吸着・直線延長吸着・直角吸着・Parallel・Align Ends・グリッド(表示+吸着)。
  ParallelとAlign Endsは隣り合わせの固定位置で、使用不可のときはグレー表示になる。
  項目定義（`RadialMenuItem`: アイコン/状態/有効条件/トグル処理をFuncで渡す）は編集モード非依存の
  汎用設計で、他の編集モードからも流用できる。
- **カーソルHUD（`UI/CursorStatusHud`）**: セッション中はカーソル脇に現在設定のアイコン列
  （ノード選択中の移動軸矢印・曲線/直線・吸着7種。OFFは半透明）と標高・盛土のテキスト（`H +3m  E +1m` 形式）を常時表示する。
  表示内容は登録されたproviderを毎フレームpollする汎用部品で、ラジアルメニュー表示中は自動で隠れる。
- **入力ゲート**: メニュー表示中は `RadialMenuController.IsOpen` を `InputHandler`（クリック・ホバー・
  ドラッグ全般）と `CameraController`（回転・パン・移動・ズーム）が参照して入力を非破壊的に停止する
  （フライモードゲートと同方式）。メニューのCanvasには GraphicRaycaster を意図的に付けていないため、
  EventSystem のUI判定では止まらないことに注意。マウスボタン押下中（ドラッグ・回転中）はメニューを開かない。
- **起動役（`Editors/RoadEditor/RoadEditControlMenu`）**: `RoadPlanManager` と同居し、
  `IsSessionActive` をポーリングしてHUDの登録/解除とCtrl検知を行う。
  曲線モードのトグル結果は `edit_path:update_shape` の `is_curved` としてUIへ逆流し、
  UI側の確定送信（`curved`）・形状プレビュー・コスト見積りが追従する。

起動役のポーリング・HUD登録/解除・Ctrl検知でのメニュー開閉・自分が開いたメニューの後始末は
共通基底 `Editors/EditControlMenuBase` にあり、各起動役は「アクティブ判定」「メニュー項目」「HUDの内容」だけを与える。
同じ仕組みを地区編集（`ZoneEditControlMenu`。7章）と土地区画作成（`ParcelEditControlMenu`。12章）でも使う。

## 12. 土地区画作成の操作モデル

土地区画作成モード（`STATE_EDIT_PARCEL`）は「作成方法の切り替えは3Dビュー、数値と確定はパネル」という分担になっている。
作成方法の真実源は `ParcelEditorManager.CurrentMode` で、`ParcelEditControlMenu`（Ctrlラジアル）で切り替える。

| 作成方法 | 3Dビューでの操作 |
|---------|----------------|
| 道路沿い（`along_path`） | 区画を作る位置の街路をクリック（クリック地点にマーカーが出る） |
| ブロック一括（`block`） | 街区内の地点をクリック。周辺の道路側面に沿って区画をまとめて生成する |
| ランダム（`random`） | 地点をクリック（矩形の探索範囲。ドラッグで角度） |
| 単一区画（`empty_area`） | 地点をクリック。続けてドラッグすると角度を変えられる |
| 矩形分割（`split_rectangle`）／ランダム分割（`split_random`） | 分割する既存区画をクリック |

新規作成の4項目はパネル・Ctrlラジアルとも上表の順。任意形状は単一区画の作成後に形状編集で調整する。ブロック一括の中央は空地として残り、設定には探索範囲・間口・奥行き・ばらつきを用いる。

- 作成方法を切り替えると入力プリセットと選択状態がその場でリセットされ、`edit_parcel:set_mode` でパネルへ通知される。
- **Esc は選択のリセット**で、作成モードからは抜けない（同じ方法で選び直せる）。パネル側の選択表示も
  `edit_parcel:set_mode`（モード据え置き）で一緒にリセットされる。
- **確定してもモードから抜けない**。パネルの確定後に届く `edit_parcel:reset_selection` で選択だけを捨て、
  そのまま次の地点／道路／区画をクリックして連続作成できる。
- カーソル脇のHUDには現在の作成方法と次の操作（未選択なら「地点をクリック」等、選択済みなら「パネルで確定」）を出す。
- **道路沿いに作成は「パス全体」と「クリック地点」の2つの作成範囲を持つ**（切り替えはパネル）。
  クリック地点モードでは、クリックした点をパス中心線へ射影したアンカーが `edit_parcel:select_path` の
  `position` としてパネルへ渡り、その周辺の1〜5区画だけが作られる。アンカーの位置は3Dビュー上に
  「道路を横切る棒＋円」のマーカーで示される。
  作られる形状はパス全体で作った場合とまったく同じ分割グリッドから選ばれるため、道路沿いを何度
  クリックしても最終的に全体作成と同じ整然とした区画列になる（クリック地点を中心に帯を置く方式だと
  隣の区画との間に使えないスリバーが残る）。
### 土地区画の形状編集

既存区画の形状編集は`ParcelShapeEditManager`が保存済みポリゴンを読み込み、共通基底`PolygonAreaEditManager`が頂点ドラッグ・辺クリックでの頂点挿入・右クリックでの頂点削除を扱う。この共通基底は天然資源エリアの作成と造成範囲の作図（`GradingAreaEditManager`）にも使う。

頂点ドラッグのXZは、道路編集と同じく`InputHandler`がカメラレイと地形の交点から求めた`worldPos`を使う（地形にヒットしなければY=0平面との交点）。`Utils.GetGroundHeight`で高さを合わせる。固定されたカメラ奥行きで逆投影してからYだけ地面に戻すと、斜め視点でカーソルと頂点がずれるため、この変換は行わない。

土地区画の形状編集は、頂点移動・辺への頂点挿入とも吸着なし固定。道路・ノード・グリッドなどへの吸着は行わず、Ctrlメニューなどで吸着を切り替える操作も持たない。`TerrainInputHandler`はこのモードのクリック・右クリック・ホバーで共通吸着を省略し、`ParcelShapeEditManager`は`UseDragSnapping=false`でドラッグ時も吸着を省略する。道路編集の共有吸着設定は書き換えず、天然資源エリア編集では共通吸着を使う。

造成範囲の作図も同じく吸着なし固定で、`TerrainInputHandler`は`MODE_EDIT_GRADING`のクリック・右クリック・ホバーで共通吸着を省略し、`GradingAreaEditManager`は`UseDragSnapping=false`でドラッグ時も省略する。ホバーで吸着した座標をカーソル位置へ書き戻すと後続のドラッグにも混入するため、ドラッグ側だけで無効にしても足りない。

#### Placing中のポインタマーカー

共通基底`PolygonAreaEditManager`は、Placing（頂点を順に置いている間）だけ、次の左クリックで頂点が置かれる位置に円のマーカーを出す。範囲が3頂点未満で形がまだ見えない間も、置く場所を確かめられる。Editing では出さない（頂点と辺のホバー強調が代わりを務める）。

- 位置は`TerrainInputHandler`が`OnTerrainHover`へ渡すワールド座標のXZ。高さは`Utils.GetGroundHeight`で地面へ合わせるので、海上は頂点の円と同じく海底の位置に出る。描画は頂点と同じスクリーン座標のオーバーレイで、円は頂点より少し大きく、色は頂点と同じ。
- 造成範囲と土地区画の形状編集は吸着なし固定なので、マーカーはクリックで置かれる頂点とXZが一致する。天然資源エリアは共通吸着を通し、ホバーもクリックも同じ吸着を掛けるため、マーカーは吸着後のXZを指す。
- `onHover`はカーソルのレイが変わったフレームか入力のあるフレームにだけ呼ばれる。UI上へ出る・カメラをドラッグする・ラジアルメニューを開くなどで`InputHandler`が入力を止めたときは`onHoverEnd`が呼ばれ、`TerrainInputHandler`がこれを`OnTerrainHoverEnd`へ中継してマーカーを消す。再びカーソルが動くまで再表示しない。

## 補正済み線路への標高吸着

線路の保存設計標高がある場合、吸着位置の絶対標高はその基準線から評価する。地上線路でも小数の相対高さを引き継ぎ、ドラッグ時にも接続先の高さを優先する。複線の中心線へ横移動するときは絶対標高を保ち、移動先の地形との差から相対高さを算出する。接続先に吸着できた鉄道は直線・直角補助で再移動しない。描画点列のYは高さ源にしない。高さ編集可能なバリアントの数値入力は小数対応で、ステッパーは0.1m刻み。

## 13. 建物の配置方法と配置前の寸法調整

建物建設中はCtrlを押すとカーソル位置にラジアルメニューが開き、項目へ合わせて離すと配置方法を選ぶ。中心で離すと変更せず、Escで閉じる。メニュー中は建物操作・カメラ操作を止める。

| 配置方法 | 操作 |
| --- | --- |
| 区画に配置(既定の初期値) | 既存区画をクリック。地形クリックでは直接配置へ自動遷移しない |
| 自由配置 | 区画の有無に関係なくカーソルへ矩形を追従させ、中ボタンドラッグで回転。カーソルが道路の上にある間だけ、その道路のカーソル側の外縁へ位置・角度を吸着 |
| 道路沿い配置 | 近くの地上道路の外縁へ位置・角度を吸着。遠ざかると吸着を解除して追従 |

初期値はUIが `STATE_EDIT_BUILDING` の `placement_mode`（parcel/free/roadside）で指定でき、省略時は区画配置。市庁舎の配置（都市創設時と移転）はUIが `free` を指定して自由配置から始める。創設時は道路も区画も無いのが通常で、移転ではカーソルを道路に乗せれば道路の脇へ置ける。自由/道路沿い配置では区画の入力をMODE_NONEにし、地形へクリックを通す。既存建物編集と忠実コピーは区画配置専用。直接配置が既存区画と重なる場合も、設置可否・買収確認はバックエンドの既存ルールに従う。

寸法は建物ローカルの間口・奥行きで示す。`1/2`で間口の増減、`3/4`で奥行きの増減(各1m)、Shift併用で5m。長押しは0.35秒後に0.075秒間隔で繰り返す。範囲は1〜500mで、resizable=falseの施設は固定。3Dビューにフォーカスがあるときだけキーを受ける。パネルは直接配置を選んだ時点から寸法入力を表示する。寸法線・操作中の辺の強調・カーソルHUDで対応を示す。

左クリックは位置固定。固定後も同じキーとパネルで寸法を調整でき、建設確定はパネルが行う。Escまたはパネルの取消しは寸法・配置方法を保って位置選びへ戻る。固定後に自由⇔道路沿いを切り替えても位置は移動しない(道路の再選択はEscで行う)。区画配置へ切り替えると直接配置の位置選択を解除する。

道路吸着は`PathManager.QueryNearbySegments`の近傍区間を重複除去して評価する。接道を許可した街路/幹線道路で、地形追従かつ区間両端が地表から0.3m未満のものだけが対象。線路・高架・地下は対象外。`width`は歩道込みなので中心線からwidth/2を外縁とし、接触誤差回避の0.05mを空ける。道路沿い配置は、候補中心からカーソルまでの距離が「奥行き/2+12m」以内なら吸着候補にし、近い候補を優先する。自由配置は、カーソルから中心線までの距離がwidth/2以内(路面の上)の区間だけを候補にし、中心線に近い道路を優先する。どちらも直前の道路・区間・側を優遇し、道路中心の1m以内では直前の側を保つ。

曲線は近接区間の接線へ矩形を回転する。矩形と周辺の地上道路コリドーの干渉を辺まで調べ、曲線内側や交差点に食い込む候補を除外する。吸着対象の道路縁と道路側の辺中央を黄色で表示する。吸着中のサイズ変更はこの辺中央を保持し、間口は左右へ、奥行きは道路から離れる側へ広げる。固定後の拡大で干渉が生じても位置を勝手にずらさず、バックエンドの設置可否結果で確認する。道路沿い配置では手動回転を無効化する。自由配置では、位置決め中に吸着している間は道路が向きを決め、手動の回転(中ボタンドラッグ・パネルの回転角)は道路から外れたときの向きとして覚える。位置を固定した後に回すと吸着を外す。

配置状態の正は`BuildingPlanManager.Placement.cs`。パネルは変更差分を`placement_command`で送り、Unityは`placement_state`で位置・寸法・角度・世代を同時に返す。配置前のゴーストはローカル即時描画であり、設置可能の保証ではない。位置固定後にパネルがpreview-directを呼び、現在の要求と一致する検証結果が届くまで建設確定を無効化する。古い形状応答はplacement_revisionと階数の照合で破棄する。

輪郭・寸法線・道路吸着マーカーは地表高+0.05mのワールド座標で生成する。`BuildingPlacementGuide`は親を持たない原点・回転なし・等倍の描画オブジェクトとし、編集用オブジェクトの位置・回転・拡縮を頂点へ重ねて適用しない。コントロール無効化時は非表示にし、破棄時はガイドと所有するメッシュ・マテリアルも破棄する。

検証はUnityの`Tests/BuildingPlacement/run.ps1`(幾何計算)と`run-unity.ps1`(独立したUnityで本編コンポーネントの状態遷移と、編集用オブジェクトの移動・回転・拡縮に対するガイド座標の不変性)。

## プロップの自由配置

MODE_EDIT_PROPはクリック・ホバーともTerrainInputHandlerの共通吸着を通さず、地形の任意XZをPropPlacementManagerへ渡す。他オブジェクトの入力を貫通し、個別配置ではパネルで指定した地面相対高さを加算する。既存の建物・道路・プロップとの干渉判定や高さによる種別制限は行わない。

個別配置の3D側操作は`PropPlacementManager.Update`が受ける。中ボタンドラッグで回転(ドラッグ中はゴースト位置を固定し、左クリックの打点を無視)、PageUp/PageDownで高さ(0.1m刻み、Shiftで1m、長押しリピート)。中ボタン回転・キー長押しリピート・入力ブロック判定(ウィンドウ非フォーカス・ラジアルメニュー・カメラのフライモード/ドラッグ・UI上のポインタ)は`Editors/PlacementInput.cs`の`MiddleDragRotation`/`KeyRepeat`/`PlacementInput.IsBlocked`で、建物の直接配置(`BuildingPlanManager`)と共有する。変更後の値は`edit_prop:placement_settings`でパネルへ返す。

## プロップの削除(ブルドーザ)

`MODE_REMOVE`中、`InputHandler`はクリック候補の発火前に`RemovalManager.TryRemovePropAt`(画面レイ+ヒット物までの距離)を呼ぶ。個別配置は即DELETE、範囲配置はUIへ確認を求めて回答待ちにする(`remove_object:request_confirm`/`confirm_result`)。ホバーはレイが変わったフレームで`UpdatePropHover`を呼び、`RemovalManager`が`OverlayMeshRenderer`で対象のAABBと(範囲配置なら)散布範囲の外周を赤く描く。

## 建物・道路・区画の削除(ブルドーザ)

プロップ以外の対象は、`MODE_REMOVE`中の左クリックを各対象の入力ハンドラ(`ParcelInputHandler` / `BuildingInputHandler` / `PathInputHandler`)が`RemovalManager.TryRemove(type, id)`へ渡す。削除はクリックのみで、ドラッグでは削除しない。可否はUnity側のキャッシュで事前判定し(ホバーのハイライトも同じ判定)、通った対象へ`DELETE /agents/{id}`・`/paths/{id}`・`/parcels/{id}`を直接発行する。最終的な可否はバックエンドのガードが決める(拒否された場合は警告ログだけで、処理済みの集合には残す)。同じ対象への二重発行は`(type, id)`で防ぎ、成功後は`CellDataLoader.ReloadAllData`を0.35秒デバウンスして1回だけ呼ぶ。区画だけは成功時に即ローカル除去する。

| モード | 区画 | 建物 | 道路 |
| --- | --- | --- | --- |
| 通常 | 空き地(`usage_id` なし) | `PENDING` / `SCHEDULED_CONSTRUCTION` | `TEMPORARY` / `SCHEDULED_CONSTRUCTION` / `PENDING` |
| 即時削除(`instant_remove`) | 通常と同じ | ステータスを問わない。ただし `TYPE_CITY`(市政府・中央銀行)と `TYPE_DEFAULT`(代理主体・国境検問所)を除く | ステータスを問わない。ただし `PATH_TRANSPORT`(公共交通の区間。路線の持ち物)を除く |

即時削除モードはテンプレート作成モード(神モード)用で、UIが`STATE_REMOVE_OBJECTS`の`instant_remove`で開始する。**Unityは神モードかどうかを知らない**ので、バックエンドが神モードのときだけ稼働中の建物・道路の削除を受け付ける。確認ダイアログは出さず、取り消しもできない。バックエンドは建物をテナントの経済主体・世帯の市民ごと消して区画を空き地に戻し、道路は削除後に端点が孤立したノードも片付ける(地形は元に戻さない)。路線や駅舎に属する区間は、路線側の既存の連鎖で整合を取る。範囲配置のプロップだけは、このモードでも従来どおりUIの確認を経てから範囲ごと消す。

`instant_remove` はブルドーザの開始ごとに決まり、`EndRemovalMode`で下ろす。次の`STATE_REMOVE_OBJECTS`で指定がなければ通常モードに戻る。

## 道路・線路の曲率と勾配の警告

敷設中は違反区間を赤線で示し、その区間の最悪位置へ引出線付きラベルを表示する。曲線アイコンには区間の最小半径Rと下限、坂アイコンには区間の最大勾配Gと上限を併記する。同じ区間が両方に違反すれば両方を表示する。マウス横には全体のR/Gを表示せず、高さ・盛土・設定アイコンだけを残す。

評価用の曲線は操作中のプレビューと同じ制御点から作る。配置中はカーソルを仮終点として、直前区間の接線変化も含める。曲線の長さに応じた密な標本を作り、全長を約1m等間隔へ再標本化する。粗い描画用折れ線の折れ角を曲率として測らない。標高に描画用の浮かせ量は含めず、勾配を取る前にマスターの `smooth_factor`（最小10m）の移動平均を適用する。

区間ラベルと赤線は操作中の中心線の推定値。地形、保存された設計標高、複線の各線についての最終判断はバックエンドが行う。Unityの線形警告だけでは確定を禁止せず、パネルは最新の形状に対する全線の検証成功を待って確定を許可する。急カーブ・急勾配はこの事前検証と登録時検証で拒否する。

例外はロープウェイ（[人員搬送設備](../economics/30_人員搬送設備.md) §30.3）の水平折れ角で、各ウェイポイントで前後の区間の水平進行方向の変化を5度以内に制限する。地形に依存しない確定的な判定のため、Unityは配置中のホバー位置も含めて赤線と角度ラベルを出し、編集中の違反は `can_confirm=false` で確定を直接禁止する。判定規則はバックエンドと同一に保つ（上下の勾配は含めず、許容誤差は0.001度、水平に重なる点は無効）。

### 高架の制御点と縦断形状

制御点の高さは、自動計算でも手動調整済みでも、そのポイントの自動接線（前後のポイントを結ぶ向き。端点は隣のポイントへの向き）の縦断勾配に、ポイントから制御点までの水平距離を掛けて決める。入口・出口の制御点は同じ勾配になるので、ハンドルの長さが左右で違ってもポイントで縦断が折れない。ハンドルのドラッグや接続方向への整列が変えるのは平面形状だけで、ポイントや隣のポイントの高さを変えれば、そのポイントの勾配も追従する。制御点直下の丘や谷は非追従の高架の形状へ影響させない。

例外は経路提案の結果で、探索が勾配の制約込みで決めた制御点の高さを保つ（[経路提案](経路提案.md)）。この固定は、そのポイントか隣のポイントを動かす・挿入や削除で隣が変わる・そのポイントのハンドルをドラッグする・曲線/直線を切り替えると解除され、上の規則に戻る。

保存・読み込みでは、制御点の相対高さを所属する頂点直下の地盤から測る。これはバックエンド・通常描画と共通の基準であり、編集プレビューの絶対標高が検証・保存後にも再現される。端点の制御点と中間頂点の制御点に同じ規則を適用する。

通常鉄道の各線路バリアント（地上・高架・地下対応・電化・駅ホーム）の許容最小曲線半径は100m、最大勾配は4.5%。正本はpath_masterシートのmin_curve_radius列とmax_grade列。道路・モノレールの制限は個別設定に従う。

区間ラベルの値は小数2桁で表示し、半径は「下限○m未満」、勾配は「上限○%超過」と併記する。判定には丸め前の値を使う。`H` は選択点または次回配置点の地面相対高さであり、全アンカーの絶対標高を水平に固定する値ではない。

曲率のローカル評価は始点を原点とする相対座標でベジエ補間・等間隔再標本化・半径計算を完了させ、警告線の描画時だけワールド座標へ戻す。大きなワールド座標をfloatで補間してから差分を取ると、端部の1m窓で丸め誤差が増幅され、緩曲線を急曲線と誤認するため。地形サンプルだけは元のワールド座標で行う。

区間ラベルは `RoadPlanManager.AlignmentLabels.cs` が管理する。原因別の連続違反区間を集計し、各区間の最悪値の位置へ引出線をつなぐ。画面外・背面・編集終了時・ラジアルメニュー表示中は描かない。投影位置が重なるラベルは位置をずらす。アイコンは線で手続き描画し、外部素材・クリック受付は追加しない。形状変更で集計と文字列を無効化する。


高架など地形に追従しない区間の勾配は、線路の約1m線分の標高差÷実水平距離で求めます。地形追従区間だけは地形の量子化段差を除くため造成用の平滑化を行います。高架には80m等の造成用平滑化を掛けないため、隣の坂の影響で水平区間まで勾配警告が広がりません。

勾配の上限比較には0.001パーセントポイントの数値誤差を許容し、上限ちょうどの線路を標高差分の丸め誤差で拒否しません。


造成後の地上路面は、地形を距離幅の移動平均で平滑化した標高を使う。端部は端点を中心に地形を点対称に延長して平均するため、曲線の外挿で終端だけが沈んだり跳ね上がったりしない。短い坂も一定勾配を保つ。この標高はバックエンドの造成プレビュー・勾配検証・施工で共通であり、操作停止後の3Dモデルと勾配ラベルにも反映される。

駅舎の通路方式は建設パネルの上空/地上/地下から選び、配置中にもプレビューへ反映する。建屋配置端はパス建設パネル（PathConstructionPanel）で駅舎を新しく敷くときだけ選択する。既存形状編集は保存済みhouse_endsとpassage_modeを保持する。

## ドライブの入力

Cockpitから`drive:open`を受けると`DriveMode`が開始位置の選択と乗車を扱う。既存InputMode定数は増やさず、`InputHandler`の通常クリック/ホバーを専用ゲートで停止する。開始位置はドラッグ移動5px未満のクリックだけを受け、選択中のカメラ移動は許可する。走行中は矢印キーを分岐選択、Space長押しを5倍速、Escを終了に使う。鉄道は路線に追従するため分岐操作を持たない。カメラの保存は選択UI表示中から止め、終了時に元の視点へ戻す。`SetView`はドラッグ状態と慣性を破棄する。詳細は[車両の走行描画](../geometries/O_車両の走行描画.md)を参照。

## 撮影モード

動画撮影向けのカメラ操作は`CameraController.Cinematic.cs`にまとめ、通常のカメラ制御と同じコンポーネントで扱う。
右上の`MinimapToggleButton`を`CameraController.cinematicModeButton`へ接続し、クリックでモード1を開始する。撮影中の再クリックは終了する。
`InputMode`やIPCのステートは増やさず、`ModeTextDisplay`に現在のモード・再生/一時停止・反転・操作案内を表示する。表示枠は撮影中だけ高さを広げ、終了時に元へ戻す。

| 操作 | 両モード共通 |
| --- | --- |
| Shift（左右どちらでも） | モード1と2を切り替える。押下ごとに1回だけ切り替え、一時停止・反転は維持する |
| Space | 自動運動の一時停止/再開 |
| Backspace | 自動運動の向きを反転。カメラの視線を反転させる操作ではない |
| Esc | 現在の位置・向きを保って通常操作へ戻る |

**モード1（Orbit）**は開始時とモード2からの切替時に、画面中央のレイから回転中心を決める。
地面・建物・海面のうち最も近い交点を採用し、区画や道路の選択用コライダー・トリガーは対象外にする。
地形コライダーが無い遠景では、通常の回転と共通の`RaycastTerrainHeightField`で地表交点を求める。
交点を得られなければ移動せず待機し、Spaceで再取得、Shiftでモード2へ切り替えられる。
回転中心・距離・仰俯角を固定してワールドY軸まわりに毎秒3度で周回する。地形が再ロードされても中心を取り直さない。
通常の右ドラッグは地表だけを中心とし、撮影とは対象が異なる。軌道の計算は`InitializeOrbit`と`ApplyOrbitPosition`を共用し、撮影開始時には通常の右ドラッグ用ピッチ制限を掛けない。

**モード2（Travel）**は切替時の視線をXZ平面に投影した方向へ毎秒5mで移動する。
水平前方の取得は既存の`Utils.GetCameraHorizontalForward`を使う（真上・真下の場合は+Z）。カメラの向きは自動で変更しない。

| モード2の操作 | 動作 |
| --- | --- |
| A / D | 押下1回につき進行方向だけを左/右へ5度変更。長押しによる連続回転はしない |
| 方向キー | カメラ基準の水平前後左右へ毎秒20mで手動移動。自動移動へ加算する |
| R / F | ワールドY方向へ毎秒5mで上昇/下降 |

方向キーとR/Fは一時停止中も利用できる。地形の起伏に合わせてYを自動補正せず、R/F以外では絶対高度を維持する。Fによる下降は通常カメラと同じY=0を下限とする。
4種類の速度はInspectorの「撮影モード設定」で調整する。経過時間は`Time.unscaledDeltaTime`を使い、ウィンドウ非フォーカス中は撮影操作と自動運動を止める。
マップ範囲の制限は既存の`ClampPositionToCityBoundary`を共有する。

撮影中は通常のWASD/QE・マウス操作・Shift+Fを処理しない。UIボタンの選択を解除し、Spaceや方向キーがSubmit/Navigationとして再実行されることを防ぐ。
撮影とドライブは`InputHandler.IsNormalMode`で編集との排他を判定し、撮影中に編集へ切り替わった場合も撮影を終了する。
フライモードへ切替、ドライブの開始、`SetView`による視点配置、セッション切替、コンポーネント無効化でも撮影を終了する。
開始・終了時は共通の`CancelCameraGestures`でドラッグ・慣性・フォーカスを打ち切り、通常操作に戻る高さを同期する。
視点保存とセルロードは通常の`CameraController.LateUpdate`と`CameraCellChecker`を引き続き使用する。

回帰検証は`unity/Tests/Camera/run-unity.ps1`。独立したUnityで物理コライダーと実際のInput Systemのキー入力を使い、回転中心・姿勢・停止/反転・モード切替・入力の排他を確認する。
