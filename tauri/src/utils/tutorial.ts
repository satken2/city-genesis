/**
 * チュートリアルウィンドウの表示・非表示をプログラム全体から制御するユーティリティ
 *
 * Tauri のグローバルイベントを通じてチュートリアルウィンドウの表示状態を変更する。
 * Rust 側のウィンドウ制御（show / hide）と連携させることで、
 * セッション開始時などに Rust からチュートリアルを表示させることができる。
 *
 * また、チュートリアル完了状態を localStorage で管理し、
 * ニューゲームウィンドウでのシナリオ選択ロック解除判定に使用する。
 */

import { emit } from "@tauri-apps/api/event";

/** localStorage にチュートリアル完了フラグを保存するキー */
const TUTORIAL_CLEARED_KEY = "tutorial_cleared";

/** チュートリアルウィンドウを表示するイベントを発行する */
export async function showTutorial(): Promise<void> {
  await emit("tutorial:show", {});
}

/** チュートリアルウィンドウを非表示にするイベントを発行する */
export async function hideTutorial(): Promise<void> {
  await emit("tutorial:hide", {});
}

/**
 * チュートリアルが完了しているかどうかを返す。
 * ニューゲームウィンドウでシナリオ選択のロック解除判定に使用する。
 */
export function isTutorialCleared(): boolean {
  return localStorage.getItem(TUTORIAL_CLEARED_KEY) === "true";
}