import * as constants from "../../src/constants";
import type { Bindings, Environment, Facts, Ref } from "../../src/tutorial/model";
const subscribers = new Map<string, Set<(event: { payload: any }) => void>>();
const master = {};
export const fixture = { records: new Map<string, Record<string, unknown>>(), creates: 0, failComplete: false };
export async function emit(name: string, payload: unknown) { for (const fn of [...subscribers.get(name) ?? []]) fn({ payload }); }
export async function listen(name: string, fn: (event: { payload: any }) => void) {
  if (!subscribers.has(name)) subscribers.set(name, new Set());
  subscribers.get(name)!.add(fn);
  return () => { subscribers.get(name)!.delete(fn); };
}
export async function sendTutorialGuide() {}
export function useMasterData() { return master; }
export async function createEnvironment(_base: string, _save: string, bindings: Bindings, pendingCreates: Record<string, boolean>, facts: Facts, _master: unknown, checkpoint: () => void, assertCurrent: () => void): Promise<Environment> {
  return { bindings, pendingCreates, facts, checkpoint, assertCurrent, constants: { ...constants, TUTORIAL_STREET: 221 },
    read: async (kind, id) => id ? [fixture.records.get(`${kind}:${id}`)!].filter(Boolean) : [...fixture.records].filter(([key]) => key.startsWith(kind + ":")).map(([, value]) => value),
    create: async (kind, body) => { const id = ++fixture.creates; fixture.records.set(`${kind}:${id}`, { id, ...(body as object) }); return id; },
    complete: async (_ref: Ref) => { if (fixture.failComplete) { fixture.failComplete = false; throw new Error("Injected completion failure"); } },
    reload: async () => {},
  };
}
