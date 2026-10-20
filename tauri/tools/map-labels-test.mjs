import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";
import ts from "typescript";

const imports = {};
function load(file) {
  const exports = {};
  const { outputText } = ts.transpileModule(readFileSync(new URL(file, import.meta.url), "utf8"), {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
  });
  vm.runInNewContext(outputText, { exports, require: (id) => imports[id], Path2D: class {} });
  return exports;
}
imports["../../constants"] = load("../src/constants.ts");
imports["./mapSymbols"] = load("../src/components/map/mapSymbols.ts");
const { facilitySymbol, drawMapLabels, roadLabelScreenAngle } = load("../src/components/map/mapLabels.ts");
for (const [variant, symbol] of [[100, "cityHall"], [1051, "hospital"], [1052, "fire"], [1053, "police"],
  [1054, "power"], [1057, "power"], [1062, "substation"], [1063, "water"], [1048, "intake"], [1058, "sewage"], [1059, "waste"]]) {
  assert.equal(facilitySymbol(variant, 10, false), symbol);
}
assert.equal(facilitySymbol(200, 2, true), "school");
assert.equal(facilitySymbol(201, 2, false), "school");
assert.equal(facilitySymbol(9999, 10, true), "generic");
assert.equal(facilitySymbol(9999, 10, false), null);
const drawn = [], circles = [], rotations = [], anchors = new Map();
let lastTranslate = null;
const ctx = new Proxy({
  measureText: (name) => ({ width: name.length * 8 }),
  fillText: (name, x, y) => { drawn.push({ name, x, y }); anchors.set(name, lastTranslate); },
  arc: (x, y) => circles.push({ x, y }),
  translate: (x, y) => { lastTranslate = { x, y }; },
  rotate: (angle) => rotations.push(angle),
}, { get: (target, key) => target[key] ?? (() => {}) });
// offsetYは画面上端のworld z。地図は+Z(北)を上に描くので、z=0は上端から(100-0)*2=200pxの位置になる。
drawMapLabels(ctx, { offsetX: -100, offsetY: 100, zoom: 2 }, 800, 600,
  [{ name: "District", center_point: [0, 0] }],
  [{ id: 1, name: "School", x: 50, z: 0, symbol: "school" },
    { id: 2, name: "Overlap", x: 50, z: 0, symbol: "hospital" },
    { id: 3, name: "Offscreen", x: -500, z: 0, symbol: "generic" }]);
assert.deepEqual(drawn[0], { name: "District", x: 200, y: 200 });
assert.equal(circles.length, 1);
assert.deepEqual(circles[0], { x: 300, y: 200 });
assert.ok(drawn.some((label) => label.name === "School"));
assert.ok(!drawn.some((label) => label.name === "Overlap" || label.name === "Offscreen"));
drawn.length = 0;
circles.length = 0;
drawMapLabels(ctx, { offsetX: -100, offsetY: 100, zoom: 2 }, 800, 600, [],
  [{ id: 1, name: "Facility", x: 50, z: 0, symbol: "generic" },
    { id: 9, name: "IC 9", x: 50, z: 0, symbol: "interchange", interchange: true },
    { id: 2, name: "IC 2", x: 150, z: 0, symbol: "interchange", interchange: true },
    { id: 30, name: "Station", x: 150, z: 0, symbol: "station", station: true }]);
assert.deepEqual(circles, [{ x: 500, y: 200 }, { x: 300, y: 200 }]);
assert.deepEqual(drawn.map((label) => label.name).sort(), ["IC 9", "Station"]);
// 道路名の向き: ワールドの角度(atan2(dz, dx))は画面では符号が逆になり、逆さにならないよう±90°へ畳む。
const deg = (rad) => Math.round((rad * 180) / Math.PI);
assert.equal(deg(roadLabelScreenAngle(0)), 0);
assert.equal(deg(roadLabelScreenAngle(180)), 0);
assert.equal(deg(roadLabelScreenAngle(45)), -45);
assert.equal(deg(roadLabelScreenAngle(-135)), -45);
assert.equal(deg(roadLabelScreenAngle(135)), 45);
assert.equal(Math.abs(deg(roadLabelScreenAngle(90))), 90);
assert.equal(deg(roadLabelScreenAngle(90)), deg(roadLabelScreenAngle(-90)));
// 配置の優先度: 駅・インターチェンジの記号 → 駅・インターチェンジの名前 → 道路名 → 施設記号 → 施設名。
drawn.length = 0;
circles.length = 0;
rotations.length = 0;
drawMapLabels(ctx, { offsetX: -100, offsetY: 100, zoom: 2 }, 800, 600, [],
  [{ id: 5, name: "IC 5", x: 50, z: 0, symbol: "interchange", interchange: true },
    { id: 7, name: "Facility", x: 150, z: 0, symbol: "generic" }],
  [{ roadId: 1, name: "Blocked Rd", color: "#7F77DD", x: 50, z: 0, angleDeg: 0 },
    { roadId: 2, name: "Ave", color: "#E4572E", x: 150, z: 0, angleDeg: 45 },
    { roadId: 3, name: "", color: "#1D9E75", x: 100, z: 50, angleDeg: 0 },
    { roadId: 4, name: "Offscreen Rd", color: "#1D9E75", x: -500, z: 0, angleDeg: 0 }]);
assert.deepEqual(circles, [{ x: 300, y: 200 }], "the facility symbol under the road name must be dropped");
const ave = drawn.find((label) => label.name === "Ave");
assert.ok(ave, "road name must be drawn");
assert.deepEqual(anchors.get("Ave"), { x: 500, y: 200 });
assert.deepEqual(rotations.map(deg), [-45]);
assert.ok(!drawn.some((label) => label.name === "Blocked Rd" || label.name === "Offscreen Rd"));
assert.ok(drawn.some((label) => label.name === "IC 5"));
// インターチェンジの名前は道路名より先に場所を取る。左端のICは名前を右にしか置けず、そこへ重なる道路名が省かれる。
drawn.length = 0;
drawMapLabels(ctx, { offsetX: -100, offsetY: 100, zoom: 2 }, 800, 600, [],
  [{ id: 6, name: "Interchange 6", x: -80, z: 0, symbol: "interchange", interchange: true }],
  [{ roadId: 5, name: "Ring", color: "#7F77DD", x: -45, z: 0, angleDeg: 0 }]);
assert.ok(drawn.some((label) => label.name === "Interchange 6"), "the interchange name must win over the road name");
assert.ok(!drawn.some((label) => label.name === "Ring"), "the road name over the interchange name must be dropped");
console.log("PASS: facility classification, international school symbol, district coordinates, label collisions, viewport culling, station/interchange symbols and names > road name > facility priority, unnamed roads skipped, upright road name rotation");
