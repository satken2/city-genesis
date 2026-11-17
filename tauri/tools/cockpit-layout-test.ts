import assert from "node:assert/strict";
import { Window } from "happy-dom";

const browser = new Window({ width: 1280, height: 720 });
Object.assign(globalThis, { window: browser, document: browser.document });
// テストは単一のESMへまとめて実行するため、Viteが渡す環境値をバンドルのimport.metaに用意する。
Object.assign(import.meta, { env: {} });
const host = await import("../web/shell/host");
const { layoutHudWindows, resizeCockpit, resizeSystemTray } = await import("../web/shell/windows");
const layer = document.createElement("div");
document.body.appendChild(layer);
host.initHost(layer as unknown as HTMLElement, { destroyed() {}, geometryChanged() {} });
for (const label of ["cockpit", "system-tray"]) {
  host.createWindow(label, { url: "about:blank", placement: { x: 0, y: 0, width: 908, height: 76 }, visible: true });
}

for (const width of [740, 799, 960, 740]) {
  resizeCockpit(width, 76);
  assert.deepEqual(host.rect("cockpit"), { x: (1280 - width) / 2, y: 636, width, height: 76 }, "内容幅に合わせて中央へ配置する");
  resizeSystemTray(176, 40);
  layoutHudWindows(null, null);
  assert.equal(host.rect("cockpit")!.width, width, "他のHUDの更新や再配置で固定幅へ戻さない");
}
resizeCockpit(740, 98);
assert.deepEqual(host.rect("cockpit"), { x: 270, y: 614, width: 740, height: 98 }, "帯の分は下端を保ったまま上へ伸ばす");
layoutHudWindows(null, null);
assert.equal(host.rect("cockpit")!.height, 98, "他のHUDの再配置で帯の高さを戻さない");
resizeCockpit(2000, 76);
assert.equal(host.rect("cockpit")!.width, 1264, "画面の左右余白を確保する");
assert.equal(host.rect("cockpit")!.x, 8);

console.log("PASS: cockpit content size, recentering, upward growth, HUD relayout and monitor bounds");
