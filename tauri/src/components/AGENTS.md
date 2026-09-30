# src/components フォルダ

ドメイン別のUIコンポーネント置き場。コンポーネントごとに`.tsx`と対応する`.css`を並べる。ここは入り口で、個別パネルの仕様は末尾の参照先(docs・Skill)が正。

## フォルダ一覧

チュートリアルの表示は `tutorial/`(ヒントバー型の `Tutorial` と開始時の説明ウィンドウ `TutorialIntroWindow`)、JSON定義・進行・各WebViewの制限は `../tutorial/`。通常コンポーネントは安定した操作名の `data-guide`(同じ名前が並ぶタイル・カードは番号の `data-guide-value` も)と観測・確定直前の接点を提供し、シナリオ固有の条件を持たない。登録済み操作名は `../tutorial/definitions.ts` が正。

| フォルダ | 内容 |
| --- | --- |
| `agent` / `citizen` | 経済主体・建物・世帯・市民の詳細パネル(`AgentDetailPanel`とタブ群) |
| `building` | 建物建設パネル、用地買収の確認、マッシュアップ建物のサブ建物配置 |
| `parcel` / `zone` | 土地区画の作成・詳細・操作、地区詳細、地区統合の確認 |
| `path` | 道路・鉄道・インフラの建設、パス詳細(駅情報・交通・設備・結合分割)、2D形状編集、経路提案 |
| `road` / `intersection` | 名前付き道路の詳細、インターチェンジ詳細 |
| `transit` | 公共交通の管理ウィンドウ、路線詳細、路線新設/経路編集、収支見込み |
| `resource` / `prop` / `terrain` | 天然資源、プロップ配置、シナリオの地形エディタ(2D `TerrainEditorCanvas`・3D `Terrain3DPreviewCanvas`) |
| `grading` | 造成(整地・埋立)の範囲作図ウィンドウ、造成工事の建設工事タブ、造成バリアントの判定と見積もりの型(`grading.ts`) |
| `map` | 2D都市マップ。`CityMapCanvas`は共通地図部品で、局所地図が要るパネルは新規実装せずこれを埋め込む |
| `budget` / `tax` / `regulation` / `statistics` / `achievement` | 市政系ウィンドウ(予算・公共施設、税、規制、統計、達成目標) |
| `construction` / `diagnosis` / `notification` | 建設プロジェクト、診断、通知トースト+L1通知一覧 |
| `hud` | 常駐HUD(4枚の専用ウィンドウ) |
| `taskbar` / `topmenu` | HUDから開くポップアップ群(エポック確認・結果、セーブ/ロード、設定、クレジット、ニューゲーム)とトップメニュー |
| `display` | 表示オプション(レイヤー定義`displayConstants.ts`、送信フック`useDisplayOptions`) |
| `template` | シナリオ管理(`ScenarioManagerWindow`)・紹介情報(`ScenarioMetadataWindow`)と、テンプレート作成モード専用(路地作成、経済主体作成) |
| `tips` / `hintbar` / `tutorial` / `parameter-setup` | Tips兼初期ローディング、ヒントバー、チュートリアル、初期パラメータ設定 |
| `debug` | デバッグ用パネル |
| `common` | 共通部品(下記) |

`SystemTray` は `App` から接続状態を受け取り、通信待ちでも設定ボタンを利用できる。保存・再読み込み・テンプレート変更などは無効にする。ゲーム用の他の画面は `App` の `inert` ゲートで表示を保持したまま操作を止める。

## 全体の規約

- `topmenu/TopMenu` の左下のコア表示は、Rust側が起動時に取得した版を `get_core_version` で受け取り、`Core v<core_version>` を表示する。未取得・取得失敗・セッション監視での接続失敗時は `Core 接続失敗`(各言語に翻訳)とする。`session-state` の `coreVersion` と `backendConnected`、状態変更時の `backend-connection` で表示を更新し、版取得のHTTP要求・タイマー・版の比較・Unityの状態取得・詳細エラーの表示はここでは行わない。
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
  - ×で閉じたときの後始末(ハイライト解除、`edit_zone:finish_merge {merged:0}`、`world:clear_road_focus`+`edit_road:cancel`、`edit_connector:hide`)はRust側`window.rs`が担う。
  - 編集ウィンドウと併存する詳細パネル(`agent_detail`等)は、自分が入れたモードの後始末に`STATE_NORMAL`を送らず専用cancelを送る(建物建設など後から開いた編集ステートまで打ち切るため)。
  - 閉じたパネルのリスナーも生きているので、Unityへ送る前に`isVisible`を確かめる。
- 種別・バリアントIDをハードコードしない(公共交通の種別は`masterData.transit`、判定は`vehicle_type`・`model_type`等の属性で行う)。

## 新しいウィンドウの追加手順

1. ドメインフォルダにコンポーネントを作る
2. `App.tsx`にimportし、`SPECIALIZED_PANEL_TYPES`に追加して分岐を書く
3. `src-tauri/config/window-registry.json`に定義(`empty_shell: true`で共通余白`.content`の`padding`が外れ、余白とスクロールをパネルが持つ。`chromeless: true`でタイトルバー(ドラッグ領域と×)も出さず、閉じる操作をパネルが持つ)
4. 排他にするウィンドウがあれば`window-mutex-groups.json`に追加。Unityの編集ステートに入るウィンドウは`window.rs`の`EDIT_MODE_WINDOW_TYPES`に足し、一覧の全種別と双方向に排他にする(`src-tauri/AGENTS.md`)。排他の相手を開くボタン(公共交通の「路線を新設」など)で自分の`onClose`を呼ばない。排他で非表示になった自分のWebViewに相手が転用されることがあり、後から届く閉じる要求が開いたばかりの相手を閉じる

初期内容や新設/編集の使い分けは`params.extra`(`App.tsx`の`windowExtra`。`titleKey`でタイトル差し替え可)で渡す。イベントを投げてからウィンドウを開くと購読が間に合わない。

## common/ の共通部品(車輪の再発明禁止)

| 部品 | 用途と契約 |
| --- | --- |
| `AgentLink` / `CitizenLink` / `PathLink` / `ZoneLink` | 名前をクリックで詳細を開く。エンティティ名を生テキストで出さない |
| `InfoHeader` / `EditableLabel` | 見出しとインライン改名。改名可否は呼び出し側が決める |
| `ConfirmDialogWindow` | 直接開かず`requestConfirm()`経由 |
| `UnaffordableOverlay` | 建設パネルのバリアント一覧で、資金不足のカードに「資金不足」の札を外から重ねる。カード(`BuildingVariantCard` / `PathVariantCard`)は一覧と建設画面の概要で同じものを使うので、一覧だけの表示をカードに足さない。選べなくするのはカードの`disabled` |
| `ConstructionInfoCard` | 建設コスト・期間・需給見通し・資金・線形の表示。確定可否は`utils/constructionFunding`と同じ判定。ドメイン固有の数量は`leadingRows`で表の先頭に足す(造成の種類・切盛土量など) |
| `TagPicker` / `TagSelect` / `TagRow` | 建設プロジェクトのタグ。現在値は詳細レスポンスに載らず`GET /tags/object`で引く。変更後は`tags:changed`をemit |
| `Meter` / `DonutGauge` | 0〜1のメーター。`polarity="negative"`で悪さ指標。1超は全箇所一律に頭打ち+赤紫の発光 |
| `FiscalSankey` | 財政Sankey。歳入項目の定義と支出カテゴリ色はここに置き、予算ウィンドウも共用 |
| `ColorSwatchPicker` | 色選択。OSのカラーダイアログは最前面固定の透明ウィンドウの裏に開くので使わない |
| `SettingsRow` / `SettingsForm.css` | アプリ設定・都市名の変更・システム統計の近似の節の共通フォーム。見出し・カード・設定行・Coming soon・セグメント選択・入力欄は同じ意匠を使う。`disabled` は表示用なので子の操作部品にもネイティブの `disabled` を指定する |
| `LocalizedTextFields` | 作者が書く多言語テキスト(シナリオの紹介文・初期の都市名・クエストの名前と説明)の入力欄。言語のタブで同じ言語の全項目をまとめて書き、英語のタブを先頭に置いて必須と示す(英語の欠けの判定は呼び出し側が `missingEnglish` で行う)。最初に開くタブは表示言語。タブで切り替わる項目はタブ列に接した枠(`.localized-text__panel`)に入るので、言語共通の項目はこの部品に渡さず枠の外に置く |
| `FlatTabs` | フラット意匠のウィンドウ上部のタブ列(ニューゲーム・シナリオ管理)。`role=tab`・`aria-selected`、無効タブと札(Coming soon)。選択状態と中身の出し分けは呼び出し側が持つ |
| `ScenarioSource`(`ScenarioOriginBadges` / `RequiredPacks`) | シナリオの保存元(自作/コミュニティ/DLC)と公式の札、都市に刻まれた必要パックと所有状況。読み込めるかの判定はコアの `compatibility` が正で、ここは表示だけ |
| `SaveVersionInfo` | 保存形式の版(「フォーマット vN」。保存したコアのMAJORだけ)・互換性・必要パックの表示。セーブ一覧・ニューゲーム・シナリオ管理・紹介情報で共通 |
| `ScenarioThumbnail` | シナリオ画像。`hooks/useApiImageUrl` で取得した Blob URL を表示し、画像が無ければプレースホルダー。標高ファイルから取り込んだシナリオには作成直後に地形の平面図が登録される |
| `TerrainFacts` | マップの大きさ・地表・気候の欄(ニューゲームのシナリオの詳細)。大きさの整形は `utils/mapSize.ts` |

このほか`SatisfactionFace`・`SpeedLimitSign`・グラフ類・`NumberStepper`・`Money`等がある。フック`hooks/useVariantAffordability`は資金目安で建設パネルのバリアントのカードを無効化する(札は`UnaffordableOverlay`)。

## hud/ の常駐パネル

- 4枚の専用ウィンドウ(`icon-panel` / `cockpit` / `system-tray` / `display-tray`)。透明領域でもWebViewがクリックを吸うため、パネルの実面積ぴったりに分割する。生成・配置・表示切替はRust(`window.rs`)、Reactは中身だけ。
- 寸法が変わるものは実測して通知する(`resize_icon_panel` / `resize_system_tray`)。`IconPanel.css`で高さを固定しない。`DisplayTray`の幅の正は`window.rs`の`DISPLAY_TRAY_WIDTH`。
- 常駐モード(ブルドーザ/地区編集)は排他。状態の正はグローバルイベント`hud:mode_changed`(`hudMode.ts`)で、副作用は操作した側だけが実行する。編集モードのウィンドウを開いたときの解除はRust(`window.rs`の`apply_mutex_rules`)が送るので、開く側のコンポーネントで`setHudMode(null)`しない。
- BGM再生、3Dのオブジェクト選択受信、Unityの確認要求`remove_object:request_confirm`は`Cockpit`だけが受ける。確認結果は取消でも必ず`sendRemoveObjectConfirmResult`で返す(返さないとUnityが待ち続ける)。
- 地区統合の確認は`IconPanel`が`edit_zone:request_merge`を受けて開く。
- チュートリアルの定義(`../tutorial/definitions/*.json`)はHUDのボタンIDを操作名 `hud.<id>` として強調・許可に使う。アイコンのIDを変える/消す前に定義と `../tutorial/definitions.ts` の登録表を確認する(消えるとその段階でボタンを押せなくなる)。

## 個別パネルの契約(知らないと壊すもの)

- システムメニュー(`taskbar/SaveLoadWindow`、種別 `save_load`): タイトルバーの無いウィンドウ(`chromeless`)で、閉じるのは「ゲームに戻る」とEsc。保存・確認はウィンドウ内のオーバーレイ(`taskbar/SaveDialog`)で行い、保存に成功したら閉じる(「保存しました」は出さない)。上書きセーブは確認してから、まだ保存していない都市(`GET /saves/{current_save_id}` が404)では別名セーブ(名前の入力)になる。保存の直前に `captureSaveThumbnail` で3Dビューを撮る。「シナリオとして保存」は神モードでなくても押せ、紹介情報ウィンドウを作成モード(`params.extra.create`)で開くだけで、ここでは保存しない。別のウィンドウを開く項目は、開いてから自分を閉じる。
- ロード画面(`taskbar/SaveListWindow`): 開くたびにセーブ一覧と現在のセーブ情報を`fetchApi`の`cache: "reload"`で取得する。画像URLへランダムなクエリは付けない。セーブ・シナリオのサムネイルのキャッシュ期限は配信APIが2時間に設定するので、セーブの画像には保存日時(`saved_at`)をリビジョンとして付ける(上書き保存で撮り直される)。カードは新しい順で、名前(`saveDisplayName`)・元のシナリオのタイトル・都市の言語(国旗の絵文字)・人口・ゲーム内日付・保存日時を出す。ロード・削除はウィンドウ内のオーバーレイで確認し、削除が成功しても知らせず一覧から外すだけにする。

- 都市名の変更(`taskbar/CityRenameWindow`、種別 `city_rename`): コックピットの都市名から開く。都市名の入力欄だけのウィンドウ(近似設定の予告表示はシステム統計にある)。
- システム統計(`statistics/SystemStatsWindow`、種別 `system_stats`): システムトレイから開く。`GET /api/v1/stats/system` の1回の応答をタブなしの左右2列に並べる(初期1120×850)。左は計算負荷と、その下に都市の規模(項目・件数だけの表)／メモリ・実行環境の2列、右は近似の予告表示で、列ごとにスクロールする。幅が狭い場合は縦並びに戻し、本文全体をスクロールする。近似の設定行は `SettingsRow` を共用し、余白と区切りだけの意匠をこのパネル内で上書きする。計算時間の区分と問題規模はコアが決めて返すので、区分や式をUI側で持たない(色だけUIの `GROUP_COLORS` で区分に固定する)。`epoch` が `null`(ロード直後・未実行・モック)なら「データなし」を出す。都市規模の `agents`・`paths` は整数の総数で、分類別の内訳と人口は応答に含めない。近似の節は値の保持・送信を行わない。

- ニューゲーム(`taskbar/NewGameWindow`): `FlatTabs` で「シナリオを選択」「シナリオを探す」(未提供。無効+Coming soon)の2タブ。一覧はチュートリアルと `GET /api/v1/scenarios`(全保存元)で、`scenarios:changed` で取り直す。地形から直接始める入口は持たない(ゲームは地形を同梱せず、標高ファイルはシナリオ管理でシナリオにする)。チュートリアルは同梱の教材の都市からコアが始めるので、`create_session` に `isTutorial`・`tutorialId` だけを送り、詳細に地形の事実欄を出さない。読み込めないシナリオは開始ボタンを無効にする。開始ボタンは選択画面の上に確認ダイアログ(ウィンドウ内のオーバーレイ。ネイティブのウィンドウは開かない)を重ね、選択画面は幕の下に残す。幕を押すか「戻る」で閉じ、開始処理中は閉じない。開始中・失敗のステータス(`.new-game-status`)はダイアログの中(項目とボタンの間)に出す(選択画面側に置くと幕の下に隠れる)。シナリオの事実欄(`TerrainFacts`)は一覧の要素の`map_width`・`map_height`・`ground_cover`・`climate`(都市が持つ値)だけで作る。`terrain_source`(取り込んだファイル名など)は表示しない。気候は`normalizeClimate`で正規化して`climate.<値>`で表示し、未指定時の既定は温暖湿潤であり、「未分類」ではない。サイズはkm、地表は`GroundCoverIcon`の自作SVGで表示する。
  - 紹介文は多言語テキストを表示言語で引く(`resolveLocalizedText`、見出しは `scenarioDisplayTitle`)。確認ダイアログの都市の言語の既定はアプリ設定の言語で、シナリオの言語と違えば名前の付け直しを注記する。都市名の初期値は初期の都市名を選んだ言語で引き、プレイヤーが書き換えていなければ言語の切り替えで引き直す。
  - シナリオでは作者が固定した開始設定(`scenario_fixed_options`)を値の表示だけにし、固定していない項目(日数・気候・地表・アセットの地域)を選ばせる。`create_session` には固定していない項目の値だけを送る(固定は null。コアは固定値と違う値を 422 で断る)。
- エポック実行(`taskbar/EpochConfirmWindow`): HTTPを日没と並行して開始し、黒幕の描画完了後だけ結果またはTipsへ進む。結果を閉じるまでは演出IDとHUD無効化を維持する。結果画面の終了はRustの共通クローズ処理が担当する。`TipsWindow`の`mode="epoch"`は初期ロードの開始ボタンを使わず、呼び出し元が処理完了時に閉じる。

- 建物建設(`building/`): 配置方法・寸法の真実源はUnity(`edit_building:placement_state`)。要求区画種別・観光名所の一点物・構造種別の階数上限はパネル側でも弾く(区画選択後のバリアント変更で不一致が残る)。
  - 選択は分類タイル→建物カード一覧→建設画面の3段で、左上の戻るで1段ずつ戻る。分類はマスターの`bd_category`(`BD_CATEGORY_*`。ID順がタイルの並び、未設定は「その他」)で、建築規制の`bd_type`では分けない。カテゴリ選択後の建物一覧は横一辺に広がるカード(`BuildingVariantCard`。アイコン・説明・フロア仕様を一体表示し、選択後も概要表示に流用)で表示する。建設画面から戻る先は選んでいた建物の分類(編集・コピーで直接開いた場合も同じ)。
  - 既存建物をコピーするボタンはヘッダー右上(戻るボタンと同じ高さ)に配置する。既存建物の編集は分類へ戻れず、候補を編集対象と同じ`bd_type`に絞る(敷地を選び直さないので建築規制の照合を通らない)。
  - 市庁舎の移転は、市庁舎の詳細パネルの移転ボタンが`params.extra.relocateCityHall`で開く移転モード(市庁舎に固定し自由配置から始める)。送信は新規の配置と同じで、配置済みならバックエンドが移転として扱う。創設後の一覧には市政府を出さない。
- 区画作成(`parcel/ParcelCreationPanel`): 作成方法の真実源はUnity(`edit_parcel:set_mode`はUnity起点だけ)。確定後も`STATE_NORMAL`を送らず連続作成。`parcel_type`は送らない。
- 公共施設(`agent/PublicFacilityPanel`): 放棄中は解除以外を無効化。`status_from`の見出しは`resolveStatusSinceLabel`で選ぶ(再開でも更新されるので一律「竣工日」にしない)。
- パス建設(`path/`): 道路・鉄道・インフラは`path_build`の`PathConstructionPanel`1つで敷く。曲線/直線・吸着はUnity側で、`curveMode`の書き込み元は`edit_path:update_shape`の`is_curved`だけ。
  - 選択は建物建設と同じカテゴリのタイル→バリアントのカード一覧→建設画面の3段。カテゴリはマスターにバリアントがある`path_type`そのもので(`local-constants`の`PATH_TYPES_HIDDEN_IN_BUILD_PANEL`は出さない)、一覧にはそのカテゴリの`path_type`のバリアントだけを並べる。タイルは道路・軌道・インフラの3区分に分けて並べ、区分は`pathBuildCategories`で決め打ちしている(軌道=`PATH_TYPES_TRACK_GROUP`、インフラ=`PATH_TYPES_INFRA`、残りは道路)。カード(`PathVariantCard`)は一覧の項目と建設画面の概要に共用する。カテゴリのアイコンは`utils/pathCategoryIcon`の専用画像だけで、無ければ枠を空けておく。
  - 建設画面の設定(複線構成・ホーム設置側・駅舎の配置端と通路方式・所属道路・タグ)は選んだバリアントの種別で決まり、新規敷設でだけ出す。既存パスの形状編集(`params.extra.editPathId`)は建設画面から始まり、カテゴリへ戻れない。
  - ホーム設置側は単線の駅だけが持つ(`railBuild.hasPlatformSideChoice`)。1本で上下線を持つ複線一体の線路(線路系で`lanes`≥2)の駅はホームの位置がバリアントで決まる(両外側の相対式か、`center_width` を持つ島式)ので、トグルを出さず、`platform_side` を送らず、`edit_path:change_platform_side` も送らない。
  - 確定は`usePathConstruction.canConfirm`(Unityの`can_confirm`∧全線`/paths/validate`。先頭1本だけの検証は禁止)。成功のたびに`notifyRoadsChanged`。
  - 確定欄は`confirmMessages`を並べる。区間ごとの形状の違反(ノード間距離・曲線半径・勾配)は形状編集パネルの吹き出しが区間を示すので「形状にエラーがあります」の1行に畳み、ほかの拒否理由(干渉・川・海湖・岸壁の天端・工事中の造成)は`pathValidation.describePathValidation`の文言で出す。`/paths/validate`に`colliding_*`が増えたらそこへ文言を足す(足さないと汎用の「敷設条件を満たしていません」になる)。
  - 複線はUIが平行な単線群へ展開するが、`railBuild.isSingleRailPathVariant`(複線一体の線路・人員搬送)は`DIR_BOTH`の1本で送る。
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
- 地形エディタ(`terrain/`): シナリオの地形を編集する。シナリオ管理の「地形を編集」が Rust の `prepare_scenario_terrain_editor` で作業コピーを作って開き、`TerrainEditorCanvas` の `mapId` は作業コピーのID(`editor_id`)。実データの編集は`elevRef`、表示だけ`displayElev()`。川・水面のオブジェクトと配列はin-placeで書き換えず差し替える(Undoと描画キャッシュが同じ参照を持つ)。
  - 編集対象は地形・水面・川・カメラの4つで、資源とプロップは編集も表示もしない(編集は「都市を編集」のテンプレート作成モードの資源編集・プロップ配置)。ただし保存APIは資源をID差分・プロップを全置換で受けるので、読み込んだ`resources`/`props`を`untouchedContentRef`に持ち、保存でそのまま送り返す(送らないとシナリオの資源とプロップが消える)。
  - `locks.heights_locked` のときは地形(自動編集を含む)・水面・川のタブを無効にし、理由を表示してカメラのタブで始める。ロック情報は `normalizeTerrainEditorLocks` で正規化し、欠けていればロック扱いにする。
  - 保存対象(標高・水面・川・カメラ・地表・気候)を変える操作は必ず `markDirty` か `pushUndoState` を通す(未保存表示の根拠)。「シナリオに保存」の引数は `terrainData.ts` の `scenarioTerrainSaveArgs` で作る(ロック中は標高・水面・川を null にし、標高は送るときだけ符号化する)。保存成功で `scenarios:changed` をemitする。
  - 保存の失敗は理由コードで `terrainEditor.saveErrors.<code>` → `saveCompatibility.<code>` の順に翻訳し、コードの無い失敗(本文が日本語固定のRust側の失敗)は `terrainEditor.saveFailed` にする。マップの寸法は変えない。
  - 地形の平面図(`terrainThumbnail.ts`の画素生成。北上・海透過、長辺512px)は、シナリオ管理が標高ファイルを取り込んだ直後にシナリオ画像として作る(`template/heightmapImport.ts`)。エディタは作らない。標高色と陸地の標高範囲は`terrainData.ts`が正。
  - 「元の地形」の欄は作業コピーの `terrain_source`(取り込んだファイル名、またはチュートリアルの舞台の名前)をそのまま出す。表示だけで、判定には使わない。
  - 3Dプレビューに関わる変更は`scheduleTerrain3dFullState()`か`emitPreviewDelta`を必ず通す。カーソル層にはマウス位置だけで変わるものしか描かない。
- パラメータ設定(`parameter-setup/`): `GET /api/v1/parameters`のメタデータ駆動。パラメータを増やすだけならi18n追加は不要(タブ名`paramSetup.category.*`だけ要る)。

## ウィンドウ間イベント(Tauri内)

`epoch:executing`、`tags:changed`、`roads:changed`、`transit:lines_changed`、`city:renamed`、`hud:mode_changed`、`building_direct:acquisition_confirmed`、`road:member_edit_started`、`scenarios:changed`(シナリオの作成・削除・紹介情報・地形の保存)。一覧・詳細はこれらを購読して取り直す。

## 参照先

- 公開docs(`city-genesis/docs/docs/`)。`programs/`に各機能のUI仕様(ページ名は機能名)。
  - 横断: `IPC連携の整理.md`(確定送信の責務分担・編集モードの同期契約)、`Unity入力制御.md` §9.1(表示プリセット)、`コンポーネント連携.md`(統計・満足度タブ)。
  - `都市マップの検索と経路案内.md`(CityMapCanvasの契約)、`アラートフラグ管理.md`(表示フィルタのビット合成)。
  - 概念とAPI: `economics/`の3_道路網・6_公共交通機関・12_建物・19_環境・22_予算管理・29_駅と駅舎。地形エディタは`geometries/D_地形形状.md` §D.10〜D.16と`L_地形データ作成マニュアル.md`、プロップは`M_地形プロップ.md`。シナリオは`programs/シナリオ制作と配布.md`と`セーブデータ仕様.md`。
- 親の`tauri/AGENTS.md`(utils一覧、フラット意匠トークン、多言語、マスターデータ、車両外観カタログ)。
- Skill: `genesis-ipc`(IPC/API)、`genesis-input-mode`(Unity側の編集モード)、`genesis-visual-mode`(表示モード)、`asset-credits`(素材)。

## シナリオの管理と紹介情報

- `template/ScenarioManagerWindow` はトップメニューの「シナリオ管理」から開き、Unityを起動しない。`FlatTabs` で「シナリオ」「地形を取り込む」の2タブ。`scenario_manager` / `scenario_metadata` はレジストリ駆動のポップアップで、後者へ `params.extra.scenarioId`(編集)か `params.extra.create`(システムメニューの「シナリオとして保存」)を渡す。
- シナリオタブは `GET /api/v1/scenarios` の全保存元(自作・コミュニティ・DLC)を1つの一覧にする。タブの下に作り方の説明(`scenarios.createHint`)を常に出し、更新ボタンは無い(一覧は `scenarios:changed` で取り直す)。アップロード先が未設定の旨はページに出さず、アップロードボタンの `title` にだけ添える。自作(`origin === "local" && !read_only`)のカードはアイコンだけのボタン(`ScenarioActionButton`。名前は `title`・`aria-label`、押せない理由は `title` の2行目)で、左詰めに「紹介情報を編集」「地形を編集」「都市を編集」「アップロード」、右端に離して「削除」を並べる。それ以外は文字のボタン「コピーして編集」だけ。「削除」は `requestConfirm`(`tone: "danger"`)の後に `DELETE /api/v1/saves/{id}?template=true` を呼ぶ。壊れたシナリオを片付けられるよう読み込めないカードでも押せ、失敗しても一覧を取り直し、成功したら選択を外して `scenarios:changed` をemitする。都市を開く2つは `create_session {templateId, templateMode: true, acceptBalanceChange: true}` で、ポップアップが全部閉じるので確認文で地形エディタの未保存の編集に触れる。「地形を編集」は `invoke("prepare_scenario_terrain_editor", {scenarioId})` の戻り値で `terrain_editor` を開く。自作でも `encrypted`(DLC由来)なら「地形を編集」を無効にし、理由をツールチップ(`scenarios.terrainEncryptedHint`)で出す(作業コピーが平文で残るため、コアも409 `SCENARIO_ENCRYPTED` で断る)。
- 地形を取り込むタブ(`TerrainImportPanel`、補助は `template/heightmapImport.ts`)は、PC内のどこにある標高ファイルでも `<input type="file" accept=".png,.npy">` で選ばせる(Tauriのダイアログ・権限は使わない)。初回表示後は隠すだけで入力を保つ。入力は上から、タイトル(既定はファイル名から拡張子を除いたもの)→「詳細地形」の枠(標高データのファイル・1画素あたりの距離・最低/最高標高を1行に並べた2つの入力(PNGだけ編集できる。.npy は値そのものが標高なので、欄にファイルの実際の最低・最高を出し、説明文は出さない)・海面の高さ・マップの大きさの見込み)→「背景地形(省略可)」の枠(標高データの `<input type="file" accept=".npy">`・背景の1画素あたりの距離は別欄・既定50・背景の大きさの見込み。「外す」で選び直す。海面の高さは詳細地形と共通)。2つの枠は薄い線の角丸の枠(`.scenario-group`。紹介情報の開始設定と共通)で囲み、枠の内側に少し大きめの見出し(`.scenario-group-title`)と、その下に短い説明文(詳細地形=プレイヤーが都市開発をし道路や建物を作れる範囲、背景地形=マップ外の背景として描かれプレイヤーは道路や建物を作れない)を付ける。形式の説明は常に出さず、項目名の横のインフォメーションアイコン(`InfoTip`)のツールチップにする。左に入力フォーム、右にプレビューを均等に並べ(`.scenario-import-layout`。1fr:1fr)、どちらもカードにしない。
  - 画素数はファイルの先頭だけを読んで得る(PNG は IHDR、.npy はヘッダーの `shape`)。分かればマップの大きさの見込み(`importedMapExtent`。コアの `terrain_import._map_extent` と同じ1km単位の切り上げ)と背景の大きさの見込み(`importedBackgroundExtent`。50m格子の画素数へ丸めるだけで切り上げない)を出し、マップが一辺30km・背景が一辺100kmを超えるなら作成を止める。値域(`HEIGHTMAP_LIMITS`)はコアの写しで、変換の可否の正はコア(422 `HEIGHTMAP_INVALID` → `saveCompatibility.HEIGHTMAP_INVALID`)。
  - プレビュー(`TerrainImportPreview`。標高は `useHeightmapPreview`、画素は `heightmapPreview.ts` の `renderImportPreview`)は、選んだファイルを `POST /api/v1/scenarios/heightmap-preview?sea_level&min_height&max_height`(背景は `background=true`。本文はファイルの生バイト)へ送ってコアが取り込みと同じ読み方で間引いた標高を受け取り、詳細地形は `terrainData.ts` の標高色(`computePixelRGB`)・背景は淡い単色(`BACKGROUND_RGB`)で、どちらも標高0m以下は透明に重ねて描く(下地は `SEA_RGB`)。2つの中心は原点で揃え、詳細地形の範囲は取り込みと同じ1km単位への切り上げで縁の標高を延ばす。標高を取り直すのはファイル・海面の高さ・PNGの標高範囲が変わったとき(250msのデバウンス。値が不正な間は取り直さず前の結果を残す)だけで、1画素あたりの距離は配置を変えるだけ。読めない理由は各ファイルの入力欄の下に出し、プレビューの図には出さない。
  - 「シナリオを作成」は `POST /api/v1/scenarios/from-heightmap?spacing&min_height&max_height&sea_level&title&language&source_name`(本文はファイルの生バイト。.npy では min/max を送らない。`language` はアプリ設定の言語で、都市内の名前の言語になる)。背景を選んでいれば本文を `new Blob([詳細, 背景])` にして末尾へ背景を続け、`background_spacing`・`background_size`(背景のバイト数)を付ける(multipart は使わない)→ `GET /api/v1/scenarios/{id}/terrain` から平面図を描いて `uploadScenarioThumbnail`(失敗はログだけで作成は成功のまま)→ 一覧の取り直し → シナリオタブへ切り替えて新しいカードを選択 → `scenarios:changed`。成功したらファイル(背景も)とタイトルだけを空にする。
- 紹介文・初期の都市名の入力は `common/LocalizedTextFields`、画像は `ScenarioThumbnail`、保存元の札と必要パックは `ScenarioSource`、保存版と互換性は `SaveVersionInfo`。同じ項目の入力・表示を各画面で再実装しない。
- 紹介文を編集するのは紹介情報ウィンドウ(`template/ScenarioMetadataWindow`)だけ。作成モードはプレイ中の都市(`GET /api/v1/city/`)の紹介情報・開始設定と都市の言語(`GET /api/v1/sessions`)を初期値にし、「保存」で画像(選んだファイルは `PUT /api/v1/sessions/thumbnail`、無ければ `captureSaveThumbnail`)を預けてから `POST /api/v1/templates` で新しいシナリオを作る。保存するまで何も書かないので、「キャンセル」なら何も残らない。達成目標ウィンドウの `ScenarioInfoSection` は神モードでも表示だけにする。紹介情報ウィンドウは開始設定(日数・気候・地表・アセットの地域の値と「変更可能」)も書く。「変更可能」はコアの固定する項目の一覧(`scenario_fixed_options`)の反転で、送るときに裏返す。開始設定の先頭には都市内の名前の言語を値の表示だけで並べ、「変更可能」はオンのまま外せなくする(`PATCH /api/v1/scenarios/{id}` は `language` を受けず、プレイヤーはゲーム開始時に常に選べる)。開始設定は `<fieldset>`/`<legend>` を使わず `.scenario-group` の枠に見出しを入れる(legend は枠線をまたいで描かれ、見出しが枠からはみ出す)。
- 取り込みの `title` は1つの文で、コアは英語にだけ入れる(何語で書いたかを尋ねないため。ほかの言語は英語で表示される)。言語ごとの書き分けは後から紹介情報ウィンドウで行う。
- 変更通知は `scenarios:changed`。投稿は保存とは別の操作として確認する。投稿先の可否はコアの一覧レスポンスに従う。
