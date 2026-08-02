# VisualHandlers フォルダ

3D空間内の建物・道路・土地区画などの「見せ方」(表示/非表示/ハイライト/データ可視化等)を一括管理する。
編集モードに入ったとき(例: パーセル編集時に全区画の境界を表示する)などに、全オブジェクトの描画方法をまとめて切り替えるための仕組み。

## 仕組み

- `VisualModeHandler`(シングルトン)がオブジェクトタイプ(`InputObjectType`)ごとに現在のモードを保持し、登録済みの `IVisualModeHandler` 群へ `ApplyVisualMode(mode)` を通知する。
- モード変更時の通知は1フレームあたり `batchSize`(既定100)件ずつのバッチ処理。**一括変更しても即座に全部は切り替わらない。**即時反映が必要な場合のみ `ForceImmediateUpdate` を使う。
- `SetMode` は前回と同じモードならスキップする(差分チェック)。プリセットは `SetPreset` / 一時切替は `SaveAndSetPreset` → `RestoreSavedPreset`。
- Destroy済み参照は10秒ごとの `PeriodicCleanup` で除去される。

## IVisualModeHandler の実装者は2種類ある(重要)

1. **`VisualModeObject`** — 建物・道路・交差点・地形タイル等、個々のGameObjectに付くコンポーネント。生成元(BuildingManager/PathManager/TerrainManager)が AddComponent し、`Start()` で自動的に VisualModeHandler に登録される。
2. **マネージャー自身** — `ParcelManager` と `ZoneManager` は自分が `IVisualModeHandler` を実装して1件だけ登録される。パーセルの枠線・データ可視化はGameObject単位ではなく `OverlayMeshRenderer` による一括描画のため。

## VisualModeObject の挙動(知らないと事故る)

- `InitializeMaterials()` が呼ばれた時点の `sharedMaterials` を「オリジナルマテリアル」として記憶し、ハイライト解除時にこれへ戻す。**メッシュやマテリアルを後から差し替えた場合は `InitializeMaterials()` を呼び直さないと、古いマテリアルに戻されてしまう。**
- `useChildRenderers` / `ignoreSelfRenderer`: 道路や建物は子オブジェクト(SplineMeshのパーツ等)にRendererがあるため、両方trueにして子だけを対象にするのが通例。
- `preventMaterialChange`: 表示/非表示のみ制御しマテリアルを変えない(交差点メッシュ等)。SpriteRenderer・TextMeshPro・進行方向矢印(`TrackDirectionArrows`付きレンダラー)は自動的に対象外。
- **ステータス連動**: `SetStatus(status)` で設定されたオブジェクトステータスがモードより優先される。
  - `STATUS_CONSTRUCTING` → 常に表示+**オリジナルマテリアル**(ホバー中はハイライト)。メッシュ自体が専用の「工事現場」表示(土マウンド/土ストリップ+足場+クレーン等。`ConstructingBuildingMeshBuilder` / `PathManager.GenerateConstructingPathMesh`)のため、旧仕様の `ConstructionMaterial`(黄色)一括上塗りは廃止。マテリアルアセット自体は `ConstructionDirt` 未配置時のフォールバック先(`BuildingMeshBuilder.GetConstructionDirtMaterial`)として存続している
  - `STATUS_TEMPORARY` / `STATUS_PENDING` / `STATUS_SCHEDULED_CONSTRUCTION` → `MODE_PLAN` のときだけ表示(temporary/pending=赤 / planned=青マテリアル)。それ以外のモードではRendererとコライダーが無効化される
- ホバーハイライトは `SetHover(true/false)`(InputHandler系から呼ばれる)。
- 数値ベースの色分けは `MaterialPropertyBlock`(_BaseColor/_Color)で行い、マテリアルのインスタンス化を避けている。

## モード定数

- 基本モードは `VisualMode`(MODE_NORMAL / MODE_HIDDEN / MODE_BOUNDARY / MODE_HIGHLIGHT_OWNER / MODE_PLAN)、プリセットは `VisualModePreset`(DEFAULT / PARCEL_EDIT / OWNER_VIEW / PLAN_VIEW)。いずれも `VisualModeHandler.cs` に定義。
- データ可視化モードは `VisualModeEx.cs` にカテゴリ別の定数クラスがあり、**値域でカテゴリを区別**する:
  - `VisualModePath` (100番台): 交通量・速度・歩行者量・耐久度・所属地区色分け(`ZONE_COLOR=104`)
  - `VisualModeParcel` (200番台): 地価、建築可能フィルタ各種、所属地区色分け(`ZONE_COLOR=210`) — ParcelManager が処理
  - `VisualModeBuilding` (300番台): 人口・家賃・耐久度・フロア種別色分け、所属地区色分け(`ZONE_COLOR=309`)
  - `VisualModeTerrain` (400番台) / `VisualModeEnvironment` (500番台)
- 統計値の供給源は `VisualPathStats` / `VisualBuildingStats` / `VisualParcelStats`(各マネージャーが生成時に `InitFrom(summary)` で初期化)。`VisualBuildingObject` / `VisualParcelObject` が色決定ロジックを持つ。
- **所属地区色分け(`ZONE_COLOR`)**: 道路・建物・区画の各レイヤーに存在する。地区の識別色はバックエンドの `zone.color`(HEX文字列)を `ZoneManager.GetZoneColor(zoneId)` で Color に変換して使用する(旧4色パレットではない)。道路は `VisualPathStats.zone_id`、建物は `VisualBuildingStats.zone_id` から所属地区を取得する。区画は `ParcelSummary` に zone_id がないため、`ZoneManager.GetZoneIdAtPoint(区画中心)` で空間的に所属地区を判定する。地区編集中のドラッグプレビュー(`ZoneEditContext.PREVIEW_SENTINEL`)のみ旧パレット(`VisualModeObject.zoneMaterials`)を使う。
- **地区拡張ドラッグ中の一時色付け(tempZoneId)**: 地区編集の拡張ツールでShift+ドラッグ中、なぞった道路・面する区画・その上の建物へ地区色を**フロントだけで**波及させる(バックエンド通信なし)。道路・建物は `VisualModeObject.SetTempZoneId`(ZONE_COLOR時に `ResolveEffectiveZoneId` が tempZoneId を最優先)、**区画は VisualModeObject を持たないため `ParcelManager.SetTempZoneOverride`**(`ResolveDataVisColor` の ZONE_COLOR で `GetZoneIdAtPoint` より優先)で色付ける。一時色付けした対象は `ZoneEditContext` の台帳(道路/建物)+ ParcelManager の `tempZoneOverrides`(区画)で追跡し、ドラッグ確定(成功/失敗の両方)・キャンセル・モード終了で `ZoneEditContext.ClearDragTempVisuals` が `ClearTempZoneId` / `ClearTempZoneOverrides` を呼んで明示的に戻す。**リロード任せにしてはいけない**(送信失敗時はversion不変でリロードが再描画せず一時色が残るため)。

## InputHandlersとの関係

`InputHandlers/` の入力モード(クリック時の挙動)と、ここのビジュアルモード(見た目)は**別物**。編集モードに入るときは通常、両方をセットで切り替える(入力は `InputSystemManager.SetModePreset`、見た目は `VisualModeHandler.SetPreset`)。
