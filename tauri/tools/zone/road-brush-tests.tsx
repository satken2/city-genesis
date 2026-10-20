import assert from "node:assert/strict";
import { act } from "react";
import { createRoot } from "react-dom/client";
import { ZoneEditWindow } from "../../src/components/zone/ZoneEditWindow";
import { PATH_SIDE_LEFT as LEFT, PATH_SIDE_RIGHT as RIGHT, PATH_STREET } from "../../src/constants";
import {
  buildFrontages, buildRoadTargets, faceKey, findSingleRoadGaps, findZoneAt,
  type RoadTarget, type StreetEdge,
} from "../../src/components/zone/zoneBrushTargets";
import { mapProps } from "./mock-map";

const street = (pathId: number, startNodeId: number, endNodeId: number,
  a: [number, number], b: [number, number]): StreetEdge => ({
  pathId, startNodeId, endNodeId, centerline: [a, b], width: 6,
});
const split = [
  street(1, 1, 2, [0, 0], [3, 0]),
  street(2, 3, 2, [57, 0], [3, 0]),
  street(3, 3, 4, [57, 0], [60, 0]),
  street(4, 4, 5, [60, 0], [70, 0]),
  street(5, 4, 6, [60, 0], [60, -10]),
];
const parcel = { front_path_id: 2, front_path_side: RIGHT,
  polygon: [[10, 1], [50, 1], [50, 10], [10, 10], [10, 1]] as [number, number][] };
const frontages = buildFrontages(split, [parcel]);
const splitTargets = buildRoadTargets(split, frontages);
const targetFor = (targets: RoadTarget[], id: number, side: number) => {
  const target = targets.find((target) => target.faces.includes(faceKey(id, side)));
  assert.ok(target);
  return target;
};
const north = targetFor(splitTargets, 2, RIGHT);
assert.deepEqual(north.faces, [faceKey(2, RIGHT)]);
assert.ok(north.rings.includes(parcel.polygon));
assert.notEqual(targetFor(splitTargets, 1, LEFT), north);
assert.notEqual(targetFor(splitTargets, 3, LEFT), north);
assert.notEqual(targetFor(splitTargets, 4, LEFT), north);
assert.notEqual(targetFor(splitTargets, 2, LEFT), north);
assert.deepEqual(findSingleRoadGaps(splitTargets, new Set([north])), []);
assert.deepEqual(findSingleRoadGaps(splitTargets, new Set([
  targetFor(splitTargets, 1, LEFT), targetFor(splitTargets, 3, LEFT),
])), [north]);
assert.equal(findZoneAt(25, 5, new Map([[10, { polygon: [], area: 0 }], [20, { polygon: [], area: 0 }]]),
  frontages, new Map([[faceKey(1, LEFT), 10], [faceKey(2, RIGHT), 20]])), 20);
console.log("PASS: one frontage selects one segment; only two directly selected sides can fill their single gap, including reversed paths");

const gapStreets = [
  street(10, 10, 11, [0, 0], [10, 0]),
  street(11, 11, 12, [10, 0], [20, 0]),
  street(12, 12, 13, [20, 0], [30, 0]),
  street(13, 11, 14, [10, 0], [10, -10]),
  street(14, 12, 15, [20, 0], [20, -10]),
];
const gapTargets = buildRoadTargets(gapStreets, buildFrontages(gapStreets, []));
assert.deepEqual(findSingleRoadGaps(gapTargets, new Set([
  targetFor(gapTargets, 10, LEFT), targetFor(gapTargets, 12, LEFT),
])), [targetFor(gapTargets, 11, LEFT)]);
assert.deepEqual(findSingleRoadGaps(gapTargets, new Set([
  targetFor(gapTargets, 10, LEFT), targetFor(gapTargets, 12, RIGHT),
])), []);
const twoGaps = [
  ...gapStreets.filter((path) => path.pathId !== 12),
  street(12, 12, 16, [20, 0], [30, 0]),
  street(15, 16, 17, [30, 0], [40, 0]),
  street(16, 16, 18, [30, 0], [30, -10]),
];
const twoGapTargets = buildRoadTargets(twoGaps, buildFrontages(twoGaps, []));
assert.deepEqual(findSingleRoadGaps(twoGapTargets, new Set([
  targetFor(twoGapTargets, 10, LEFT), targetFor(twoGapTargets, 15, LEFT),
])), []);
console.log("PASS: only a single missing road face between painted sides is filled; opposite sides and two-road gaps remain separate");

const requests: { path_ids: number[]; sides: number[] }[] = [];
globalThis.fetch = async (_input, init) => {
  if (init?.method === "POST") {
    requests.push(JSON.parse(String(init.body)));
    return Response.json({ code: "ZONE_SIZE_ERROR", message: "diagnostic" }, { status: 400 });
  }
  return Response.json({ map_width: 1000, map_height: 1000 });
};
async function paint(streets: StreetEdge[], parcels: typeof parcel[], points: [number, number][], mode: "road" | "block") {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  await act(async () => { root.render(<ZoneEditWindow apiBaseUrl="http://test" />); });
  await act(async () => {
    mapProps.onVectorsLoaded!({ zones: [], buildings: [], parcels,
      paths: streets.map((path) => ({ path_id: path.pathId, path_type: PATH_STREET,
        path_variant: 0, width: path.width, start_node_id: path.startNodeId, end_node_id: path.endNodeId,
        start_point: [path.centerline[0][0], 0, path.centerline[0][1]],
        end_point: [path.centerline[1][0], 0, path.centerline[1][1]] })) });
  });
  await act(async () => { container.querySelector<HTMLButtonElement>('[data-guide-value="create"]')!.click(); });
  await act(async () => { container.querySelector<HTMLButtonElement>(`[data-guide-value="${mode}"]`)!.click(); });
  const event = ([x, z]: [number, number]) => ({ world: { x, z }, screen: { x: 0, y: 0 },
    view: { zoom: 40, offsetX: 0, offsetY: 0 }, event: { ctrlKey: true, button: 0 } as PointerEvent });
  await act(async () => { assert.equal(mapProps.onPointerDown!(event(points[0])), true); });
  for (const point of points.slice(1)) await act(async () => { mapProps.onPointerMove!(event(point)); });
  const paints: number[][][] = [];
  let ring: number[][] = [];
  const ctx = { canvas: {}, save() {}, restore() {}, beginPath() { ring = []; },
    moveTo(x: number, z: number) { ring.push([x, z]); }, lineTo(x: number, z: number) { ring.push([x, z]); },
    closePath() {}, fill() { paints.push([...ring]); }, stroke() {}, arc() {},
  } as unknown as CanvasRenderingContext2D;
  mapProps.animatedOverlayDraw!(ctx, event(points[0]).view, 0);
  await act(async () => { mapProps.onPointerUp!(event(points.at(-1)!)); });
  await act(async () => { root.unmount(); });
  container.remove();
  const request = requests.at(-1)!;
  return { faces: new Set(request.path_ids.map((id, i) => faceKey(id, request.sides[i]))), paints };
}

const splitPaint = await paint(split, [parcel], [[25, 5]], "road");
assert.deepEqual(splitPaint.faces, new Set(north.faces));
assert.ok(!splitPaint.paints.some((ring) => ring.some(([x]) => x === 0 || x === 60)));
const gapPaint = await paint(gapStreets, [], [[5, 1], [5, 30], [25, 30], [25, 1]], "road");
assert.deepEqual(gapPaint.faces, new Set([faceKey(10, LEFT), faceKey(11, LEFT), faceKey(12, LEFT)]));
assert.ok(gapPaint.paints.some((ring) => ring.some(([x]) => x === 10) && ring.some(([x]) => x === 20)));
const square = [
  street(21, 21, 22, [0, 0], [10, 0]), street(22, 22, 23, [10, 0], [10, 10]),
  street(23, 23, 24, [10, 10], [0, 10]), street(24, 24, 21, [0, 10], [0, 0]),
];
assert.deepEqual((await paint(square, [], [[5, 5]], "block")).faces,
  new Set(square.map((path) => faceKey(path.pathId, LEFT))));
console.log("PASS: a single parcel previews and submits only its frontage; one-road gap filling and block painting remain available");
