import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";
import ts from "typescript";

const { outputText } = ts.transpileModule(readFileSync(new URL("../src/components/map/mapHeatmap.ts", import.meta.url), "utf8"), {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
});
const exports = {};
vm.runInNewContext(outputText, { exports });
const { effectiveHeatmapMode, pickHeatmapLod, heatmapPixels, getHeatmapRampStops } = exports;

for (const kind of ["noise", "pollution", "crime", "sunshine"]) {
  const pixels = heatmapPixels(new Uint16Array([0, 16384, 32768, 65535]), kind);
  const alpha = [3, 7, 11, 15].map((i) => pixels[i]);
  assert.equal(alpha[0], 0, "zero load must be transparent");
  assert.ok(alpha[0] < alpha[1] && alpha[1] < alpha[2] && alpha[2] < alpha[3]);
  assert.ok(alpha[3] < 255, "the ground remains visible at maximum load");
  assert.equal(effectiveHeatmapMode(kind, "sources"), ["noise", "pollution"].includes(kind) ? "sources" : "overlay");
  assert.equal(effectiveHeatmapMode(kind, "overlay"), "overlay");
  assert.ok(getHeatmapRampStops(kind)[0].endsWith(", 0)"));
}
const levels = [5, 20, 80].map((spacing) => ({ rows: 10, cols: 10, spacing }));
assert.equal(pickHeatmapLod(levels, 1), 0);
assert.equal(pickHeatmapLod(levels, 0.05), 1);
assert.equal(pickHeatmapLod(levels, 0.01), 2);
console.log("PASS: zero transparency, increasing opacity, zoom LOD, sources only for noise/pollution, legend alpha");
