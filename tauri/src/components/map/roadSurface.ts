type Point = [number, number];

/** Accept the same curve commands as Canvas and convert to point sequences for finding road surface edges. */
export class RoadCenterline {
  points: Point[] = [];
  moveTo(x: number, z: number) { this.points.push([x, z]); }
  lineTo(x: number, z: number) {
    const last = this.points[this.points.length - 1];
    if (!last || Math.hypot(x - last[0], z - last[1]) > 1e-6) this.points.push([x, z]);
  }
  bezierCurveTo(ax: number, az: number, bx: number, bz: number, x: number, z: number) {
    const start = this.points[this.points.length - 1];
    const flatten = (p: Point, a: Point, b: Point, end: Point, depth: number) => {
      const length = Math.hypot(end[0] - p[0], end[1] - p[1]);
      const distance = (v: Point) => length > 1e-6
        ? Math.abs((end[0] - p[0]) * (p[1] - v[1]) - (p[0] - v[0]) * (end[1] - p[1])) / length
        : Math.hypot(v[0] - p[0], v[1] - p[1]);
      const controlLength = Math.hypot(a[0] - p[0], a[1] - p[1])
        + Math.hypot(b[0] - a[0], b[1] - a[1]) + Math.hypot(end[0] - b[0], end[1] - b[1]);
      if (depth >= 12 || (Math.max(distance(a), distance(b)) <= 0.15 && controlLength - length <= 0.15)) {
        this.lineTo(...end);
        return;
      }
      const mid = (u: Point, v: Point): Point => [(u[0] + v[0]) / 2, (u[1] + v[1]) / 2];
      const pa = mid(p, a), ab = mid(a, b), be = mid(b, end);
      const left = mid(pa, ab), right = mid(ab, be), center = mid(left, right);
      flatten(p, pa, left, center, depth + 1);
      flatten(center, right, be, end, depth + 1);
    };
    flatten(start, [ax, az], [bx, bz], [x, z], 0);
  }
}

/**
 * Road surface with ends cut perpendicular to travel. At two-path connections, transition from the shared width
 * to actual width using smoothstep. For short roads, cap transition distances at half the length at each end
 * to prevent overlapping width corrections from bulging.
 */
export function roadSurface(points: Point[], width: number, startWidth = width, endWidth = width,
  startNormal?: Point, endNormal?: Point): Point[] {
  if (points.length < 2) return [];
  const distances = [0];
  for (let i = 1; i < points.length; i++) {
    distances.push(distances[i - 1] + Math.hypot(points[i][0] - points[i - 1][0], points[i][1] - points[i - 1][1]));
  }
  const total = distances[distances.length - 1];
  if (total < 1e-6) return [];
  const startLength = Math.min(total / 2, Math.max(width, startWidth) * 1.5);
  const endLength = Math.min(total / 2, Math.max(width, endWidth) * 1.5);
  // Subdivide only width-changing parts even on straight roads, avoiding extra vertices along the entire long straight.
  const samples = new Set(distances);
  for (let i = 1; i <= 16; i++) {
    if (startWidth !== width) samples.add(startLength * i / 16);
    if (endWidth !== width) samples.add(total - endLength * i / 16);
  }
  const dense: { point: Point; distance: number }[] = [];
  let segment = 1;
  for (const d of [...samples].sort((a, b) => a - b)) {
    while (segment < points.length - 1 && distances[segment] < d) segment++;
    const t = (d - distances[segment - 1]) / (distances[segment] - distances[segment - 1]);
    const a = points[segment - 1], b = points[segment];
    dense.push({ point: [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t], distance: d });
  }
  const normal = (a: Point, b: Point): Point => {
    const length = Math.hypot(b[0] - a[0], b[1] - a[1]);
    return [-(b[1] - a[1]) / length, (b[0] - a[0]) / length];
  };
  const blend = (distance: number, length: number) => {
    const t = Math.min(1, distance / length);
    return 1 - t * t * (3 - 2 * t);
  };
  const left: Point[] = [], right: Point[] = [];
  dense.forEach(({ point, distance }, i) => {
    const before = normal(dense[Math.max(0, i - 1)].point, i === 0 ? dense[1].point : point);
    const after = normal(i === dense.length - 1 ? dense[i - 1].point : point, dense[Math.min(dense.length - 1, i + 1)].point);
    const nx = before[0] + after[0], nz = before[1] + after[1];
    const norm = Math.hypot(nx, nz);
    const jointNormal = i === 0 ? startNormal : i === dense.length - 1 ? endNormal : undefined;
    const n: Point = jointNormal ?? (norm > 1e-6 ? [nx / norm, nz / norm] : after);
    const halfWidth = (width + (startWidth - width) * blend(distance, startLength)
      + (endWidth - width) * blend(total - distance, endLength)) / 2;
    const offset = jointNormal ? halfWidth : halfWidth / Math.max(0.5, n[0] * after[0] + n[1] * after[1]);
    left.push([point[0] + n[0] * offset, point[1] + n[1] * offset]);
    right.push([point[0] - n[0] * offset, point[1] - n[1] * offset]);
  });
  return left.concat(right.reverse());
}
