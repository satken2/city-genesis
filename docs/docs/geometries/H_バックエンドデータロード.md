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
- **差分同期**: `SyncBuildingsInCells` で削除建物を除去
- **取得範囲**: カメラ移動では未ロード・要再取得(`MarkLoadedCellsStale`)・ティア変化のセルだけを取得する。取得対象外セルの建物は生成しない
- **アンロード**: 離脱セルの建物は分割ワーカーが退避キャッシュへ移し、再入場時にキーで復元する
- **Status変化検知**: 受信した `status` を既存オブジェクトの `ObjectIdInfo.status` と比較する(status は再利用キーの形状ハッシュにも含まれる)
- **個別取得**: `CellDataLoader.FetchBuilding(agentId)` → `GET /api/v1/buildings/{agent_id}`。応答は一覧(`GET /api/v1/buildings`)の1要素と同じ形式(サブ建物配置を含む)なので、一括ロードと同じ生成経路で描き直せる。呼び出し元は爆破通知 `building:exploded`

### PathManager

- **LODティア**: Far（直線ストリップ）/ Near（スプライン・地形追従）
- **差分同期**: `SyncPathsInCells` で削除道路を除去
- **ノード同期**: `SyncNodesInCells` で削除ノードを除去（ノードのセル逆引きで対象セルだけを見る）
- **交差点メッシュ再生成**: パスの追加/削除/status変化時に、接続ノードの交差点メッシュを `UpdateNodeIntersectionMesh` で再生成。入力署名(ノード位置・種別・接続道路の版・状態・進捗・地形)が前回と同じなら再計算を省く
- **アンロード**: 離脱セルの道路・ノード・交差点メッシュは分割ワーカーが非表示にする(破棄しない)。常駐セルに跨る道路は残す
- **個別取得**: `CellDataLoader.FetchPath(pathId)`, `PathManager.RemovePathById(pathId)`

### ParcelManager

- **描画方式**: 枠線・ホバーは `OverlayMeshRenderer`(`_ZTest=Always`)、データ可視化の塗りは `Custom/OverlayFillTerrainConform`(GPU地形追従)の別レンダラー
- **取得範囲**: 未取得・要再取得(`MarkLoadedCellsStale`)セルの外接矩形だけを取得し、応答は重心セルで絞り込む。件数上限(5000)に達したらセル集合を二分して取り直す
- **差分同期**: `SyncParcelsInCells` で、重心セル逆引きでセル内の区画のうち応答に無いものを除去
- **アンロード**: 離脱セルの区画は分割ワーカーが非表示にし、常駐外のまま保持する。保持セルが81を超えたら古いセルの区画から破棄する
- **Status変化検知**: usageの変化を検知して再生成
- **オブジェクトキャッシュ**: `parcelObjectsCache` (Dictionary) で管理。`FindGameObjectsWithTag` は不使用
- **環境スコアの色分け**: 騒音・大気汚染・治安の区画色分けは、`GET /city/noise` / `/city/pollution` / `/city/crime` が返す `parcel_id` → 0〜1 の相対値(そのエポックの最小値〜最大値を両端に引き伸ばした値なので、濃さは都市内での相対的な悪さ)を使う。`ParcelSummary` には無いため区画のbbox取得とは別経路で、表示モードの切替時に種別ごとに1回取得し(同じモードの選び直しでもエポックで値が変わるため取り直す)、応答時に別モードへ変わっていれば捨てる。スコアの無い区画(ヒートマップ未計算・対象外)は塗らない
- **個別取得**: `CellDataLoader.FetchParcel(parcelId)`

### ZoneManager

- **描画方式**: 地区枠線は `OverlayMeshRenderer`(32本ごとのチャンクで視錐台カリング)、地区名ラベルと建物アイコンは Screen Space Overlay Canvas 上の uGUI(`Text`+`Outline`、`Image`)
- **ロード方式**: セル単位ではなく全量ロード（`GET /zones/`）
- **差分同期**: `SyncZones` でAPIレスポンスに含まれないゾーンを `RemoveZone` で除去。版が変わった地区は1地区ずつ予算を挟んで適用するので、適用は複数フレームに分かれる
- **高さの再計算**: 地形ロード完了時、変化のあったセルに掛かる地区だけ枠線・ラベルの高さを予算内で再計算する。非表示中は印を付けるだけで、表示ONのときに始める
- **再取得の経路**: カメラ由来のロード(`LoadCells`)は未ロード時と強制時だけ全件を取る。エポック完了・編集確定の `ReloadAllData` は isForce=false のため、代わりに `RequestZoneRefresh()`(version差分の全件リロード。進行中なら完了後に1回だけ追加実行)を明示的に呼ぶ。地区はエポックごとに自動で作成・統合・分離されるので、この呼び出しが無いと地区表示が起動時のスナップショットのまま更新されない。地区編集の確定は `ReloadAllZonesForced()`(version差分を使わない全再取得)
- **枠線の描き方**: 隣り合う地区は境界の街路を共有して辺が重なるため、枠線は各地区の内側へ3mセットバックして描き、境界では2本の線が平行に並んで両方の地区色が読めるようにする。地区編集モード中だけ枠線の `_ZTest` を `Always` にして太くし(地形・建物に隠れないように)、通常プレイでは `LEqual` に戻す。編集中にマウスが入った既存地区は枠線とは別のレンダラーの半透明フィルで強調し、ホバー地区が変わったフレームだけ作り直す
- **個別取得**: `CellDataLoader.FetchZone(zoneId)`

### ResourceManager

天然資源(油田・鉱脈・レアメタル・森林・水源。[D_地形形状](D_地形形状.md) §D.12)の分布を、地形に追従するヒートマップ風のオーバーレイと種別名のラベルで描く。

- **ロード方式**: セル単位ではなく `GET /resources` で取得する
- **描画方式**: `OverlayMeshRenderer` ではなく、UVと頂点カラーを持つ専用の MeshFilter/MeshRenderer と `Custom/ResourceHeatmap`(プロシージャルノイズ)。各ポリゴンのAABB内を10m格子に細分化して頂点ごとに地形高さをサンプルする(ポリゴン頂点だけの板では丘に埋もれる)。境界からの符号付き距離で12mのソフトフェードを付け、輪郭線は引かない
- **再構築**: ワールド固定メッシュなのでカメラ移動では作り直さない。資源ロードの完了と、資源のあるセルの `OnTerrainTileReady` で作り直し、同一フレームの複数イベントは `LateUpdate` で1回にまとめる
- **ラベル**: `ZoneManager` と同じ ScreenSpaceOverlay Canvas(sortingOrder 55。地区の60より奥)に `resource_type_label` を出し、カメラが1m/2°以上動いたフレームだけ位置を更新する

### InterchangeManager

インターチェンジ（IC/JCT。[道路網](../economics/3_道路網.md#インターチェンジとジャンクション)）の名前ラベルだけを持つ。内部の道路は普通のパスとして PathManager が描く。

- **描画方式**: Screen Space Overlay Canvas（sortingOrder 62）+ uGUI の Text・Outline・Button。ラベルは `center` の地面高さ＋6mをスクリーン座標へ変換した位置に置き、カメラが1m/2°以上動いたとき・一覧の更新後・地形ロード完了後・表示再開時だけ位置を更新する。カメラの背面と6,000mより遠いものは隠す(水平距離で遠いと分かるものは地形をサンプルしない)。地面の高さはICごとにキャッシュし、地形ロード完了・一覧の更新で捨てる。文字列は `name`、空なら `IC {id}` / `JCT {id}`。
- **ロード方式**: セル単位ではなく全量ロード（`GET /interchanges/`）。初回ロードと全量リロードでは未取得のときだけ取得し、`ReloadAllData`（エポック完了・編集確定・`session:reload`）では毎回取り直す。バックエンドはこのAPI呼び出しの時点で道路の変化を再検出するため、取り直しを省くと竣工したランプのインターチェンジが現れない。
- **差分同期**: 応答の `version` が前回と同じなら何もしない。変わっていればラベルを追加・更新・削除する。
- **表示条件**: 道路・地形の入力モードがともに通常、ワールド表示中、ドライブUI非表示、道路の表示モードが非表示でないとき。編集中にラベルが道路編集のクリックを奪わないようにするため。
- **選択**: クリックで `world:select_object`（`window_type=intersection_detail`、`entity.type=interchange`）を送る。
- **破棄**: `ClearAllManagerData`（セッション切り替え・全量リロード・ゲーム終了）で全ラベルを破棄し、取得中の応答は世代番号で捨てる。

### TerrainManager

- **解像度**: 常駐セルはすべて共通の513格子。常駐範囲外は全景地形(UltraFar)で描く(詳細は [E_LOD処理](E_LOD処理.md)「地形の取得と確定」)
- **差分同期**: `/terrain/cell-versions` の版が変わったセルだけを取り直す。常駐外のタイルは最大24枚まで非表示で保持し、再入場で版が同じなら取得せずに表示へ戻す

## 全件走査の回避(逆引きインデックス)

オブジェクト数が増えてもフレーム時間とロード時間を保つため、各マネージャーは「1件の更新のために全件を線形走査する」処理を逆引きインデックスで避けている。インデックスは登録・削除の専用メソッドを通してだけ更新する(Dictionary を直接書くと索引と実体がずれる)。

| インデックス | 引く内容 | 同期するメソッド |
| --- | --- | --- |
| `PathManager.pathsByNodeIndex` | ノードID → 接続パスIDの集合。交差点1つの更新で全パスキャッシュを走査しない | `SavePathToCache` / `RemovePathFromCache` |
| `PathManager.pathIdsByCell` | セル → 通過パスID。地形タイル到着時は対象セルと隣接セルのパスだけ交差判定する | 同上 |
| `BuildingManager.buildingIdsByCell` | セル → 建物ID。`UnloadCell` / `SyncBuildingsInCells` で全建物を走査しない | `RegisterBuildingCell` / `UnregisterBuildingCell` |
| `BuildingManager.buildingIdsByParcel` | 区画ID → その上の建物ID | `RegisterBuildingDataToCache`(parcel_id 変更時は旧対応を除去) / `SyncBuildingsInCells` / `ClearAllCells` |
| `ParcelManager.parcelIdsByCell` | セル → 区画ID。`GetParcelIdAtPoint` がマウス移動ごとに全区画のポリゴン判定をしないため | `RegisterParcelDataForLines`(内部で `RegisterParcelSpatialIndex`) / `RemoveParcel` / `ClearAllCells` |
| `ParcelManager.parcelIdsByFrontPath` | 接道街路ID → 面している区画ID | `RegisterParcelSpatialIndex` / `UnregisterParcelSpatialIndex` / `ClearAllCells` |
| `ZoneManager.zoneBboxCache` | 地区の外接矩形。多角形内外判定(`IsPointInPolygon`)の前の早期リターン | 地区の追加・削除に合わせて更新 |

このほか、毎フレームの無駄を避ける規則が2つある。

- `InputHandler.Update` は、Ray(マウス・カメラ)の変化と入力イベントが無いフレームではレイキャストと地区判定を行わない。`ZoneManager.GetZoneIdAtPoint` は地区編集モード中(地形の入力モードが `MODE_ZONE_EDIT`)だけ実行する。
- ワールド固定のオーバーレイメッシュ(地区のホバーフィル等)は、内容が変わったフレームだけ再構築する。`OverlayMeshRenderer` は次の `BeginFrame` まで前フレームのメッシュを保持するので、毎フレーム `BeginFrame`〜`EndFrame` で作り直すとカメラ静止中も CPU/GPU を消費する。カメラ追従が必要なオーバーレイ(区画の枠線等)はカメラ移動の閾値でガードする。

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
          ├─ 入力署名が前回と一致 → 省略
          └─ 接続点が変化 → バッチ中は延期、それ以外は再生成キュー
                            (TerrainFollowRebuildWorker が描画LODを判定して作る)
```

## ロード順序

CellDataLoader.UpdateManagers は以下の順序でマネージャーに指示を出す：

1. **Terrain** — 地形タイルを取得・公開し、既存の道路・建物・プロップを新しい地形へ追従させる（他マネージャーが `GetGroundHeight` を使う）
2. **Parcel** — 区画を取得し、適用の完了を待つ（海上区画の建物の接地高が区画の基準標高に依存するため、区画を先に確定させる）
3. **Building** → **Path** — 地形と区画に追従して配置する。道路は交差点メッシュも生成する
4. **Zone** → **Resource** → **Prop** — 地区は全量の版差分、資源は一覧の再取得、プロップは常駐セルの配置・障害物のスナップショット

## 主要ファイル

| ファイル | 役割 |
|----------|------|
| `DataLoaders/CellDataLoader.cs` | オーケストレーター。カメラ移動・エポック完了のエントリポイント |
| `DataLoaders/BaseApiDataManager.cs` | 抽象基底クラス。LoadCells/LoadLodCells/セル範囲計算の共通ロジック |
| `DataLoaders/BuildingManager.cs` | 建物のロード・ティア管理・差分同期 |
| `DataLoaders/PathManager.cs` | 道路・ノード・交差点のロード・ティア管理・差分同期 |
| `DataLoaders/ParcelManager.cs` | 区画のロード・オーバーレイ描画・差分同期 |
| `DataLoaders/ZoneManager.cs` | 地区のロード・UI描画・差分同期 |
| `DataLoaders/InterchangeManager.cs` | インターチェンジ一覧のロードと名前ラベルの描画・選択 |
| `DataLoaders/RoadManager.cs` | 名前付き道路一覧のロードと道路名ラベルの描画・選択（[道路の詳細と道路名](../programs/道路の詳細と道路名.md)） |
| `DataLoaders/ResourceManager.cs` | 天然資源エリアのロードとヒートマップ描画 |
| `DataLoaders/TerrainManager.cs` | 地形タイルのロード・常駐外タイルの保持・隣接接続 |
| `DataLoaders/LodConfig.cs` | ティア定数・セルセット計算 |
