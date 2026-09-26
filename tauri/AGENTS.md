# プロジェクト概要

本レポジトリは都市建設シミュレーションゲーム"City Genesis"のUI(Tauri + React)レポジトリです。
Unityのグラフィックの上に透明なWebViewウィンドウとして重なって表示され、ゲーム内のウィンドウ・メニュー・グラフ類はすべてここで描画されます。
バックエンド(FastAPI)とはHTTP、Unity(グラフィック)とはMemoryMappedFile(MMF)のIPCで通信します。

# アーキテクチャ(知らないと絶対わからない)

- **1つのReactアプリ(`src/App.tsx`)が全ウィンドウを描画する。** Tauriのウィンドウラベル(`main` / `icon-panel` / `cockpit` / `system-tray` / `display-tray` / `topmenu` / `notification` / `hintbar` / `tutorial` / `parameter-setup` / `tips` / それ以外=popup)によって`windowKind`が決まり、描画内容が分岐する。
- **ゲーム中に常駐するHUDは4枚のウィンドウに分かれている**(`icon-panel`=画面左端の縦並びメニュー、`cockpit`=画面下部中央、`system-tray`=画面右下、`display-tray`=画面左下の簡易表示切替)。Unityの3Dビューに重ねる透明ウィンドウは、見た目が透明な領域でもクリックをWebViewが吸ってしまうため、**パネルの実面積ぴったりのウィンドウに分割する必要がある**(1枚の全画面ウィンドウにはできない)。生成・配置・表示切替はRust側(`window.rs`の`create_hud_windows` / `layout_hud_windows` / `set_hud_visible`)、中身は`src/components/hud/`。
- **ポップアップパネルはすべて `React.lazy` で遅延インポートされる。** メインバンドルから重い依存(`three.js` / `recharts`)を切り離し、新規ウィンドウ起動時のJSパース量を減らすため。ポップアップの `<section>` 内は `<Suspense key={`${window_type}:${entity_id}:${label}`}>` で囲まれ、context切り替え時に内部コンポーネントが確実に再マウントされる(各Panelが `initialEntityId` を `useState` 初期値に取る実装のため、prop変更を追跡できないことを補う)。新しいパネルを追加するときも `lazy(() => import(...))` の形式で追加すること。
- ポップアップウィンドウの種類(サイズ、単一/複数インスタンスのポリシー等)は`src-tauri/config/window-registry.json`で定義される。同時に開けないウィンドウの組は`window-mutex-groups.json`で定義される。
- **`WindowContext`(ウィンドウタイプ、対象エンティティ等)を持つのは`window-registry.json`駆動のポップアップだけ。** それらは起動時にTauriコマンド`get_window_context`で自分の`WindowContext`を取得する。Rust側が直接生成する常駐ウィンドウ(HUD4枚・トップメニュー・通知・Tips・ヒントバー・チュートリアル・初期設定)はcontextを持たず、`App.tsx`の`contextlessLabels`で取得対象から除外する。**ここから漏れたウィンドウは起動時に`get_window_context`が失敗し、同じ`initialize()`内で後続の言語復元まで巻き添えでスキップされる**ため、Rust直接生成のウィンドウを追加したらこの配列にも足すこと。
- 新しいウィンドウを追加する手順: (1)`src/components/`配下にコンポーネント作成 → (2)`App.tsx`のimport(必ず `lazy(() => import(...))` 形式)と`SPECIALIZED_PANEL_TYPES`に追加 → (3)`window-registry.json`に定義を追加。

# プログラム構造

```
- src/
  - App.tsx            ... 全ウィンドウのエントリポイント(ラベルで分岐)
  - components/        ... ドメイン別のUIコンポーネント(詳細はcomponents/AGENTS.md)
  - contexts/          ... MasterDataContext(マスターデータ), FontSizeContext
  - data/              ... UI内に静的に持つ掲載データ(credits.ts=クレジット表示の掲載内容、propCategories.json=プロップ配置パネルの分類、transitVehicleModels.json=公共交通の車両外観の候補。「公共交通の車両外観カタログ」の節、resourcePresets.ts=天然資源エリアの埋蔵量上限・回復率の目安。提供物資ごとの型〔河川・地下水など〕と、面積比例か定量か。値の根拠は公開ドキュメント economics/21_天然資源.md §21.9)
  - hooks/             ... 共通Reactフック(useTemplateMode=テンプレートモード判定, useConstructionBudgetRow=建設中/解体中の1エポックコスト・残りエポック数の取得, useVariantAffordability=建設パネルのバリアント単位の資金目安〔GET /{paths,buildings}/variant-affordability〕, useObjectVisibility=建物・道路のオブジェクト単位の表示/非表示〔状態の正はUnity側。バックエンド・セーブには残らない〕, useTransitEstimate=公共交通路線の収支見込み〔POST /transit/lines/estimate・/lines/{id}/estimate。入力が落ち着いてから取得し古い要求は中断、取得中は直前の結果を残す〕, useResourcePresetInputs=天然資源エリア作成フォームの目安・埋蔵量上限・回復率〔目安から導出し面積比例なら形状の面積に追従、手入力が優先、種別が変わると全部捨てる。地形エディタの資源編集タブと resource/ResourceConstructionPanel が共有〕)
  - utils/             ... 共通処理(下記の一覧参照)
  - i18n/              ... 多言語リソース(言語ディレクトリごとにドメイン別JSONを配置。index.tsがglobで集約)
  - types.ts           ... 共通型定義(WindowContext, AgentData等)
  - assets/sounds/     ... BGM・効果音・環境音(音声形式の規約は「音声アセット」の節)
  - constants.ts       ... 自動生成の定数(編集禁止)
  - local-constants.ts ... UI側で独自定義する定数
  - types/generated.ts ... 自動生成のAPIスキーマ(編集禁止)
- src-tauri/           ... Rust側(ウィンドウ管理、MMF IPC。詳細はsrc-tauri/AGENTS.md)
```

# src/utils の一覧(新しい共通処理を書く前に必ず確認)

| ファイル | 内容 |
| --- | --- |
| `api.ts` | バックエンドHTTP通信の共通窓口。`fetchApi<T>()`と`buildApiUrl()`。**fetchを直接書かないこと**。fetchApiはAcceptヘッダでMessagePackを受け入れ宣言し、バックエンドの対応エンドポイント(bbox一括取得系)からはバイナリで受信して`@msgpack/msgpack`でデコードする(Content-Typeで自動判別。呼び出し側は形式を意識しない) |
| `ipc.ts` | Unityへのイベント送信(`sendChangeState`, `sendEditBuildingConfirm`等)。**Unityに何か伝えたいときはここに関数を追加する**。**`setInterval`などで周期的に送ってはいけない**(Unity不在の間の送信はMMFに溜まり続け、約2時間で満杯になって以降の送信が`MMF buffer full`で全滅する)。Unity起動後の再同期は`graphic-reconnected`イベントを受けて送る。3Dビューの表示切替は2系統: `sendChangeDisplay`(=ユーザーの選択。Unityが通常モードの設定として記憶する)と`sendViewApplyPreset`(=ウィンドウを開いている間だけの一時的な差し替え。閉じると元の表示へ戻る)。**編集作業中だけ表示を変えたいときに前者を使ってはいけない**(ユーザーが選んでいた表示を上書きしてしまう) |
| `notification.ts` | 通知ウィンドウの表示(`showNotification`) |
| `log.ts` | Rust側ファイルログ(実行ファイル横の`city-genesis/tauri.log`)へのINFO転送(`logInfoToFile`)。本番でconsoleが見えないため、無言で止まりうる重要フロー(エポック実行など)の進行記録に使う。なお未キャッチ例外・unhandledrejection・`console.error`は`main.tsx`が自動で同ログへERROR転送する |
| `constructionTags.ts` | 建設プロジェクトのタグ(建物・道路に付ける単一の分類ラベル)の共通処理。`fetchTags`(候補一覧) / `fetchTagProjects`(タグ別の工事一覧+集計) / `fetchEntityTag`(**1件の現在値。`tag`は単体取得のレスポンスに載らないので専用の口 GET /tags/object から引く**) / `saveEntityTag`(PATCH agents/paths の`tag`) / `deleteTag` / `TAGS_CHANGED_EVENT`(タグ変更をウィンドウ間に知らせるグローバルイベント名)。**表示・非表示はここでは扱わない**(`hooks/useObjectVisibility`) |
| `cityNotifications.ts` | L1通知(重大通知)の共通処理。`fetchL1Notifications`(GET /api/v1/notifications)・`formatL1NotificationMessage`(i18nキー`notifications.types.<type>`への差し込み。未知typeはフォールバック)・`NOTIFICATION_ENTITY_WINDOW_TYPES`(通知対象→詳細ウィンドウの対応表)。コックピット(hud/Cockpit.tsx)とCityNotificationsWindowが共用 |
| `hintbar.ts` | 画面下部ヒントバーのテキスト設定 |
| `locale.ts` | 対応言語(`SUPPORTED_LANGUAGES`)と、言語コード→`Intl`用ロケールの変換(`resolveIntlLocale`)・検証(`isSupportedLanguage`)・対応言語コードへの丸め(`resolveSupportedLanguage`。`ja-JP`のような地域付きコードを`ja`へ寄せる。プルダウンの現在値解決に使う)。詳細は「多言語対応」の節 |
| `date.ts` | ゲーム内仮想暦(1ヶ月=30日/1年=360日)の整形。`formatEpochDate` / `formatEpochDateRange` |
| `money.ts` | 金額の整形。`formatCompactAmount`(有効3桁のコンパクト表記) / `formatExactAmount`(3桁区切り) |
| `parking.ts` | 駐車メーター(建物の駐車容量・道路の路上駐車量)の`parkingUsage(used, capacity, fractionDigits)`。台数と容量を表示桁に丸めてから`Meter`用の使用率を返す(生値で割ると、表示は「20 / 20台」なのに端数で100%超として光る食い違いが起きるため)。容量0で使用があれば`+Infinity`(全量が超過) |
| `settings.ts` | アプリ設定のロード・保存 |
| `weatherAudio.ts` | AmbienceVolumeSync。CockpitからUnityへmaster×sfxを送る。音声再生はしない |
| `playSound.ts` | 効果音再生。**音声ファイルは必ず`import`して得たURLを渡すこと**(`"src/assets/sounds/x.opus"`のようなパス文字列を直接渡すとバンドル対象から外れ、開発実行では鳴るのに本番ビルドだけ無音になる) |
| `tutorial.ts` | チュートリアルの表示制御 |
| `agentIcon.ts` / `parcelIcon.ts` / `pathIcon.ts` / `resourceIcon.ts` | 各種アイコン画像の解決 |
| `propThumbnail.ts` | プロップのサムネイル画像(`src/assets/images/props/{id}.png`)の解決`resolvePropThumbnail(id)`。未生成の種別は`undefined`で、呼び出し側が代替表示にする。`import.meta.glob`(eager・`?url`)で全画像をバンドルする。自由配置パネル(`prop/PropTypeSelector`)と公共交通の車両外観選択(`transit/TransitVehicleSelector`)が共有するので、**プロップ画像のglobを各コンポーネントに書かないこと** |

# 音声アセット

環境音はUnityのResources/Audio/Ambienceに置き、CameraAmbienceが地表から100m〜10mでミックスする。Tauriは音量設定だけを送る。詳細は../docs/docs/programs/サウンドシステム.md。

`src/assets/sounds/`に置く音声は**Ogg Opus(`.opus`)を既定とする**。実行環境がWebView2(Chromium)に固定されているため互換性の問題がなく、同音質でMP3・Ogg Vorbisより配布サイズが小さい。PCM(`.wav`)をそのまま置いてはいけない(同じ音が10倍以上のサイズになる)。

- 目安のビットレート: BGM 112kbps / 効果音 96kbps / 環境音 64kbps(モノラル素材は40kbps)。
- 例外: 原本が既に低ビットレートで、再符号化するとサイズが増えるだけのもの(`notify.mp3`)はMP3のまま置く。
- 変換例: `ffmpeg -i src.wav -c:a libopus -b:a 96k -vbr on -application audio out.opus`
- 天候の環境音だけは原本からの加工手順をスクリプト化してある(`tools/prepare_weather_audio.py`、`../unity/Assets/Resources/Audio/Ambience/README.md`)。
- 外部素材を取り込む・差し替えるときは`src/data/credits.ts`の追記も必ずセットで行う。

# スクロールバー(全ウィンドウ共通)

WebView2(Chromium/Edge)の既定はコンテンツに重なるオーバーレイ式スクロールバーで、パネル右端の値やボタンが隠れて操作できなくなる。これを避けるため`App.css`で`*::-webkit-scrollbar`系を定義し、**全スクロール領域を従来型(レイアウト幅を占める)スクロールバーに切り替えている**。個別コンポーネントで独自のスクロールバー幅・色が必要なときだけ、そのセレクタで`::-webkit-scrollbar`を上書きする(統計ウィンドウの各タブが例)。

標準プロパティの`scrollbar-width` / `scrollbar-color`は使わないこと。Chromiumではそれらが指定された要素で`::-webkit-scrollbar`の指定が無効化され、オーバーレイ表示に戻る場合がある。

# フラット意匠の共通トークン(--flat-*)

トップメニュー系のウィンドウ(ニューゲーム・セーブ/ロード・エポック結果・設定)は**枠線・角丸・発光を使わず、ベタ塗りの四角と落ち影だけで面の高さを表す**意匠で統一している。強調は枠ではなく**左端の実線ブロック**(`box-shadow: inset 4px 0 0 var(--flat-accent)`)で示す。

配色・影は`App.css`の`:root`にある`--flat-*`(面`--flat-surface`/`--flat-raised`/`--flat-sunken`、選択`--flat-selected`、文字`--flat-text*`、状態`--flat-good`/`--flat-warn`/`--flat-danger`とその面色、影`--flat-shadow`/`--flat-shadow-sm`)が唯一の正。**この意匠のUIを新設・改修するときは直値ではなくこの変数を参照すること**(色を変えるときもここを直す)。`NewGameWindow.css` / `SaveListWindow.css` / `SaveLoadWindow.css` は変数化前の実装で同じ値を直値で持っており、触る機会があれば変数へ寄せる。

なお`App.css`の`.panel` / `button`など旧来のグローバルスタイルは角丸・枠線を持つ別系統なので、この意匠のウィンドウでは`border: none; border-radius: 0`で明示的に打ち消す。

# 多言語対応

全体的に、多言語対応できるように作ってね。
表示文字列はreact-i18nextの`useTranslation()`(`t("key")`)を使い、文字列のハードコード禁止。

対応言語は`src/utils/locale.ts`の`SUPPORTED_LANGUAGES`が唯一の正で、`ja` / `en` / `es-419` / `zh-Hans` / `zh-Hant` / `fr` / `ko`の7つ。**日本語が原本**で、他言語はそこから訳す。設定ウィンドウ・ニューゲームの言語選択肢もこの配列をそのまま並べるため、言語を増やすときはここに足す(表示名は`settings.language.<コード>`)。中国語は語彙が字体で異なるため地域ではなく字体で分けている。

翻訳リソースは`src/i18n/<言語コード>/`配下にドメインごとのJSONへ分割している(例: `agent.json`, `budget.json`, `statistics.json`)。各ファイルはトップレベル名前空間をそのまま含み、`index.ts`が`import.meta.glob("./*/*.json")`で全言語・全ファイルを取得し、言語ごとに単一の`translation`名前空間へ集約する(キー参照は従来どおり`t("agent.foo")`)。**キーを追加するときは、その名前空間を含むファイルを全言語ぶん必ず更新する**こと。新しいトップレベル名前空間を足す場合は既存のドメインファイルに入れるか、全言語に同名の新規ファイルを作れば自動で拾われる。

キーの過不足と`{{変数}}`の食い違いは`tools/check_i18n_parity.py`で検査できる(日本語を基準に全言語を突き合わせる)。翻訳リソースを触ったら実行すること。

**i18nインスタンスはウィンドウ(WebView)ごとに独立している。** 各ウィンドウはマウント時に`loadAppSettings`で言語を復元し(`App.tsx`)、以降の変更は設定保存時にブロードキャストされる`settings-updated`イベントを`App.tsx`が受けて`changeLanguage`する。HUD3枚・トップメニューは常駐したまま再マウントされないため、この経路がないと再起動まで旧言語のままになる。

数値・日付の整形に渡すロケールは`resolveIntlLocale(i18n.language)`(`src/utils/locale.ts`)で解決する。**`i18n.language`を直接`Intl`やtoLocaleStringへ渡さないこと**(言語コードと地域付きロケールの対応表がここに集約されている)。ゲーム内の仮想暦の整形は`src/utils/date.ts`が担当し、月名は`Intl`から引くので言語追加時の手当ては不要(年月日の並びだけ言語系統ごとに分岐している)。

# マスターデータについて

建物の種類とか道路の種類とか、その表示名とか、そういうもののマスターデータと呼ばれるものに関しては、プログラム起動時にバックエンドから取得済みです。 
任意のコンポーネントから useMasterData() フックで参照可能

```
export type MasterData = {
  bd_types: Record<number, BdTypeEntry>;
  skills: Record<number, SkillEntry>;
  vehicle_types: Record<number, VehicleTypeEntry>;
  space_categories: Record<number, SpaceCategoryEntry>;
  product_types: Record<number, ProductTypeEntry>;
  parcel_types: Record<number, ParcelTypeEntry>;
  path_types: Record<number, PathTypeEntry>;
  agent_types: Record<number, AgentTypeEntry>;
  agents: Record<number, AgentMasterEntry>;
  subbuildings: Record<number, SubBuildingMasterEntry>;
  structures: Record<number, StructureMasterEntry>;
  landmarks: Record<number, LandmarkMasterEntry>;
  products: Record<number, ProductMasterEntry>;
  paths: Record<number, PathMasterEntry>;
  transit: Record<number, TransitMasterEntry>;
  combined_paths: Record<number, CombinedPathEntry>;
  budget_categories: Record<number, BudgetCategoryEntry>;
  resource_types: Record<number, ResourceTypeEntry>;
  prop_types: Record<number, PropMasterEntry>;
};
```

# 編集してはいけないファイル

`src/types/generated.ts`と`src/constants.ts`はプログラムによって自動生成されるファイルなので、編集しないように。
これらのファイルを編集する必要がある場合は、ユーザーに相談すること。
UI側で独自に定義する定数は`local-constants.ts`に定義する。

`local-constants.ts`には、**自動生成では出力されないバックエンド共有定数の写し**も置く。`PATH_TYPES_INFRA`(=インフラのパス種別の一覧。送電`PATH_ELECTRICITY`/導水`PATH_WATER`/パイプライン`PATH_PIPELINE`の3種)と判定ヘルパ`isInfraPathType`がこれにあたり、**バックエンドの`consts.PATH_TYPES_INFRA`と一致させる契約**(生成器`sync_consts.py`はタプルを出力しないため写しになる)。判定は地形を変形しない・グリッド吸着を切るといった「財を区別しない共通のインフラ挙動」にのみ使い、電力網と水網の物理的分離はノードのビット側が受け持つ。**種別を各コンポーネントで直接列挙しないこと**(種別が増えたときに片方だけ取り残される)。同じ理由で、パス詳細が「その種別で意味を持つ値か」を判定する種別集合もここに置く: `PATH_TYPES_WITH_BASE_SPEED`(通過速度・最小曲線半径・最大勾配を算出する種別。バックエンド`geo_path_alignment.SPEED_PATH_TYPES`の写し)、`PATH_TYPES_WITH_SPEED_LIMIT`(制限速度を扱う種別。前者+LRTが走る歩道)、`PATH_TYPES_WITH_UTILIZATION`(利用率を算出する種別。バックエンド`PathUtilizationPhase`の写し)。バックエンド側の対象種別を変えたらこちらも合わせること。

生成元と生成器はどちらも**非公開のコアシミュレータ側**にある(`constants.ts`の正は`genesis-core/consts.py`、
生成器は`genesis-core/tools/sync_consts.py`)。このリポジトリ単体では再生成できないので、
定数の追加・値変更が必要になったら自分で書き足さずユーザーに依頼すること。

# コメントの書き方

ソースコードのコメントは、日本語で丁寧に書いてください。後から読んでもわかるように。 
「今回、この変更をした理由」(前回からの差分に関するコメント)は書かない。あくまで、「現在の仕様とその理由」にフォーカスしたコメントを書くこと。

# トップメニューとHUDの表示条件

バックエンドの `GET /api/v1/sessions` が返す `current_save_id` は、**サーバ起動直後・セッション作成 API をまだ呼んでいない間**は JSON の `null` になる。`POST /api/v1/sessions` で新規セッションを作ると、ファイル保存の前から UUID 文字列が入る（`null` のままではない）。

トップメニュー／HUD の WebView ウィンドウの表示は `TopMenu.tsx`・`hud/useHudSession.ts` のポーリング受信が決める。どちらの実行形態（`tauri build` でパッケージした本番 UI も `tauri dev` の開発実行も）とも、`current_save_id` と初期ロード状態で表示を切り替える。`current_save_id` が `null` の間はトップメニューを表示し、非 `null` かつ `isLoading=false` の間はHUDを表示する。Rust側も同じ条件で `set_hud_visible` を呼ぶ(二重だが、片方の取りこぼしで出たまま/消えたままになるのを防ぐため両方残している)。

ヒントバーの表示は「セッションがロード済み(`current_save_id` が非 `null`)」かつ「設定 `hintbar_visible` が真」のANDで決まる。ゲーム開始時にネイティブウィンドウを出すのは Rust 側の `show_hintbar_if_enabled`(設定を見て show/hide を判断する唯一の入口。`hintbar.show()` を直接呼ばないこと)で、設定変更の即時反映は `Hintbar.tsx` が `settings-updated` イベントを受けて `show()`/`hide()` する。

# 実行環境

このプログラムはプロダクションでは基本的にWindowsでしか起動しない想定で、開発中のみLinux(gnome)環境で起動する場合がある。

# AGENTS.mdの運用ルール

各主要ディレクトリに、役割・既存ユーティリティ・注意点を書いた`AGENTS.md`がある(`CLAUDE.md`は`AGENTS.md`へのシンボリックリンク)。作業対象ディレクトリのAGENTS.mdを必ず先に読むこと。
開発によってAGENTS.mdの記載内容に影響が出た場合や、記載と実装の矛盾を発見した場合は、必ず該当するAGENTS.mdを更新すること。

# プロップのカテゴリとサムネイル

配置パネルの分類は `src/data/propCategories.json`（カテゴリIDと種別IDの対応）を正とする。表示名と選択可能な種別はバックエンドの `prop_types` を使い、未分類の種別は「その他」に出す。カテゴリ名は全7言語の `construction.json` の `prop_placement.categories` に置く。

`tools/render_prop_thumbnails.py` は数値ID付き通常版FBXから `src/assets/images/props/{id}.png` を生成する独立ツール。PythonとBlenderを使い、非公開制作ツールやマスターは不要。PNGはGit管理し、差分キャッシュは管理しない。UIは `utils/propThumbnail.ts` の `import.meta.glob` で画像をバンドルする。詳細は公開M文書§M.7.8を参照。

### 公共交通の車両外観カタログ

路線ごとの車両の外観(APIの`vehicle_model`。描画専用で定員・費用・速度は変わらない)の候補は `src/data/transitVehicleModels.json` を正とする。キーは路線種別(transit_masterの`transit_path_id`=区間パスのバリアント)で、値は `{variant, prop, key}` の列。`variant` はUnityの `Resources/Config/vehicle_model_config.yaml` にある車種の候補番号(路線種別→車種の対応はUnityの `Resources/Config/transit_vehicle_models.json`)、`prop` はその外観の先頭車を写した自由配置別名のプロップ種別IDで、そのサムネイルを見本画像に使う(未生成なら文言の代替表示)。表示名は全7言語の `transit.json` の `transit.vehicleModels.<key>`。**Unity側で外観を増減したら、このカタログ・`vehicle_model_config.yaml`・表示名の3つを揃えること。** バックエンドは候補一覧を持たず0以上の整数かだけを検証し、候補外の番号はUnityが路線IDと編成番号による既定の選択へ戻して描く。カタログに候補の無い種別では選択UIを出さない。見本画像は元の塗装で写したもので、Unityは車体の帯を路線カラーで塗って描く(外観の説明文 `transit.vehicleModelNote` でそう案内する)。

### 初期ロードとTips

初期ロードは `tips` ウィンドウに統合する。バックエンドへの開始要求中からTipsと回転アイコン付きの `Now Loading...` を表示し、文字は20px・太字で左から右へ光が移動する。動きを減らす設定ではアニメーションを停止する。

`begin_initial_loading` は要求ごとの `loading_id` と、空の都市を新規作成したかを保持する。POST成功後に `session:reload` と `game:start {loading_id}` を送り、同じセーブIDを再ロードした場合も初期描画を開始する。Unityの `game:loaded {loading_id}` が現在の世代と一致したら、Tipsを「ゲームを開始する」ボタンへ切り替える。TipsはRustの保持状態を問い合わせるので、WebView起動前の完了通知も取りこぼさない。

開始ボタンは `game:reveal {loading_id}` で黒画面を解除する。通常ロード・シナリオ・チュートリアル・テンプレート編集では初期設定を自動表示しない。空の都市を新規作成した場合だけTipsの次に `ParameterSetupWindow` を表示し、設定完了までHUDを抑制する。設定完了時に都市を再ロードしない。テンプレート編集から手動で開くパラメータ編集は従来どおり使用できる。

HUDとヒントバーは `current_save_id != null && !isLoading` を表示条件とする。`isLoading` は描画準備・開始ボタン待ち・初期設定中を含む。TipsのAlt+F4では待機を解除しない。開始APIの失敗時はTipsを破棄し、ロード状態を解除する。

ロード中(Tips・初期設定)にバックエンドがセッションを破棄した場合は、`start_session_polling` の `session_lost_while_loading` がゲーム終了として扱い、Tips・初期設定を閉じてトップメニューへ戻す。通常の `save_id_changed && !is_loading` 判定はロード中を除外するため、これが無いとUnityの `game:loaded` が届かず操作不能になる。POST応答前の `null` は正常なので、`backend_load_complete` か `loading_started` をGET前に確認した同一 `loading_id` の応答だけで判定する。

初期設定またはTipsから通常操作へ進む際はUI内の `initial-loading:finished` イベントでHUDとヒントバーの描画を即座に解禁し、次回のセッションポーリングを待たない。


開発時のUnityなし起動: 通常の `npm run tauri dev`（Rustの `cfg(all(debug_assertions, dev))`）では、Unityが接続中でない場合に限り、バックエンドの開始要求成功とセッション確認後に描画待ちを省略し、Tipsを開始ボタンへ切り替える。接続中かどうかは `graphic_connected`（`system:graphic_ready` で立ち、`system:graphic_stopped`＝正常終了・EditorのPlay停止で下りる）で判定し、再接続時のポップアップ一掃に使う `graphic_ready_seen`（一度立ったら戻さない）とは別に持つ。「一度でも接続したか」で判定するとPlayを止めた後の再ロードで永遠に待つ。接続中は描画完了を待ち、待機中にUnityが停止した場合はその時点で開始ボタンへ切り替える。待機中にUnityが接続した場合は開始要求を再送して描画待ちに戻る。`tauri build`（`--debug` を含む）およびリリースビルドには省略処理を含めない。環境変数やバックエンドのNuitka／uvicorn判定は使用しない。
