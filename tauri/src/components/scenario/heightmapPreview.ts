/**
 * Generate elevation import preview pixels (north up, east right), independent of DOM/WebGL.
 *
 * Color selected terrain by elevation using terrainData's ramp, shared with the editor and thumbnails; draw
 * background terrain beneath in a pale solid color.
 * Both make elevations <= 0m (sea) transparent, allowing background land beneath the selected terrain's sea to
 * show through.
 * Align both centers to the city origin, as the core does for the background grid.
 */
import { computePixelRGB, landElevationRange, type Rgb } from "../terrain/terrainData";
import { importedBackgroundExtent, importedMapExtent, type HeightmapPreviewGrid } from "./heightmapImport";

/** Elevation layer centered on the origin */
export type PreviewLayer = {
  elev: Float32Array;
  rows: number;
  cols: number;
  /** Extent covered by the elevation grid in meters. Endpoint pixels correspond to extent boundaries. */
  dataWidth: number;
  dataHeight: number;
  /** Rendered extent in meters. Extend edge elevations beyond the data extent, matching import's kilometer rounding and edge padding. */
  fillWidth: number;
  fillHeight: number;
};

/** Background terrain color: a pale solid color distinct from lowland green and highland brown. */
export const BACKGROUND_RGB: Rgb = [176, 188, 166];

const SAMPLES_PER_AXIS = 2;

/** Selected terrain layer. Round map extent up to kilometers, as during import, and center the source extent. */
export function detailPreviewLayer(grid: HeightmapPreviewGrid, spacing: number): PreviewLayer {
  return {
    elev: grid.elev, rows: grid.rows, cols: grid.cols,
    dataWidth: (grid.sourceCols - 1) * spacing, dataHeight: (grid.sourceRows - 1) * spacing,
    fillWidth: importedMapExtent(grid.sourceCols, spacing), fillHeight: importedMapExtent(grid.sourceRows, spacing),
  };
}

/** Background terrain layer. Round extent to pixel dimensions on the 50m grid and draw nothing outside it. */
export function backgroundPreviewLayer(grid: HeightmapPreviewGrid, spacing: number): PreviewLayer {
  const width = importedBackgroundExtent(grid.sourceCols, spacing);
  const height = importedBackgroundExtent(grid.sourceRows, spacing);
  return { elev: grid.elev, rows: grid.rows, cols: grid.cols, dataWidth: width, dataHeight: height, fillWidth: width, fillHeight: height };
}

/** Elevation at an offset from center (x east, z north, in meters). NaN outside the rendered extent. */
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
 * Generate a preview of overlaid layers, scaled to fit the entire extent with longEdge pixels on the longer
 * side.
 * Sample four points per coastal pixel and use land coverage as alpha, keeping sea colors out of edges.
 * Return null if there are no layers to draw.
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
      // Overlay detailed terrain above the background using normal alpha compositing.
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
