# VisualHandlers

3D空間の建物・道路・土地区画・地形・海面の「見せ方」(表示/非表示・ハイライト・データ可視化)をオブジェクトタイプごとに一括で切り替える。仕様の正は公開 `programs/Unity表示モード.md`(docs パスは `city-genesis/docs/docs/` 起点)。作業は Skill genesis-visual-mode に従う。

入力モード(`InputHandlers/`。クリック時に何が起きるか)とは別系統。編集モードでは `IpcViewCoordinator` が両方をセットで切り替え、`IpcStateCoordinator` の `ApplyState*` は `VisualModeHandler` を直接触らない(`programs/Unity入力制御.md` §9.1)。

## ファイル

| ファイル | 役割 |
| --- | --- |
| `VisualModeHandler.cs` | シングルトン。タイプごとの色モード(`SetMode`)と表示フラグ(`SetFlags`)を保持し、登録された `IVisualModeHandler` へバッチ通知する。`VisualMode` / `VisualFlag` 定数もここ |
| `VisualModeEx.cs` | データ可視化モードの定数。値域でカテゴリ分け(Path=100番台 / Parcel=200番台 / Building=300番台 / Terrain=400番台) |
| `IVisualModeHandler.cs` | `ApplyVisualMode(mode)` の契約。実装者は `VisualModeObject` と、`ParcelManager` / `ZoneManager` 自身の2種 |
| `VisualModeObject.cs` | 個々の GameObject に付く実装。元材質の記憶・ステータス連動・ホバー・一時地区色・フィルタ非表示 |
| `VisualBuildingObject.cs` / `VisualTerrainObject.cs` / `VisualSeaObject.cs` / `VisualParcelObject.cs` | タイプ別の派生・補助。区画個体は `VisualParcelStats` を渡すだけで登録しない |
| `VisualPathStats.cs` / `VisualBuildingStats.cs` / `VisualParcelStats.cs` | 色決定に使う統計値。各 Manager が生成時に `InitFrom(summary)` で初期化 |
| `ObjectVisibilityFilter.cs` | ID 単位の非表示(第3の系統)。状態の正は Unity で、UI からは全集合が届く |
| `PublicFacilityGlowOverlay.cs` | 公共施設ハイライト(`VisualFlag.HIGHLIGHT_PUBLIC`)の常駐オーバーレイ |
| `PathSpeedLimitOverlay.cs` | 制限速度標識(`VisualFlag.SPEED_LIMIT_SIGNS`) |
| `RoadFocusOverlay.cs` | 名前付き道路の区間強調帯 |
| `CityBoundaryOverlay.cs` | 都市範囲の境界破線(常時) |

## 知らないと事故ること

- 色モードとフラグは直交する。フラグは `ApplyVisualMode` の引数に乗らず、必要な側が `GetFlags` をプル参照する。
  - モード変更の通知は1フレーム `batchSize`(100)件ずつで、一括変更しても即座に全部は切り替わらない。数万件を即時に扱うオーバーレイは登録せず `Update` でポーリングする(`PublicFacilityGlowOverlay` の方式)。
- `VisualModeObject` は `InitializeMaterials()` 時点の `sharedMaterials` を元材質として記憶する。メッシュ・材質を差し替えたら呼び直す。
  - 非同期で子 Renderer を追加・復元するときは `RefreshMaterials()`。上塗り後に `InitializeMaterials()` を呼ぶと計画色を元材質として記録してしまう。基底材質そのものを置換する場合だけ、置換後に `InitializeMaterials()`。
- `SetStatus` のステータスは色モードより優先される。建設中・解体中・倒壊は常に元材質(メッシュ自体が専用形状)、計画中3種は `SHOW_PLAN` が立っているときだけ表示。ステータス判定を他所で再実装せず `IsVisualVisible` を参照する。
- 数値の色分けは `MaterialPropertyBlock`。`RoadMarkingSurface` 付き Renderer の復元では `RoadMarkingSurface.Restore` を呼ばないと路面標示が消える。
- `ObjectVisibilityFilter` は `UpdateVisuals` の最優先分岐(クリックも拾わせない)。セッションリセットで必ずクリアする(`IpcStateCoordinator.HandleSessionReset`)。
  - 遠景LODの建物は `VisualModeObject` を持たないので `MeshRenderer.enabled` を直接落とし、呼んだ側が直後に `FarBuildingMember.RefreshObject(go)` で結合バッチへ通知する。差分ロードで後から生成されるオブジェクトには生成側(`BuildingManager` / `PathManager`)が `IsHidden` を引く。
- 所属地区色分け(`ZONE_COLOR`)の色は `zone.color` を `ZoneManager.GetZoneColor` で変換する。地区編集中は道路だけ塗り、区画・建物は通常表示。
  - 適用直後の一時色(`SetTempZoneId`)は `ZoneEditContext.ClearTempVisuals` が送信結果とモード終了で明示的に戻す。送信失敗時は version が変わらずリロードが再描画しないので、リロード任せにしない。
- `PublicFacilityGlowOverlay` の敷地解決は `ParcelManager.GetParcelIdAtPoint`(`multi_agent` スキーマに `parcel_id` が無いため `BuildingSummary.parcel_id` は当てにしない)。`public_facility` が同スキーマに無い間は常に false で光らない。
- 環境スコア(騒音・大気汚染・治安・日照)は区画レイヤーのモード。`ParcelSummary` に無く、`ParcelManager` が `/city/noise` 等から別途取得する。
- `VisualPathStats` の速度は `PathSummary.base_speed`、歩行者量は `path_traffic["0"]`。存在しないフィールドをリフレクションで読むと0になり色分けが無反応になるので、フィールドは直接参照する。
- 公共交通ハイライト(`DataLoaders/TransitOverlayManager`)は独立レイヤー。VisualMode の排他色モードや InputMode の編集プリセットを追加しない。

## 参照

- 仕様: `programs/Unity表示モード.md`。個別: `programs/建設プロジェクト管理.md`(表示フィルタ)、`programs/道路の詳細と道路名.md`(強調帯)、`geometries/D_地形形状.md`(都市境界線)、`geometries/E_LOD処理.md`(標識のLOD)、`programs/公共交通ハイライト.md`
- 検証: `unity/Tests/SpeedLimitSigns/run.ps1`
