/**
 * Placement methods for the path construction panel.
 *
 * Each method defines Unity's click sequence and the core's expansion (POST /paths/layout). Single placement is available
 * for all types; other methods require membership in shared type lists (PATH_TYPES_GRID_BUILD for grids).
 */

import { PATH_BUILD_METHOD_GRID, PATH_BUILD_METHOD_SINGLE, PATH_TYPES_GRID_BUILD } from "../../constants";

const METHOD_PATH_TYPES: Record<number, number[]> = {
  [PATH_BUILD_METHOD_GRID]: PATH_TYPES_GRID_BUILD,
};

/** Names under translation keys path_construction.buildMethod.*. */
export const BUILD_METHOD_KEYS: Record<number, string> = {
  [PATH_BUILD_METHOD_SINGLE]: "single",
  [PATH_BUILD_METHOD_GRID]: "grid",
};

/** Initial grid subdivision counts ([first side, second side]). */
export const DEFAULT_GRID_DIVISIONS: [number, number] = [3, 3];

/** Return placement methods available for the type, in panel display order. */
export function buildMethodsFor(pathType: number | null | undefined): number[] {
  const methods = [PATH_BUILD_METHOD_SINGLE];
  for (const [method, types] of Object.entries(METHOD_PATH_TYPES)) {
    if (pathType != null && types.includes(pathType)) methods.push(Number(method));
  }
  return methods;
}
