/**
 * Sea-floor generation for the terrain editor's "Automatic editing".
 *
 * Importing uniformly 0 m sea data such as GSI DEM leaves no sea, or extremely shallow sea,
 * in the game. Deepen sea cells according to coastline distance to generate a natural sea
 * floor continuously meeting land at elevation 0 m.
 *
 * Like terrainErosion.ts, contains only pure functions independent of React/canvas rendering.
 * Accepts a row-major Float32Array (rows × cols, spacing in meters) and modifies it in place.
 * The caller must rebuild textures and contours.
 *
 * Depth profile: depth(d) = maxDepth × tanh(slope × d / maxDepth).
 * At the coast (distance 0), depth is 0 with initial slope slope, meeting the land continuously.
 * Farther offshore, the gradient relaxes and depth approaches maxDepth. tanh provides smooth
 * saturation without kinks.
 */

import type { ErosionRegion } from "./terrainErosion";

/** Sea-floor generation parameters exposed in the UI. */
export type SeaFloorParams = {
  /** Cells at/below this elevation (meters) are sea. Leave at 0 for uniformly 0 m sea data. */
  seaLevelThreshold: number;
  /** Sea-floor slope (%) immediately offshore, relaxing toward maximum depth farther out. */
  shelfSlopePercent: number;
  /** Maximum depth (positive meters), never exceeded even far offshore. */
  maxDepth: number;
};

/**
 * Approximates Euclidean distance (meters) from each cell to the nearest land cell above
 * the threshold using a two-pass (forward/backward) chamfer distance transform; land has
 * distance 0. Propagates through eight neighbors with cardinal cost spacing and diagonal
 * cost spacing×√2. Error is within a few percent, sufficient for depth calculation.
 */
function computeDistanceToLand(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  seaLevelThreshold: number
): Float32Array {
  const dist = new Float32Array(rows * cols);
  for (let i = 0; i < dist.length; i++) {
    dist[i] = elev[i] > seaLevelThreshold ? 0 : Infinity;
  }

  const orth = spacing;
  const diag = spacing * Math.SQRT2;

  // Forward pass (top left → bottom right): propagate from left, above, upper-left, and upper-right neighbors.
  for (let r = 0; r < rows; r++) {
    const rowBase = r * cols;
    for (let c = 0; c < cols; c++) {
      const i = rowBase + c;
      let d = dist[i];
      if (d === 0) continue;
      if (c > 0) d = Math.min(d, dist[i - 1] + orth);
      if (r > 0) {
        d = Math.min(d, dist[i - cols] + orth);
        if (c > 0) d = Math.min(d, dist[i - cols - 1] + diag);
        if (c < cols - 1) d = Math.min(d, dist[i - cols + 1] + diag);
      }
      dist[i] = d;
    }
  }

  // Backward pass (bottom right → top left): propagate from right, below, lower-right, and lower-left neighbors.
  for (let r = rows - 1; r >= 0; r--) {
    const rowBase = r * cols;
    for (let c = cols - 1; c >= 0; c--) {
      const i = rowBase + c;
      let d = dist[i];
      if (d === 0) continue;
      if (c < cols - 1) d = Math.min(d, dist[i + 1] + orth);
      if (r < rows - 1) {
        d = Math.min(d, dist[i + cols] + orth);
        if (c < cols - 1) d = Math.min(d, dist[i + cols + 1] + diag);
        if (c > 0) d = Math.min(d, dist[i + cols - 1] + diag);
      }
      dist[i] = d;
    }
  }

  return dist;
}

/**
 * Applies sea-floor generation to elevations. Returns changed sea-cell count; 0 diagnoses
 * no qualifying sea cells, indicating the threshold may need adjustment.
 *
 * Sea classification (elevation <= threshold) and distance to land use the entire map.
 * region limits only which cells are modified. Restricting distance calculation would mistake
 * selection boundaries for coastlines and create false shallows at selection edges.
 * Select only sea areas to protect 0 m regions such as lakes that should remain inland.
 *
 * Replace target elevations with the depth profile rather than adding to them, so reruns
 * converge to the same profile even after parameter changes. Selection feather weights
 * (0..1) linearly interpolate current elevations and the profile.
 */
export function applySeaFloor(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  params: SeaFloorParams,
  region?: ErosionRegion
): number {
  const maxDepth = Math.max(1, params.maxDepth);
  const slope = Math.max(1e-4, params.shelfSlopePercent / 100);
  const threshold = params.seaLevelThreshold;

  const dist = computeDistanceToLand(elev, rows, cols, spacing, threshold);

  const rMin = region ? region.rMin : 0;
  const rMax = region ? region.rMax : rows - 1;
  const cMin = region ? region.cMin : 0;
  const cMax = region ? region.cMax : cols - 1;

  let modified = 0;
  for (let r = rMin; r <= rMax; r++) {
    const rowBase = r * cols;
    for (let c = cMin; c <= cMax; c++) {
      const i = rowBase + c;
      if (elev[i] > threshold) continue;
      const weight = region ? region.mask[i] : 1;
      if (weight <= 0) continue;
      // With no land cells, dist=Infinity → tanh(Infinity)=1 → maximum depth.
      const target = -maxDepth * Math.tanh((slope * dist[i]) / maxDepth);
      elev[i] += (target - elev[i]) * weight;
      modified++;
    }
  }
  return modified;
}
