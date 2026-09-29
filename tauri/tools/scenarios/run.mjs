import { build } from "esbuild";
import { mkdir } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const output = resolve("node_modules/.cache/scenario-tests.mjs");
const mock = resolve("tools/scenarios/mock-runtime.ts");
await mkdir(resolve("node_modules/.cache"), { recursive: true });
await build({ entryPoints: ["tools/scenarios/tests.tsx"], outfile: output, bundle: true,
  platform: "node", format: "esm", packages: "external", loader: { ".css": "empty", ".png": "empty" },
  alias: { "@tauri-apps/api/core": mock, "@tauri-apps/api/event": mock, "react-i18next": mock } });
// React はモジュールの読み込み時に DOM の有無を調べ、無ければテキスト入力の input イベントを使わない方式になる。
// バンドルの外部パッケージ(react-dom)はテスト本体より先に評価されるので、DOM はここで先に用意する。
const { Window } = await import("happy-dom");
const window = new Window();
Object.assign(globalThis, { window, document: window.document, HTMLElement: window.HTMLElement, IS_REACT_ACT_ENVIRONMENT: true });
await import(pathToFileURL(output).href);
