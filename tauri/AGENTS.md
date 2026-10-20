# tauri

Unity に重ねて表示する Tauri・React・TypeScript のUI。React は各ウィンドウの内容、Rust はウィンドウとプロセスの管理を担当する。Tauri はストアの起動対象で、コアと Unity を子プロセスとして起動・監視する。画面は `src/components/`、共通処理は `src/hooks/`・`src/utils/`、翻訳は `src/i18n/` に置く。

`web/` はブラウザ版のページ。同じ `src/` を各ウィンドウの iframe で動かし、Rust の役割は Web シェル(`web/shell/`)が受け持つ。Tauri の API は Web ビルドだけ `web/tauri-shim/` に差し替わる。開発用の地図ページ(`web/debug-map/`。Unity の代わりに2D地図を置く)は、コアが配る開発用ビルド(`npm run build:web:core`)にだけ入り、製品のビルドには含めない。

## 共通ルール

- バックエンドへの HTTP は `fetchApi`、Unity への送信は `utils/ipc.ts` を通す。通信監視・版確認・マスター取得を各画面へ複製しない。
- プレイモードは共有セッション状態と `usePlayMode` から参照し、画面ごとに別のフラグを持たない。
- IPC を周期的に送り続けない。状態が変わったときや再接続通知で同期し、受信値をそのまま送り返すループを作らない。
- ウィンドウの生成・位置・排他・終了処理は Rust 側へ集約する。ポップアップは別モニターに出ることがあり、閉じても WebView が破棄されるとは限らない。
- 表示文字列は i18n を使い、翻訳キーを追加したら全対応言語を揃える。対応言語と Intl ロケールは `utils/locale.ts` の共通定義を使う。
- 作者が入力する多言語テキストは共通の解決・入力部品を使い、i18n リソースと混同しない。
- 確認は `requestConfirm` を使い、`window.confirm` に依存しない。共通部品・フック・整形処理を新設前に確認する。
- 共通スタイルは `App.css` のトークンを使う。本文・表・入力欄・通常の操作ラベルは `1rem` に揃え、見出し・強調値・補足だけに大小を付ける。文字サイズと連動する寸法は rem を基本とし、固定寸法が必要な箇所は共通スタイルと該当部品の定義に従う。
- WebView の CSP は `eval` を許可しない。実行時にコード生成を必要とする依存を持ち込まない。
- `src/` から新しい Tauri の API(関数・メソッド・プラグイン)を使うときは、`web/tauri-shim/` にも実装を足す。足りない名前は Web ビルドが失敗する。
- Rust のウィンドウ管理・起動とセッション監視・IPC の中継・コマンドを変えたら、`web/shell/` の同じ処理も揃える。ブラウザ版だけの違いは Web シェルと差し替えモジュールの中に閉じ、`src/` に環境の判定を書かない。開発用の地図ページの部品は `web/debug-map/` に置き、`src/` から import しない。狭い画面への対応は、ウィンドウの幅で切り替える CSS で行う。

## 参照先

- アプリ構成: `docs/ja/programs/コンポーネント連携.md`。ウィンドウ・多言語・共通スタイル: `docs/ja/programs/UI共通基盤.md`。
- IPC: `docs/ja/programs/IPC連携の整理.md`。素材: `docs/ja/contributing/素材クレジット管理.md`。
- 配下の規約: `tauri/src/components/AGENTS.md`・`tauri/src-tauri/AGENTS.md`・`tauri/src/tutorial/AGENTS.md`。
- Windows環境でデスクトップ版を開発する場合は `npm run tauri dev`、配布ビルドは `npm run tauri build`(Steam 版は `npm run tauri build -- --features steam`)。翻訳の整合性は `tools/check_i18n_parity.py` で確認する。
- テストは `npm test` ですべて実行する。複数のファイルからなるテストは `tools/<名前>/run.mjs` を入口にし、単独のテストは `tools/*-test.mjs`・`tools/*-test.ts` の名前で置く(どちらも `npm test` が拾う)。
- ブラウザ版は `npm run dev:web`(開発サーバー)・`npm run build:web`(`dist-web/` へ出力)。構成は `docs/ja/programs/コンポーネント連携.md` の「ブラウザ版」。
