[English](README.md) | Japanese (original)

# City Genesis UI

Unity に重ねる Tauri・React・TypeScript の UI です。React が画面の内容を、Rust がウィンドウとプロセスを管理します。ブラウザ版は同じ UI を使い、Web シェルが Rust の役割を担います。[コンポーネント連携](../docs/ja/programs/コンポーネント連携.md)も参照してください。

## 起動

依存関係を `npm ci` でインストールしてから実行します。

```powershell
npm run tauri dev
```

Linux の GNOME で X11 を使う場合は `GDK_BACKEND=x11 npm run tauri dev` です。

開発用のモック親プロセスは `npm run mock-parent` で起動します。[http://127.0.0.1:18765](http://127.0.0.1:18765) の Web UI から「Debug Launcher」を開けます。

ブラウザ版は次のコマンドで起動します。

```powershell
npm run dev:web
```

[http://localhost:1420/](http://localhost:1420/) を開きます。コアの URL は `web/.env` の `VITE_CORE_API_URL` で、手元だけの変更は `web/.env.local` に置きます。コアの CORS が許可するポート1420を使うため、`npm run tauri dev` とは同時に動かせません。`unity/Builds/Web` に Unity の Web ビルドがあれば読み込み、なければ描画なしで動きます。

## ビルド

```powershell
npm run tauri build
```

ブラウザ版は `dist-web/` に出力します。先に Unity の Tools/Build/Web で Web ビルドを作ると、`dist-web/unity/` にコピーされます。

```powershell
npm run build:web
```

Steam 版はストア連携を有効にします（`build.ps1`）。Tauri 実行ファイルの隣にコアを `Core/`、Unity を `Graphics/` として同梱し、ビルド時に `src-tauri/target/release/build/steamworks-sys-*/out/` に生成される `steam_api64.dll` も隣に置きます。

```powershell
npm run tauri build -- --features steam --no-bundle
```

`--no-bundle` は ZIP・Steam 向けの実行ファイルだけを作り、MSI・NSIS インストーラーの生成を省きます。

## テスト

```powershell
npm test
```

`tools/` のテストをすべて実行します。個別のグループには `npm run test:tutorial` などを使います。

## フロントの版の同期

リポジトリ直下の `version.json` でフロントの版と IPC 契約番号を変更し、`npm run sync:version` で配布設定と Unity へ同期します。生成された版定義は手編集しません。
