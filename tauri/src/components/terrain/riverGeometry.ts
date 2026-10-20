/**
 * Geometry utilities for the river "area polygon + water-surface profile line" model (RiverPayload).
 *
 * Contains only pure functions, without React or canvas rendering dependencies. Shared by the
 * terrain editor (area polygon extraction / 2D display) and 3D preview (water surface mesh),
 * and serves as a reference for the same contract in the backend (geo_rivers.py) and Unity
 * (RiverStore / RiverSurfaceManager).
 *
 * All coordinates use world XZ (map center at (0,0)); elevations are absolute meters with sea level = 0.
 */

import type { WaterPolygonGeoJson } from "./terrainData";

/* ============================================================================
 * Water-surface profile line sampler
 * ========================================================================== */

/**
 * Sampler finding the nearest point on a profile line [[x, y, z], ...].
 * Implements the contract: water elevation anywhere in the area equals y at the nearest point
 * on the profile line.
 *
 * Registers segments in world-XZ grid buckets and searches outward in concentric rings from
 * the query bucket to find the nearest segment, avoiding an exhaustive segment scan when
 * generating water meshes with tens or hundreds of thousands of query points.
 */
export class RiverProfileSampler {
  private readonly xs: Float64Array;
  private readonly ys: Float64Array;
  private readonly zs: Float64Array;
  private readonly segCount: number;
  private readonly bucketSize: number;
  private readonly bx0: number;
  private readonly bz0: number;
  private readonly bw: number;
  private readonly bh: number;
  private readonly buckets: Int32Array[];

  constructor(profile: number[][], bucketSize = 64) {
    const n = profile.length;
    this.xs = new Float64Array(n);
    this.ys = new Float64Array(n);
    this.zs = new Float64Array(n);
    let minX = Infinity;
    let maxX = -Infinity;
    let minZ = Infinity;
    let maxZ = -Infinity;
    for (let i = 0; i < n; i++) {
      const p = profile[i];
      this.xs[i] = p[0];
      this.ys[i] = p[1];
      this.zs[i] = p[2];
      if (p[0] < minX) minX = p[0];
      if (p[0] > maxX) maxX = p[0];
      if (p[2] < minZ) minZ = p[2];
      if (p[2] > maxZ) maxZ = p[2];
    }
    this.segCount = Math.max(0, n - 1);
    this.bucketSize = Math.max(1, bucketSize);
    if (n === 0) {
      minX = maxX = minZ = maxZ = 0;
    }
    this.bx0 = minX;
    this.bz0 = minZ;
    this.bw = Math.max(1, Math.floor((maxX - minX) / this.bucketSize) + 1);
    this.bh = Math.max(1, Math.floor((maxZ - minZ) / this.bucketSize) + 1);
    const lists: number[][] = new Array(this.bw * this.bh);
    for (let s = 0; s < this.segCount; s++) {
      const cMin = Math.floor((Math.min(this.xs[s], this.xs[s + 1]) - this.bx0) / this.bucketSize);
      const cMax = Math.floor((Math.max(this.xs[s], this.xs[s + 1]) - this.bx0) / this.bucketSize);
      const rMin = Math.floor((Math.min(this.zs[s], this.zs[s + 1]) - this.bz0) / this.bucketSize);
      const rMax = Math.floor((Math.max(this.zs[s], this.zs[s + 1]) - this.bz0) / this.bucketSize);
      for (let r = rMin; r <= rMax; r++) {
        for (let c = cMin; c <= cMax; c++) {
          const bi = r * this.bw + c;
          (lists[bi] ??= []).push(s);
        }
      }
    }
    // lists is sparse (unregistered buckets are holes), so fill every element by length rather than using map.
    const empty = new Int32Array(0);
    this.buckets = Array.from({ length: this.bw * this.bh }, (_, i) => (lists[i] ? Int32Array.from(lists[i]) : empty));
  }

  /** True if there are fewer than two profile points (no segments). */
  get isEmpty(): boolean {
    return this.segCount === 0;
  }

  /**
   * Returns the segment nearest (x, z), its closest-point parameter, and the distance.
   * seg is the segment's starting index; t is the projection parameter clamped to 0..1.
   */
  nearest(x: number, z: number): { seg: number; t: number; dist: number } {
    if (this.segCount === 0) return { seg: 0, t: 0, dist: Infinity };
    const cq = Math.floor((x - this.bx0) / this.bucketSize);
    const rq = Math.floor((z - this.bz0) / this.bucketSize);
    let best = Infinity;
    let bestSeg = 0;
    let bestT = 0;
    const maxRing = Math.max(this.bw, this.bh) + Math.abs(cq) + Math.abs(rq) + 2;
    for (let ring = 0; ring <= maxRing; ring++) {
      // Segments in buckets outside this ring are at least (ring-1)*bucketSize away.
      // If the nearest point found is closer, expanding further cannot improve the result.
      if (best <= (ring - 1) * this.bucketSize) break;
      const rMin = rq - ring;
      const rMax = rq + ring;
      const cMin = cq - ring;
      const cMax = cq + ring;
      for (let r = rMin; r <= rMax; r++) {
        if (r < 0 || r >= this.bh) continue;
        const edgeRow = r === rMin || r === rMax;
        for (let c = cMin; c <= cMax; c++) {
          if (c < 0 || c >= this.bw) continue;
          if (!edgeRow && c !== cMin && c !== cMax) continue;
          const list = this.buckets[r * this.bw + c];
          for (let k = 0; k < list.length; k++) {
            const s = list[k];
            const ax = this.xs[s];
            const az = this.zs[s];
            const dx = this.xs[s + 1] - ax;
            const dz = this.zs[s + 1] - az;
            const len2 = dx * dx + dz * dz;
            let t = len2 > 1e-12 ? ((x - ax) * dx + (z - az) * dz) / len2 : 0;
            if (t < 0) t = 0;
            else if (t > 1) t = 1;
            const px = ax + dx * t - x;
            const pz = az + dz * t - z;
            const d = Math.hypot(px, pz);
            if (d < best) {
              best = d;
              bestSeg = s;
              bestT = t;
            }
          }
        }
      }
    }
    return { seg: bestSeg, t: bestT, dist: best };
  }

  /** Linearly interpolates water elevation from a nearest() result (segment index and parameter). */
  heightOn(seg: number, t: number): number {
    if (this.segCount === 0) return this.ys.length > 0 ? this.ys[0] : 0;
    return this.ys[seg] + (this.ys[seg + 1] - this.ys[seg]) * t;
  }

  /** Water elevation at (x, z), linearly interpolating y at the closest point on the nearest segment. */
  heightAt(x: number, z: number): number {
    if (this.segCount === 0) return this.ys.length > 0 ? this.ys[0] : 0;
    const { seg, t } = this.nearest(x, z);
    return this.heightOn(seg, t);
  }

  /** Downstream direction at (x, z) (nearest segment's unit tangent in XZ). +X if there are no segments. */
  flowDirAt(x: number, z: number): [number, number] {
    if (this.segCount === 0) return [1, 0];
    const { seg } = this.nearest(x, z);
    const dx = this.xs[seg + 1] - this.xs[seg];
    const dz = this.zs[seg + 1] - this.zs[seg];
    const len = Math.hypot(dx, dz);
    return len > 1e-9 ? [dx / len, dz / len] : [1, 0];
  }
}

/* ============================================================================
 * Polygon traversal (GeoJSON-like)
 * ========================================================================== */

/** Normalizes Polygon / MultiPolygon into an array of ring arrays ([exterior, holes...]). */
export function polygonParts(polygon: WaterPolygonGeoJson | null | undefined): number[][][][] {
  if (!polygon || !Array.isArray(polygon.coordinates)) return [];
  if (polygon.type === "Polygon") return [polygon.coordinates as number[][][]];
  if (polygon.type === "MultiPolygon") return polygon.coordinates as number[][][][];
  return [];
}

/** Lists all polygon rings flat, without distinguishing exterior rings and holes, for even-odd tests. */
export function polygonRings(polygon: WaterPolygonGeoJson | null | undefined): number[][][] {
  const rings: number[][][] = [];
  for (const part of polygonParts(polygon)) {
    for (const ring of part) {
      if (Array.isArray(ring) && ring.length >= 3) rings.push(ring);
    }
  }
  return rings;
}

/** Polygon's XZ bounding rectangle; null if it has no rings. */
export function polygonBounds(
  polygon: WaterPolygonGeoJson | null | undefined,
): { minX: number; minZ: number; maxX: number; maxZ: number } | null {
  let minX = Infinity;
  let minZ = Infinity;
  let maxX = -Infinity;
  let maxZ = -Infinity;
  for (const ring of polygonRings(polygon)) {
    for (const p of ring) {
      if (p[0] < minX) minX = p[0];
      if (p[0] > maxX) maxX = p[0];
      if (p[1] < minZ) minZ = p[1];
      if (p[1] > maxZ) maxZ = p[1];
    }
  }
  return isFinite(minX) ? { minX, minZ, maxX, maxZ } : null;
}

/** Point-in-ring test using intersection parity. Behavior on the boundary is unspecified. */
export function pointInRing(x: number, z: number, ring: number[][]): boolean {
  let inside = false;
  const n = ring.length;
  for (let i = 0, j = n - 1; i < n; j = i++) {
    const xi = ring[i][0];
    const zi = ring[i][1];
    const xj = ring[j][0];
    const zj = ring[j][1];
    if (zi > z !== zj > z) {
      const xCross = xi + ((z - zi) * (xj - xi)) / (zj - zi);
      if (x < xCross) inside = !inside;
    }
  }
  return inside;
}

/** Point-in-polygon test, including holes and multiple parts, using even-odd parity over all rings. */
export function pointInPolygon(x: number, z: number, polygon: WaterPolygonGeoJson | null | undefined): boolean {
  let inside = false;
  for (const ring of polygonRings(polygon)) {
    if (pointInRing(x, z, ring)) inside = !inside;
  }
  return inside;
}

/**
 * Rasterizes a polygon into a grid-point mask (scanline + even-odd rule).
 * Grid point (i, j) has world coordinates (originX + i*step, originZ + j*step).
 * Returns a w*h Uint8Array (row-major, 1 = inside). Parity handles holes and multiple parts automatically.
 * Collects intersections with every edge per row, giving rows × edges complexity, practical even
 * for thousands of edges and rows.
 */
export function rasterizePolygonMask(
  polygon: WaterPolygonGeoJson | null | undefined,
  originX: number,
  originZ: number,
  step: number,
  w: number,
  h: number,
): Uint8Array {
  const mask = new Uint8Array(w * h);
  const rings = polygonRings(polygon);
  if (rings.length === 0 || w <= 0 || h <= 0) return mask;

  // Flatten edges as (x1,z1,x2,z2), retaining zmin/zmax for early filtering by row range.
  const edges: number[] = [];
  for (const ring of rings) {
    const n = ring.length;
    for (let i = 0, j = n - 1; i < n; j = i++) {
      const x1 = ring[j][0];
      const z1 = ring[j][1];
      const x2 = ring[i][0];
      const z2 = ring[i][1];
      if (z1 === z2) continue; // Horizontal edges do not contribute intersections.
      edges.push(x1, z1, x2, z2);
    }
  }
  const edgeCount = edges.length / 4;
  const crossings: number[] = [];
  for (let j = 0; j < h; j++) {
    const z = originZ + j * step;
    crossings.length = 0;
    for (let e = 0; e < edgeCount; e++) {
      const x1 = edges[e * 4];
      const z1 = edges[e * 4 + 1];
      const x2 = edges[e * 4 + 2];
      const z2 = edges[e * 4 + 3];
      // Use half-open intervals to avoid counting twice when a scanline passes exactly through a vertex.
      if (z1 > z === z2 > z) continue;
      crossings.push(x1 + ((z - z1) * (x2 - x1)) / (z2 - z1));
    }
    if (crossings.length < 2) continue;
    crossings.sort((a, b) => a - b);
    const rowBase = j * w;
    for (let k = 0; k + 1 < crossings.length; k += 2) {
      const iStart = Math.max(0, Math.ceil((crossings[k] - originX) / step));
      const iEnd = Math.min(w - 1, Math.floor((crossings[k + 1] - originX) / step));
      for (let i = iStart; i <= iEnd; i++) mask[rowBase + i] = 1;
    }
  }
  return mask;
}

/* ============================================================================
 * Water surface mesh (grid surface)
 * ========================================================================== */

export type RiverSurfaceMesh = {
  /** Flat array of vertex coordinates (x, y, z). */
  positions: Float32Array;
  /** Flat array of downstream directions (dx, dz) per vertex, for flow in the water shader. */
  flow: Float32Array;
  /** Triangle indices, wound for +Y normals. */
  indices: Uint32Array;
  /** Grid origin, spacing, and dimensions (grid point (i, j) → (originX + i*step, originZ + j*step)). */
  grid: { originX: number; originZ: number; step: number; w: number; h: number };
  /** Grid-point mask (rasterizePolygonMask result), reusable for cell tests. */
  cornerMask: Uint8Array;
};

/**
 * Builds a grid-surface mesh covering a river's area polygon.
 *
 * Avoids triangulating only the polygon's boundary vertices: rivers are long and meandering,
 * so such triangles extend far downstream, and linearly interpolated water elevation between
 * vertices diverges from the actual descending water surface. A grid gives each vertex the
 * local profile elevation, reproducing the gradient correctly.
 *
 * Draws cells covering the polygon plus one neighboring cell. Terrain and water grids have
 * different spacing/diagonals, so clipping exactly at the bank would expose edges on curves.
 * Bury the margin in the bank without expanding the saved area.
 *
 * Vertex y is the local profile elevation + lift. lift prevents Z-fighting with the riverbed.
 */
export function buildRiverSurfaceMesh(
  polygon: WaterPolygonGeoJson,
  sampler: RiverProfileSampler,
  step: number,
  lift = 0,
): RiverSurfaceMesh | null {
  const bounds = polygonBounds(polygon);
  if (!bounds || step <= 0) return null;
  const originX = Math.floor(bounds.minX / step) * step - 2 * step;
  const originZ = Math.floor(bounds.minZ / step) * step - 2 * step;
  const w = Math.ceil((bounds.maxX - originX) / step) + 3;
  const h = Math.ceil((bounds.maxZ - originZ) / step) + 3;
  const cornerMask = rasterizePolygonMask(polygon, originX, originZ, step, w, h);

  const vertexIndex = new Int32Array(w * h).fill(-1);
  const positions: number[] = [];
  const flow: number[] = [];
  const indices: number[] = [];
  const getVertex = (i: number, j: number): number => {
    const gi = j * w + i;
    let vi = vertexIndex[gi];
    if (vi >= 0) return vi;
    const x = originX + i * step;
    const z = originZ + j * step;
    vi = positions.length / 3;
    vertexIndex[gi] = vi;
    positions.push(x, sampler.heightAt(x, z) + lift, z);
    const [fx, fz] = sampler.flowDirAt(x, z);
    flow.push(fx, fz);
    return vi;
  };

  for (let j = 0; j < h - 1; j++) {
    for (let i = 0; i < w - 1; i++) {
      let included = false;
      for (let y = Math.max(0, j - 1); y <= Math.min(h - 1, j + 2) && !included; y++) {
        for (let x = Math.max(0, i - 1); x <= Math.min(w - 1, i + 2); x++) {
          if (cornerMask[y * w + x]) { included = true; break; }
        }
      }
      if (!included) continue;
      const a = getVertex(i, j);
      const b = getVertex(i + 1, j);
      const c = getVertex(i, j + 1);
      const d = getVertex(i + 1, j + 1);
      // Winding for +Y normals (clockwise from above in a right-handed system with x right and z away).
      indices.push(a, c, b, b, c, d);
    }
  }
  if (indices.length === 0) return null;
  return {
    positions: Float32Array.from(positions),
    flow: Float32Array.from(flow),
    indices: Uint32Array.from(indices),
    grid: { originX, originZ, step, w, h },
    cornerMask,
  };
}

/* ============================================================================
 * Area polygon extraction from a scalar field (Marching Squares → closed rings → hole classification)
 * ========================================================================== */

/**
 * Extracts the field > 0 region from a grid scalar field (w*h, row-major) as polygons with holes.
 *
 * Like contour rendering, uses Marching Squares to obtain level-0 contour segments, but joins
 * them into closed rings using keys identifying crossed grid edges. Edge keys avoid floating-point
 * equality checks and ensure rings close. The caller must pad the field boundary with negative
 * values, since areas cut off at array edges cannot close (see buildRiverPolygon).
 *
 * Classifies exterior rings and holes by containment depth: even depth = exterior, odd = hole.
 * A hole's parent is the smallest exterior ring containing it.
 *
 * Returned ring coordinates are world coordinates; returns null if no area exists.
 * If simplifyTolerance > 0, simplifies each ring with Douglas-Peucker (meters).
 */
export function extractRegionPolygon(
  field: Float32Array,
  w: number,
  h: number,
  originX: number,
  originZ: number,
  step: number,
  simplifyTolerance = 0,
): WaterPolygonGeoJson | null {
  // Grid-edge keys: horizontal H(i,j) = (j*w+i)*2 (point (i,j)–(i+1,j)); vertical V(i,j) = (j*w+i)*2+1 (point (i,j)–(i,j+1)).
  const keyH = (i: number, j: number) => (j * w + i) * 2;
  const keyV = (i: number, j: number) => (j * w + i) * 2 + 1;
  // Intersection coordinates for each key (cached on first calculation).
  const crossX = new Map<number, number>();
  const crossZ = new Map<number, number>();
  const crossing = (key: number, i0: number, j0: number, i1: number, j1: number) => {
    if (crossX.has(key)) return;
    const f0 = field[j0 * w + i0];
    const f1 = field[j1 * w + i1];
    const denom = f0 - f1;
    const t = Math.abs(denom) < 1e-12 ? 0.5 : Math.max(0, Math.min(1, f0 / denom));
    crossX.set(key, originX + (i0 + (i1 - i0) * t) * step);
    crossZ.set(key, originZ + (j0 + (j1 - j0) * t) * step);
  };
  // Adjacency: key → connected keys (at most two).
  const adj = new Map<number, number[]>();
  const link = (a: number, b: number) => {
    (adj.get(a) ?? (adj.set(a, []), adj.get(a)!)).push(b);
    (adj.get(b) ?? (adj.set(b, []), adj.get(b)!)).push(a);
  };

  for (let j = 0; j < h - 1; j++) {
    for (let i = 0; i < w - 1; i++) {
      const a = field[j * w + i] > 0 ? 1 : 0; // Top left.
      const b = field[j * w + i + 1] > 0 ? 2 : 0; // Top right.
      const c = field[(j + 1) * w + i + 1] > 0 ? 4 : 0; // Bottom right.
      const d = field[(j + 1) * w + i] > 0 ? 8 : 0; // Bottom left.
      const caseIdx = a | b | c | d;
      if (caseIdx === 0 || caseIdx === 15) continue;
      const top = keyH(i, j);
      const bottom = keyH(i, j + 1);
      const left = keyV(i, j);
      const right = keyV(i + 1, j);
      const useTop = () => crossing(top, i, j, i + 1, j);
      const useBottom = () => crossing(bottom, i, j + 1, i + 1, j + 1);
      const useLeft = () => crossing(left, i, j, i, j + 1);
      const useRight = () => crossing(right, i + 1, j, i + 1, j + 1);
      switch (caseIdx) {
        case 1: case 14: useLeft(); useTop(); link(left, top); break;
        case 2: case 13: useTop(); useRight(); link(top, right); break;
        case 3: case 12: useLeft(); useRight(); link(left, right); break;
        case 4: case 11: useRight(); useBottom(); link(right, bottom); break;
        case 6: case 9: useTop(); useBottom(); link(top, bottom); break;
        case 7: case 8: useLeft(); useBottom(); link(left, bottom); break;
        case 5: case 10: {
          // Saddle point: use the cell-center average to decide whether diagonally opposite interior regions connect.
          useTop(); useBottom(); useLeft(); useRight();
          const center =
            (field[j * w + i] + field[j * w + i + 1] + field[(j + 1) * w + i + 1] + field[(j + 1) * w + i]) / 4;
          const connected = center > 0;
          if ((caseIdx === 5) === connected) {
            link(top, right); link(left, bottom);
          } else {
            link(top, left); link(right, bottom);
          }
          break;
        }
        default: break;
      }
    }
  }
  if (adj.size === 0) return null;

  // Traverse the adjacency graph (every vertex has degree 2) to form closed rings.
  const visited = new Set<number>();
  const rings: number[][][] = [];
  for (const start of adj.keys()) {
    if (visited.has(start)) continue;
    const ring: number[][] = [];
    let prev = -1;
    let cur = start;
    while (!visited.has(cur)) {
      visited.add(cur);
      ring.push([crossX.get(cur)!, crossZ.get(cur)!]);
      const nb = adj.get(cur)!;
      const next = nb[0] !== prev ? nb[0] : nb[1];
      if (next === undefined) break;
      prev = cur;
      cur = next;
    }
    if (ring.length >= 3) rings.push(simplifyTolerance > 0 ? simplifyRing(ring, simplifyTolerance) : ring);
  }
  if (rings.length === 0) return null;

  // Classify exterior rings/holes by containment depth. Sort by descending area and count how many
  // larger rings contain a representative point of each ring.
  const withArea = rings
    .map((ring) => ({ ring, area: Math.abs(ringSignedArea(ring)) }))
    .filter((r) => r.area > 1e-6)
    .sort((p, q) => q.area - p.area);
  type Part = { exterior: number[][]; holes: number[][][] };
  const parts: Part[] = [];
  const partOfRing: (Part | null)[] = [];
  for (let k = 0; k < withArea.length; k++) {
    const { ring } = withArea[k];
    const [px, pz] = ring[0];
    let depth = 0;
    let parent: Part | null = null;
    // Check only larger rings (a ring cannot be contained by a smaller one).
    for (let m = k - 1; m >= 0; m--) {
      if (pointInRing(px, pz, withArea[m].ring)) {
        depth++;
        // Use the exterior ring belonging to the first (= smallest) containing ring as the parent candidate.
        if (parent === null) parent = partOfRing[m];
      }
    }
    if (depth % 2 === 0) {
      const part: Part = { exterior: closeRing(ring), holes: [] };
      parts.push(part);
      partOfRing.push(part);
    } else {
      if (parent) parent.holes.push(closeRing(ring));
      partOfRing.push(parent);
    }
  }
  if (parts.length === 0) return null;
  if (parts.length === 1) {
    return { type: "Polygon", coordinates: [parts[0].exterior, ...parts[0].holes] };
  }
  return { type: "MultiPolygon", coordinates: parts.map((p) => [p.exterior, ...p.holes]) };
}

/** Ring's signed area in the XZ plane, using the plain shoelace formula rather than a positive-clockwise x→z convention. */
export function ringSignedArea(ring: number[][]): number {
  let area = 0;
  const n = ring.length;
  for (let i = 0, j = n - 1; i < n; j = i++) {
    area += ring[j][0] * ring[i][1] - ring[i][0] * ring[j][1];
  }
  return area / 2;
}

/** Closes a ring in GeoJSON style by matching its first and last points (leaves already closed rings unchanged). */
function closeRing(ring: number[][]): number[][] {
  const first = ring[0];
  const last = ring[ring.length - 1];
  if (first[0] === last[0] && first[1] === last[1]) return ring;
  return [...ring, [first[0], first[1]]];
}

/**
 * Simplifies a closed ring with Douglas-Peucker. To retain closure, splits it into two open
 * polylines at the vertex farthest from the starting point, simplifies each, and rejoins them.
 * Guarantees at least three vertices remain.
 */
export function simplifyRing(ring: number[][], tolerance: number): number[][] {
  if (ring.length <= 4 || tolerance <= 0) return ring;
  let farIdx = 1;
  let farD = -1;
  for (let i = 1; i < ring.length; i++) {
    const d = (ring[i][0] - ring[0][0]) ** 2 + (ring[i][1] - ring[0][1]) ** 2;
    if (d > farD) {
      farD = d;
      farIdx = i;
    }
  }
  const first = douglasPeucker(ring.slice(0, farIdx + 1), tolerance);
  const second = douglasPeucker([...ring.slice(farIdx), ring[0]], tolerance);
  const out = first.concat(second.slice(1, -1));
  return out.length >= 3 ? out : ring;
}

function douglasPeucker(points: number[][], tolerance: number): number[][] {
  if (points.length <= 2) return points;
  const keep = new Uint8Array(points.length);
  keep[0] = 1;
  keep[points.length - 1] = 1;
  const stack: [number, number][] = [[0, points.length - 1]];
  while (stack.length > 0) {
    const [s, e] = stack.pop()!;
    if (e - s < 2) continue;
    const ax = points[s][0];
    const az = points[s][1];
    const bx = points[e][0];
    const bz = points[e][1];
    const dx = bx - ax;
    const dz = bz - az;
    const len2 = dx * dx + dz * dz;
    let maxD = -1;
    let maxI = -1;
    for (let i = s + 1; i < e; i++) {
      const px = points[i][0] - ax;
      const pz = points[i][1] - az;
      let d: number;
      if (len2 < 1e-12) {
        d = Math.hypot(px, pz);
      } else {
        const t = Math.max(0, Math.min(1, (px * dx + pz * dz) / len2));
        d = Math.hypot(px - dx * t, pz - dz * t);
      }
      if (d > maxD) {
        maxD = d;
        maxI = i;
      }
    }
    if (maxD > tolerance && maxI > 0) {
      keep[maxI] = 1;
      stack.push([s, maxI], [maxI, e]);
    }
  }
  const out: number[][] = [];
  for (let i = 0; i < points.length; i++) if (keep[i]) out.push(points[i]);
  return out;
}

/** Polygon area (m², subtracting holes). */
export function polygonArea(polygon: WaterPolygonGeoJson | null | undefined): number {
  let area = 0;
  for (const part of polygonParts(polygon)) {
    for (let k = 0; k < part.length; k++) {
      const a = Math.abs(ringSignedArea(part[k]));
      area += k === 0 ? a : -a;
    }
  }
  return Math.max(0, area);
}
