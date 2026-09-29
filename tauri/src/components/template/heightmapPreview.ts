/**
 * 標高ファイルの取り込みプレビュー(北が上・東が右)の画素を作る。DOM・WebGLには依存しない。
 *
 * 選んだ地形は標高で色分けし(色は地形エディタ・サムネイルと同じ terrainData のランプ)、背景地形はその下に
 * 淡い単色で敷く。どちらも標高0m以下(海)は透明にするので、地形の海の下に背景の陸地が残っていればそこから見える。
 * 2つの中心は都市の原点で揃える(コアも背景の格子の中心を都市の原点に置く)。
 */
import { computePixelRGB, landElevationRange, type Rgb } from "../terrain/terrainData";
import { importedBackgroundExtent, importedMapExtent, type HeightmapPreviewGrid } from "./heightmapImport";

/** 中心を原点に置く標高の層 */
export type PreviewLayer = {
  elev: Float32Array;
  rows: number;
  cols: number;
  /** 標高の格子が覆う範囲(m)。格子の両端の画素がこの範囲の両端に当たる */
  dataWidth: number;
  dataHeight: number;
  /** 描く範囲(m)。データの範囲より広い分は縁の標高を延ばす(取り込みが1km単位へ切り上げて縁の標高で埋めるのと同じ) */
  fillWidth: number;
  fillHeight: number;
};

/** 背景地形の色。地形の低地の緑・高地の茶とは別の、淡い単色にする */
export const BACKGROUND_RGB: Rgb = [176, 188, 166];

const SAMPLES_PER_AXIS = 2;

/** 選んだ地形の層。マップの範囲は取り込みと同じく1km単位に切り上げ、元の範囲を中央に置く */
export function detailPreviewLayer(grid: HeightmapPreviewGrid, spacing: number): PreviewLayer {
  return {
    elev: grid.elev, rows: grid.rows, cols: grid.cols,
    dataWidth: (grid.sourceCols - 1) * spacing, dataHeight: (grid.sourceRows - 1) * spacing,
    fillWidth: importedMapExtent(grid.sourceCols, spacing), fillHeight: importedMapExtent(grid.sourceRows, spacing),
  };
}

/** 背景地形の層。50m格子の画素数に丸めた範囲で、その外は描かない */
export function backgroundPreviewLayer(grid: HeightmapPreviewGrid, spacing: number): PreviewLayer {
  const width = importedBackgroundExtent(grid.sourceCols, spacing);
  const height = importedBackgroundExtent(grid.sourceRows, spacing);
  return { elev: grid.elev, rows: grid.rows, cols: grid.cols, dataWidth: width, dataHeight: height, fillWidth: width, fillHeight: height };
}

/** 中心からの位置(x: 東, z: 北。m)の標高。描く範囲の外は NaN */
function sampleLayer(layer: PreviewLayer, x: number, z: number): number {
  if (Math.abs(x) > layer.fillWidth / 2 || Math.abs(z) > layer.fillHeight / 2) return NaN;
  const { elev, rows, cols } = layer;
  const gx = Math.min(cols - 1, Math.max(0, (x + layer.dataWidth / 2) / layer.dataWidth * (cols - 1)));
  const gy = Math.min(rows - 1, Math.max(0, (z + layer.dataHeight / 2) / layer.dataHeight * (rows - 1)));
  const c = Math.min(cols - 2, Math.floor(gx));
  const r = Math.min(rows - 2, Math.floor(gy));
  const fx = gx - c;
  const fy = gy - r;
  const i = r * cols + c;
  const south = elev[i] * (1 - fx) + elev[i + 1] * fx;
  const north = elev[i + cols] * (1 - fx) + elev[i + cols + 1] * fx;
  return south * (1 - fy) + north * fy;
}

export type PreviewImage = { width: number; height: number; pixels: Uint8ClampedArray };

/**
 * 層を重ねたプレビュー画像を作る。長辺が longEdge 画素で、描く範囲すべてが収まる縮尺にする。
 * 海岸の各画素を4点で評価し、陸地の被覆率をアルファにする(海の色が縁に混ざらない)。
 * 描く層が無ければ null。
 */
export function renderImportPreview(
  layers: { detail: PreviewLayer | null; background: PreviewLayer | null }, longEdge: number,
): PreviewImage | null {
  const { detail, background } = layers;
  const drawn = [detail, background].filter((layer): layer is PreviewLayer => layer !== null);
  if (drawn.length === 0) return null;
  for (const layer of drawn) {
    if (layer.rows < 2 || layer.cols < 2 || layer.elev.length !== layer.rows * layer.cols
      || ![layer.dataWidth, layer.dataHeight, layer.fillWidth, layer.fillHeight].every(v => Number.isFinite(v) && v > 0)) {
      throw new Error("Invalid terrain preview layer");
    }
  }
  const worldWidth = Math.max(...drawn.map(layer => layer.fillWidth));
  const worldHeight = Math.max(...drawn.map(layer => layer.fillHeight));
  const scale = longEdge / Math.max(worldWidth, worldHeight);
  const width = Math.max(1, Math.round(worldWidth * scale));
  const height = Math.max(1, Math.round(worldHeight * scale));
  const pixels = new Uint8ClampedArray(width * height * 4);
  const range = detail ? landElevationRange(detail.elev) : null;
  const samples = SAMPLES_PER_AXIS ** 2;

  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      let detailRed = 0, detailGreen = 0, detailBlue = 0, detailLand = 0, backgroundLand = 0;
      for (let sy = 0; sy < SAMPLES_PER_AXIS; sy++) {
        const z = (height / 2 - (y + (sy + 0.5) / SAMPLES_PER_AXIS)) / scale;
        for (let sx = 0; sx < SAMPLES_PER_AXIS; sx++) {
          const worldX = ((x + (sx + 0.5) / SAMPLES_PER_AXIS) - width / 2) / scale;
          if (detail && range) {
            const e = sampleLayer(detail, worldX, z);
            if (e > 0) {
              const rgb = computePixelRGB(e, range.minLand, range.maxLand);
              detailRed += rgb[0]; detailGreen += rgb[1]; detailBlue += rgb[2];
              detailLand++;
            }
          }
          if (background && sampleLayer(background, worldX, z) > 0) backgroundLand++;
        }
      }
      if (detailLand === 0 && backgroundLand === 0) continue;
      // 詳細地形を背景の上に、通常のアルファ合成で重ねる
      const detailAlpha = detailLand / samples;
      const backgroundAlpha = backgroundLand / samples * (1 - detailAlpha);
      const alpha = detailAlpha + backgroundAlpha;
      const p = (y * width + x) * 4;
      pixels[p] = Math.round((detailRed / samples + BACKGROUND_RGB[0] * backgroundAlpha) / alpha);
      pixels[p + 1] = Math.round((detailGreen / samples + BACKGROUND_RGB[1] * backgroundAlpha) / alpha);
      pixels[p + 2] = Math.round((detailBlue / samples + BACKGROUND_RGB[2] * backgroundAlpha) / alpha);
      pixels[p + 3] = Math.round(255 * alpha);
    }
  }
  return { width, height, pixels };
}
