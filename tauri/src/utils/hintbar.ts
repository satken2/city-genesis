/**
 * Update hint bar text from anywhere in the program. Emit Tauri's global `hintbar:set-text`; the hint bar window
 * receives it and updates display.
 *
 * Usage:
 *   import { setHintbarText } from "../utils/hintbar";
 *   setHintbarText(t("hint.placeRoad"));
 */

import { emit } from "@tauri-apps/api/event";

/**
 * Set hint bar text. An empty string hides the hint bar.
 */
export async function setHintbarText(text: string): Promise<void> {
  await emit("hintbar:set-text", { text });
}

/**
 * Clear only if the hint bar displays expected. Use when cleaning up your own guidance to preserve text set
 * meanwhile by another panel.
 */
export async function clearHintbarTextIf(expected: string): Promise<void> {
  await emit("hintbar:set-text", { text: "", only_if: expected });
}
