import * as constants from "../../src/constants";
import type { Bindings, Environment, Facts, Ref } from "../../src/tutorial/model";
const subscribers = new Map<string, Set<(event: { payload: any }) => void>>();
const master = {};
export const fixture = { records: new Map<string, Record<string, unknown>>(), creates: 0, failComplete: false, actions: 0, failAction: false, onAction: undefined as (() => void) | undefined };
export async function emit(name: string, payload: unknown) { for (const fn of [...subscribers.get(name) ?? []]) fn({ payload }); }
export async function listen(name: string, fn: (event: { payload: any }) => void) {
  if (!subscribers.has(name)) subscribers.set(name, new Set());
  subscribers.get(name)!.add(fn);
  return () => { subscribers.get(name)!.delete(fn); };
}
export async function sendTutorialGuide() {}
export function useMasterData() { return master; }
export const windowState = { hidden: false };
const appWindow = { listen, show: async () => { windowState.hidden = false; }, hide: async () => { windowState.hidden = true; } };
export function getCurrentWindow() { return appWindow; }
const t = (key: string) => key;
export function useTranslation() { return { t }; }
export const invocations: unknown[][] = [];
export async function invoke(...args: unknown[]) { invocations.push(args); }
export const notifications: string[] = [];
export async function showNotification({ message }: { message: string }) { notifications.push(message); }
export const TUTORIAL_SCENARIOS = [{ id: "basic", steps: [1, 2, 3].map(id => ({ id, image: "", titleKey: `step${id}`, descriptionKey: `description${id}` })) }];
/** 画面部品の試験では進行管理を差し替え、「次へ」がそのまま合格するようにする。 */
export function useTutorialGuide() {
  return { managed: true, hasSetup: false, ready: true, failed: false, textKey: undefined, debug: "", check: async () => true, advance: async () => false, retry: () => {} };
}
export async function createEnvironment(_base: string, _save: string, bindings: Bindings, pendingCreates: Record<string, boolean>, facts: Facts, _master: unknown, checkpoint: () => void, assertCurrent: () => void): Promise<Environment> {
  return { bindings, pendingCreates, facts, checkpoint, assertCurrent, constants: { ...constants, TUTORIAL_STREET: 221 },
    read: async (kind, id) => id ? [fixture.records.get(`${kind}:${id}`)!].filter(Boolean) : [...fixture.records].filter(([key]) => key.startsWith(kind + ":")).map(([, value]) => value),
    create: async (kind, body) => { const id = ++fixture.creates; fixture.records.set(`${kind}:${id}`, { id, ...(body as object) }); return id; },
    complete: async (_ref: Ref) => { if (fixture.failComplete) { fixture.failComplete = false; throw new Error("Injected completion failure"); } },
    scenarioAction: async () => { fixture.actions++; if (fixture.failAction) { fixture.failAction = false; throw new Error("Injected action failure"); } fixture.onAction?.(); },
    reload: async () => {},
  };
}
