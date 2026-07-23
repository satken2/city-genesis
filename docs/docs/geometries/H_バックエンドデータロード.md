# バックエンドデータロード

## 概要

バックエンドAPIから建物・道路・区画・地区・地形のデータを取得し、Unity空間にモデルを生成・管理する仕組み。

## 更新トリガー

| トリガー | メソッド | isDiff | isForce | 動作 |
|----------|----------|--------|---------|------|
| カメラ移動 | `OnCameraCellChanged` | false | false | 増分ロード（不要セルアンロード、新規セルロード） |
| リロードボタン | `ForceUpdateCurrentCells` | false | true | セル全量アンロード→再ロード |
| エポック完了 | `ReloadAllData` | true | false | 差分同期（後述） |
| セッションリセット | `ClearAllCells` | - | - | 全オブジェクト破棄 |

## 差分同期（Diff Sync）

エポック完了時に `ReloadAllData()` → `UpdateManagers(isDiff: true)` が呼ばれる。
各マネージャーの `LoadLodCells` は `isDiff=true` の場合、既存セルに対してDiff APIでデータを再取得し、以下の差分アルゴリズムでモデルを更新する。

### 共通アルゴリズム

```
1. APIからデータ取得（&diff=true パラメータ付き）
2. レスポンスから HashSet<int> receivedIds を構築
3. レスポンスの各オブジェクトを処理:
   - キャッシュに存在する → status比較
     - 同じ → データキャッシュのみ更新（再生成スキップ）
     - 変化あり → destroy + recreate
   - キャッシュに存在しない → 新規作成
4. SyncInCell:
   キャッシュを走査し、セル内かつ receivedIds にないオブジェクトを
   destroy + キャッシュ削除（ゾンビオブジェクト除去）
```

## マネージャー別動作

### BuildingManager

- **LODティア**: Far（白い箱）/ Near（フル詳細）
- **差分同期**: `SyncBuildingsInCell` で削除建物を除去
- **Status変化検知**: `ExtractStatusFromObjectName` で既存statusと比較
- **個別取得**: `CellDataLoader.FetchBuilding(agentId)`

### PathManager

- **LODティア**: Far（直線ストリップ）/ Near（スプライン・地形追従）
- **差分同期**: `SyncPathsInCell` で削除道路を除去
- **ノード同期**: `SyncNodesInCell` で削除ノードを除去（既存実装）
- **交差点メッシュ再生成**: パスの追加/削除/status変化時に、接続ノードの交差点メッシュを `UpdateNodeIntersectionMesh` で再生成
- **個別取得**: `CellDataLoader.FetchPath(pathId)`, `PathManager.RemovePathById(pathId)`

### ParcelManager

- **描画方式**: 2Dスクリーンオーバーレイ（ParcelOverlayRenderer）
- **差分同期**: `SyncParcelsInCell` で削除区画を除去（既存実装）
- **Status変化検知**: usageの変化を検知して再生成
- **オブジェクトキャッシュ**: `parcelObjectsCache` (Dictionary) で管理。`FindGameObjectsWithTag` は不使用
- **個別取得**: `CellDataLoader.FetchParcel(parcelId)`

### ZoneManager

- **描画方式**: Screen Space Overlay Canvas + UI Image + TMPラベル
- **ロード方式**: セル単位ではなく全量ロード（`GET /zones/`）
- **差分同期**: `SyncZones` でAPIレスポンスに含まれないゾーンを `RemoveZone` で除去
- **個別取得**: `CellDataLoader.FetchZone(zoneId)`

### TerrainManager

- **LODティア**: Dynamic（解像度513）/ Near（257）/ Far（65）/ UltraFar（33）
- **エポック時更新**: `dynamicCells`（カメラがあるセル）のみ再取得。near/farはカメラ移動時に更新
- **差分同期**: `RefreshDynamicTerrain` コルーチンでdynamic地形のみ再ロード

## 交差点メッシュの再生成フロー

```
PathManager.LoadCellDataAtTier (isDiff=true)
  ├─ API レスポンス処理
  │   ├─ status変化検知 → affectedNodeIds に追加
  │   └─ CreateOrUpdatePath で各パスを処理
  ├─ SyncPathsInCell
  │   └─ 削除パスの start/end node → affectedNodeIds に追加
  └─ foreach affectedNodeIds:
      └─ UpdateNodeIntersectionMesh(nodeId)
          ├─ GetPathsConnectedToNode で接続パスを取得
          ├─ NodeMeshFactory.GenerateNodeMesh でメッシュ生成
          └─ 接続点が変化 → CreateOrUpdatePath(updateIntersections: false)
```

## ロード順序

CellDataLoader.UpdateManagers は以下の順序でマネージャーに指示を出す：

1. **Terrain** — 高さデータを先に準備（他マネージャーが GetGroundHeight を使用）
2. **Building** — 地形に追従して配置
3. **Parcel** — 地形に追従して配置
4. **Path** — 地形に追従、交差点メッシュ生成
5. **Zone** — セル非依存

## 主要ファイル

| ファイル | 役割 |
|----------|------|
| `DataLoaders/CellDataLoader.cs` | オーケストレーター。カメラ移動・エポック完了のエントリポイント |
| `DataLoaders/BaseApiDataManager.cs` | 抽象基底クラス。LoadCells/LoadLodCells/セル範囲計算の共通ロジック |
| `DataLoaders/BuildingManager.cs` | 建物のロード・ティア管理・差分同期 |
| `DataLoaders/PathManager.cs` | 道路・ノード・交差点のロード・ティア管理・差分同期 |
| `DataLoaders/ParcelManager.cs` | 区画のロード・2Dオーバーレイ描画・差分同期 |
| `DataLoaders/ZoneManager.cs` | 地区のロード・UI描画・差分同期 |
| `DataLoaders/TerrainManager.cs` | 4段階LOD地形ロード・エッジステッチ |
| `DataLoaders/LodConfig.cs` | ティア定数・セルセット計算 |
