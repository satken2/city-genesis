/**
 * チュートリアルウィンドウの表示・非表示をプログラム全体から制御するユーティリティ
 *
 * Tauri のグローバルイベントを通じてチュートリアルウィンドウの表示状態を変更する。
 * Rust 側のウィンドウ制御（show / hide）と連携させることで、
 * セッション開始時などに Rust からチュートリアルを表示させることができる。
 */

import { emit } from "@tauri-apps/api/event";

/** チュートリアルウィンドウを表示するイベントを発行する */
export async function showTutorial(): Promise<void> {
  await emit("tutorial:show", {});
}

/** チュートリアルウィンドウを非表示にするイベントを発行する */
export async function hideTutorial(): Promise<void> {
  await emit("tutorial:hide", {});
}
