/** 格子への位置ずれによる川底の脈動と、斜面への水面の漏れを検証する。 */
import { carveRivers, buildRiverPolygon, type GeneratedRiver } from "../src/components/terrain/terrainRiver";
import { RiverProfileSampler, polygonRings, polygonArea } from "../src/components/terrain/riverGeometry";

const rows = 101, cols = 101, spacing = 10, size = 1000;
function river(width: number, slope = 0): GeneratedRiver {
  return { id: "regression", mouth: "edge", points: [
    { x: -380, z: -327, q: 1, width, waterY: 100, bedY: 90, curvature: 0, braided: false },
    { x: 370, z: 313, q: 1, width, waterY: 100 - slope, bedY: 90 - slope, curvature: 0, braided: false },
  ] };
}
for (const width of [0, 3, 15, 16, 30, 60]) {
  const rv = river(width);
  const elev = new Float32Array(rows * cols).fill(100);
  carveRivers(elev, rows, cols, spacing, size, size, [rv]);
  const sampler = new RiverProfileSampler(rv.points.map(p => [p.x, p.waterY, p.z]));
  let checked = 0;
  for (let r = 0; r < rows; r++) for (let c = 0; c < cols; c++) {
    const near = sampler.nearest(c * spacing - size / 2, r * spacing - size / 2);
    if (near.t <= 0 || near.t >= 1 || near.dist > spacing / Math.SQRT2) continue;
    if (Math.abs(elev[r * cols + c] - 90) > 1e-4) throw new Error(`川底が不均一: 幅${width}m、標高${elev[r * cols + c]}`);
    checked++;
  }
  if (checked < 100) throw new Error("検査点が不足");
  console.log(`幅${width}m: 川底${checked}点の深さが一定`);
}

// 川に沿って下り、横断方向にも下る斜面。低い岸より下へ水位を置いてから彫る。
const rv = river(3, 20);
const sampler = new RiverProfileSampler(rv.points.map(p => [p.x, p.waterY, p.z]));
const elev = new Float32Array(rows * cols);
for (let r = 0; r < rows; r++) for (let c = 0; c < cols; c++) {
  const x = c * spacing - size / 2, z = r * spacing - size / 2;
  const across = ((x + 380) * -640 + (z + 327) * 750) / Math.hypot(750, 640);
  elev[r * cols + c] = sampler.heightAt(x, z) - across * 0.2;
}
for (const p of rv.points) { p.waterY -= 15; p.bedY -= 15; }
carveRivers(elev, rows, cols, spacing, size, size, [rv]);
const poly = buildRiverPolygon(elev, rows, cols, spacing, size, size, rv, { kind: "spline" }, new Uint8Array(rows * cols), []);
if (!poly) throw new Error("水面がない");
for (const ring of polygonRings(poly)) for (const [x, z] of ring) {
  if (sampler.nearest(x, z).dist > 3 / 2 + spacing / Math.SQRT2 + spacing * 1.5) throw new Error("水面が河道帯から漏れた");
}
const again = buildRiverPolygon(elev, rows, cols, spacing, size, size, rv, { kind: "polygon", polygon: poly }, new Uint8Array(rows * cols), []);
if (!again || polygonArea(again) > polygonArea(poly) * 1.05) throw new Error("再計算で水面が拡大した");
console.log("斜面の水面範囲と再計算: OK");
