# VisualHandlers フォルダ

3D空間内の建物・道路・土地区画などの「見せ方」(表示/非表示/ハイライト/データ可視化等)を一括管理する。
編集モードに入ったとき(例: パーセル編集時に全区画の境界を表示する)などに、全オブジェクトの描画方法をまとめて切り替えるための仕組み。

## 仕組み

- `VisualModeHandler`(シングルトン)がオブジェクトタイプ(`InputObjectType`)ごとに現在の**色モード**(`SetMode`/`GetMode`)と**表示フラグ**(`SetFlags`/`GetFlags`、`VisualFlag` のビット和)を保持し、登録済みの `IVisualModeHandler` 群へ `ApplyVisualMode(mode)` を通知する。
- **色モードとフラグは直交する2系統**: 色モードは排他1択(通常/色分け各種/非表示)、フラグは独立ON/OFF(計画中表示 `SHOW_PLAN` / 公共施設ハイライト `HIGHLIGHT_PUBLIC` / 制限速度標識 `SPEED_LIMIT_SIGNS`)。フラグは `ApplyVisualMode` の引数には乗らず、`VisualModeObject` と `PublicFacilityGlowOverlay` が `GetFlags` をプル参照する(そのため `IVisualModeHandler` インターフェースはフラグを知らない。ParcelManager/ZoneManager にも影響しない)。
- モード/フラグ変更時の通知は1フレームあたり `batchSize`(既定100)件ずつのバッチ処理。**一括変更しても即座に全部は切り替わらない。**
- `SetMode` / `SetFlags` は前回と同じ値ならスキップする(差分チェック)。
- Destroy済み参照は10秒ごとの `PeriodicCleanup` で除去される。

## オブジェクト単位の表示フィルタ(ObjectVisibilityFilter)

- レイヤー単位の色モード・表示フラグとは**直交する第3の系統**。非表示にする `agent_id` / `path_id` の集合を静的クラス `ObjectVisibilityFilter` が持ち、`VisualModeObject.SetFilterHidden` で反映する。
- **`UpdateVisuals` の最優先分岐**で、建設中・計画中の常時表示やハイライトを含む何よりも先に Renderer とコライダーを落とす(プレイヤーが明示的に消した状態なので、クリックも拾わせない)。
- 入力は UI(Tauri)からの `view:set_object_filter`(建設プロジェクトのタグ単位／詳細パネルの個別切替)だけで、**差分ではなくIDの全集合**が毎回届く。状態の正はUnity側にあり、`view:object_filter_state` でUIへ返す。**バックエンドにもセーブにも残らない**(タグ自体は保存されるが、隠しているかどうかは保存しない)。
- 差分ロードで後から生成されるオブジェクトにも効かせるため、**生成側(`BuildingManager` / `PathManager`)が生成直後に `IsHidden` を引く**。集合が変わったときは各マネージャーの `RefreshVisibilityFilter()` がキャッシュ済みオブジェクトへ反映する(シーン全体は走査しない)。
- **遠景LODの建物は `VisualModeObject` を持たない**(`FarBuildingBatcher` が輪郭メッシュを結合するため)ので `MeshRenderer.enabled` を直接落とす(`ObjectVisibilityFilter.ApplyToObject`)。`FarBuildingMember` が `enabled` の変化を検知して結合バッチから自動的に外れる。
- セッションのリセット(新規都市・ロード)では `IpcStateCoordinator.HandleSessionReset` が必ずクリアする。IDは都市ごとに別物のため。
- アラートアイコン(`AlertManager`)はこのフィルタと連動しない(レイヤー単位の非表示でも同じ)。

## IVisualModeHandler の実装者は2種類ある(重要)

1. **`VisualModeObject`** — 建物・道路・交差点・地形タイル等、個々のGameObjectに付くコンポーネント。生成元(BuildingManager/PathManager/TerrainManager)が AddComponent し、`Start()` で自動的に VisualModeHandler に登録される。
2. **マネージャー自身** — `ParcelManager` と `ZoneManager` は自分が `IVisualModeHandler` を実装して1件だけ登録される。パーセルの枠線・データ可視化はGameObject単位ではなく `OverlayMeshRenderer` による一括描画のため。

## VisualModeObject の挙動(知らないと事故る)

- `InitializeMaterials()` が呼ばれた時点の `sharedMaterials` を「オリジナルマテリアル」として記憶し、ハイライト解除時にこれへ戻す。**メッシュやマテリアルを後から差し替えた場合は `InitializeMaterials()` を呼び直さないと、古いマテリアルに戻されてしまう。**
- `useChildRenderers` / `ignoreSelfRenderer`: 道路や建物は子オブジェクト(SplineMeshのパーツ等)にRendererがあるため、両方trueにして子だけを対象にするのが通例。
- `preventMaterialChange`: 表示/非表示のみ制御しマテリアルを変えない(交差点メッシュ等)。SpriteRenderer・TextMeshPro・進行方向矢印(`TrackDirectionArrows`付きレンダラー)は自動的に対象外。
- **ステータス連動**: `SetStatus(status)` で設定されたオブジェクトステータスがモードより優先される。
  - `STATUS_CONSTRUCTING` / `STATUS_DEMOLISHING` / `STATUS_COLLAPSED` → 常に表示+**オリジナルマテリアル**(ホバー中はハイライト)。メッシュ自体が専用の表示(建設中/解体中=土マウンド/土ストリップ+足場+建機等、`ConstructingBuildingMeshBuilder` / `PathManager.GenerateConstructingPathMesh`。倒壊=瓦礫マウンド+倒壊片、`CollapsedBuildingMeshBuilder`)のため、色モードで塗り潰さずオリジナルを出す(旧仕様の `ConstructionMaterial`(黄色)一括上塗りは廃止)。マテリアルアセット自体は `ConstructionDirt` 未配置時のフォールバック先(`BuildingMeshBuilder.GetConstructionDirtMaterial`)として存続している
  - `STATUS_TEMPORARY` / `STATUS_PENDING` / `STATUS_SCHEDULED_CONSTRUCTION` → `VisualFlag.SHOW_PLAN` が立っている(かつ色モードが非表示でない)ときだけ表示(temporary=赤 `PendingMaterial` / pending・planned=灰色 `PlannedMaterial`)。フラグOFFまたは非表示モードではRendererとコライダーが無効化される。**どちらも不透明(ZWrite有効・影あり)**。半透明ゴースト表示は形状が読み取れないため廃止した
- **公共施設ハイライト(`VisualFlag.HIGHLIGHT_PUBLIC`)は `VisualModeObject` の責務ではない**。建物のマテリアルは触らず、`PublicFacilityGlowOverlay` が敷地の土地区画形状に光を描く(下記)。そのため色モード(地区別色塗り・耐久度色分け等)と同時に成立する。
- ホバーハイライトは `SetHover(true/false)`(InputHandler系から呼ばれる)。
- 数値ベースの色分けは `MaterialPropertyBlock`(_BaseColor/_Color)で行い、マテリアルのインスタンス化を避けている。

## PublicFacilityGlowOverlay(公共施設ハイライト)

- `VisualFlag.HIGHLIGHT_PUBLIC` の描画専任の常駐オーバーレイ。`BuildingManager.Awake` の `PublicFacilityGlowOverlay.Ensure()` で自動生成される(シーンへの事前配置は不要・多重生成なし)。
- **VisualModeHandler には登録しない**。`ApplyVisualMode` のバッチ通知は建物数が多いと末尾のオブジェクトまで数秒かかるため、`Update` で `GetFlags`/`GetMode` を直接ポーリングして有効・無効を決める。
- 表示条件は「フラグON かつ 建物の色モードが非表示でない」+ 建物が `public_facility` かつ `STATUS_ACTIVE`。
- 描画は敷地の土地区画ポリゴンに沿った「地面フィル + 上端で透明になる光の柱」。柱の高さは区画の対角長に比例(10〜34m)。マテリアルは `Custom/OverlayLine` の `_ZTest=Always` + 加算合成(`_DstBlend=One`)。
- **敷地の解決は建物中心からの空間判定(`ParcelManager.GetParcelIdAtPoint`)**。`GET /buildings/` の `multi_agent` スキーマには `parcel_id` が含まれないため `BuildingSummary.parcel_id` は当てにできない(区画が未ロードなら建物底面ポリゴンにフォールバック)。
- メッシュはカメラ非依存なので毎フレーム作らず 0.4 秒間隔で再構築(建物・区画のストリーミング追従はこの周期)。明滅はメッシュではなくマテリアルの `_Tint` を毎フレーム更新して表現する。
- **`public_facility` は `multi_agent` スキーマに追加されるまで常に false**(=光らない)。endpoint_schemas シートへの行追加と `generate_models.py` の再生成が前提。

## モード定数

- 基本モードは `VisualMode`(MODE_NORMAL / MODE_HIDDEN / MODE_BOUNDARY / MODE_HIGHLIGHT_OWNER)、表示フラグは `VisualFlag`(SHOW_PLAN / HIGHLIGHT_PUBLIC / SPEED_LIMIT_SIGNS)。いずれも `VisualModeHandler.cs` に定義。旧 `MODE_PLAN` は「計画中表示は色モードと独立トグル」化に伴い廃止し、`VisualFlag.SHOW_PLAN` に置き換えた。
- データ可視化モードは `VisualModeEx.cs` にカテゴリ別の定数クラスがあり、**値域でカテゴリを区別**する:
  - `VisualModePath` (100番台): 交通量・速度・歩行者量・耐久度・所属地区色分け(`ZONE_COLOR=104`)
  - `VisualModeParcel` (200番台): 地価、建築可能フィルタ各種、所属地区色分け(`ZONE_COLOR=210`)、環境スコア色分け(`NOISE=211`/`AIR_POLLUTION=212`/`CRIME=213`/`SUNSHINE=214`。`GET /city/noise`等から区画単位スコアを取得して塗る) — ParcelManager が処理
  - `VisualModeBuilding` (300番台): 人口・家賃・耐久度・フロア種別色分け、所属地区色分け(`ZONE_COLOR=309`)
  - `VisualModeTerrain` (400番台): 透過・標高色分け — `VisualTerrainObject` が処理(`TRANSPARENT` のみ非表示、他は表示)
- **環境スコア(騒音=`NOISE` 211 / 大気汚染=`AIR_POLLUTION` 212 / 治安=`CRIME` 213)は区画レイヤーのモード**。バックエンドが持つのは区画中心でサンプリング済みの区画単位スコアで、面として描くための都市全体のビットマップは公開されていないため、地形ではなく区画の塗り分けで表現する。値は `ParcelSummary` に含まれず `ParcelManager` が `/city/noise`・`/pollution`・`/crime` から別途取得する(下記)
- 統計値の供給源は `VisualPathStats` / `VisualBuildingStats` / `VisualParcelStats`(各マネージャーが生成時に `InitFrom(summary)` で初期化)。`VisualBuildingObject` / `VisualParcelObject` が色決定ロジックを持つ。
- **所属地区色分け(`ZONE_COLOR`)**: 道路・建物・区画の各レイヤーに存在する。地区の識別色はバックエンドの `zone.color`(HEX文字列)を `ZoneManager.GetZoneColor(zoneId)` で Color に変換して使用する(旧4色パレットではない)。道路は `VisualPathStats.zone_id`、建物は `VisualBuildingStats.zone_id` から所属地区を取得する。区画は `ParcelSummary` に zone_id がないため、`ZoneManager.GetZoneIdAtPoint(区画中心)` で空間的に所属地区を判定する。地区編集中の新規地区プレビュー(`ZoneEditContext.PREVIEW_SENTINEL`)だけは地区がまだ存在しないため、`ZoneEditContext.PreviewColor`(ツール選択時にUnityが引いた色)で塗る。**地区編集モードでは道路だけを`ZONE_COLOR`にし、区画・建物は通常表示のままにする**(区画・建物まで塗ると画面が地区色で埋まって道路の所属変化が読めない。地区の広がりは`ZoneManager`の枠線が示す)。
- **適用直後の一時色付け(tempZoneId)**: 地区編集モードで道路をクリックした瞬間、バックエンドの応答を待たずに適用先の地区色を先行表示する(`VisualModeObject.SetTempZoneId`。ZONE_COLOR時に `ResolveEffectiveZoneId` が tempZoneId を最優先。新規作成は `ZoneEditContext.PREVIEW_SENTINEL`)。対象は `ZoneEditContext.tempColoredPathIds` で追跡し、送信結果(成功/失敗の両方)・モード終了で `ZoneEditContext.ClearTempVisuals` が `ClearTempZoneId` を呼んで明示的に戻す。**リロード任せにしてはいけない**(送信失敗時はversion不変でリロードが再描画せず一時色が残るため)。

## InputHandlersとの関係

`InputHandlers/` の入力モード(クリック時の挙動)と、ここのビジュアルモード(見た目)は**別物**。編集モードに入るときは通常、両方をセットで切り替える(入力は `InputSystemManager.SetModePreset`、見た目は `IpcStateCoordinator` が `VisualModeHandler.SetMode`/`SetFlags` をオブジェクトタイプごとに呼ぶ)。

非同期で子Rendererを追加・キャッシュ復元する場合は `RefreshMaterials()` を使う。既存Rendererの元材質を保持し、新しいRendererだけ材質を取得したうえで現在の表示を即時適用する。上塗り後の `InitializeMaterials()` は計画色などを元材質として記録してしまうため使わない。既存Rendererそのものの基底材質を置換する場合のみ、元材質へ置換した後にInitializeMaterialsを使う。既定表示用マテリアルはAwakeでロードするためStart前のSetStatusにも適用できる。

公共交通ハイライトはTransitOverlayManagerが独立レイヤーとして管理する。VisualModeの排他色モードやInputModeの編集プリセットは追加しない。詳細はDataLoaders/AGENTS.mdと公開programs/公共交通ハイライト.md。

## 道路・線路の制限速度標識

標識は`DrawLodInfo`がClose/Normalのときだけ表示する。固定区間を持つ長い道路は`SplinePartLod.GetDrawLodAtPosition`で中心の本体区間も確認する。`PathPresentation.IsReady`で差し替え生成を待ち、道路GOの置換時は表示条件の参照先も即座に更新する。既存LODの閾値・ヒステリシスを複製しない。

`VisualFlag.SPEED_LIMIT_SIGNS`（`speed_limit`）は道路レイヤーの独立トグル。`PathSpeedLimitOverlay`は`PathManager.Awake`で1つ生成され、モード/フラグを直接参照する。街路・高速道路・鉄道・モノレールの`PathSummary.max_speed`（km/h）だけを表示し、公共交通区間・歩道・人員搬送・インフラ・駅舎は対象外。`base_speed`・混雑込み速度・マスターの既定速度から補完しない。欠損・非有限・非正値は表示しない。

アイコンは`Utils.CreateOverlayCanvas`（sortingOrder=68）と`UiFontProvider`を共有し、アラートと同じ距離スケール（100m基準、0.3〜1.5倍）で描く。中心位置は固定でボビングや引出線はない。画面外・カメラ背後・道路レイヤー非表示・個別非表示では隠す。計画中などの表示条件は`VisualModeObject.IsVisualVisible`（`SetRenderersEnabled`の結果）を参照し、ステータス判定を再実装しない。

`UI/SpeedLimitSign`が数値表示、`SpeedLimitSignStyle`が意匠を担当する。現在は全言語で日本式の赤い円・白地・青数字を使い、画像アセットは不要。別意匠はStyleの実装を追加し、言語設定側から`PathSpeedLimitOverlay.SetSignStyle`へ渡せる。位置・取得・表示状態のロジックを意匠へ持ち込まない。検証は`Tests/SpeedLimitSigns/run.ps1`。
