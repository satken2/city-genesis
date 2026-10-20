/**
 * 開発用の地図ページ(Web シェルの別の入口)。Unity の代わりに都市の2D地図を全面に置き、ウィンドウはスマホの画面に合わせた
 * 定位置(`layout.ts`)へ置く。それ以外のアプリの制御は PC 向けページと同じ Web シェルが行う。
 * コアが配る開発用ビルド(`npm run build:web:core`)にだけ入り、製品のビルドには含めない。
 */
import "../shell/shell.css";
import "./debug-map.css";
import { bootShell } from "../shell/boot";
import * as host from "../shell/host";
import { createStartupWindows } from "../shell/windows";
import { arrangeForDebugMap, DEBUG_HUD } from "./layout";
import { startMapGraphic } from "./mapGraphic";

host.setArrangement(arrangeForDebugMap);
bootShell({
  createWindows: () => createStartupWindows(DEBUG_HUD, false),
  startGraphic: () => startMapGraphic(document.getElementById("graphic") as HTMLElement),
});
window.addEventListener("resize", host.rearrange);
