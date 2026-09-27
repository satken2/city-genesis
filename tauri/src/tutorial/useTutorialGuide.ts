import { useEffect, useRef, useState } from "react";
import { emit, listen } from "@tauri-apps/api/event";
import { DEFINITIONS } from "./definitions";
import { createEnvironment } from "./environment";
import { evaluate, execute, findPhase, resolve } from "./engine";
import { acceptSnapshot, GUIDE_OBSERVE, GUIDE_QUERY, GUIDE_STATE } from "./bus";
import { useMasterData } from "../contexts/MasterDataContext";
import { sendTutorialGuide } from "../utils/ipc";
import type { Bindings, Environment, Facts, Phase, Snapshot } from "./model";

type Observation = { runId: string; revision: number; label?: string; window?: string; visible?: boolean; entity?: number; pathVariant?: number; pathShape?: Facts["pathShape"]; savedPaths?: number[] };
const idleUi: Phase["ui"] = { policy: "highlight", allow: [], highlight: [] };

export function useTutorialGuide(active: boolean, scenario: string, stepNumber: number, saveId: string | null, base: string) {
  const definition = DEFINITIONS[scenario];
  const master = useMasterData();
  const step = definition?.steps.find(s => s.legacyStep === stepNumber);
  const [ready, setReady] = useState(false);
  const [failed, setFailed] = useState(false);
  const [textKey, setTextKey] = useState<string>();
  const [debug, setDebug] = useState("");
  const [attempt, setAttempt] = useState(0);
  const checkRef = useRef<() => Promise<boolean>>(async () => false);

  useEffect(() => {
    if (!active || !saveId || !base || !definition || !master) { setReady(!definition); setTextKey(undefined); return; }
    let disposed = false, current: Snapshot | undefined, env: Environment | undefined, phaseIndex = -1;
    const runId = crypto.randomUUID();
    const key = `tutorial_runtime_v1:${scenario}:${saveId}`;
    let stored: { bindings: Bindings; pendingCreates?: Record<string, boolean>; setupDone?: boolean };
    try { stored = JSON.parse(localStorage.getItem(key) ?? '{"bindings":{}}'); } catch { stored = { bindings: {} }; }
    stored.pendingCreates ??= {};
    const facts: Facts = { windows: {} };
    const unlisten: (() => void)[] = [];
    let queue = Promise.resolve();
    setReady(false); setFailed(false); setTextKey(undefined);
    const assertCurrent = () => { if (disposed) throw new Error("Tutorial operation cancelled"); };
    const checkpoint = () => { assertCurrent(); localStorage.setItem(key, JSON.stringify(stored)); };
    const fail = (error: unknown) => { if (!disposed) { console.error("Tutorial guide:", error); setFailed(true); setReady(false); } };
    const publish = async (index: number, preparing = false) => {
      if (disposed || !env) return;
      const phase = step?.phases[index];
      const world = phase ? JSON.parse(JSON.stringify(phase.world)) : { mode: preparing ? "blocked" : "observe" };
      if (world.path) world.path.variant = resolve(world.path.variant, env);
      current = { active: preparing || !!phase, runId, revision: (current?.revision ?? 0) + 1, saveId, scenario, stepId: step?.id ?? "", phaseId: phase?.id ?? "", ui: preparing ? { policy: "strict", allow: [], highlight: [] } : phase?.ui ?? idleUi, world };
      phaseIndex = index;
      acceptSnapshot(current);
      setTextKey(preparing ? "tutorial.guide.preparing" : phase?.textKey);
      setDebug(JSON.stringify({ step: step?.id, phase: phase?.id, until: phase?.until, bindings: stored.bindings }, null, 2));
      await emit(GUIDE_STATE, current);
      await sendTutorialGuide(current);
      await emit(GUIDE_QUERY, { runId });
    };
    const reconcile = async () => {
      if (!env || !step || disposed) return;
      const index = await findPhase(step, env);
      if (index !== phaseIndex) await publish(index);
    };
    async function subscribe<T>(event: string, handler: (payload: T) => void) {
      const stop = await listen<T>(event, e => handler(e.payload));
      if (disposed) stop(); else unlisten.push(stop);
    }
    checkRef.current = async () => {
      if (!env || disposed) return false;
      try {
        await queue;
        if (!step) return true;
        await reconcile();
        if (phaseIndex !== step.phases.length - 1 || !await evaluate(step.phases[phaseIndex].until, env)) return false;
        await execute(step.beforeCheck ?? [], env);
        if (step.complete && !await evaluate(step.complete, env)) return false;
        await execute(step.afterCheck ?? [], env);
        checkpoint();
        return true;
      } catch (error) { fail(error); return false; }
    };
    void (async () => {
      await subscribe<Observation>(GUIDE_OBSERVE, data => {
        if (!current?.active || data.runId !== runId || data.revision !== current.revision) return;
        if (data.label && data.window) {
          facts.windows[data.label] = { window: data.window, visible: data.visible === true, entity: data.entity };
          if (data.window === "path_build" && !data.visible) { delete facts.pathVariant; delete facts.pathShape; }
        }
        if (data.pathVariant !== undefined) facts.pathVariant = data.pathVariant;
        if (data.pathShape) facts.pathShape = data.pathShape;
        if (data.savedPaths?.length === 1 && current.world.path?.bind) {
          stored.bindings[current.world.path.bind] = { kind: "paths", id: data.savedPaths[0] }; checkpoint();
        }
        queue = queue.then(reconcile).catch(fail);
      });
      await subscribe<{ label: string; window: string; entity?: number; visible: boolean }>("tutorial:window_visibility", data => {
        facts.windows[data.label] = data;
        if (data.window === "path_build" && !data.visible) { delete facts.pathVariant; delete facts.pathShape; }
        queue = queue.then(reconcile).catch(fail);
      });
      await subscribe("graphic-reconnected", () => { if (current) void sendTutorialGuide(current).catch(fail); });
      await subscribe<{ request?: boolean }>(GUIDE_QUERY, payload => { if (payload.request && current) void emit(GUIDE_STATE, current).catch(fail); });
      env = await createEnvironment(base, saveId, stored.bindings, stored.pendingCreates!, facts, master, checkpoint, assertCurrent);
      if (!stored.setupDone) {
        await publish(-1, true);
        if (!stored.setupDone) { await execute(definition.setup ?? [], env); stored.setupDone = true; checkpoint(); }
      }
      assertCurrent();
      await publish(0);
      setReady(true);
    })().catch(fail);
    return () => {
      disposed = true; unlisten.forEach(stop => stop()); checkRef.current = async () => false;
      const cleared: Snapshot = { active: false, runId, revision: (current?.revision ?? 0) + 1, saveId, scenario, stepId: "", phaseId: "", ui: idleUi, world: { mode: "observe" } };
      acceptSnapshot(cleared);
      void emit(GUIDE_STATE, cleared).catch(console.error);
      void sendTutorialGuide(cleared).catch(console.error);
    };
  }, [active, scenario, stepNumber, saveId, base, attempt, definition, step, master]);

  return { managed: !!step, hasSetup: !!definition?.setup, ready: definition ? ready : true, failed, textKey, debug,
    check: () => checkRef.current(), retry: () => setAttempt(a => a + 1) };
}
