import {
  GROUND_COVER_DESERT,
  GROUND_COVER_GRASSLAND,
  GROUND_COVER_GRID,
  GROUND_COVER_LUSH,
  GROUND_COVER_SEMI_ARID,
  GROUND_COVER_SNOW,
} from "../../constants";
import { landElevationRange, normalizeGroundCover, type Rgb } from "./terrainData";
import { ELEVATION_RAMP, buildTerrainHillshade, terrainReliefColor } from "./terrainRelief";

export type TerrainThumbnailInput = {
  elev: Float32Array;
  rows: number;
  cols: number;
  mapWidth: number;
  mapHeight: number;
  spacing: number;
  /** 地表種別(GROUND_COVER_*)。未指定時は既定の草原を使う。 */
  groundCover?: number;
};

/**
 * 地表種別ごとのサムネイル標高ランプ(低地→高地)。
 * 暗いタイル背景の上で海岸線が浮かび上がり、ソフトライト陰影で凹凸が読める明度帯にそろえる。
 * 乾燥地(地中海性・半乾燥)は共通の標高ランプ(ベージュ低地→オリーブ高地)をそのまま使う。
 */
const THUMBNAIL_GROUND_COVER_RAMPS: Record<number, readonly Rgb[]> = {
  [GROUND_COVER_GRASSLAND]: [[198, 216, 156], [168, 196, 118], [130, 164, 88], [90, 122, 64]],
  [GROUND_COVER_DESERT]: [[232, 208, 164], [214, 178, 122], [186, 142, 90], [146, 102, 62]],
  [GROUND_COVER_LUSH]: [[146, 192, 124], [106, 162, 88], [70, 128, 62], [42, 90, 44]],
  [GROUND_COVER_SNOW]: [[232, 238, 245], [214, 224, 236], [186, 200, 216], [148, 164, 184]],
  [GROUND_COVER_SEMI_ARID]: ELEVATION_RAMP,
  [GROUND_COVER_GRID]: [[218, 222, 227], [198, 203, 210], [176, 182, 190], [148, 154, 164]],
};

export function thumbnailElevationRamp(groundCover?: number): readonly Rgb[] {
  return THUMBNAIL_GROUND_COVER_RAMPS[normalizeGroundCover(groundCover)];
}

const THUMBNAIL_LONG_EDGE = 512;
const SAMPLES_PER_AXIS = 2;

/**
 * Plan view of an elevation-based Scenario image, registered immediately after creation by
 * "Import terrain" in Scenario management. Independent of DOM/WebGL.
 * Grid row 0 is the south edge, so reverse image rows for north up and east right.
 * Shares original-grid-spacing shading with the city map while coloring land by ground cover,
 * preserving relief after downscaling. Samples coastal pixels at four points and uses land
 * coverage as alpha to prevent sea colors bleeding into edges.
 */
export function renderTerrainThumbnail(input: TerrainThumbnailInput) {
  const { elev, rows, cols, mapWidth, mapHeight, spacing, groundCover } = input;
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
  const ramp = thumbnailElevationRamp(groundCover);

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
          const rgb = terrainReliefColor(e, minLand, maxLand, shade, ramp);
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
