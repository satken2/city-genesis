/**
 * ブラウザ版のビルド設定。exe 用の vite.config.ts とは別に持ち、こちらにだけ Tauri の API の差し替えを置く。
 * `web/index.html` が Web シェル(ページ本体)、`web/app.html` が各ウィンドウの iframe の中身(exe と同じ `src/main.tsx`)。
 * どこに置いても動くよう、ページから読むファイルは相対パスにする(base)。
 * モード core はコアが配る開発用の版(`web/.env.core`)で、別の出力先に作る。開発用の地図ページ(`web/debug-map.html` と
 * そのウィンドウの中身 `web/debug-map-frame.html`)はこの版にだけ入れ、製品のビルドには含めない。
 */
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";

const tauriDir = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.join(tauriDir, "web");
const shimDir = path.join(webDir, "tauri-shim");
/** Unity の Web ビルドの出力先(`unity/Assets/Editor/WebBuild.cs`)。ページの `unity/` として配る。 */
const graphicBuildDir = path.resolve(tauriDir, "../unity/Builds/Web");

const CONTENT_TYPES: Record<string, string> = {
  ".js": "application/javascript",
  ".json": "application/json",
  ".wasm": "application/wasm",
};

/** 開発サーバーでは Unity の Web ビルドをその場から配り、ビルドでは出力へ写す。 */
function graphicBuild(): Plugin {
  return {
    name: "genesis-graphic-build",
    configureServer(server) {
      server.middlewares.use("/unity", (req, res, next) => {
        const relative = decodeURIComponent((req.url ?? "/").split("?")[0]);
        const file = path.join(graphicBuildDir, relative);
        if (!file.startsWith(graphicBuildDir)) {
          next();
          return;
        }
        if (!fs.existsSync(file) || !fs.statSync(file).isFile()) {
          res.statusCode = 404;
          res.end();
          return;
        }
        res.setHeader("Content-Type", CONTENT_TYPES[path.extname(file)] ?? "application/octet-stream");
        fs.createReadStream(file).pipe(res);
      });
    },
    writeBundle(options) {
      if (options.dir && fs.existsSync(graphicBuildDir)) {
        fs.cpSync(graphicBuildDir, path.join(options.dir, "unity"), { recursive: true });
      }
    },
  };
}

export default defineConfig(({ mode }) => ({
  root: webDir,
  base: "./",
  publicDir: path.join(tauriDir, "public"),
  plugins: [react(), graphicBuild()],
  resolve: {
    dedupe: ["react", "react-dom"],
    alias: [
      { find: /^@tauri-apps\/api\/core$/, replacement: path.join(shimDir, "core.ts") },
      { find: /^@tauri-apps\/api\/event$/, replacement: path.join(shimDir, "event.ts") },
      { find: /^@tauri-apps\/api\/window$/, replacement: path.join(shimDir, "window.ts") },
      { find: /^@tauri-apps\/plugin-dialog$/, replacement: path.join(shimDir, "plugin-dialog.ts") },
      { find: /^@tauri-apps\/plugin-opener$/, replacement: path.join(shimDir, "plugin-opener.ts") },
    ],
  },
  clearScreen: false,
  server: {
    port: 1420, // コアの CORS が開発サーバーに許可している番号。exe の `npm run tauri dev` と同じなので同時には動かせない
    strictPort: true,
    fs: { allow: [path.resolve(tauriDir, "..")] },
    watch: { ignored: ["**/src-tauri/target/**"] },
  },
  build: {
    outDir: path.join(tauriDir, mode === "core" ? "dist-web-core" : "dist-web"),
    emptyOutDir: true,
    rollupOptions: {
      input: {
        index: path.join(webDir, "index.html"),
        app: path.join(webDir, "app.html"),
        ...(mode === "core"
          ? { debugMap: path.join(webDir, "debug-map.html"), debugMapFrame: path.join(webDir, "debug-map-frame.html") }
          : {}),
      },
    },
  },
}));
