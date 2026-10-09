import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";
import ts from "typescript";

// 実コンポーネントの描画変換とイベントを動かす。DOMと通信だけを置き換え、
// 地形の北側(ワールド+Z)をクリックしたときの座標、パン、ズーム、重ね描きの整合を検査する。
const effects = [];
const canvases = [];
let frame;
let drawn;
let animated;
let clicked;
const texts = [];
const paints = [];
let pathsResponse = { paths: [] };
let buildingsResponse = { buildings: [] };
const heatmapRequests = [];
const images = [];
const grid = { origin_x: -500, origin_z: -500, width_m: 1000, height_m: 1000, min: 0, max: 1,
  levels: [{ rows: 200, cols: 200, spacing: 5 }, { rows: 50, cols: 50, spacing: 20 }, { rows: 12, cols: 12, spacing: 1000 / 12 }] };
const handle = {};
const near = (a, b) => assert.ok(Math.abs(a - b) < 1e-8, `${a} != ${b}`);
function eventTarget() {
  const events = new Map();
  return {
    addEventListener: (name, fn) => events.set(name, fn),
    removeEventListener: (name) => events.delete(name),
    dispatch: (name, e) => events.get(name)?.({ type: name, button: 0, pointerId: 1, preventDefault() {}, ...e }),
  };
}
function context() {
  let matrix = [1, 0, 0, 1, 0, 0];
  const stack = [];
  return new Proxy({
    setTransform: (...m) => { matrix = m; },
    save: () => stack.push([...matrix]),
    restore: () => { matrix = stack.pop(); },
    translate: (x, y) => { matrix[4] += matrix[0] * x; matrix[5] += matrix[3] * y; },
    scale: (x, y) => { matrix[0] *= x; matrix[3] *= y; },
    point: (x, z) => ({ x: matrix[0] * x + matrix[4], y: matrix[3] * z + matrix[5] }),
    fillText: (text, x, y) => texts.push({ text, x: matrix[0] * x + matrix[4], y: matrix[3] * y + matrix[5], upright: matrix[3] > 0 }),
    createImageData: (w, h) => ({ data: new Uint8ClampedArray(w * h * 4) }),
    putImageData: function (image) { this.imageData = image; },
    drawImage: function (image, x, z, width, height) { images.push({ image, x, z, width, height, south: this.point(x, z), north: this.point(x, z + height) }); },
    measureText: (text) => ({ width: text.length * 8 }),
    fill: function () { paints.push({ op: "fill", style: this.fillStyle, alpha: this.globalAlpha ?? 1 }); },
    stroke: function () { paints.push({ op: "stroke", style: this.strokeStyle, alpha: this.globalAlpha ?? 1 }); },
  }, { get: (obj, key) => obj[key] ?? (() => {}) });
}
function canvas() {
  const ctx = context();
  return { ...eventTarget(), style: {}, ctx, getContext: () => ctx,
    getBoundingClientRect: () => ({ left: 0, top: 0, right: 800, bottom: 600 }) };
}
function jsx(type, props) {
  if (props.ref) {
    const node = type === "canvas" ? canvas() : { clientWidth: 800, clientHeight: 600 };
    props.ref.current = node;
    if (type === "canvas") canvases.push(node);
  }
  return { type, props };
}
const windowMock = { ...eventTarget(), devicePixelRatio: 2, setTimeout, clearTimeout,
  requestAnimationFrame: (cb) => { frame = cb; return 1; }, cancelAnimationFrame() {} };
const imports = {
  "react": { forwardRef: (fn) => fn, useRef: (current) => ({ current }),
    useEffect: (fn) => effects.push(fn), useImperativeHandle: (ref, fn) => Object.assign(ref, fn()) },
  "react/jsx-runtime": { jsx, jsxs: jsx },
  "react-i18next": { useTranslation: () => ({ t: (key) => key }) },
  "../../contexts/MasterDataContext": { useMasterData: () => null },
  "../../utils/api": {
    buildApiUrl: (_, path) => path,
    fetchApi: async (url) => url.startsWith("/api/v1/city/")
      ? (heatmapRequests.push(url), url.includes("environment-sources")
        ? { path_ids: [1, 2], path_values: [0, 65535], building_ids: [10], building_values: [32768], min: 0, max: 100 }
        : grid)
      : url.endsWith("settings")
      ? { map_width: 100, map_height: 200, near_map_spacing: 100, cell_size: 100 }
      : url.startsWith("/api/v1/paths") ? pathsResponse
      : url.startsWith("/api/v1/buildings") ? buildingsResponse
      : { type: "Polygon", coordinates: [] },
  },
  "../terrain/terrainData": { computePixelRGB: () => [0, 0, 0], decodeFloat16Array: () => new Float32Array(6), normalizeGroundCover: () => 1 },
  "../../constants": { PATH_MOTORWAY: 1, PATH_STREET: 2, PATH_WALKWAY: 3, PATH_TRACK: 4, ROAD_COLOR_PALETTE: ["#7F77DD"],
    VALUE_NONE: -1, PATH_TYPES_NAMED_ROAD: [2, 1] },
  "../../utils/ipc": { sendRefreshRoads: async () => {} },
  "@tauri-apps/api/window": { getCurrentWindow: () => ({ isVisible: async () => true, onFocusChanged: async () => () => {} }) },
};
function load(path) {
  const { outputText } = ts.transpileModule(readFileSync(new URL(path, import.meta.url), "utf8"), {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX },
  });
  const exports = {};
  vm.runInNewContext(outputText, {
    exports, require: (name) => { assert.ok(name in imports, name); return imports[name]; },
    window: windowMock, document: { createElement: canvas }, console, AbortController,
    ResizeObserver: class { observe() {} disconnect() {} },
    Path2D: class { rect() {} moveTo() {} lineTo() {} closePath() {} addPath() {} bezierCurveTo() {} },
    fetch: async (url) => {
      if (url.startsWith("/api/v1/city/heatmaps/")) {
        heatmapRequests.push(url);
        const level = grid.levels[Number(url.split("/").at(-1))];
        const values = new Uint16Array(level.rows * level.cols);
        values[values.length - 1] = 65535;
        return { ok: true, arrayBuffer: async () => values.buffer };
      }
      return { ok: true, arrayBuffer: async () => new ArrayBuffer(12) };
    },
  });
  return exports;
}
imports["./roadSurface"] = load("../src/components/map/roadSurface.ts");
imports["./mapHeatmap"] = load("../src/components/map/mapHeatmap.ts");
imports["../terrain/terrainRelief"] = load("../src/components/terrain/terrainRelief.ts");
imports["./mapSymbols"] = load("../src/components/map/mapSymbols.ts");
imports["./mapLabels"] = load("../src/components/map/mapLabels.ts");
imports["@tauri-apps/api/event"] = { listen: async () => () => {}, emit: async () => {} };
// 幹線の判定は本物の hasRoad で検査する(一覧の取得だけ差し替える)
const roadApi = load("../src/components/road/roadApi.ts");
imports["../road/roadApi"] = { ...roadApi, fetchRoadList: async () => ({ version: 0, roads: [] }) };
for (const [id, expected] of [[-1, false], [0, false], [null, false], [undefined, false], [3, true]]) {
  assert.equal(roadApi.hasRoad(id), expected, `hasRoad(${id})`);
}
imports["./transitLineDraw"] = { drawTransitLines() {} };
imports["./infraOverlay"] = { buildCoverageRender() {}, buildNetworkRender() {}, drawCoverageFill() {}, drawCoverageOutlines() {}, drawNetwork() {} };
imports["../intersection/interchangeName"] = load("../src/components/intersection/interchangeName.ts");
const { CityMapCanvas, DEFAULT_CITY_MAP_LAYERS } = load("../src/components/map/CityMapCanvas.tsx");
assert.deepEqual(Object.keys(DEFAULT_CITY_MAP_LAYERS).filter((key) => DEFAULT_CITY_MAP_LAYERS[key]).sort(),
  ["allRoads", "buildings", "interchanges", "railways", "stations", "trunkRoads"]);
const { worldToScreen, screenToWorld } = load("../src/components/path/pathShapeOverlay.ts");
const { drawMapLabels } = imports["./mapLabels"];
CityMapCanvas({ apiBaseUrl: "test", showCompass: false,
  layers: Object.fromEntries(Object.keys(DEFAULT_CITY_MAP_LAYERS).map((key) => [key, false])),
  overlayDraw: (ctx, view) => {
    drawn = { view, north: ctx.point(0, 50), south: ctx.point(0, -50) };
    texts.length = 0;
    drawMapLabels(ctx, view, 800, 600, [{ name: "N", center_point: [0, 50] }], []);
  },
  animatedOverlayDraw: (ctx) => { animated = ctx.point(0, 50); },
  onMapClick: (e) => { clicked = e.world; },
}, handle);
const cleanups = effects.map((fn) => fn());
await new Promise((resolve) => setImmediate(resolve));
const base = canvases[0];
const screenNorth = () => ({ x: drawn.north.x / 2, y: drawn.north.y / 2 });
const click = (p) => {
  base.dispatch("pointerdown", { clientX: p.x, clientY: p.y });
  windowMock.dispatch("pointerup", { clientX: p.x, clientY: p.y });
};
assert.ok(drawn.north.y < drawn.south.y, "North (+Z) must be above south (-Z)");
click(screenNorth()); near(clicked.x, 0); near(clicked.z, 50);
// 地名ラベルはY反転を打ち消して、北側の地点と同じ画面位置に正立で描く。
const label = texts.find((t) => t.text === "N");
assert.ok(label?.upright, "Map labels must be drawn upright");
near(label.x, drawn.north.x); near(label.y, drawn.north.y);
const point = worldToScreen(drawn.view, { x: 0, z: 50 });
near(point.x, screenNorth().x); near(point.y, screenNorth().y);
near(screenToWorld(drawn.view, point).z, 50);

const before = screenNorth();
base.dispatch("pointerdown", { clientX: before.x, clientY: before.y });
windowMock.dispatch("pointermove", { clientX: before.x + 30, clientY: before.y + 40 });
windowMock.dispatch("pointerup", { clientX: before.x + 30, clientY: before.y + 40 });
near(screenNorth().x, before.x + 30); near(screenNorth().y, before.y + 40);
const anchor = screenNorth();
base.dispatch("wheel", { clientX: anchor.x, clientY: anchor.y, deltaY: -1 });
near(screenNorth().x, anchor.x); near(screenNorth().y, anchor.y);
click(anchor); near(clicked.z, 50);
handle.fitBounds({ x1: -20, z1: 20, x2: 20, z2: 80 });
near(screenNorth().x, 400); near(screenNorth().y, 300);
frame(100);
near(animated.x, drawn.north.x); near(animated.y, drawn.north.y);
for (const cleanup of cleanups.reverse()) cleanup?.();

// 幹線道路レイヤーは高速道路と道路(road_id)に属する区間だけを描き、強調の帯は道路色で重ねる。
const street = (id, x, roadId, type = 2) => ({ path_id: id, path_type: type, path_variant: 0, road_id: roadId,
  start_point: [x, 0, 0], end_point: [x, 0, 50], curved: 0 });
pathsResponse = { paths: [street(1, 0, -1), street(2, 10, 3), street(3, 20, -1, 1), street(4, 30, 0)] };
const renderMap = async (props, layerOverrides = {}) => {
  const effectStart = effects.length;
  CityMapCanvas({ apiBaseUrl: "test", showCompass: false,
    layers: { ...Object.fromEntries(Object.keys(DEFAULT_CITY_MAP_LAYERS).map((key) => [key, false])), trunkRoads: true,
      ...layerOverrides },
    ...props }, {});
  const disposers = effects.slice(effectStart).map((fn) => fn());
  for (let i = 0; i < 10; i++) await new Promise((resolve) => setImmediate(resolve));
  for (const dispose of disposers.reverse()) dispose?.();
};
paints.length = 0;
await renderMap({});
const fills = paints.filter((p) => p.op === "fill").map((p) => p.style);
assert.ok(fills.includes("#aab7c1"), "a street that belongs to a road is a trunk road");
assert.ok(fills.includes("#778896"), "motorways are always trunk roads");
// 幹線だけのレイヤーでは幹線でない帯を描かないので、次の assert は余計な帯が無いことだけを見る
assert.ok(!fills.includes("#c6ced3"), "nothing but trunk roads is drawn on the trunk layer");
// 全道路レイヤーも ON にすると、road_id の無い街路(-1・0)は一般の街路の色で塗られる。幹線扱いの退行ならこの色が消える
paints.length = 0;
await renderMap({}, { allRoads: true });
const allFills = paints.filter((p) => p.op === "fill").map((p) => p.style);
assert.ok(allFills.includes("#c6ced3"), "streets without a road are ordinary streets");
assert.ok(allFills.includes("#aab7c1"), "a street that belongs to a road stays a trunk road with all roads on");
assert.ok(allFills.includes("#778896"), "motorways stay trunk roads with all roads on");
paints.length = 0;
await renderMap({ focusPathIds: [2], focusColor: "#E4572E", focusDimOthers: true });
assert.ok(paints.some((p) => p.op === "stroke" && p.style === "#E4572E" && p.alpha === 1), "the focused section is drawn in the road color");
assert.ok(paints.some((p) => p.op === "fill" && p.style === "#aab7c1" && p.alpha < 1), "other roads are dimmed while focusing");

// 遠景では低解像度だけを取得し、南から北の行順を画像にも適用する。
heatmapRequests.length = 0;
images.length = 0;
await renderMap({ heatmap: "noise", initialBounds: { x1: -10000, z1: -10000, x2: 10000, z2: 10000 } });
assert.ok(heatmapRequests.includes("/api/v1/city/heatmaps/noise/1"));
assert.ok(!heatmapRequests.includes("/api/v1/city/heatmaps/noise/0"), "far view does not fetch the detailed image");
const overlay = images.find((image) => image.width === 1000);
assert.ok(overlay && overlay.north.y < overlay.south.y, "overlay rows run from south to north");
assert.equal(overlay.image.ctx.imageData.data[3], 0);
assert.ok(overlay.image.ctx.imageData.data.at(-1) > 0);

buildingsResponse = { buildings: [{ agent_id: 10, variant: 20, polygon: [[-2, -2], [2, -2], [2, 2], [-2, 2]], center_point: [0, 0] }] };
heatmapRequests.length = 0;
paints.length = 0;
let stats;
await renderMap({ heatmap: "noise", heatmapMode: "sources", onHeatmapStats: (value) => { stats = value; } }, { allRoads: true, buildings: true });
assert.ok(heatmapRequests.includes("/api/v1/city/environment-sources/noise"));
assert.ok(!heatmapRequests.some((url) => url.includes("/heatmaps/")), "sources do not request a raster");
assert.ok(paints.some((paint) => paint.op === "fill" && paint.style?.startsWith("rgb(")), "sources color road and building surfaces");
assert.equal(stats.mode, "sources");
assert.equal(stats.max, 100);

for (const kind of ["crime", "sunshine"]) {
  heatmapRequests.length = 0;
  await renderMap({ heatmap: kind, heatmapMode: "sources", onHeatmapStats: (value) => { stats = value; } });
  assert.ok(heatmapRequests.includes(`/api/v1/city/heatmaps/${kind}`));
  assert.ok(!heatmapRequests.some((url) => url.includes("environment-sources")));
  assert.equal(stats.mode, "overlay");
}
console.log("PASS: overlay orientation and alpha, far LOD fetching, source surface colors, sunshine/crime fallback");
console.log("PASS: north-up, upright labels, click coordinates, path editor coordinates, pan, cursor zoom, fitBounds, animated overlay (DPR=2), trunk roads = motorways + road members, road focus band");
