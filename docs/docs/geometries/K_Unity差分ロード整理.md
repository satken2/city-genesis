# 概要

City Genesisのフロントエンド（Unity）では、バックエンドから取得した都市データを3D空間に描画する際、以下の3段階の順序でロードを行う。

1. 建物・道路・区画・地区のロード
2. 地形（near/farティア）のロード
3. 地形（dynamicティア）のロード

この順序は、地形が正しく準備された状態で建物や道路を配置することを保証するために重要である。

# 更新のトリガー

マネージャー群（建物・道路・区画・地区・地形）の更新は3つのトリガーで実行される。

## カメラ移動による更新

`CellDataLoader.OnCameraCellChanged` がカメラの中心セル変更を検知した時に呼ばれる。

カメラが移動すると、各セルのLODティア（Far/Near/Dynamic）が変わりうる。`CalculateTierChangedCells` は前回のティア割り当てと今回のティア割り当てを比較し、ティアが変化したセルのみを抽出する。ティア変更がない場合は全マネージャーの更新をスキップする。これは純粋なカメラ移動による更新であり、バックエンドデータに変更はないため、スキップしても問題ない。

## バックエンドデータ変更後の差分更新

`CellDataLoader.ReloadAllData` がバックエンドデータの変更（道路作成・建物作成・地区作成の確定時など）に呼ばれる。

カメラが動いていなくても全マネージャーの差分更新を実行する。`forceManagerUpdate` パラメータが `true` のため、ティア変更の有無に関わらず `UpdateManagers` が各マネージャーにロード指示を出す。各マネージャーはバックエンドからデータを取得するが、version比較により変更のないオブジェクトの3D再生成をスキップする。これにより、API呼び出しは発生するものの、不要な描画処理は省略される。

## 強制全量更新

`CellDataLoader.ForceReloadAllData` がセッションの強制リロードなどに呼ばれる。

全マネージャーのデータをクリア（`ClearAllManagerData`）した上で、開放済みセルリストを再取得し、再度ロードを実行する。キャッシュが空の状態で再取得するため、すべてのデータが新規として扱われ、全オブジェクトが再作成される。

# ロード順序の詳細

## 1. 建物・道路・区画・地区のロード

`CellDataLoader.UpdateManagers` の最初の段階で、以下のマネージャーに対してロード指示を出す。

- `BuildingManager`
- `PathManager`
- `ParcelManager`
- `ZoneManager`

### versionベースの差分更新

各マネージャーはバックエンドから `version` 付きのデータを取得し、ローカルにキャッシュしている `version` と比較する。

- **バックエンドのversionがローカルと異なる、または新規のデータ** → 3Dオブジェクト（または2Dオーバーレイ）を再作成する
- **バックエンドのversionがローカルと同じ** → オブジェクトの再作成をスキップする。非表示になっていた場合は再表示するのみ
- **フロント側に存在するがバックエンドから取得できなかったデータ** → 3Dオブジェクトを削除する

このversionベースの差分更新により、変更がないオブジェクトの再作成を省略し、フレームレートの低下を防ぐ。

### 描画・更新処理のパフォーマンス最適化

データロードおよびエポック更新に伴う処理負荷（CPU/GPUスパイク）を低減するため、フロントエンド側で以下の最適化を適用している。

- **交差点接続パスのO(1)ルックアップインデックス**:
  `PathManager` は、交差点（ノード）の3Dメッシュ更新や削除を行う際、全パスのキャッシュ（`pathDataCache`）を線形走査するのを防ぐため、`pathsByNodeIndex`（ノードIDから接続パスIDのHashSet）の逆引きインデックスを内部で構築・維持している。これにより、交差点の再生成コストを `O(N_paths)` から `O(1)` へ削減している。
- **地形Ready時の道路交差検索の最適化**:
  地形タイル生成時（`OnTerrainTileReady`）に影響を受ける道路を検出する際、すべてのパスの全セグメントをループ走査するのを防ぐため、すでに通過セル情報が登録されている `pathIdsByCell` を使って、影響のある5セルに存在する道路IDのみに候補を絞り込んでからセグメント交差判定を行っている。
- **入力判定処理の静止時スキップ**:
  `InputHandler` は、マウスやカメラが動いていない（Rayの原点と方向が変化していない）かつクリックなどの入力イベントがないフレームでは、重い `RaycastAll` やホバー判定、地区ポリゴン内外判定などの処理を丸ごとスキップし、CPUオーバーヘッドを排除している。
- **地区内外判定の実行制限とBBOXフィルタリング**:
  `ZoneManager.GetZoneIdAtPoint` による多角形内外判定は、通常プレイ中は毎フレーム実行する必要がないため、「地区編集モード中」のみに実行を限定している。また、判定を行う際には、あらかじめ各地区の Bounding Box（BBOX）キャッシュを用いた AABB 判定を挟み、範囲外の地区を高速に弾く（早期リターン）ことで、多角形内外判定（`IsPointInPolygon`）の計算負荷を最小限に抑えている。

### 各マネージャーの取得範囲と制御

| マネージャー | 取得セル範囲 | version比較 | `isForce` の効果 | 備考 |
|---|---|---|---|---|
| PathManager | farCells全セル | `pathVersionCache` と比較 | 未使用（宣言のみ） | 常に全セルのデータをバックエンドから取得し、version差分のみ再描画 |
| BuildingManager | farCells全セル | `buildingVersionCache` と比較 | 未使用（宣言のみ） | 同上 |
| ParcelManager | farCells全セル | `parcelVersionCache` と比較 | 既存セルを一旦アンロードして再取得 | isForce=trueでキャッシュクリア後に再取得 |
| ZoneManager | 全地域一括 | `zoneVersionCache` と比較 | `areZonesLoaded=true`ならスキップ、`isForce=true`で強制再取得 | 地区作成後は個別に `isForce:true` で呼ばれる |
| TerrainManager | farCells全セル | `X-Cell-Version` ヘッダーと比較 | 既存セルをアンロードして再取得 | dynamicティアのみversion比較対象 |

### 強制全量更新の場合

`CellDataLoader.ClearAllManagerData` を呼び出して、すべてのマネージャーの `ClearAllCells` を実行する。これによりUnity側の全オブジェクトが破棄される。その後、差分更新フローを呼び出す。キャッシュが空になっているため、すべてのデータが新規取得・再作成される。

## 2. 地形（near/farティア）のロード

`TerrainManager` に対して `LoadLodCells` を呼び出す。nearティアとfarティアの地形はランタイム中に不変であり、未取得のセルがあれば取得してTerrainタイルを生成する。ティア変更があった場合は古いタイルを破棄して新しい解像度のタイルを生成する。

## 3. 地形（dynamicティア）のロード

dynamicティアの地形はセル単位で `version` を持つ。`TerrainManager` はバックエンドの `/terrain/dynamic` API に対して、Unityセルをバックエンドセルに変換した上で1セルずつ問い合わせる。レスポンスヘッダー `X-Cell-Version` からversionを取得し、ローカルの `dynamicCellVersions` と比較する。

- **versionが異なる、または新規のセル** → Terrainタイルを再描画する
- **versionが同じ** → 何もしない

## 4. 地形追従更新（OnTerrainAllLoaded後）

`TerrainManager` が全セルのロードを完了し、`OnAllTerrainLodLoaded` イベントを発火すると、`CellDataLoader.OnTerrainAllLoaded` が呼ばれる。

### 更新セルの差分化

`OnAllTerrainLodLoaded` は引数として「このロードバッチで実際に地形タイルが(再)生成・再縫合されたセル」の一覧を渡す。`TerrainManager` は `BuildTerrainTileAsync`（タイル生成）と境界の再縫合（restitch）のたびに更新セルを記録し、発火時にスナップショットして渡す。

`CellDataLoader` はこの更新セルだけを追従更新の対象とする。**カメラがセル境界をまたいで可視セルの集合が変わっても、地形データが実際に変化していないセルには一切触れない**。これにより、地形不変のセル跨ぎでは建物・道路の再生成コストがほぼゼロになる。

- **CPU高さ配列** → 地形タイルの生成・再縫合の時点で `TerrainManager` が `HeightmapTextureManager.WriteCellHeights` にCPU側のハイトマップをそのまま渡して直接更新する（GPUからの `GetHeights` readbackは行わない）。追従更新のステップとしての差分更新は不要。初回ロード時と `ForceReloadAllData` 時のみ全量 `RebuildCpuHeightmap` を行う（これもCPUキャッシュからの再書き込み）。
- **建物** → `BuildingManager.AdjustBuildingHeightsForCellsCoroutine` を呼び出し、「更新セル ∩ nearCells（=Nearティア以上。dynamicCells ⊂ nearCells）」内の建物について、Dynamicセルはフェンス・地面パネルの地形追従のためメッシュを再生成し、それ以外はY座標だけを再調整する。バックエンドからの再取得は行わない。
- **道路** → `PathManager.RebuildPathShapesForCellsCoroutine` を呼び出し、「更新セル ∩ nearCells」を通る道路の形状を現在のティアで再計算する。バックエンドからの再取得は行わない。

追従対象をDynamicセルに限定してはいけない。建物・道路のHTTPロードと地形タイル生成は完了順が不定なため、Nearセルにも「古い高さ」で生成された個体が存在しうる。Farセルの簡易表示は遠距離で高さずれが視認できないため対象外とする。

### フレーム分散とコルーチンのキャンセル

上記の追従更新は `CellDataLoader.TerrainFollowUpCoroutine` で実行され、`PerformanceConfig.FrameBudgetMs`（目標フレーム時間の一定割合）を超えると `yield return null` で次フレームに譲る。これにより、対象セル内のオブジェクト数が多くてもメッシュ再生成が1フレームに集中せず、カメラのカクつき（フレームヒッチ）を防ぐ。

カメラが移動して次のロードが始まると（`UpdateManagers` 冒頭）、進行中の追従コルーチンは `StopCoroutine` でキャンセルされ、最新の地形に対して仕切り直される。キャンセルで取りこぼした更新セルは `pendingFollowUpCells` に残り、次回のロード完了時にまとめて再処理される。建物・道路の追従処理は `StartCoroutine` ではなく `IEnumerator` を直接 `yield return` することで親コルーチンと同一実行コンテキストで動かし、親のキャンセルで内側も確実に停止するようにしている。

また、`UpdateManagers` はロードのたびに `LoadBatch` を発行し、旧バッチを `Cancel` する。`StopCoroutine` では停止できない並行子コルーチン（TerrainManagerのセル並行ロード等）は、このバッチの `IsCanceled` を確認して協調的に停止する。

なお、地形タイル生成イベントによる再生成抑制（`BeginDeferredTerrainTilePathRebuilds` / `EndDeferred...`）の解除フラグは boolean（冪等）であり、次のロードで再度 Begin されるため、コルーチンがキャンセルされて解除処理が走らなくても整合性は保たれる。加えて、抑制中に破棄されたイベントのセルは `EndDeferred...` が返却し、未処理であれば `pendingFollowUpCells` へ合流して追加の追従が実行される（取りこぼしへの防御）。

# なぜこの順序なのか

建物や道路は `Utils.GetGroundHeight` を使って地形の高さに基づいて配置される。もし地形を先にロードしないと、建物や道路が不正な高さで生成されてしまう可能性がある。しかし、現在の実装では建物・道路のロードを先に行い、地形ロード完了後に追従更新を行う方式を採用している。これにより、建物・道路のデータ取得と地形ロードを並行化でき、ロード時間の短縮を図っている。

dynamicティアの地形は工事などで随時変更される可能性があるため、versionベースの差分更新を行う。near/farティアは不変なので、version管理は不要である。

# 各データのversion管理

| データ種別 | versionフィールド | 差分更新の対象 |
|---|---|---|
| 建物（Building） | `AgentData.version` | version比較で再作成判定 |
| 道路（Path） | `PathData.version` | version比較で再作成判定 |
| 区画（Parcel） | `ParcelData.version` | version比較で再作成判定 |
| 地区（Zone） | `ZoneData.version` | version比較で再作成判定 |
| 地形（Dynamic） | `X-Cell-Version` ヘッダー | version比較で再描画判定 |

# special case: 地区作成後の強制再描画

地区の枠線はカメラ移動時にしか再描画されない仕様である。そのため、地区作成・拡張の確定後は、`IpcStateCoordinator.ReloadWithZoneForce` が `HandleSessionReload`（`ReloadAllData`を含む）に続いて `ZoneManager.LoadCells` を `isForce:true` で個別に呼び出す。これにより、カメラが動いていなくても地区の枠線が即座に再描画される。

# APIエンドポイント

## dynamic地形

`GET /api/v1/terrain/dynamic?cell_x={backend_x}&cell_z={backend_z}`

- バックエンドのセルインデックスを1セルずつ指定する
- BBOX範囲指定は受け付けない
- レスポンスヘッダー `X-Cell-Version` にセルのversionが含まれる
- 未開放セルの場合は404を返す

## 全開放セルのversion一覧

`GET /api/v1/terrain/cell-versions`

- 全ての開放済みセルとその地形バージョンの一覧を返す
- フロントエンドはこの一覧を取得し、クライアント側で保持しているversionと比較することで、更新が必要なセルのみを個別に `/terrain/dynamic` へ問い合わせることができる

# dynamicティアの地形versionの管理方針

dynamicティアの地形はセル単位で遅延生成される。セルが解放（acquire）された時点で、当該セルのversionは `0` で初期化される。

その後、道路工事や建設などの理由で地形が変更されるたびに、変更対象となったセルのversionは自動的に1ずつインクリメントされる。これにより、同じセル内で何度地形が書き換えられても、常に一意な世代番号が割り当てられる。

versionはセル単位で管理される。1つの地形変更処理が複数のセルにまたがる場合、各セルは独立してversionが上がる。これは、変更の影響を受けたセルのみを効率的に再描画するために必要である。

## 永続化

セルデータをディスクに書き出す際、標高データとともにversion番号も保存される。セーブZIP内の各セルファイル（`cell_{id}.npz`）には、`elevation` キーに加えて `version` キーが含まれる。

ロード時には、ディスク上のセルファイルから標高データとversion番号を両方とも読み出し、メモリ上のセルバージョンテーブルに復元する。古いセーブデータで `version` キーが存在しない場合は、version `0` として扱う。

## 遠景・近景マップとの違い

遠景マップ（far_map）と近景マップ（near_map）はランタイム中に不変であるため、version管理の対象外とする。これらはマップ全体として一括で取得し、dynamicティアのようにセル単位の差分更新は行わない。
# プロップの差分同期

PropManagerはfarCells全体のマージBBOXで、配置情報と障害物を同じレスポンスから取得する。取得はApiClient.GetBulk、ロードのキャンセルは共通LoadBatchと世代番号で管理する。候補点生成の署名（プロップID/version）と非表示判定の署名（障害物key/signature）を分け、道路一本の編集で森全体を再散布しない。削除・移動による旧位置の復元も新旧スナップショットの比較で反映する。

地形追従は既存TerrainFollowUpCoroutineからupdatedCells∩nearCellsの候補のYを更新する。初回FarタイルについてもOnTerrainTileReadyで対象小区画の高さ更新を予約する。通信より地形が先に完成した場合は配置コミット時の地形高さを使用する。候補座標、除外結果、完成済み描画バッチを分け、キャンセル時に未完成の結果を表示しない。詳細は[M_地形プロップ](M_地形プロップ.md)。
