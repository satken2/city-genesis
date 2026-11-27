import assert from "node:assert/strict";
import React, { act } from "react";
import { createRoot } from "react-dom/client";
import i18next from "i18next";
import { I18nextProvider } from "react-i18next";
import { OpeningCaption } from "../../src/components/topmenu/OpeningCaption";
import { ConfirmDialogWindow } from "../../src/components/common/ConfirmDialogWindow";
import { installGraphicBridge, makeEnvelope } from "../../web/shell/ipc";
import * as host from "../../web/shell/host";
import { createStartupWindows } from "../../web/shell/windows";
import { loadSettings } from "../../web/shell/settings";
import { showMenu, startWithoutOpening } from "../../web/shell/opening";
import { definition } from "../../web/shell/version";
import jaWindow from "../../src/i18n/ja/window.json";
import jaCommon from "../../src/i18n/ja/common.json";

const mode = new URL(import.meta.url).search.slice(1);
const tick = () => new Promise(resolve => setTimeout(resolve, 10));
const storageKey = "city-genesis.settings";
if (mode === "legacy") localStorage.setItem(storageKey, JSON.stringify({ first_launch_tutorial_prompted: true }));
else if (mode !== "returning") localStorage.removeItem(storageKey);

const layer = document.createElement("div");
document.body.append(layer);
host.initHost(layer, { destroyed() {}, geometryChanged() {} });
createStartupWindows();
installGraphicBridge();
const bridge = (globalThis as any).__genesisGraphicBridge;
const stages: number[] = [];
bridge.attach((json: string) => {
  const message = JSON.parse(json);
  if (message.payload?.action === "opening:stage") stages.push(message.payload.params.stage);
});
async function fromUnity(event: string, payload: unknown) {
  bridge.receive(JSON.stringify(makeEnvelope("unity", "tauri", event, payload)));
  await act(tick);
}
const ready = () => fromUnity("system:graphic_ready", { ...definition(), core_compatible: true });
const acknowledge = (stage: number) => fromUnity("opening:shown", { stage });
const menu = () => host.isVisible("topmenu");
const caption = () => host.isVisible("opening-caption");
showMenu();
assert.ok(!menu() && !caption(), "スプラッシュ中はどちらの窓も隠す");

if (mode === "no-graphic") {
  startWithoutOpening();
  assert.ok(menu() && !caption() && !loadSettings().first_launch_presented);
} else {
  await ready();
  if (mode === "returning" || mode === "legacy") {
    assert.ok(menu() && !caption());
    assert.deepEqual(stages, []);
  } else {
    assert.ok(!menu() && !caption());
    assert.deepEqual(stages, [0]);
    await acknowledge(0);
    assert.ok(!menu() && caption() && loadSettings().first_launch_presented);

    const i18n = i18next.createInstance();
    await i18n.init({ lng: "ja", resources: { ja: { translation: { ...jaCommon, ...jaWindow } } } });
    const surface = document.createElement("div");
    document.body.append(surface);
    const root = createRoot(surface);
    await act(async () => { root.render(<I18nextProvider i18n={i18n}><OpeningCaption /></I18nextProvider>); });
    const click = async (selector: string) => { await act(async () => surface.querySelector<HTMLButtonElement>(selector)!.click()); };
    if (mode === "skip") {
      await click(".opening-skip");
      assert.deepEqual(stages, [0, -1]);
    } else {
      for (let stage = 0; stage < 3; stage++) {
        await click(".opening-next");
        assert.equal(stages.at(-1), stage, "一度目のクリックで字幕が全文表示される");
        assert.equal(surface.querySelector("p")?.textContent, Object.values(jaWindow.opening)[stage]);
        await click(".opening-next");
        const next = stage === 2 ? -1 : stage + 1;
        assert.equal(stages.at(-1), next);
        await click(".opening-next");
        assert.equal(stages.length, stage + 2, "重複クリックで場面を飛ばさない");
        await acknowledge(stage);
        assert.ok(!menu(), "前の場面からの遅延応答でメニューを出さない");
        if (next !== -1) await acknowledge(next);
      }
    }
    assert.ok(!menu());
    await acknowledge(-1);
    assert.ok(menu() && !caption());
    await act(async () => root.unmount());
    surface.remove();
  }
}
layer.remove();
// 4. ConfirmDialogWindow が cancelLabel を表示し、未指定時は既定のキャンセルを使う。
if (mode === "first") {
  const i18n = i18next.createInstance();
  await i18n.init({ lng: "ja", fallbackLng: "ja", resources: { ja: { translation: { ...jaCommon, ...jaWindow } } } });
  const host = document.createElement("div");
  document.body.append(host);
  const root = createRoot(host);
  await act(async () => {
    root.render(
      <I18nextProvider i18n={i18n}>
        <ConfirmDialogWindow
          request={{ requestId: "r1", message: "チュートリアルを開始しますか？", confirmLabel: "はい", cancelLabel: "いいえ" }}
          onClose={() => {}}
        />
      </I18nextProvider>,
    );
    await tick();
  });
  const buttons = [...host.querySelectorAll("button")].map((b) => b.textContent);
  assert.deepEqual(buttons, ["いいえ", "はい"]);
  await act(async () => root.unmount());
  host.remove();
}

console.log("PASS: opening " + mode);
