import React from "react";
import ReactDOM from "react-dom/client";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import "./i18n";
import App from "./App";
import { FontSizeProvider } from "./contexts/FontSizeContext";

/**
 * フロントエンドで発生したエラーを Rust 側のログファイルに転送する。
 * Windows 本番ビルドではコンソールが表示されないため、ファイルログが重要となる。
 *
 * 未キャッチ例外・unhandledrejection に加えて console.error もフックする。
 * catch した上で console.error に流すだけのエラー処理(通信失敗など)が多く、
 * それらは本番ではどこにも残らず「無言で失敗した」ように見えてしまうため。
 * どのウィンドウで起きたか分かるよう、ウィンドウラベルを先頭に付ける。
 */
function setupFrontendErrorLogging() {
  const label = getCurrentWindow().label;
  const forward = (message: string) => {
    invoke("log_frontend_error", { message: `[${label}] ${message}` }).catch(() => {});
  };

  window.addEventListener("error", (event) => {
    forward(`${event.message} at ${event.filename}:${event.lineno}:${event.colno}`);
  });

  window.addEventListener("unhandledrejection", (event) => {
    const reason = event.reason instanceof Error ? event.reason.stack : String(event.reason);
    forward(`Unhandled rejection: ${reason}`);
  });

  // Error は stack、オブジェクトは JSON にして1行へまとめる
  const formatArg = (arg: unknown): string => {
    if (arg instanceof Error) return arg.stack ?? String(arg);
    if (typeof arg === "string") return arg;
    try {
      return JSON.stringify(arg);
    } catch {
      return String(arg);
    }
  };
  const originalError = console.error.bind(console);
  console.error = (...args: unknown[]) => {
    originalError(...args);
    forward(args.map(formatArg).join(" "));
  };
}

setupFrontendErrorLogging();

ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <React.StrictMode>
    <FontSizeProvider>
      <App />
    </FontSizeProvider>
  </React.StrictMode>,
);
