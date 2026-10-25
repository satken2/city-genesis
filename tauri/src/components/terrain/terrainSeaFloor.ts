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
 *
 * Slope modes:
 * - "land": continue the slope of the coastal land offshore, so steep coasts have deep water nearby
 *   (natural harbors) and flat coasts stay shallow. Same rule as the core's heightmap import
 *   contract; port berth depths are judged on this sea floor.
 * - "fixed": one slope everywhere. Used with a selection to correct places the land rule misses,
 *   such as deep river channels beside flat land.
 */

import type { ErosionRegion } from "./terrainErosion";

export type SeaFloorSlopeMode = "land" | "fixed";

/** Sea-floor generation parameters exposed in the UI. */
export type SeaFloorParams = {
  /** Cells at/below this elevation (meters) are sea. Leave at 0 for uniformly 0 m sea data. */
  seaLevelThreshold: number;
  slopeMode: SeaFloorSlopeMode;
  /** Sea-floor slope (%) immediately offshore in "fixed" mode, relaxing toward maximum depth farther out. */
  shelfSlopePercent: number;
  /** Maximum depth (positive meters), never exceeded even far offshore. */
  maxDepth: number;
};

/** Half width (m) of the square around each coast cell whose land slope is averaged. */
const COAST_BAND_M = 200;
/** Lower bound of the sea-floor slope; keeps the sea floor below sea level even off flat reclaimed land. */
const MIN_SLOPE = 0.005;
/** Narrowest blur width (m) when extending coast slopes offshore; doubles per level. */
const SMOOTH_BASE_M = 50;
/** Number of blur levels (50 m to 6400 m). */
const SMOOTH_LEVELS = 8;
/** Blurred weights at or below this are treated as "no coast within reach". */
const WEIGHT_EPSILON = 1e-12;

/**
 * One-dimensional squared Euclidean distance transform (lower envelope of parabolas,
 * Felzenszwalb–Huttenlocher). Infinite inputs are left out of the envelope; a line with no
 * finite input stays infinite.
 */
function squaredDistance1D(f: Float64Array, n: number, out: Float64Array, v: Int32Array, z: Float64Array): void {
  let k = -1;
  for (let q = 0; q < n; q++) {
    if (f[q] === Infinity) continue;
    if (k < 0) {
      k = 0;
      v[0] = q;
      z[0] = -Infinity;
      z[1] = Infinity;
      continue;
    }
    let s = (f[q] + q * q - (f[v[k]] + v[k] * v[k])) / (2 * q - 2 * v[k]);
    while (s <= z[k]) {
      k--;
      s = (f[q] + q * q - (f[v[k]] + v[k] * v[k])) / (2 * q - 2 * v[k]);
    }
    k++;
    v[k] = q;
    z[k] = s;
    z[k + 1] = Infinity;
  }
  if (k < 0) {
    out.fill(Infinity, 0, n);
    return;
  }
  k = 0;
  for (let q = 0; q < n; q++) {
    while (z[k + 1] < q) k++;
    out[q] = (q - v[k]) * (q - v[k]) + f[v[k]];
  }
}

/**
 * Exact Euclidean distance (meters) from each cell to the nearest land cell above the
 * threshold; land has distance 0. The core uses the same distance definition.
 */
function computeDistanceToLand(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  seaLevelThreshold: number
): Float32Array {
  const n = Math.max(rows, cols);
  const line = new Float64Array(n);
  const out = new Float64Array(n);
  const v = new Int32Array(n);
  const z = new Float64Array(n + 1);
  const squared = new Float64Array(rows * cols);
  for (let c = 0; c < cols; c++) {
    for (let r = 0; r < rows; r++) line[r] = elev[r * cols + c] > seaLevelThreshold ? 0 : Infinity;
    squaredDistance1D(line, rows, out, v, z);
    for (let r = 0; r < rows; r++) squared[r * cols + c] = out[r];
  }
  const dist = new Float32Array(rows * cols);
  for (let r = 0; r < rows; r++) {
    const base = r * cols;
    for (let c = 0; c < cols; c++) line[c] = squared[base + c];
    squaredDistance1D(line, cols, out, v, z);
    for (let c = 0; c < cols; c++) dist[base + c] = Math.sqrt(out[c]) * spacing;
  }
  return dist;
}

/**
 * In-place mean over a (2·radius+1)-cell window along rows and then columns. Cells outside the
 * map count as 0 and the divisor stays the full width, so callers blur a weight field the same
 * way and divide. Prefix sums (not sliding sums) keep windows of exact zeros at exactly 0.
 */
function boxMean2D(values: Float32Array, rows: number, cols: number, radius: number): void {
  if (radius <= 0) return;
  const width = 2 * radius + 1;
  const rowPrefix = new Float64Array(cols + 1);
  for (let r = 0; r < rows; r++) {
    const base = r * cols;
    for (let c = 0; c < cols; c++) rowPrefix[c + 1] = rowPrefix[c] + values[base + c];
    for (let c = 0; c < cols; c++) {
      values[base + c] = (rowPrefix[Math.min(cols, c + radius + 1)] - rowPrefix[Math.max(0, c - radius)]) / width;
    }
  }
  const colPrefix = new Float64Array(rows + 1);
  for (let c = 0; c < cols; c++) {
    for (let r = 0; r < rows; r++) colPrefix[r + 1] = colPrefix[r] + values[r * cols + c];
    for (let r = 0; r < rows; r++) {
      values[r * cols + c] = (colPrefix[Math.min(rows, r + radius + 1)] - colPrefix[Math.max(0, r - radius)]) / width;
    }
  }
}

/** In-place Gaussian blur of standard deviation sigmaCells, approximated by three equal box means. */
function gaussianLike(values: Float32Array, rows: number, cols: number, sigmaCells: number): void {
  const radius = Math.max(1, Math.floor((Math.sqrt(4 * sigmaCells * sigmaCells + 1) - 1) / 2 + 0.5));
  for (let pass = 0; pass < 3; pass++) boxMean2D(values, rows, cols, radius);
}

/**
 * Slope each sea cell uses in "land" mode, before MIN_SLOPE is applied.
 *  1. Coast slope: mean land slope within COAST_BAND_M of each coast cell (land touching sea on a
 *     side). Coast cells themselves are excluded because their gradient includes the step to sea level.
 *  2. Sea slope: coast slopes blurred with a width equal to the distance to the coast, so wider
 *     stretches of coast are averaged farther offshore and no straight seams extend from where
 *     steep and flat coasts meet.
 */
function extendedCoastSlope(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  threshold: number,
  dist: Float32Array
): Float32Array {
  const n = rows * cols;
  const ground = (i: number) => Math.max(elev[i], threshold);
  const isSea = (i: number) => elev[i] <= threshold;
  const coast = new Uint8Array(n);
  const innerSum = new Float32Array(n);
  const innerCount = new Float32Array(n);
  for (let r = 0; r < rows; r++) {
    for (let c = 0; c < cols; c++) {
      const i = r * cols + c;
      if (isSea(i)) continue;
      const seaSide =
        (r > 0 && isSea(i - cols)) || (r < rows - 1 && isSea(i + cols)) ||
        (c > 0 && isSea(i - 1)) || (c < cols - 1 && isSea(i + 1));
      if (seaSide) {
        coast[i] = 1;
        continue;
      }
      // 中央差分を使い、マップ端では片側差分を使う。
      const gx = c === 0 ? (ground(i + 1) - ground(i)) / spacing
        : c === cols - 1 ? (ground(i) - ground(i - 1)) / spacing
        : (ground(i + 1) - ground(i - 1)) / (2 * spacing);
      const gz = r === 0 ? (ground(i + cols) - ground(i)) / spacing
        : r === rows - 1 ? (ground(i) - ground(i - cols)) / spacing
        : (ground(i + cols) - ground(i - cols)) / (2 * spacing);
      innerSum[i] = Math.hypot(gx, gz);
      innerCount[i] = 1;
    }
  }
  const band = Math.floor(COAST_BAND_M / spacing + 0.5);
  boxMean2D(innerSum, rows, cols, band);
  boxMean2D(innerCount, rows, cols, band);

  // Coast cells with no inner land nearby (reefs, pier tips) have no slope and stay out of the average.
  const coastWeight = new Float32Array(n);
  const coastValue = new Float32Array(n);
  for (let i = 0; i < n; i++) {
    if (coast[i] && innerCount[i] > WEIGHT_EPSILON) {
      coastWeight[i] = 1;
      coastValue[i] = innerSum[i] / innerCount[i];
    }
  }

  const slope = new Float32Array(n);
  const want = new Float32Array(n);
  const neededLevels = new Uint8Array(SMOOTH_LEVELS);
  for (let i = 0; i < n; i++) {
    if (!isSea(i)) continue;
    const w = Math.min(SMOOTH_LEVELS - 1, Math.max(0, Math.log2(Math.max(dist[i], SMOOTH_BASE_M) / SMOOTH_BASE_M)));
    want[i] = w;
    const lower = Math.floor(w);
    neededLevels[lower] = 1;
    if (lower + 1 < SMOOTH_LEVELS && w > lower) neededLevels[lower + 1] = 1;
  }
  const weight = innerCount;
  const value = innerSum;
  for (let level = 0; level < SMOOTH_LEVELS; level++) {
    if (!neededLevels[level]) continue;
    weight.set(coastWeight);
    value.set(coastValue);
    const sigmaCells = (SMOOTH_BASE_M * 2 ** level) / spacing;
    gaussianLike(weight, rows, cols, sigmaCells);
    gaussianLike(value, rows, cols, sigmaCells);
    for (let i = 0; i < n; i++) {
      if (!isSea(i)) continue;
      const lower = Math.floor(want[i]);
      const upperShare = want[i] - lower;
      const share = lower === level ? 1 - upperShare : lower + 1 === level ? upperShare : 0;
      if (share === 0) continue;
      slope[i] += share * (weight[i] > WEIGHT_EPSILON ? value[i] / weight[i] : 0);
    }
  }
  return slope;
}

/**
 * Applies sea-floor generation to elevations. Returns changed sea-cell count; 0 diagnoses
 * no qualifying sea cells, indicating the threshold may need adjustment.
 *
 * Sea classification (elevation <= threshold), distance to land and coast slopes use the entire map.
 * region limits only which cells are modified. Restricting the calculation would mistake
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
  const fixedSlope = Math.max(1e-4, params.shelfSlopePercent / 100);
  const threshold = params.seaLevelThreshold;

  const dist = computeDistanceToLand(elev, rows, cols, spacing, threshold);
  const landSlope = params.slopeMode === "land" ? extendedCoastSlope(elev, rows, cols, spacing, threshold, dist) : null;

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
      const slope = landSlope ? Math.max(landSlope[i], MIN_SLOPE) : fixedSlope;
      // With no land cells, dist=Infinity → tanh(Infinity)=1 → maximum depth.
      const target = -maxDepth * Math.tanh((slope * dist[i]) / maxDepth);
      elev[i] += (target - elev[i]) * weight;
      modified++;
    }
  }
  return modified;
}
