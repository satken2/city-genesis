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
### 3.3. ドラッグを持たないモード

ブルドーザ（`MODE_REMOVE`）、パス結合・分割の対象選択（`MODE_PATH_MERGE` / `MODE_PATH_SPLIT`）、地区編集（`MODE_ZONE_EDIT`）はクリックのみで操作する。
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
制御点は Catmull-Rom スプラインから自動計算されるが、ドラッグした制御点は手動調整済みとして扱われ、以後のポイント移動・挿入・削除・標高変更で自動計算に戻されない。手動調整済みの制御点は、その制御点が属するポイントを動かしたときだけアンカーと一緒に平行移動する（隣接ポイントの移動には追従しない）。曲線/直線モードの切り替えでは手動調整は破棄され、全制御点が自動計算し直される。
確定ボタンでバックエンドにデータを送信する。

道路エディタは `InputHandler` から入力イベントを受け取るほか、独自の `Update` ループでホバー状態の更新と高さ入力を処理し、`LateUpdate` でオーバーレイの描画を行う。

標高（PageUp/PageDown または 1/2 キー、±1m）と盛土（3/4 キー、±1m）はキーボードで変更する（標高は上記のY軸ドラッグでも変更できる）。
`Editing` フェーズで選択中ノードがあればそのノードの値を、選択がない場合（`Placing` フェーズを含む）は
「次に配置するノード」に適用される値（`currentHeight` / `currentEmbankmentHeight`）を変更する。
標高・盛土は道路建設パネルのステッパーからも変更でき（`edit_path:change_node` / `edit_path:change_embankment`）、
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
| `STATE_EDIT_ZONE` | `ZONE_EDIT` | `ZoneEditContext.EnterCreateMode` または `EnterEditMode`（道路のみ地区色分け表示にし、地区枠線を強調） |
| `STATE_EDIT_PARCEL` | 作成方法に応じて切替 | `ParcelEditorManager.EnterCreationMode`（作成方法の切り替えはUnity側のCtrlラジアルメニュー`ParcelEditControlMenu`が担い、プリセットも同メソッドが切り替える） |
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
- 並行モードの優先順位は、接続吸着（ノード・計画中端点・パス、複線オフセットを含む）→並行・終端揃え→
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
## 補正済み線路への標高吸着

線路の保存設計標高がある場合、吸着位置の絶対標高はその基準線から評価する。地上線路でも小数の相対高さを引き継ぎ、ドラッグ時にも接続先の高さを優先する。複線の中心線へ横移動するときは絶対標高を保ち、移動先の地形との差から相対高さを算出する。接続先に吸着できた鉄道は直線・直角補助で再移動しない。描画点列のYは高さ源にしない。高さ編集可能なバリアントの数値入力は小数対応で、ステッパーは0.1m刻み。

## 13. 建物の配置方法と配置前の寸法調整

建物建設中はCtrlを押すとカーソル位置にラジアルメニューが開き、項目へ合わせて離すと配置方法を選ぶ。中心で離すと変更せず、Escで閉じる。メニュー中は建物操作・カメラ操作を止める。

| 配置方法 | 操作 |
| --- | --- |
| 区画に配置(既定の初期値) | 既存区画をクリック。地形クリックでは直接配置へ自動遷移しない |
| 自由配置 | 区画の有無に関係なくカーソルへ矩形を追従させ、中ボタンドラッグで回転 |
| 道路沿い配置 | 近くの地上道路の外縁へ位置・角度を吸着。遠ざかると吸着を解除して追従 |

初期値はUIが `STATE_EDIT_BUILDING` の `placement_mode`（parcel/free/roadside）で指定でき、省略時は区画配置。都市創設時の市庁舎は道路も区画も無いのが通常なので、UIが `free` を指定して自由配置から始める。自由/道路沿い配置では区画の入力をMODE_NONEにし、地形へクリックを通す。既存建物編集と忠実コピーは区画配置専用。直接配置が既存区画と重なる場合も、設置可否・買収確認はバックエンドの既存ルールに従う。

寸法は建物ローカルの間口・奥行きで示す。`1/2`で間口の増減、`3/4`で奥行きの増減(各1m)、Shift併用で5m。長押しは0.35秒後に0.075秒間隔で繰り返す。範囲は1〜500mで、resizable=falseの施設は固定。3Dビューにフォーカスがあるときだけキーを受ける。パネルは直接配置を選んだ時点から寸法入力を表示する。寸法線・操作中の辺の強調・カーソルHUDで対応を示す。

左クリックは位置固定。固定後も同じキーとパネルで寸法を調整でき、建設確定はパネルが行う。Escまたはパネルの取消しは寸法・配置方法を保って位置選びへ戻る。固定後に自由⇔道路沿いを切り替えても位置は移動しない(道路の再選択はEscで行う)。区画配置へ切り替えると直接配置の位置選択を解除する。

道路吸着は`PathManager.QueryNearbySegments`の近傍区間を重複除去して評価する。接道を許可した街路/幹線道路で、地形追従かつ区間両端が地表から0.3m未満のものだけが対象。線路・高架・地下は対象外。`width`は歩道込みなので中心線からwidth/2を外縁とし、接触誤差回避の0.05mを空ける。候補中心からカーソルまでの距離が「奥行き/2+12m」以内なら吸着候補にする。直前の道路・区間・側を優遇し、道路中心の1m以内では直前の側を保つ。

曲線は近接区間の接線へ矩形を回転する。矩形と周辺の地上道路コリドーの干渉を辺まで調べ、曲線内側や交差点に食い込む候補を除外する。吸着対象の道路縁と道路側の辺中央を黄色で表示する。吸着中のサイズ変更はこの辺中央を保持し、間口は左右へ、奥行きは道路から離れる側へ広げる。固定後の拡大で干渉が生じても位置を勝手にずらさず、バックエンドの設置可否結果で確認する。道路沿い配置では手動回転を無効化する。

配置状態の正は`BuildingPlanManager.Placement.cs`。パネルは変更差分を`placement_command`で送り、Unityは`placement_state`で位置・寸法・角度・世代を同時に返す。配置前のゴーストはローカル即時描画であり、設置可能の保証ではない。位置固定後にパネルがpreview-directを呼び、現在の要求と一致する検証結果が届くまで建設確定を無効化する。古い形状応答はplacement_revisionと階数の照合で破棄する。

輪郭・寸法線・道路吸着マーカーは地表高+0.05mのワールド座標で生成する。`BuildingPlacementGuide`は親を持たない原点・回転なし・等倍の描画オブジェクトとし、編集用オブジェクトの位置・回転・拡縮を頂点へ重ねて適用しない。コントロール無効化時は非表示にし、破棄時はガイドと所有するメッシュ・マテリアルも破棄する。

検証はUnityの`Tests/BuildingPlacement/run.ps1`(幾何計算)と`run-unity.ps1`(独立したUnityで本編コンポーネントの状態遷移と、編集用オブジェクトの移動・回転・拡縮に対するガイド座標の不変性)。

## プロップの自由配置

MODE_EDIT_PROPはクリック・ホバーともTerrainInputHandlerの共通吸着を通さず、地形の任意XZをPropPlacementManagerへ渡す。他オブジェクトの入力を貫通し、個別配置ではパネルで指定した地面相対高さを加算する。既存の建物・道路・プロップとの干渉判定や高さによる種別制限は行わない。
