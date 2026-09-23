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
const { facilitySymbol, drawMapLabels } = load("../src/components/map/mapLabels.ts");
for (const [variant, symbol] of [[100, "cityHall"], [1051, "hospital"], [1052, "fire"], [1053, "police"],
  [1054, "power"], [1057, "power"], [1062, "substation"], [1063, "water"], [1048, "intake"], [1058, "sewage"], [1059, "waste"]]) {
  assert.equal(facilitySymbol(variant, 10, false), symbol);
}
assert.equal(facilitySymbol(200, 2, true), "school");
assert.equal(facilitySymbol(201, 2, false), "school");
assert.equal(facilitySymbol(9999, 10, true), "generic");
assert.equal(facilitySymbol(9999, 10, false), null);
const drawn = [], circles = [];
const ctx = new Proxy({
  measureText: (name) => ({ width: name.length * 8 }),
  fillText: (name, x, y) => drawn.push({ name, x, y }),
  arc: (x, y) => circles.push({ x, y }),
}, { get: (target, key) => target[key] ?? (() => {}) });
drawMapLabels(ctx, { offsetX: -100, offsetY: -100, zoom: 2 }, 800, 600,
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
drawMapLabels(ctx, { offsetX: -100, offsetY: -100, zoom: 2 }, 800, 600, [],
  [{ id: 1, name: "Facility", x: 50, z: 0, symbol: "generic" },
    { id: 9, name: "IC 9", x: 50, z: 0, symbol: "interchange", interchange: true },
    { id: 2, name: "IC 2", x: 150, z: 0, symbol: "interchange", interchange: true },
    { id: 30, name: "Station", x: 150, z: 0, symbol: "station", station: true }]);
assert.deepEqual(circles, [{ x: 500, y: 200 }, { x: 300, y: 200 }]);
assert.deepEqual(drawn.map((label) => label.name).sort(), ["IC 9", "Station"]);
console.log("PASS: facility classification, international school symbol, district coordinates, label collisions, viewport culling, station > interchange > facility priority");
