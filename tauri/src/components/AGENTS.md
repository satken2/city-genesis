# src/components フォルダ

ドメイン別のUIコンポーネント置き場。コンポーネントごとに`.tsx`と対応する`.css`を並べる。ここは入り口で、個別パネルの仕様は末尾の参照先(docs・Skill)が正。

## フォルダ一覧

チュートリアルの表示は `tutorial/`、JSON定義・進行・各WebViewの制限は `../tutorial/`。通常コンポーネントは安定した操作名の `data-guide` と観測・確定直前の接点を提供し、シナリオ固有の条件を持たない。登録済み操作名は `../tutorial/definitions.ts` が正。

| フォルダ | 内容 |
| --- | --- |
| `agent` / `citizen` | 経済主体・建物・世帯・市民の詳細パネル(`AgentDetailPanel`とタブ群) |
| `building` | 建物建設パネル、用地買収の確認、マッシュアップ建物のサブ建物配置 |
| `parcel` / `zone` | 土地区画の作成・詳細・操作、地区詳細、地区統合の確認 |
| `path` | 道路/インフラ/軌道の建設、パス詳細(駅情報・交通・設備・結合分割)、2D形状編集、経路提案 |
| `road` / `intersection` | 名前付き道路の詳細、インターチェンジ詳細 |
| `transit` | 公共交通の管理ウィンドウ、路線詳細、路線新設/経路編集、収支見込み |
| `resource` / `prop` / `terrain` | 天然資源、プロップ配置、地形エディタ(2D `TerrainEditorCanvas`・3D `Terrain3DPreviewCanvas`) |
| `grading` | 造成(整地・埋立)の範囲作図ウィンドウ、造成工事の建設工事タブ、造成バリアントの判定と見積もりの型(`grading.ts`) |
| `map` | 2D都市マップ。`CityMapCanvas`は共通地図部品で、局所地図が要るパネルは新規実装せずこれを埋め込む |
| `budget` / `tax` / `regulation` / `statistics` / `achievement` | 市政系ウィンドウ(予算・公共施設、税、規制、統計、達成目標) |
| `construction` / `diagnosis` / `notification` | 建設プロジェクト、診断、通知トースト+L1通知一覧 |
| `hud` | 常駐HUD(4枚の専用ウィンドウ) |
| `taskbar` / `topmenu` | HUDから開くポップアップ群(エポック確認・結果、セーブ/ロード、設定、クレジット、ニューゲーム、エディタ)とトップメニュー |
| `display` | 表示オプション(レイヤー定義`displayConstants.ts`、送信フック`useDisplayOptions`) |
| `template` | テンプレート作成モード専用(路地作成、経済主体作成) |
| `tips` / `hintbar` / `tutorial` / `parameter-setup` | Tips兼初期ローディング、ヒントバー、チュートリアル、初期パラメータ設定 |
| `debug` | デバッグ用パネル |
| `common` | 共通部品(下記) |

## 全体の規約

- 表示文字列は必ずi18n(`t("key")`。`src/i18n/`の全7言語に追加)。マスターの表示名は日本語のみなので多言語ラベルはi18nから採る。
- 他コンポーネントのCSSクラスを借りるときは、そのCSSを明示的に`import`する。パネルは`App.tsx`で`lazy()`されCSSもチャンク分割されるため、importしないと直前に開いたウィンドウ次第で当たる/当たらない崩れになる。
  - 共通クラス(`.hud-icon-btn`等)の上書きは2クラス以上のセレクタで書く(同点の詳細度は読み込み順に依存する)。
- 確認ダイアログは`utils/confirmDialog`の`requestConfirm()`。`window.confirm`は非同期版に差し替えられていて確認にならない。
- 3Dビューの操作説明・モード全体の前提条件はパネル本文に書かず、ヒントバーに出す(マウントで`setHintbarText(t("hint.<名前>"))`、アンマウントで`""`。文言は`tutorial.json`の`hint.*`)。
- 編集中だけの一時的な表示切替を`sendChangeDisplay`で送らない(Unityは通常モード中の`view:change_view`をユーザーの選択として記憶し、戻せなくなる)。
  - ステートを持つ操作はUnityのプリセットが自動で切り替える。持たないウィンドウは`sendViewApplyPreset`を使う。
- 建設・編集パネルの「確定」はUIが直接バックエンドへPOST/PATCHし、成功後に`edit_*:finish`系IPC(無ければ`sendSessionReload`)と`STATE_NORMAL`でUnityをリセットする。
  - エラーは`ApiError.code`→`errors.{code}`で`showNotification`。例外は地区の即時割当と接続端子ノードのドラッグ(Unityが直接送信)。
- Unityから受けた値をそのまま送り返さない(建物配置の寸法、プロップの回転/高さ、パス形状の標高、区画作成モードは双方向同期で、エコーすると往復ループになる)。
- ポップアップは閉じても非表示になるだけでアンマウントが走らない(`window_type`をまたいで使い回される)。
  - ×で閉じたときの後始末(ハイライト解除、`edit_zone:finish_merge {merged:0}`、`world:clear_road_focus`+`edit_road:cancel`)はRust側`window.rs`が担う。
  - 閉じたパネルのリスナーも生きているので、Unityへ送る前に`isVisible`を確かめる。
- 種別・バリアントIDをハードコードしない(公共交通の種別は`masterData.transit`、判定は`vehicle_type`・`model_type`等の属性で行う)。

## 新しいウィンドウの追加手順

1. ドメインフォルダにコンポーネントを作る
2. `App.tsx`にimportし、`SPECIALIZED_PANEL_TYPES`に追加して分岐を書く
3. `src-tauri/config/window-registry.json`に定義(`empty_shell: true`で共通余白`.content`の`padding`が外れ、余白とスクロールをパネルが持つ)
4. 排他にするウィンドウがあれば`window-mutex-groups.json`に追加

初期内容や新設/編集の使い分けは`params.extra`(`App.tsx`の`windowExtra`。`titleKey`でタイトル差し替え可)で渡す。イベントを投げてからウィンドウを開くと購読が間に合わない。

## common/ の共通部品(車輪の再発明禁止)

| 部品 | 用途と契約 |
| --- | --- |
| `AgentLink` / `CitizenLink` / `PathLink` / `ZoneLink` | 名前をクリックで詳細を開く。エンティティ名を生テキストで出さない |
| `InfoHeader` / `EditableLabel` | 見出しとインライン改名。改名可否は呼び出し側が決める |
| `ConfirmDialogWindow` | 直接開かず`requestConfirm()`経由 |
| `ConstructionInfoCard` | 建設コスト・期間・需給見通し・資金・線形の表示。確定可否は`utils/constructionFunding`と同じ判定。ドメイン固有の数量は`leadingRows`で表の先頭に足す(造成の種類・切盛土量など) |
| `TagPicker` / `TagSelect` / `TagRow` | 建設プロジェクトのタグ。現在値は詳細レスポンスに載らず`GET /tags/object`で引く。変更後は`tags:changed`をemit |
| `Meter` / `DonutGauge` | 0〜1のメーター。`polarity="negative"`で悪さ指標。1超は全箇所一律に頭打ち+赤紫の発光 |
| `FiscalSankey` | 財政Sankey。歳入項目の定義と支出カテゴリ色はここに置き、予算ウィンドウも共用 |
| `ColorSwatchPicker` | 色選択。OSのカラーダイアログは最前面固定の透明ウィンドウの裏に開くので使わない |

このほか`SatisfactionFace`・`SpeedLimitSign`・グラフ類・`NumberStepper`・`Money`等がある。フック`hooks/useVariantAffordability`は資金目安でプルダウンを無効化する。

## hud/ の常駐パネル

- 4枚の専用ウィンドウ(`icon-panel` / `cockpit` / `system-tray` / `display-tray`)。透明領域でもWebViewがクリックを吸うため、パネルの実面積ぴったりに分割する。生成・配置・表示切替はRust(`window.rs`)、Reactは中身だけ。
- 寸法が変わるものは実測して通知する(`resize_icon_panel` / `resize_system_tray`)。`IconPanel.css`で高さを固定しない。`DisplayTray`の幅の正は`window.rs`の`DISPLAY_TRAY_WIDTH`。
- 常駐モード(ブルドーザ/地区編集)は排他。状態の正はグローバルイベント`hud:mode_changed`(`hudMode.ts`)で、副作用は操作した側だけが実行する。
- BGM再生、3Dのオブジェクト選択受信、Unityの確認要求`remove_object:request_confirm`は`Cockpit`だけが受ける。確認結果は取消でも必ず`sendRemoveObjectConfirmResult`で返す(返さないとUnityが待ち続ける)。
- 地区統合の確認は`IconPanel`が`edit_zone:request_merge`を受けて開く。
- チュートリアルのハイライトID(`tutorial/tutorialSteps.ts`の`targetElementId`)はHUDのウィンドウをまたぐ。アイコンを消す/移す前に参照先を確認する(消えると無言で進行する)。
  - ステップは`prepare`→`validate`→`onComplete`。パスの端点ノードは稼働後にしか付かないので、接続判定を伴う工期スキップは`prepare`に置く。

## 個別パネルの契約(知らないと壊すもの)

- ロード画面(`taskbar/SaveListWindow`): 開くたびにセーブ一覧と現在のセーブ情報を`fetchApi`の`cache: "reload"`で取得する。画像URLへランダムなクエリは付けない。地形・セーブ・シナリオのサムネイルのキャッシュ期限は配信APIが2時間に設定する。

- ニューゲーム(`taskbar/NewGameWindow`): 地形の名称・自然地理の説明は全7言語の`terrainProfiles.json`に置き、`data/terrainProfiles.ts`で対象IDを管理する。収録地形の表示名は地形や景観を表す短い名称とし、実在・架空の固有地名を使わない。説明にも固有名詞・人間の都市・歴史・文化を含めない。マップIDは表示名と分けて維持する。気候はAPIの`climate`を`normalizeClimate`で正規化して`climate.<値>`で表示する。マップIDから別の気候を割り当てない。未指定時の既定は温暖湿潤であり、「未分類」ではない。サイズはkm、地表は`GroundCoverIcon`の自作SVGで表示する。
- エポック実行(`taskbar/EpochConfirmWindow`): HTTPを日没と並行して開始し、黒幕の描画完了後だけ結果またはTipsへ進む。結果を閉じるまでは演出IDとHUD無効化を維持する。結果画面の終了はRustの共通クローズ処理が担当する。`TipsWindow`の`mode="epoch"`は初期ロードの開始ボタンを使わず、呼び出し元が処理完了時に閉じる。

- 建物建設(`building/`): 配置方法・寸法の真実源はUnity(`edit_building:placement_state`)。要求区画種別・観光名所の一点物・構造種別の階数上限はパネル側でも弾く(区画選択後のバリアント変更で不一致が残る)。
- 区画作成(`parcel/ParcelCreationPanel`): 作成方法の真実源はUnity(`edit_parcel:set_mode`はUnity起点だけ)。確定後も`STATE_NORMAL`を送らず連続作成。`parcel_type`は送らない。
- 公共施設(`agent/PublicFacilityPanel`): 放棄中は解除以外を無効化。`status_from`の見出しは`resolveStatusSinceLabel`で選ぶ(再開でも更新されるので一律「竣工日」にしない)。
- パス建設(`path/`): `path_build`と`infra_build`は同じ`PathConstructionPanel`を`category` propで使い分ける。曲線/直線・吸着はUnity側で、`curveMode`の書き込み元は`edit_path:update_shape`の`is_curved`だけ。
  - 確定は`usePathConstruction.canConfirm`(Unityの`can_confirm`∧全線`/paths/validate`。先頭1本だけの検証は禁止)。成功のたびに`notifyRoadsChanged`。
  - 確定欄は`confirmMessages`を並べる。区間ごとの形状の違反(ノード間距離・曲線半径・勾配)は形状編集パネルの吹き出しが区間を示すので「形状にエラーがあります」の1行に畳み、ほかの拒否理由(干渉・川・海湖・岸壁の天端・工事中の造成)は`pathValidation.describePathValidation`の文言で出す。`/paths/validate`に`colliding_*`が増えたらそこへ文言を足す(足さないと汎用の「敷設条件を満たしていません」になる)。
  - 複線はUIが平行な単線群へ展開するが、`railBuild.isSingleRailPathVariant`(LRT専用軌道・人員搬送)は`DIR_BOTH`の1本で送る。
  - 駅の簡易建設(`useStationConstruction`)は`usePathConstruction`を使わず、幾何はUnity(`StationPlanManager`)の`placement_state`をそのまま表示する。
- 結合・分割(`path/PathTopologyEditPanel`): アンマウントで必ず`sendEndPathTopologyEdit`。確定成功は`sendFinishPathTopologyEdit`を1発だけ(リロードと`STATE_NORMAL`を分けると差分リロードに割り込んで形状が壊れる)。
- 2D形状編集(`path/PathShapeEditorPanel`): 形状の真実源はUnity(`RoadPlanManager`)。操作結果はドラッグ終了時に1回送る。地図は`initialBounds`を決めてからマウントする。
- 造成(`grading/`): 範囲の形の正はUnity(`edit_grading:update_shape`の`{polygon, finalized}`)。見積もり(`POST /grading/preview`)は`finalized`かつ3点以上のときだけ、デバウンスと古い要求の中断つきで取る(Unityは頂点ドラッグ中に毎フレーム送る)。
  - 確定可否は今の範囲に対する応答の`valid`∧`affordable`。確定は`POST /grading`→主体の詳細を開く→`sendEditGradingFinish`→閉じる。finishの中でUnityが通常ステートへ戻して再取得するので、パネル自身は`STATE_NORMAL`を送らない(閉じるときにRustが×と同じ経路で`STATE_NORMAL`を送るが、冗長なだけで再取得は妨げない)。キャンセルは`STATE_EDIT_GRADING`の再送で描き直す。
  - 発注が拒否されたら(400/409)見積もりを取り直し、新しい応答が来るまで確定を押せなくする(拒否の理由と資金は見積もりの表示で見せる)。
  - 造成工事の主体の判定は`isGradingVariant`(TYPE_OPEN_SPACEの広場とtypeが同じ)。建設工事タブは`GET /grading/{agent_id}`(プレビューは自分の区画に当たって拒否される)、公共施設パネルは取り壊しと編集を出さない、建物建設の一覧から外す。
- 制限速度: 送信値と応答の比較は許容差で行う(バックエンドはfloat32)。規制タブ(`SpeedLimitTab`)は許可リスト`EDITABLE_PATH_TYPE_IDS`で、保存は全バリアント分を送る。
- 予算(`budget/CityBudgetWindow`): 比率は自動保存で`AUTO_SAVE_DEBOUNCE_MS`のデバウンス必須(PATCHは再導出を伴い重い)。rechartsはCSS変数を解決できないのでグラフ色だけ直値定数。
- 公共交通(`transit/`): 路線カラーの正はバックエンドの`line_color`。作成/更新/削除後は`emit("transit:lines_changed")`。収支見込みはバックエンドの試算を並べるだけ。貨物は `TransitMasterEntry.freight` で判定し、編成の総両数（機関車1両を含む）の下限を2にする。外観候補は新設・詳細が `TransitVehicleSelector` と同じカタログを共有する。
- 道路詳細(`road/`): 型・API・変更通知は`roadTypes.ts` / `roadApi.ts`が唯一の窓口(道路を変えたら`notifyRoadsChanged`)。強調帯はアンマウント時に送らず、区間編集の終了に`STATE_NORMAL`を送らない。
- 地形エディタ(`terrain/`): 実データの編集は`elevRef`、表示だけ`displayElev()`。川・水面・資源のオブジェクトと配列はin-placeで書き換えず差し替える(Undoと描画キャッシュが同じ参照を持つ)。
  - 地形PNGは`terrainThumbnail.ts`で作る北上・海透過の平面図(長辺512px)。保存時と`tools/regenerate_terrain_thumbnails.py`の一括生成は同じ描画処理を使う。標高色と陸地の標高範囲は`terrainData.ts`が正。`TerrainMapThumbnail`は縦横比を保ち全域を表示する。
  - 3Dプレビューに関わる変更は`scheduleTerrain3dFullState()`か`emitPreviewDelta`を必ず通す。カーソル層にはマウス位置だけで変わるものしか描かない。
- パラメータ設定(`parameter-setup/`): `GET /api/v1/parameters`のメタデータ駆動。パラメータを増やすだけならi18n追加は不要(タブ名`paramSetup.category.*`だけ要る)。

## ウィンドウ間イベント(Tauri内)

`epoch:executing`、`tags:changed`、`roads:changed`、`transit:lines_changed`、`city:renamed`、`hud:mode_changed`、`building_direct:acquisition_confirmed`、`road:member_edit_started`。一覧・詳細はこれらを購読して取り直す。

## 参照先

- 公開docs(`city-genesis/docs/docs/`)。`programs/`に各機能のUI仕様(ページ名は機能名)。
  - 横断: `IPC連携の整理.md`(確定送信の責務分担・編集モードの同期契約)、`Unity入力制御.md` §9.1(表示プリセット)、`コンポーネント連携.md`(統計・満足度タブ)。
  - `都市マップの検索と経路案内.md`(CityMapCanvasの契約)、`アラートフラグ管理.md`(表示フィルタのビット合成)。
  - 概念とAPI: `economics/`の3_道路網・6_公共交通機関・12_建物・19_環境・22_予算管理・29_駅と駅舎。地形エディタは`geometries/D_地形形状.md` §D.10〜D.16、プロップは`M_地形プロップ.md`。
- 親の`tauri/AGENTS.md`(utils一覧、フラット意匠トークン、多言語、マスターデータ、車両外観カタログ)。
- Skill: `genesis-ipc`(IPC/API)、`genesis-input-mode`(Unity側の編集モード)、`genesis-visual-mode`(表示モード)、`asset-credits`(素材)。

## シナリオの管理と紹介情報

- `template/ScenarioManagerWindow` は同梱・ダウンロード・自作のカード一覧。`scenario_manager` / `scenario_metadata` はレジストリ駆動のポップアップで、後者へ `params.extra.scenarioId` を渡す。
- 紹介文入力は `common/ScenarioTextFields`、画像は `ScenarioThumbnail`、保存版と互換性は `SaveVersionInfo`。同じ項目の入力・表示を各画面で再実装しない。
- 変更通知は `scenarios:changed`。保存成功後に紹介情報を開き、投稿は別操作として確認する。投稿先の可否はコアの一覧レスポンスに従う。
