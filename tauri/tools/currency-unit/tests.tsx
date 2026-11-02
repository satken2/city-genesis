import assert from "node:assert/strict";
import React, { act } from "react";
import { createRoot } from "react-dom/client";
import i18next from "i18next";
import { I18nextProvider } from "react-i18next";
import { SettingsWindow } from "../../src/components/taskbar/SettingsWindow";
import { Money } from "../../src/components/common/Money";
import { useAppDisplaySettings } from "../../src/hooks/useAppDisplaySettings";
import { useCurrencyUnit } from "../../src/hooks/useCurrencyUnit";
import { formatExactAmount, getCurrencyUnit, setCurrencyUnit } from "../../src/utils/money";
import { loadSettings } from "../../web/shell/settings";
import { state, emit } from "./runtime";
import ja from "../../src/i18n/ja/settings.json";
import en from "../../src/i18n/en/settings.json";
import fr from "../../src/i18n/fr/settings.json";

const storageKey = "city-genesis.settings";
const tick = () => new Promise((resolve) => setTimeout(resolve, 0));
for (const [language, unit] of Object.entries({ ja: "円", en: "$", "es-419": "$", "zh-Hans": "元", "zh-Hant": "元", fr: "€", ko: "₩" })) {
  assert.equal(getCurrencyUnit(language), unit);
}
assert.equal(getCurrencyUnit("fr-CA"), "€");
setCurrencyUnit("コイン");
assert.equal(formatExactAmount(75000, "en-US", true), "75,000コイン");
assert.equal(formatExactAmount(75000, "en-US"), "75,000");
setCurrencyUnit(null);

function UnitLabel() {
  return <span data-unit>{useCurrencyUnit()}</span>;
}
function Display() {
  useAppDisplaySettings();
  return <><input data-draft defaultValue="編集中" /><Money value={75000} unit /><UnitLabel /></>;
}
async function mount(settings = true) {
  const i18n = i18next.createInstance();
  await i18n.init({ lng: "ja", fallbackLng: "ja", resources: {
    ja: { translation: ja }, en: { translation: en }, fr: { translation: fr },
  } });
  const host = document.createElement("div");
  document.body.append(host);
  const root = createRoot(host);
  await act(async () => {
    root.render(<I18nextProvider i18n={i18n}><Display />{settings && <SettingsWindow apiBaseUrl="" />}</I18nextProvider>);
    await tick();
  });
  await act(tick);
  return { host, i18n, async close() { await act(async () => root.unmount()); host.remove(); } };
}
async function edit(host: HTMLElement, value: string) {
  const input = host.querySelector<HTMLInputElement>(".settings-currency-unit")!;
  const setValue = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, "value")!.set!;
  await act(async () => {
    setValue.call(input, value);
    input.dispatchEvent(new window.Event("input", { bubbles: true }));
  });
  await act(async () => {
    input.dispatchEvent(new window.FocusEvent("focusout", { bubbles: true }));
    await tick();
  });
}

// 以前の設定にも、既存の初期言語判定が返した言語にも、未指定の単位をそのまま追従させる。
localStorage.setItem(storageKey, JSON.stringify({ language: "en" }));
assert.equal(loadSettings().currency_unit, null);
const view = await mount();
assert.equal(view.i18n.language, "en");
assert.equal(view.host.querySelector("[data-unit]")!.textContent, "$");
assert.equal(view.host.querySelector<HTMLInputElement>(".settings-currency-unit")!.placeholder, "$");
const draft = view.host.querySelector("[data-draft]");
await edit(view.host, " G ");
assert.equal(loadSettings().currency_unit, "G");
assert.equal(view.host.querySelector("[data-unit]")!.textContent, "G");
assert.equal(view.host.querySelector(".money")!.getAttribute("title"), "75,000G");
assert.equal(view.host.querySelector("[data-draft]"), draft, "編集中の画面を作り直さない");

await act(async () => {
  const select = view.host.querySelector<HTMLSelectElement>(".settings-select")!;
  select.value = "fr";
  select.dispatchEvent(new window.Event("change", { bubbles: true }));
  await tick();
});
assert.equal(view.i18n.language, "fr");
assert.equal(loadSettings().currency_unit, "G", "言語の変更で任意単位を上書きしない");
await edit(view.host, " ");
assert.equal(loadSettings().currency_unit, null);
assert.equal(view.host.querySelector("[data-unit]")!.textContent, "€");
await edit(view.host, "Cr");
await view.close();

// 後から開くウィンドウも、保存された単位を復元する。
setCurrencyUnit(null);
const reopened = await mount(false);
assert.equal(reopened.i18n.language, "fr");
assert.equal(reopened.host.querySelector("[data-unit]")!.textContent, "Cr");
await reopened.close();

// 起動時の読み込みより先に別ウィンドウの設定通知が届いても、古い値へ戻さない。
let finishLoad!: (settings: unknown) => void;
state.nextLoad = new Promise((resolve) => { finishLoad = resolve; });
const racing = await mount(false);
assert.equal(state.nextLoad, null, "起動時の読み込みを待機させる");
await act(async () => {
  await emit("settings-updated", { ...loadSettings(), language: "en", currency_unit: "¤" });
  finishLoad({ ...loadSettings(), language: "ja", currency_unit: null });
  await tick();
});
assert.equal(racing.i18n.language, "en");
assert.equal(racing.host.querySelector("[data-unit]")!.textContent, "¤");
await racing.close();
setCurrencyUnit(null);
console.log("PASS: language defaults, custom units, unchanged amounts, settings edits, persistence, live updates, and initialization races");
