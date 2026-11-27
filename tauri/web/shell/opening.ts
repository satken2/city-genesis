import { emitTo } from "./events";
import * as host from "./host";
import { sendUiAction } from "./ipc";
import { loadSettings, saveSettings } from "./settings";
import { state } from "./state";

// 数値は Unity に要求した場面。-1 は演出の終了応答待ち。
let phase: "splash" | "menu" | number = "splash";
export const getOpeningStage = () => typeof phase === "number" ? phase : null;

export function showMenu(): void {
  if (phase === "menu" && !state.isLoading && state.lastSessionId === null) host.show("topmenu");
}

export function hideOpeningForCity(): void {
  if (typeof phase === "number") phase = "menu";
  host.hide("opening-caption");
  host.hide("topmenu");
}

export function openingGraphicReady(): void {
  if (phase === "splash") {
    phase = state.isLoading || state.lastSessionId !== null || loadSettings().first_launch_presented ? "menu" : 0;
  }
  if (typeof phase === "number") sendUiAction("opening", null, "opening:stage", { stage: phase });
  else showMenu();
}

/** Unity を使わない地図ページや、グラフィックを読み込めない構成に起動待ちを残さない。 */
export function startWithoutOpening(): void {
  if (phase !== "splash") return;
  phase = "menu";
  showMenu();
}

export function advanceOpening(stage: number, skip: boolean): void {
  if (phase !== stage || stage < 0) return;
  const next = skip || stage === 2 ? -1 : stage + 1;
  sendUiAction("opening", null, "opening:stage", { stage: next });
  phase = next;
}

export function openingShown(stage: unknown): void {
  if (phase !== stage || typeof phase !== "number") return;
  if (phase === -1) {
    phase = "menu";
    host.hide("opening-caption");
    showMenu();
    return;
  }
  if (phase === 0) {
    const settings = loadSettings();
    if (!settings.first_launch_presented) {
      try { saveSettings({ ...settings, first_launch_presented: true }); }
      catch (error) { console.error("初回案内を保存できません", error); }
    }
  }
  emitTo("opening-caption", "opening-stage", stage);
  host.show("opening-caption");
}
