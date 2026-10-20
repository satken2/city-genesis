/**
 * Parallel track placement logic (railway, monorail, people movers).
 *
 * Instead of selecting preset composite paths (combined_paths), players configure track count, each track's direction,
 * and spacing directly for track variants. Default spacing is master width (adjacent corridors without gaps),
 * adjustable within a range using the panel's widen/narrow buttons.
 * This module provides pure functions generating component definitions (equivalent to CombinedPathComponent)
 * for expanding one centerline into parallel single-track paths.
 *
 * People movers (PATH_CONVEYOR, e.g. ropeways) are treated as tracks but drawn/placed like roads: one path with meshes
 * for ropes in both directions. They are therefore excluded from parallel expansion. Economically they are fast
 * footpaths accessible only at boarding/alighting sections, not lines, and create no lines or stops.
 *
 * Rail/monorail variants with master lanes >= 2 (combined double-track railway/monorail paths and dedicated LRT tracks)
 * also do not expand. One path carries both directions; Unity draws lanes tracks, and the backend fixes direction to
 * DIR_BOTH and rejects one-way settings. This is incompatible with arranging single-track paths in parallel.
 * Travel direction determines the physical track. Classification is centralized in isSingleRailPathVariant.
 * Combined double-track station variants define platform positions (side platforms outside or an island between tracks),
 * so do not offer platform side selection (hasPlatformSideChoice).
 *
 * Reuse existing composite path mechanisms (pathOffset / confirmPath) for expanded placement, previews, and costs.
 * Generated components have the same shape as combined_paths components.
 */

import type { MasterData, PathMasterEntry, CombinedPathComponent } from "../../types";
import {
  PATH_TRACK,
  PATH_MONORAIL,
  PATH_CONVEYOR,
  TRACK_NORMAL,
  TRACK_BRIDGE,
  TRACK_STATION,
  DIR_FORWARD,
  DIR_BACKWARD,
  DIR_BOTH,
  TRAFFIC_HAND_LEFT,
  PRODUCT_HIGH_ELECTRICITY,
} from "../../constants";

/** path_type values placed as tracks (railway, monorail, people movers). */
export const RAIL_TYPES: readonly number[] = [PATH_TRACK, PATH_MONORAIL, PATH_CONVEYOR];

/**
 * path_type values whose master lanes represents track count (railway/monorail).
 * Do not read road lanes, which counts traffic lanes. People movers always use one path regardless of lanes and are excluded.
 */
const TRACK_COUNT_PATH_TYPES: readonly number[] = [PATH_TRACK, PATH_MONORAIL];

/** Structure (track_type) order in variant lists: normal → bridge → station. */
const TRACK_TYPE_ORDER: readonly number[] = [TRACK_NORMAL, TRACK_BRIDGE, TRACK_STATION];

/** Minimum/maximum track counts for the panel stepper. */
export const MIN_TRACK_COUNT = 1;
export const MAX_TRACK_COUNT = 10;

/**
 * Fallback track spacing (m).
 * Default spacing is master width, placing corridors directly adjacent. Use this only if width is missing abnormally.
 */
export const FALLBACK_TRACK_SPACING = 4;

/** Spacing increment/decrement (m) per widen/narrow button press. */
export const TRACK_SPACING_STEP = 0.5;

/**
 * Adjustable track spacing range (multiples of default spacing = master width).
 * Allow down to half the width because adjacent-track combined rendering (trackbed fillers) also works with
 * overlapping corridors, enabling narrow, realistic double-track spacing.
 */
export const TRACK_SPACING_MIN_FACTOR = 0.5;
export const TRACK_SPACING_MAX_FACTOR = 3;

/**
 * Return effective spacing from default base (m) and adjustment delta (m).
 * Clamp to base multiplied by [TRACK_SPACING_MIN_FACTOR, TRACK_SPACING_MAX_FACTOR].
 */
export function clampTrackSpacing(base: number, delta: number): number {
  return Math.min(
    base * TRACK_SPACING_MAX_FACTOR,
    Math.max(base * TRACK_SPACING_MIN_FACTOR, base + delta)
  );
}

/** Whether path_type is a track category (railway, monorail, people movers). */
export function isRailType(type: number | null | undefined): boolean {
  return type != null && RAIL_TYPES.includes(type);
}

/** Return the variant's track_type (TRACK_NORMAL if undefined). */
export function variantTrackType(entry: PathMasterEntry | null | undefined): number {
  return entry?.track_type ?? TRACK_NORMAL;
}

/** Whether the variant is electrified (infra_products includes high-voltage power). */
export function isElectrifiedVariant(entry: PathMasterEntry | null | undefined): boolean {
  return !!entry?.infra_products?.includes(PRODUCT_HIGH_ELECTRICITY);
}

/**
 * Track count carried by one path: max(1, lanes) for railway/monorail, otherwise 1.
 * Same definition as backend MasterData.get_track_count.
 */
export function variantTrackCount(entry: PathMasterEntry | null | undefined): number {
  if (!entry || !TRACK_COUNT_PATH_TYPES.includes(entry.type)) return 1;
  return Math.max(1, entry.lanes ?? 1);
}

/**
 * Whether a railway variant represents both directions in one path (combined double track).
 * Same definition as backend MasterData.is_multi_track_variant; the backend fixes these variants to DIR_BOTH.
 */
export function isMultiTrackRailVariant(entry: PathMasterEntry | null | undefined): boolean {
  return variantTrackCount(entry) >= 2;
}

/**
 * Whether this station variant offers platform side selection (outside/inside/both) during construction
 * (single-track stations with platform_width). Combined double-track stations use variant-defined side/island positions,
 * so do not offer side selection or send platform_side.
 */
export function hasPlatformSideChoice(entry: PathMasterEntry | null | undefined): boolean {
  const width = entry?.platform_width;
  return typeof width === "number" && width > 0 && !isMultiTrackRailVariant(entry);
}

/**
 * Whether this track variant is placed as one DIR_BOTH path without parallel expansion
 * (people mover or combined double track). These have no track count/direction/spacing settings.
 */
export function isSingleRailPathVariant(entry: PathMasterEntry | null | undefined): boolean {
  return entry?.type === PATH_CONVEYOR || isMultiTrackRailVariant(entry);
}

/**
 * Return variants of a track type (railway/monorail/people mover, including combined double track) in construction-card order.
 * Structure (normal/bridge/station), electrification, and track standards (gauge/design speed, etc.) are all variant choices
 * selected directly by the player. Ground/elevated/underground placement depends on node heights, not variants,
 * and does not appear in the list.
 * Sort by structure (normal → bridge → station), single-track before combined double-track, nonelectrified before electrified,
 * then ascending ID. Within each structure, configurable parallel single tracks precede variants carrying both directions in one path.
 */
export function railVariantsInOrder(
  masterData: MasterData | null,
  railType: number,
): { id: number; data: PathMasterEntry }[] {
  if (!masterData?.paths) return [];
  const matches: { id: number; data: PathMasterEntry }[] = [];
  for (const key in masterData.paths) {
    const data = masterData.paths[key];
    if (data.type !== railType) continue;
    matches.push({ id: parseInt(key), data });
  }
  matches.sort((a, b) => {
    const ta = TRACK_TYPE_ORDER.indexOf(variantTrackType(a.data));
    const tb = TRACK_TYPE_ORDER.indexOf(variantTrackType(b.data));
    if (ta !== tb) return ta - tb;
    const ma = isMultiTrackRailVariant(a.data) ? 1 : 0;
    const mb = isMultiTrackRailVariant(b.data) ? 1 : 0;
    if (ma !== mb) return ma - mb;
    const ea = isElectrifiedVariant(a.data) ? 1 : 0;
    const eb = isElectrifiedVariant(b.data) ? 1 : 0;
    if (ea !== eb) return ea - eb;
    return a.id - b.id;
  });
  return matches;
}

/**
 * Return lateral offsets (m) from track count and spacing (m), centered evenly about the centerline.
 * Index 0 is leftmost (smallest offset), the last index rightmost.
 * Examples: count=2, spacing=4 → [-2, 2]; count=3, spacing=4 → [-4, 0, 4].
 */
export function computeLateralOffsets(count: number, spacing: number): number[] {
  const mid = (count - 1) / 2;
  return Array.from({ length: count }, (_, i) => (i - mid) * spacing);
}

/**
 * Return default directions from track count and city trafficHand.
 * Index 0 is leftmost (smallest offset), the last index rightmost.
 *
 * - Even count: half DIR_FORWARD, half DIR_BACKWARD. For TRAFFIC_HAND_LEFT the left half is forward;
 *   for right-hand traffic the right half is forward.
 * - Odd count: the center track is DIR_BOTH; divide the remaining tracks equally into forward/backward halves.
 *
 * Forward means geometric start → end (DIR_FORWARD), consistent with combined_paths presets
 * (double track: left forward/right backward; quadruple track: two left forward/two right backward).
 */
export function defaultTravelDirections(count: number, trafficHand: number): number[] {
  const mid = (count - 1) / 2;
  const leftIsForward = trafficHand === TRAFFIC_HAND_LEFT;
  const dirs: number[] = [];
  for (let i = 0; i < count; i++) {
    if (count % 2 === 1 && i === mid) {
      dirs.push(DIR_BOTH);
      continue;
    }
    const isLeft = i < mid;
    const forward = isLeft ? leftIsForward : !leftIsForward;
    dirs.push(forward ? DIR_FORWARD : DIR_BACKWARD);
  }
  return dirs;
}

/** Direction toggle cycle: forward → backward → both → forward … */
export function cycleDirection(dir: number): number {
  if (dir === DIR_FORWARD) return DIR_BACKWARD;
  if (dir === DIR_BACKWARD) return DIR_BOTH;
  return DIR_FORWARD;
}

/**
 * Generate centerline-expansion component definitions from track placement settings.
 * Components match combined_paths and can directly reuse confirmPath, cost estimation, and preview expansion logic.
 */
export function buildRailComponents(
  variantId: number,
  count: number,
  spacing: number,
  directions: number[]
): CombinedPathComponent[] {
  const offsets = computeLateralOffsets(count, spacing);
  return offsets.map((offset, i) => ({
    path_variant: variantId,
    lateral_offset: offset,
    height_offset: 0,
    travel_direction: directions[i] ?? DIR_BOTH,
  }));
}
