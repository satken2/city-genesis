/**
 * Web シェル(ページ本体)が各ウィンドウの iframe へ公開する窓口の取り決め。
 *
 * 境界を越えるのは文字列(JSON)だけにする。Tauri の IPC が値を JSON で往復させるのと同じ結果になり、
 * 別の iframe(別の JavaScript の世界)で作られたオブジェクトを相手に持ち込まない。
 */

/** シェルが自分の window に置く窓口の名前。iframe は親の window から引く。 */
export const SHELL_GLOBAL = "__genesisShell";

/** iframe の要素に付ける、そのウィンドウのラベルの属性。 */
export const WINDOW_LABEL_ATTRIBUTE = "data-window-label";

/** Tauri v2 のイベントの宛先。Any は全体向けとすべてのウィンドウ宛てを受け、Window は全体向けと自分宛てを受ける。 */
export type ListenTarget = { kind: "Any" } | { kind: "Window"; label: string };

/** コマンドの結果。Tauri のコマンドが Err を返したときは、その値で invoke が失敗する。 */
export type InvokeReply = { ok: true; value: unknown } | { ok: false; error: unknown };

/** iframe 内の論理座標を起点に開く、1階層の選択メニュー。 */
export interface PopupMenuRequest {
  id: string;
  items: { id: string; text: string; checked?: boolean; enabled?: boolean }[];
  at: { x: number; y: number };
  fontSize: number;
}

export interface ShellApi {
  /** Tauri のコマンドを実行する。args と戻り値は JSON(戻り値は InvokeReply)。 */
  invoke(label: string, command: string, argsJson: string): Promise<string>;
  /** イベントを購読する。handler には Tauri と同じ形の {event, id, payload} を JSON で渡す。 */
  listen(label: string, event: string, targetJson: string, handler: (eventJson: string) => void): number;
  unlisten(id: number): void;
  /** 全体向けのイベントを送る。 */
  emit(event: string, payloadJson: string): void;
  /** 呼び出したウィンドウ自身の操作(表示・移動など)。引数と戻り値は JSON。 */
  windowOp(label: string, op: WindowOp, argsJson: string): Promise<string>;
  popupMenu(label: string, requestJson: string, onSelect: (id: string) => void, onClose: () => void): Promise<string>;
  closeMenu(label: string, id: string): void;
}

export type WindowOp =
  | "show"
  | "hide"
  | "isVisible"
  | "setSize"
  | "minimize"
  | "moveBy"
  | "moveEnd"
  | "activate";
