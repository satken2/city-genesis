import assert from "node:assert/strict";
import { Window } from "happy-dom";
import type { PopupMenuRequest } from "../web/shared/shellApi";

const browser = new Window({ width: 800, height: 600 });
Object.assign(globalThis, { window: browser, document: browser.document, getComputedStyle: browser.getComputedStyle.bind(browser) });
const { showPopupMenu, closePopupMenu } = await import("../web/shell/popupMenu");
const { hide, destroy, initHost, createWindow } = await import("../web/shell/host");
const layer = browser.document.createElement("div");
browser.document.body.appendChild(layer);
initHost(layer as unknown as HTMLElement, { destroyed() {}, geometryChanged() {} });
createWindow("cockpit", { url: "about:blank", placement: { x: 0, y: 524, width: 800, height: 76 }, visible: true });

const selected: string[] = [];
let dismissed = 0;
const request: PopupMenuRequest = {
  id: "weather",
  at: { x: 790, y: 580 },
  fontSize: 16,
  items: ["auto", "clear", "high_clouds", "fair", "broken_clouds", "overcast", "rain_clouds", "fog", "drizzle", "rain", "heavy_rain", "light_snow", "snow", "blizzard"]
    .map(id => ({ id, text: id, checked: id === "clear" })),
};
const buttons = () => Array.from(browser.document.querySelectorAll("dialog button"));
const open = () => showPopupMenu("cockpit", request, id => selected.push(id), () => dismissed++);

open();
assert.equal(buttons().length, 14, "全候補を親ページへ表示する");
assert.equal(browser.document.querySelector("iframe")!.contentDocument!.querySelector("dialog"), null, "候補を高さ76pxのiframeへ閉じ込めない");
assert.equal(browser.document.activeElement?.textContent, "clear", "選択中の天気へフォーカスする");
browser.document.activeElement!.dispatchEvent(new browser.KeyboardEvent("keydown", { key: "End", bubbles: true }));
assert.equal(browser.document.activeElement?.textContent, "blizzard", "末尾の候補へキーボードで移動できる");
(browser.document.activeElement as unknown as HTMLButtonElement).click();
assert.deepEqual(selected, ["blizzard"]);
assert.equal(browser.document.querySelector("dialog"), null);
assert.equal(dismissed, 1, "選択後に呼び出し元へ終了を知らせる");

open();
buttons()[0].click();
assert.deepEqual(selected, ["blizzard", "auto"], "自動へ戻せる");
open();
browser.document.querySelector("dialog")!.dispatchEvent(new browser.Event("cancel", { cancelable: true }));
assert.equal(selected.length, 2, "取消で天気を変更しない");
assert.equal(browser.document.querySelector("dialog"), null);

open();
closePopupMenu("cockpit", "previous-menu");
assert.ok(browser.document.querySelector("dialog"), "古いメニューの解放で現在のメニューを閉じない");
hide("cockpit");
assert.equal(browser.document.querySelector("dialog"), null, "呼び出し元が隠れたら候補を残さない");
open();
destroy("cockpit");
assert.equal(browser.document.querySelector("dialog"), null);

// APIの差し替えも、項目と位置をJSONで渡し、選択時だけ元のコールバックを呼ぶ。
let payload: PopupMenuRequest | undefined;
let choose: ((id: string) => void) | undefined;
const closed: string[] = [];
Object.assign(browser, { __genesisShell: {
  popupMenu: async (_label: string, json: string, action: (id: string) => void) => {
    payload = JSON.parse(json) as PopupMenuRequest;
    choose = action;
    return JSON.stringify({ ok: true, value: null });
  },
  closeMenu: (_label: string, id: string) => closed.push(id),
} });
browser.document.documentElement.style.fontSize = "16px";
const { Menu } = await import("../web/tauri-shim/menu");
const { LogicalPosition } = await import("../web/tauri-shim/window");
const menu = await Menu.new({ items: request.items.map(item => ({ ...item, action: (id: string) => selected.push(id) })) });
await menu.popup(new LogicalPosition(12, 60));
assert.deepEqual(payload!.at, { x: 12, y: 60 });
assert.equal(payload!.fontSize, 16);
assert.equal(payload!.items.length, 14);
choose!("blizzard");
assert.equal(selected.at(-1), "blizzard");
await menu.close();
assert.equal(closed[0], payload!.id);

await browser.happyDOM.close();
console.log("PASS popup menus: all weather choices, iframe boundary, keyboard, automatic mode, cancellation and cleanup");
