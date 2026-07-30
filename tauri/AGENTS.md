# プロジェクト概要

本レポジトリは都市建設シミュレーションゲーム"City Genesis"のUI(Tauri + React)レポジトリです。
Unityのグラフィックの上に透明なWebViewウィンドウとして重なって表示され、ゲーム内のウィンドウ・メニュー・グラフ類はすべてここで描画されます。
バックエンド(FastAPI)とはHTTP、Unity(グラフィック)とはMemoryMappedFile(MMF)のIPCで通信します。

# アーキテクチャ(知らないと絶対わからない)

- **1つのReactアプリ(`src/App.tsx`)が全ウィンドウを描画する。** Tauriのウィンドウラベル(`main` / `taskbar` / `topmenu` / `loading` / `notification` / `hintbar` / `tutorial` / `parameter-setup` / `tips` / それ以外=popup)によって`windowKind`が決まり、描画内容が分岐する。
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
  - hooks/             ... 共通Reactフック(useTemplateMode=テンプレートモード判定, useConstructionBudgetRow=建設中/解体中の1エポックコスト・残りエポック数の取得)
  - utils/             ... 共通処理(下記の一覧参照)
  - i18n/              ... 多言語リソース(ja/*.json・en/*.json をドメインごとに分割。index.tsがglobで集約)
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
| `hintbar.ts` | 画面下部ヒントバーのテキスト設定 |
| `settings.ts` | アプリ設定のロード・保存 |
| `playSound.ts` | 効果音再生 |
| `tutorial.ts` | チュートリアルの表示制御 |
| `agentIcon.ts` / `parcelIcon.ts` / `pathIcon.ts` / `resourceIcon.ts` | 各種アイコン画像の解決 |

# 多言語対応

全体的に、多言語対応できるように作ってね。
表示文字列はreact-i18nextの`useTranslation()`(`t("key")`)を使い、文字列のハードコード禁止。
翻訳リソースは`src/i18n/ja/`・`src/i18n/en/`配下にドメインごとのJSONへ分割している(例: `agent.json`, `budget.json`, `statistics.json`)。各ファイルはトップレベル名前空間をそのまま含み、`index.ts`が`import.meta.glob`で全ファイルを単一の`translation`名前空間へ集約する(キー参照は従来どおり`t("agent.foo")`)。**キーを追加するときは、その名前空間を含むファイルの`ja`・`en`両方に必ず追加する**こと。新しいトップレベル名前空間を足す場合は既存のドメインファイルに入れるか、新規ファイルを作れば自動で拾われる。

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

# トップメニューとタスクバーの表示条件

バックエンドの `GET /api/v1/sessions` が返す `current_save_id` は、**サーバ起動直後・セッション作成 API をまだ呼んでいない間**は JSON の `null` になる。`POST /api/v1/sessions` で新規セッションを作ると、ファイル保存の前から UUID 文字列が入る（`null` のままではない）。

トップメニュー／タスクバーの WebView ウィンドウの表示は `TopMenu.tsx`・`Taskbar.tsx` のポーリングが決める。どちらの実行形態（`tauri build` でパッケージした本番 UI も `tauri dev` の開発実行も）とも、`current_save_id` のみで表示を切り替える。`current_save_id` が `null` の間はトップメニューを表示し、非 `null` の間はタスクバーを表示する。

# 実行環境

このプログラムはプロダクションでは基本的にWindowsでしか起動しない想定で、開発中のみLinux(gnome)環境で起動する場合がある。

# AGENTS.mdの運用ルール

各主要ディレクトリに、役割・既存ユーティリティ・注意点を書いた`AGENTS.md`がある(`CLAUDE.md`は`AGENTS.md`へのシンボリックリンク)。作業対象ディレクトリのAGENTS.mdを必ず先に読むこと。
開発によってAGENTS.mdの記載内容に影響が出た場合や、記載と実装の矛盾を発見した場合は、必ず該当するAGENTS.mdを更新すること。
