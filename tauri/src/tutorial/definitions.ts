import { validate as validateGeneratedSchema } from "./schemaValidator.generated";
import rail from "./definitions/scenario4.json";
import trade from "./definitions/trade.json";
import highway from "./definitions/highway.json";
import mining from "./definitions/mining.json";
import tourism from "./definitions/tourism.json";
import reclamation from "./definitions/reclamation.json";
import acquisition from "./definitions/acquisition.json";
import controls from "./definitions/controls.json";

import type { Condition, Definition } from "./model";
import * as constants from "../constants";

/** Definitions specify registered action names, not DOM structure or translated text. */
export const TARGETS = ["hud.road", "hud.construction", "hud.land", "hud.district", "hud.execute", "hud.visibility", "hud.transport", "hud.grading", "hud.map", "hud.citizen_list", "hud.bulldozer", "hud.notifications", "hud.budget", "hud.publicServices", "hud.score", "hud.date", "hud.climate", "hud.construction_projects", "hud.regulation", "hud.stat", "hud.prop_placement", "display.tabs", "display.modes", "agent.demolish", "path.type", "path.variant", "path.height", "path.followTerrain", "path.embankment", "path.platformSide", "path.back", "path.confirm", "path.cancel", "path.shapeEditor", "path.plan", "path.profile", "path.route", "building.category", "building.variant", "building.back", "building.cancel", "building.confirm", "parcel.mode", "parcel.clear", "parcel.confirm", "transit.new", "transit.type", "transit.fare", "transit.headway", "transit.confirm", "transit.cancel", "zone_edit.tool", "zone_edit.mode", "window.path_build", "window.agent_detail"];
/** Actions with multiple elements sharing a name. `name:value` selects one; values are category/variant IDs, terrain-following settings, platform side values, parcel creation method names, district editing tool names, or district painting method names. */
export const VALUE_TARGETS = ["path.type", "path.variant", "path.followTerrain", "path.platformSide", "building.category", "building.variant", "parcel.mode", "zone_edit.tool", "zone_edit.mode"];
/** Formats for actions with nonnumeric values (all other values are numeric). */
const VALUE_PATTERN: Record<string, RegExp> = { "parcel.mode": /^[a-z_]+$/, "zone_edit.tool": /^[a-z_]+$/, "zone_edit.mode": /^[a-z_]+$/ };
/** Names of targets initially placed by the core. Receive bindings from the core when the tutorial starts. */
const CORE_TARGET = /^(support|lesson)\./;
/** Validate schema.json with pre-generated Ajv code because CSP forbids eval (npm run gen:tutorial-validator). */
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
  const ids = new Set<string>(), indices = new Set<number>(), pageIds = new Set<string>();
  for (const step of definition.steps) {
    if (ids.has(step.id) || indices.has(step.legacyStep)) throw new Error(`Duplicate step: ${step.id}`);
    ids.add(step.id); indices.add(step.legacyStep);
    const phases = new Set<string>();
    const pageIn = (condition: Condition | undefined) => { let found = false; walk(condition, node => { if (node.type === "page") found = true; }); return found; };
    if (pageIn(step.complete)) throw new Error(`page cannot be a completion condition: ${step.id}`);
    for (const [index, phase] of step.phases.entries()) {
      if (phases.has(phase.id)) throw new Error(`Duplicate phase: ${phase.id}`);
      phases.add(phase.id);
      // Paging advances only through Next. It cannot be placed in the final stage, where Next completes the step, or combined with other conditions.
      if (pageIn(phase.until)) {
        if (phase.until.type !== "page" || index === step.phases.length - 1) throw new Error(`page must be the whole condition of a non-last phase: ${phase.id}`);
        if (pageIds.has(phase.until.id)) throw new Error(`Duplicate page: ${phase.until.id}`);
        pageIds.add(phase.until.id);
      }
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
for (const value of [rail, trade, highway, mining, tourism, reclamation, acquisition, controls]) { validateDefinition(value); DEFINITIONS[value.id] = value; }
