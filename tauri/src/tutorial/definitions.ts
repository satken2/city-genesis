import { validate as validateGeneratedSchema } from "./schemaValidator.generated";
import basic from "./definitions/basic.json";
import district from "./definitions/scenario2.json";
import infra from "./definitions/scenario3.json";
import rail from "./definitions/scenario4.json";
import trade from "./definitions/trade.json";
import highway from "./definitions/highway.json";
import mining from "./definitions/mining.json";
import tourism from "./definitions/tourism.json";

import type { Definition } from "./model";
import * as constants from "../constants";

/** DOMの構造や翻訳文ではなく、登録済みの操作名だけを定義で指定する。 */
export const TARGETS = ["hud.road", "hud.construction", "hud.land", "hud.district", "hud.execute", "hud.visibility", "hud.transport", "path.type", "path.variant", "path.followTerrain", "path.platformSide", "path.back", "path.confirm", "path.cancel", "building.category", "building.variant", "building.back", "building.cancel", "building.confirm", "parcel.mode", "parcel.clear", "parcel.confirm", "transit.new", "transit.type", "transit.fare", "transit.headway", "transit.confirm", "transit.cancel", "window.path_build", "window.agent_detail"];
/** 同じ名前の要素が並ぶ操作。`名前:値`(値はカテゴリ番号・バリアント番号・地形追従の設定値・ホーム設置側の値・区画の作成方法の名前)で1つだけを指せる。 */
export const VALUE_TARGETS = ["path.type", "path.variant", "path.followTerrain", "path.platformSide", "building.category", "building.variant", "parcel.mode"];
/** 値が番号でない操作の書式(それ以外の値は数字)。 */
const VALUE_PATTERN: Record<string, RegExp> = { "parcel.mode": /^[a-z_]+$/ };
/** コアが初期配置した対象の名前。束縛はチュートリアル開始時にコアから受け取る。 */
const CORE_TARGET = /^(support|lesson)\./;
/** schema.json の検証。CSP で eval を許さないため、Ajv の事前生成コードを使う(npm run gen:tutorial-validator)。 */
const validateSchema = validateGeneratedSchema as ((data: unknown) => boolean) & { errors?: unknown };
export function validateDefinition(value: unknown): asserts value is Definition {
  if (!validateSchema(value)) throw new Error(JSON.stringify(validateSchema.errors));
  const definition = value as Definition;
  const bindings = new Set<string>();
  const walk = (node: unknown, visit: (value: Record<string, unknown>) => void) => {
    if (Array.isArray(node)) node.forEach(v => walk(v, visit));
    else if (node && typeof node === "object") { visit(node as Record<string, unknown>); Object.values(node).forEach(v => walk(v, visit)); }
  };
  walk(definition, node => { if (typeof node.bind === "string") bindings.add(node.bind); });
  walk(definition, node => {
    if (typeof node.$constant === "string" && node.$constant !== "TUTORIAL_STREET" && !(node.$constant in constants)) throw new Error(`Unknown constant: ${node.$constant}`);
    for (const key of ["$ref", "target", "from", "to"]) if (typeof node[key] === "string" && !bindings.has(node[key] as string) && !CORE_TARGET.test(node[key] as string)) throw new Error(`Unknown binding: ${node[key]}`);
    if (("$ref" in node || "$constant" in node) && Object.keys(node).length !== 1) throw new Error("References must have exactly one key");
    if (node.type === "create" && node.resource === "parcels" && (node.body as Record<string, unknown>).mode !== "empty_area") throw new Error("Tutorial parcels use empty_area (one returned ID)");
    if (node.type === "create" && node.resource === "paths" && (!Array.isArray((node.body as Record<string, unknown>).paths) || ((node.body as Record<string, unknown>).paths as unknown[]).length !== 1)) throw new Error("Create one bound path per action");
  });
  const ids = new Set<string>(), indices = new Set<number>();
  for (const step of definition.steps) {
    if (ids.has(step.id) || indices.has(step.legacyStep)) throw new Error(`Duplicate step: ${step.id}`);
    ids.add(step.id); indices.add(step.legacyStep);
    const phases = new Set<string>();
    for (const phase of step.phases) {
      if (phases.has(phase.id)) throw new Error(`Duplicate phase: ${phase.id}`);
      phases.add(phase.id);
      for (const target of [...phase.ui.allow, ...phase.ui.highlight]) {
        const colon = target.indexOf(":");
        const name = colon < 0 ? target : target.slice(0, colon), value = colon < 0 ? undefined : target.slice(colon + 1);
        if (value === undefined ? !TARGETS.includes(name) : !VALUE_TARGETS.includes(name) || !(VALUE_PATTERN[name] ?? /^\d+$/).test(value)) throw new Error(`Unknown tutorial target: ${target}`);
      }
      if (phase.ui.policy === "strict" && phase.ui.allow.some(t => t.startsWith("window."))) throw new Error("strict requires individual controls");
      if (phase.world.mode === "path" && !phase.world.path) throw new Error(`Missing path constraint: ${step.id}`);
      if (phase.world.path) {
        const [a, b] = phase.world.path.points;
        if (Math.hypot(a[0] - b[0], a[2] - b[2]) < 1) throw new Error("Tutorial path endpoints overlap");
      }
    }
  }
}
export const DEFINITIONS: Record<string, Definition> = {};
for (const value of [basic, district, infra, rail, trade, highway, mining, tourism]) { validateDefinition(value); DEFINITIONS[value.id] = value; }
