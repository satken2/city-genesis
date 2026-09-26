# Unity表示モード

3Dビューの建物・道路・土地区画・地形・海面の「見せ方」(表示/非表示・ハイライト・データ可視化)を、オブジェクトタイプごとに一括で切り替える仕組み。実装は `unity/Assets/Scripts/VisualHandlers/`。

クリック時に何が起きるかを決める入力モード([Unity入力制御](Unity入力制御.md))とは別系統で、編集ステートに入るときは両方をセットで切り替える(同 §9.1)。

## レイヤーの色モードと表示フラグ

`VisualModeHandler`(シングルトン)がオブジェクトタイプ(`InputObjectType`)ごとに色モード(`SetMode` / `GetMode`)と表示フラグ(`SetFlags` / `GetFlags`。`VisualFlag` のビット和)を保持する。2つは直交する。

| 系統 | 性質 | 値 |
| --- | --- | --- |
| 色モード | 排他1択 | 通常 / 非表示 / 境界表示 / 所有者ハイライト、各種データ可視化 |
| 表示フラグ | 独立ON/OFF | `SHOW_PLAN`(計画中の表示) / `HIGHLIGHT_PUBLIC`(公共施設ハイライト) / `SPEED_LIMIT_SIGNS`(制限速度標識) |

UIからは `view:change_view {layer, mode, sub_mode, flags}` で届く([IPC連携](IPC連携の整理.md))。`flags` は色モードと独立したCSV(`plan` / `public` / `speed_limit`)で、`null` は現在値の維持、空文字は全OFF。

- 色モードの変更は登録済みの `IVisualModeHandler` へ `ApplyVisualMode(mode)` で通知する。通知は1フレームあたり `batchSize`(既定100)件ずつのバッチなので、一括変更しても即座に全オブジェクトは切り替わらない。
- 表示フラグは `ApplyVisualMode` の引数に乗らない。必要な側(`VisualModeObject`・`PublicFacilityGlowOverlay`・`PathSpeedLimitOverlay`)が `GetFlags` をプル参照する。そのため `IVisualModeHandler` はフラグを知らず、`ParcelManager` / `ZoneManager` にも影響しない。
- `SetMode` / `SetFlags` は前回と同じ値なら何もしない。
- 破棄済みの登録は10秒ごとに、登録集合の写しを1フレーム2,000件ずつ走査して除去する(数万件の null 判定を1フレームに集めないため)。

### モード定数の値域

基本モードは `VisualMode`(`MODE_NORMAL`=0 / `MODE_HIDDEN`=1 / `MODE_BOUNDARY`=2 / `MODE_HIGHLIGHT_OWNER`=3)、表示フラグは `VisualFlag`。いずれも `VisualModeHandler.cs` にある。

データ可視化モードは `VisualModeEx.cs` にカテゴリ別の定数クラスがあり、値域でカテゴリを区別する。

| 定数クラス | 値域 | 内容 | 色を決める側 |
| --- | --- | --- | --- |
| `VisualModePath` | 100番台 | 利用率・通過速度・歩行者量・耐久度・所属地区色分け | `VisualModeObject` |
| `VisualModeParcel` | 200番台 | 地価・建築可能フィルタ各種・所属地区色分け・環境スコア(騒音/大気汚染/治安/日照) | `ParcelManager.ResolveDataVisColor` |
| `VisualModeBuilding` | 300番台 | 人口・家賃・耐久度・フロア種別・所属地区色分け | `VisualBuildingObject` |
| `VisualModeTerrain` | 400番台 | 透過・標高色分け(`TRANSPARENT` だけ非表示、他は表示) | `VisualTerrainObject` |

環境スコアは区画レイヤーのモードである。バックエンドが公開するのは区画中心でサンプリング済みの区画単位スコアで([環境](../economics/19_環境.md))、面として塗るための都市全体のビットマップは公開されない。

そのため地形ではなく区画の塗り分けで表現する。値は `ParcelSummary` に含まれないので、`ParcelManager` が `GET /api/v1/city/noise` 等から別途取得する。

### 統計値の供給源

色決定に使う値は `VisualPathStats` / `VisualBuildingStats` / `VisualParcelStats` が持ち、各マネージャーが生成時に `InitFrom(summary)` で初期化する。道路の統計は `PathSummary` の型付きフィールドを直接読む。

| 統計 | 供給元 |
| --- | --- |
| `utilization`・`durability`・`zone_id` | `PathSummary` の同名フィールド |
| 通過速度 | `PathSummary.base_speed`。形状から決まる通過速度(km/h)で、カーブ・勾配・交差点の加減速を含むため短い区間ほど制限速度より低い |
| 歩行者量 | `path_traffic` の歩行者キー(`VEHICLE_PEDESTRIAN` を文字列にした `"0"`)。1日あたりの通過トリップ数 |

存在しないフィールドをリフレクションで読むと常に0になり色分けが無反応になるため、フィールド名で直接参照する。

## IVisualModeHandler の実装者

1. `VisualModeObject`: 建物・道路・交差点・地形タイルなど個々の GameObject に付くコンポーネント。生成元(`BuildingManager` / `PathManager` / `TerrainManager`)が `AddComponent` し、`Start()` で自動登録される。
2. マネージャー自身: `ParcelManager` と `ZoneManager` は自分が `IVisualModeHandler` を実装して1件だけ登録する。区画の枠線・データ可視化は GameObject 単位ではなく `OverlayMeshRenderer` による一括描画のため。区画個体の `VisualParcelObject` は `VisualParcelStats` を渡すだけの軽量コンポーネントで、登録しない。

## VisualModeObject の表示規則

- `InitializeMaterials()` を呼んだ時点の `sharedMaterials` を「元の材質」として記憶し、ハイライト解除時にそこへ戻す。メッシュや材質を後から差し替えたら呼び直す(呼ばないと古い材質へ戻る)。
    - 非同期で子 Renderer を追加・復元するときは `RefreshMaterials()` を使う。既存 Renderer の元材質を保ちつつ新しい Renderer だけ材質を取得し、現在の表示を即時適用する。上塗り後に `InitializeMaterials()` を呼ぶと計画色などを元材質として記録してしまう。
- `useChildRenderers` / `ignoreSelfRenderer`: 道路・建物は子(SplineMesh のパーツ等)に Renderer があるため、両方 true にして子だけを対象にするのが通例。`preventMaterialChange` は表示/非表示だけを制御して材質を変えない(交差点メッシュ等)。SpriteRenderer・TextMeshPro・進行方向矢印(`TrackDirectionArrows`)は自動的に対象外。
- 既定表示用の材質(`PendingMaterial` 等)は `Awake` で Resources から名前ごとに static キャッシュする(見つからなかった名前も再検索しない)。`Start` 前の `SetStatus` にも適用できる。
- 数値ベースの色分けは `MaterialPropertyBlock`(`_BaseColor` / `_Color`)で行い、材質のインスタンス化を避ける。`RoadMarkingSurface` を持つ Renderer は通常材質への復元時に `RoadMarkingSurface.Restore` を呼び、クリアされたプロパティブロックの標示座標・ページ表を戻す([道路メッシュの生成](../geometries/G_道路メッシュの生成.md#83-路面標示))。
- ホバーハイライトは `SetHover(true/false)`(入力ハンドラが呼ぶ)。
- ステータス連動: `SetStatus(status)` で設定したステータスが色モードより優先される。

| ステータス | 表示 |
| --- | --- |
| `STATUS_CONSTRUCTING` / `STATUS_DEMOLISHING` / `STATUS_COLLAPSED` | 常に表示し、元の材質のまま(ホバー中はハイライト)。メッシュ自体が工事中・解体中・倒壊の専用形状なので色モードで塗り潰さない |
| `STATUS_TEMPORARY` / `STATUS_PENDING` / `STATUS_SCHEDULED_CONSTRUCTION` | `VisualFlag.SHOW_PLAN` が立ち、かつ色モードが非表示でないときだけ表示。temporary は赤の `PendingMaterial`、pending・scheduled は灰色の `PlannedMaterial`(いずれも不透明・ZWrite有効・影あり)。それ以外では Renderer とコライダーを無効化する |

## 所属地区色分け(ZONE_COLOR)

道路・建物・区画の各レイヤーにある。地区の識別色はバックエンドの `zone.color`(HEX文字列)を `ZoneManager.GetZoneColor(zoneId)` で変換して使う。

道路は `VisualPathStats.zone_id`、建物は `VisualBuildingStats.zone_id` から所属地区を取り、区画は `ParcelSummary` に `zone_id` が無いため `ZoneManager.GetZoneIdAtPoint(区画中心)` で空間的に判定する。

- 地区編集モードでは道路だけを `ZONE_COLOR` にし、区画・建物は通常表示のまま。区画・建物まで塗ると画面が地区色で埋まり、道路の所属変化が読めない(地区の広がりは `ZoneManager` の枠線が示す)。
- 新規地区のプレビュー(`ZoneEditContext.PREVIEW_SENTINEL`)は地区がまだ無いため `ZoneEditContext.PreviewColor` で塗る。
- 適用直後の一時色: 地区編集で道路をクリックした瞬間、バックエンドの応答を待たずに適用先の地区色を先行表示する(`VisualModeObject.SetTempZoneId`。`ZONE_COLOR` 時は `tempZoneId` を最優先)。
    - 対象は `ZoneEditContext.tempColoredPathIds` で追跡し、送信結果(成功・失敗の両方)とモード終了で `ClearTempVisuals` が `ClearTempZoneId` を呼んで明示的に戻す。送信失敗時は version が変わらずリロードが再描画しないため、リロード任せにしない。

## 公共施設ハイライト(PublicFacilityGlowOverlay)

`VisualFlag.HIGHLIGHT_PUBLIC` の描画専任の常駐オーバーレイ。`BuildingManager.Awake` の `PublicFacilityGlowOverlay.Ensure()` で自動生成する(シーンへの配置不要・多重生成なし)。建物の材質は触らず、敷地の土地区画形状に光を描くので、色モード(地区色分け・耐久度色分け等)と同時に成立する。

- `VisualModeHandler` には登録しない。バッチ通知は建物数が多いと末尾まで数秒かかるため、`Update` で `GetFlags` / `GetMode` を直接ポーリングして有効・無効を決める。
- 表示条件は「フラグON かつ 建物の色モードが非表示でない」に加え、建物が `public_facility` かつ `STATUS_ACTIVE`。
- 描画は敷地ポリゴンに沿った地面フィルと、上端で透明になる光の柱(高さは区画の対角長に比例して10〜34m)。材質は `Custom/OverlayLine` の `_ZTest=Always` + 加算合成(`_DstBlend=One`)。
- 明滅は材質の `_Tint` を毎フレーム更新して表現し、メッシュはカメラ非依存なので0.4秒間隔で作り直す(建物・区画のストリーミングへの追従もこの周期)。
- 敷地は建物中心からの空間判定(`ParcelManager.GetParcelIdAtPoint`)で解決する。`GET /buildings/` の `multi_agent` スキーマに `parcel_id` が無いため `BuildingSummary.parcel_id` は当てにしない(区画が未ロードなら建物底面ポリゴンにフォールバック)。
- `public_facility` が同スキーマに無い間は常に false になり光らない。

## そのほかの常駐オーバーレイ

いずれも `VisualModeHandler` に登録せず、必要な状態を直接参照する。

| クラス | 役割 | 仕様 |
| --- | --- | --- |
| `ObjectVisibilityFilter` | 建物・道路をID単位で非表示にする第3の系統。`UpdateVisuals` の最優先分岐で Renderer とコライダーを落とす | [建設プロジェクト管理](建設プロジェクト管理.md) |
| `CityBoundaryOverlay` | 都市範囲の境界の破線を常時描く | [地形形状](../geometries/D_地形形状.md#都市外の背景地形) |
| `RoadFocusOverlay` | 名前付き道路の区間を道路色の帯で強調 | [道路の詳細と道路名](道路の詳細と道路名.md) |
| `PathSpeedLimitOverlay` | 道路・線路の制限速度標識(`VisualFlag.SPEED_LIMIT_SIGNS`) | 下記 |
| `TransitOverlayManager`(DataLoaders) | 公共交通の路線帯・停留所・名前。独立レイヤーで、色モードやプリセットを追加しない | [公共交通ハイライト](公共交通ハイライト.md) |

### 制限速度標識

`PathSpeedLimitOverlay` は `PathManager.Awake` で1つ生成され、モードとフラグを直接参照する。街路・高速道路・鉄道・モノレールの `PathSummary.max_speed`(km/h)だけを表示し、公共交通区間・歩道・人員搬送・インフラ・駅舎は対象外。

`base_speed`・混雑込み速度・マスターの既定速度から補完せず、欠損・非有限・非正値は表示しない。

- 表示は描画LODが Close/Normal のときだけ。固定区間を持つ長い道路は中心の本体区間も確認し、差し替え生成中は `PathPresentation.IsReady` を待つ([LOD処理](../geometries/E_LOD処理.md#道路))。計画中などの表示条件は `VisualModeObject.IsVisualVisible` を参照し、ステータス判定を再実装しない。
- アイコンは `Utils.CreateOverlayCanvas`(sortingOrder 68)と `UiFontProvider` を共有し、アラートと同じ距離スケール(100m基準、0.3〜1.5倍)で描く。中心位置は固定でボビングや引出線は無い。画面外・カメラ背後・道路レイヤー非表示・個別非表示では隠す。
- 数値表示は `UI/SpeedLimitSign`、意匠は `SpeedLimitSignStyle`(現在は全言語で日本式の赤い円・白地・青数字。画像アセット不要)。別意匠は Style の実装を追加して `PathSpeedLimitOverlay.SetSignStyle` へ渡す。位置・取得・表示状態のロジックを意匠へ持ち込まない。
- 検証は `unity/Tests/SpeedLimitSigns/run.ps1`。

## 編集ステートとの関係

編集ステートに入るときの表示プリセットと、通常モードへ戻ったときの復帰は `IpcViewCoordinator` が担う([Unity入力制御](Unity入力制御.md#91-ステート別の表示プリセット))。

`IpcStateCoordinator` の各 `ApplyState*` は入力モードとマネージャーの初期化だけを担い、`VisualModeHandler` を直接触らない。
