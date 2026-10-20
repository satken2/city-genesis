/**
 * ブラウザ版のページ本体(Web シェル)の PC 向けの入口。
 * 全面に Unity の canvas を置いて、その上に UI の各ウィンドウを iframe として重ねる。
 */
import "./shell.css";
import { bootShell } from "./boot";
import { startGraphic } from "./process";
import { createStartupWindows } from "./windows";

bootShell({
  createWindows: () => createStartupWindows(),
  startGraphic: () => startGraphic(document.getElementById("graphic") as HTMLCanvasElement),
});
