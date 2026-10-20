/**
 * River generation algorithms for the terrain editor's "River editing".
 *
 * Like terrainErosion.ts, contains only pure functions called by TerrainEditorCanvas,
 * independent of React/canvas rendering. Accepts the editor's row-major Float32Array unchanged
 * (rows × cols, spacing in meters, elevations <=0 treated as sea). World coordinates use
 * worldX = col*spacing - mapWidth/2, with the origin at map center.
 *
 * Three processing stages support the UI's two-stage "Calculate courses (preview)" →
 * "Carve riverbeds and confirm" workflow:
 *   1. extractRiverNetwork: automatically identifies courses without changing elevations.
 *      Downsample → priority-flood depression fill → D8 flow direction → flow accumulation
 *      → extract above-threshold discharge cells as a river network, separating main stems
 *      and tributaries. Accepts rainfall (mm/year) and rainfall region (selection mask).
 *   2. evolveRivers: develops meanders on flat sections through curvature-driven lateral
 *      migration (a simplified Howard-Knutson model), then finalizes water elevation profiles,
 *      monotonically non-increasing downstream, without changing terrain elevations.
 *   3. carveRivers: carves riverbeds along finalized courses into elevations in place, reproducing
 *      asymmetric sections with deep, steep outer bends and braided sandbars in low-gradient,
 *      high-discharge sections.
 *
 * The editor handles conversion to RiverPayload (terrainData.ts).
 * The authoritative specification is D.8 in city-genesis/docs/ja/geometries/D_地形形状.md.
 */

import type { WaterSurfacePayload, WaterPolygonGeoJson, GridRect } from "./terrainData";
import type { ErosionRegion } from "./terrainErosion";
import {
  RiverProfileSampler,
  extractRegionPolygon,
  pointInPolygon,
  polygonBounds,
  polygonRings,
  rasterizePolygonMask,
} from "./riverGeometry";

/** River generation parameters exposed in the UI. */
export type RiverGenParams = {
  /** Annual rainfall (mm). More rain produces more tributaries and wider rivers on the same terrain. */
  rainfallMmPerYear: number;
  /** Minimum river discharge (m³/s). Cells whose flow accumulation exceeds this become rivers. */
  minDischargeM3s: number;
  /** Meander strength (0..1); 0 disables meander evolution. */
  meanderStrength: number;
  /** Meander evolution iterations. More iterations grow the meander wavelength. */
  meanderIterations: number;
  /** Width coefficient. width = widthCoef × discharge^0.5 (hydraulic geometry). */
  widthCoef: number;
  /** Depth coefficient. depth = depthCoef × discharge^0.4 (hydraulic geometry). */
  depthCoef: number;
  /** Whether to form sandbars (braided channels) in low-gradient, high-discharge sections. */
  enableBraiding: boolean;
};

/** Centerline point in world XZ, with absolute elevation (meters, sea level = 0). */
export type RiverPoint = {
  x: number;
  z: number;
  /** Discharge at this point (m³/s), increasing at each confluence. */
  q: number;
  /** Channel width reflecting terrain confinement (meters). Water edges are intersections with carved terrain. */
  width: number;
  /** Water Y elevation (meters), monotonically non-increasing downstream. */
  waterY: number;
  /** Riverbed Y elevation (meters) at the deepest point (thalweg): waterY - depth. */
  bedY: number;
  /** Dimensionless smoothed curvature, clamped to ±0.8. Positive = left bend; used for asymmetric sections. */
  curvature: number;
  /** Whether this is a braided section with sandbars. */
  braided: boolean;
};

/** River endpoint (mouth) type. */
export type RiverMouthKind = "sea" | "lake" | "edge" | "confluence";

/** extractRiverNetwork result with diagnostic information. */
export type RiverExtractionResult = {
  rivers: GeneratedRiver[];
  /** Maximum discharge reached on land cells (m³/s). With zero courses, diagnoses a threshold beyond
   *  maximum discharge in the UI, prompting threshold/rainfall adjustment. */
  maxDischargeM3s: number;
  /** Rivers discarded for being too short. If there are no courses despite maximum discharge exceeding
   *  the threshold, these short above-threshold sections are the cause; the UI needs a different
   *  hint from insufficient discharge. */
  discardedShortCount: number;
};

/** One generated river, with points ordered upstream to downstream. */
export type GeneratedRiver = {
  id: string;
  points: RiverPoint[];
  mouth: RiverMouthKind;
  /** With mouth === "lake", receiving lake level (meters), used to clamp the water profile at the mouth. */
  mouthWaterLevel?: number;
  /** With mouth === "confluence", the receiving river and nearest-point index. */
  confluence?: { riverId: string; pointIndex: number };
};

/* ============================================================================
 * Internal constants.
 *
 * Exposing everything would create too many controls, so only influential settings are
 * parameters in RiverGenParams; the rest remain fixed here.
 * ========================================================================== */

const RIVER = {
  /** Maximum grid side length in cells for downsampled course identification, keeping synchronous JS within seconds even for 5000² maps. */
  maxDsDim: 2048,
  /** priority-flood fill gradient (meters), ensuring a unique downstream direction even in flat areas. */
  fillEpsilon: 1e-4,
  /** Runoff coefficient: fraction of rainfall becoming river discharge. */
  runoffCoef: 0.4,
  /** Bankfull conversion factor, multiplying annual mean flow to obtain channel-forming discharge.
   *  River appearance and channel dimensions (width/depth) depend on high-water/flood flow,
   *  tens of times the annual mean in small basins. Using the mean alone gives maximum flow
   *  below 0.1 m³/s on islands a few km across, yielding no rivers with realistic thresholds.
   *  Hydraulic geometry (width ∝ Q^0.5) also describes bankfull discharge, so consistently use
   *  this converted flow as discharge. */
  bankfullFactor: 30,
  /** Centerline polyline resampling interval as a multiple of spacing. */
  resampleFactor: 2,
  /** Discard rivers with fewer points, since extremely short streams are not worth drawing/carving.
   *  Keep this modest to retain short rivers on islands a few km across. */
  minRiverPoints: 10,
  /** Minimum depth (meters). */
  minDepth: 0.5,
  /** Meandering: base lateral migration speed coefficient at meanderStrength=1. */
  migrationCoef: 1.0,
  /** Meandering: bend growth saturation. Bends whose radius falls to this multiple of river width
   *  stop growing, corresponding to real minimum radii of about 2–3 widths and preventing runaway growth. */
  saturationRadiusWidths: 3,
  /** Meandering: local-curvature fraction blended into smoothed curvature (0 = upstream EMA only,
   *  1 = local only). Local curvature grows amplitude; upstream curvature moves bend apices downstream. */
  curvatureLocalMix: 0.6,
  /** Meandering: initial perturbation amplitude and wavelength as multiples of river width.
   *  Curvature-driven evolution cannot grow from a perfectly straight zero-curvature line,
   *  so seed small lateral fluctuations representing natural riverbed irregularities. */
  seedAmpWidths: 0.3,
  seedWavelengthWidths: 12,
  /** Meandering: upstream curvature decay distance in river widths; approximates HK convolution with an exponential moving average. */
  curvatureLagWidths: 10,
  /** Meandering: maximum lateral movement per iteration as a multiple of resampling interval, preventing numerical runaway. */
  maxStepFactor: 0.4,
  /** Meandering: sections whose water-surface longitudinal slope reaches this threshold do not meander (steep mountain streams). */
  maxMeanderSlope: 0.01,
  /** Meandering neck cutoff: remove loops when point pairs separated by course distance > neckPathWidths × width
   *  approach within Euclidean distance < neckCutWidths × width. */
  neckPathWidths: 5,
  neckCutWidths: 1.5,
  /** Water profile: minimum longitudinal slope (m/m), preventing a perfectly flat surface. */
  minSurfaceSlope: 5e-5,
  /** Water profile: maximum upstream return slope from the mouth (m/m), blending the mouth clamp. */
  maxMouthSlope: 0.005,
  /** Cross section: dimensionless curvature clamp, affecting outer-bank thalweg shift and pool deepening. */
  maxNormCurvature: 0.8,
  /** Cross section: outer-bank thalweg displacement fraction, proportional to dimensionless curvature. */
  thalwegShift: 0.6,
  /** Cross section: bend pool deepening coefficient. depth × (1 + poolDeepen × |κ|). */
  poolDeepen: 0.5,
  /** Carving: bank shoulder height above water (meters), beyond which the outer blending band transitions to existing terrain. */
  freeboard: 0.7,
  /** Carving: channel bank slope tangent, about 35°. Taking min with the V-shaped section profile
   *  raises steep bank edges like the walls visible when a real river is drained. */
  bankSlope: 0.7,
  /** Carving: cross-section exponent, depth = d × (1 - |x|^p).
   *  p=1 is a linear V, but gentle slopes near the bottom look like a soft depression.
   *  p<1 makes slopes steep immediately above the thalweg, cutting a distinct sharp notch
   *  visible even on coarse grids. */
  crossSectionExponent: 0.5,
  /** Carving: blending band width (multiple of river width and minimum multiple of spacing).
   *  Keep it minimal for a quick transition from bank shoulder to existing terrain; wide bands
   *  would create broad bowl-shaped depressions around rivers. */
  blendWidthFactor: 0.3,
  blendMinSpacingFactor: 1.5,
  /** Braiding thresholds: maximum water slope, minimum width (meters), and minimum continuous points.
   *  The width minimum corresponds to 36 m³/s with widthCoef=5. Medium gravel-bed rivers
   *  also braid (e.g. Japanese alluvial-fan rivers), so do not restrict this to large rivers. */
  braidMaxSlope: 0.002,
  braidMinWidth: 30,
  braidMinRun: 4,
  /** Braiding: bar height as a multiple of depth, and bar-top depth below water (meters, submerged bars). */
  braidBarHeightFactor: 0.9,
  braidBarClipBelowWater: 0.3,
  /** Braiding: cross-sectional bar placement range (|tn| below this value), keeping two outer branches low. */
  braidBarExtent: 0.55,
  /** Braiding: downstream wavelength of bar-field noise as a multiple of river width. */
  braidNoiseWavelengthWidths: 3,
  /** Area extraction: margin below water elevation (meters). Excludes grid points exactly at
   *  water level in the bank blending band, clipping the shoreline slightly below the surface. */
  polygonWaterEps: 0.05,
  /** Area extraction: ring simplification tolerance as a multiple of spacing, removing small
   *  grid-derived jagged edges while preserving bank shape. */
  polygonSimplifyFactor: 0.02,
  /** Control points: tolerance for simplifying generated centerlines, as a multiple of spacing,
   *  reducing nodes while preserving meander shape. */
  controlPointToleranceFactor: 0.75,
  /** Control points: relative discharge jump always retained in simplification, fixing tributary inflow/confluence points. */
  controlPointQJump: 0.1,
  /** Control points: minimum confluence detection distance as a multiple of resampling interval.
   *  The snap radius is the larger of this and the receiving river's width. */
  confluenceSnapDsFactor: 2,
} as const;

/** Seconds per year, used to convert discharge to m³/s. */
const SECONDS_PER_YEAR = 365 * 24 * 3600;

/** D8 eight-neighbor offsets (row, column) and horizontal distance factors. */
const D8_OFFSETS: ReadonlyArray<{ dr: number; dc: number; dist: number }> = [
  { dr: -1, dc: 0, dist: 1 },
  { dr: 1, dc: 0, dist: 1 },
  { dr: 0, dc: -1, dist: 1 },
  { dr: 0, dc: 1, dist: 1 },
  { dr: -1, dc: -1, dist: Math.SQRT2 },
  { dr: -1, dc: 1, dist: Math.SQRT2 },
  { dr: 1, dc: -1, dist: Math.SQRT2 },
  { dr: 1, dc: 1, dist: Math.SQRT2 },
];

/* ============================================================================
 * General helpers
 * ========================================================================== */

/** Deterministic pseudorandom [-1,1] from integer grid coordinates, for sandbar-field noise. */
function hashNoise2D(ix: number, iz: number): number {
  let n = ix * 374761393 + iz * 668265263;
  n = (n ^ (n >> 13)) * 1274126177;
  n = n ^ (n >> 16);
  return ((n & 0x7fffffff) / 0x7fffffff) * 2 - 1;
}

/** Value noise [-1,1] smoothly interpolating hashNoise2D at grid points. */
function valueNoise2D(x: number, z: number): number {
  const x0 = Math.floor(x);
  const z0 = Math.floor(z);
  const sx = x - x0;
  const sz = z - z0;
  const fx = sx * sx * (3 - 2 * sx);
  const fz = sz * sz * (3 - 2 * sz);
  const n00 = hashNoise2D(x0, z0);
  const n10 = hashNoise2D(x0 + 1, z0);
  const n01 = hashNoise2D(x0, z0 + 1);
  const n11 = hashNoise2D(x0 + 1, z0 + 1);
  const ix0 = n00 + (n10 - n00) * fx;
  const ix1 = n01 + (n11 - n01) * fx;
  return ix0 + (ix1 - ix0) * fz;
}

/** Bilinearly interpolates full-resolution elevation at world (wx, wz), clamping out-of-bounds coordinates. */
function sampleElevAtWorld(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  wx: number,
  wz: number,
): number {
  const gx = Math.min(Math.max((wx + mapWidth / 2) / spacing, 0), cols - 1.001);
  const gz = Math.min(Math.max((wz + mapHeight / 2) / spacing, 0), rows - 1.001);
  const cx = Math.floor(gx);
  const cz = Math.floor(gz);
  const fx = gx - cx;
  const fz = gz - cz;
  const base = cz * cols + cx;
  const h00 = elev[base];
  const h10 = elev[base + 1];
  const h01 = elev[base + cols];
  const h11 = elev[base + cols + 1];
  return h00 * (1 - fx) * (1 - fz) + h10 * fx * (1 - fz) + h01 * (1 - fx) * fz + h11 * fx * fz;
}

/** Ray-casting point-in-polygon test, with vertices as [x, z] arrays. */
function pointInRing(x: number, z: number, ring: number[][]): boolean {
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const xi = ring[i][0];
    const zi = ring[i][1];
    const xj = ring[j][0];
    const zj = ring[j][1];
    const intersects = zi > z !== zj > z && x < ((xj - xi) * (z - zi)) / (zj - zi) + xi;
    if (intersects) inside = !inside;
  }
  return inside;
}

/* ============================================================================
 * Binary min-heap for priority-flood.
 *
 * Store keys (filled elevations) in Float64. With float32, adding fillEpsilon (1e-4 m) to
 * elevations of thousands of meters rounds away, breaking flow directions in flat areas.
 * ========================================================================== */

class MinHeap {
  private idx: Int32Array;
  private key: Float64Array;
  private n = 0;

  constructor(capacity: number) {
    this.idx = new Int32Array(capacity);
    this.key = new Float64Array(capacity);
  }

  get size(): number {
    return this.n;
  }

  push(index: number, key: number): void {
    let i = this.n++;
    this.idx[i] = index;
    this.key[i] = key;
    while (i > 0) {
      const parent = (i - 1) >> 1;
      if (this.key[parent] <= this.key[i]) break;
      this.swap(i, parent);
      i = parent;
    }
  }

  /** Extracts the index of the element with the minimum key. */
  pop(): number {
    const top = this.idx[0];
    this.n--;
    if (this.n > 0) {
      this.idx[0] = this.idx[this.n];
      this.key[0] = this.key[this.n];
      let i = 0;
      for (;;) {
        const l = i * 2 + 1;
        const r = l + 1;
        let smallest = i;
        if (l < this.n && this.key[l] < this.key[smallest]) smallest = l;
        if (r < this.n && this.key[r] < this.key[smallest]) smallest = r;
        if (smallest === i) break;
        this.swap(i, smallest);
        i = smallest;
      }
    }
    return top;
  }

  private swap(a: number, b: number): void {
    const ti = this.idx[a];
    this.idx[a] = this.idx[b];
    this.idx[b] = ti;
    const tk = this.key[a];
    this.key[a] = this.key[b];
    this.key[b] = tk;
  }
}

/* ============================================================================
 * [1] Course identification (extractRiverNetwork)
 * ========================================================================== */

/** Downsampled grid and all hydrological quantities computed on it (extractRiverNetwork internal state). */
type HydroGrid = {
  dsRows: number;
  dsCols: number;
  stride: number;
  /** Block-min downsampled elevation, with lakes replaced by water levels. */
  dsElev: Float32Array;
  /** Lake index for each cell (-1 = not a lake). */
  lakeIdx: Int32Array;
  /** Elevations after priority-flood (Float64, including ε gradient). */
  filled: Float64Array;
  /** D8 flow direction (D8_OFFSETS index; -1 = no outlet). */
  flowDir: Int8Array;
  /** Discharge (m³/s). */
  discharge: Float64Array;
  /** Downsampled cell → world coordinates. */
  cellToWorldX: (c: number) => number;
  cellToWorldZ: (r: number) => number;
};

/**
 * Builds a downsampled grid, then replaces lake levels, applies priority-flood, computes D8
 * directions, and accumulates flow.
 *
 * Downsampling uses each stride×stride block's minimum, preserving narrow valley floors;
 * averages or point subsampling would erase one-cell valleys and put courses on ridges.
 * Replace cells inside water_surfaces polygons with lake water levels before filling. Flat
 * lake surfaces plus ε naturally route incoming water out through spill points, matching physics.
 *
 * With rainRegion, rain falls only within its mask (initial accumulation is the block-average
 * mask weight). Flow continues outside, so rivers sourced in the region naturally extend to sea.
 */
function buildHydroGrid(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  params: RiverGenParams,
  lakes: WaterSurfacePayload[],
  rainRegion?: ErosionRegion,
): HydroGrid {
  const stride = Math.max(1, Math.ceil(Math.max(rows, cols) / RIVER.maxDsDim));
  const dsRows = Math.ceil(rows / stride);
  const dsCols = Math.ceil(cols / stride);
  const dsCount = dsRows * dsCols;
  const halfW = mapWidth / 2;
  const halfH = mapHeight / 2;

  // World coordinates of block centers; match original grid points when stride=1.
  const cellToWorldX = (c: number) => (c * stride + (stride - 1) / 2) * spacing - halfW;
  const cellToWorldZ = (r: number) => (r * stride + (stride - 1) / 2) * spacing - halfH;

  // --- Downsample (block minimum) + rainfall weights (block average) ---
  const dsElev = new Float32Array(dsCount);
  const rainWeight = rainRegion ? new Float32Array(dsCount) : null;
  for (let dr = 0; dr < dsRows; dr++) {
    const r0 = dr * stride;
    const r1 = Math.min(r0 + stride, rows);
    for (let dc = 0; dc < dsCols; dc++) {
      const c0 = dc * stride;
      const c1 = Math.min(c0 + stride, cols);
      let mn = Infinity;
      let wSum = 0;
      let wCount = 0;
      for (let r = r0; r < r1; r++) {
        const rowBase = r * cols;
        for (let c = c0; c < c1; c++) {
          const e = elev[rowBase + c];
          if (e < mn) mn = e;
          if (rainRegion) {
            wSum += rainRegion.mask[rowBase + c];
            wCount++;
          }
        }
      }
      const di = dr * dsCols + dc;
      dsElev[di] = mn;
      if (rainWeight) rainWeight[di] = wCount > 0 ? wSum / wCount : 0;
    }
  }

  // --- Replace lake water levels ---
  const lakeIdx = new Int32Array(dsCount).fill(-1);
  for (let li = 0; li < lakes.length; li++) {
    const lake = lakes[li];
    if (!lake?.polygon || lake.polygon.type !== "Polygon") continue;
    const ring = (lake.polygon.coordinates as number[][][])[0];
    if (!Array.isArray(ring) || ring.length < 3) continue;
    let minX = Infinity;
    let maxX = -Infinity;
    let minZ = Infinity;
    let maxZ = -Infinity;
    for (const p of ring) {
      if (p[0] < minX) minX = p[0];
      if (p[0] > maxX) maxX = p[0];
      if (p[1] < minZ) minZ = p[1];
      if (p[1] > maxZ) maxZ = p[1];
    }
    const dsSpacing = stride * spacing;
    const cMin = Math.max(0, Math.floor((minX + halfW) / dsSpacing));
    const cMax = Math.min(dsCols - 1, Math.ceil((maxX + halfW) / dsSpacing));
    const rMin = Math.max(0, Math.floor((minZ + halfH) / dsSpacing));
    const rMax = Math.min(dsRows - 1, Math.ceil((maxZ + halfH) / dsSpacing));
    for (let dr = rMin; dr <= rMax; dr++) {
      for (let dc = cMin; dc <= cMax; dc++) {
        if (!pointInRing(cellToWorldX(dc), cellToWorldZ(dr), ring)) continue;
        const di = dr * dsCols + dc;
        lakeIdx[di] = li;
        dsElev[di] = lake.water_level;
      }
    }
  }

  // --- priority-flood depression fill with ε (Barnes 2014) ---
  // Seeds: all sea cells (elevation <=0) plus map boundary cells, allowing outflow at map edges.
  // Propagate filled = max(original elevation, filled[cur] + ε) from each popped lowest cell to neighbors.
  const filled = new Float64Array(dsCount);
  const visited = new Uint8Array(dsCount);
  const popOrder = new Int32Array(dsCount);
  let popCount = 0;
  const heap = new MinHeap(dsCount);

  const pushSeed = (di: number) => {
    if (visited[di]) return;
    visited[di] = 1;
    filled[di] = dsElev[di];
    heap.push(di, filled[di]);
  };
  for (let di = 0; di < dsCount; di++) {
    if (dsElev[di] <= 0) pushSeed(di);
  }
  for (let dc = 0; dc < dsCols; dc++) {
    pushSeed(dc);
    pushSeed((dsRows - 1) * dsCols + dc);
  }
  for (let dr = 0; dr < dsRows; dr++) {
    pushSeed(dr * dsCols);
    pushSeed(dr * dsCols + dsCols - 1);
  }

  while (heap.size > 0) {
    const cur = heap.pop();
    popOrder[popCount++] = cur;
    const r = Math.floor(cur / dsCols);
    const c = cur - r * dsCols;
    for (const o of D8_OFFSETS) {
      const nr = r + o.dr;
      const nc = c + o.dc;
      if (nr < 0 || nr >= dsRows || nc < 0 || nc >= dsCols) continue;
      const ni = nr * dsCols + nc;
      if (visited[ni]) continue;
      visited[ni] = 1;
      filled[ni] = Math.max(dsElev[ni], filled[cur] + RIVER.fillEpsilon);
      heap.push(ni, filled[ni]);
    }
  }

  // --- D8 flow direction (steepest descent on filled elevations) ---
  // Filled elevations have no local minima: every non-seed has a lower neighbor,
  // so every cell can descend to sea, a lake, or the map edge.
  const flowDir = new Int8Array(dsCount).fill(-1);
  for (let r = 0; r < dsRows; r++) {
    for (let c = 0; c < dsCols; c++) {
      const di = r * dsCols + c;
      let best = -1;
      let bestSlope = 0;
      for (let oi = 0; oi < D8_OFFSETS.length; oi++) {
        const o = D8_OFFSETS[oi];
        const nr = r + o.dr;
        const nc = c + o.dc;
        if (nr < 0 || nr >= dsRows || nc < 0 || nc >= dsCols) continue;
        const ni = nr * dsCols + nc;
        const slope = (filled[di] - filled[ni]) / o.dist;
        if (slope > bestSlope) {
          bestSlope = slope;
          best = oi;
        }
      }
      flowDir[di] = best;
    }
  }

  // --- Flow accumulation ---
  // popOrder is ascending filled elevation; traverse in reverse, highest first,
  // so all upstream contributions are accumulated before processing a cell, without sorting.
  const acc = new Float64Array(dsCount);
  if (rainWeight) {
    acc.set(rainWeight);
  } else {
    acc.fill(1);
  }
  for (let k = popCount - 1; k >= 0; k--) {
    const di = popOrder[k];
    const d = flowDir[di];
    if (d < 0) continue;
    const o = D8_OFFSETS[d];
    const r = Math.floor(di / dsCols);
    const c = di - r * dsCols;
    acc[(r + o.dr) * dsCols + (c + o.dc)] += acc[di];
  }

  // Accumulated cell count → discharge (m³/s). Annual mean flow is rainfall × catchment area × runoff coefficient
  // divided by seconds per year; multiply by bankfullFactor to obtain channel-forming discharge.
  const cellArea = (stride * spacing) * (stride * spacing);
  const qFactor =
    (cellArea * params.rainfallMmPerYear * 1e-3 * RIVER.runoffCoef * RIVER.bankfullFactor) / SECONDS_PER_YEAR;
  const discharge = new Float64Array(dsCount);
  for (let di = 0; di < dsCount; di++) discharge[di] = acc[di] * qFactor;

  return { dsRows, dsCols, stride, dsElev, lakeIdx, filled, flowDir, discharge, cellToWorldX, cellToWorldZ };
}

/**
 * Computes width (meters) from discharge: hydraulic geometry w ∝ Q^0.5.
 * No minimum width; sub-grid channels can be carved with carveRivers' grid-footprint correction.
 * Terrain is represented only by grid-point elevations, however, so actual grooves remain
 * at least about one grid spacing wide.
 */
function widthFromDischarge(q: number, params: RiverGenParams): number {
  return params.widthCoef * Math.sqrt(Math.max(0, q));
}

/** Computes depth (meters) from discharge: hydraulic geometry d ∝ Q^0.4. */
function depthFromDischarge(q: number, depthCoef: number): number {
  return Math.max(depthCoef * Math.pow(Math.max(0, q), 0.4), RIVER.minDepth);
}

/**
 * Recalculates bedY from finalized waterY, discharge q, and curvature with a specified depth
 * coefficient, using the same formula as evolveRivers. Applies each river's depth coefficient
 * immediately before carving, supporting individual list-UI adjustment and carving.
 */
export function applyRiverDepth(river: GeneratedRiver, depthCoef: number): void {
  for (const p of river.points) {
    const d = depthFromDischarge(p.q, depthCoef);
    p.bedY = p.waterY - d * (1 + RIVER.poolDeepen * Math.abs(p.curvature));
  }
}

/**
 * Resamples a centerline at equal ds intervals, linearly interpolating x, z, q, and width.
 * Does not retain waterY / bedY / curvature / braided, which later stages recalculate.
 */
function resampleCenterline(points: RiverPoint[], ds: number): RiverPoint[] {
  if (points.length < 2) return points;
  const out: RiverPoint[] = [];
  let acc = 0;
  out.push({ ...points[0] });
  for (let i = 1; i < points.length; i++) {
    const prev = points[i - 1];
    const cur = points[i];
    const segLen = Math.hypot(cur.x - prev.x, cur.z - prev.z);
    if (segLen <= 1e-9) continue;
    let remain = ds - acc;
    while (remain <= segLen) {
      const t = remain / segLen;
      out.push({
        x: prev.x + (cur.x - prev.x) * t,
        z: prev.z + (cur.z - prev.z) * t,
        q: prev.q + (cur.q - prev.q) * t,
        width: prev.width + (cur.width - prev.width) * t,
        waterY: 0,
        bedY: 0,
        curvature: 0,
        braided: false,
      });
      remain += ds;
    }
    acc = segLen - (remain - ds);
  }
  // Always include the endpoint so resampling does not move the mouth.
  const last = points[points.length - 1];
  const tail = out[out.length - 1];
  if (Math.hypot(last.x - tail.x, last.z - tail.z) > ds * 0.25) {
    out.push({ ...last });
  } else {
    out[out.length - 1] = { ...last };
  }
  return out;
}

/**
 * Smooths centerline positions with Laplacian smoothing, fixing endpoints.
 * weight is the neighbor weight in kernel [w, 1-2w, w]. During meander iterations, use a light
 * kernel (small weight) so smoothing attenuation does not cancel meander growth.
 */
function smoothCenterline(points: RiverPoint[], passes: number, weight: number = 0.25): void {
  for (let pass = 0; pass < passes; pass++) {
    let prevX = points[0].x;
    let prevZ = points[0].z;
    for (let i = 1; i < points.length - 1; i++) {
      const cur = points[i];
      const next = points[i + 1];
      const nx = weight * prevX + (1 - 2 * weight) * cur.x + weight * next.x;
      const nz = weight * prevZ + (1 - 2 * weight) * cur.z + weight * next.z;
      prevX = cur.x;
      prevZ = cur.z;
      cur.x = nx;
      cur.z = nz;
    }
  }
}

/**
 * Extracts a river network automatically from terrain without changing elevations.
 *
 * Computes flow accumulation from params.rainfallMmPerYear and rainRegion, selecting cell
 * chains exceeding params.minDischargeM3s as rivers. At confluences, the largest-discharge
 * branch continues as the main stem; others end as tributaries (mouth="confluence").
 *
 * Returned rivers[].points are upstream-to-downstream, with only x/z/q/width finalized.
 * evolveRivers computes waterY/bedY/curvature. IDs use `${idPrefix}_${sequenceNumber}`.
 */
export function extractRiverNetwork(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  params: RiverGenParams,
  lakes: WaterSurfacePayload[],
  rainRegion?: ErosionRegion,
  idPrefix: string = "river",
): RiverExtractionResult {
  const g = buildHydroGrid(elev, rows, cols, spacing, mapWidth, mapHeight, params, lakes, rainRegion);
  const { dsRows, dsCols, dsElev, lakeIdx, flowDir, discharge } = g;
  const dsCount = dsRows * dsCols;

  // --- Identify river cells: land cells at/above the discharge threshold, excluding lake interiors ---
  // Also collect maximum land discharge for diagnosing zero-course results.
  const isRiver = new Uint8Array(dsCount);
  let maxDischargeM3s = 0;
  for (let di = 0; di < dsCount; di++) {
    if (dsElev[di] <= 0 || lakeIdx[di] >= 0) continue;
    if (discharge[di] > maxDischargeM3s) maxDischargeM3s = discharge[di];
    if (discharge[di] >= params.minDischargeM3s) isRiver[di] = 1;
  }

  // --- Dominant upstream branch (highest-discharge incoming river cell) and inflow count per river cell ---
  // The dominant upstream branch continues through the confluence as the main stem; tributaries end there.
  const domUp = new Int32Array(dsCount).fill(-1);
  const inflow = new Uint8Array(dsCount);
  for (let r = 0; r < dsRows; r++) {
    for (let c = 0; c < dsCols; c++) {
      const di = r * dsCols + c;
      if (!isRiver[di]) continue;
      const d = flowDir[di];
      if (d < 0) continue;
      const o = D8_OFFSETS[d];
      const ni = (r + o.dr) * dsCols + (c + o.dc);
      if (!isRiver[ni]) continue;
      inflow[ni] = Math.min(255, inflow[ni] + 1);
      if (domUp[ni] < 0 || discharge[di] > discharge[domUp[ni]]) domUp[ni] = di;
    }
  }

  // --- Walk downstream from channel heads (river cells without incoming river cells), extracting rivers individually ---
  // Each river cell belongs to exactly one river through the dominant-upstream chain.
  const cellRiver = new Int32Array(dsCount).fill(-1);
  type RawRiver = {
    cells: number[];
    mouth: RiverMouthKind;
    mouthWaterLevel?: number;
    confluenceCell?: number;
  };
  const rawRivers: RawRiver[] = [];

  for (let src = 0; src < dsCount; src++) {
    if (!isRiver[src] || inflow[src] > 0) continue;
    const cells: number[] = [src];
    let mouth: RiverMouthKind = "edge";
    let mouthWaterLevel: number | undefined;
    let confluenceCell: number | undefined;
    let cur = src;
    for (;;) {
      const d = flowDir[cur];
      if (d < 0) {
        mouth = "edge";
        break;
      }
      const o = D8_OFFSETS[d];
      const r = Math.floor(cur / dsCols);
      const next = (r + o.dr) * dsCols + (cur - r * dsCols + o.dc);
      if (lakeIdx[next] >= 0) {
        // End on entering a lake; do not create lake-interior sections. A separate river is extracted beyond the spill point.
        mouth = "lake";
        mouthWaterLevel = lakes[lakeIdx[next]]?.water_level ?? 0;
        cells.push(next);
        break;
      }
      if (dsElev[next] <= 0) {
        // Reached sea. Extend one cell seaward before ending to make the mouth explicit.
        mouth = "sea";
        cells.push(next);
        break;
      }
      if (!isRiver[next]) {
        // If river cells break near a threshold boundary (normally impossible), end here.
        mouth = "edge";
        break;
      }
      if (domUp[next] !== cur) {
        // Confluence: next's dominant upstream is another branch (the main stem); this tributary ends here.
        mouth = "confluence";
        confluenceCell = next;
        break;
      }
      cells.push(next);
      cellRiver[next] = rawRivers.length;
      cur = next;
    }
    cellRiver[src] = rawRivers.length;
    rawRivers.push({ cells, mouth, mouthWaterLevel, confluenceCell });
  }

  // --- Cell chain → world-coordinate polyline → resampling + smoothing ---
  const ds = RIVER.resampleFactor * spacing;
  const rivers: GeneratedRiver[] = [];
  const rawIndexToRiverIndex = new Int32Array(rawRivers.length).fill(-1);
  let discardedShortCount = 0;

  for (let ri = 0; ri < rawRivers.length; ri++) {
    const raw = rawRivers[ri];
    const pts: RiverPoint[] = raw.cells.map((di) => {
      const r = Math.floor(di / dsCols);
      const c = di - r * dsCols;
      const q = discharge[di];
      return {
        x: g.cellToWorldX(c),
        z: g.cellToWorldZ(r),
        q,
        width: widthFromDischarge(q, params),
        waterY: 0,
        bedY: 0,
        curvature: 0,
        braided: false,
      };
    });
    const resampled = resampleCenterline(pts, ds);
    if (resampled.length < RIVER.minRiverPoints) {
      discardedShortCount++;
      continue;
    }
    smoothCenterline(resampled, 2);
    rawIndexToRiverIndex[ri] = rivers.length;
    rivers.push({
      id: `${idPrefix}_${rivers.length}`,
      points: resampled,
      mouth: raw.mouth,
      mouthWaterLevel: raw.mouthWaterLevel,
    });
  }

  // --- Resolve receiving rivers, falling back to edge if a receiver was discarded as too short ---
  for (let ri = 0; ri < rawRivers.length; ri++) {
    const riverIdx = rawIndexToRiverIndex[ri];
    if (riverIdx < 0) continue;
    const raw = rawRivers[ri];
    if (raw.mouth !== "confluence" || raw.confluenceCell === undefined) continue;
    const targetRaw = cellRiver[raw.confluenceCell];
    const targetIdx = targetRaw >= 0 ? rawIndexToRiverIndex[targetRaw] : -1;
    if (targetIdx < 0) {
      rivers[riverIdx].mouth = "edge";
      continue;
    }
    const target = rivers[targetIdx];
    rivers[riverIdx].confluence = {
      riverId: target.id,
      pointIndex: nearestPointIndex(target.points, rivers[riverIdx].points[rivers[riverIdx].points.length - 1]),
    };
  }

  const merged = mergeParallelChannels(rivers);
  return { rivers: merged, maxDischargeM3s, discardedShortCount };
}

/**
 * Merges duplicate channels running nearly parallel.
 *
 * D8 supports only eight directions. On nearly flat valley floors, downstream gradient can
 * consistently beat tiny lateral gradients toward adjacent columns, extracting parallel rivers
 * one or two cells apart, especially in symmetric valleys/flat floodplains. Physically, courses
 * within about one river width merge. For parallel rivers with at least 60% of samples near
 * each other, retain only the larger mouth-discharge river. Redirect tributaries referencing
 * deleted rivers to the retained river.
 * Deleted catchment flow is not added to retained discharge; width is slightly underestimated,
 * prioritizing prevention of duplicate carving in the same valley.
 */
function mergeParallelChannels(rivers: GeneratedRiver[]): GeneratedRiver[] {
  const n = rivers.length;
  if (n < 2) return rivers;

  // Bounding rectangles for fast prefiltering. Centerlines have zero width, so straight rivers'
  // rectangles may not overlap; pad by the proximity threshold, about one river width.
  const bboxes = rivers.map((river) => {
    let minX = Infinity;
    let maxX = -Infinity;
    let minZ = Infinity;
    let maxZ = -Infinity;
    let maxW = 0;
    for (const p of river.points) {
      if (p.x < minX) minX = p.x;
      if (p.x > maxX) maxX = p.x;
      if (p.z < minZ) minZ = p.z;
      if (p.z > maxZ) maxZ = p.z;
      if (p.width > maxW) maxW = p.width;
    }
    return { minX: minX - maxW, maxX: maxX + maxW, minZ: minZ - maxW, maxZ: maxZ + maxW };
  });

  const removedTo = new Map<string, string>(); // Deleted river ID → retained river ID.
  const removed = new Set<number>();

  for (let i = 0; i < n; i++) {
    if (removed.has(i)) continue;
    for (let j = i + 1; j < n; j++) {
      if (removed.has(j)) continue;
      // Parallel duplicates have similar lengths and extents. Skip clearly different shapes
      // as a prefilter to prevent excessive all-pairs tributary distance calculations.
      const li = rivers[i].points.length;
      const lj = rivers[j].points.length;
      if (Math.min(li, lj) / Math.max(li, lj) < 0.7) continue;
      const a = bboxes[i];
      const b = bboxes[j];
      if (a.minX > b.maxX || b.minX > a.maxX || a.minZ > b.maxZ || b.minZ > a.maxZ) continue;

      // Count the fraction of smaller-river samples within about one river width of the other centerline.
      const [smallIdx, bigIdx] = li <= lj ? [i, j] : [j, i];
      const small = rivers[smallIdx];
      const big = rivers[bigIdx];
      let near = 0;
      let checked = 0;
      for (let k = 0; k < small.points.length; k += 2) {
        const p = small.points[k];
        const q = big.points[nearestPointIndex(big.points, p)];
        checked++;
        if (Math.hypot(p.x - q.x, p.z - q.z) < (p.width + q.width) * 0.75) near++;
      }
      if (checked === 0 || near / checked <= 0.6) continue;

      // Retain the river with larger mouth discharge.
      const qi = rivers[i].points[rivers[i].points.length - 1].q;
      const qj = rivers[j].points[rivers[j].points.length - 1].q;
      const dropIdx = qi >= qj ? j : i;
      const keepIdx = qi >= qj ? i : j;
      removed.add(dropIdx);
      removedTo.set(rivers[dropIdx].id, rivers[keepIdx].id);
      if (dropIdx === i) break; // If i was deleted, leave its loop.
    }
  }

  if (removed.size === 0) return rivers;

  const kept = rivers.filter((_, idx) => !removed.has(idx));
  const keptById = new Map(kept.map((river) => [river.id, river]));
  // Redirect tributaries of deleted rivers to merge destinations, following one additional link if a destination was also deleted.
  for (const river of kept) {
    if (river.mouth !== "confluence" || !river.confluence) continue;
    let targetId = river.confluence.riverId;
    while (removedTo.has(targetId)) targetId = removedTo.get(targetId)!;
    const target = keptById.get(targetId);
    if (!target) {
      river.mouth = "edge";
      river.confluence = undefined;
      continue;
    }
    river.confluence = {
      riverId: targetId,
      pointIndex: nearestPointIndex(target.points, river.points[river.points.length - 1]),
    };
  }
  return kept;
}

/** Returns the index of the polyline point nearest pt. */
export function nearestPointIndex(polyline: RiverPoint[], pt: { x: number; z: number }): number {
  let best = 0;
  let bestDist = Infinity;
  for (let i = 0; i < polyline.length; i++) {
    const d = (polyline[i].x - pt.x) ** 2 + (polyline[i].z - pt.z) ** 2;
    if (d < bestDist) {
      bestDist = d;
      best = i;
    }
  }
  return best;
}

/* ============================================================================
 * [2] Meander evolution + water elevation profile (evolveRivers)
 * ========================================================================== */

/**
 * Computes signed smoothed curvature K[i] (1/m).
 *
 * Local curvature uses Menger curvature, from the circumcircle through three points.
 * Approximates Howard-Knutson's local curvature + weighted upstream-curvature integral with
 * a blend of the local term and an upstream-to-downstream exponential moving average (IIR).
 * The upstream EMA phase-lags bend apices downstream, driving migration; the local term
 * grows amplitude. EMA alone strongly damps small perturbations and prevents growth.
 * Positive values indicate left bends, counterclockwise relative to travel direction.
 */
function computeSmoothedCurvature(points: RiverPoint[], ds: number): Float64Array {
  const n = points.length;
  const K = new Float64Array(n);
  let ema = 0;
  for (let i = 1; i < n - 1; i++) {
    const ax = points[i].x - points[i - 1].x;
    const az = points[i].z - points[i - 1].z;
    const bx = points[i + 1].x - points[i].x;
    const bz = points[i + 1].z - points[i].z;
    const cross = ax * bz - az * bx;
    const la = Math.hypot(ax, az);
    const lb = Math.hypot(bx, bz);
    const lc = Math.hypot(ax + bx, az + bz);
    const k = la > 1e-9 && lb > 1e-9 && lc > 1e-9 ? (2 * cross) / (la * lb * lc) : 0;
    // Decay distance λ = curvatureLagWidths × width. Wider rivers retain effects from farther upstream.
    const lambda = RIVER.curvatureLagWidths * Math.max(points[i].width, 1);
    const alpha = Math.min(1, ds / lambda);
    ema = alpha * k + (1 - alpha) * ema;
    K[i] = RIVER.curvatureLocalMix * k + (1 - RIVER.curvatureLocalMix) * ema;
  }
  return K;
}

/**
 * Neck cutoff: removes loops whose necks narrow as meanders develop.
 *
 * Uses spatial hashing to find point pairs far apart along the course but close in Euclidean
 * distance, then removes intervening points to create a shortcut, corresponding to natural
 * meander cutoff. Preserving removed loops as oxbow lakes is outside scope.
 * Cuts only the first location per call, since iterations are plentiful.
 */
function neckCutoff(points: RiverPoint[], ds: number): RiverPoint[] {
  const n = points.length;
  if (n < 8) return points;
  let maxWidth = 0;
  for (const p of points) if (p.width > maxWidth) maxWidth = p.width;
  const cellSize = Math.max(RIVER.neckCutWidths * maxWidth, ds);
  const buckets = new Map<number, number[]>();
  const keyOf = (x: number, z: number) => {
    const gx = Math.floor(x / cellSize);
    const gz = Math.floor(z / cellSize);
    return gx * 73856093 + gz * 19349663;
  };
  for (let i = 0; i < n; i++) {
    const key = keyOf(points[i].x, points[i].z);
    const list = buckets.get(key);
    if (list) list.push(i);
    else buckets.set(key, [i]);
  }
  for (let i = 0; i < n; i++) {
    const p = points[i];
    const gx = Math.floor(p.x / cellSize);
    const gz = Math.floor(p.z / cellSize);
    for (let ox = -1; ox <= 1; ox++) {
      for (let oz = -1; oz <= 1; oz++) {
        const list = buckets.get((gx + ox) * 73856093 + (gz + oz) * 19349663);
        if (!list) continue;
        for (const j of list) {
          if (j <= i) continue;
          const w = Math.max(p.width, points[j].width);
          // Exclude point pairs close along the course, since they are merely adjacent points.
          if ((j - i) * ds <= RIVER.neckPathWidths * w) continue;
          const dist = Math.hypot(points[j].x - p.x, points[j].z - p.z);
          if (dist < RIVER.neckCutWidths * w) {
            // Remove the loop between i and j, creating a shortcut.
            return points.slice(0, i + 1).concat(points.slice(j));
          }
        }
      }
    }
  }
  return points;
}

/**
 * Simulates meander evolution (simplified Howard-Knutson) and finalizes water elevation profiles.
 * Updates rivers' points in place without changing terrain elev.
 *
 * Each iteration moves centerline points laterally along normals in proportion to smoothed
 * curvature × width, growing bends outward. Attenuates movement by:
 *   - D_slope: no meandering on steep longitudinal water gradients (mountain streams).
 *   - D_confine: high valley banks prevent lateral movement in gorges.
 * After iterations, resamples evenly, smooths, and cuts necks, then re-snaps tributary mouths
 * to nearest main-stem points so migration does not break confluences.
 *
 * Finally computes water elevation profiles; see computeSurfaceProfile.
 */
export function evolveRivers(
  rivers: GeneratedRiver[],
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  params: RiverGenParams,
): void {
  const ds = RIVER.resampleFactor * spacing;
  const sample = (wx: number, wz: number) => sampleElevAtWorld(elev, rows, cols, spacing, mapWidth, mapHeight, wx, wz);
  const riverById = new Map<string, GeneratedRiver>();
  for (const r of rivers) riverById.set(r.id, r);

  const iterations = params.meanderStrength > 0 ? Math.max(0, Math.round(params.meanderIterations)) : 0;
  const migration = params.meanderStrength * RIVER.migrationCoef;

  // --- Apply initial perturbations ---
  // Curvature-driven evolution cannot grow a perfectly straight zero-curvature line (displacement stays zero),
  // so seed small lateral fluctuations representing natural riverbed irregularities. Use deterministic
  // value noise with a phase per river, applying the same slope/gorge attenuation as main migration.
  if (iterations > 0) {
    for (let ri = 0; ri < rivers.length; ri++) {
      const pts = rivers[ri].points;
      const n = pts.length;
      if (n < 4) continue;
      let arc = 0;
      for (let i = 2; i < n - 2; i++) {
        const p = pts[i];
        arc += Math.hypot(p.x - pts[i - 1].x, p.z - pts[i - 1].z);
        let tx = pts[i + 1].x - pts[i - 1].x;
        let tz = pts[i + 1].z - pts[i - 1].z;
        const tl = Math.hypot(tx, tz);
        if (tl < 1e-9) continue;
        tx /= tl;
        tz /= tl;
        const hUp = sample(pts[i - 1].x, pts[i - 1].z);
        const hDown = sample(pts[i + 1].x, pts[i + 1].z);
        const slope = Math.max(0, (hUp - hDown) / (2 * ds));
        const dSlope = Math.max(0, 1 - slope / RIVER.maxMeanderSlope);
        if (dSlope <= 0) continue;
        const wavelength = RIVER.seedWavelengthWidths * p.width;
        const amp = RIVER.seedAmpWidths * p.width * params.meanderStrength * dSlope;
        const offset = amp * valueNoise2D(arc / wavelength, ri * 7.13 + 0.5);
        p.x += -tz * offset;
        p.z += tx * offset;
      }
      smoothCenterline(pts, 1);
    }
  }

  for (let iter = 0; iter < iterations; iter++) {
    for (const river of rivers) {
      const pts = river.points;
      const n = pts.length;
      if (n < 4) continue;

      const K = computeSmoothedCurvature(pts, ds);

      // Calculate all displacements before applying them to avoid changing the array during calculation.
      const dxArr = new Float64Array(n);
      const dzArr = new Float64Array(n);
      for (let i = 2; i < n - 2; i++) {
        const p = pts[i];
        const w = p.width;
        const d = depthFromDischarge(p.q, params.depthCoef);

        // Tangent and left normal: rotate the tangent +90°, (tx,tz) → (-tz,tx).
        let tx = pts[i + 1].x - pts[i - 1].x;
        let tz = pts[i + 1].z - pts[i - 1].z;
        const tl = Math.hypot(tx, tz);
        if (tl < 1e-9) continue;
        tx /= tl;
        tz /= tl;
        const nx = -tz;
        const nz = tx;

        // Flatness check: terrain longitudinal slope, substituting terrain samples while no water profile exists.
        const hUp = sample(pts[i - 1].x, pts[i - 1].z);
        const hDown = sample(pts[i + 1].x, pts[i + 1].z);
        const slope = Math.max(0, (hUp - hDown) / (2 * ds));
        const dSlope = Math.max(0, 1 - slope / RIVER.maxMeanderSlope);
        if (dSlope <= 0) continue;

        // Valley confinement: attenuate movement if terrain 2w to either side is at least 3d above estimated water (current terrain).
        const hC = sample(p.x, p.z);
        const hL = sample(p.x + nx * 2 * w, p.z + nz * 2 * w);
        const hR = sample(p.x - nx * 2 * w, p.z - nz * 2 * w);
        const excess = Math.max(hL, hR) - (hC + 3 * d);
        const dConfine = excess <= 0 ? 1 : Math.max(0, 1 - excess / (3 * d));
        if (dConfine <= 0) continue;

        // Bend saturation: stop growth once radius falls to saturationRadiusWidths × width,
        // indicating a fully developed bend. Without this, meanders grow indefinitely and run away.
        const saturation = Math.max(0, 1 - Math.abs(K[i]) * w * RIVER.saturationRadiusWidths);
        if (saturation <= 0) continue;

        // Lateral migration: meanders grow outward, away from the center of curvature.
        // For K>0 (left bend), curvature center is on the left-normal side, so displacement is along -N.
        let delta = migration * (K[i] * w) * w * dSlope * dConfine * saturation;
        const maxStep = RIVER.maxStepFactor * ds;
        if (delta > maxStep) delta = maxStep;
        else if (delta < -maxStep) delta = -maxStep;
        dxArr[i] = -delta * nx;
        dzArr[i] = -delta * nz;
      }
      for (let i = 2; i < n - 2; i++) {
        pts[i].x += dxArr[i];
        pts[i].z += dzArr[i];
      }

      // Refine shape: repeat neck cutoffs until none remain → equal-interval resampling → smoothing.
      // Use a very light smoothing kernel; stronger damping would overwhelm growth and straighten the river.
      let newPts = pts;
      for (let cut = 0; cut < 20; cut++) {
        const after = neckCutoff(newPts, ds);
        if (after.length === newPts.length) break;
        newPts = after;
      }
      newPts = resampleCenterline(newPts, ds);
      smoothCenterline(newPts, 1, 0.08);
      if (newPts.length >= 4) river.points = newPts;
    }

    // Re-snap tributary mouths to nearest main-stem centerline points.
    for (const river of rivers) {
      if (river.mouth !== "confluence" || !river.confluence) continue;
      const target = riverById.get(river.confluence.riverId);
      if (!target) continue;
      const tail = river.points[river.points.length - 1];
      const idx = nearestPointIndex(target.points, tail);
      river.confluence.pointIndex = idx;
      tail.x = target.points[idx].x;
      tail.z = target.points[idx].z;
    }
  }

  computeSurfaceProfiles(rivers, elev, rows, cols, spacing, mapWidth, mapHeight, params, ds);
}

/**
 * Finalizes waterY, bedY, cross-section curvature, and braiding flags for all rivers.
 *
 * Places water below minimum channel/bank elevations; a downstream min scan ensures a
 * monotonically non-increasing profile. Enforces minimum slope ε_s to avoid perfect flatness
 * and make Unity water ribbons visibly flow downward. Clamp mouth levels by type:
 *   - sea: 0 m (sea level).
 *   - lake: lake water level.
 *   - confluence: main-stem water at the confluence. Process the main stem first to prevent
 *     tributary water from being lower.
 * Blend any level reduction upstream from the mouth at maximum slope maxMouthSlope (reverse scan).
 */
function computeSurfaceProfiles(
  rivers: GeneratedRiver[],
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  params: RiverGenParams,
  ds: number,
): void {
  const sample = (wx: number, wz: number) => sampleElevAtWorld(elev, rows, cols, spacing, mapWidth, mapHeight, wx, wz);
  const riverById = new Map<string, GeneratedRiver>();
  for (const r of rivers) riverById.set(r.id, r);

  // Process tributaries only after receiving water levels are finalized (topological order).
  // Cycles cannot occur structurally, but impose a defensive iteration limit.
  const processed = new Set<string>();
  let pending = rivers.slice();
  for (let round = 0; round < 100 && pending.length > 0; round++) {
    const next: GeneratedRiver[] = [];
    for (const river of pending) {
      if (river.mouth === "confluence" && river.confluence && !processed.has(river.confluence.riverId)) {
        // Defer if the receiving river is unprocessed; process immediately if it no longer exists.
        if (riverById.has(river.confluence.riverId)) {
          next.push(river);
          continue;
        }
      }
      computeOneSurfaceProfile(river, riverById, sample, params, ds);
      processed.add(river.id);
    }
    if (next.length === pending.length) {
      // Unresolved dependencies (e.g. cyclic receivers) are abnormal; force-process the remaining rivers.
      for (const river of next) {
        computeOneSurfaceProfile(river, riverById, sample, params, ds);
        processed.add(river.id);
      }
      pending = [];
    } else {
      pending = next;
    }
  }
}

/** Finalizes one river's waterY / bedY / curvature / braided; the core of computeSurfaceProfiles. */
function computeOneSurfaceProfile(
  river: GeneratedRiver,
  riverById: Map<string, GeneratedRiver>,
  sample: (wx: number, wz: number) => number,
  params: RiverGenParams,
  ds: number,
): void {
  const pts = river.points;
  const n = pts.length;
  if (n === 0) return;

  const spacing = ds / RIVER.resampleFactor;
  const normals = pts.map((_, i) => {
    const prev = pts[Math.max(0, i - 1)];
    const next = pts[Math.min(n - 1, i + 1)];
    const dx = next.x - prev.x, dz = next.z - prev.z;
    const len = Math.hypot(dx, dz);
    return len > 1e-9 ? { x: -dz / len, z: dx / len } : { x: 0, z: 1 };
  });

  // Requested width is a discharge-based estimate. Narrow channels in valleys whose cross-sectional relief is large relative to depth.
  // Recalculate from discharge every time to avoid accumulating widening factors during node edits/rebuilds.
  const widths = pts.map((p, i) => {
    const base = widthFromDischarge(p.q, params);
    const normal = normals[i];
    const reach = Math.max(base, spacing);
    const h = sample(p.x, p.z);
    const left = sample(p.x + normal.x * reach, p.z + normal.z * reach);
    const right = sample(p.x - normal.x * reach, p.z - normal.z * reach);
    const relief = Math.max(Math.abs(left - h), Math.abs(right - h));
    const depth = depthFromDischarge(p.q, params.depthCoef);
    return base * (0.6 + 0.8 / (1 + relief / depth));
  });
  for (let i = 0; i < n; i++) {
    pts[i].width = (widths[Math.max(0, i - 1)] + 2 * widths[i] + widths[Math.min(n - 1, i + 1)]) / 4;
    pts[i].braided = false;
  }

  // Check out to grid correction/blending-band edges to retain banks, placing water below the lower bank.
  // Also inspect half-sections before/after each point so averages do not raise depressions between centerline samples.
  const caps = new Float64Array(n);
  for (let i = 0; i < n; i++) {
    const p = pts[i], normal = normals[i];
    const reach = p.width / 2 + spacing / Math.SQRT2 + blendWidth(p.width, spacing) + spacing;
    const steps = Math.max(4, Math.ceil(2 * reach / (spacing / 2)));
    let low = Infinity;
    for (let k = 0; k <= steps; k++) {
      const off = -reach + 2 * reach * k / steps;
      for (const along of [-ds / 2, 0, ds / 2]) {
        low = Math.min(low, sample(p.x + normal.x * off + normal.z * along, p.z + normal.z * off - normal.x * along));
      }
    }
    caps[i] = low - RIVER.freeboard;
  }
  const ws = new Float64Array(n);
  for (let i = 0; i < n; i++) {
    const average = (caps[Math.max(0, i - 1)] + caps[i] + caps[Math.min(n - 1, i + 1)]) / 3;
    const ceiling = Math.min(caps[i], average);
    const distance = i > 0 ? Math.hypot(pts[i].x - pts[i - 1].x, pts[i].z - pts[i - 1].z) : 0;
    ws[i] = i === 0 ? ceiling : Math.min(ceiling, ws[i - 1] - RIVER.minSurfaceSlope * distance);
  }

  // --- Mouth clamping + reverse blending scan ---
  let mouthLevel: number | null = null;
  if (river.mouth === "sea") mouthLevel = 0;
  else if (river.mouth === "lake") mouthLevel = river.mouthWaterLevel ?? 0;
  else if (river.mouth === "confluence" && river.confluence) {
    const target = riverById.get(river.confluence.riverId);
    if (target && target.points.length > 0) {
      const idx = nearestPointIndex(target.points, pts[n - 1]);
      river.confluence.pointIndex = idx;
      mouthLevel = target.points[idx].waterY;
    }
  }
  if (mouthLevel !== null) {
    if (ws[n - 1] > mouthLevel) {
      const drop = ws[n - 1] - mouthLevel;
      const rampLen = Math.min(n - 1, Math.max(1, Math.ceil(drop / (RIVER.maxMouthSlope * ds))));
      for (let k = 0; k <= rampLen; k++) {
        const t = 1 - k / Math.max(1, rampLen);
        ws[n - 1 - k] -= drop * t * t * (3 - 2 * t);
      }
    }
    // Connect the mouth to sea/lake/main-stem water. Area extraction excludes overlap with sea/lakes in backwater sections.
    for (let i = 0; i < n; i++) ws[i] = Math.max(mouthLevel, ws[i]);
    ws[n - 1] = mouthLevel;
  }

  // --- Dimensionless cross-section curvature, clamped to ±maxNormCurvature ---
  const K = computeSmoothedCurvature(pts, ds);
  for (let i = 0; i < n; i++) {
    const kn = K[i] * pts[i].width * 2;
    pts[i].curvature = Math.max(-RIVER.maxNormCurvature, Math.min(RIVER.maxNormCurvature, kn));
  }

  // --- Identify braided sections without widening after bank inspection ---
  // Conditions: gentle water slope below braidMaxSlope and width at least braidMinWidth.
  // Short isolated patches are noise; braid only runs of at least braidMinRun points.
  if (params.enableBraiding) {
    const rawFlag = new Uint8Array(n);
    for (let i = 1; i < n - 1; i++) {
      const slope = Math.max(0, (ws[i - 1] - ws[i + 1]) / (2 * ds));
      if (slope < RIVER.braidMaxSlope && pts[i].width >= RIVER.braidMinWidth) rawFlag[i] = 1;
    }
    let runStart = -1;
    for (let i = 0; i <= n; i++) {
      const flag = i < n ? rawFlag[i] : 0;
      if (flag && runStart < 0) runStart = i;
      if (!flag && runStart >= 0) {
        if (i - runStart >= RIVER.braidMinRun) {
          for (let j = runStart; j < i; j++) {
            pts[j].braided = true;
          }
        }
        runStart = -1;
      }
    }
  }

  // --- Finalize water and riverbed ---
  // bedY is thalweg (deepest point) elevation. Bend pools deepen by poolDeepen × |κ|.
  for (let i = 0; i < n; i++) {
    const d = depthFromDischarge(pts[i].q, params.depthCoef);
    pts[i].waterY = ws[i];
    pts[i].bedY = ws[i] - d * (1 + RIVER.poolDeepen * Math.abs(pts[i].curvature));
  }
}

/* ============================================================================
 * [2b] Manual course drawing and node editing with control points.
 *
 * Also represents courses as sparse control points, allowing equally spaced centerline points
 * to be rebuilt from smooth curves through them. Manual rivers use clicked vertices directly;
 * generated rivers use Douglas-Peucker-simplified centerlines after meander evolution.
 * On each node move/add/delete, buildRiverFromControlPoints rebuilds centerline, mouth, and
 * water profile without changing elevations; carving still uses carveRivers.
 * ========================================================================== */

/** Course control point. q is local discharge (m³/s), linearly interpolated between control points onto the centerline. */
export type RiverControlPoint = { x: number; z: number; q: number };

/** buildRiverFromControlPoints result. If the mouth snaps to another river, the final control point also uses the snapped position. */
export type RiverRebuildResult = { river: GeneratedRiver; controlPoints: RiverControlPoint[] };

/**
 * Uses dynamic programming to select a continuous route within cross-sectional bands around
 * the original course, minimizing elevation, reverse gradients, and detours. Fixes endpoints
 * and traverses cross sections in sequence to prevent large detours into other valleys or
 * backtracking. Leaves terrain unchanged; pass the result to normal control-point rebuilding
 * to share preview and Undo handling.
 */
export function fitRiverRouteToValley(
  river: GeneratedRiver,
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  radius: number,
): RiverControlPoint[] {
  if (!Number.isFinite(radius) || radius <= 0 || river.points.length < 2) {
    return river.points.map(({ x, z, q }) => ({ x, z, q }));
  }
  const points = resampleCenterline(river.points, Math.max(spacing, radius / 8));
  if (points.length < 3) return points.map(({ x, z, q }) => ({ x, z, q }));
  const sample = (x: number, z: number) => sampleElevAtWorld(elev, rows, cols, spacing, mapWidth, mapHeight, x, z);
  const half = Math.min(20, Math.max(1, Math.ceil(radius / spacing)));
  const count = half * 2 + 1;
  const candidates = points.map((p, i) => {
    const prev = points[Math.max(0, i - 1)], next = points[Math.min(points.length - 1, i + 1)];
    const dx = next.x - prev.x, dz = next.z - prev.z;
    const len = Math.max(1e-9, Math.hypot(dx, dz));
    return Array.from({ length: count }, (_, j) => {
      const offset = (j - half) * radius / half;
      const x = p.x - dz / len * offset, z = p.z + dx / len * offset;
      const valid = x >= -mapWidth / 2 && x <= mapWidth / 2 && z >= -mapHeight / 2 && z <= mapHeight / 2;
      return { x, z, q: p.q, h: valid ? sample(x, z) : Infinity, offset };
    });
  });
  let cost = new Float64Array(count).fill(Infinity);
  cost[half] = 0;
  const parents = new Int16Array(points.length * count).fill(-1);
  for (let i = 1; i < points.length; i++) {
    const nextCost = new Float64Array(count).fill(Infinity);
    const baseDistance = Math.hypot(points[i].x - points[i - 1].x, points[i].z - points[i - 1].z);
    const low = Math.min(...candidates[i].map((p) => p.h));
    for (let j = 0; j < count; j++) {
      if (i === points.length - 1 && j !== half) continue;
      const p = candidates[i][j];
      if (!Number.isFinite(p.h)) continue;
      for (let k = 0; k < count; k++) {
        if (!Number.isFinite(cost[k])) continue;
        const prev = candidates[i - 1][k];
        const distance = Math.hypot(p.x - prev.x, p.z - prev.z);
        if (distance > baseDistance + radius / half * 2) continue;
        // Inspect segment interiors too, preventing ridge crossings when only candidate endpoints are low.
        let ridge = 0;
        for (const t of [0.25, 0.5, 0.75]) {
          ridge += Math.max(0, sample(prev.x + (p.x - prev.x) * t, prev.z + (p.z - prev.z) * t) - (prev.h + (p.h - prev.h) * t));
        }
        const value = cost[k] + (p.h - low) + 4 * Math.max(0, p.h - prev.h)
          + 2 * ridge + 0.05 * distance + 0.02 * Math.abs(p.offset) + 0.05 * Math.abs(p.offset - prev.offset);
        if (value < nextCost[j]) {
          nextCost[j] = value;
          parents[i * count + j] = k;
        }
      }
    }
    cost = nextCost;
  }
  if (!Number.isFinite(cost[half])) return river.points.map(({ x, z, q }) => ({ x, z, q }));
  const result: RiverControlPoint[] = new Array(points.length);
  let j = half;
  for (let i = points.length - 1; i >= 0; i--) {
    const p = candidates[i][j];
    result[i] = { x: p.x, z: p.z, q: p.q };
    if (i > 0) j = parents[i * count + j];
  }
  return result;
}

/** Infers discharge (m³/s) from width (meters), the inverse of widthFromDischarge. */
export function dischargeFromWidth(width: number, params: RiverGenParams): number {
  const coef = Math.max(1e-6, params.widthCoef);
  return (Math.max(0, width) / coef) ** 2;
}

/**
 * Simplifies the centerline into control points with Douglas-Peucker tolerance
 * spacing×controlPointToleranceFactor. Always retain step increases in discharge at confluences,
 * regardless of shape; otherwise interpolation would smear tributary inflow changes upstream
 * and widen the river before the confluence.
 */
export function simplifyCenterlineToControlPoints(points: RiverPoint[], spacing: number): RiverControlPoint[] {
  const n = points.length;
  if (n === 0) return [];
  const toCp = (p: RiverPoint): RiverControlPoint => ({ x: p.x, z: p.z, q: p.q });
  if (n <= 2) return points.map(toCp);

  const keep = new Uint8Array(n);
  keep[0] = 1;
  keep[n - 1] = 1;
  for (let i = 1; i < n; i++) {
    if (points[i].q > points[i - 1].q * (1 + RIVER.controlPointQJump) + 1e-9) {
      keep[i - 1] = 1;
      keep[i] = 1;
    }
  }

  // Iterative DP in sections split by fixed points, using a stack to avoid recursion depth proportional to point count.
  const stack: [number, number][] = [];
  let segStart = 0;
  for (let i = 1; i < n; i++) {
    if (keep[i]) {
      stack.push([segStart, i]);
      segStart = i;
    }
  }
  const tol2 = (RIVER.controlPointToleranceFactor * spacing) ** 2;
  while (stack.length > 0) {
    const [a, b] = stack.pop()!;
    if (b - a < 2) continue;
    const ax = points[a].x;
    const az = points[a].z;
    const dx = points[b].x - ax;
    const dz = points[b].z - az;
    const len2 = dx * dx + dz * dz;
    let bestD = -1;
    let bestI = -1;
    for (let i = a + 1; i < b; i++) {
      const px = points[i].x - ax;
      const pz = points[i].z - az;
      let d2: number;
      if (len2 < 1e-12) {
        d2 = px * px + pz * pz;
      } else {
        const t = Math.max(0, Math.min(1, (px * dx + pz * dz) / len2));
        const ex = px - t * dx;
        const ez = pz - t * dz;
        d2 = ex * ex + ez * ez;
      }
      if (d2 > bestD) {
        bestD = d2;
        bestI = i;
      }
    }
    if (bestD > tol2) {
      keep[bestI] = 1;
      stack.push([a, bestI], [bestI, b]);
    }
  }
  const out: RiverControlPoint[] = [];
  for (let i = 0; i < n; i++) if (keep[i]) out.push(toCp(points[i]));
  return out;
}

/**
 * Expands a centripetal Catmull-Rom curve through control points into a polyline with roughly
 * step spacing. Centripetal α=0.5 avoids cusps and small self-intersecting loops even with uneven
 * point spacing, fitting manually drawn vertices. End tangents use virtual points mirrored from
 * adjacent control points. Interpolates q linearly; the caller recalculates width and later fields.
 */
function expandCatmullRom(cps: RiverControlPoint[], step: number): RiverPoint[] {
  const n = cps.length;
  const mk = (x: number, z: number, q: number): RiverPoint => ({ x, z, q, width: 0, waterY: 0, bedY: 0, curvature: 0, braided: false });
  if (n === 0) return [];
  if (n === 1) return [mk(cps[0].x, cps[0].z, cps[0].q)];
  const out: RiverPoint[] = [];
  const mirror = (a: RiverControlPoint, b: RiverControlPoint): RiverControlPoint => ({ x: 2 * a.x - b.x, z: 2 * a.z - b.z, q: a.q });
  const knot = (a: RiverControlPoint, b: RiverControlPoint) => Math.max(1e-6, Math.sqrt(Math.hypot(b.x - a.x, b.z - a.z)));
  for (let i = 0; i < n - 1; i++) {
    const p0 = i === 0 ? mirror(cps[0], cps[1]) : cps[i - 1];
    const p1 = cps[i];
    const p2 = cps[i + 1];
    const p3 = i + 2 < n ? cps[i + 2] : mirror(cps[n - 1], cps[n - 2]);
    const pieces = Math.max(1, Math.ceil(Math.hypot(p2.x - p1.x, p2.z - p1.z) / step));
    const t0 = 0;
    const t1 = t0 + knot(p0, p1);
    const t2 = t1 + knot(p1, p2);
    const t3 = t2 + knot(p2, p3);
    for (let k = 0; k < pieces; k++) {
      const u = k / pieces;
      const t = t1 + (t2 - t1) * u;
      // Barry-Goldman pyramid evaluation.
      const w01a = (t1 - t) / (t1 - t0);
      const w01b = (t - t0) / (t1 - t0);
      const w12a = (t2 - t) / (t2 - t1);
      const w12b = (t - t1) / (t2 - t1);
      const w23a = (t3 - t) / (t3 - t2);
      const w23b = (t - t2) / (t3 - t2);
      const a1x = w01a * p0.x + w01b * p1.x;
      const a1z = w01a * p0.z + w01b * p1.z;
      const a2x = w12a * p1.x + w12b * p2.x;
      const a2z = w12a * p1.z + w12b * p2.z;
      const a3x = w23a * p2.x + w23b * p3.x;
      const a3z = w23a * p2.z + w23b * p3.z;
      const w02a = (t2 - t) / (t2 - t0);
      const w02b = (t - t0) / (t2 - t0);
      const w13a = (t3 - t) / (t3 - t1);
      const w13b = (t - t1) / (t3 - t1);
      const b1x = w02a * a1x + w02b * a2x;
      const b1z = w02a * a1z + w02b * a2z;
      const b2x = w13a * a2x + w13b * a3x;
      const b2z = w13a * a2z + w13b * a3z;
      out.push(mk(w12a * b1x + w12b * b2x, w12a * b1z + w12b * b2z, p1.q + (p2.q - p1.q) * u));
    }
  }
  const last = cps[n - 1];
  out.push(mk(last.x, last.z, last.q));
  return out;
}

/** True if river flows into targetId through the confluence chain, with cycle protection. */
function flowsInto(river: GeneratedRiver, targetId: string, byId: Map<string, GeneratedRiver>): boolean {
  let cur: GeneratedRiver | undefined = river;
  for (let hop = 0; hop < 64 && cur; hop++) {
    if (cur.mouth !== "confluence" || !cur.confluence) return false;
    if (cur.confluence.riverId === targetId) return true;
    cur = byId.get(cur.confluence.riverId);
  }
  return false;
}

/**
 * Determines mouth type from the centerline endpoint and updates river.mouth, mouthWaterLevel,
 * and confluence. Priority: sea (nearby terrain <=0 m) → lake (inside an existing water polygon)
 * → confluence (within snap radius of another centerline, using the larger of its width and
 * resampleFactor×spacing×confluenceSnapDsFactor) → unconnected (edge, retaining the terrain
 * depression-filled water profile). For confluences, snaps the endpoint to the other centerline's
 * nearest point and returns the snapped coordinates. Never joins a river that flows into this
 * one (a tributary), since that would create a cycle.
 */
function resolveRiverMouth(
  river: GeneratedRiver,
  sample: (wx: number, wz: number) => number,
  ds: number,
  lakes: WaterSurfacePayload[],
  others: GeneratedRiver[],
): { x: number; z: number } | null {
  const pts = river.points;
  if (pts.length === 0) return null;
  const tail = pts[pts.length - 1];
  river.mouthWaterLevel = undefined;
  river.confluence = undefined;

  let minAround = Infinity;
  for (const [ox, oz] of [[0, 0], [ds, 0], [-ds, 0], [0, ds], [0, -ds]]) {
    const h = sample(tail.x + ox, tail.z + oz);
    if (h < minAround) minAround = h;
  }
  if (minAround <= 0) {
    river.mouth = "sea";
    return null;
  }

  for (const lake of lakes) {
    if (!lake?.polygon || lake.polygon.type !== "Polygon") continue;
    const ring = (lake.polygon.coordinates as number[][][])[0];
    if (!Array.isArray(ring) || ring.length < 3) continue;
    if (pointInRing(tail.x, tail.z, ring)) {
      river.mouth = "lake";
      river.mouthWaterLevel = lake.water_level;
      return null;
    }
  }

  const byId = new Map<string, GeneratedRiver>();
  for (const r of others) byId.set(r.id, r);
  byId.set(river.id, river);
  let best: { river: GeneratedRiver; index: number; dist: number } | null = null;
  for (const other of others) {
    if (other.id === river.id || other.points.length < 2) continue;
    if (flowsInto(other, river.id, byId)) continue;
    const idx = nearestPointIndex(other.points, tail);
    const op = other.points[idx];
    const dist = Math.hypot(op.x - tail.x, op.z - tail.z);
    const snap = Math.max(op.width, RIVER.confluenceSnapDsFactor * ds);
    if (dist <= snap && (!best || dist < best.dist)) best = { river: other, index: idx, dist };
  }
  if (best) {
    river.mouth = "confluence";
    river.confluence = { riverId: best.river.id, pointIndex: best.index };
    const op = best.river.points[best.index];
    tail.x = op.x;
    tail.z = op.z;
    return { x: op.x, z: op.z };
  }
  river.mouth = "edge";
  return null;
}

/**
 * Rebuilds one river's centerline, mouth, and water profile from control points without changing terrain.
 *
 * Expands a curve through control points into a centerline spaced at resampleFactor×spacing,
 * computes widths from discharge using hydraulic geometry, determines mouth type from the
 * endpoint, and finalizes the water profile. keepMouth=true preserves base's mouth type,
 * avoiding re-detection for edits not moving the endpoint or tributaries re-snapped after
 * main-stem changes. Pass all other rivers, including carved ones, in others so tributaries
 * can reference receiving water levels. Returns a new GeneratedRiver without modifying base,
 * because editor Undo snapshots share the same object references.
 */
export function buildRiverFromControlPoints(
  base: GeneratedRiver,
  controlPoints: RiverControlPoint[],
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  params: RiverGenParams,
  others: GeneratedRiver[],
  lakes: WaterSurfacePayload[],
  keepMouth: boolean,
): RiverRebuildResult {
  const ds = RIVER.resampleFactor * spacing;
  const sample = (wx: number, wz: number) => sampleElevAtWorld(elev, rows, cols, spacing, mapWidth, mapHeight, wx, wz);
  let points = expandCatmullRom(controlPoints, ds / 2);
  points = resampleCenterline(points, ds);
  for (const p of points) p.width = widthFromDischarge(p.q, params);
  const river: GeneratedRiver = {
    ...base,
    points,
    confluence: base.confluence ? { ...base.confluence } : undefined,
  };
  let cps = controlPoints;
  if (!keepMouth || (river.mouth === "confluence" && !river.confluence)) {
    const snapped = resolveRiverMouth(river, sample, ds, lakes, others);
    if (snapped && cps.length > 0) {
      cps = cps.slice();
      cps[cps.length - 1] = { ...cps[cps.length - 1], x: snapped.x, z: snapped.z };
    }
  }
  const byId = new Map<string, GeneratedRiver>();
  for (const r of others) byId.set(r.id, r);
  byId.set(river.id, river);
  computeOneSurfaceProfile(river, byId, sample, params, ds);
  return { river, controlPoints: cps };
}

/* ============================================================================
 * [3] Riverbed carving (carveRivers)
 * ========================================================================== */

/**
 * Carves finalized river-network beds into full-resolution elevations in place.
 * Returns changed grid bounds (equivalent to DirtyRect), or null if nothing changed.
 *
 * Cross sections are V-shaped over tn ∈ [-1, 1], positive toward the left normal, with the
 * thalweg shifted to the bend's outer bank. The shorter outer-bank side is steep (erosion bank),
 * the inner side gentle (point bar). Limiting depth by distance from bank × bankSlope creates
 * steep channel walls like a drained real river. Outside the width, a minimal blending band
 * transitions from bank shoulder (water + freeboard) to existing terrain using smoothstep.
 *
 * In braided sections, add value-noise bar fields to create sandbars. Clip bar tops to
 * braidBarClipBelowWater below water (submerged bars), adding nothing to two branches near
 * cross-sectional ±0.7 to guarantee continuous navigable channels.
 *
 * Only lower elevations: elev = min(elev, target). This prevents artificial embankments when
 * crossing existing depressions. Do not carve sea cells (elevation <=0). Land carved below
 * sea level near mouths is allowed as a natural estuary.
 *
 * Rasterize capsules per segment, accumulating target elevations by min in a stamp buffer
 * before applying them together. This prevents overly deep double carving in overlapping
 * segments at joins or inside meanders.
 */
export function carveRivers(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  rivers: GeneratedRiver[],
): GridRect | null {
  if (rivers.length === 0) return null;
  const halfW = mapWidth / 2;
  const halfH = mapHeight / 2;

  const footprint = spacing / Math.SQRT2;

  // --- Find the whole river network's grid bounds, including blending bands, and allocate the stamp buffer ---
  let minX = Infinity;
  let maxX = -Infinity;
  let minZ = Infinity;
  let maxZ = -Infinity;
  let maxReach = 0;
  for (const river of rivers) {
    for (const p of river.points) {
      if (p.x < minX) minX = p.x;
      if (p.x > maxX) maxX = p.x;
      if (p.z < minZ) minZ = p.z;
      if (p.z > maxZ) maxZ = p.z;
      const reach = p.width / 2 + blendWidth(p.width, spacing) + footprint;
      if (reach > maxReach) maxReach = reach;
    }
  }
  if (!isFinite(minX)) return null;

  const bboxCMin = Math.max(0, Math.floor((minX - maxReach + halfW) / spacing));
  const bboxCMax = Math.min(cols - 1, Math.ceil((maxX + maxReach + halfW) / spacing));
  const bboxRMin = Math.max(0, Math.floor((minZ - maxReach + halfH) / spacing));
  const bboxRMax = Math.min(rows - 1, Math.ceil((maxZ + maxReach + halfH) / spacing));
  if (bboxCMin > bboxCMax || bboxRMin > bboxRMax) return null;
  const bboxW = bboxCMax - bboxCMin + 1;
  const bboxH = bboxRMax - bboxRMin + 1;
  const stamp = new Float32Array(bboxW * bboxH).fill(Infinity);

  // --- Accumulate each segment's target elevations by min into the stamp buffer ---
  for (const river of rivers) {
    const pts = river.points;
    // Cumulative arc length for bar noise: course-coordinate s for each river.
    let arc = 0;
    for (let i = 0; i < pts.length - 1; i++) {
      const a = pts[i];
      const b = pts[i + 1];
      const segX = b.x - a.x;
      const segZ = b.z - a.z;
      const segLen = Math.hypot(segX, segZ);
      if (segLen < 1e-9) continue;
      const invLen = 1 / segLen;
      const tx = segX * invLen;
      const tz = segZ * invLen;
      // Left normal (tangent rotated +90°), defining the positive cross-sectional direction t.
      const nx = -tz;
      const nz = tx;

      const reach = Math.max(a.width, b.width) / 2 + blendWidth(Math.max(a.width, b.width), spacing) + footprint;
      const cMin = Math.max(bboxCMin, Math.floor((Math.min(a.x, b.x) - reach + halfW) / spacing));
      const cMax = Math.min(bboxCMax, Math.ceil((Math.max(a.x, b.x) + reach + halfW) / spacing));
      const rMin = Math.max(bboxRMin, Math.floor((Math.min(a.z, b.z) - reach + halfH) / spacing));
      const rMax = Math.min(bboxRMax, Math.ceil((Math.max(a.z, b.z) + reach + halfH) / spacing));

      for (let r = rMin; r <= rMax; r++) {
        const wz = r * spacing - halfH;
        for (let c = cMin; c <= cMax; c++) {
          const wx = c * spacing - halfW;
          // Projection onto the segment (s clamped to 0..1) and actual distance from it.
          // Measure cross-sectional distance by Euclidean distance to the closest point, including
          // axial overshoot at endpoints, rather than only the normal component. Otherwise, cells
          // beyond a segment would receive an axially extended endpoint section, carving low downstream
          // beds high up steep slopes. Endpoints must instead act as radial caps.
          const relX = wx - a.x;
          const relZ = wz - a.z;
          let s = (relX * tx + relZ * tz) * invLen;
          if (s < 0) s = 0;
          else if (s > 1) s = 1;
          const projX = a.x + segX * s;
          const projZ = a.z + segZ * s;
          const offX = wx - projX;
          const offZ = wz - projZ;
          // Include the half-diagonal footprint represented by each grid point to prevent bed-depth pulses from grid alignment.
          const absT = Math.max(0, Math.hypot(offX, offZ) - footprint);
          // Use the normal-component sign only to distinguish cross-sectional sides for asymmetry.
          const t = offX * nx + offZ * nz >= 0 ? absT : -absT;

          // Interpolate parameters along the segment.
          const w = a.width + (b.width - a.width) * s;
          const halfWidth = w / 2;
          const blend = blendWidth(w, spacing);
          if (absT > halfWidth + blend) continue;

          const waterY = a.waterY + (b.waterY - a.waterY) * s;
          const bedY = a.bedY + (b.bedY - a.bedY) * s;
          const kappa = a.curvature + (b.curvature - a.curvature) * s;
          const depth = waterY - bedY; // Thalweg depth, including pool deepening.

          let target: number;
          if (halfWidth <= 1e-6 && absT === 0) {
            target = bedY;
          } else if (absT <= halfWidth && halfWidth > 1e-6) {
            // --- Channel interior: asymmetric sharp-notch section, clipped by bank slope ---
            // tn ∈ [-1,1]. Shift thalweg outward: K>0 (left bend) shifts to the right bank, negative t.
            // Depth profile is 1 - |x|^p with p<1, steep immediately above the thalweg,
            // cutting a distinct sharp notch at the deepest bed. A linear V has a gentle bottom slope
            // that looks like a soft depression. Also cap depth by distance from bank × bank slope
            // to produce steep bank walls like a drained real river
            // (bankSlope, about 35°).
            const tn = t / halfWidth;
            const t0 = -RIVER.thalwegShift * kappa;
            const denom = tn < t0 ? 1 + t0 : 1 - t0;
            const xNorm = Math.abs((tn - t0) / Math.max(1e-6, denom));
            const fRaw = 1 - Math.pow(Math.min(1, xNorm), RIVER.crossSectionExponent);
            const f = fRaw > 0 ? fRaw : 0;
            // Raise bank slope to at least the slope required to reach the requested thalweg depth.
            // A fixed slope makes depth > half-width × bankSlope geometrically impossible,
            // clipping the bottom flat and nullifying the depth coefficient. Deep rivers instead get gorge-like walls.
            const bankToThalweg = Math.max(1e-3, halfWidth * (1 - Math.abs(t0)));
            const effSlope = Math.max(RIVER.bankSlope, depth / bankToThalweg);
            const bankLimit = (halfWidth - absT) * effSlope;
            target = waterY - Math.min(depth * f, bankLimit);

            // --- Sandbars: bar field in braided sections ---
            const braided = (s < 0.5 ? a.braided : b.braided) && (s < 0.5 ? b.braided : a.braided);
            if (braided && absT / halfWidth < RIVER.braidBarExtent) {
              const sGlobal = arc + segLen * s;
              const wavelength = RIVER.braidNoiseWavelengthWidths * w;
              const noise = valueNoise2D(sGlobal / wavelength, tn * 1.5);
              const bar = RIVER.braidBarHeightFactor * depth * Math.max(0, noise);
              // Bar tops remain braidBarClipBelowWater below the surface (submerged bars).
              target = Math.min(target + bar, waterY - RIVER.braidBarClipBelowWater);
            }
          } else {
            // --- Blending band: smoothstep from bank shoulder (water + freeboard) to existing terrain ---
            const u = (absT - halfWidth) / blend;
            const sm = u * u * (3 - 2 * u);
            const shoulder = waterY + RIVER.freeboard;
            const existing = elev[r * cols + c];
            target = shoulder * (1 - sm) + existing * sm;
          }

          const si = (r - bboxRMin) * bboxW + (c - bboxCMin);
          if (target < stamp[si]) stamp[si] = target;
        }
      }

      arc += segLen;
    }
  }

  // --- Apply the stamp buffer together, lowering only and excluding sea ---
  let dirty: GridRect | null = null;
  for (let r = bboxRMin; r <= bboxRMax; r++) {
    const rowBase = r * cols;
    const stampBase = (r - bboxRMin) * bboxW;
    for (let c = bboxCMin; c <= bboxCMax; c++) {
      const target = stamp[stampBase + (c - bboxCMin)];
      if (!isFinite(target)) continue;
      const idx = rowBase + c;
      const cur = elev[idx];
      if (cur <= 0 || target >= cur) continue;
      elev[idx] = target;
      if (!dirty) {
        dirty = { rMin: r, rMax: r, cMin: c, cMax: c };
      } else {
        if (r < dirty.rMin) dirty.rMin = r;
        if (r > dirty.rMax) dirty.rMax = r;
        if (c < dirty.cMin) dirty.cMin = c;
        if (c > dirty.cMax) dirty.cMax = c;
      }
    }
  }
  return dirty;
}

/** Computes blending band width (meters) from river width. */
function blendWidth(width: number, spacing: number): number {
  return Math.max(RIVER.blendWidthFactor * width, RIVER.blendMinSpacingFactor * spacing);
}

/* ============================================================================
 * Storage contract helpers
 * ========================================================================== */

/**
 * Seed cells for river area extraction:
 *   - spline: immediately after carving, seed the centerline ± (half-width + blending band),
 *     the carved footprint, using points' width.
 *   - polygon: for recalculating saved rivers without width data, seed inside existing polygons.
 *     If banks were raised manually after carving, retain only submerged portions of that area.
 */
export type RiverPolygonBand =
  | { kind: "spline" }
  | { kind: "polygon"; polygon: WaterPolygonGeoJson };

/**
 * Extracts one river's area polygon from carved terrain, with holes for sandbars/islands.
 *
 * The area is the set of grid points reachable from seeds with terrain below water elevation,
 * defined by waterY at the nearest point on the profile line. This applies the lake-hole idea
 * (regions below water inside a footprint) to downstream-varying water, additionally requiring
 * connectivity to the channel so water can actually reach it. Without connectivity, unrelated
 * low depressions beyond the bank blending band could be included despite separating levees.
 * Find terrain/water intersections in a survey band expanded from river width by the grid's
 * half-diagonal, blending band, and one-cell margin. Saved rivers without width data are
 * recalculated within their existing polygon bounds.
 *
 * Excludes:
 *   - Elevations <=0 m (sea), already covered by the sea plane; overlapping river water would Z-fight.
 *   - Regions below existing lake levels (water_surfaces), covered by lake water.
 *   - Grid points marked in claimed. Process larger-discharge rivers first so tributaries
 *     do not duplicate main-stem areas at confluences, avoiding overlapping-water Z-fighting.
 * Writes extracted grid points back to claimed.
 *
 * Builds the field over reached-area bounds with one negative-valued padding cell to ensure
 * closure. Returns null if the area is empty.
 */
export function buildRiverPolygon(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  mapWidth: number,
  mapHeight: number,
  river: GeneratedRiver,
  band: RiverPolygonBand,
  claimed: Uint8Array,
  waterSurfaces: WaterSurfacePayload[],
): WaterPolygonGeoJson | null {
  const pts = river.points;
  if (pts.length < 2) return null;
  const halfW = mapWidth / 2;
  const halfH = mapHeight / 2;
  // --- Seed grid bounds, expanded by the flood-reachable area ---
  let cMin = Infinity;
  let cMax = -Infinity;
  let rMin = Infinity;
  let rMax = -Infinity;
  const extend = (wx: number, wz: number, reach: number) => {
    cMin = Math.min(cMin, Math.floor((wx - reach + halfW) / spacing));
    cMax = Math.max(cMax, Math.ceil((wx + reach + halfW) / spacing));
    rMin = Math.min(rMin, Math.floor((wz - reach + halfH) / spacing));
    rMax = Math.max(rMax, Math.ceil((wz + reach + halfH) / spacing));
  };
  const floodReach = spacing;
  if (band.kind === "spline") {
    for (const p of pts) extend(p.x, p.z, p.width / 2 + spacing / Math.SQRT2 + blendWidth(p.width, spacing) + floodReach);
  } else {
    const b = polygonBounds(band.polygon);
    if (!b) return null;
    extend(b.minX, b.minZ, floodReach);
    extend(b.maxX, b.maxZ, floodReach);
  }
  cMin = Math.max(0, cMin);
  rMin = Math.max(0, rMin);
  cMax = Math.min(cols - 1, cMax);
  rMax = Math.min(rows - 1, rMax);
  if (!isFinite(cMin) || cMin > cMax || rMin > rMax) return null;

  // Field size is bw × bh including one padding cell. Field (i, j) ↔ terrain (c, r) = (cMin + i - 1, rMin + j - 1).
  const bw = cMax - cMin + 3;
  const bh = rMax - rMin + 3;
  const originX = (cMin - 1) * spacing - halfW;
  const originZ = (rMin - 1) * spacing - halfH;
  const field = new Float32Array(bw * bh).fill(-1);
  const seed = new Uint8Array(bw * bh);
  let boundaryDistance: Float32Array | null = null;

  if (band.kind === "spline") {
    // Seed cells within each segment's footprint, as in carveRivers.
    for (let s = 0; s < pts.length - 1; s++) {
      const a = pts[s];
      const b = pts[s + 1];
      const segX = b.x - a.x;
      const segZ = b.z - a.z;
      const len2 = segX * segX + segZ * segZ;
      if (len2 < 1e-12) continue;
      const wMax = Math.max(a.width, b.width);
      const reach = wMax / 2 + blendWidth(wMax, spacing);
      const sc0 = Math.max(cMin, Math.floor((Math.min(a.x, b.x) - reach + halfW) / spacing));
      const sc1 = Math.min(cMax, Math.ceil((Math.max(a.x, b.x) + reach + halfW) / spacing));
      const sr0 = Math.max(rMin, Math.floor((Math.min(a.z, b.z) - reach + halfH) / spacing));
      const sr1 = Math.min(rMax, Math.ceil((Math.max(a.z, b.z) + reach + halfH) / spacing));
      for (let r = sr0; r <= sr1; r++) {
        const wz = r * spacing - halfH;
        for (let c = sc0; c <= sc1; c++) {
          const wx = c * spacing - halfW;
          let t = ((wx - a.x) * segX + (wz - a.z) * segZ) / len2;
          if (t < 0) t = 0;
          else if (t > 1) t = 1;
          const d = Math.hypot(wx - (a.x + segX * t), wz - (a.z + segZ * t));
          const w = a.width + (b.width - a.width) * t;
          if (d > w / 2 + blendWidth(w, spacing)) continue;
          seed[(r - rMin + 1) * bw + (c - cMin + 1)] = 1;
        }
      }
    }
  } else {
    // Seed grid points inside the existing polygon.
    const base = rasterizePolygonMask(band.polygon, originX, originZ, spacing, bw, bh);
    for (let j = 1; j < bh - 1; j++) {
      for (let i = 1; i < bw - 1; i++) {
        if (base[j * bw + i]) seed[j * bw + i] = 1;
      }
    }
    // Bound by the saved shoreline. Using simple ±1 inside/outside values shifts interpolated
    // contours outward for deeper rivers, expanding water with every recalculation.
    boundaryDistance = new Float32Array(bw * bh).fill(spacing * 2);
    for (const ring of polygonRings(band.polygon)) {
      for (let k = 1; k < ring.length; k++) {
        const a = ring[k - 1], b = ring[k];
        const dx = b[0] - a[0], dz = b[1] - a[1], len2 = dx * dx + dz * dz;
        const i0 = Math.max(0, Math.floor((Math.min(a[0], b[0]) - originX) / spacing) - 2);
        const i1 = Math.min(bw - 1, Math.ceil((Math.max(a[0], b[0]) - originX) / spacing) + 2);
        const j0 = Math.max(0, Math.floor((Math.min(a[1], b[1]) - originZ) / spacing) - 2);
        const j1 = Math.min(bh - 1, Math.ceil((Math.max(a[1], b[1]) - originZ) / spacing) + 2);
        for (let j = j0; j <= j1; j++) for (let i = i0; i <= i1; i++) {
          const x = originX + i * spacing, z = originZ + j * spacing;
          const t = len2 > 0 ? Math.max(0, Math.min(1, ((x - a[0]) * dx + (z - a[1]) * dz) / len2)) : 0;
          const index = j * bw + i;
          boundaryDistance[index] = Math.min(boundaryDistance[index], Math.hypot(x - a[0] - dx * t, z - a[1] - dz * t));
        }
      }
    }
  }

  // --- Lazily test whether each grid point is below water and eligible for the river area ---
  const sampler = new RiverProfileSampler(pts.map((p) => [p.x, p.waterY, p.z]));
  const lakes = waterSurfaces
    .filter((ws) => typeof ws?.water_level === "number")
    .map((ws) => ({ level: ws.water_level, polygon: ws.polygon, bounds: polygonBounds(ws.polygon) }));
  // 0 = unevaluated, 1 = candidate, 2 = outside.
  const state = new Uint8Array(bw * bh);
  const evaluate = (i: number, j: number): boolean => {
    const fi = j * bw + i;
    if (state[fi] !== 0) return state[fi] === 1;
    let ok = false;
    if (i >= 1 && i < bw - 1 && j >= 1 && j < bh - 1) {
      const c = cMin + i - 1;
      const r = rMin + j - 1;
      const h = elev[r * cols + c];
      if (h > 0 && !claimed[r * cols + c]) {
        const wx = c * spacing - halfW;
        const wz = r * spacing - halfH;
        const near = sampler.nearest(wx, wz);
        // Search water edges out to surveyed bank limits; do not clip water at the specified width itself.
        const width = pts[near.seg].width + (pts[near.seg + 1].width - pts[near.seg].width) * near.t;
        const inBand = band.kind === "spline"
          ? near.dist <= width / 2 + spacing / Math.SQRT2 + blendWidth(width, spacing) + spacing
          : seed[fi] !== 0;
        const waterDepth = sampler.heightOn(near.seg, near.t) - h - RIVER.polygonWaterEps;
        const depth = boundaryDistance
          ? Math.min(waterDepth, (seed[fi] ? 1 : -1) * boundaryDistance[fi])
          : inBand ? waterDepth : -1;
        if (depth <= 0) field[fi] = depth;
        if (depth > 0) {
          let inLake = false;
          for (const lake of lakes) {
            const b = lake.bounds;
            if (!b || h > lake.level) continue;
            if (wx < b.minX || wx > b.maxX || wz < b.minZ || wz > b.maxZ) continue;
            if (pointInPolygon(wx, wz, lake.polygon)) {
              inLake = true;
              break;
            }
          }
          if (!inLake) {
            ok = true;
            field[fi] = depth;
          }
        }
      }
    }
    state[fi] = ok ? 1 : 2;
    return ok;
  };

  // --- Breadth-first expansion from seeds through four neighbors to extract connected components ---
  const queueI: number[] = [];
  const queueJ: number[] = [];
  const reached = new Uint8Array(bw * bh);
  for (let j = 1; j < bh - 1; j++) {
    for (let i = 1; i < bw - 1; i++) {
      const fi = j * bw + i;
      if (seed[fi] && evaluate(i, j)) {
        reached[fi] = 1;
        queueI.push(i);
        queueJ.push(j);
      }
    }
  }
  let head = 0;
  while (head < queueI.length) {
    const i = queueI[head];
    const j = queueJ[head];
    head++;
    const neighbors: [number, number][] = [[i - 1, j], [i + 1, j], [i, j - 1], [i, j + 1]];
    for (const [ni, nj] of neighbors) {
      const nfi = nj * bw + ni;
      if (reached[nfi]) continue;
      if (!evaluate(ni, nj)) continue;
      reached[nfi] = 1;
      queueI.push(ni);
      queueJ.push(nj);
    }
  }
  // Reset unreached candidates (lowlands disconnected from seeds) to outside.
  for (let fi = 0; fi < field.length; fi++) {
    if (!reached[fi] && field[fi] > 0) field[fi] = -1;
  }

  const polygon = extractRegionPolygon(field, bw, bh, originX, originZ, spacing, RIVER.polygonSimplifyFactor * spacing);
  if (!polygon) return null;

  // Write included grid points back to claimed for exclusion by subsequent rivers.
  for (let j = 1; j < bh - 1; j++) {
    const r = rMin + j - 1;
    for (let i = 1; i < bw - 1; i++) {
      if (field[j * bw + i] > 0) claimed[r * cols + (cMin + i - 1)] = 1;
    }
  }
  return polygon;
}
