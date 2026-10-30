import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import ts from "typescript";
import vm from "node:vm";
import { createRequire } from "node:module";
import { renderToStaticMarkup } from "react-dom/server";

const source = await readFile(new URL("../src/utils/epochFlow.ts", import.meta.url), "utf8");
const { outputText } = ts.transpileModule(source, {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 },
});
const { runEpochWithPresentation } = await import(`data:text/javascript;base64,${Buffer.from(outputText).toString("base64")}`);
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
};
const flush = () => new Promise((resolve) => setImmediate(resolve));

// Tipsの期限より前に終わった実行は、期限を待たずに結果を返し、Tipsも出さない。
{
  const due = deferred(), calls = [];
  const flow = runEpochWithPresentation(() => { calls.push("execute"); return Promise.resolve(42); }, due.promise, async (v) => calls.push(v));
  assert.equal(await flow, 42);
  assert.deepEqual(calls, ["execute"]);
}

// 遅い処理は期限が来てからだけTipsを出し、閉じ終えてから結果へ渡す。
{
  const request = deferred(), due = deferred(), closing = deferred(), calls = [];
  let returned = false;
  const flow = runEpochWithPresentation(() => request.promise, due.promise, async (v) => { calls.push(v); if (!v) await closing.promise; });
  void flow.then(() => { returned = true; });
  await flush(); assert.deepEqual(calls, []);
  due.resolve(); await flush(); assert.deepEqual(calls, [true]);
  request.resolve(7); await flush();
  assert.deepEqual(calls, [true, false]); assert.equal(returned, false);
  closing.resolve(); assert.equal(await flow, 7);
}

// Tips生成中に完了しても、生成後に確実に閉じる。
{
  const request = deferred(), opening = deferred(), calls = [];
  const flow = runEpochWithPresentation(() => request.promise, Promise.resolve(), async (v) => { calls.push(v); if (v) await opening.promise; });
  await flush(); request.resolve(9); await flush();
  assert.deepEqual(calls, [true]);
  opening.resolve(); assert.equal(await flow, 9);
  assert.deepEqual(calls, [true, false]);
}

// 期限前の拒否はTipsを出さずにすぐ返し、Tips表示中の通信失敗ではTipsも閉じる。
for (const early of [true, false]) {
  const request = deferred(), due = deferred(), calls = [], failure = new Error("HTTP 503");
  const flow = runEpochWithPresentation(() => request.promise, due.promise, async (v) => calls.push(v));
  const check = assert.rejects(flow, (error) => error === failure);
  if (early) request.reject(failure);
  else { due.resolve(); await flush(); request.reject(failure); }
  await check;
  assert.deepEqual(calls, early ? [] : [true, false]);
}
// 初期ロードの準備完了は手動開始を保ち、同じ状態でもエポック用は開始ボタンを出さない。
{
  const require = createRequire(import.meta.url);
  const tipsSource = await readFile(new URL("../src/components/tips/TipsWindow.tsx", import.meta.url), "utf8");
  const code = ts.transpileModule(tipsSource, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX },
  }).outputText;
  for (const mode of ["initial", "epoch"]) {
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
    const html = renderToStaticMarkup(exports.TipsWindow({ mode }));
    const cleanup = effects.map((effect) => effect());
    await flush();
    assert.equal(html.includes("tips.startGame"), mode === "initial");
    assert.equal(html.includes("epochConfirm.executing"), mode === "epoch");
    assert.equal(queries, mode === "initial" ? 1 : 0);
    assert.equal(starts, 0);
    cleanup.forEach((fn) => fn?.());
  }
}

// Unityの準備完了の通知は演出IDを照合する。未接続・停止時も待ち続けず、購読を必ず解除する。
{
  const source = await readFile(new URL("../src/utils/epochPresentation.ts", import.meta.url), "utf8");
  const code = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
  }).outputText;
  for (const scenario of ["connected", "disconnected", "timeout", "send-error"]) {
    let listener, timeout, unsubscribed = 0, completed = false;
    const exports = {};
    vm.runInNewContext(code, {
      exports, console, crypto: { randomUUID: () => "epoch-id" },
      setTimeout: (fn) => { timeout = fn; return 1; }, clearTimeout: () => {},
      require: (name) => name.endsWith("/core") ? { invoke: async () => {
        if (scenario === "send-error") throw new Error("IPC unavailable");
        return scenario !== "disconnected";
      } } : name.endsWith("/event") ? { listen: async (_, fn) => {
        listener = fn; return () => { unsubscribed++; };
      } } : { logInfoToFile: () => {} },
    });
    if (scenario === "send-error") {
      await assert.rejects(exports.beginEpochPresentation(), /IPC unavailable/);
    } else {
      const presentation = await exports.beginEpochPresentation();
      void presentation.ready.then(() => { completed = true; });
      if (scenario !== "disconnected") {
        listener({ payload: { id: "old-id" } }); await flush();
        assert.equal(completed, false);
        if (scenario === "timeout") timeout();
        else listener({ payload: { id: "epoch-id" } });
      }
      await presentation.ready;
    }
    assert.equal(unsubscribed, 1);
  }
}
console.log("PASS: epoch ordering, Tips modes and lifecycle, fast/slow/error responses, correlated readiness acknowledgement and cleanup");
