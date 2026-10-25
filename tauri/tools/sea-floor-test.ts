/** 地形エディタの海底生成。「陸の勾配」はコアの取り込み(carve_sea_floor)と同じ海底になり、「一定の勾配」は従来の式のまま。 */
import assert from "node:assert/strict";
import { applySeaFloor, type SeaFloorParams } from "../src/components/terrain/terrainSeaFloor";

const rows = 121, cols = 121, spacing = 10, coastCol = 60;
/** 列60より西が陸。北半分(行60以上)は勾配30%、南半分は勾配1%の斜面で、海は -1m。 */
function terrain(): Float32Array {
  return Float32Array.from({ length: rows * cols }, (_, i) => {
    const r = Math.floor(i / cols), c = i % cols;
    const inland = (coastCol - c) * spacing;
    return inland > 0 ? (r >= 60 ? 0.3 : 0.01) * inland : -1;
  });
}
const land: SeaFloorParams = { seaLevelThreshold: 0, slopeMode: "land", shelfSlopePercent: 2, maxDepth: 50 };

// 同じ地形をコアの carve_sea_floor で彫った値 [行, 列, 標高]。海岸線に直交する向きなので距離変換の違いも出ない。
const coreProbes: [number, number, number][] = [
  [100, 60, -2.9964], [100, 61, -5.9714], [100, 62, -8.904], [100, 65, -17.2817], [100, 70, -29.016], [100, 90, -46.3272], [100, 120, -49.3803],
  [20, 60, -0.1], [20, 61, -0.2], [20, 62, -0.3], [20, 65, -0.6551], [20, 70, -1.9624], [20, 90, -28.0158], [20, 120, -48.2105],
  [55, 60, -1.9779], [55, 61, -3.9496], [55, 62, -5.909], [55, 65, -11.602], [55, 70, -20.0177], [55, 90, -40.5735], [55, 120, -48.8773],
  [65, 60, -2.6834], [65, 61, -5.3513], [65, 62, -7.9888], [65, 65, -15.4575], [65, 70, -25.4998], [65, 90, -43.0316], [65, 120, -49.026],
];

{
  const original = terrain();
  const elev = original.slice();
  const modified = applySeaFloor(elev, rows, cols, spacing, land);
  assert.equal(modified, rows * (cols - coastCol), "海のマスだけを書き換える");
  for (let i = 0; i < elev.length; i++) {
    if (original[i] > 0) assert.equal(elev[i], original[i], "陸は変えない");
  }
  for (const [r, c, expected] of coreProbes) {
    assert(Math.abs(elev[r * cols + c] - expected) < 1e-3, `(${r}, ${c}) ${elev[r * cols + c]} はコアの ${expected} と一致する`);
  }
  assert(-elev[100 * cols + 62] > 10 * -elev[20 * cols + 62], "急な海岸の沖は平らな海岸の沖より深い");
}

{
  const elev = terrain();
  applySeaFloor(elev, rows, cols, spacing, { ...land, slopeMode: "fixed" });
  for (const c of [60, 70, 90]) {
    const d = (c - coastCol + 1) * spacing;
    const expected = -50 * Math.tanh((0.02 * d) / 50);
    assert(Math.abs(elev[20 * cols + c] - expected) < 1e-4, "一定の勾配は場所によらず同じ式");
    assert(Math.abs(elev[100 * cols + c] - expected) < 1e-4, "一定の勾配は場所によらず同じ式");
  }
}

{
  // 範囲選択は書き換える範囲だけを絞り、勾配と距離はマップ全体で測る。
  const whole = terrain();
  applySeaFloor(whole, rows, cols, spacing, land);
  const elev = terrain();
  const mask = new Float32Array(rows * cols);
  for (let r = 90; r <= 110; r++) for (let c = 60; c <= 80; c++) mask[r * cols + c] = 1;
  applySeaFloor(elev, rows, cols, spacing, land, { rMin: 90, rMax: 110, cMin: 60, cMax: 80, mask });
  assert.equal(elev[100 * cols + 70], whole[100 * cols + 70]);
  assert.equal(elev[20 * cols + 70], -1, "範囲外は変えない");
}

console.log("sea floor: ok");
