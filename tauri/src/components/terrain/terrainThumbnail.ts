import { computePixelRGB, landElevationRange } from "./terrainData";

export type TerrainThumbnailInput = {
  elev: Float32Array;
  rows: number;
  cols: number;
  mapWidth: number;
  mapHeight: number;
  spacing: number;
  groundCover?: number;
};

const THUMBNAIL_LONG_EDGE = 512;
const SAMPLES_PER_AXIS = 2;

/**
 * 標高から作るシナリオ画像(シナリオ管理の「地形を取り込む」が作成直後に登録する)の平面図。DOM・WebGLには依存しない。
 * 格子の行0は南端なので画像の行順を反転し、北を上・東を右にする。
 * 海岸の各画素を4点で評価し、陸地の被覆率をアルファにすることで海の色が縁に混ざるのを防ぐ。
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
          const rgb = computePixelRGB(e, minLand, maxLand, groundCover);
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
