/**
 * 地区編集のブラシが拾う対象(道路の面)を、地図のベクターから組み立てる。
 *
 * 道路は始点→終点の向きに対する左右の面ごとに地区を持ち(PATH_SIDE_LEFT / PATH_SIDE_RIGHT)、
 * 土地区画は接道のうち区画がある側の面の地区に所属する。ブラシが塗るのは面で、拾い方は2つある。
 *   - ブロック: 生活道路の中心線で囲まれた領域。触れたブロックの境界の道路の、ブロック側の面を拾う
 *     (ブロックの中で行き止まる道路は両面がブロック側になる)。
 *   - 沿道: 道路区間の片側を拾い、途中の分割点を越えて選択を広げない。
 *     道路の半分の帯と接道区画のどちらでも選べる。選んだ面の間に1本だけ残った道路の面も補う。
 */

import { PATH_SIDE_LEFT, PATH_SIDE_RIGHT } from "../../constants";

export type Ring = [number, number][];
type Bounds = { x1: number; z1: number; x2: number; z2: number };

/** 道路の面の識別子("道路ID:面") */
export type FaceKey = string;

export function faceKey(pathId: number, side: number): FaceKey {
  return `${pathId}:${side}`;
}

export function parseFaceKey(key: FaceKey): { pathId: number; side: number } {
  const [pathId, side] = key.split(":").map(Number);
  return { pathId, side };
}

/** 生活道路。両端のノードID(未接続は0以下)・始点→終点の中心線・道路幅(m) */
export type StreetEdge = { pathId: number; startNodeId: number; endNodeId: number; centerline: Ring; width: number };

export type Block = { ring: Ring; faces: FaceKey[]; bounds: Bounds };
export type Frontage = { face: FaceKey; rings: Ring[]; bounds: Bounds };
export type RoadTarget = { faces: FaceKey[]; rings: Ring[]; bounds: Bounds; neighbors: FaceKey[] };

export function pointInRing(x: number, z: number, ring: Ring): boolean {
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const [xi, zi] = ring[i];
    const [xj, zj] = ring[j];
    if (zi > z !== zj > z && x < ((xj - xi) * (z - zi)) / (zj - zi) + xi) inside = !inside;
  }
  return inside;
}

function ringBounds(rings: Ring[]): Bounds {
  const b = { x1: Infinity, z1: Infinity, x2: -Infinity, z2: -Infinity };
  for (const ring of rings) {
    for (const [x, z] of ring) {
      b.x1 = Math.min(b.x1, x);
      b.z1 = Math.min(b.z1, z);
      b.x2 = Math.max(b.x2, x);
      b.z2 = Math.max(b.z2, z);
    }
  }
  return b;
}

/** リングの符号付き面積。x-z平面で反時計回りが正 */
export function signedArea(ring: Ring): number {
  let area = 0;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) area += ring[j][0] * ring[i][1] - ring[i][0] * ring[j][1];
  return area / 2;
}

function segmentDistanceSq(px: number, pz: number, a: [number, number], b: [number, number]): number {
  const dx = b[0] - a[0];
  const dz = b[1] - a[1];
  const len = dx * dx + dz * dz;
  const t = len > 0 ? Math.max(0, Math.min(1, ((px - a[0]) * dx + (pz - a[1]) * dz) / len)) : 0;
  const ex = a[0] + dx * t - px;
  const ez = a[1] + dz * t - pz;
  return ex * ex + ez * ez;
}

/** 中心 (x, z)・半径 r の円が、bounds を持つリング群のどれかに触れるか */
export function circleTouches(x: number, z: number, r: number, rings: Ring[], bounds: Bounds): boolean {
  if (x + r < bounds.x1 || x - r > bounds.x2 || z + r < bounds.z1 || z - r > bounds.z2) return false;
  const rr = r * r;
  for (const ring of rings) {
    if (pointInRing(x, z, ring)) return true;
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
      if (segmentDistanceSq(x, z, ring[j], ring[i]) <= rr) return true;
    }
  }
  return false;
}

/** 輪郭のない地区も道路の面から選び、外周が別地区を覆う場所では実際の面所属を優先する。 */
export function findZoneAt(
  x: number, z: number,
  zones: ReadonlyMap<number, { polygon: Ring; area: number }>,
  frontages: Frontage[], faceZones: ReadonlyMap<FaceKey, number>
): number | null {
  for (const frontage of frontages) {
    if (!circleTouches(x, z, 0, frontage.rings, frontage.bounds)) continue;
    const zoneId = faceZones.get(frontage.face);
    if (zoneId && zones.has(zoneId)) return zoneId;
  }
  let found: { id: number; area: number } | null = null;
  for (const [id, zone] of zones) {
    if (pointInRing(x, z, zone.polygon) && (!found || zone.area < found.area)) found = { id, area: zone.area };
  }
  return found?.id ?? null;
}

/** 点列の先頭から出ていく向き(x-z平面の角度) */
function leavingAngle(points: Ring): number {
  const [x0, z0] = points[0];
  for (let i = 1; i < points.length; i++) {
    const dx = points[i][0] - x0;
    const dz = points[i][1] - z0;
    if (Math.abs(dx) + Math.abs(dz) > 1e-6) return Math.atan2(dz, dx);
  }
  return 0;
}

/**
 * 生活道路の網の面(道路の中心線で囲まれた領域)のうち、囲まれているものをブロックとして返す。
 *
 * 道路1本を向きの逆な2本の半辺にし、各ノードで出ていく半辺を角度順に並べて、到着した半辺の逆向きから
 * 時計回りに次の半辺へ進む(面を常に左に見て一周する)。始点→終点の半辺は道路の左の面、逆向きの半辺は
 * 右の面を、たどった領域に向ける。反時計回りに一周した領域が囲まれたブロックで、時計回りの一周は
 * どこまでも開いた外側。ノードを共有しない立体交差は交わらないものとして扱う。
 */
export function buildBlocks(streets: StreetEdge[]): Block[] {
  type HalfEdge = { pathId: number; side: number; from: number; to: number; angle: number; points: Ring };
  const halves: HalfEdge[] = [];
  for (const s of streets) {
    if (s.startNodeId <= 0 || s.endNodeId <= 0 || s.centerline.length < 2) continue;
    const reversed = [...s.centerline].reverse();
    halves.push({ pathId: s.pathId, side: PATH_SIDE_LEFT, from: s.startNodeId, to: s.endNodeId, angle: leavingAngle(s.centerline), points: s.centerline });
    halves.push({ pathId: s.pathId, side: PATH_SIDE_RIGHT, from: s.endNodeId, to: s.startNodeId, angle: leavingAngle(reversed), points: reversed });
  }
  const twin = (i: number) => i ^ 1;
  const outgoing = new Map<number, number[]>();
  halves.forEach((h, i) => {
    const list = outgoing.get(h.from);
    if (list) list.push(i);
    else outgoing.set(h.from, [i]);
  });
  const order = new Int32Array(halves.length);
  for (const list of outgoing.values()) {
    list.sort((a, b) => halves[a].angle - halves[b].angle);
    list.forEach((i, k) => (order[i] = k));
  }
  const next = (i: number) => {
    const list = outgoing.get(halves[i].to)!;
    return list[(order[twin(i)] - 1 + list.length) % list.length];
  };

  const blocks: Block[] = [];
  const visited = new Uint8Array(halves.length);
  for (let start = 0; start < halves.length; start++) {
    if (visited[start]) continue;
    const cycle: number[] = [];
    for (let i = start; !visited[i]; i = next(i)) {
      visited[i] = 1;
      cycle.push(i);
    }
    const ring: Ring = [];
    for (const i of cycle) ring.push(...halves[i].points.slice(0, -1));
    if (ring.length < 3 || signedArea(ring) <= 0) continue;
    const faces = new Set(cycle.map((i) => faceKey(halves[i].pathId, halves[i].side)));
    blocks.push({ ring, faces: [...faces], bounds: ringBounds([ring]) });
  }
  return blocks;
}

/** 道路の片側の面の帯(中心線からその側の端まで)。左は始点→終点の向きの左(x-z平面の反時計回り側) */
function halfRibbon(centerline: Ring, halfWidth: number, side: number): Ring | null {
  const points = centerline.filter(
    (p, i) => i === 0 || Math.hypot(p[0] - centerline[i - 1][0], p[1] - centerline[i - 1][1]) > 1e-6
  );
  if (points.length < 2 || !(halfWidth > 0)) return null;
  const offset = halfWidth * (side === PATH_SIDE_LEFT ? 1 : -1);
  const normals = points.slice(0, -1).map((p, i) => {
    const dx = points[i + 1][0] - p[0];
    const dz = points[i + 1][1] - p[1];
    const len = Math.hypot(dx, dz);
    return [-dz / len, dx / len];
  });
  const edge = points.map((p, i): [number, number] => {
    const a = normals[Math.max(0, i - 1)];
    const b = normals[Math.min(normals.length - 1, i)];
    const len = Math.hypot(a[0] + b[0], a[1] + b[1]) || 1;
    return [p[0] + (offset * (a[0] + b[0])) / len, p[1] + (offset * (a[1] + b[1])) / len];
  });
  return [...points, ...edge.reverse()];
}

/**
 * 沿道の対象を面ごとに組み立てる。道路の片側の半分の帯と、その面に接道する土地区画を1つにまとめる。
 * 接道か面が決まっていない区画は除く。
 */
export function buildFrontages(
  streets: StreetEdge[],
  parcels: { front_path_id?: number; front_path_side?: number; polygon?: Ring | null }[]
): Frontage[] {
  const groups = new Map<FaceKey, Ring[]>();
  const add = (key: FaceKey, ring: Ring) => {
    const rings = groups.get(key);
    if (rings) rings.push(ring);
    else groups.set(key, [ring]);
  };
  for (const street of streets) {
    for (const side of [PATH_SIDE_LEFT, PATH_SIDE_RIGHT]) {
      const ribbon = halfRibbon(street.centerline, street.width / 2, side);
      if (ribbon) add(faceKey(street.pathId, side), ribbon);
    }
  }
  for (const parcel of parcels) {
    const pathId = parcel.front_path_id ?? 0;
    const side = parcel.front_path_side;
    if (pathId <= 0 || (side !== PATH_SIDE_LEFT && side !== PATH_SIDE_RIGHT)) continue;
    if (!parcel.polygon || parcel.polygon.length < 3) continue;
    add(faceKey(pathId, side), parcel.polygon);
  }
  return [...groups].map(([face, rings]) => ({ face, rings, bounds: ringBounds(rings) }));
}

/**
 * 選択単位は道路区間の片側のままにし、1区間の補完に使う両端の隣接面を求める。
 */
export function buildRoadTargets(streets: StreetEdge[], frontages: Frontage[]): RoadTarget[] {
  type Port = { pathId: number; atStart: boolean; angle: number };
  const portsByNode = new Map<number, Port[]>();
  for (const street of streets) {
    if (street.centerline.length < 2) continue;
    for (const atStart of [true, false]) {
      const nodeId = atStart ? street.startNodeId : street.endNodeId;
      if (nodeId <= 0) continue;
      const ports = portsByNode.get(nodeId) ?? [];
      ports.push({ pathId: street.pathId, atStart,
        angle: leavingAngle(atStart ? street.centerline : [...street.centerline].reverse()) });
      portsByNode.set(nodeId, ports);
    }
  }

  const byFace = new Map(frontages.map((frontage) => [frontage.face, frontage]));
  const neighbors = new Map<FaceKey, FaceKey[]>();
  const connect = (a: FaceKey, b: FaceKey) => {
    if (!byFace.has(a) || !byFace.has(b)) return;
    neighbors.set(a, [...(neighbors.get(a) ?? []), b]);
    neighbors.set(b, [...(neighbors.get(b) ?? []), a]);
  };
  for (const ports of portsByNode.values()) {
    if (ports.length < 2) continue;
    ports.sort((a, b) => a.angle - b.angle);
    for (let i = 0; i < ports.length; i++) {
      const a = ports[i];
      const b = ports[(i + 1) % ports.length];
      // ノードから見た左側と、反時計回りで隣の道路の右側が同じ街区側になる。
      connect(
        faceKey(a.pathId, a.atStart ? PATH_SIDE_LEFT : PATH_SIDE_RIGHT),
        faceKey(b.pathId, b.atStart ? PATH_SIDE_RIGHT : PATH_SIDE_LEFT));
    }
  }

  return frontages.map((frontage) => ({
    faces: [frontage.face], rings: frontage.rings, bounds: frontage.bounds,
    neighbors: neighbors.get(frontage.face) ?? [],
  }));
}

/**
 * 両端で直接なぞった面に挟まれる道路の面を補う。交差点の同じ街区側だけをつなぐ。
 * 補完結果を次の判定に使わず、複数の道路を経由する遠回りへ選択を広げない。
 */
export function findSingleRoadGaps(targets: RoadTarget[], touched: ReadonlySet<{ faces: FaceKey[] }>): RoadTarget[] {
  const faces = new Set([...touched].flatMap((target) => target.faces));
  return targets.filter((target) => !touched.has(target)
    && target.neighbors.length === 2 && target.neighbors.every((face) => faces.has(face)));
}
