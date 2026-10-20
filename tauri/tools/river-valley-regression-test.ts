/** 山地の接岸、水位、地形による川幅、経路補正と河口接続を実データ形式で検証する。 */
import assert from "node:assert/strict";
import { buildRiverFromControlPoints, carveRivers, buildRiverPolygon, fitRiverRouteToValley,
  type GeneratedRiver, type RiverControlPoint, type RiverGenParams } from "../src/components/terrain/terrainRiver";
import { RiverProfileSampler, polygonRings, polygonArea, buildRiverSurfaceMesh } from "../src/components/terrain/riverGeometry";

const rows = 121, cols = 121, spacing = 10, size = 1200;
const params: RiverGenParams = { rainfallMmPerYear: 1000, minDischargeM3s: 1, meanderStrength: 0,
  meanderIterations: 0, widthCoef: 5, depthCoef: 1, enableBraiding: true };
function terrain(height: (x: number, z: number) => number) {
  return Float32Array.from({ length: rows * cols }, (_, i) => height((i % cols) * spacing - size / 2, Math.floor(i / cols) * spacing - size / 2));
}
function sample(elev: Float32Array, x: number, z: number) {
  const gx = Math.max(0, Math.min(cols - 1.00001, (x + size / 2) / spacing));
  const gz = Math.max(0, Math.min(rows - 1.00001, (z + size / 2) / spacing));
  const c = Math.floor(gx), r = Math.floor(gz), fx = gx - c, fz = gz - r;
  return elev[r * cols + c] * (1 - fx) * (1 - fz) + elev[r * cols + c + 1] * fx * (1 - fz)
    + elev[(r + 1) * cols + c] * (1 - fx) * fz + elev[(r + 1) * cols + c + 1] * fx * fz;
}
const cps: RiverControlPoint[] = [{ x: -450, z: 0, q: 36 }, { x: 450, z: 0, q: 36 }];
function rebuild(elev: Float32Array, controls = cps, base: GeneratedRiver = { id: "test", mouth: "edge", points: [] }, others: GeneratedRiver[] = []) {
  return buildRiverFromControlPoints(base, controls, elev, rows, cols, spacing, size, size, params, others, [], true).river;
}
for (const [name, height] of [
  ["平地", (_x: number, _z: number) => 100],
  ["横断斜面", (x: number, z: number) => 200 - x * 0.04 + z * 0.6],
  ["途中の尾根", (x: number, z: number) => 200 - x * 0.03 + 50 * Math.exp(-(((x - 100) / 60) ** 2)) + z * 0.15],
  ["谷幅の変化", (x: number, z: number) => 200 - x * 0.03 + z * z * (x < 0 ? 0.02 : 0.0005)],
  ["山腹の曲線", (x: number, z: number) => 400 - x * 0.05 + z * 0.35],
] as const) {
  const elev = terrain(height), original = elev.slice();
  const controls = name === "山腹の曲線" ? [
    { x: -450, z: -120, q: 36 }, { x: 0, z: 80, q: 36 }, { x: 450, z: -100, q: 36 },
  ] : cps;
  const river = rebuild(elev, controls);
  for (let i = 0; i < river.points.length; i++) {
    const p = river.points[i];
    assert(p.waterY < sample(original, p.x, p.z) - 0.5, `${name}: 水面は元の地形より低い`);
    if (i) assert(p.waterY <= river.points[i - 1].waterY, `${name}: 水面の逆勾配`);
  }
  carveRivers(elev, rows, cols, spacing, size, size, [river]);
  assert(elev.every((h, i) => h <= original[i]), "盛土しない");
  const polygon = buildRiverPolygon(elev, rows, cols, spacing, size, size, river, { kind: "spline" }, new Uint8Array(elev.length), []);
  assert(polygon && polygonArea(polygon) > 0, `${name}: 水面が存在する`);
  const sampler = new RiverProfileSampler(river.points.map(p => [p.x, p.waterY, p.z]));
  let maxGap = -Infinity;
  for (const ring of polygonRings(polygon)) for (let i = 1; i < ring.length; i++) {
    const a = ring[i - 1], b = ring[i];
    const pieces = Math.max(1, Math.ceil(Math.hypot(b[0] - a[0], b[1] - a[1]) / 2));
    for (let k = 0; k <= pieces; k++) {
      const x = a[0] + (b[0] - a[0]) * k / pieces, z = a[1] + (b[1] - a[1]) * k / pieces;
      if (Math.abs(x) > 400) continue;
      maxGap = Math.max(maxGap, sampler.heightAt(x, z) - sample(elev, x, z));
    }
  }
  assert(Number.isFinite(maxGap), "岸の検査点が存在する");
  assert(maxGap < spacing * 0.05, `${name}: 岸の浮き ${maxGap.toFixed(2)}m`);
  for (const step of [4, spacing / 2]) {
    const mesh = buildRiverSurfaceMesh(polygon, sampler, step, 0.05)!;
    const edges = new Map<string, { a: number; b: number; count: number }>();
    for (let i = 0; i < mesh.indices.length; i += 3) for (let j = 0; j < 3; j++) {
      const a = mesh.indices[i + j], b = mesh.indices[i + (j + 1) % 3];
      const key = `${Math.min(a, b)}:${Math.max(a, b)}`;
      const edge = edges.get(key);
      if (edge) edge.count++;
      else edges.set(key, { a, b, count: 1 });
    }
    let meshGap = -Infinity;
    for (const { a, b, count } of edges.values()) {
      if (count !== 1) continue;
      for (let k = 0; k <= 4; k++) {
        const x = mesh.positions[a * 3] + (mesh.positions[b * 3] - mesh.positions[a * 3]) * k / 4;
        const z = mesh.positions[a * 3 + 2] + (mesh.positions[b * 3 + 2] - mesh.positions[a * 3 + 2]) * k / 4;
        const y = mesh.positions[a * 3 + 1] + (mesh.positions[b * 3 + 1] - mesh.positions[a * 3 + 1]) * k / 4;
        if (Math.abs(x) < 400) meshGap = Math.max(meshGap, y - sample(elev, x, z));
      }
    }
    assert(meshGap < 0.25, `${name}: 描画メッシュの端が浮く ${meshGap.toFixed(2)}m`);
  }
  if (name === "谷幅の変化") {
    const narrow = river.points.filter(p => p.x < -150).map(p => p.width);
    const wide = river.points.filter(p => p.x > 150).map(p => p.width);
    assert(Math.min(...wide) > Math.max(...narrow) * 1.2, "谷が開けると幅が広がる");
  }
  console.log(`${name}: 接岸・水位・掘り込み OK (岸の最大隙間 ${maxGap.toFixed(3)}m)`);
}

const valley = terrain((x, z) => 200 - x * 0.02 + (z - 60) ** 2 * 0.01);
const initial = rebuild(valley), initialCopy = JSON.stringify(initial), terrainCopy = valley.slice();
const fitted = fitRiverRouteToValley(initial, valley, rows, cols, spacing, size, size, 100);
assert.deepEqual(fitted[0], cps[0]);
assert.deepEqual(fitted.at(-1), cps.at(-1));
assert.equal(JSON.stringify(initial), initialCopy, "補正は入力の川を変更しない");
assert.deepEqual(valley, terrainCopy, "補正は地形を変更しない");
assert(fitted.every(p => Math.abs(p.z) <= 100), "探索距離を守る");
assert(fitted.filter(p => Math.abs(p.x) < 250).every(p => Math.abs(p.z - 60) <= 15), "谷底へ移動する");
const fittedRiver = rebuild(valley, fitted);
assert(fittedRiver.points.filter(p => Math.abs(p.x) < 250).every(p => Math.abs(p.z - 60) <= 20), "再構築後も谷に沿う");
console.log("経路補正: 谷への移動・両端固定・探索距離・非破壊 OK");

for (const mouth of ["sea", "lake", "confluence"] as const) {
  const level = mouth === "sea" ? 0 : 100;
  const base: GeneratedRiver = { id: "outlet", points: [], mouth, mouthWaterLevel: level,
    confluence: mouth === "confluence" ? { riverId: "main", pointIndex: 0 } : undefined };
  const main: GeneratedRiver = { id: "main", mouth: "edge", points: [{ ...initial.points[0], x: 450, z: 0, waterY: level }] };
  const river = rebuild(terrain((x) => level + (450 - x) * 0.1), cps, base, [main]);
  assert.equal(river.points.at(-1)!.waterY, level, `${mouth}: 接続水位`);
  assert(river.points.every((p, i) => i === 0 || p.waterY <= river.points[i - 1].waterY), `${mouth}: 単調性`);
}
console.log("海・湖・本流への水位接続 OK");
