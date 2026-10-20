import type { Rgb } from "./terrainData";

/** Uses beige lowlands and green highlands so elevations remain readable regardless of ground cover. */
export const ELEVATION_RAMP: Rgb[] = [
  [225, 220, 193], [203, 209, 160], [165, 183, 120], [117, 135, 85],
];
/** Lights from northwest (world -X, +Z) at 50° altitude with 2× vertical exaggeration to reveal relief. */
const HILLSHADE_ALTITUDE = 50 * Math.PI / 180;
const HILLSHADE_LIGHT = [
  -Math.cos(HILLSHADE_ALTITUDE) / Math.SQRT2,
  Math.sin(HILLSHADE_ALTITUDE),
  Math.cos(HILLSHADE_ALTITUDE) / Math.SQRT2,
] as const;
const HILLSHADE_VERTICAL_EXAGGERATION = 2;

/** Stretches land shading to 0–1. Flat terrain uses neutral 0.5, preserving elevation colors. */
export function buildTerrainHillshade(elev: Float32Array, rows: number, cols: number, spacing: number): Float32Array {
  const shade = new Float32Array(rows * cols);
  const [lx, ly, lz] = HILLSHADE_LIGHT;
  let min = Infinity;
  let max = -Infinity;
  for (let r = 0; r < rows; r++) {
    const south = Math.max(0, r - 1);
    const north = Math.min(rows - 1, r + 1);
    for (let c = 0; c < cols; c++) {
      const i = r * cols + c;
      if (elev[i] <= 0) continue;
      const west = Math.max(0, c - 1);
      const east = Math.min(cols - 1, c + 1);
      const dx = (elev[r * cols + east] - elev[r * cols + west]) / (Math.max(1, east - west) * spacing);
      const dz = (elev[north * cols + c] - elev[south * cols + c]) / (Math.max(1, north - south) * spacing);
      const nx = -dx * HILLSHADE_VERTICAL_EXAGGERATION;
      const nz = -dz * HILLSHADE_VERTICAL_EXAGGERATION;
      // Array rows run south to north, so normals use world +Z gradient rather than screen Y.
      const intensity = Math.max(0, (nx * lx + ly + nz * lz) / Math.hypot(nx, 1, nz));
      shade[i] = intensity;
      min = Math.min(min, intensity);
      max = Math.max(max, intensity);
    }
  }
  const range = max - min;
  if (range > 1e-6) {
    // Thumbnails interpolate between grid points; keep sea values neutral to avoid a dark shoreline rim.
    for (let i = 0; i < shade.length; i++) {
      shade[i] = elev[i] > 0 ? Math.max(0, Math.min(1, (shade[i] - min) / range)) : 0.5;
    }
  } else {
    shade.fill(0.5);
  }
  return shade;
}

/** Overlays soft-light shading on the elevation colors shared by city maps and Scenario images. */
export function terrainReliefColor(elev: number, minLand: number, maxLand: number, shade: number): Rgb {
  const t = Math.max(0, Math.min(1, (elev - minLand) / (maxLand - minLand)));
  const segment = t * (ELEVATION_RAMP.length - 1);
  const index = Math.min(ELEVATION_RAMP.length - 2, Math.floor(segment));
  const from = ELEVATION_RAMP[index];
  const to = ELEVATION_RAMP[index + 1];
  const f = segment - index;
  const light = 2 * shade - 1;
  return from.map((channel, k) => {
    const color = channel + (to[k] - channel) * f;
    return color + light * color * (1 - color / 255);
  }) as Rgb;
}
