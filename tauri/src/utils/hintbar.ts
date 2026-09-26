/**
 * ヒントバーのテキストをプログラム全体から更新するためのユーティリティ
 *
 * Tauri のグローバルイベント `hintbar:set-text` を発行し、
 * ヒントバーウィンドウ上のコンポーネントがそのイベントを受信して表示を更新する。
 *
 * 使用例:
 *   import { setHintbarText } from "../utils/hintbar";
 *   setHintbarText(t("hint.placeRoad"));
 */

import { emit } from "@tauri-apps/api/event";

/**
 * ヒントバーに表示するテキストを設定する。
 * 空文字を渡すとヒントバーは非表示になる。
 */
export async function setHintbarText(text: string): Promise<void> {
  await emit("hintbar:set-text", { text });
}

/**
 * ヒントバーが expected を表示しているときだけ消す。
 * 自分が出した案内を後から片付けるときに使う(その間に別のパネルが出した案内を消さないため)。
 */
export async function clearHintbarTextIf(expected: string): Promise<void> {
  await emit("hintbar:set-text", { text: "", only_if: expected });
}
