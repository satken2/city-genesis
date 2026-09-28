/** 地形エディタの「シナリオに保存」の引数、ロック情報の正規化、保存後の資源IDの置き換えを検証する。 */
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
const { normalizeTerrainEditorLocks, scenarioTerrainSaveArgs, resourceIdReplacer, base64ToUint8, decodeFloat16Array } = await import(
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

const existing = { id: "7", resourceType: 1, points: [] };
const created = { id: "resource_1", resourceType: 2, points: [] };
const addedWhileSaving = { id: "resource_2", resourceType: 3, points: [] };
const replace = resourceIdReplacer({ resource_1: "12", unknown: "13", broken: 5 });
const current = [existing, created, addedWhileSaving];
const replaced = replace(current);
assert.notEqual(replaced, current, "置き換えがあれば新しい配列");
assert.deepEqual(replaced.map((area) => area.id), ["7", "12", "resource_2"], "保存中に足した資源は仮IDのまま");
assert.equal(replaced[0], existing, "置き換えの無い資源は同じオブジェクト");
assert.equal(replaced[2], addedWhileSaving);
assert.equal(created.id, "resource_1", "元のオブジェクトは書き換えない");
assert.equal(replaced[1].resourceType, 2);
const history = [existing, created];
assert.equal(replace(history)[1], replaced[1], "履歴と現在の一覧で差し替え先のオブジェクトを共有する");
const unchanged = [existing, addedWhileSaving];
assert.equal(replace(unchanged), unchanged, "置き換えの無い一覧は同じ配列");
for (const empty of [undefined, null, {}]) {
  assert.equal(resourceIdReplacer(empty)(current), current);
}

console.log("PASS: terrain editor save arguments, lock normalization and resource ID replacement");
