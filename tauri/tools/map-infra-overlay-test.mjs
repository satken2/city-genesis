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
const { buildCoverageRender, pairWithFeedNetwork } = exports;

// 外周が時計回り・穴が反時計回りで届いても、外周は正、穴は負の向きに揃える(円は Canvas の既定で正)。
const square = (x, z, h) => [[x - h, z - h], [x - h, z + h], [x + h, z + h], [x + h, z - h]];
const coverageData = {
  product_id: 306,
  suppliers: [
    { agent_id: 1, name: "a", variant: 1058, status: 1, utilization: 1, load: 0.2, center_point: [0, 0],
      region: { type: "polygon", polygons: [{ exterior: square(0, 0, 100), holes: [[...square(0, 0, 20)].reverse()] }] } },
    { agent_id: 2, name: "b", variant: 1058, status: 1, utilization: 1, load: 0.5, center_point: [150, 0],
      region: { type: "circle", center: [150, 0], radius: 80 } },
    { agent_id: 3, name: "c", variant: 1058, status: 4, utilization: 0, load: 0, center_point: [-300, 0],
      region: { type: "circle", center: [-300, 0], radius: 80 } },
  ],
  covered_building_ids: [10, 11],
  uncovered_building_ids: [12],
};
const render = buildCoverageRender(coverageData);
assert.equal(render.fill.subpaths.length, 3, "塗りは稼働中の施設だけ(外周・穴・円)");
const [exterior, hole, circle] = render.fill.subpaths.map(signedArea);
assert.ok(exterior > 0 && circle > 0 && hole < 0, `外周と円は正、穴は負: ${exterior}, ${circle}, ${hole}`);
assert.equal(render.activeOutline.subpaths.length, 3);
assert.equal(render.inactiveOutline.subpaths.length, 1, "稼働していない施設は破線の輪郭だけ");
assert.deepEqual([...render.covered], [10, 11]);
assert.deepEqual([...render.uncovered], [12]);
console.log("PASS: coverage fill winding (union with holes), active/inactive outlines, building sets");

// 業務逼迫度の円グラフは施設ごとに1つ。一緒に描く網に出る施設は網の記号の下に描くので、効果範囲の側には置かない。
assert.deepEqual(render.pies.map((p) => p.x), [0, 150, -300], "網が無ければ全施設の中心に置く");
assert.deepEqual(render.pies.map((p) => p.load), [0.2, 0.5, 0], "円グラフは稼働率ではなく業務逼迫度");
const withNetwork = buildCoverageRender(coverageData, { producers: [], consumers: [{ agent_id: 2 }] });
assert.deepEqual(withNetwork.pies.map((p) => p.x), [0, -300], "網にも出る施設は効果範囲の側で描かない");
console.log("PASS: one load pie per facility (network markers take the facilities they show)");

// 効果範囲を持たない財(消防など)は施設の円グラフだけを置き、範囲の塗りと輪郭は描かない。
const locationsOnly = buildCoverageRender({
  product_id: 125,
  suppliers: [{ agent_id: 4, name: "d", variant: 1052, status: 1, utilization: 1, load: 0.1, center_point: [40, 60], region: null }],
  covered_building_ids: [],
  uncovered_building_ids: [],
});
assert.equal(locationsOnly.fill.subpaths.length + locationsOnly.activeOutline.subpaths.length + locationsOnly.inactiveOutline.subpaths.length, 0);
assert.deepEqual(JSON.parse(JSON.stringify(locationsOnly.pies)), [{ x: 40, z: 60, load: 0.1 }]);
console.log("PASS: suppliers without an effect range are drawn as pies only");

// 供給施設がインフラ財を受け取るサービス財は網と組にし、組になったインフラ財は単独の選択肢から外す。
const options = pairWithFeedNetwork([
  { productId: 401, kind: "network", supplierVariants: [], feedProductId: null },
  { productId: 403, kind: "network", supplierVariants: [], feedProductId: null },
  { productId: 303, kind: "coverage", supplierVariants: [1062], feedProductId: 401 },
  { productId: 306, kind: "coverage", supplierVariants: [1058], feedProductId: null },
]);
assert.deepEqual(JSON.parse(JSON.stringify(options)), [
  { productId: 403, selection: { network: 403, coverage: null } },
  { productId: 303, selection: { network: 401, coverage: 303 } },
  { productId: 306, selection: { network: null, coverage: 306 } },
]);
console.log("PASS: service goods paired with the infrastructure network their suppliers receive");
