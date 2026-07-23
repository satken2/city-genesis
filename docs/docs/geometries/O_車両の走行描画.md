# O. 車両の走行描画

## O.1. 概要

道路の上を車が走る様子を描画する仕組みである。カメラ周辺の道路に、パスの**交通量（`path_traffic`）**と**走行方向（`travel_direction`）**に応じて車をスポーンさせ、交差点で確率的に分岐させながら走らせ、範囲外に出たらデスポーンする。

この描画は**純粋な演出**であり、経済シミュレーションには一切影響しない。バックエンドは車を1台ずつシミュレートしておらず（[事故と災害](../economics/20_事故と災害.md) 参照）、車の位置・台数はクライアント（Unity）側で手続き的に生成する短命オブジェクトである。したがって：

- サーバー永続エンティティではないため、パス（[パス形状](A_パス形状.md)）や建物のようなセル単位のロード/アンロードには載せない。
- 他の車との**当たり判定は行わない**。信号待ちも考慮しない。
- ロードするのは `PathManager` が既に取得済みのパスデータ（`pathDataCache`）であり、車描画専用のAPI取得は行わない（通行方向の取得を除く。O.6 参照）。

当面はすべて単純な箱で描画するが、将来的に「車種ごとに異なる fbx」「同一車種内でもランダムに複数 fbx」へ拡張できる構造を持つ（O.5）。設計は[追加コンテンツとアセットパック](../programs/追加コンテンツとアセットパック.md) の `ModelManager` 解決層（論理パス → 物理アセット）と整合する。

## O.2. 入力データ

車描画は既存のパスデータ2種と都市設定1つを入力とする。いずれも新規のバックエンド構造は増やさない。

### 交通量 `path_traffic`

パスごとに記録される**車種別**の交通量。`VEHICLE_*`（`VEHICLE_STANDARD=10` 乗用車 / `VEHICLE_TRUCK=11` トラック / `VEHICLE_BUS=12` バス / `VEHICLE_COMMERCIAL=13` 商用車 …）をキーとする dict である。この値がスポーン量と交差点での分岐確率の重みになる。

- **スポーン量**: 流入パス（O.4）× 車種ごとに、単位時間あたり期待スポーン数 = `path_traffic[車種] × spawnRateK` とする。
- **分岐の重み**: 交差点で次のパスを選ぶとき、各候補パスの `path_traffic[車種]` を重みにした重み付きランダムで曲がる方向を決める。

!!! note "バルク配信について"
    `path_traffic` は現状パス詳細（`PathData`）にのみ含まれ、バルクのパス一覧（bbox の `PathSummary`）には載っていない。車描画はパス一覧を使うため、`PathSummary` に `path_traffic` を追加して配信する必要がある。Unity 側は**リフレクションで読み取り、フィールドが未追加でも落ちない**（無ければ集計値 `utilization` を乗用車の代替として使う暫定 degrade）実装とし、スキーマ追加後に自動で車種別精度になる。

### 走行方向 `travel_direction`

パスのジオメトリ方向（`start_point → end_point`）に対する通行可否。`DIR_BOTH=0`（双方向）/ `DIR_FORWARD=1`（start→end のみ）/ `DIR_BACKWARD=2`（end→start のみ）。スポーン時・交差点分岐時に、そのパスへ入る向きが許可されるかの判定に使う（O.4）。`PathSummary` に既に含まれる。

### 通行方向（左側/右側） `traffic_hand`

車をどちらの車線に寄せるかの都市設定。`city` の `traffic_hand`（`0`=左側通行 / `1`=右側通行、既定=左）。`GET /api/v1/city` で配信し、Unity は起動時に一度取得する（取得失敗時は既定=左側通行）。変更UIは後日実装予定。

## O.3. 描画範囲

「カメラから一定距離」の範囲は、**カメラ注視点を中心とした半径**で定義する。

- 中心: `CameraCellChecker.CurrentWorldPosition`（カメラ中央のレイと地面 y=0 平面の交点）。
- 半径: `renderRadius`（既定 400m ≒ [LOD処理](E_LOD処理.md) の Dynamic 相当）。
- デスポーン: 車のワールド座標（XZ距離）が `renderRadius + despawnMargin`（既定 +80m）を超えたらデスポーンする。

判定は XZ 平面距離のみ（高さは無視）で行う。

## O.4. スポーン・走行・デスポーン

### 流入パスの抽出

一定間隔（`entryScanInterval`、既定 0.5 秒）で「流入パス」を再スキャンしてキャッシュする。流入パスとは、ロード済みの走行可能な道路（`PATH_MOTORWAY` / `PATH_STREET`、`STATUS_ACTIVE`）で、**中心線の一方の端点が描画範囲の外・他方が内**にあるものを指す。

- 外側の端から内側へ入る向きを進行方向とする（start が外側 → 前進、end が外側 → 後退）。
- その向きが `travel_direction` で許可されないパスは流入対象から外す。
- スポーン始点は、中心線と半径境界の交点（隣接頂点の距離を線形補間して近似）とする。これにより長い直線区間でも車が**境界上**から現れる。

### スポーン

流入パス × 車種ごとに、毎フレーム確率 `p = path_traffic[車種] × spawnRateK × Δt` でスポーンを抽選する（フレームレート非依存のポアソン過程）。同時存在数が `maxActiveVehicles`（既定 300）に達したら抑止する。

生成した車には車種係数（トラック/バスは遅め）と個体ばらつきを掛けた速度を与える。

### 走行

各フレーム、車は中心線に沿って進行方向へ距離を進める。位置は中心線を距離パラメータで補間して求め、**進行方向に対して `traffic_hand` の側へ横オフセット**（左側通行なら進行方向左）した車線中心に配置し、接線方向へ向ける。

### 交差点での分岐

パスの端（ノード）へ到達したら、次のパスへ乗り換える。

- 候補は `PathManager.GetPathsConnectedToNode(nodeId)` で得た、そのノードに接続する走行可能な道路。**現在のパスは除外**（Uターン回避）。
- 各候補について、ノードが `start_node_id` 側なら前進・`end_node_id` 側なら後退で入るものとし、その向きを `travel_direction` が許可するものだけ残す。
- 残った候補を `path_traffic[車種]`（+ 微小値 ε で交通量0の道路も残す）を重みにした重み付きランダムで1つ選ぶ。
- 有効な候補が無い（行き止まり等）ときはデスポーンする。

### travel_direction フィルタ

ノード N からパスへ入れるかの判定：

| 入る向き | 許可される `travel_direction` |
| --- | --- |
| 前進（N が `start_node_id`、start→end） | `DIR_BOTH` または `DIR_FORWARD` |
| 後退（N が `end_node_id`、end→start） | `DIR_BOTH` または `DIR_BACKWARD` |

## O.5. モデル解決と拡張性

車種（+バリアント）から描画用オブジェクトを解決する層を持ち、[建物メッシュ](F_建物メッシュの生成.md)・[地形プロップ](M_地形プロップ.md) と同じく `ModelManager.GetModel(論理パス)` を単一の入口とする。解決順は次のとおり：

1. `Models/Vehicles/{車種}/{バリアント}`（車種内で複数モデルを持つ場合）
2. `Models/Vehicles/{車種}`（車種単一モデル）
3. `Models/Vehicles/{VEHICLE_STANDARD}`（汎用車フォールバック）
4. いずれも無ければ**単純な箱**（車寸の Cube、車種別に色分け。fbx 導入までの暫定）

車種内の複数バリアントは、[地形プロップ](M_地形プロップ.md) の `seed` 方式と同様に、車1台の乱数シードから決定的に1つを選ぶ。どの車種にどのバリアントがあるかは任意の設定ファイル（`Resources/Config/vehicle_model_config.yaml`、`agent_model_config.yaml` と同形式）で「車種ID → バリアント一覧」を宣言する。未定義の車種は上記フォールバックで動作する。

この設計により、**箱 → 車種別 fbx → 車種内ランダム複数 fbx** を段階的に拡張できる。物理配置は将来のコンテンツパック（[追加コンテンツとアセットパック](../programs/追加コンテンツとアセットパック.md)）の論理パス解決にそのまま乗る。

生成した車は車種+バリアント単位でプールし、デスポーン時に破棄せず再利用する。

## O.6. データフローと関連コンポーネント

```
GET /api/v1/city ─ traffic_hand ─┐
                                 ▼
PathManager（取得済み pathDataCache: path_traffic / travel_direction / ノードグラフ / 中心線）
   │  GetAllPaths / TryGetPathPolyline / GetPathData / GetPathsConnectedToNode
   ▼
VehicleManager（毎フレーム: 流入抽出 → スポーン → 走行 → 交差点分岐 → デスポーン, プール管理）
   │  ModelManager.GetModel("Models/Vehicles/{車種}/{バリアント}")
   ▼
車オブジェクト（箱 or fbx）を中心線上に配置
```

- 中心線・ノードグラフ・パス属性はすべて `PathManager`（[バックエンドデータロード](H_バックエンドデータロード.md)）が既に取得済みのものを再利用する。中心線は curved パスではベジエサンプリング済み・地形追従済みの点列（[パス形状](A_パス形状.md)）。
- 描画範囲の中心は `CameraCellChecker`、範囲区分の目安は[LOD処理](E_LOD処理.md) の Dynamic 距離に準じる。

## O.7. 調整パラメータ

| パラメータ | 既定 | 意味 |
| --- | --- | --- |
| `renderRadius` | 400m | 車を走らせる半径 |
| `despawnMargin` | 80m | この余白を超えたらデスポーン |
| `spawnRateK` | 0.02 | `path_traffic` → 秒あたり期待スポーン数への変換係数 |
| `maxActiveVehicles` | 300 | 同時存在数の上限 |
| `entryScanInterval` | 0.5s | 流入パスの再スキャン間隔 |
| `baseSpeed` | 12 m/s | 基準速度（車種係数・個体ばらつきが乗る） |
| `laneOffset` | 2.5m | 中心線からの車線オフセット |

`path_traffic` はスケール不定の抽象量のため、`spawnRateK` と `maxActiveVehicles` は実データに合わせて調整する前提である。

## O.8. 現在の実装状況と今後

- 実装済み: 流入スポーン／中心線走行（左側通行のレーンオフセット）／交差点の確率分岐（`travel_direction` 尊重）／範囲外デスポーン／プール／箱フォールバック描画／`traffic_hand` の city 設定・配信。
- 未実装（拡張余地）: `PathSummary` への `path_traffic` 追加（追加までは `utilization` 代替で degrade 動作）／車種別・車種内ランダムの fbx プレハブ（`Models/Vehicles/...` と `vehicle_model_config.yaml` の実データ）／通行方向（左右）の変更UI／線路上の車両描画（同じ仕組みを `RAIL_VEHICLE_KEYS` と線路型に差し替えて拡張可能）。
