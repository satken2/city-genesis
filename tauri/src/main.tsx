import React from "react";
import ReactDOM from "react-dom/client";
import { invoke } from "@tauri-apps/api/core";
import "./i18n";
import App from "./App";
import { FontSizeProvider } from "./contexts/FontSizeContext";

/**
 * フロントエンドで発生した未キャッチ例外を Rust 側のログファイルに転送する。
 * Windows 本番ビルドではコンソールが表示されないため、ファイルログが重要となる。
 */
function setupFrontendErrorLogging() {
  window.addEventListener("error", (event) => {
    const msg = `${event.message} at ${event.filename}:${event.lineno}:${event.colno}`;
    invoke("log_frontend_error", { message: msg }).catch(() => {});
  });

  window.addEventListener("unhandledrejection", (event) => {
    const reason = event.reason instanceof Error ? event.reason.stack : String(event.reason);
    invoke("log_frontend_error", { message: `Unhandled rejection: ${reason}` }).catch(() => {});
  });
}

setupFrontendErrorLogging();

ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <React.StrictMode>
    <FontSizeProvider>
      <App />
    </FontSizeProvider>
  </React.StrictMode>,
);
