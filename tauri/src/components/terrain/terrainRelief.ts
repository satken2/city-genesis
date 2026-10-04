import type { Rgb } from "./terrainData";

/** 地表種別によらず標高を読み取れるよう、低地をベージュ、高地を緑で表す。 */
export const ELEVATION_RAMP: Rgb[] = [
  [225, 220, 193], [203, 209, 160], [165, 183, 120], [117, 135, 85],
];
/** 北西(ワールド-X・+Z)から高度50度で照らし、標高を2倍に強調して起伏を見せる。 */
const HILLSHADE_ALTITUDE = 50 * Math.PI / 180;
const HILLSHADE_LIGHT = [
  -Math.cos(HILLSHADE_ALTITUDE) / Math.SQRT2,
  Math.sin(HILLSHADE_ALTITUDE),
  Math.cos(HILLSHADE_ALTITUDE) / Math.SQRT2,
] as const;
const HILLSHADE_VERTICAL_EXAGGERATION = 2;

/** 陸地の明暗を0〜1へ伸ばす。平坦な地形は中立の0.5として標高色をそのまま残す。 */
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
      // 配列の行は南→北なので、画面のY軸ではなくワールド+Zの勾配を法線に使う。
      const intensity = Math.max(0, (nx * lx + ly + nz * lz) / Math.hypot(nx, 1, nz));
      shade[i] = intensity;
      min = Math.min(min, intensity);
      max = Math.max(max, intensity);
    }
  }
  const range = max - min;
  if (range > 1e-6) {
    // サムネイルは格子間を補間するため、海側の値も中立にして岸へ暗い縁を作らない。
    for (let i = 0; i < shade.length; i++) {
      shade[i] = elev[i] > 0 ? Math.max(0, Math.min(1, (shade[i] - min) / range)) : 0.5;
    }
  } else {
    shade.fill(0.5);
  }
  return shade;
}

/** 都市マップとシナリオ画像で共通の標高色へ、ソフトライトで陰影を重ねる。 */
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
