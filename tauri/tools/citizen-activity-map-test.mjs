import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";
import ts from "typescript";

const imports = {
  react: { useEffect() {}, useState: value => [value, () => {}] },
  "react/jsx-runtime": { jsx: (type, props) => ({ type, props }) },
  "react-i18next": { useTranslation: () => ({ t: key => key === "citizenTracking.visitKind.home" ? "自宅" : key }) },
  "../map/CityMapCanvas": { CityMapCanvas() {} },
};
const palette = { sunken: "#10161f", accent: "#4ea1ff", "text-label": "#a4bccc", good: "#6cd47f", warn: "#f0c040" };
function load(path) {
  const { outputText } = ts.transpileModule(readFileSync(new URL(path, import.meta.url), "utf8"), {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX },
  });
  const exports = {};
  vm.runInNewContext(outputText, { exports, require: name => {
    assert.ok(name in imports, name); return imports[name];
  }, document: { documentElement: {} }, getComputedStyle: () => ({
    getPropertyValue: token => palette[token.replace("--flat-", "")],
  }) });
  return exports;
}
imports["../map/mapRoute"] = load("../src/components/map/mapRoute.ts");
const { drawMapRoute } = imports["../map/mapRoute"];
const { CitizenActivityMap } = load("../src/components/citizen/CitizenActivityMap.tsx");

function context(zoom) {
  const strokes = [], labels = [], stack = [];
  let path = [], matrix = [zoom, -zoom, 0, 0];
  const ctx = {
    globalAlpha: 1, lineWidth: 1, strokeStyle: "", fillStyle: "", dash: [],
    save() { stack.push({ matrix: [...matrix], alpha: this.globalAlpha, dash: this.dash, width: this.lineWidth }); },
    restore() { const state = stack.pop(); matrix = state.matrix; this.globalAlpha = state.alpha; this.dash = state.dash; this.lineWidth = state.width; },
    translate(x, z) { matrix[2] += matrix[0] * x; matrix[3] += matrix[1] * z; },
    scale(x, y) { matrix[0] *= x; matrix[1] *= y; },
    beginPath() { path = []; }, moveTo(x, z) { path.push([x, z]); }, lineTo(x, z) { path.push([x, z]); },
    setLineDash(dash) { this.dash = [...dash]; },
    stroke() { strokes.push({ points: [...path], color: this.strokeStyle, width: this.lineWidth, alpha: this.globalAlpha, dash: [...this.dash] }); },
    roundRect() {}, fill() {},
    measureText(label) { return { width: label.length * parseFloat(this.font) * 0.6 }; },
    fillText(label) { labels.push({ label, x: matrix[2], y: matrix[3], upright: matrix[1] > 0, color: this.fillStyle }); },
  };
  return { ctx, strokes, labels };
}

const leg = (kind, points, color = null) => ({ kind, points, color });
const visits = [{ x: 0, z: 0 }, { x: 100, z: 200 }, { x: 100, z: 200 }, { x: null, z: 10000 }];
const outbound = [leg("walk", [[10, 0], [20, 0]]), leg("transit", [[20, 0], [1000, 200], [90, 200]], "#ff00aa")];
const map = CitizenActivityMap({ apiBaseUrl: "test", itinerary: { visits, trips: [
  { status: "ok", from_visit: 0, to_visit: 1, legs: outbound },
  { status: "ok", from_visit: 1, to_visit: 0, legs: [leg("drive", [[2000, 0], [0, 0]])] },
  { status: "unreachable", from_visit: 0, to_visit: 2, legs: [leg("walk", [[3000, 0]])] },
] } }).props.children;
assert.equal(map.props.interactive, true);
assert.deepEqual(JSON.parse(JSON.stringify(map.props.initialBounds)), { x1: -100, x2: 1100, z1: -100, z2: 300 },
  "遠回りの経路も収め、復路・到達不能の経路・座標のない訪問先は範囲に含めない");
const drawing = context(2);
map.props.overlayDraw(drawing.ctx, { zoom: 2 });
const routeStrokes = drawing.strokes.filter(stroke => stroke.points.length > 0);
assert.equal(routeStrokes.length, 8, "片道2区間と両端の接続を描き、復路は重ねない");
assert.deepEqual(routeStrokes[1].points, [[0, 0], [10, 0]], "自宅から道路への接続を点線で補う");
assert.deepEqual(routeStrokes[7].points, [[90, 200], [100, 200]], "道路から訪問先への接続を点線で補う");
assert.deepEqual(routeStrokes[3].dash, [1, 4], "徒歩の点線は画面上の間隔を保つ");
assert.equal(routeStrokes[5].color, "#ff00aa", "公共交通は路線色を使う");
assert.equal(routeStrokes[5].width, 2.5);
assert.deepEqual(drawing.labels.map(item => item.label), ["自宅", "1・2"], "同じ場所の訪問先は番号をまとめる");
assert.ok(drawing.labels.every(item => item.upright), "札の文字は北が上の地図でも正立する");
assert.equal(drawing.labels[1].x, 200);
assert.equal(drawing.labels[1].y, -400);

const focused = context(2);
drawMapRoute(focused.ctx, { zoom: 2 }, outbound, 1);
assert.equal(focused.strokes[1].alpha, 0.35, "都市地図の区間選択で他の区間を薄くする挙動を保つ");
assert.equal(focused.strokes[3].alpha, 1);
assert.equal(focused.strokes[3].width, 3.5);
assert.equal(focused.ctx.globalAlpha, 1, "後続のマーカーへ経路の透明度を残さない");

const homeOnly = CitizenActivityMap({ apiBaseUrl: "test", itinerary: { visits: [visits[0]], trips: [] } });
assert.equal(homeOnly.props.children.props.initialBounds.x2, 60, "訪問先がなくても自宅の周囲を表示する");
assert.equal(CitizenActivityMap({ apiBaseUrl: "test", itinerary: { visits: [visits[3]], trips: [] } }), null,
  "座標が1つもない行程では無関係な都市全体の地図を表示しない");
console.log("PASS: citizen itinerary map, outbound geometry, endpoint access, shared route styles and numbered markers");
