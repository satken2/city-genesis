import { landElevationRange } from "./terrainData";
import { buildTerrainHillshade, terrainReliefColor } from "./terrainRelief";

export type TerrainThumbnailInput = {
  elev: Float32Array;
  rows: number;
  cols: number;
  mapWidth: number;
  mapHeight: number;
  spacing: number;
};

const THUMBNAIL_LONG_EDGE = 512;
const SAMPLES_PER_AXIS = 2;

/**
 * Plan view of an elevation-based Scenario image, registered immediately after creation by
 * "Import terrain" in Scenario management. Independent of DOM/WebGL.
 * Grid row 0 is the south edge, so reverse image rows for north up and east right.
 * Shares original-grid-spacing shading and elevation colors with the city map, preserving
 * relief after downscaling. Samples coastal pixels at four points and uses land coverage
 * as alpha to prevent sea colors bleeding into edges.
 */
export function renderTerrainThumbnail(input: TerrainThumbnailInput) {
  const { elev, rows, cols, mapWidth, mapHeight, spacing } = input;
  if (!Number.isInteger(rows) || !Number.isInteger(cols) || rows < 2 || cols < 2 || elev.length !== rows * cols
    || ![mapWidth, mapHeight, spacing].every((v) => Number.isFinite(v) && v > 0)) {
    throw new Error("Invalid terrain thumbnail grid");
  }
  const scale = THUMBNAIL_LONG_EDGE / Math.max(mapWidth, mapHeight);
  const width = Math.max(1, Math.round(mapWidth * scale));
  const height = Math.max(1, Math.round(mapHeight * scale));
  const pixels = new Uint8ClampedArray(width * height * 4);
  const { minLand, maxLand } = landElevationRange(elev);
  const hillshade = buildTerrainHillshade(elev, rows, cols, spacing);

  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      let red = 0, green = 0, blue = 0, landSamples = 0;
      for (let sy = 0; sy < SAMPLES_PER_AXIS; sy++) {
        const gy = Math.min(rows - 1, (1 - (y + (sy + 0.5) / SAMPLES_PER_AXIS) / height) * mapHeight / spacing);
        const r = Math.min(rows - 2, Math.floor(gy));
        const fy = gy - r;
        for (let sx = 0; sx < SAMPLES_PER_AXIS; sx++) {
          const gx = Math.min(cols - 1, (x + (sx + 0.5) / SAMPLES_PER_AXIS) / width * mapWidth / spacing);
          const c = Math.min(cols - 2, Math.floor(gx));
          const fx = gx - c;
          const i = r * cols + c;
          const south = elev[i] * (1 - fx) + elev[i + 1] * fx;
          const north = elev[i + cols] * (1 - fx) + elev[i + cols + 1] * fx;
          const e = south * (1 - fy) + north * fy;
          if (!(e > 0) || !Number.isFinite(e)) continue;
          const southShade = hillshade[i] * (1 - fx) + hillshade[i + 1] * fx;
          const northShade = hillshade[i + cols] * (1 - fx) + hillshade[i + cols + 1] * fx;
          const shade = southShade * (1 - fy) + northShade * fy;
          const rgb = terrainReliefColor(e, minLand, maxLand, shade);
          red += rgb[0]; green += rgb[1]; blue += rgb[2];
          landSamples++;
        }
      }
      if (landSamples === 0) continue;
      const p = (y * width + x) * 4;
      pixels[p] = Math.round(red / landSamples);
      pixels[p + 1] = Math.round(green / landSamples);
      pixels[p + 2] = Math.round(blue / landSamples);
      pixels[p + 3] = Math.round(255 * landSamples / (SAMPLES_PER_AXIS ** 2));
    }
  }
  return { width, height, pixels };
}
