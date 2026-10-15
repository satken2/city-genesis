/** ブラウザ版の選択メニュー。候補を親ページへ渡し、呼び出し元の iframe の大きさに制限されず開く。 */
import type { CheckMenuItemOptions } from "@tauri-apps/api/menu";
import { closeMenu, popupMenu } from "./bridge";
import type { LogicalPosition } from "./window";

let nextMenuId = 0;

/** 使用する Tauri Menu API のうち、1階層のチェック付き項目と表示・解放に対応する。 */
export class Menu {
  private readonly id = `menu-${++nextMenuId}`;

  private constructor(private readonly items: CheckMenuItemOptions[]) {}

  static async new(options: { items: CheckMenuItemOptions[] }): Promise<Menu> {
    return new Menu(options.items);
  }

  async popup(at: LogicalPosition): Promise<void> {
    const focused = document.activeElement as HTMLElement | null;
    await popupMenu({
      id: this.id,
      items: this.items.map(({ id, text, checked, enabled }, index) => ({ id: id ?? String(index), text, checked, enabled })),
      at: { x: at.x, y: at.y },
      fontSize: parseFloat(getComputedStyle(document.documentElement).fontSize),
    }, id => {
      const index = this.items.findIndex((item, index) => (item.id ?? String(index)) === id);
      this.items[index]?.action?.(id);
    }, () => { focused?.focus(); });
  }

  async close(): Promise<void> {
    closeMenu(this.id);
  }
}
