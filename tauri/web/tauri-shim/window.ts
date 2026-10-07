/** ブラウザ版の `@tauri-apps/api/window`。ウィンドウはページ内の iframe で、操作は Web シェルが行う。 */
import type { EventCallback, UnlistenFn } from "@tauri-apps/api/event";
import { currentLabel, startWindowDrag, subscribe, windowOp } from "./bridge";

export class LogicalSize {
  readonly type = "Logical";

  constructor(
    readonly width: number,
    readonly height: number,
  ) {}
}

class WebWindow {
  readonly label = currentLabel;

  /** 全体向けのイベントと、このウィンドウ宛てのイベントを受ける。 */
  async listen<T>(event: string, handler: EventCallback<T>): Promise<UnlistenFn> {
    return subscribe<T>(event, { kind: "Window", label: this.label }, handler);
  }

  async show(): Promise<void> {
    await windowOp("show");
  }

  async hide(): Promise<void> {
    await windowOp("hide");
  }

  async isVisible(): Promise<boolean> {
    return (await windowOp("isVisible")) === true;
  }

  async setSize(size: LogicalSize): Promise<void> {
    await windowOp("setSize", { width: size.width, height: size.height });
  }

  async minimize(): Promise<void> {
    await windowOp("minimize");
  }

  async startDragging(): Promise<void> {
    startWindowDrag();
  }

  /** iframe の window のフォーカスは、OS のウィンドウのフォーカスと同じく、中を押したときに得てほかを押したときに失う。 */
  async onFocusChanged(handler: EventCallback<boolean>): Promise<UnlistenFn> {
    let id = 0;
    const focus = () => handler({ event: "tauri://focus", id: ++id, payload: true });
    const blur = () => handler({ event: "tauri://blur", id: ++id, payload: false });
    window.addEventListener("focus", focus);
    window.addEventListener("blur", blur);
    return () => {
      window.removeEventListener("focus", focus);
      window.removeEventListener("blur", blur);
    };
  }
}

let current: WebWindow | undefined;

export function getCurrentWindow(): WebWindow {
  current ??= new WebWindow();
  return current;
}
