import { emit } from "@tauri-apps/api/event";
import type { Snapshot } from "./model";

export const GUIDE_STATE = "tutorial:guide_state";
export const GUIDE_QUERY = "tutorial:guide_query";
export const GUIDE_OBSERVE = "tutorial:guide_observe";
let currentState: Snapshot | null = null;
const retiredRuns = new Set<string>();
export function acceptSnapshot(next: Snapshot): Snapshot | null {
  if (retiredRuns.has(next.runId)) return currentState;
  if (currentState?.runId === next.runId && currentState.revision >= next.revision) return currentState;
  if (!next.active && currentState && currentState.runId !== next.runId) { retiredRuns.add(next.runId); return currentState; }
  if (currentState && currentState.runId !== next.runId) retiredRuns.add(currentState.runId);
  currentState = next;
  if (!next.active) retiredRuns.add(next.runId);
  return currentState;
}
export function snapshot(): Snapshot | null {
  return currentState;
}
export async function observe(data: Record<string, unknown>): Promise<void> {
  const current = snapshot();
  if (current?.active) await emit(GUIDE_OBSERVE, { runId: current.runId, revision: current.revision, ...data });
}

/** Check the same restrictions on final submission. Button appearance alone cannot prevent bypasses through 2D editing, etc. */
export function allowsPathBody(body: unknown, state = snapshot()): boolean {
  if (!state?.active) return true;
  if (state.world.mode !== "path" || !state.world.path) return state.world.mode === "observe";
  const rule = state.world.path;
  const paths = (body as { paths?: Record<string, unknown>[] })?.paths;
  if (paths?.length !== 1) return false;
  const p = paths[0];
  const equal = (v: unknown, expected: number[]) => Array.isArray(v) && v.length === 3 && v.every((n, i) => typeof n === "number" && Math.abs(n - expected[i]) < 0.05);
  return p.path_variant === rule.variant && p.curved === false && equal(p.start_point, rule.points[0]) && equal(p.end_point, rule.points[1])
    && (!Array.isArray(p.waypoints) || p.waypoints.length === 0)
    && p.start_control == null && p.end_control == null
    && p.follow_terrain == null && (!Array.isArray(p.waypoint_controls) || p.waypoint_controls.length === 0)
    && (!Array.isArray(p.embankment_heights) || p.embankment_heights.every(v => v === 0));
}
