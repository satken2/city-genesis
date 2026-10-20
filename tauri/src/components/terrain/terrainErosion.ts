/**
 * Erosion algorithms for the terrain editor's "Automatic editing".
 *
 * Pure functions called by TerrainEditorCanvas, independent of React and canvas rendering.
 * Accepts the editor's row-major Float32Array (rows × cols, spacing in meters, elevations at
 * or below 0 treated as sea) unchanged and modifies it in place. The caller must rebuild
 * textures and contours.
 *
 * Two erosion types:
 *   - Thermal erosion: simulates sediment collapsing from slopes above the angle of repose
 *     and accumulating at their foot, making cliffs gentler.
 *   - Hydraulic erosion: simulates raindrop particles eroding, transporting, and depositing
 *     sediment as they descend, carving valleys/alluvial fans. Drops reaching SEA_LEVEL
 *     disappear; carried sediment slides down underwater slopes and settles on sea floor
 *     gentler than the angle of repose (settleSedimentUnderwater).
 */

/** Thermal erosion parameters exposed in the UI. */
export type ThermalErosionParams = {
  /** Angle of repose (degrees). Steeper slopes collapse. About 30–35° for sand; larger for rock. */
  talusAngleDeg: number;
  /** Iteration count. More iterations smooth a wider area. */
  iterations: number;
};

/** Hydraulic erosion parameters exposed in the UI. */
export type HydraulicErosionParams = {
  /** Number of simulated raindrops. More increases erosion and computation time proportionally. */
  droplets: number;
  /** Erosion strength (0..1), scaling sediment removed by one drop per step. */
  erodeStrength: number;
};

/**
 * Region specification for selection-based erosion.
 *
 * Passes the terrain editor's selection mask (0..1 weights per grid point) and bounding
 * rectangle unchanged. mask is row-major with rows*cols elements; unselected cells have
 * weight 0. To avoid React/editor dependencies, defines its own minimal field shape instead
 * of importing the editor's DirtyRect.
 *
 * Erosion starts in the selection rather than being strictly confined to it: sediment and
 * raindrops may flow somewhat beyond the boundary, blending continuously into the surroundings.
 */
export type ErosionRegion = {
  /** Per-grid-point selection weights (0..1), row-major, length rows*cols. */
  mask: Float32Array;
  /** Selection bounding rectangle (inclusive grid row/column ranges). */
  rMin: number;
  rMax: number;
  cMin: number;
  cMax: number;
};

/** Fraction of excess material moved per thermal iteration. Limited to half because 1.0 tends to oscillate. */
const THERMAL_RATE = 0.5;

/**
 * Sea elevation (meters). Locations at/below this are sea, matching near.bin's elevation <=0
 * contract. In hydraulic erosion, a drop reaching this level hits a large body of seawater,
 * loses velocity (and carrying capacity), deposits all carried sediment underwater, and ends.
 */
const SEA_LEVEL = 0;

/**
 * Internal hydraulic erosion constants (values commonly used in Hans Beyer's droplet method).
 * Exposing all would create too many controls, so only the influential raindrop count and
 * erosion strength are parameters; the rest remain fixed here.
 */
const HYDRAULIC = {
  /** Inertia (0..1). Higher values keep drops straighter; lower values follow gradients more closely. */
  inertia: 0.05,
  /** Carrying capacity coefficient. Higher values let each drop carry more sediment. */
  capacityFactor: 4,
  /** Minimum gradient for capacity calculation, preventing zero capacity and immediate deposition on flat ground. */
  minSlope: 0.01,
  /** Fraction deposited per step when capacity is exceeded (0..1). */
  depositSpeed: 0.3,
  /** Water evaporation rate per step (0..1). */
  evaporate: 0.01,
  /** Gravity coefficient for updating speed from height differences. */
  gravity: 4,
  /** Maximum steps per raindrop, bounding computation and preventing infinite loops. */
  maxLifetime: 30,
  /** Erosion-spreading brush radius in cells, preventing single-point vertical pits. */
  erosionRadius: 3,
  /** Retry count for landing on land (above SEA_LEVEL). Discard drops that repeatedly land in the sea. */
  spawnRetries: 8,
} as const;

/**
 * Internal constants for underwater sediment settling after a drop reaches SEA_LEVEL.
 *
 * A drop moves only one cell per step, so its water-entry point is always within one cell of
 * the coastline. Depositing everything there would fill only one offshore row vertically to
 * sea level regardless of depth, unnaturally growing an island one cell at a time with vertical
 * cliffs. Instead, slide sediment down sea-floor slopes like a turbidity current and settle it
 * only where slopes are gentler than the angle of repose (settleSedimentUnderwater).
 */
const UNDERWATER = {
  /** Underwater angle of repose (degrees). Sediment keeps sliding on steeper underwater slopes. */
  talusAngleDeg: 30,
  /** Sediment fraction settling per step on a flat sea floor, decreasing as slope approaches the angle of repose. */
  depositRate: 0.3,
  /** Maximum settling steps, bounding computation. Deposit any remainder at the final position. */
  maxSteps: 100,
  /** Once carried sediment falls below this amount (meters), deposit the remainder at the current position and end. */
  minSediment: 1e-3,
} as const;

/**
 * Applies thermal erosion (angle-of-repose simulation) to elevations.
 *
 * Each iteration scans cells and compares height differences to eight neighbors against
 * tan(angle of repose) × horizontal distance. Move sediment proportional to the excess
 * into lower neighbors. Repetition collapses cliffs toward the angle of repose.
 *
 * Accumulate deltas in a temporary buffer and apply them together after each iteration;
 * in-place updates during scanning would bias collapse in scan order (top left → bottom right).
 * Subsea cells (elevation <=0) are treated normally: underwater collapse causes no practical
 * display/gameplay harm and improves natural blending at shorelines.
 *
 * With region, restrict collapse sources to selected bounds and attenuate outflow by mask
 * weights. Weights fall smoothly to 0 at boundaries, avoiding artificial cliffs. Sediment may
 * still pass to lower neighbors outside the selection, allowing slight smoothing there.
 * Without region, process the whole map.
 */
export function applyThermalErosion(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  params: ThermalErosionParams,
  region?: ErosionRegion,
): void {
  const count = rows * cols;
  // Scan bounds, limited to region's rectangle when present and clamped to rows/cols.
  const rLo = region ? Math.max(0, region.rMin) : 0;
  const rHi = region ? Math.min(rows - 1, region.rMax) : rows - 1;
  const cLo = region ? Math.max(0, region.cMin) : 0;
  const cHi = region ? Math.min(cols - 1, region.cMax) : cols - 1;
  // Expand delta application bounds by one cell on each side to include sediment assigned to neighbors.
  const applyRLo = Math.max(0, rLo - 1);
  const applyRHi = Math.min(rows - 1, rHi + 1);
  const applyCLo = Math.max(0, cLo - 1);
  const applyCHi = Math.min(cols - 1, cHi + 1);
  // Eight-neighbor offsets (row, column) and collapse thresholds (height differences above these collapse).
  const talusTan = Math.tan((params.talusAngleDeg * Math.PI) / 180);
  const thresholdOrtho = talusTan * spacing; // Cardinal neighbors.
  const thresholdDiag = talusTan * spacing * Math.SQRT2; // Diagonals.
  const neighborOffsets = [
    { dr: -1, dc: 0, threshold: thresholdOrtho },
    { dr: 1, dc: 0, threshold: thresholdOrtho },
    { dr: 0, dc: -1, threshold: thresholdOrtho },
    { dr: 0, dc: 1, threshold: thresholdOrtho },
    { dr: -1, dc: -1, threshold: thresholdDiag },
    { dr: -1, dc: 1, threshold: thresholdDiag },
    { dr: 1, dc: -1, threshold: thresholdDiag },
    { dr: 1, dc: 1, threshold: thresholdDiag },
  ];

  // Per-iteration elevation delta buffer, ensuring symmetric collapse independent of scan order.
  const delta = new Float32Array(count);
  // Temporary excess values, reused for eight neighbors of each cell.
  const excess = new Float32Array(neighborOffsets.length);
  const excessIdx = new Int32Array(neighborOffsets.length);

  for (let iter = 0; iter < params.iterations; iter++) {
    delta.fill(0);
    for (let r = rLo; r <= rHi; r++) {
      const rowBase = r * cols;
      for (let c = cLo; c <= cHi; c++) {
        const idx = rowBase + c;
        // Cell selection weight when region is present. Weight-0 cells do not collapse.
        const weight = region ? region.mask[idx] : 1;
        if (weight <= 0) continue;
        const h = elev[idx];

        // Find neighbors lower by more than the threshold, summing excesses and finding the maximum.
        let excessCount = 0;
        let totalExcess = 0;
        let maxExcess = 0;
        for (const n of neighborOffsets) {
          const nr = r + n.dr;
          const nc = c + n.dc;
          if (nr < 0 || nr >= rows || nc < 0 || nc >= cols) continue;
          const nIdx = nr * cols + nc;
          const e = h - elev[nIdx] - n.threshold;
          if (e <= 0) continue;
          excess[excessCount] = e;
          excessIdx[excessCount] = nIdx;
          excessCount++;
          totalExcess += e;
          if (e > maxExcess) maxExcess = e;
        }
        if (excessCount === 0) continue;

        // Distribute part of the maximum excess among neighbors in proportion to their excess values.
        // With region, attenuate outflow by selection weight to blend boundaries smoothly.
        const moveTotal = THERMAL_RATE * (maxExcess / 2) * weight;
        delta[idx] -= moveTotal;
        for (let i = 0; i < excessCount; i++) {
          delta[excessIdx[i]] += moveTotal * (excess[i] / totalExcess);
        }
      }
    }
    // With region, nonzero delta occurs only in scan bounds plus one neighboring cell, so apply only there.
    if (region) {
      for (let r = applyRLo; r <= applyRHi; r++) {
        const rowBase = r * cols;
        for (let c = applyCLo; c <= applyCHi; c++) {
          const i = rowBase + c;
          elev[i] += delta[i];
        }
      }
    } else {
      for (let i = 0; i < count; i++) elev[i] += delta[i];
    }
  }
}

/**
 * Bilinearly interpolated height and gradient at continuous cell coordinates.
 * Gradient is elevation change per cell.
 */
type HeightAndGradient = { height: number; gradX: number; gradZ: number };

/** Bilinearly interpolates height and gradient at continuous cell coordinates (x, z). */
function sampleHeightAndGradient(
  elev: Float32Array,
  cols: number,
  x: number,
  z: number,
  out: HeightAndGradient,
): void {
  const cx = Math.floor(x);
  const cz = Math.floor(z);
  const fx = x - cx;
  const fz = z - cz;
  const base = cz * cols + cx;
  const h00 = elev[base];
  const h10 = elev[base + 1];
  const h01 = elev[base + cols];
  const h11 = elev[base + cols + 1];
  // Compute X/Z partial derivatives directly from bilinear coefficients.
  out.gradX = (h10 - h00) * (1 - fz) + (h11 - h01) * fz;
  out.gradZ = (h01 - h00) * (1 - fx) + (h11 - h10) * fx;
  out.height = h00 * (1 - fx) * (1 - fz) + h10 * fx * (1 - fz) + h01 * (1 - fx) * fz + h11 * fx * fz;
}

/** Adds sediment amount (meters) to four grid points around continuous (x, z), using bilinear weights. */
function depositBilinear(elev: Float32Array, cols: number, x: number, z: number, amount: number): void {
  const cx = Math.floor(x);
  const cz = Math.floor(z);
  const fx = x - cx;
  const fz = z - cz;
  const idx = cz * cols + cx;
  elev[idx] += amount * (1 - fx) * (1 - fz);
  elev[idx + 1] += amount * fx * (1 - fz);
  elev[idx + cols] += amount * (1 - fx) * fz;
  elev[idx + cols + 1] += amount * fx * fz;
}

/**
 * Slides and settles sediment carried by a drop reaching the sea along underwater slopes.
 *
 * The drop disappears on hitting seawater and losing velocity. Depositing all its sediment
 * at entry would fill vertical columns just offshore (see UNDERWATER constants). Actual
 * sediment keeps sliding underwater and accumulates on slopes below the angle of repose.
 * Move it one cell at a time down the steepest gradient like a turbidity current, depositing
 * more where the slope is gentler.
 *
 * Sediment entering from cliffs above deep water barely settles on steep cliff faces, instead
 * depositing on the sea floor below. If deposits grow steeper than the angle of repose,
 * subsequent sediment slides farther offshore, naturally shaping fans/cones at or below that
 * angle. New land appears only once deposits rise from sea floor to sea level.
 *
 * On flat sea floor, retain the previous direction through inertia and move straight while
 * settling. Discard sediment leaving the map as deposition outside the map.
 */
function settleSedimentUnderwater(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  startX: number,
  startZ: number,
  startDirX: number,
  startDirZ: number,
  sediment: number,
  hg: HeightAndGradient,
): void {
  // Interpolation/movement access cx+1 / cz+1, so use the same range as drops: [0, cols-1) × [0, rows-1).
  const maxX = cols - 1;
  const maxZ = rows - 1;
  const talusTan = Math.tan((UNDERWATER.talusAngleDeg * Math.PI) / 180);
  let x = startX;
  let z = startZ;
  let dirX = startDirX;
  let dirZ = startDirZ;
  for (let step = 0; step < UNDERWATER.maxSteps; step++) {
    sampleHeightAndGradient(elev, cols, x, z, hg);
    // Local slope tangent. gradX/gradZ are elevation change (meters) per cell, so divide by spacing.
    const slopeTan = Math.hypot(hg.gradX, hg.gradZ) / spacing;
    // No settling above the angle of repose; gentler slopes approach depositRate.
    const settleRatio = Math.max(0, 1 - slopeTan / talusTan) * UNDERWATER.depositRate;
    if (settleRatio > 0) {
      const deposit = sediment * settleRatio;
      depositBilinear(elev, cols, x, z, deposit);
      sediment -= deposit;
      if (sediment < UNDERWATER.minSediment) return;
    }
    // Move one cell along steepest descent; if flat, retain the previous direction through inertia.
    const len = Math.hypot(hg.gradX, hg.gradZ);
    if (len > 1e-8) {
      dirX = -hg.gradX / len;
      dirZ = -hg.gradZ / len;
    }
    x += dirX;
    z += dirZ;
    if (x < 0 || x >= maxX || z < 0 || z >= maxZ) return; // Outflow beyond the map.
  }
  // If steps are exhausted, deposit the remainder here to avoid losing mass.
  depositBilinear(elev, cols, x, z, sediment);
}

/** One erosion brush point (offset from center and normalized weight). */
type BrushPoint = { dr: number; dc: number; weight: number };

/**
 * Precomputes a circular erosion-spreading brush with linear falloff toward the edge and
 * weights summing to 1. Reuse the table during hydraulic erosion to avoid per-drop computation.
 */
function buildErosionBrush(radius: number): BrushPoint[] {
  const points: BrushPoint[] = [];
  let totalWeight = 0;
  for (let dr = -radius; dr <= radius; dr++) {
    for (let dc = -radius; dc <= radius; dc++) {
      const dist = Math.sqrt(dr * dr + dc * dc);
      if (dist > radius) continue;
      const weight = 1 - dist / radius;
      if (weight <= 0) continue;
      points.push({ dr, dc, weight });
      totalWeight += weight;
    }
  }
  for (const p of points) p.weight /= totalWeight;
  return points;
}

/**
 * Applies particle-based hydraulic erosion (droplet method) to elevations.
 *
 * Drop particles individually onto random land, repeating until their lifetime ends:
 *   1. Sample the gradient and move one cell in a direction blended with inertia.
 *   2. Calculate carrying capacity from height change, speed, and water amount.
 *   3. If carrying too much sediment or moving uphill, deposit at the previous position;
 *      otherwise erode around that position with a brush and pick up sediment.
 *   4. Update speed and water (accelerate downhill and gradually evaporate).
 * When entering sea (elevation <= SEA_LEVEL), clamp the height change to the drop until
 * contact with sea level, then erode/deposit normally before ending the drop: hitting a large
 * seawater body means zero velocity and capacity. Underwater cliff height therefore does not
 * contribute to erosion; only the above-water cliff erodes, gradually cutting cliff-edged
 * islands back from the upper edge. Pass carried sediment to settleSedimentUnderwater to
 * slide down sea-floor slopes and settle below the angle of repose, forming fans below deep
 * cliffs. Depositing it all at entry, always within one cell of shore, would fill vertical columns
 * to sea level regardless of depth and expand the island endlessly one cell at a time. With
 * settling, new land appears only after many thousands of deposits raise elevations above
 * SEA_LEVEL, so river-mouth plains (deltas) advance only as the sea actually fills.
 *
 * Positions use cell units, but capacity and speed use dimensionless slope (height difference
 * in meters / spacing in meters). Raw meter height differences would make capacity effectively
 * unlimited, removing tens of meters at cliff edges in one step. This is a standard terrain-editor
 * simplification prioritizing visible valley carving over strict physical accuracy.
 *
 * With region, drop positions are restricted to selected bounds and accepted with mask-weight
 * probability, reducing rain smoothly near edges. Once landed, flow/erosion/deposition follows
 * the same physics as whole-map runs, producing continuous valleys without boundary steps.
 * Drops may cross boundaries and slightly erode outside. Scale the actual particle count by
 * selected area (sum of mask weights), preserving erosion density per unit area for the same
 * droplets setting. Without region, process the whole map as usual.
 */
export function applyHydraulicErosion(
  elev: Float32Array,
  rows: number,
  cols: number,
  spacing: number,
  params: HydraulicErosionParams,
  region?: ErosionRegion,
): void {
  const brush = buildErosionBrush(HYDRAULIC.erosionRadius);
  const hg: HeightAndGradient = { height: 0, gradX: 0, gradZ: 0 };
  // Interpolation/movement access cx+1 / cz+1, so drops move within [0, cols-1) × [0, rows-1).
  const maxX = cols - 1;
  const maxZ = rows - 1;

  // Raindrop spawn bounds in cell coordinates, restricted to region's rectangle when present.
  const spawnCLo = region ? Math.max(0, region.cMin) : 0;
  const spawnCHi = region ? Math.min(maxX, region.cMax) : maxX;
  const spawnRLo = region ? Math.max(0, region.rMin) : 0;
  const spawnRHi = region ? Math.min(maxZ, region.rMax) : maxZ;

  // Actual particle count. With region, scale by selected area (sum of mask weights / grid-point count)
  // to avoid excessive erosion from dropping the same count into a small selection.
  let dropletCount = params.droplets;
  if (region) {
    let weightSum = 0;
    for (let r = spawnRLo; r <= spawnRHi; r++) {
      const rowBase = r * cols;
      for (let c = spawnCLo; c <= spawnCHi; c++) weightSum += region.mask[rowBase + c];
    }
    const fraction = weightSum / (rows * cols);
    dropletCount = Math.max(1, Math.round(params.droplets * fraction));
  }

  for (let d = 0; d < dropletCount; d++) {
    // Retry a few times to land on land (above SEA_LEVEL); discard drops repeatedly selecting sea.
    // With region, also use selection weight as landing probability to confine starting points.
    let x = 0;
    let z = 0;
    let spawned = false;
    for (let retry = 0; retry < HYDRAULIC.spawnRetries; retry++) {
      x = spawnCLo + Math.random() * (spawnCHi - spawnCLo);
      z = spawnRLo + Math.random() * (spawnRHi - spawnRLo);
      sampleHeightAndGradient(elev, cols, x, z, hg);
      if (hg.height <= SEA_LEVEL) continue;
      if (region && Math.random() >= region.mask[Math.round(z) * cols + Math.round(x)]) continue;
      spawned = true;
      break;
    }
    if (!spawned) continue;

    let dirX = 0;
    let dirZ = 0;
    let speed = 1;
    let water = 1;
    let sediment = 0;

    for (let life = 0; life < HYDRAULIC.maxLifetime; life++) {
      sampleHeightAndGradient(elev, cols, x, z, hg);
      const oldHeight = hg.height;
      const oldX = x;
      const oldZ = z;

      // Blend inertia with the negative gradient (downhill) to choose direction.
      dirX = dirX * HYDRAULIC.inertia - hg.gradX * (1 - HYDRAULIC.inertia);
      dirZ = dirZ * HYDRAULIC.inertia - hg.gradZ * (1 - HYDRAULIC.inertia);
      const len = Math.sqrt(dirX * dirX + dirZ * dirZ);
      if (len < 1e-8) break; // No direction available on flat terrain.
      dirX /= len;
      dirZ /= len;
      x += dirX;
      z += dirZ;
      if (x < 0 || x >= maxX || z < 0 || z >= maxZ) break; // Outflow beyond the map.

      sampleHeightAndGradient(elev, cols, x, z, hg);

      // Detect sea-level arrival. A drop entering water disappears after this step's erosion/deposition
      // (reachedSea branch below). Clamp deltaH to the height drop until sea-level contact,
      // since the drop loses speed there; underwater cliff height must not contribute
      // to erosion/capacity. Without clamping, each particle could remove tens of meters
      // from cliffs above deep water.
      const reachedSea = hg.height <= SEA_LEVEL;
      const deltaH = (reachedSea ? SEA_LEVEL : hg.height) - oldHeight;
      // Dimensionless slope, positive downhill, keeping capacity/speed independent of map elevation scale.
      const slope = -deltaH / spacing;

      // Grid cell at the previous position, used as the erosion brush center.
      const cx = Math.floor(oldX);
      const cz = Math.floor(oldZ);

      // Carrying capacity based on downhill slope, speed, and water amount (meters of removable elevation).
      const capacity = Math.max(slope, HYDRAULIC.minSlope) * speed * water * HYDRAULIC.capacityFactor;

      if (sediment > capacity || deltaH > 0) {
        // Over capacity or uphill: deposit at the previous position.
        // Uphill, fill up to the height difference; otherwise deposit part of the excess.
        const deposit =
          deltaH > 0 ? Math.min(deltaH, sediment) : (sediment - capacity) * HYDRAULIC.depositSpeed;
        sediment -= deposit;
        depositBilinear(elev, cols, oldX, oldZ, deposit);
      } else {
        // Spare capacity: erode around the previous position with a brush.
        // Do not erode more than the height difference, avoiding unnatural holes along the travel direction.
        const erode = Math.min((capacity - sediment) * params.erodeStrength, -deltaH);
        for (const p of brush) {
          const br = cz + p.dr;
          const bc = cx + p.dc;
          if (br < 0 || br >= rows || bc < 0 || bc >= cols) continue;
          const bIdx = br * cols + bc;
          const amount = erode * p.weight;
          // Erode only down to sea level in each cell, without deepening the sea floor.
          const above = elev[bIdx] - SEA_LEVEL;
          const actual = above < amount ? Math.max(0, above) : amount;
          elev[bIdx] -= actual;
          sediment += actual;
        }
      }

      // A drop hitting sea ends with zero velocity/capacity; pass its carried sediment to underwater
      // settling, sliding it down sea-floor slopes instead of depositing everything at entry.
      if (reachedSea) {
        settleSedimentUnderwater(elev, rows, cols, spacing, x, z, dirX, dirZ, sediment, hg);
        break;
      }

      // Accelerate downhill, slow uphill, and gradually reduce water by evaporation.
      speed = Math.sqrt(Math.max(0, speed * speed + slope * HYDRAULIC.gravity));
      water *= 1 - HYDRAULIC.evaporate;
    }
  }
}
