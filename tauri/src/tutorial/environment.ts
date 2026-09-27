import * as constants from "../constants";
import { fetchApi } from "../utils/api";
import { sendSessionReload } from "../utils/ipc";
import { entityId } from "./engine";
import type { Bindings, Environment, Facts, Resource } from "./model";
import type { MasterData } from "../types";

const routes: Record<Resource, string> = { paths: "paths", parcels: "parcels", buildings: "buildings", agents: "agents", zones: "zones", city: "city", budgets: "city/budgets", lines: "transit/lines", tutorial: "tutorials" };
const single: Record<Resource, string> = { paths: "path", parcels: "parcel", buildings: "economic_agent", agents: "economic_agent", zones: "zone", city: "city", budgets: "budgets", lines: "line", tutorial: "tutorial" };

export async function createEnvironment(base: string, saveId: string, bindings: Bindings, pendingCreates: Record<string, boolean>, facts: Facts, master: MasterData, checkpoint: () => void, assertCurrent: () => void): Promise<Environment> {
  const root = `${base.replace(/\/+$/, "")}/api/v1`;
  assertCurrent();
  // 表示言語に依存しない属性で地上の2車線街路を解決し、曖昧な場合は準備を止める。
  const streets = Object.entries(master.paths).filter(([, p]) => p.type === constants.PATH_STREET && p.lanes === 2 && !p.height_editable && p.frontage_allowed);
  if (streets.length !== 1) throw new Error("Tutorial requires exactly one ground-level two-lane street");
  const request = (resource: Resource, id?: number) => `${root}/${id && resource === "buildings" ? "agents" : routes[resource]}${id ? `/${id}` : ""}`;
  async function assertSession() {
    assertCurrent();
    const session = await fetchApi<{ current_save_id: string | null }>(`${root}/sessions`);
    assertCurrent();
    if (session.current_save_id !== saveId) throw new Error("Tutorial session changed");
  }
  const env: Environment = {
    bindings, pendingCreates, facts, constants: { ...constants, TUTORIAL_STREET: Number(streets[0][0]) }, checkpoint, assertCurrent,
    async read(resource, id) {
      const data = await fetchApi<Record<string, unknown>>(request(resource, id));
      assertCurrent();
      if (!id && Array.isArray(data[resource])) return data[resource] as Record<string, unknown>[];
      return [(data[single[resource]] ?? data) as Record<string, unknown>];
    },
    async create(resource, payload) {
      await assertSession();
      let body = payload as Record<string, unknown>;
      if (resource === "buildings") {
        const entry = master.agents[Number(body.variant)];
        if (!entry) throw new Error(`Unknown building variant: ${body.variant}`);
        body = { type: entry.type, floors: entry.min_floors ?? 1, construction_ratio: 1, ...body };
      }
      const data = await fetchApi<Record<string, unknown>>(`${root}/${resource}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
      assertCurrent();
      if (resource === "parcels" && typeof data.parcel === "number" && data.parcel > 0) return data.parcel;
      const raw = resource === "paths" ? (data.paths as Record<string, unknown>[])?.[0] : data[single[resource as Resource]] ?? data;
      // zonesの作成だけは単体レスポンスをさらにzoneで包む。
      const row = raw as Record<string, unknown>;
      return entityId((row?.[single[resource as Resource]] ?? row) as Record<string, unknown>, resource as Resource);
    },
    async complete(ref) {
      if (!["paths", "buildings", "agents"].includes(ref.kind)) throw new Error("Only paths and buildings have construction status");
      await assertSession();
      const [current] = await env.read(ref.kind, ref.id);
      if (current.status === constants.STATUS_ACTIVE) return;
      await fetchApi(request(ref.kind, ref.id), { method: "PATCH", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ status: constants.STATUS_ACTIVE }) });
      assertCurrent();
      const [updated] = await env.read(ref.kind, ref.id);
      if (updated.status !== constants.STATUS_ACTIVE) throw new Error("Construction did not become active");
    },
    async scenarioAction(scenario, action) {
      await assertSession();
      await fetchApi(`${root}/tutorials/actions`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ save_id: saveId, scenario, action }) });
      assertCurrent();
    },
    async reload() { assertCurrent(); await sendSessionReload(); },
  };
  return env;
}
