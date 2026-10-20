import React from "react";
import ReactDOM from "react-dom/client";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import "./i18n";
import App from "./App";
import { FontSizeProvider } from "./contexts/FontSizeContext";

/**
 * Forward frontend errors to Rust's log file. File logging is essential because Windows production builds have
 * no console. Hook console.error in addition to uncaught exceptions and unhandledrejection: many handlers only
 * catch and console.error failures (such as connection errors), leaving no production record and appearing to
 * fail silently. Prefix window labels to identify the source.
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

  // Use stack for Error, JSON for objects, and combine into one line.
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
