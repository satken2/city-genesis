/**
 * Control tutorial window visibility from anywhere in the program through global Tauri events. Cooperates with
 * Rust show/hide control so Rust can show tutorials at session start, etc.
 */

import { emit } from "@tauri-apps/api/event";

/** Emit the event to show the tutorial window. */
export async function showTutorial(): Promise<void> {
  await emit("tutorial:show", {});
}

/** Emit the event to hide the tutorial window. */
export async function hideTutorial(): Promise<void> {
  await emit("tutorial:hide", {});
}
