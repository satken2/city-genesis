import type { Action, Condition, Environment, GuideStep, Match, Resource, Value } from "./model";

export function field(record: unknown, path: string): unknown {
  return path.split(".").reduce<unknown>((value, key) => value != null && typeof value === "object" ? (value as Record<string, unknown>)[key] : undefined, record);
}

export function resolve(value: Value, env: Environment): unknown {
  if (Array.isArray(value)) return value.map(v => resolve(v, env));
  if (value && typeof value === "object") {
    if (typeof value.$constant === "string") {
      const result = env.constants[value.$constant];
      if (result === undefined) throw new Error(`Unknown constant: ${value.$constant}`);
      return result;
    }
    if (typeof value.$ref === "string") {
      const result = env.bindings[value.$ref];
      if (!result) throw new Error(`Unbound entity: ${value.$ref}`);
      return result.id;
    }
    return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, resolve(v, env)]));
  }
  return value;
}

function matches(row: Record<string, unknown>, where: Match[], env: Environment): boolean {
  const equal = (a: unknown, b: unknown): boolean => {
    if (a === b) return true;
    if (Array.isArray(a) && Array.isArray(b)) return a.length === b.length && a.every((v, i) => equal(v, b[i]));
    return false;
  };
  return where.every(m => {
    const value = field(row, m.field);
    if (m.oneOf) return m.oneOf.some(v => equal(value, resolve(v, env)));
    if (m.greaterThan !== undefined) return typeof value === "number" && value > m.greaterThan;
    return equal(value, resolve(m.equals ?? null, env));
  });
}

export function entityId(row: Record<string, unknown>, resource: Resource): number {
  const key = ({ paths: "path_id", parcels: "parcel_id", agents: "agent_id", buildings: "agent_id", zones: "zone_id", lines: "line_agent_id", city: "agent_id", budgets: "id", tutorial: "id" })[resource];
  const id = row?.[key];
  if (typeof id !== "number" || id <= 0) throw new Error("Entity response has no positive ID");
  return id;
}

export async function evaluate(condition: Condition, env: Environment): Promise<boolean> {
  env.assertCurrent();
  switch (condition.type) {
    case "ack": return true;
    case "all":
    case "any": {
      // allが途中で失敗したときに一部の束縛だけを残さない。
      const original = { ...env.bindings };
      const restore = () => { Object.keys(env.bindings).forEach(k => delete env.bindings[k]); Object.assign(env.bindings, original); };
      try {
        for (const child of condition.conditions) {
          const pass = await evaluate(child, env);
          if (condition.type === "all" && !pass) { restore(); return false; }
          if (condition.type === "any" && pass) return true;
        }
      } catch (error) { restore(); throw error; }
      return condition.type === "all";
    }
    case "window": return Object.values(env.facts.windows).some(w => w.visible && w.window === condition.window
      && (!condition.target || w.entity === env.bindings[condition.target]?.id));
    case "pathVariant": return env.facts.pathVariant === resolve(condition.value, env);
    case "pathPoints": return (env.facts.pathShape?.selected_points.length ?? 0) >= condition.count
      && (!condition.phase || env.facts.pathShape?.phase === condition.phase);
    case "entity": {
      const target = condition.target ? env.bindings[condition.target] : undefined;
      if (condition.target && !target) return false;
      const rows = await env.read(condition.resource, target?.id);
      env.assertCurrent();
      let row: Record<string, unknown> | undefined;
      for (const summary of rows) {
        // 一覧に無い項目（建物のparcel_id等）を条件にした場合だけ単体APIで補う。
        const candidate = condition.where.some(m => field(summary, m.field) === undefined)
          ? (await env.read(condition.resource, entityId(summary, condition.resource)))[0] : summary;
        if (candidate && matches(candidate, condition.where, env)) { row = candidate; break; }
      }
      env.assertCurrent();
      if (!row) return false;
      if (condition.bind) env.bindings[condition.bind] = { kind: condition.resource, id: entityId(row, condition.resource) };
      return true;
    }
    case "budgetRatio": {
      const [budgets] = await env.read("budgets");
      env.assertCurrent();
      const sum = Object.values(budgets ?? {}).reduce<number>((n, v) => n + (typeof v === "number" ? v : 0), 0);
      return sum > 0 && Number(budgets?.[condition.category] ?? 0) / sum >= condition.min;
    }
    case "connected": {
      const refs = [env.bindings[condition.from], env.bindings[condition.to]];
      if (refs.some(r => !r)) return false;
      const terminals = await Promise.all(refs.map(async r => field((await env.read(r.kind, r.id))[0], condition.terminal)));
      if (terminals.some(n => typeof n !== "number" || n <= 0)) return false;
      const paths = (await env.read("paths")).filter(p => matches(p, condition.where, env));
      env.assertCurrent();
      const adjacent = new Map<unknown, unknown[]>();
      for (const p of paths) {
        const a = p.start_node_id, b = p.end_node_id;
        if (typeof a !== "number" || typeof b !== "number" || a <= 0 || b <= 0) continue;
        adjacent.set(a, [...(adjacent.get(a) ?? []), b]); adjacent.set(b, [...(adjacent.get(b) ?? []), a]);
      }
      const pending = [terminals[0]], seen = new Set<unknown>();
      while (pending.length) { const node = pending.pop(); if (node === terminals[1]) return true; if (seen.has(node)) continue; seen.add(node); pending.push(...(adjacent.get(node) ?? [])); }
      return false;
    }
  }
}

/** 都市を書き換える処理は判定から独立させ、各成功を保存して再試行時に再利用する。 */
export async function execute(actions: Action[], env: Environment): Promise<void> {
  for (const action of actions) {
    env.assertCurrent();
    if (action.type === "create") {
      const existing = env.bindings[action.bind];
      if (existing) {
        const rows = await env.read(existing.kind, existing.id);
        if (rows.length === 0) throw new Error(`Missing setup entity: ${action.bind}`);
        continue;
      }
      if (env.pendingCreates[action.bind]) throw new Error(`Creation outcome unknown: ${action.bind}. Restart this tutorial in a new city.`);
      const payload = resolve(action.body, env);
      env.pendingCreates[action.bind] = true;
      env.checkpoint();
      const id = await env.create(action.resource, payload);
      env.assertCurrent();
      env.bindings[action.bind] = { kind: action.resource, id };
      delete env.pendingCreates[action.bind];
      env.checkpoint();
    } else if (action.type === "completeConstruction") {
      const ref = env.bindings[action.target];
      if (!ref) throw new Error(`Unbound construction: ${action.target}`);
      await env.complete(ref);
    } else if (action.type === "scenarioAction") {
      if (!env.scenarioAction) throw new Error("Tutorial actions are unavailable");
      await env.scenarioAction(action.scenario, action.action);
    } else await env.reload();
    env.assertCurrent();
  }
}

/** 取消やパネルの閉鎖も含め、現在満たしている前提から案内段階を再計算する。 */
export async function findPhase(step: GuideStep, env: Environment): Promise<number> {
  const pathBinding = step.phases.find(p => p.world.path?.bind)?.world.path?.bind;
  if (pathBinding && env.bindings[pathBinding] && step.complete && await evaluate(step.complete, env)) return step.phases.length - 1;
  let index = 0;
  while (index < step.phases.length - 1 && await evaluate(step.phases[index].until, env)) index++;
  return index;
}
