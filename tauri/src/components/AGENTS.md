# src/components フォルダ

ドメイン別のUIコンポーネント置き場。コンポーネントごとに`.tsx`と対応する`.css`を並べる。
表示文字列は必ずi18n(`t("key")`、`src/i18n/ja/`・`src/i18n/en/`配下のドメイン別JSONの両言語に追加)を使うこと。

## ドメイン別フォルダ

| フォルダ | 内容 |
| --- | --- |
| `agent` / `building` / `citizen` / `parcel` / `path` / `zone` / `resource` / `terrain` | 各エンティティの詳細パネル・作成/建設パネル。**マッシュアップ建物(MODEL_MASHUP、`masterData.agents[variant].model_type`で判定)の詳細パネルには「サブ建物の配置を編集」ボタンが付き、`building/MashupEditPanel`(=`mashup_edit`ウィンドウ)を開く**。エディタは上部にサブ建物カード(敷地と同縮尺の底面矩形。`masterData.subbuildings`から取得)、下部に敷地キャンバス(区画ポリゴン+接道辺ハイライト+配置済み棟)を描き、カードドラッグ=配置/ドラッグ=移動/Shift+ドラッグ=回転/Delete=削除(重なりは許容)。保存は`PUT /buildings/{id}/sub-buildings`の一括置換→`sendSessionReload()`でUnity再描画。`terrain` には地形エディタ(`TerrainEditorCanvas`=2D編集、`Terrain3DPreviewCanvas`=3Dプレビュー)もある。**水面(海面以外の湖など)の追加**は `TerrainEditorCanvas` の「水面(湖など)」カード=`waterMode` トグルで、左クリック=頂点追加/右クリック=確定(3点以上)。水位(`waterLevelInput`)ごとに1枚。内部表現 `{id,waterLevel,points}` を `WaterSurfacePayload`(GeoJSONライク、`terrainData.ts`)へ変換し、`save_terrain_editor_data`(meta.json)・`export_terrain_as_npy`(サイドカー `map_xxx.water.json`)・`EVT_TERRAIN_3D_FULL_STATE`(3Dプレビュー)へ渡す。ゲーム本体での描画はUnity `WaterSurfaceManager`、海岸線への穴反映はバックエンド `compute_coastline_polygon`。仕様の正は `genesis-docs/docs/geometries/D_地形形状.md §D.10`。**川の自動生成**は「川編集」タブ(第3タブ)で、「流路を計算」(標高不変)→一覧に未彫り込みとして追加→一覧で個別に削除・川ごとの深さ係数調整・「彫る」で1本ずつ標高へ焼き込み(Undo対応。再計算は未彫り込みのみ差し替え)のフロー。アルゴリズム本体(flow accumulation/蛇行進化/非対称断面彫り込み/中州)はReact非依存の`terrainRiver.ts`。降雨範囲は地形編集と同じ選択マスク(`buildErosionRegion`)を流用し、選択があればその範囲にのみ雨が降る。彫り込み済みの川のみ `RiverPayload`(centerline/widths/surface_offsets/footprint、`terrainData.ts`)として`save_terrain_editor_data`(meta.jsonの`rivers`)・`export_terrain_as_npy`(サイドカー `map_xxx.rivers.json`)・`EVT_TERRAIN_3D_FULL_STATE`(3Dプレビューが水面リボン描画)へ渡す。川床彫り込み自体は標高データ(.npy)に含まれる。Unity描画とバックエンドのfootprint穴あけは将来実装。仕様の正は同 `§D.11` |
| `budget` / `tax` / `regulation` / `statistics` / `achievement` / `search` | 市政系ウィンドウ(予算、税、規制、統計グラフ、達成目標、検索) |
| `transit` | 公共交通ウィンドウ。`TransitManagementWindow`(中央=`transit_management`。稼働中路線の一覧。混雑率は`statistics/tabs/AccidentsTab`の`UsageMeter`を再利用、運賃/運行間隔のインライン編集・削除・新設、路線名クリックで`transit_detail`を開く。収支の市政府付け替えは内部動作のためプレイヤーには見せない=補助/収支は非表示)、`TransitDetailPanel`(右パネル=`transit_detail`。AgentDetailPanelと同体裁。種別名は`masterData.paths[path_variant].display_name`、アイコンは`resolveTransitIcon(path_variant)`。基本情報・運賃/運行間隔編集・運行実績・**路線図(`TransitLineMap`=実地図オーバーレイ)**・削除。**路線図は`CityMapCanvas`を`interactive={false}`で埋め込み、停留所(駅)マーカーと区間エッジを`overlayDraw`でワールド座標に重ね描き**。種別色は`local-constants.resolveTransitColor`(マスターに色項目が無いためUI側定義)。停留所(`stops`)・区間エッジ(`edges`={from,to,travel_direction})はバックエンド`GET /api/v1/transit/lines/{id}`が返す。**進行方向は `travel_direction` で描き分け**: 複線(メトロ等)=DIR_FORWARD/DIR_BACKWARD で進行方向左側にオフセット+矢印、街路走行(バス等)=DIR_BOTH で両方向矢印。運休中は点線・暗色)、`TransitCreationPanel`(左ドック=`transit_creation`。**建物建設パネルと同一デザイン**=`building/BuildingConstructionPanel.css`の`bc-*`を共有。**種別プルダウンの選択肢・表示名・説明は公共交通マスター`masterData.transit`(transit_master)から取得**〔ハードコードしない〕、アイコンは`pathIcon.resolveTransitIcon(entry.transit_path_id)`。**種別選択で`sendChangeState("STATE_EDIT_TRANSIT", {path_variant, stop_path_variants, route_path_variants}, "transit_creation")`を送り、Unityを停留所配置モードにする**(停留所は種別の`stop_path_variants`のパス=駅・街路上にのみ設置可。Unity側が吸着する)。配置中の停留所座標列は`edit_transit:update_stops`(`utils/ipc.ts`の`IPC_EVENT_EDIT_TRANSIT_UPDATE_STOPS`)で受信し停留所数を表示、確定で`POST /api/v1/transit/lines`に`path_variant=選択種別のtransit_path_id`と`stops`(2点以上必須)を渡す→成功後`emit("transit:lines_changed")`+`sendEditTransitFinish()`。マウント中は`sendChangeDisplay("transit","default",全種別CSV)`で公共交通表示をON、アンマウントで`STATE_NORMAL`へ復帰。`TransitDetailPanel`もマウント時に表示ON)。バックエンドは`GET /api/v1/transit/lines`(一覧)・`GET .../lines/{id}`(詳細+区間+停留所+エッジ)・`POST/PATCH/DELETE`・`GET /api/v1/master/transit`(公共交通マスター)。作成/削除/更新後は`emit("transit:lines_changed")`で管理ウィンドウが再取得する。運賃等の既定値は`local-constants.ts`の`TRANSIT_*` |
| `map` | 都市2Dマップ。`CityMapCanvas`(**共通地図コンポーネント**。ツールバー等のUIを持たない純粋な描画部品。海岸線/幹線道路/全道路/線路/土地区画/建物底面/地区枠線/標高色分け/等高線/グリッドの各レイヤーをpropsでON/OFF、`initialBounds`で描画範囲指定、`overlayDraw`でワールド座標系の重ね描き(描画内容を変えたら`overlayVersion`をインクリメントして再描画を要求する。コールバック差し替えだけでは再描画されない)、`animatedOverlayDraw`で連続アニメーションの重ね描き(アニメ専用の第2キャンバス〔マウス操作透過〕にrequestAnimationFrameループで毎フレーム`(ctx, view, timeMs)`が呼ばれる。ベース地図はフル再描画されないためコストはコールバック分のみ。未指定ならループは走らない。例: 統計ウィンドウのインフラ網タブ=`statistics/tabs/InfrastructureTab`の流量縞アニメ)、`interactive=false`で埋め込みプレビュー用の静的表示が可能。パス・区画・建物はビューポート+パディングのbbox指定で取得し、範囲外へパンしたらデバウンス再取得する。幹線道路の判定は`masterData.paths[variant].trunk`(自動車専用道路はフラグにかかわらず常に幹線扱い)。道路はマスターの`width`(全幅m)をstroke幅に使った実幅の面で描く(街路=灰、自動車専用道路=黄、線路=茶。形状は曲線パス(curved=1)のみアンカー点列をCatmull-Rom由来ベジエ補間、直線パスは折れ線のまま)。**ref(`CityMapCanvasHandle`)で`resetView()`/`reload()`/`fitBounds(bounds)`を公開**。`fitBounds`は`initialBounds`propと違い地形・ベクターを再取得せずビュー変換だけを更新するので、敷設中プレビューのように範囲が頻繁に変わる用途はこちらを使う〔`initialBounds`を毎回変えると初期化effectが再走して重い〕)、`CityMapWindow`(中央=`city_map`。上部ツールバーでレイヤー切り替え・全体表示・再読み込み。将来は左側に検索/経路探索パネルを追加予定)。**局所地図が必要なパネルは新規実装せずCityMapCanvasを埋め込むこと**(例: `path/PathShapeMapPreview`=道路建設パネルの形状プレビュー。周辺の道路/区画/建物はレイヤーに任せ、敷設中の道路(ノード・曲線・制御点)を`overlayDraw`でワールド座標に重ね描きし、ノード更新のたびに`fitBounds`で追従) |
| `taskbar` / `topmenu` | タスクバー(エポック実行確認、セーブ/ロード、設定、ニューゲーム等)とトップメニュー |
| `loading` / `notification` / `hintbar` / `tutorial` / `parameter-setup` | 専用ウィンドウ(ローディング、通知、ヒントバー、チュートリアル、初期パラメータ設定) |
| `display` | 表示オプション(ヒートマップ切り替え等)。レイヤー定義は`displayConstants.ts`の`DISPLAY_LAYERS`、選択状態とUnityへの`view:change_view`送信は`useDisplayOptions`。**公共交通タブ(`transit`)**は`DisplayOptionsPanel`の`TransitSection`が`masterData.transit`(transit_master)の路線種別ごとに独立トグルを描画し、有効な`transit_path_id`のCSVを`subMode`として送る(アラートタブのビットマスク方式に相当)。Unity側は種別色の帯オーバーレイで表示 |
| `debug` | デバッグ用パネル |
| `common` | 共通部品(下記) |

## common/ の共通部品(車輪の再発明禁止。新しいUIを作る前に必ず確認)

| コンポーネント | 内容 |
| --- | --- |
| `AgentLink` / `CitizenLink` / `PathLink` / `ZoneLink` | エンティティ名のクリッカブルリンク。クリックすると該当の詳細ウィンドウが開く。**エンティティ名を表示するときは生テキストではなく必ずこれを使う** |
| `DataList` | 履歴配列(先頭=最新)の表示。最新値+クリックでミニ棒グラフのポップアップ。`clampRange`で棒の高さだけをクランプできる(ツールチップは生値。利益率など値域が決まっているデータの外れ値対策) |
| `EpochDate` | エポック日付(1800年1月1日からの経過日数)の表示変換 |
| `InfoHeader` | ウィンドウ内の見出し(アイコン+ステータス+名前)。`iconLabel`を渡すとアイコンを黄色の角丸枠で縁取り、枠下部にラベルを黒文字表示する(公共施設の明示に使用) |
| `Meter` | メーター表示 |
| `NumberStepper` | 数値の増減入力 |
| `ConstructionInfoCard` | 建設情報カード。推定建設コスト(標準コストに対する倍率の色付きバッジ付き)・推定建設期間を枠付きグリッドで表示する。建物系は`buildingArea`/`totalHeight`/`totalFloorArea`を渡すと物理情報の行が付き、`perEpoch`を渡すと建設中・解体中の「1エポックのコスト・残り期間」ブロック、`nextEpochConstructionBudget`を渡すと「来期の建設予算の X%」注記が付く。倍率バッジのホバー説明はドメインごとに文言が違うため`costRatioHelp`(翻訳済み文字列)で受け取る。i18nキーは`construction.*`。建物/パスの建設パネルと詳細パネル(建設工事タブ)の4箇所で共用しており、**建設コスト系の表示を新設・変更するときは個別実装せずこのカードを使う・拡張すること**。1エポックコスト・残りエポック数の取得は`src/hooks/useConstructionBudgetRow`(kind="agent"/"path")を使う |
| `TimeSeriesChart` | 時系列グラフ(recharts) |
| `SpaceAmountChart` | フロア面積の内訳チャート |
| `FiscalSankey` | 財政Sankey(歳入→資金プール→歳出の2段階フロー。recharts)。税率ウィンドウ(支出側=前エポック実績、`linkStyle="solid"`)と、予算ウィンドウの「収支フロー」タブ2種(`linkStyle`を切り替え。「来期見込み」=支出側が予算枠のプレビューで`linkStyle="planned"`の破線、「前期実績」=税率ウィンドウと同一内容で`linkStyle="solid"`)が共用。**歳入は税(`TAX_REVENUE_META`=total_revenueキー31〜35)だけでなく、公営事業収入(公共サービス売上・公営住宅家賃。total_revenueのコストカテゴリキー1〜7に計上される)も`buildBusinessRevenueItems`で支出側と同じ予算カテゴリ粒度・同色(`spendCategoryColor`)で描く**。さらに**土地取引の税外収支**として、払い下げ収入(市有地を民間へ売却した土地代。`LEDGER_LAND_SALE`=キー36)を`buildLandSaleRevenueItem`で歳入側に、収用補償(市が民間から土地を収用した土地代。`LEDGER_LAND_ACQUISITION`=キー37)を`buildLandAcquisitionSpendItem`で歳出側に、同系の土色(`FISCAL_COLOR_LAND`)で描く。**収用補償は予算枠ではなく実支出のため、前エポック実績を描く画面(税率ウィンドウ、予算ウィンドウの「前期実績」タブ)でのみ歳出側に加え、予算枠を描く「来期見込み」タブでは加えない(払い下げ収入はすべての画面の歳入側に描く)**。総歳入と総歳出の差はresidualノード(黒字=繰越/赤字=貯蓄取り崩し)でバランスさせる(`buildFiscalSankeyData`) |
| `TerrainMapThumbnail` | バックエンド管理の地形マップ(`GET /api/v1/terrain/maps`)のサムネイル画像。`GET /api/v1/terrain/maps/{id}/thumbnail`から取得し、404時は緑色プレースホルダーに自動フォールバックする |

## 新しいウィンドウの追加手順

1. 適切なドメインフォルダにコンポーネントを作成
2. `App.tsx`にimportを追加し、`SPECIALIZED_PANEL_TYPES`にウィンドウタイプ名を追加して分岐を書く
3. `src-tauri/config/window-registry.json`にウィンドウ定義(タイトル、サイズ、インスタンスポリシー)を追加
4. 排他にしたいウィンドウがあれば`window-mutex-groups.json`にも追加

## 建設・編集パネルの確定送信の責務分担

作成/建設/編集パネル(建物・パス・土地区画・資源・公共交通・プロップ)の「確定」は、**UIが直接バックエンドへPOST/PATCHする**(`fetchApi`使用)。Unityは形状の組み立てと `edit_*:update_*` 系IPCによる逐次通知のみを担い、UIは送信成功後に `edit_*:finish` 系IPC(建物のように専用イベントが無いものは `sendSessionReload`)+`STATE_NORMAL` でUnityの編集状態をリセットして再描画させる。エラーは `ApiError.code` を `errors.{code}` のi18nキーへマップして `showNotification` で表示する。例外は3Dジェスチャで完結する操作(地区の即時割当、接続端子ノードのドラッグ移動)で、これらはUnityが直接バックエンドへ送信する。

### 道路建設パネル(path/)の設定系はUnity移管済み

曲線/直線・吸着5種の設定UIは`PathConstructionPanel`に**無い**(Unity側のCtrlラジアルメニュー・カーソルHUDが担う)。復活させないこと。曲線モード(`usePathConstruction`の`curveMode`)は`edit_path:update_shape`の`is_curved`受信が唯一の書き込み元で、確定送信の`curved`・コスト見積り・形状プレビューがこれを参照する。**標高・盛土のステッパーはパネルに残す**(既存ノードを選んで数値で微調整する用途のため)。表示値は`edit_path:select_node`受信で選択ノード(node_id=-1は次回配置値)の値に同期し、ステッパー変更を`sendEditPathChangeNode`/`sendEditPathChangeEmbankment`でUnityへ送り返す双方向同期(Unityのキー操作でもHUD・パネル双方に反映される)。パネルが持つのはカテゴリ/バリアント選択・標高/盛土・ホーム設置側・グリッド設定(サイズ/角度。表示状態はUnityの`view:grid_toggled`受信で同期)・形状プレビュー・コスト・確定/キャンセル。軌道複線(線路数≥2)は確定時にUI側が中心線を平行な単線パス群へ展開して一括POSTするが、Unityの3Dプレビューにも複線が見えるよう、各線の横オフセット列を`STATE_EDIT_PATH`の`lateral_offsets` params(初期値)と`edit_path:change_lateral_offsets`(線路数変更時の逐次通知。配置中ノードは維持される)でUnityへ渡す(`usePathConstruction`の`lateralOffsetsForVariant`とrailComponents監視effect)。
