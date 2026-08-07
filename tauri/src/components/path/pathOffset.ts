/**
 * 複合パス(combined_paths)の展開に使う平行オフセット幾何ユーティリティ
 *
 * 複合パスは、プレイヤーが引いた1本の中心線を各コンポーネントの
 * lateral_offset(横方向)・height_offset(高さ方向)でオフセットした
 * 平行パス群に展開して敷設される。本モジュールはその展開計算を担い、
 * 確定送信(usePathConstruction.confirmPath)から使うことで、コスト見積りと
 * 実際の敷設位置を一致させる。
 *
 * 座標系: ワールドX-Z平面(Y=高さ)。横方向オフセットは敷設方向(始点→終点)に
 * 向かって右が正(master.yaml combined_paths の lateral_offset の規約と同じ)。
 */

export type Vec2 = { x: number; z: number };

/** セグメント(a→b)の右向き単位法線を返す。ゼロ長セグメントはゼロベクトル */
function segmentRightNormal(ax: number, az: number, bx: number, bz: number): Vec2 {
  const dx = bx - ax;
  const dz = bz - az;
  const len = Math.hypot(dx, dz);
  if (len < 1e-9) return { x: 0, z: 0 };
  // 上から見下ろす(x=東, z=北)とき、進行方向(dx,dz)の右手側は(dz,-dx)
  return { x: dz / len, z: -dx / len };
}

/**
 * アンカー列の各点における「右向きオフセット方向ベクトル」を返す。
 *
 * 端点は隣接セグメントの法線そのもの、中間点は前後セグメント法線のマイター平均
 * (角の外側で間隔が保たれるよう 1/cos(θ/2) でスケール)。急角度でオフセット点が
 * 飛びすぎないようスケールは2倍までにクランプする。
 * 返り値は lateral_offset(m)を掛けてそのまま加算できる方向ベクトル。
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
      // 前後セグメントがちょうど折り返し(180°)の場合。片側の法線で代用する。
      dirs.push(n1);
      continue;
    }
    mx /= mlen;
    mz /= mlen;
    // マイター補正: 角の内外で平行間隔を保つためのスケール(急角度は2倍まで)
    const cosHalf = mx * n1.x + mz * n1.z;
    const scale = 1 / Math.max(0.5, cosHalf);
    dirs.push({ x: mx * scale, z: mz * scale });
  }
  return dirs;
}

/**
 * [x, y, z] 形式の3D点列を、横方向 lateral(m)・高さ方向 heightOffset(m) だけ
 * オフセットした新しい点列を返す。元の配列は変更しない。
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
 * {x, z} 形式の2D点列を横方向 lateral(m) だけオフセットした新しい点列を返す。
 * プレビュー描画(BuildPoint列)用。
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
 * 制御点(絶対座標)を、対応するアンカーのオフセット方向でオフセットする。
 * 制御点はアンカー近傍にあるため、アンカーと同じ方向ベクトルで十分な近似になる。
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
