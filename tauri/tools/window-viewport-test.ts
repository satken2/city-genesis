import assert from "node:assert/strict";
import { Window } from "happy-dom";
import * as host from "../web/shell/host";

const browser = new Window({ width: 640, height: 480 });
Object.assign(globalThis, { window: browser, document: browser.document });
host.initHost(document.body, { destroyed() {}, geometryChanged() {} });

const label = "popup-settings";
host.createWindow(label, {
  url: "about:blank",
  placement: { x: -80, y: -210, width: 800, height: 900 },
  visible: true,
});
assert.deepEqual(host.rect(label), { x: 0, y: 0, width: 640, height: 480 });

browser.happyDOM.setViewport({ width: 1280, height: 1000 });
host.refitPopups();
assert.deepEqual(host.rect(label), { x: 0, y: 0, width: 800, height: 900 });

host.place(label, { x: 320, y: 50, width: 800, height: 900 });
browser.happyDOM.setViewport({ width: 700, height: 600 });
host.refitPopups();
assert.deepEqual(host.rect(label), { x: 0, y: 0, width: 700, height: 600 });

await browser.happyDOM.close();
console.log("PASS: tall popups stay inside the browser viewport on open and resize");
