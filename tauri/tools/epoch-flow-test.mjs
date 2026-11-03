import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import ts from "typescript";
import vm from "node:vm";
import { createRequire } from "node:module";
import { renderToStaticMarkup } from "react-dom/server";

const flush = () => new Promise((resolve) => setImmediate(resolve));

// 初期ロードの準備完了は手動開始を保つ。
{
  const require = createRequire(import.meta.url);
  const tipsSource = await readFile(new URL("../src/components/tips/TipsWindow.tsx", import.meta.url), "utf8");
  const code = ts.transpileModule(tipsSource, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX },
  }).outputText;
  let stateIndex = 0, queries = 0, starts = 0;
  const effects = [], exports = {};
  vm.runInNewContext(code, {
    exports, console, setTimeout: () => 1, clearTimeout: () => {}, setInterval: () => 1, clearInterval: () => {},
    require: (name) => {
      // TipsWindow の3番目の useState(ready)だけを true にして、準備完了の画面を描く。
      if (name === "react") return {
        useState: (initial) => [stateIndex++ === 2 ? true : typeof initial === "function" ? initial() : initial, () => {}],
        useEffect: (effect) => effects.push(effect),
        useCallback: (callback) => callback,
      };
      if (name === "react/jsx-runtime") return require(name);
      if (name === "react-i18next") return { useTranslation: () => ({ t: (key) => key }) };
      if (name.endsWith("/ipc")) return {
        getInitialLoadingReady: async () => { queries++; return true; },
        startGameFromTips: async () => { starts++; },
      };
      if (name.endsWith("/hudCommon")) return { iconUrl: () => "test-image" };
      return { default: "test-image" };
    },
  });
  const html = renderToStaticMarkup(exports.TipsWindow());
  const cleanup = effects.map((effect) => effect());
  await flush();
  assert.equal(html.includes("tips.startGame"), true);
  assert.equal(queries, 1);
  assert.equal(starts, 0);
  cleanup.forEach((fn) => fn?.());
}

// 計算前の同期と計算後の日没を別々に待ち、実行IDの照合・通信失敗時の解放を両方で検査する。
{
  const source = await readFile(new URL("../src/utils/epochPresentation.ts", import.meta.url), "utf8");
  const code = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
  }).outputText;
  for (const stage of ["prepare", "sunset"]) for (const scenario of ["connected", "disconnected", "timeout", "send-error"]) {
    let listener, eventName, timeout, unsubscribed = 0, completed = false;
    const command = stage === "prepare" ? "begin_epoch_presentation" : "begin_epoch_sunset";
    const exports = {};
    vm.runInNewContext(code, {
      exports, console, crypto: { randomUUID: () => "epoch-id" },
      setTimeout: (fn) => { timeout = fn; return 1; }, clearTimeout: () => {},
      require: (name) => name.endsWith("/core") ? { invoke: async (requested) => {
        assert.equal(eventName, requested === "begin_epoch_sunset" ? "epoch:darkened" : "epoch:ready");
        if (requested !== command) return true;
        if (scenario === "send-error") throw new Error("IPC unavailable");
        return scenario !== "disconnected";
      } } : name.endsWith("/event") ? { listen: async (event, fn) => {
        eventName = event; listener = fn; return () => { unsubscribed++; };
      } } : { logInfoToFile: () => {} },
    });
    let presentation;
    if (stage === "sunset") {
      presentation = await exports.beginEpochPresentation();
      listener({ payload: { id: "epoch-id" } });
      await presentation.ready;
      unsubscribed = 0;
    }
    if (scenario === "send-error") {
      await assert.rejects(stage === "prepare" ? exports.beginEpochPresentation() : presentation.sunset(), /IPC unavailable/);
    } else {
      const ready = stage === "prepare" ? (await exports.beginEpochPresentation()).ready : presentation.sunset();
      void ready.then(() => { completed = true; });
      await flush();
      if (scenario !== "disconnected") {
        listener({ payload: { id: "old-id" } }); await flush();
        assert.equal(completed, false);
        if (scenario === "timeout") timeout();
        else listener({ payload: { id: "epoch-id" } });
      }
      await ready;
    }
    assert.equal(unsubscribed, 1);
  }
}

// 計算の成功後だけ日没を始め、暗転の応答より先に再取得・結果表示へ進まない。
{
  const source = await readFile(new URL("../src/utils/epochExecution.ts", import.meta.url), "utf8");
  const code = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
  }).outputText;
  for (const scenario of ["success", "failure", "no-report", "cancel-sunset", "sunset-error", "cancel-achievements", "result-error"]) {
    const calls = [], exports = {};
    let resolveReady, resolveCore, resolveSunset, current = true, reloads = 0, finishes = 0, notifications = 0;
    const ready = new Promise(resolve => { resolveReady = resolve; });
    const core = new Promise(resolve => { resolveCore = resolve; });
    const sunset = new Promise(resolve => { resolveSunset = resolve; });
    vm.runInNewContext(code, {
      exports, console: { error: () => {} },
      require: (name) => {
        if (name.endsWith("/core")) return { invoke: async (_command, args) => {
          calls.push(args.windowType);
          if (scenario === "result-error") throw new Error("cannot open results");
        } };
        if (name === "./epochPresentation") return { beginEpochPresentation: async () => ({
          id: "epoch-id", ready, isCurrent: async () => current,
          hideProgress: async () => { calls.push("hide-progress"); },
          sunset: async () => {
            calls.push("sunset");
            if (scenario === "sunset-error") throw new Error("IPC unavailable");
            await sunset;
          },
          finish: async () => { finishes++; },
        }) };
        if (name === "./api") return {
          buildApiUrl: (base, path) => base + path,
          fetchApi: async (url) => {
            assert.equal(url, "http://core/api/v1/epochs?count=3");
            calls.push("compute");
            if (scenario === "failure") throw new Error("core failed");
            return await core;
          },
        };
        if (name === "./ipc") return { sendSessionReload: async () => { reloads++; calls.push("reload"); } };
        if (name === "./achievements") return {
          takeUnlockedAchievements: async () => { if (scenario === "cancel-achievements") current = false; return []; },
          showUnlockedAchievements: async () => {},
        };
        if (name === "./notification") return { showNotification: async () => { notifications++; } };
        if (name === "./errors") return { describeError: () => "failed" };
        if (name === "./locale") return { resolveLocalizedText: value => value };
        if (name === "./log") return { logInfoToFile: () => {} };
        throw new Error(`unexpected import: ${name}`);
      },
    });
    const outcome = exports.executeEpochs({ apiBaseUrl: "http://core", count: 3, t: key => key, language: "ja" });
    await flush();
    assert.deepEqual(calls, []);
    resolveReady();
    await flush();
    assert.deepEqual(calls, ["compute"]);
    if (scenario === "failure") {
      assert.equal(await outcome, "failed");
      assert.equal(finishes, 1);
      assert.equal(notifications, 1);
      continue;
    }
    resolveCore({ epoch: 3, current_date: 123, epoch_report: scenario === "no-report" ? undefined : {} });
    await flush();
    if (scenario !== "sunset-error") {
      assert.deepEqual(calls, ["compute", "hide-progress", "sunset"]);
      assert.equal(reloads, 0);
    }
    if (scenario === "cancel-sunset") current = false;
    resolveSunset();
    const cancelled = scenario.startsWith("cancel-");
    assert.equal(await outcome, cancelled ? "superseded" : "completed");
    assert.equal(reloads, scenario === "cancel-sunset" ? 0 : 1);
    assert.equal(calls.includes("epoch_result"), !cancelled && scenario !== "no-report");
    assert.equal(finishes, scenario === "no-report" || scenario === "result-error" ? 1 : 0);
  }
}
// ブラウザ版も同じ要求と応答を中継し、古い実行IDと未接続時の送信を除外する。
{
  const state = { epochPresentationId: null, graphicConnected: true, isLoading: false };
  const actions = [], events = [];
  const modules = {
    "./state": { state },
    "./events": { emit: (event, payload) => events.push({ event, payload }) },
    "./ipc": { sendUiAction: (_window, _entity, action, params) => actions.push({ action, params }) },
    "./session": { requireBackend: () => {} },
    "./windows": { emitEpochProgressVisible: () => {} },
  };
  const source = await readFile(new URL("../web/shell/commands.ts", import.meta.url), "utf8");
  const exports = {};
  vm.runInNewContext(ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
  }).outputText, { exports, console, require: name => modules[name] ?? {} });
  assert.equal(await exports.runCommand("cockpit", "begin_epoch_presentation", { id: "web-epoch" }), true);
  assert.equal(actions[0].action, "epoch:prepare");
  assert.equal(await exports.runCommand("cockpit", "begin_epoch_sunset", { id: "old" }), false);
  assert.equal(actions.length, 1);
  assert.equal(await exports.runCommand("cockpit", "begin_epoch_sunset", { id: "web-epoch" }), true);
  assert.equal(actions[1].action, "epoch:sunset");
  assert.equal(actions[1].params.id, "web-epoch");
  state.graphicConnected = false;
  assert.equal(await exports.runCommand("cockpit", "begin_epoch_sunset", { id: "web-epoch" }), false);
  assert.equal(actions.length, 2);

  const ipcSource = await readFile(new URL("../web/shell/ipc.ts", import.meta.url), "utf8");
  const ipcSandbox = { exports: {}, console, queueMicrotask, require: name => modules[name] ?? {} };
  vm.runInNewContext(ts.transpileModule(ipcSource, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
  }).outputText, ipcSandbox);
  ipcSandbox.exports.installGraphicBridge();
  events.length = 0;
  state.graphicConnected = true;
  for (const event of ["epoch:ready", "epoch:darkened"]) {
    ipcSandbox.__genesisGraphicBridge.receive(JSON.stringify({ event, payload: { id: "old" } }));
    ipcSandbox.__genesisGraphicBridge.receive(JSON.stringify({ event, payload: { id: "web-epoch" } }));
  }
  await flush();
  assert.deepEqual(events.map(item => item.event), ["epoch:ready", "epoch:darkened"]);
}
console.log("PASS: Tips manual start, correlated stage replies, computation/sunset/results order, recovery and Web IPC");
