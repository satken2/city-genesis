/**
 * Footprint geometry of the wings (sub-buildings) composing a mashup building, shared by the placement editor
 * and the city map.
 */

import type { SubBuildingMasterEntry } from "../types";
import { MASHUP_CELL_SIZE } from "../constants";

/**
 * Placement entry for one sub-building (sub_buildings in GET /buildings and the format of
 * PUT /buildings/{id}/sub-buildings).
 */
export type SubBuildingPlacement = {
  subbuilding_id: number;
  /** Relative XZ coordinates [x, z] from the building center. */
  position: number[];
  /** Rotation in the XZ plane (degrees, counterclockwise from +X). */
  rotation: number;
};

/**
 * Sub-building footprint dimensions (m). Master width / height use cells (1 unit = MASHUP_CELL_SIZE m).
 * Treat undefined dimensions as one cell, following the backend convention.
 */
export function subbuildingSize(entry: SubBuildingMasterEntry | undefined): [number, number] {
  return [(entry?.width ?? 1) * MASHUP_CELL_SIZE, (entry?.height ?? 1) * MASHUP_CELL_SIZE];
}

/**
 * Return the four world-space corners of a placement entry's footprint rectangle.
 * The rotation axes u (width) = (cosθ, sinθ), v (depth) = (-sinθ, cosθ)
 * follow the backend cell grid convention (generate_mashup_cells).
 */
export function placementCorners(
  placement: SubBuildingPlacement,
  centerPoint: number[],
  entry: SubBuildingMasterEntry | undefined,
): [number, number][] {
  const [width, height] = subbuildingSize(entry);
  const rad = (placement.rotation * Math.PI) / 180;
  const ux = Math.cos(rad);
  const uz = Math.sin(rad);
  const vx = -uz;
  const vz = ux;
  const cx = centerPoint[0] + placement.position[0];
  const cz = centerPoint[1] + placement.position[1];
  const hw = width / 2;
  const hh = height / 2;
  return [
    [cx - ux * hw - vx * hh, cz - uz * hw - vz * hh],
    [cx + ux * hw - vx * hh, cz + uz * hw - vz * hh],
    [cx + ux * hw + vx * hh, cz + uz * hw + vz * hh],
    [cx - ux * hw + vx * hh, cz - uz * hw + vz * hh],
  ];
}
