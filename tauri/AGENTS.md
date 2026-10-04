# tauri

Unity に重ねて表示する Tauri・React・TypeScript のUI。React は各ウィンドウの内容、Rust はウィンドウとプロセスの管理を担当する。画面は `src/components/`、共通処理は `src/hooks/`・`src/utils/`、翻訳は `src/i18n/` に置く。

## 共通ルール

- バックエンドへの HTTP は `fetchApi`、Unity への送信は `utils/ipc.ts` を通す。通信監視・版確認・マスター取得を各画面へ複製しない。
- プレイモードは共有セッション状態と `usePlayMode` から参照し、画面ごとに別のフラグを持たない。
- IPC を周期的に送り続けない。状態が変わったときや再接続通知で同期し、受信値をそのまま送り返すループを作らない。
- ウィンドウの生成・位置・排他・終了処理は Rust 側へ集約する。ポップアップは別モニターに出ることがあり、閉じても WebView が破棄されるとは限らない。
- 表示文字列は i18n を使い、翻訳キーを追加したら全対応言語を揃える。対応言語と Intl ロケールは `utils/locale.ts` の共通定義を使う。
- 作者が入力する多言語テキストは共通の解決・入力部品を使い、i18n リソースと混同しない。
- 確認は `requestConfirm` を使い、`window.confirm` に依存しない。共通部品・フック・整形処理を新設前に確認する。
- 共通スタイルは `App.css` のトークンを使う。文字サイズと連動する寸法は rem を基本とし、固定寸法の例外は公開の共通スタイル仕様に従う。
- WebView の CSP は `eval` を許可しない。実行時にコード生成を必要とする依存を持ち込まない。

## 参照先

- アプリ構成: `docs/docs/programs/コンポーネント連携.md`。ウィンドウ・多言語・共通スタイル: `docs/docs/programs/UI共通基盤.md`。
- IPC: `docs/docs/programs/IPC連携の整理.md`。素材: `docs/docs/programs/素材クレジット管理.md`。
- 配下の規約: `tauri/src/components/AGENTS.md`・`tauri/src-tauri/AGENTS.md`・`tauri/src/tutorial/AGENTS.md`。
- 開発は `npm run tauri dev`、配布ビルドは `npm run tauri build`。翻訳の整合性は `tools/check_i18n_parity.py` で確認する。
