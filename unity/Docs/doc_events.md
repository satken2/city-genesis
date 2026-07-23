# マウスクリックイベント処理の流れ

## 1. 通常モード vs 編集モード

システム内のマウスクリック処理は、「通常モード」と「編集モード」の2つの状態で異なる処理を行います。

### 通常モードと編集モードの切り替え条件
- 編集モードか否かは `ParcelPlanPopupManager.IsAnyParcelEditMode()` メソッドで判定
  - 以下のいずれかのフラグが `true` の場合、編集モードとみなす
    - `isPointSelectionMode` (地点選択モード)
    - `isPathSelectionMode` (道路選択モード)
    - `isParcelSelectionMode` (土地区画選択モード)

## 2. 通常モード時のクリック処理

通常モードでは、各オブジェクト（土地区画、道路、建物）に添付されたClickHandlerコンポーネントがクリックを処理します。

### 土地区画のクリック処理
- **ファイル**: `ParcelClickHandler.cs`
- **メソッド**: `OnMouseDown()`
- **処理内容**:
  - 編集モード中でなければ (`ParcelPlanPopupManager.IsAnyParcelEditMode()` が `false`)
  - `InformationPopupManager.Instance.HighlightParcel()` で土地区画をハイライト
  - `FetchParcelInfo()` コルーチンでAPIから土地区画情報を取得
  - 情報を `InformationPopupManager.Instance.SelectParcel()` で表示

### 道路のクリック処理
- **ファイル**: `PathClickHandler.cs`
- **メソッド**: `OnMouseDown()`
- **処理内容**:
  - 編集モード中でなければ (`ParcelPlanPopupManager.IsAnyParcelEditMode()` が `false`)
  - `informationPopupManager.HighlightPath()` で道路をハイライト
  - `FetchPathInfo()` コルーチンでAPIから道路情報を取得
  - 情報を `informationPopupManager.SelectPath()` で表示

### 建物のクリック処理
- **ファイル**: `BuildingClickHandler.cs`
- **メソッド**: `OnMouseDown()`
- **処理内容**:
  - `InformationPopupManager.Instance.SelectBuilding()` で建物情報を表示
  - `InformationPopupManager.Instance.HighlightBuilding()` で建物をハイライト
  - `FetchBuildingDetail()` コルーチンでAPIから建物詳細情報を取得
  - 建物が建つ土地区画情報も取得し表示

## 3. 編集モード時のクリック処理

編集モードでは、`ParcelEditorInputHandler`がクリックを含む入力を管理します。

### 入力処理の流れ
- **ファイル**: `ParcelEditorInputHandler.cs`
- **メソッド**: `Update()`
- **処理内容**:
  - UI上のクリックは無視 (`EventSystem.current.IsPointerOverGameObject()`)
  - `ParcelEditorManager` の現在のモードに応じて処理を分岐:
    - `IsPointSelectionMode()` → 地点選択処理
    - `IsPathSelectionMode()` → 道路選択処理
    - `IsParcelSelectionMode()` → 土地区画選択処理

### 地点選択モード
- **メソッド**: `HandlePointSelection(Ray ray)`
- **処理内容**:
  - 地面に対するレイキャスト
  - マウスカーソル位置にマーカー表示
  - クリック時に `popupManager.OnPointSelected()` または `ParcelEditorManager.OnPointSelected()` を呼び出し

### 道路選択モード
- **メソッド**: `HandlePathSelection(Ray ray)`
- **処理内容**:
  - 道路オブジェクトに対するレイキャスト
  - ホバー時にハイライト表示
  - クリック時に道路IDとウェイポイントを取得し、`popupManager.OnPathSelected()` を呼び出し

### 土地区画選択モード
- **メソッド**: `HandleParcelSelection(Ray ray)`
- **処理内容**:
  - 土地区画オブジェクトに対するレイキャスト
  - ホバー時にハイライト表示
  - クリック時に区画IDを取得し、`popupManager.OnParcelSelected()` を呼び出し

## 4. 編集モードの切り替え

編集モードは主に `ParcelPlanPopupManager` のメソッドによって切り替えられます。

### タブ切り替えによるモード変更
- **ファイル**: `ParcelPlanPopupManager.cs`
- **メソッド**: `SwitchTab(int tabIndex)`
- **処理内容**:
  - 前のタブの操作をキャンセル
  - 現在のタブを更新
  - タブに応じたモードを有効化:
    - タブ0: `EnablePointSelectionMode()` (地点選択)
    - タブ1: `EnablePathSelectionMode()` (道路選択)
    - タブ2: `EnableParcelSelectionMode()` (区画選択)

### 各モードの有効化メソッド
- **地点選択モード**: `EnablePointSelectionMode()`
  - `isPointSelectionMode = true` にセット
  - `ParcelEditorManager.EnablePointSelectionMode()` を呼び出し

- **道路選択モード**: `EnablePathSelectionMode()`
  - `isPathSelectionMode = true` にセット
  - `ParcelEditorManager.EnablePathSelectionMode()` を呼び出し

- **土地区画選択モード**: `EnableParcelSelectionMode()`
  - `isParcelSelectionMode = true` にセット
  - `ParcelEditorManager.EnableParcelSelectionMode()` を呼び出し

## 5. ParcelEditorManagerとの連携

`ParcelEditorManager`は、`ParcelPlanPopupManager`と連携して編集モードを管理します。

### 編集モード管理
- **ファイル**: `ParcelEditorManager.cs`
- **主要メソッド**:
  - `EnablePointSelectionMode()` - 地点選択モードを有効化
  - `EnablePathSelectionMode()` - 道路選択モードを有効化
  - `EnableParcelSelectionMode()` - 土地区画選択モードを有効化
  - `OnPointSelected()` - 地点選択時の処理
  - `OnPathSelected()` - 道路選択時の処理
  - `OnParcelSelected()` - 土地区画選択時の処理

## 6. 処理の流れ（例：土地区画作成時）

1. ユーザーが「土地区画」ボタンをクリック → `ParcelPlanPopupManager.OpenPopup()`
2. タブを選択（地点選択タブなど）→ `ParcelPlanPopupManager.SwitchTab(0)`
3. 地点選択モードが有効化 → `EnablePointSelectionMode()`
4. `ParcelEditorInputHandler.Update()` が地点選択モードを検出
5. ユーザーがマップ上をクリック → `HandlePointSelection()` が処理
6. 選択された地点情報が `popupManager.OnPointSelected()` に渡される
7. API経由でプレビューが表示され、確定ボタンが有効化
8. 確定ボタンをクリック → `ConfirmPointSelection()` でAPIにリクエスト
9. 成功時にポップアップが閉じられ、マップが更新される
