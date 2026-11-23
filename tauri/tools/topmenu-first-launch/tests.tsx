import assert from "node:assert/strict";
import React, { act } from "react";
import { createRoot } from "react-dom/client";
import i18next from "i18next";
import { I18nextProvider } from "react-i18next";
import { TopMenu } from "../../src/components/topmenu/TopMenu";
import { ConfirmDialogWindow } from "../../src/components/common/ConfirmDialogWindow";
import { PLAY_MODE_TUTORIAL } from "../../src/constants";
import { configureBackendRequests, setBackendConnected } from "../../src/utils/api";
import { loadSettings } from "../../web/shell/settings";
import { emit, resetRuntime, state } from "./runtime";
import jaWindow from "../../src/i18n/ja/window.json";
import enWindow from "../../src/i18n/en/window.json";
import jaCommon from "../../src/i18n/ja/common.json";
import enCommon from "../../src/i18n/en/common.json";

const API_BASE = "http://127.0.0.1:18000";
const STORAGE_KEY = "city-genesis.settings";
const tick = () => new Promise((resolve) => setTimeout(resolve, 10));

let mockSaves: Record<string, unknown> = {};
let mockProfile = { tutorials_cleared: [] as string[], scenarios_cleared: [] as string[], achievements: [] as string[] };
let fetchPaths: string[] = [];

(globalThis as unknown as { __mockFetch?: typeof fetch }).__mockFetch = (async (input: string | URL | Request) => {
  const url = new URL(typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url);
  fetchPaths.push(url.pathname);
  if (url.pathname === "/api/v1/saves") {
    return new Response(JSON.stringify({ success: true, saves: mockSaves }), { status: 200, headers: { "Content-Type": "application/json" } });
  }
  if (url.pathname === "/api/v1/profile") {
    return new Response(JSON.stringify(mockProfile), { status: 200, headers: { "Content-Type": "application/json" } });
  }
  return new Response("not found", { status: 404 });
}) as typeof fetch;

configureBackendRequests(API_BASE);
setBackendConnected(true);

async function mountTopMenu(initialLng = "ja") {
  const i18n = i18next.createInstance();
  await i18n.init({
    lng: initialLng,
    fallbackLng: "ja",
    resources: {
      ja: { translation: { ...jaCommon, ...jaWindow } },
      en: { translation: { ...enCommon, ...enWindow } },
    },
  });
  const host = document.createElement("div");
  document.body.append(host);
  const root = createRoot(host);
  await act(async () => {
    root.render(
      <I18nextProvider i18n={i18n}>
        <TopMenu apiBaseUrl={API_BASE} />
      </I18nextProvider>,
    );
    await tick();
  });
  return {
    host,
    i18n,
    async showSession() {
      await act(async () => {
        await emit("session-state", {
          current_save_id: null,
          session: null,
          isLoading: false,
          coreVersion: "1.0.0",
          backendConnected: true,
        });
        await tick();
      });
      await act(tick);
      await act(tick);
    },
    async close() {
      await act(async () => root.unmount());
      host.remove();
    },
  };
}

// 1. 完全初回起動で「はい」を選ぶと、基本操作編("controls")がロードされ案内済みが記録される。
resetRuntime();
localStorage.removeItem(STORAGE_KEY);
mockSaves = {};
mockProfile = { tutorials_cleared: [], scenarios_cleared: [], achievements: [] };
fetchPaths = [];
state.confirmAnswer = true;
{
  // OS/ブラウザ言語に依らず設定言語(ja)で文言が解決されることも確認する。
  localStorage.setItem(STORAGE_KEY, JSON.stringify({ language: "ja", first_launch_tutorial_prompted: false }));
  const view = await mountTopMenu("en");
  await view.showSession();

  const confirmCall = state.invocations.find((c) => c.command === "open_registered_window" && c.args?.windowType === "confirm_dialog");
  assert.ok(confirmCall, "初回起動で確認ポップアップが開く");
  const req = (confirmCall.args?.params as { extra: { confirmRequest: Record<string, unknown> } }).extra.confirmRequest;
  assert.equal(req.message, "チュートリアルを開始しますか？");
  assert.equal(req.confirmLabel, "はい");
  assert.equal(req.cancelLabel, "いいえ");
  assert.equal(loadSettings().first_launch_tutorial_prompted, true, "案内済みフラグが保存される");

  const createCall = state.invocations.find((c) => c.command === "create_session");
  assert.ok(createCall, "はい選択後に create_session が呼ばれる");
  assert.equal(createCall.args?.playMode, PLAY_MODE_TUTORIAL);
  assert.equal(createCall.args?.tutorialId, "controls");
  assert.equal(createCall.args?.scenarioId, null);
  assert.equal(createCall.args?.language, "ja");
  await view.close();
}

// 2. 完全初回起動で「いいえ」を選ぶとロードせず、次回以降は再表示しない。
resetRuntime();
localStorage.setItem(STORAGE_KEY, JSON.stringify({ language: "en", first_launch_tutorial_prompted: false }));
mockSaves = {};
mockProfile = { tutorials_cleared: [], scenarios_cleared: [], achievements: [] };
fetchPaths = [];
state.confirmAnswer = false;
{
  const view = await mountTopMenu("ja");
  await view.showSession();

  const confirmCalls = state.invocations.filter((c) => c.command === "open_registered_window" && c.args?.windowType === "confirm_dialog");
  assert.equal(confirmCalls.length, 1);
  const req = (confirmCalls[0].args?.params as { extra: { confirmRequest: Record<string, unknown> } }).extra.confirmRequest;
  assert.equal(req.message, "Would you like to start the tutorial?");
  assert.equal(req.confirmLabel, "Yes");
  assert.equal(req.cancelLabel, "No");
  assert.equal(state.invocations.some((c) => c.command === "create_session"), false);
  assert.equal(loadSettings().first_launch_tutorial_prompted, true);

  // 同じ画面に session-state が再送されても再表示しない。
  await view.showSession();
  assert.equal(state.invocations.filter((c) => c.command === "open_registered_window").length, 1);
  await view.close();

  // 再起動(再マウント)しても API 取得やポップアップ表示を行わない。
  resetRuntime();
  fetchPaths = [];
  const secondLaunch = await mountTopMenu("en");
  await secondLaunch.showSession();
  assert.equal(fetchPaths.length, 0);
  assert.equal(state.invocations.some((c) => c.command === "open_registered_window"), false);
  await secondLaunch.close();
}

// 3. 既にセーブデータやクリア記録がある既存プレイヤーには出さず、案内済みだけ保存する。
resetRuntime();
localStorage.setItem(STORAGE_KEY, JSON.stringify({ language: "ja", first_launch_tutorial_prompted: false }));
mockSaves = { save1: { id: "save1" } };
mockProfile = { tutorials_cleared: [], scenarios_cleared: [], achievements: [] };
fetchPaths = [];
state.confirmAnswer = true;
{
  const view = await mountTopMenu("ja");
  await view.showSession();
  assert.equal(state.invocations.some((c) => c.command === "open_registered_window"), false);
  assert.equal(state.invocations.some((c) => c.command === "create_session"), false);
  assert.equal(loadSettings().first_launch_tutorial_prompted, true);
  await view.close();
}

// 4. ConfirmDialogWindow が cancelLabel を表示し、未指定時は既定のキャンセルを使う。
{
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

console.log("PASS: TopMenu first-launch tutorial prompt and ConfirmDialog cancelLabel");
