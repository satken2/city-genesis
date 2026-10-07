# Tauri + React + Typescript

This template should help get you started developing with Tauri, React and Typescript in Vite.

## Recommended IDE Setup

- [VS Code](https://code.visualstudio.com/) + [Tauri](https://marketplace.visualstudio.com/items?itemName=tauri-apps.tauri-vscode) + [rust-analyzer](https://marketplace.visualstudio.com/items?itemName=rust-lang.rust-analyzer)


## 起動方法

### Tauri本体

`npm run tauri dev`

In Linux(gnome):
`GDK_BACKEND=x11 npm run tauri dev`

### 親プロセス起動方法

`npm run mock-parent`

起動後、ブラウザで http://127.0.0.1:18765 を開くとモック親のWeb UIが表示されます。そこから「Debug Launcher」ボタンを押すとデバッグランチャーが開きます。

Tauriのメインウィンドウは仕様上 非表示 で起動します（tauri.conf.json で visible: false）。親プロセスがNamed Pipeに接続し、ui.open_window を送って初めてポップアップウィンドウが生成される設計です。

### ブラウザ版

`npm run dev:web`

http://localhost:1420/ を開く。接続するコアの URL は `web/.env` の `VITE_CORE_API_URL` で、手元だけで変えるときは `web/.env.local` に書く。コアの CORS が許可する番号(1420)を使うので、`npm run tauri dev` とは同時に動かせない。Unity の Web ビルド(`unity/Builds/Web`)があれば読み込み、無ければ描画なしで動く。

# ビルド

```
npm run tauri build
```

ブラウザ版は `dist-web/` に出力する。先に Unity で Web ビルド(メニューの Tools/Build/Web)を作っておくと `dist-web/unity/` に写される。

```
npm run build:web
```

Steam 版の配布ビルドはストア連携を有効にする(`build.ps1`)。インストール先での配置は `docs/docs/programs/コンポーネント連携.md` の「起動順序」に従い、`steam_api64.dll`(ビルドで `src-tauri/target/release/build/steamworks-sys-*/out/` にできる)を実行ファイルの隣に置く。

```
npm run tauri build -- --features steam
```