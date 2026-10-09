import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";
import ts from "typescript";

// 効果範囲の描画データ(塗りの巻き方向・稼働中と稼働していない施設の分け方・建物の集合)を検査する。
// Path2D は呼び出しを部分パスごとの点列として記録する差し替えで、Canvas の非ゼロ規則が
// 外周を和集合にし穴を抜くための向きの関係を確かめる。
class RecordingPath {
  constructor() { this.subpaths = []; }
  moveTo(x, z) { this.subpaths.push([[x, z]]); }
  lineTo(x, z) { this.subpaths.at(-1).push([x, z]); }
  closePath() {}
  arc(cx, cz, r, start, end) {
    // Canvas の既定(anticlockwise=false)と同じく角度の増える向きに点を打つ
    const ring = this.subpaths.at(-1);
    for (let i = 1; i <= 16; i++) {
      const a = start + ((end - start) * i) / 16;
      ring.push([cx + r * Math.cos(a), cz + r * Math.sin(a)]);
    }
  }
}
const signedArea = (ring) => ring.reduce((sum, [x1, z1], i) => {
  const [x2, z2] = ring[(i + 1) % ring.length];
  return sum + x1 * z2 - x2 * z1;
}, 0) / 2;

const { outputText } = ts.transpileModule(readFileSync(new URL("../src/components/map/infraOverlay.ts", import.meta.url), "utf8"), {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
});
const modules = {
  "react": { useEffect() {}, useMemo: (fn) => fn(), useState: (v) => [v, () => {}] },
  "../../utils/api": { buildApiUrl() {}, fetchApi: async () => ({}) },
  "../../constants": { GOODS_INFRASTRUCTURE: 4, STATUS_ACTIVE: 1 },
  "../../contexts/MasterDataContext": { useMasterData: () => null },
};
const exports = {};
vm.runInNewContext(outputText, { exports, Path2D: RecordingPath, require: (name) => {
  assert.ok(name in modules, name);
  return modules[name];
} });
const { buildCoverageRender } = exports;

// 外周が時計回り・穴が反時計回りで届いても、外周は正、穴は負の向きに揃える(円は Canvas の既定で正)。
const square = (x, z, h) => [[x - h, z - h], [x - h, z + h], [x + h, z + h], [x + h, z - h]];
const render = buildCoverageRender({
  product_id: 306,
  suppliers: [
    { agent_id: 1, name: "a", variant: 1058, status: 1, center_point: [0, 0],
      region: { type: "polygon", polygons: [{ exterior: square(0, 0, 100), holes: [[...square(0, 0, 20)].reverse()] }] } },
    { agent_id: 2, name: "b", variant: 1058, status: 1, center_point: [150, 0],
      region: { type: "circle", center: [150, 0], radius: 80 } },
    { agent_id: 3, name: "c", variant: 1058, status: 4, center_point: [-300, 0],
      region: { type: "circle", center: [-300, 0], radius: 80 } },
  ],
  covered_building_ids: [10, 11],
  uncovered_building_ids: [12],
});
assert.equal(render.fill.subpaths.length, 3, "塗りは稼働中の施設だけ(外周・穴・円)");
const [exterior, hole, circle] = render.fill.subpaths.map(signedArea);
assert.ok(exterior > 0 && circle > 0 && hole < 0, `外周と円は正、穴は負: ${exterior}, ${circle}, ${hole}`);
assert.equal(render.activeOutline.subpaths.length, 3);
assert.equal(render.inactiveOutline.subpaths.length, 1, "稼働していない施設は破線の輪郭だけ");
assert.deepEqual([...render.covered], [10, 11]);
assert.deepEqual([...render.uncovered], [12]);
console.log("PASS: coverage fill winding (union with holes), active/inactive outlines, building sets");
