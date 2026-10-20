import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";
import ts from "typescript";

const source = readFileSync(new URL("../src/components/map/roadSurface.ts", import.meta.url), "utf8");
const { outputText } = ts.transpileModule(source, {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
});
const exports = {};
vm.runInNewContext(outputText, { exports });
const { RoadCenterline, roadSurface } = exports;
const near = (a, b) => assert.ok(Math.abs(a - b) < 1e-7, `${a} != ${b}`);
const widthAt = (polygon, z) => {
  const xs = polygon.filter((p) => Math.abs(p[1] - z) < 1e-7).map((p) => p[0]);
  return Math.max(...xs) - Math.min(...xs);
};

// 終端に半円も延長もなく、幅24mの道路がノード上で4mの道へつながる。
const surface = roadSurface([[0, 0], [0, 150]], 24, 4);
near(Math.min(...surface.map((p) => p[1])), 0);
near(Math.max(...surface.map((p) => p[1])), 150);
near(widthAt(surface, 0), 4);
near(widthAt(surface, 36), 24);
near(widthAt(surface, 150), 24);
const left = surface.slice(0, surface.length / 2);
for (let i = 1; i < left.length; i++) assert.ok(Math.abs(left[i][0]) >= Math.abs(left[i - 1][0]));

// 両端の幅補正が重なる短区間でも負幅・過大な膨らみを作らない。
const short = roadSurface([[0, 0], [0, 3]], 24, 4, 8);
near(widthAt(short, 0), 4); near(widthAt(short, 3), 8);
assert.ok(short.every(([x, z]) => Number.isFinite(x) && Number.isFinite(z) && Math.abs(x) <= 12));
assert.equal(roadSurface([[0, 0], [0, 0]], 24).length, 0);

// 保存された曲線が直線へ潰れず、左右の端点が共有法線上で一致する。
const line = new RoadCenterline();
line.moveTo(0, 0); line.bezierCurveTo(60, 0, 60, 100, 0, 100);
assert.ok(line.points.length > 10);
assert.ok(Math.max(...line.points.map((p) => p[0])) > 40);
near(line.points.at(-1)[0], 0); near(line.points.at(-1)[1], 100);
const n = [Math.SQRT1_2, Math.SQRT1_2];
const a = roadSurface([[-100, 0], [0, 0]], 24, 24, 4, undefined, n);
const b = roadSurface([[0, 0], [0, 100]], 4, 4, 4, n);
near(a[a.length / 2 - 1][0], b[0][0]);
near(a[a.length / 2 - 1][1], b[0][1]);
console.log("PASS: flat ends, monotonic taper, short segments, curved centerlines, shared joint edges");
