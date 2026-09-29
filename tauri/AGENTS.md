# tauri フォルダ(UI)

City GenesisのUI(Tauri + React + TypeScript)。Unityのグラフィックの上に透明なWebViewウィンドウとして重なり、ゲーム内のウィンドウ・メニュー・グラフ類をすべて描画する。バックエンド(FastAPI)とはHTTP、Unityとは MemoryMappedFile(MMF)で通信する。
ここに書くのはアプリ全体の構成と全体に跨る規約だけ。コンポーネントは `src/components/AGENTS.md`、Rust側は `src-tauri/AGENTS.md` を先に読むこと。

## フォルダ構成

| パス | 内容 |
| --- | --- |
| `src/App.tsx` | 全ウィンドウのエントリポイント(Tauriのウィンドウラベルで分岐) |
| `src/main.tsx` | 起動処理。未キャッチ例外・`console.error` をRust側のファイルログへ転送する |
| `src/components/` | ドメイン別のUIコンポーネント(`components/AGENTS.md`) |
| `src/contexts/` | `MasterDataContext`(マスターデータ)・`FontSizeContext` |
| `src/hooks/` | 共通Reactフック。仕様は各ファイル冒頭のコメント。APIの画像は `useApiImageUrl`(fetch→Blob URL)で表示する |
| `src/utils/` | 共通処理(下記の一覧) |
| `src/data/` | 静的な掲載データ。`credits.ts`(クレジット表示の正)・`propCategories.json`・`transitVehicleModels.json`・`resourcePresets.ts` |
| `src/i18n/` | 翻訳リソース `<言語コード>/<ドメイン>.json`(`index.ts` がglobで集約) |
| `src/assets/` | 画像(`images/`)と音声(`sounds/`) |
| `src/types.ts` | 手書きの共通型(`WindowContext` など) |
| `src/constants.ts` / `src/types/generated.ts` | 自動生成(編集禁止) |
| `src/local-constants.ts` | UI独自の定数と、自動生成に載らないバックエンド定数の写し |
| `src/App.css` | 全ウィンドウ共通のグローバルスタイル |
| `src-tauri/` | Rust側(ウィンドウ管理・MMF IPC・セッションポーリング)。`config/window-registry.json`=ポップアップ定義、`window-mutex-groups.json`=同時に開けない組 |
| `tools/` | 開発補助スクリプト(翻訳キー整合検査・プロップサムネイル生成・Unity音源の再生成・開発プロセス掃除 ほか)と検証。`tutorial/generate-schema-validator.mjs`(チュートリアルのJSON Schema検証コードの事前生成)、`api-client/`(`npm run test:api-client`。版ヘッダーの付与範囲・画像のBlob URL・版照合の失敗)、`scenarios/`(`npm run test:scenarios`。シナリオ管理・標高ファイルの取り込み・ニューゲーム)、`terrain-thumbnail-test.mjs`(地形の平面図の画素生成)、`terrain-editor-save-test.mjs`(地形エディタの保存引数・ロック情報の正規化・保存後の資源IDの置き換え) |
| `scripts/` | 河川生成(`terrainRiver.ts`)の開発用検証スクリプト(`npx tsx`。CI外) |
| `*-launch.toml` | Rust側が子プロセスとしてバックエンド/Unityを起動するコマンド |
| `SPEC.md` | 初期設計時の仕様書(現行の正ではない) |

## ウィンドウ構成(知らないと分からない)

- 1つのReactアプリが全ウィンドウを描く。ラベル `main` / `icon-panel` / `cockpit` / `system-tray` / `display-tray` / `topmenu` / `notification` / `hintbar` / `tutorial` / `parameter-setup` / `tips` はRust側が直接生成する常駐ウィンドウ、それ以外は `window-registry.json` 駆動のポップアップ。
- HUDは `icon-panel`・`cockpit`・`system-tray`・`display-tray` の4枚。透明WebViewは透明な領域でもクリックを吸うので、1枚の全画面ウィンドウにはできない。生成・配置は `src-tauri/src/window.rs`、中身は `components/hud/`。
- ポップアップはHUDと同じモニターにあるとは限らない(設定のマルチディスプレイモードでは別のモニターに出る)。ウィンドウの位置はRust側が決め、React側からは動かさない。
- `WindowContext` を持つのはポップアップだけ。常駐ウィンドウは `App.tsx` の `contextlessLabels` で `get_window_context` の対象から外す。Rust直接生成のウィンドウを追加してここに足し忘れると、起動時に `get_window_context` が失敗し、同じ `initialize()` 内の言語復元まで巻き添えでスキップされる。
- ポップアップのパネルは必ず `lazy(() => import(...))` で追加する(重い依存をメインバンドルから切り離すため)。`<Suspense>` の key に種別・エンティティID・ラベルを含めてあり、対象の切り替え時に内部が再マウントされる。
- 追加手順は `components/AGENTS.md`、全体像は `../docs/docs/programs/コンポーネント連携.md` の「UIのウィンドウ構成」。

## 必ず守る規約・知らないと危険なこと

- 自動生成ファイル(`src/constants.ts`・`src/types/generated.ts`)を編集しない。正と生成器は非公開のコア側(`genesis-core/consts.py`・`tools/sync_consts.py`)にあり、このレポジトリ単体では再生成できない。必要ならユーザーに依頼する(Skill: `consts-sync`)。
- `local-constants.ts` の `PATH_TYPES_INFRA`・`PATH_TYPES_WITH_*` は、生成器がタプルを出力しないために置いているバックエンド定数の写し。バックエンド側を変えたら合わせる。パス種別を各コンポーネントで直接列挙しない。
- バックエンドHTTPは `utils/api.ts` の `fetchApi<T>()` 経由のみ。`fetch` を直接書かない(MessagePack受信とエラー処理を一箇所に寄せている)。版ヘッダー(`X-City-Genesis-API`)もここと置き換え後の `fetch` が付けるので、各画面でヘッダーを組まない。Rust側から送る要求は `version.rs` の `headers()` / `client()` を通す(`src-tauri/AGENTS.md`)。
- APIの画像(サムネイル等)を `<img src>` にAPIのURLで直接書かない。画像要求は通信契約の版ヘッダー・版の照合を通らない。`hooks/useApiImageUrl` で取得して Blob URL を表示する(`ScenarioThumbnail` が実例)。
- WebViewはCSPで `eval` を許していない(`tauri.conf.json`)。実行時に `new Function` を使うライブラリを入れない。JSON Schema検証は `npm run gen:tutorial-validator` の事前生成コードを使う。本番ビルドは devtools を含まない。
- Unityへの送信は `utils/ipc.ts` に関数を足して行う。`setInterval` 等で周期的に送らない(Unity不在の間はMMFに溜まり続け、満杯になると以後の送信が `MMF buffer full` で全滅する)。Unity起動後の再同期は `graphic-reconnected` イベントを受けて送る。
- 3Dビューの表示切替は2系統。`sendChangeDisplay` はユーザーの選択(Unityが通常表示として記憶)、`sendViewApplyPreset` はウィンドウを開いている間だけの一時差し替え。編集作業中だけ表示を変えたいときに前者を使わない(ユーザーの選択を上書きする)。
- 確認ダイアログは `utils/confirmDialog.ts` の `requestConfirm()`。`window.confirm` は使わない(tauri-plugin-dialogがPromiseを返す非同期版に差し替えるため、`if (!window.confirm(...))` は常に素通りする)。
- 表示文字列は `useTranslation()` の `t("key")`。ハードコード禁止。
- 作者が書く文(シナリオの紹介文・初期の都市名・クエストの名前と説明)は i18n ではなく多言語テキスト(`LocalizedText`)で、コアから `{言語コード: 文}` で届く。そのまま描画せず `resolveLocalizedText(値, i18n.language)` で引き、入力は `common/LocalizedTextFields` を使う(英語が必須)。
- 対応言語は `utils/locale.ts` の `SUPPORTED_LANGUAGES`(`ja` / `en` / `es-419` / `zh-Hans` / `zh-Hant` / `fr` / `ko`)が唯一の正で、日本語が原本。言語を増やすときは `SUPPORTED_LANGUAGES`・`INTL_LOCALES`、`src/i18n/<コード>/` の全JSON、`tools/check_i18n_parity.py` の `LANGS` を揃える。
- 翻訳キーを足したら全言語のJSONを更新し、`python tools/check_i18n_parity.py` で過不足と `{{変数}}` の食い違いを確認する。
- i18nインスタンスはウィンドウごとに独立し、言語変更は `settings-updated` イベントで各ウィンドウが追従する(`App.tsx`)。`Intl` に渡すロケールは `resolveIntlLocale(i18n.language)` で解決し、`i18n.language` を直接渡さない。
- マスターデータ(建物・道路の種別と表示名など)は起動時に取得済み。`useMasterData()`(`contexts/MasterDataContext.tsx`。キー一覧は `MasterData` 型)で参照し、各コンポーネントで取り直さない。
- 音声は `src/assets/sounds/` に Ogg Opus(`.opus`)で置き、`.wav` は置かない。再生には `import` したURLを渡す(パス文字列の直指定は本番ビルドだけ無音になる)。外部素材の取り込み・差し替えは `src/data/credits.ts` への追記とセット(Skill: `asset-credits`)。
- スクロールバーは `App.css` で全域を従来型に切り替えている。`scrollbar-width` / `scrollbar-color` は使わない(指定した要素で `::-webkit-scrollbar` が無効化され、右端を隠すオーバーレイ式に戻る)。
- トップメニュー系ウィンドウのフラット意匠は `App.css` の `--flat-*` トークンが唯一の正で、色・影を直値で書かない。どちらも詳細は `コンポーネント連携.md` の「UIの共通スタイル」。
- 文字サイズは rem で書く。1rem = 設定の文字サイズ = 本文で、見出し・補足はその倍率にする(本文を 1rem 未満で書くと、設定値より小さく表示される)。文字に連動する寸法(行の高さ、ラベル列やタブ列の幅)も rem で書き、px の数値しか受け付けない寸法(recharts の Y 軸の `width` など)は `hooks/useRemPx` で rem から換算する。グラフの `fontSize` は `"0.93rem"` のような rem の文字列で渡す。px で固定してよいのは、寸法固定の常駐ウィンドウ(HUD・ヒントバー・チュートリアル・通知・Tips)、設定フォーム(`SettingsForm.css`)、canvas などの図の中の文字だけ(理由は同節)。
- HUD・ヒントバーの表示可否はRust側とUI側(`hud/useHudSession.ts`・`TopMenu.tsx`)が同じ条件で二重に判定する(取りこぼし対策)。ヒントバーをRust側で出す入口は `show_hintbar_if_enabled` だけ(`hintbar.show()` を直接呼ぶと設定 `hintbar_visible` が無視される)。
- 本番ではconsoleが見えない。無言で止まりうる重要フロー(エポック実行など)は `utils/log.ts` の `logInfoToFile` で実行ファイル横の `city-genesis/tauri.log` へ記録する。
- 本番はWindows(WebView2)のみ。開発中はLinux(gnome)で起動することもある(`GDK_BACKEND=x11 npm run tauri dev`)。
- コメントの書き方・AGENTS.mdの運用などレポジトリ共通の規約は `../AGENTS.md`。

## 開発サーバ

`npm run tauri dev`(Viteは1420固定ポート)。バックエンドは別途起動する(実物または `programs/モックバックエンド.md`)。Unityなしでも起動でき、その挙動は `コンポーネント連携.md` の「初期ロードとTips」。残った開発プロセスは `npm run cleanup-dev` で掃除する。本番ビルドは `npm run tauri build`。

## src/utils の一覧(新しい共通処理を書く前に必ず確認)

| ファイル | 内容 |
| --- | --- |
| `api.ts` | `fetchApi<T>()` / `buildApiUrl()`。HTTPの唯一の窓口。`cache`オプションで取得時のHTTPキャッシュ方針を指定できる。`configureBackendRequests` が `fetch` を置き換え、版の照合と版ヘッダーの付与を行う。版ヘッダーは設定したバックエンドの `/api/v1/` 配下にだけ付ける |
| `ipc.ts` | Unityへのイベント送信(`send*`) |
| `notification.ts` / `tutorial.ts` | 通知ウィンドウ・チュートリアルウィンドウの表示 |
| `cityNotifications.ts` | L1通知(重大通知)の取得・文言化・詳細ウィンドウ対応表 |
| `confirmDialog.ts` | `requestConfirm()`。確定=true / 取消・×閉じ=false |
| `hintbar.ts` | `setHintbarText`(置換) / `clearHintbarTextIf(expected)`(自分が出した案内だけ消す) |
| `log.ts` | `logInfoToFile`。Rust側ファイルログへのINFO転送 |
| `settings.ts` | アプリ設定のロード・保存。保存時に `settings-updated` が全ウィンドウへ飛ぶ |
| `locale.ts` | 対応言語と `Intl` ロケールの解決(`SUPPORTED_LANGUAGES` / `resolveIntlLocale` / `resolveSupportedLanguage`)。作者が書く多言語テキスト `LocalizedText`(`{言語コード: 文}`。シナリオの紹介文・初期の都市名・クエストの名前と説明)の表示 `resolveLocalizedText`(表示言語 → 英語 → 対応言語)・送信前の整理 `compactLocalizedText`・英語の欠け `missingEnglish` |
| `date.ts` / `money.ts` | 仮想暦(1ヶ月=30日・1年=360日)と金額の整形 |
| `parking.ts` | 駐車メーターの使用率 `parkingUsage`。表示桁に丸めてから割る(生値だと「20 / 20台」で100%超に光る) |
| `constructionTags.ts` | 建設プロジェクトのタグ。`fetchEntityTag` は専用API(`tag` は単体取得に載らない)。`TAGGABLE_STATUSES` はバックエンドの写し |
| `constructionFunding.ts` | 公共工事の発注時資金審査の先回り判定(最終判定はバックエンド) |
| `costRatio.ts` | 建設コストの標準コスト比の色分け(建物・道路で共用) |
| `playSound.ts` / `bgm.ts` | 効果音(URLごとにキャッシュ)・BGM(`BgmManager`。Cockpitだけが所有) |
| `weatherAudio.ts` | 音量設定をUnityへ送るだけ(再生しない) |
| `epochPresentation.ts` / `epochFlow.ts` | 演出ID付きの暗転通知待ち、HTTPと日没の並行実行、処理時間に応じたTipsの開閉。結果画面のクローズまで黒幕を保持する |
| `agentIcon.ts` / `parcelIcon.ts` / `pathIcon.ts` / `resourceIcon.ts` / `ledgerIcon.ts` / `bdCategoryIcon.ts` / `pathCategoryIcon.ts` | 各種アイコン画像の解決。`bdCategoryIcon` は建物建設パネルの分類(`assets/images/bd_categories/{id}.png`)、`pathCategoryIcon` はパス建設パネルのカテゴリ(`assets/images/paths/path_{path_type}.png`)。`agentIcon` の `resolveAgentVariantIcon` はバリアント専用画像だけを返す(無ければ空文字) |
| `propThumbnail.ts` | プロップサムネイルの解決。画像のglobはここ1箇所だけ |
| `saves.ts` | 保存データの型(`SaveEntry`・保存元 `ScenarioOrigin`・パック所有状況 `PackStatus`・開始設定の項目 `ScenarioStartOption`)、シナリオの見出し `scenarioDisplayTitle`、ロード前確認 `prepareSaveLoad`、理由コードの取り出し `errorCode`(`ApiError` の code、Rust の「CODE: 本文」の先頭)、エラー表示 `saveError`(`saveCompatibility.<code>` で翻訳。コードが無ければ本文のまま)、シナリオ画像の登録 `uploadScenarioThumbnail` |
| `mapSize.ts` | マップの大きさ(m)をkmで整形する `formatMapSize`(例: 7 × 6 km。ニューゲームの事実欄・地形の取り込みの見込み) |

## 詳細の参照先

- チュートリアルの定義と進行基盤は `src/tutorial/`。シナリオ作者向け設計は `../docs/tutorials/`、通信・制約・永続化の契約は `../docs/docs/programs/チュートリアル基盤.md`。操作名や条件を追加したらJSON Schemaと `npm run test:tutorial` を揃える。`schema.json` を変えたら `npm run gen:tutorial-validator` で `schemaValidator.generated.ts` を作り直す(`src/tutorial/AGENTS.md`)。

- 公開ドキュメント(`../docs/docs/programs/`): コンポーネント連携(ウィンドウ構成・セッション状態・CSP・初期ロード・多言語対応・共通スタイル)、シナリオ制作と配布、セーブデータ仕様、IPC連携の整理(MMF・イベント一覧・起動フロー)、モックバックエンド、ファイル配置、通知レベル管理、素材クレジット管理、サウンドシステム、建設プロジェクト管理。プロップ分類とサムネイルは `geometries/M_地形プロップ.md` §M.7.8、車両外観カタログは `geometries/O_車両の走行描画.md`。
- Skill: `genesis-ipc`(Unityとの通信イベント・APIの追加)、`asset-credits`(素材の取り込み)、`consts-sync`(コア側。共有定数の同期)。
- 配下: `src/components/AGENTS.md`(共通部品・HUD・ウィンドウ追加手順・各パネルの契約)、`src-tauri/AGENTS.md`(Rust側)。

## 版情報とシナリオ

- フロントの版の正は `../version.json`。`npm run sync:version` でTauri・Unityの配布設定を同期する。開発・ビルド前処理も同じ同期を使う。
- `utils/api.ts` の `configureBackendRequests` は既存のバイナリHTTP取得も含めてコアの通信契約を照合し、版ヘッダーを付ける。API互換性検証・ヘッダー付与を各画面へ複製しない。
- 保存データの型・ロード前確認・エラー表示は `utils/saves.ts`。新しいロード入口も `prepareSaveLoad` を通し、確認後にRustの開始コマンドを呼ぶ。
- シナリオの保存元(`local` / `workshop` / `dlc`)・読み取り専用・暗号化・必要パックの契約は `../docs/docs/programs/シナリオ制作と配布.md` と `セーブデータ仕様.md`。自作とみなすのは `origin === "local" && !read_only` で、それ以外のシナリオは変更操作を出さず「コピーして編集」だけにする。
