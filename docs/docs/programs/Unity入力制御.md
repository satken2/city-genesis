# Unity入力制御

## 1. 概要

Unityプログラムにおけるユーザーのマウス・キーボード入力は、`InputHandler` が中央管理している。
毎フレーム、カメラからRayを飛ばしてマウス直下のオブジェクトを検出し、オブジェクトの種類と現在の入力モードに応じて適切なハンドラにイベントをディスパッチする。
カメラのドラッグ移動は `CameraController` が独自に処理し、`InputHandler` との競合を回避する仕組みを持つ。

## 2. オブジェクトタイプと入力モード

### 2.1. オブジェクトタイプ

Rayが命中したオブジェクトはTagまたはコンポーネントの有無によって6種類に分類される。

| 定数 | 値 | 判定方法 |
|------|------|----------|
| `BUILDING` | 1 | Tag `BuildingObject` |
| `PARCEL` | 2 | Tag `ParcelObject` |
| `PATH` | 3 | Tag `PathObject` |
| `NODE` | 4 | Tag `PathNode` |
| `TERRAIN` | 5 | `Terrain` コンポーネントの有無 |
| `ZONE` | 6 | Tag `ZoneObject` |
| `PROP` | 7 | 地形プロップ（配置は地形クリックで行うため専用タグ判定はまだ持たない） |

`MODE_NONE` に設定されたオブジェクトタイプはRayを貫通し、背後にあるオブジェクトが選択対象となる。

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
| `MODE_UNLOCK_CELL` | 9 | 地形セル開放モード |
| `MODE_PARCEL_SHAPE_EDIT` | 10 | 土地区画形状編集モード |
| `MODE_SET_GRID` | 11 | グリッド角度設定モード |
| `MODE_EDIT_RESOURCE` | 12 | 資源エリア編集モード |
| `MODE_EDIT_TRANSIT` | 13 | 公共交通設置モード |
| `MODE_EDIT_PROP` | 14 | プロップ配置モード（個別配置・範囲散布） |

接続点編集（`STATE_EDIT_CONNECTOR`）では専用のモード定数を追加せず、既存の `MODE_EDIT`（=7、既存オブジェクトの編集モード）を NODE に適用して接続端子ノードをドラッグ移動する。

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
| `UNLOCK_CELL` | UNLOCK_CELL | NONE | NONE | NONE | NONE | NONE |
| `PARCEL_SHAPE_EDIT` | PARCEL_SHAPE_EDIT | NONE | NONE | NONE | NONE | NONE |
| `PROP_PLACEMENT` | EDIT_PROP | NONE | NONE | NONE | NONE | NONE |
| `CONNECTOR_EDIT` | NONE | NONE | EDIT | NONE | NONE | NONE |

表の「—」はプリセットで明示的に設定されないオブジェクトタイプを示す（前回の設定が維持される）。

`CONNECTOR_EDIT` は `STATE_EDIT_CONNECTOR`（接続点編集）で適用され、NODE のみを `MODE_EDIT`（ドラッグ移動）にして施設の接続端子ノード（`infra_node_id` / `depot_node_id`）を移動できるようにする。対象ノードの強調表示と、非電化の路線専用ノードへの `NodeInputHandler` 後付けは `ConnectorEditManager` が担う。ドラッグ確定時は `NodeInputHandler` が `PATCH /api/v1/nodes/{node_id}` を送り、パスが接続済みのノードはバックエンドが移動を拒否して元位置に戻る。詳細は `IPC連携の整理.md` の `STATE_EDIT_CONNECTOR` を参照。

`PROP_PLACEMENT` は `STATE_EDIT_PROP`（プロップ配置）で適用され、地形クリックでプロップを配置する。`IpcStateCoordinator.ApplyStateEditProp` が `PropPlacementManager` を動的生成し、個別配置（`edit_prop:update_instances`）／範囲散布（`edit_prop:update_shape`）のモードごとに処理する。詳細は `IPC連携の整理.md` の `STATE_EDIT_PROP` を参照。

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
- 地区編集モード中は `CameraController` の左ドラッグ移動を無効化する。

### 3.3. 地区ドラッグ

地区編集モード（`MODE_ZONE_EDIT`）では、地形または道路オブジェクト上で左ドラッグを開始した際に、マウス位置が地区ポリゴン内であれば地区ドラッグとして処理される。
地区ドラッグ中はホバーしている道路をハイライトし、ドラッグ終了時にその道路を地区に追加する。
地区ドラッグはノードドラッグや地形ドラッグよりも優先される。

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

| モード | 左クリック | 右クリック | ホバー |
|-------|-----------|-----------|-------|
| `MODE_NORMAL` | ログ出力 | — | スナップ位置計算・マウス位置上書き |
| `MODE_ROAD_CONSTRUCTION` | `RoadPlanManager.OnTerrainLeftClick` に委譲 | 同右 | `RoadPlanManager.OnTerrainHover` に委譲 |
| `MODE_ZONE_EDIT` | 何もしない | — | 地区ポリゴン内判定・ツールチップ表示 |
| `MODE_BUILDING_CONSTRUCTION` | `BuildingPlanManager.OnTerrainClickDirect` に委譲（直接配置=区画外地点の確定） | — | `BuildingPlanManager.OnTerrainHoverDirect` に委譲（直接配置=カーソル追従の矩形ゴースト表示）。ホバー離脱時 `HideDirectGhost` |
| `MODE_UNLOCK_CELL` | セル座標を計算してUIに通知 | — | — |
| `MODE_PARCEL_CREATION_POINT` | 土地区画エディタに点を通知 | — | 配置マーカー表示 |
| `MODE_PARCEL_SHAPE_EDIT` | `ParcelShapeEditManager.OnTerrainLeftClick` に委譲 | 同右 | 頂点ホバー判定 |
| `MODE_EDIT_RESOURCE` | `ResourceAreaEditManager.OnTerrainLeftClick` に委譲 | 同右 | 同 `OnTerrainHover` に委譲 |
| `MODE_EDIT_TRANSIT` | `TransitPlanManager.OnTerrainLeftClick` に委譲 | 同右 | 同 `OnTerrainHover` に委譲 |
| `MODE_EDIT_PROP` | `PropPlacementManager.OnTerrainLeftClick` に委譲（個別=1個配置／範囲=頂点追加） | 同右（個別=直近取消／範囲=直近頂点取消） | 同 `OnTerrainHover` に委譲（個別=ゴースト追従） |

### 4.2. PathInputHandler（道路）

道路オブジェクトのクリック・ホバーを処理する。地区ドラッグに関する追加メソッドを持つ。

| モード | 左クリック | 右クリック | ホバー |
|-------|-----------|-----------|-------|
| `MODE_NORMAL` | 道路詳細パネルをIPC通知 | — | ハイライト表示 |
| `MODE_ZONE_EDIT` | 道路を地区に追加・選択トグル | — | 地区プレビュー色でハイライト |
| `MODE_PARCEL_CREATION_PATH` | 土地区画エディタに道路選択を通知 | — | 選択可能な道路のみハイライト |
| `MODE_ROAD_CONSTRUCTION` | 地形クリックとして処理 | 同右 | ハイライトをスキップ |

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

### 4.5. BuildingInputHandler（建物）

建物オブジェクトのクリック・ホバーを処理する。

| モード | 左クリック | ホバー |
|-------|-----------|-------|
| `MODE_NORMAL` | エージェント詳細パネルをIPC通知 | 3Dメッシュのハイライト |
| `MODE_BUILDING_CONSTRUCTION` | 建物の解体をバックエンドにPOST | — |

## 5. ドラッグの種類と処理フロー

`InputHandler` はドラッグの種類を以下の優先度で判定する。

### 5.1. 地区ドラッグ（最優先）

条件：地形または道路上でドラッグ開始、かつマウス位置が地区ポリゴン内、かつ地形モードが `MODE_ZONE_EDIT`。

1. `InputHandler.StartZoneDrag(zoneId)` が呼ばれ、UIにドラッグ開始を通知
2. ドラッグ中、`InputHandler.UpdateDrag()` でホバーしている道路に `PathInputHandler.onZoneDragHover` を呼び出し
3. ドラッグ終了時、`InputHandler.EndDrag()` で道路に `PathInputHandler.onZoneDragEnd` を呼び出し、地区に道路を追加

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

道路編集中の場合、ドラッグ開始前に `RoadPlanManager.IsMouseOverEditHandle` で制御点上かどうかを判定する。
制御点上でなければ地形ドラッグとして道路のポイント移動を開始する。

### 5.4. カメラドラッグ

`CameraController.HandleMouseDragPan` が地形を左ドラッグでカメラ移動を行う。
以下の条件ではカメラドラッグを開始しない。

- UI上をクリックした場合
- 右クリック回転中の場合
- 編集ハンドル上をクリックした場合
- 地区編集モード中の場合

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
確定ボタンでバックエンドにデータを送信する。

道路エディタは `InputHandler` から入力イベントを受け取るほか、独自の `Update` ループでホバー状態の更新と高さ入力を処理し、`LateUpdate` でオーバーレイの描画を行う。

標高（PageUp/PageDown または 1/2 キー、±1m）と盛土（3/4 キー、±1m）はキーボードで変更する。
`Editing` フェーズで選択中ノードがあればそのノードの値を、選択がない場合（`Placing` フェーズを含む）は
「次に配置するノード」に適用される値（`currentHeight` / `currentEmbankmentHeight`）を変更する。
標高・盛土は道路建設パネルのステッパーからも変更でき（`edit_path:change_node` / `edit_path:change_embankment`）、
既存ノードを選んで数値で微調整する用途に使う。キー操作・選択で変わった値は `edit_path:select_node` で
パネルへ返り、双方向に同期する。曲線/直線と吸着の切り替えはCtrlラジアルメニュー（後述の11章）で行う。

## 7. 地区編集の状態管理

`ZoneEditContext` は地区編集モードの状態を管理するシングルトンである。
新規作成モードと既存編集モードの2つの状態を持つ。

| 状態 | `IsActive` | `CurrentZoneId` | 意味 |
|------|-----------|----------------|------|
| 非アクティブ | `false` | `null` | 地区編集モードでない |
| 新規作成 | `true` | `null` | クリック・ドラッグで新規地区に道路を追加 |
| 既存編集 | `true` | `int` | クリック・ドラッグで既存地区に道路を追加 |

新規作成モードでは、1本目の道路をクリックすると追加選択フェーズ（`isInClickMultiSelect`）に入り、
以降のクリックで接続された `PATH_STREET` を追加・トグルできる。
既存編集モードでは、クリックで道路を選択し、ドラッグで道路を地区に追加する。

## 8. 土地区画形状編集のドラッグ処理

`ParcelShapeEditManager` は既存の土地区画のポリゴン頂点をドラッグで編集する機能を提供する。
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
| `STATE_EDIT_ZONE` | `ZONE_EDIT` | `ZoneEditContext.EnterCreateMode` または `EnterEditMode` |
| `STATE_EDIT_PARCEL` | プロパティに応じて切替 | `ParcelEditorManager` の初期化 |
| `STATE_EDIT_PARCEL_SHAPE` | `PARCEL_SHAPE_EDIT` | `ParcelShapeEditManager.InitializeEditMode` |
| `STATE_UNLOCK_CELL` | `UNLOCK_CELL` | — |
| `STATE_EDIT_PROP` | `PROP_PLACEMENT` | `PropPlacementManager` の初期化（個別配置／範囲散布） |
| `STATE_EDIT_CONNECTOR` | `CONNECTOR_EDIT` | `ConnectorEditManager.EnterEditMode`（接続端子ノードの強調＋ドラッグ有効化） |

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

吸着のON/OFFフラグ（`TerrainInputHandler.Enable*Snapping` の static 変数群）の真実源はUnity側にあり、
切り替えは後述のCtrlラジアルメニューで行う（UIパネルからの設定イベントは存在しない）。
グリッド吸着はグリッド表示と連動しており、UIパネルの `view:set_grid`・ラジアルメニューのグリッド項目の
どちらで表示を切り替えても、`GridOverlay` が表示状態に合わせて吸着を自動でON/OFFする。
グリッド角度設定モード（`STATE_SET_GRID`）中は `GridSetManager` が吸着フラグを退避して
「ノード吸着のみON」に切り替え、モード終了時（確定・キャンセルとも）に復元する。

## 11. Ctrlラジアルメニューとカーソル設定HUD

パス敷設中（`RoadPlanManager` のセッション中）の設定操作は、3Dビュー内で完結する。

- **ラジアルメニュー（`UI/RadialMenu/RadialMenuController`）**: Ctrl押下でカーソル位置から
  項目アイコンが円形に展開する。マウスを動かして項目をハイライトし、Ctrlを離すとその項目をトグルする。
  中心付近（デッドゾーン）で離した場合や無効項目（曲線不可バリアントの曲線トグル等）は何もしない。
  道路編集の項目は6個: 直線/曲線・ノード吸着・パス吸着・直線延長吸着・直角吸着・グリッド(表示+吸着)。
  項目定義（`RadialMenuItem`: アイコン/状態/有効条件/トグル処理をFuncで渡す）は編集モード非依存の
  汎用設計で、他の編集モードからも流用できる。
- **カーソルHUD（`UI/CursorStatusHud`）**: セッション中はカーソル脇に現在設定のアイコン列
  （曲線/直線・吸着5種。OFFは半透明）と標高・盛土のテキスト（`H +3m  E +1m` 形式）を常時表示する。
  表示内容は登録されたproviderを毎フレームpollする汎用部品で、ラジアルメニュー表示中は自動で隠れる。
- **入力ゲート**: メニュー表示中は `RadialMenuController.IsOpen` を `InputHandler`（クリック・ホバー・
  ドラッグ全般）と `CameraController`（回転・パン・移動・ズーム）が参照して入力を非破壊的に停止する
  （フライモードゲートと同方式）。メニューのCanvasには GraphicRaycaster を意図的に付けていないため、
  EventSystem のUI判定では止まらないことに注意。マウスボタン押下中（ドラッグ・回転中）はメニューを開かない。
- **起動役（`Editors/RoadEditor/RoadEditControlMenu`）**: `RoadPlanManager` と同居し、
  `IsSessionActive` をポーリングしてHUDの登録/解除とCtrl検知を行う。
  曲線モードのトグル結果は `edit_path:update_shape` の `is_curved` としてUIへ逆流し、
  UI側の確定送信（`curved`）・形状プレビュー・コスト見積りが追従する。