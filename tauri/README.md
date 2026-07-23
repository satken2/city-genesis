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

# ビルド

```
npm run tauri build
```