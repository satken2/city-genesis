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
| `VisualModeTerrain` | 400番台 | 地下表示(`TRANSPARENT`)・標高色分け | `VisualTerrainObject` / `UndergroundView` |

環境スコアは区画レイヤーのモードである。バックエンドが公開するのは区画中心でサンプリング済みの区画単位スコアで([環境](../economics/19_環境.md))、面として塗るための都市全体のビットマップは公開されない。

そのため地形ではなく区画の塗り分けで表現する。値は `ParcelSummary` に含まれないので、`ParcelManager` が `GET /api/v1/city/noise` 等から別途取得する。

### 統計値の供給源

色決定に使う値は `VisualPathStats` / `VisualBuildingStats` / `VisualParcelStats` が持ち、各マネージャーが生成時に `InitFrom(summary)` で初期化する。道路の統計は `PathSummary` の型付きフィールドを直接読む。

| 統計 | 供給元 |
| --- | --- |
| `utilization`・`durability`・`zone_id` | `PathSummary` の同名フィールド |
| 通過速度 | `PathSummary.base_speed`。形状から決まる通過速度(km/h)で、カーブ・勾配で制限速度より低くなる。交差点での減速・停止は含まない |
| 歩行者量 | `path_traffic` の歩行者キー(`VEHICLE_PEDESTRIAN` を文字列にした `"0"`)。1日あたりの通過トリップ数 |

存在しないフィールドをリフレクションで読むと常に0になり色分けが無反応になるため、フィールド名で直接参照する。

## IVisualModeHandler の実装者

1. `VisualModeObject`: 建物・道路・交差点・地形タイルなど個々の GameObject に付くコンポーネント。生成元(`BuildingManager` / `PathManager` / `TerrainManager`)が `AddComponent` し、`Start()` で自動登録される。
2. マネージャー自身: `ParcelManager` と `ZoneManager` は自分が `IVisualModeHandler` を実装して1件だけ登録する。区画の枠線・データ可視化は GameObject 単位ではなく `OverlayMeshRenderer` による一括描画のため。区画個体の `VisualParcelObject` は `VisualParcelStats` を渡すだけの軽量コンポーネントで、登録しない。
3. `FarBuildingMember`: 遠景建物を有効化時に登録し、非表示・計画中表示・個別フィルタを集約元のRendererに適用する。表示や材質が変わると結合バッチを更新する。地形と水面はそれぞれ `VisualTerrainObject` / `VisualSeaObject` が担当する。

## VisualModeObject の表示規則

### 地下表示

地形レイヤーの既存の「透過」(`terrain=transparent` / `VisualModeTerrain.TRANSPARENT`)は地下表示を有効にする。独立した入力プリセットやIPCイベントは追加しない。

- `UndergroundView` がカメラの背景を土色にし、粒状の模様と薄い地層の濃淡を描く。土は実体の遮蔽物ではなく背景なので、絶対標高が負の設備も隠れない。通常表示へ戻すと、カメラの背景色とクリア方式を復元する。
- 地形タイルは通常のTerrain描画を止め、`UndergroundTerrainSurface` が同じ高さから最大129格子の半透明の地表を描く。地表の不透明度は6%、10m間隔の等高線は最大14%とし、地下の設備を読み取れる濃さで起伏を示す。高さ・接地・地形コライダーは元のTerrainを使用する。高さ変更時は膜のメッシュも更新する。背景地形・超遠景・埋立地の岸壁は既存メッシュの材質だけを半透明にする。
- 海面・川・湖の水面は非表示にし、海岸線・水面ストアから作る細い水際の帯に波紋を描く。水際の版は `TerrainManager.ShoreDataVersion` を使用し、埋立やセッション切り替えに追従する。海岸線は地形上、湖は水位、川はプロファイルの水位に置く。
- 建物・地上道路・プロップの表示可否は既存のレイヤー設定・計画中表示・個別フィルタに従う。表示される地上モデルだけを一律に半透明にし、色分けは保持する。地下表示中の非表示レイヤーは工事中の物体にも適用する。地上モデルを強制的に隠したり表示したりしない。
- 半透明材質は `UndergroundMaterials` が元材質ごとに共有し、地上モデルの不透明度は `SurfaceOpacity` の8%を使う。`UndergroundGhostObject` は現在の材質から半透明の上塗りだけを外し、表示更新後に差し替える。メッシュ更新で置き換えた材質を古い退避値で上書きせず、通常表示へ戻したときに半透明材質を元材質として記録しない。材質の再取得後にも透明化を適用する。遠景の建物と結合バッチ、インスタンス描画のプロップ、GPU森林も同じ透明度にする。
- 建物の外構を非同期で追加するときは `RefreshMaterials()` で既存の元材質を保ち、色モード・計画中表示・透明化を即時適用する。交差点の更新は歩道・レール・信号の生成後に材質を取り直し、現在の表示モードを適用する。
- 完全埋設の道路・線路も通常の `PathManager` が生成・更新・LOD・退避を管理する。地下表示では共通の区間分類が完全埋設区間を表示対象へ加える。地上と地下が混在するパスも1つのパスとして管理し、地下区間と地下の交差点は元材質を不透明のまま使う。モデルを持たない埋設インフラは共通ファクトリで通常道路のフォールバックを選ぶ。選択は通常のパスコライダーと入力ハンドラを使い、ヒット位置の深さによって地形越しの選択を決める。地上の建物・土地区画・パス区間はホバー・クリック・ドラッグの対象から外し、地下パスが無い場所でも地上モデルを選択しない。地形への配置入力は保持する。ホバー・計画中表示・個別フィルタ・既存の色モードも通常のパス表示規則に従う。
- 表示切替で描画区間が変わるパスは、既存の再生成キューへ渡す。キャッシュキーに地下表示の状態を含め、表示区間の違うモデルを復元しない。地下表示の解除時は地下区間を隠し、共通の再生成で描画・選択対象から外す。施工中も進捗に応じて完成区間と土を生成し、未施工の地下の土は設計標高を保つ。データ・地形・削除ルールは変更しない。深さを示す縦線や数値ラベルは表示しない。

地上・地下とも登録・変更・地形追従・近接線路・交差点端部の通知を同じ更新経路で処理する。生成は `RenderWorkScheduler` の共通予算内で実行し、ロード範囲外のパスを再生成しない。常駐セルから離れたパスは通常の退避処理で非表示となり、削除・全消去時は同じ所有・解放処理に従う。検証は `unity/Tests/Underground/run.ps1`。

道路・線路の敷設中は、半透明にした地上区間の辺・ノードを候補として表示せず、吸着対象にも含めない。候補は完全埋設区間に限定し、混在パスの境界も通常描画の分類を共有する。入力側の詳細は [道路エディタの状態遷移](Unity入力制御.md#6-道路エディタの状態遷移) を参照。

### 通常の表示規則

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

## 編集ステートの表示プリセット

編集ステートに入ると、その作業に必要なものが見える表示へ自動で切り替わり、**通常モードへ戻ると元の表示へ復帰する**。
表示状態の正は `IpcViewCoordinator` のレイヤー記憶で、プリセットの適用も復帰もここに集約されている
（`IpcStateCoordinator` の各 `ApplyState*` は入力モードとマネージャの初期化だけを担い、`VisualModeHandler` を直接触らない）。

| ステート（操作） | 強制する表示 |
|---|---|
| `STATE_EDIT_PATH` / `STATE_MERGE_PATH` / `STATE_SPLIT_PATH` / `STATE_EDIT_ROAD_MEMBERS` / `STATE_EDIT_GRADING` / `STATE_EDIT_BUILDING`（道路・線路・インフラ・建物の設置、道路の区間編集、造成範囲の作図） | 区画（建物建設は建設可能な区画の色分け、他は境界線）・地区・等高線を表示、計画中の道路と建物を表示、プロップを非表示 |
| `STATE_EDIT_PARCEL`（土地区画編集） | 区画・地区を表示、計画中の道路と建物を表示 |
| `STATE_EDIT_ZONE`（地区編集） | 区画・地区を表示、道路のみ地区色分け、計画中表示と公共施設ハイライトはOFF |
| `STATE_EDIT_TRANSIT` ＋ プリセット `transit`（公共交通の一覧・新設） | 公共交通路線を全種別表示、計画中の道路と建物を表示 |
| プリセット `parcel_detail`（土地区画詳細） | 土地区画の境界線を表示 |
| `STATE_REMOVE_OBJECTS`（ブルドーザ） | 区画の境界線・計画中の道路と建物を表示、地区は非表示（プロップは削除対象なので消さない） |

復帰の規則は次の2つ。

- **通常モードでユーザーが選んだ表示だけを記憶する。** `view:change_view` による変更は `STATE_NORMAL` のときだけ記憶され、編集ステート中やプリセット適用中の変更は記憶しない。
- **`STATE_NORMAL` へ戻るかプリセットを解除すると記憶を復元する。** 記憶が無いレイヤーは既定値（UI側 `DISPLAY_LAYERS` の初期値）に戻る。編集中に強制したレイヤーは必ず復元対象に含める。

新しい都市の読み込み(`game:loading`。新規ゲーム・チュートリアル・ロードのすべて)では、表示パネルで選べる全レイヤー(地形・道路・建物・区画・地区・天然資源・公共交通・プロップ・アラート)の色モード・サブモード・フラグと通常モードの記憶を、UI側 `DISPLAY_LAYERS` の初期値へ戻して描画へ反映し、`view:current_state` でUIへ通知する。前の都市で選んだ表示(資源の表示・アラートの絞り込みなど)は次の都市へ持ち越されない。グリッドと、オブジェクト単位の表示・非表示(`view:set_object_filter`)はこのリセットの対象外(後者は `session:reset` で捨てる)。

専用の編集ステートを持たないウィンドウは `view:apply_preset` で同じ仕組みに乗る。復帰はウィンドウを閉じたときにRust側が送る `STATE_NORMAL`（排他ウィンドウの場合）、または `view:restore_preset`（編集ステートと併存しうる詳細パネルの場合）が担う（ウィンドウは非表示になるだけでReact側のアンマウント処理が走らないため、Rust側のクローズ処理でも確実に送る）。

**UI側のパネルが一時的な表示変更を `view:change_view` で行ってはいけない。** 通常モードで送ると「ユーザーが選んだ表示」として記憶され、元の表示に戻せなくなる。
例外は、建設の確定後に計画中表示をONにする場合のように、**意図的にユーザーの既定を書き換えたいとき**だけ。

