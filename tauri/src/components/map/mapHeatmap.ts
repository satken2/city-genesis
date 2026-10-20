/** Share colors and LOD selection between environmental overlays and source display. */
export type CityMapHeatmapKind = "none" | "noise" | "pollution" | "crime" | "sunshine";
export type CityMapHeatmapActiveKind = Exclude<CityMapHeatmapKind, "none">;
export type CityMapHeatmapMode = "overlay" | "sources";
export type HeatmapLod = { rows: number; cols: number; spacing: number };
export type HeatmapGrid = {
  origin_x: number; origin_z: number; width_m: number; height_m: number;
  min: number; max: number; levels: HeatmapLod[];
};
export type HeatmapSources = {
  path_ids: number[]; path_values: number[];
  building_ids: number[]; building_values: number[];
  min: number; max: number;
};
export const HEATMAP_RAMPS: Record<CityMapHeatmapActiveKind, [number, number, number][]> = {
  noise: [[253, 230, 138], [251, 146, 60], [230, 81, 0]],
  pollution: [[209, 231, 221], [149, 82, 191], [74, 20, 140]],
  crime: [[255, 205, 210], [229, 87, 87], [183, 28, 28]],
  sunshine: [[255, 245, 157], [144, 164, 174], [55, 71, 79]],
};
const MAX_OVERLAY_ALPHA = 0.7;

export function effectiveHeatmapMode(kind: CityMapHeatmapKind, mode: CityMapHeatmapMode): CityMapHeatmapMode {
  return mode === "sources" && (kind === "noise" || kind === "pollution") ? "sources" : "overlay";
}

/** Choose an LOD whose pixels are not too fine on screen, fetching detailed images only when zoomed in. */
export function pickHeatmapLod(levels: HeatmapLod[], zoom: number): number {
  let lod = 0;
  while (lod + 1 < levels.length && levels[lod + 1].spacing * zoom <= 1.5) lod++;
  return lod;
}

function rgbAt(kind: CityMapHeatmapActiveKind, t: number): number[] {
  const ramp = HEATMAP_RAMPS[kind];
  const position = t * (ramp.length - 1);
  const index = Math.min(ramp.length - 2, Math.floor(position));
  const fraction = position - index;
  return ramp[index].map((v, channel) => Math.round(v + (ramp[index + 1][channel] - v) * fraction));
}

/** Zero impact is transparent; increase color intensity and opacity in proportion to the fixed adverse-impact score. */
export function heatmapPixels(values: Uint16Array, kind: CityMapHeatmapActiveKind): Uint8ClampedArray {
  const palette = Array.from({ length: 256 }, (_, i) => [...rgbAt(kind, i / 255), Math.round(i * MAX_OVERLAY_ALPHA)]);
  const pixels = new Uint8ClampedArray(values.length * 4);
  for (let i = 0; i < values.length; i++) pixels.set(palette[Math.round(values[i] / 257)], i * 4);
  return pixels;
}

export function getHeatmapRampStops(kind: CityMapHeatmapActiveKind, mode: CityMapHeatmapMode = "overlay"): string[] {
  return HEATMAP_RAMPS[kind].map(([r, g, b], i, ramp) =>
    `rgba(${r}, ${g}, ${b}, ${mode === "sources" ? 1 : i / (ramp.length - 1) * MAX_OVERLAY_ALPHA})`);
}
