# プロジェクト概要

本レポジトリは都市建設シミュレーションゲーム"City Genesis"のUI(Tauri + React)レポジトリです。
Unityのグラフィックの上に透明なWebViewウィンドウとして重なって表示され、ゲーム内のウィンドウ・メニュー・グラフ類はすべてここで描画されます。
バックエンド(FastAPI)とはHTTP、Unity(グラフィック)とはMemoryMappedFile(MMF)のIPCで通信します。

# アーキテクチャ(知らないと絶対わからない)

- **1つのReactアプリ(`src/App.tsx`)が全ウィンドウを描画する。** Tauriのウィンドウラベル(`main` / `icon-panel` / `cockpit` / `system-tray` / `topmenu` / `loading` / `notification` / `hintbar` / `tutorial` / `parameter-setup` / `tips` / それ以外=popup)によって`windowKind`が決まり、描画内容が分岐する。
- **ゲーム中に常駐するHUDは3枚のウィンドウに分かれている**(`icon-panel`=画面左端の縦並びメニュー、`cockpit`=画面下部中央、`system-tray`=画面右下)。Unityの3Dビューに重ねる透明ウィンドウは、見た目が透明な領域でもクリックをWebViewが吸ってしまうため、**パネルの実面積ぴったりのウィンドウに分割する必要がある**(1枚の全画面ウィンドウにはできない)。生成・配置・表示切替はRust側(`window.rs`の`create_hud_windows` / `layout_hud_windows` / `set_hud_visible`)、中身は`src/components/hud/`。
- **ポップアップパネルはすべて `React.lazy` で遅延インポートされる。** メインバンドルから重い依存(`three.js` / `recharts`)を切り離し、新規ウィンドウ起動時のJSパース量を減らすため。ポップアップの `<section>` 内は `<Suspense key={`${window_type}:${entity_id}:${label}`}>` で囲まれ、context切り替え時に内部コンポーネントが確実に再マウントされる(各Panelが `initialEntityId` を `useState` 初期値に取る実装のため、prop変更を追跡できないことを補う)。新しいパネルを追加するときも `lazy(() => import(...))` の形式で追加すること。
- ポップアップウィンドウの種類(サイズ、単一/複数インスタンスのポリシー等)は`src-tauri/config/window-registry.json`で定義される。同時に開けないウィンドウの組は`window-mutex-groups.json`で定義される。
- 各ウィンドウは起動時にTauriコマンド`get_window_context`で自分の`WindowContext`(ウィンドウタイプ、対象エンティティ等)を取得する。
- 新しいウィンドウを追加する手順: (1)`src/components/`配下にコンポーネント作成 → (2)`App.tsx`のimport(必ず `lazy(() => import(...))` 形式)と`SPECIALIZED_PANEL_TYPES`に追加 → (3)`window-registry.json`に定義を追加。

# プログラム構造

```
- src/
  - App.tsx            ... 全ウィンドウのエントリポイント(ラベルで分岐)
  - components/        ... ドメイン別のUIコンポーネント(詳細はcomponents/AGENTS.md)
  - contexts/          ... MasterDataContext(マスターデータ), FontSizeContext
  - data/              ... UI内に静的に持つ掲載データ(credits.ts=クレジット表示の掲載内容)
  - hooks/             ... 共通Reactフック(useTemplateMode=テンプレートモード判定, useConstructionBudgetRow=建設中/解体中の1エポックコスト・残りエポック数の取得, useVariantAffordability=建設パネルのバリアント単位の資金目安〔GET /{paths,buildings}/variant-affordability〕)
  - utils/             ... 共通処理(下記の一覧参照)
  - i18n/              ... 多言語リソース(言語ディレクトリごとにドメイン別JSONを配置。index.tsがglobで集約)
  - types.ts           ... 共通型定義(WindowContext, AgentData等)
  - constants.ts       ... 自動生成の定数(編集禁止)
  - local-constants.ts ... UI側で独自定義する定数
  - types/generated.ts ... 自動生成のAPIスキーマ(編集禁止)
- src-tauri/           ... Rust側(ウィンドウ管理、MMF IPC。詳細はsrc-tauri/AGENTS.md)
```

# src/utils の一覧(新しい共通処理を書く前に必ず確認)

| ファイル | 内容 |
| --- | --- |
| `api.ts` | バックエンドHTTP通信の共通窓口。`fetchApi<T>()`と`buildApiUrl()`。**fetchを直接書かないこと**。fetchApiはAcceptヘッダでMessagePackを受け入れ宣言し、バックエンドの対応エンドポイント(bbox一括取得系)からはバイナリで受信して`@msgpack/msgpack`でデコードする(Content-Typeで自動判別。呼び出し側は形式を意識しない) |
| `ipc.ts` | Unityへのイベント送信(`sendChangeState`, `sendEditBuildingConfirm`等)。**Unityに何か伝えたいときはここに関数を追加する** |
| `notification.ts` | 通知ウィンドウの表示(`showNotification`) |
| `log.ts` | Rust側ファイルログ(実行ファイル横の`city-genesis/tauri.log`)へのINFO転送(`logInfoToFile`)。本番でconsoleが見えないため、無言で止まりうる重要フロー(エポック実行など)の進行記録に使う。なお未キャッチ例外・unhandledrejection・`console.error`は`main.tsx`が自動で同ログへERROR転送する |
| `cityNotifications.ts` | L1通知(重大通知)の共通処理。`fetchL1Notifications`(GET /api/v1/notifications)・`formatL1NotificationMessage`(i18nキー`notifications.types.<type>`への差し込み。未知typeはフォールバック)・`NOTIFICATION_ENTITY_WINDOW_TYPES`(通知対象→詳細ウィンドウの対応表)。コックピット(hud/Cockpit.tsx)とCityNotificationsWindowが共用 |
| `hintbar.ts` | 画面下部ヒントバーのテキスト設定 |
| `locale.ts` | 対応言語(`SUPPORTED_LANGUAGES`)と、言語コード→`Intl`用ロケールの変換(`resolveIntlLocale`)・検証(`isSupportedLanguage`)・対応言語コードへの丸め(`resolveSupportedLanguage`。`ja-JP`のような地域付きコードを`ja`へ寄せる。プルダウンの現在値解決に使う)。詳細は「多言語対応」の節 |
| `date.ts` | ゲーム内仮想暦(1ヶ月=30日/1年=360日)の整形。`formatEpochDate` / `formatEpochDateRange` |
| `money.ts` | 金額の整形。`formatCompactAmount`(有効3桁のコンパクト表記) / `formatExactAmount`(3桁区切り) |
| `settings.ts` | アプリ設定のロード・保存 |
| `weatherAudio.ts` | Cockpitだけが所有する共通環境音と雨音のループ・クロスフェード。音量はmaster×sfx。長いOggをHTMLAudioElementで再生し、ゲーム外では停止する |
| `playSound.ts` | 効果音再生。**音声ファイルは必ず`import`して得たURLを渡すこと**(`"src/assets/sounds/x.wav"`のようなパス文字列を直接渡すとバンドル対象から外れ、開発実行では鳴るのに本番ビルドだけ無音になる) |
| `tutorial.ts` | チュートリアルの表示制御 |
| `agentIcon.ts` / `parcelIcon.ts` / `pathIcon.ts` / `resourceIcon.ts` | 各種アイコン画像の解決 |

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
  products: Record<number, ProductMasterEntry>;
  paths: Record<number, PathMasterEntry>;
};
```

# 編集してはいけないファイル

`src/types/generated.ts`と`src/constants.ts`はプログラムによって自動生成されるファイルなので、編集しないように。
これらのファイルを編集する必要がある場合は、ユーザーに相談すること。
UI側で独自に定義する定数は`local-constants.ts`に定義する。

# コメントの書き方

ソースコードのコメントは、日本語で丁寧に書いてください。後から読んでもわかるように。 
「今回、この変更をした理由」(前回からの差分に関するコメント)は書かない。あくまで、「現在の仕様とその理由」にフォーカスしたコメントを書くこと。

# トップメニューとHUDの表示条件

バックエンドの `GET /api/v1/sessions` が返す `current_save_id` は、**サーバ起動直後・セッション作成 API をまだ呼んでいない間**は JSON の `null` になる。`POST /api/v1/sessions` で新規セッションを作ると、ファイル保存の前から UUID 文字列が入る（`null` のままではない）。

トップメニュー／HUD の WebView ウィンドウの表示は `TopMenu.tsx`・`hud/useHudSession.ts` のポーリング受信が決める。どちらの実行形態（`tauri build` でパッケージした本番 UI も `tauri dev` の開発実行も）とも、`current_save_id` のみで表示を切り替える。`current_save_id` が `null` の間はトップメニューを表示し、非 `null` の間はHUD3枚を表示する。Rust側も同じ条件で `set_hud_visible` を呼ぶ(二重だが、片方の取りこぼしで出たまま/消えたままになるのを防ぐため両方残している)。

ヒントバーの表示は「セッションがロード済み(`current_save_id` が非 `null`)」かつ「設定 `hintbar_visible` が真」のANDで決まる。ゲーム開始時にネイティブウィンドウを出すのは Rust 側の `show_hintbar_if_enabled`(設定を見て show/hide を判断する唯一の入口。`hintbar.show()` を直接呼ばないこと)で、設定変更の即時反映は `Hintbar.tsx` が `settings-updated` イベントを受けて `show()`/`hide()` する。

# 実行環境

このプログラムはプロダクションでは基本的にWindowsでしか起動しない想定で、開発中のみLinux(gnome)環境で起動する場合がある。

# AGENTS.mdの運用ルール

各主要ディレクトリに、役割・既存ユーティリティ・注意点を書いた`AGENTS.md`がある(`CLAUDE.md`は`AGENTS.md`へのシンボリックリンク)。作業対象ディレクトリのAGENTS.mdを必ず先に読むこと。
開発によってAGENTS.mdの記載内容に影響が出た場合や、記載と実装の矛盾を発見した場合は、必ず該当するAGENTS.mdを更新すること。
