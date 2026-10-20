/**
 * Parallel offset geometry utilities for expanding composite paths (combined_paths).
 *
 * A composite path expands the player's centerline into parallel paths offset by each component's lateral_offset
 * and height_offset. This module performs that calculation for confirmation (usePathConstruction.confirmPath)
 * to keep cost estimates and actual placement positions consistent.
 *
 * Coordinates: world X-Z plane (Y = height). Positive lateral offset is to the right of the placement direction
 * (start → end), matching master.yaml combined_paths lateral_offset.
 */

export type Vec2 = { x: number; z: number };

/** Return the right-facing unit normal of segment a → b. Zero-length segments return the zero vector. */
function segmentRightNormal(ax: number, az: number, bx: number, bz: number): Vec2 {
  const dx = bx - ax;
  const dz = bz - az;
  const len = Math.hypot(dx, dz);
  if (len < 1e-9) return { x: 0, z: 0 };
  // Viewed from above (x = east, z = north), the right of travel direction (dx,dz) is (dz,-dx).
  return { x: dz / len, z: -dx / len };
}

/**
 * Return each anchor's rightward offset direction vector.
 *
 * Endpoints use the adjacent segment's normal; intermediate points use the miter average of neighboring normals,
 * scaled by 1/cos(θ/2) to preserve spacing around outside corners. Clamp scale to 2 so sharp angles do not send
 * offset points too far away. Multiply these vectors by lateral_offset (m) and add directly.
 */
export function computeAnchorOffsetDirs(points: { x: number; z: number }[]): Vec2[] {
  const n = points.length;
  if (n === 0) return [];
  if (n === 1) return [{ x: 0, z: 0 }];

  const segNormals: Vec2[] = [];
  for (let i = 0; i < n - 1; i++) {
    segNormals.push(
      segmentRightNormal(points[i].x, points[i].z, points[i + 1].x, points[i + 1].z)
    );
  }

  const dirs: Vec2[] = [];
  for (let i = 0; i < n; i++) {
    if (i === 0) {
      dirs.push(segNormals[0]);
      continue;
    }
    if (i === n - 1) {
      dirs.push(segNormals[n - 2]);
      continue;
    }
    const n1 = segNormals[i - 1];
    const n2 = segNormals[i];
    let mx = n1.x + n2.x;
    let mz = n1.z + n2.z;
    const mlen = Math.hypot(mx, mz);
    if (mlen < 1e-9) {
      // Adjacent segments reverse exactly (180°). Fall back to one side's normal.
      dirs.push(n1);
      continue;
    }
    mx /= mlen;
    mz /= mlen;
    // Miter correction: scale to maintain parallel spacing inside/outside corners (capped at 2 for sharp angles).
    const cosHalf = mx * n1.x + mz * n1.z;
    const scale = 1 / Math.max(0.5, cosHalf);
    dirs.push({ x: mx * scale, z: mz * scale });
  }
  return dirs;
}

/**
 * Return a new [x, y, z] point sequence offset laterally by lateral (m) and vertically by heightOffset (m).
 * Leave the original array unchanged.
 */
export function offsetPolyline3D(
  points: number[][],
  lateral: number,
  heightOffset: number
): number[][] {
  const pts2d = points.map((p) => ({ x: p[0], z: p[2] }));
  const dirs = computeAnchorOffsetDirs(pts2d);
  return points.map((p, i) => [
    p[0] + dirs[i].x * lateral,
    (p[1] ?? 0) + heightOffset,
    p[2] + dirs[i].z * lateral,
  ]);
}

/**
 * Return a new {x, z} point sequence offset by lateral (m).
 * Used for preview drawing (BuildPoint sequences).
 */
export function offsetPolyline2D<T extends { x: number; z: number }>(
  points: T[],
  lateral: number
): T[] {
  const dirs = computeAnchorOffsetDirs(points);
  return points.map((p, i) => ({
    ...p,
    x: p.x + dirs[i].x * lateral,
    z: p.z + dirs[i].z * lateral,
  }));
}

/**
 * Offset absolute control points using their owning anchors' offset directions.
 * Controls are near their anchors, so using the same direction vectors is a sufficient approximation.
 */
export function offsetControlPoint(
  cp: { x: number; z: number } | null,
  anchorDir: Vec2,
  lateral: number
): { x: number; z: number } | null {
  if (!cp) return null;
  return {
    x: cp.x + anchorDir.x * lateral,
    z: cp.z + anchorDir.z * lateral,
  };
}
