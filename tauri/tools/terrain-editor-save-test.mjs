/** 地形エディタの「シナリオに保存」の引数とロック情報の正規化を検証する。 */
import assert from "node:assert/strict";
import { build } from "esbuild";
import { fileURLToPath } from "node:url";

const bundled = await build({
  entryPoints: [fileURLToPath(new URL("../src/components/terrain/terrainData.ts", import.meta.url))],
  bundle: true,
  platform: "node",
  format: "esm",
  write: false,
});
const { normalizeTerrainEditorLocks, scenarioTerrainSaveArgs, base64ToUint8, decodeFloat16Array } = await import(
  `data:text/javascript;base64,${Buffer.from(bundled.outputFiles[0].text).toString("base64")}`
);

const content = {
  elev: new Float32Array([-10, 0, 12.5, 300]),
  waterSurfaces: [{ id: "lake", water_level: 5, polygon: { type: "Polygon", coordinates: [[[0, 0], [1, 0], [1, 1]]] } }],
  rivers: [{ id: "river", kind: "river", polygon: { type: "Polygon", coordinates: [] }, profile: [] }],
  resources: [{ id: "12", resource_type: 1, max_amount: 100, recovery_rate: 0, polygon: { type: "Polygon", coordinates: [] } }],
  props: { instances: [], scatters: [] },
  camera: null,
  groundCover: 2,
  climate: 6,
};

const unlocked = scenarioTerrainSaveArgs("editor", false, content);
assert.equal(unlocked.editorId, "editor");
assert.deepEqual(Array.from(decodeFloat16Array(base64ToUint8(unlocked.nearBase64), 4)), [-10, 0, 12.5, 300]);
assert.equal(unlocked.waterSurfaces, content.waterSurfaces);
assert.equal(unlocked.rivers, content.rivers);
assert.deepEqual([unlocked.groundCover, unlocked.climate, unlocked.camera], [2, 6, null]);

const locked = scenarioTerrainSaveArgs("editor", true, content);
assert.deepEqual([locked.nearBase64, locked.waterSurfaces, locked.rivers], [null, null, null], "ロック中は標高・水面・川を送らない");
assert.equal(locked.resources, content.resources, "資源・プロップ・カメラ・地表・気候はロック中も送る");
assert.equal(locked.props, content.props);

assert.deepEqual(
  normalizeTerrainEditorLocks({ heights_locked: true, reasons: ["PATHS_EXIST", { code: "RECLAIMED_LAND" }, 3, ""] }),
  { heights_locked: true, reasons: ["PATHS_EXIST", "RECLAIMED_LAND"] },
);
assert.deepEqual(normalizeTerrainEditorLocks({ heights_locked: false }), { heights_locked: false, reasons: [] });
for (const broken of [null, undefined, {}, { heights_locked: "no" }, "locked"]) {
  assert.equal(normalizeTerrainEditorLocks(broken).heights_locked, true, "形の崩れたロック情報はロック扱い");
}

console.log("PASS: terrain editor save arguments and lock normalization");
