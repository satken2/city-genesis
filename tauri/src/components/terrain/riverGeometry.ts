/**
 * 川の「範囲ポリゴン + 水面プロファイル線」モデル(RiverPayload)を扱う幾何ユーティリティ。
 *
 * React やキャンバス描画には依存しない純粋関数のみを置く。地形エディタ(範囲ポリゴンの
 * 抽出・2D表示)と3Dプレビュー(水面メッシュ)が共用し、同じ契約を実装するバックエンド
 * (geo_rivers.py)・Unity(RiverStore / RiverSurfaceManager)の参照実装を兼ねる。
 *
 * 座標系はすべてワールドXZ(マップ中心が(0,0))、標高は海面=0の絶対フレーム(m)。
 */

import type { WaterPolygonGeoJson } from "./terrainData";

/* ============================================================================
 * 水面プロファイル線のサンプラー
 * ========================================================================== */

/**
 * プロファイル線 [[x, y, z], ...] 上の最近点を引くサンプラー。
 * 「範囲内の任意地点の水面標高 = プロファイル線上の最近点の y」という契約の実装。
 *
 * 線分をワールドXZの格子バケットに登録し、問い合わせ点のバケットから同心リング状に
 * 探索範囲を広げて最近線分を求める(問い合わせ点数が数万〜数十万になる水面メッシュ生成で、
 * 全線分の総当たりを避けるため)。
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
    // lists は疎配列(登録の無いバケットは hole)なので、map ではなく長さ指定で全要素を埋める
    const empty = new Int32Array(0);
    this.buckets = Array.from({ length: this.bw * this.bh }, (_, i) => (lists[i] ? Int32Array.from(lists[i]) : empty));
  }

  /** プロファイル点が2点未満(線分が無い)なら true */
  get isEmpty(): boolean {
    return this.segCount === 0;
  }

  /**
   * (x, z) に最も近い線分と、その上の最近点パラメータ・距離を返す。
   * 戻り値の seg は線分の始点インデックス、t は 0..1 のクランプ済み射影パラメータ。
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
      // このリングより外のバケットにある線分は、少なくとも (ring-1)*bucketSize 離れている。
      // 既に見つけた最近点がそれより近ければ、これ以上広げても更新されない。
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

  /** nearest() の結果(線分インデックスとパラメータ)から水面標高を線形補間する */
  heightOn(seg: number, t: number): number {
    if (this.segCount === 0) return this.ys.length > 0 ? this.ys[0] : 0;
    return this.ys[seg] + (this.ys[seg + 1] - this.ys[seg]) * t;
  }

  /** (x, z) の水面標高(最近線分上の最近点で y を線形補間) */
  heightAt(x: number, z: number): number {
    if (this.segCount === 0) return this.ys.length > 0 ? this.ys[0] : 0;
    const { seg, t } = this.nearest(x, z);
    return this.heightOn(seg, t);
  }

  /** (x, z) の流下方向(最近線分の単位接線、XZ)。線分が無ければ +X */
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
 * ポリゴン(GeoJSONライク)の走査
 * ========================================================================== */

/** Polygon / MultiPolygon を「リング配列の配列」([外周, 穴...] の並び)に正規化する */
export function polygonParts(polygon: WaterPolygonGeoJson | null | undefined): number[][][][] {
  if (!polygon || !Array.isArray(polygon.coordinates)) return [];
  if (polygon.type === "Polygon") return [polygon.coordinates as number[][][]];
  if (polygon.type === "MultiPolygon") return polygon.coordinates as number[][][][];
  return [];
}

/** ポリゴンの全リングを平坦に列挙する(外周・穴を区別しない。even-odd 判定用) */
export function polygonRings(polygon: WaterPolygonGeoJson | null | undefined): number[][][] {
  const rings: number[][][] = [];
  for (const part of polygonParts(polygon)) {
    for (const ring of part) {
      if (Array.isArray(ring) && ring.length >= 3) rings.push(ring);
    }
  }
  return rings;
}

/** ポリゴンのXZ外接矩形。リングが無ければ null */
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

/** 1リングに対する点の内外(交差数の偶奇)。境界上の扱いは不定 */
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

/** ポリゴン(穴・複数パート込み)に対する点の内外。全リングの even-odd で判定する */
export function pointInPolygon(x: number, z: number, polygon: WaterPolygonGeoJson | null | undefined): boolean {
  let inside = false;
  for (const ring of polygonRings(polygon)) {
    if (pointInRing(x, z, ring)) inside = !inside;
  }
  return inside;
}

/**
 * ポリゴンを格子点マスクへラスタライズする(スキャンライン + even-odd)。
 * 格子点 (i, j) のワールド座標は (originX + i*step, originZ + j*step)。
 * 戻り値は w*h の Uint8Array(行優先、1=内側)。穴・複数パートは偶奇規則で自動的に扱われる。
 * 行ごとに全エッジの交点を集めるため計算量は 行数×エッジ数 で、数千エッジ×数千行でも実用的。
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

  // エッジを (x1,z1,x2,z2) の平坦配列にまとめ、行範囲で事前に絞れるよう zmin/zmax も持つ
  const edges: number[] = [];
  for (const ring of rings) {
    const n = ring.length;
    for (let i = 0, j = n - 1; i < n; j = i++) {
      const x1 = ring[j][0];
      const z1 = ring[j][1];
      const x2 = ring[i][0];
      const z2 = ring[i][1];
      if (z1 === z2) continue; // 水平エッジは交差数に寄与しない
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
      // 半開区間で判定し、頂点をちょうど通る走査線が二重に数えられないようにする
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
 * 水面メッシュ(格子面)
 * ========================================================================== */

export type RiverSurfaceMesh = {
  /** 頂点座標 (x, y, z) の平坦配列 */
  positions: Float32Array;
  /** 各頂点の流下方向 (dx, dz) の平坦配列。水面シェーダーの流れ表現用 */
  flow: Float32Array;
  /** 三角形インデックス(法線が +Y になる巻き順) */
  indices: Uint32Array;
  /** 格子の原点・間隔・寸法(格子点 (i, j) → (originX + i*step, originZ + j*step)) */
  grid: { originX: number; originZ: number; step: number; w: number; h: number };
  /** 格子点マスク(rasterizePolygonMask の結果)。セル判定の再利用用 */
  cornerMask: Uint8Array;
};

/**
 * 川の範囲ポリゴンを覆う格子面メッシュを作る。
 *
 * ポリゴンを頂点ごとに三角形分割する方式にしない理由: 川は細長く蛇行するため、
 * 外周頂点だけを結ぶ三角形は流下方向に長く伸び、頂点間で線形補間される水面標高が
 * 実際の(流下方向に下がる)水面から乖離する。格子面なら各頂点がその地点のプロファイル
 * 標高を持ち、勾配が正しく再現される。
 *
 * セルは「4隅のいずれかがポリゴン内側」なら採用するため、水面は岸線から最大1セル分だけ
 * 外側へはみ出す。はみ出した部分は岸(水面より高い地形)に埋まって見えないので問題ない。
 * 逆にポリゴン内側のセルは必ず覆われる(水面に穴が開かない)。
 *
 * 頂点の y は「その地点のプロファイル標高 + lift」。lift は川床との Z-fighting 回避用。
 */
export function buildRiverSurfaceMesh(
  polygon: WaterPolygonGeoJson,
  sampler: RiverProfileSampler,
  step: number,
  lift = 0,
): RiverSurfaceMesh | null {
  const bounds = polygonBounds(polygon);
  if (!bounds || step <= 0) return null;
  // 外側に1セルの余白を持たせ、境界セルも4隅を持てるようにする
  const originX = Math.floor(bounds.minX / step) * step - step;
  const originZ = Math.floor(bounds.minZ / step) * step - step;
  const w = Math.ceil((bounds.maxX - originX) / step) + 2;
  const h = Math.ceil((bounds.maxZ - originZ) / step) + 2;
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
      const tl = cornerMask[j * w + i];
      const tr = cornerMask[j * w + i + 1];
      const bl = cornerMask[(j + 1) * w + i];
      const br = cornerMask[(j + 1) * w + i + 1];
      if (!(tl | tr | bl | br)) continue;
      const a = getVertex(i, j);
      const b = getVertex(i + 1, j);
      const c = getVertex(i, j + 1);
      const d = getVertex(i + 1, j + 1);
      // +Y 法線になる巻き順(x→右, z→奥の右手系で、上から見て時計回り)
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
 * スカラー場からの領域ポリゴン抽出(Marching Squares → 閉リング → 穴の分類)
 * ========================================================================== */

/**
 * 格子スカラー場 field(w*h、行優先)の「field > 0」領域を、穴付きポリゴンとして抽出する。
 *
 * Marching Squares で等値線(レベル0)の線分を得る点は等高線描画と同じだが、ここでは
 * 線分を「どの格子辺を横切ったか」のキーで繋いで閉リングにする。格子辺キーで繋ぐため
 * 浮動小数の一致判定が不要で、リングは必ず閉じる。場の外周は呼び出し側が負値で
 * パディングしておくこと(領域が配列の端で切れると閉じないため。buildRiverPolygon 参照)。
 *
 * リングの外周/穴の分類は「他のリングに何重に含まれているか」で決める(偶数重=外周、
 * 奇数重=穴。穴の親は自分を含む最小の外周)。
 *
 * 戻り値のリング座標はワールド座標。領域が無ければ null。
 * simplifyTolerance > 0 なら各リングを Douglas-Peucker で間引く(m)。
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
  // 格子辺キー: 水平辺 H(i,j) = (j*w+i)*2 (点(i,j)-(i+1,j))、垂直辺 V(i,j) = (j*w+i)*2+1 (点(i,j)-(i,j+1))
  const keyH = (i: number, j: number) => (j * w + i) * 2;
  const keyV = (i: number, j: number) => (j * w + i) * 2 + 1;
  // 各キーの交点座標(初回計算時にキャッシュ)
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
  // 隣接: キー → 繋がる相手キー(最大2)
  const adj = new Map<number, number[]>();
  const link = (a: number, b: number) => {
    (adj.get(a) ?? (adj.set(a, []), adj.get(a)!)).push(b);
    (adj.get(b) ?? (adj.set(b, []), adj.get(b)!)).push(a);
  };

  for (let j = 0; j < h - 1; j++) {
    for (let i = 0; i < w - 1; i++) {
      const a = field[j * w + i] > 0 ? 1 : 0; // 左上
      const b = field[j * w + i + 1] > 0 ? 2 : 0; // 右上
      const c = field[(j + 1) * w + i + 1] > 0 ? 4 : 0; // 右下
      const d = field[(j + 1) * w + i] > 0 ? 8 : 0; // 左下
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
          // 鞍点: セル中央の平均値で「対角の内側同士が繋がっているか」を決める
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

  // 隣接グラフ(全頂点が次数2)をたどって閉リングにする
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

  // 外周/穴の分類(包含の重なり数)。面積降順に並べ、各リングの代表点が
  // より大きいリングに何個含まれるかを数える。
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
    // 自分より大きいリングだけを見る(小さいリングに含まれることはない)
    for (let m = k - 1; m >= 0; m--) {
      if (pointInRing(px, pz, withArea[m].ring)) {
        depth++;
        // 最初に見つかった(=最小の)包含リングの属する外周を親候補にする
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

/** リングの符号付き面積(XZ平面。正=x→z の右回り基準ではなく単純な shoelace) */
export function ringSignedArea(ring: number[][]): number {
  let area = 0;
  const n = ring.length;
  for (let i = 0, j = n - 1; i < n; j = i++) {
    area += ring[j][0] * ring[i][1] - ring[i][0] * ring[j][1];
  }
  return area / 2;
}

/** GeoJSON 流儀で始点と終点を一致させて閉じる(既に閉じていればそのまま) */
function closeRing(ring: number[][]): number[][] {
  const first = ring[0];
  const last = ring[ring.length - 1];
  if (first[0] === last[0] && first[1] === last[1]) return ring;
  return [...ring, [first[0], first[1]]];
}

/**
 * 閉リングを Douglas-Peucker で間引く。閉じた形状のまま扱えるよう、始点から最も遠い頂点で
 * 2本の開折れ線に分割してそれぞれ間引き、繋ぎ直す。頂点数が3未満にならないよう保証する。
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

/** ポリゴンの面積(m²。穴を差し引く) */
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

/** ポリゴンの全頂点を (dx, dz) だけ平行移動した新しいポリゴンを返す(マップリサイズ追従用) */
export function translatePolygon(polygon: WaterPolygonGeoJson, dx: number, dz: number): WaterPolygonGeoJson {
  const shiftRing = (ring: number[][]) => ring.map((p) => [p[0] + dx, p[1] + dz]);
  if (polygon.type === "Polygon") {
    return { type: "Polygon", coordinates: (polygon.coordinates as number[][][]).map(shiftRing) };
  }
  return {
    type: "MultiPolygon",
    coordinates: (polygon.coordinates as number[][][][]).map((part) => part.map(shiftRing)),
  };
}
