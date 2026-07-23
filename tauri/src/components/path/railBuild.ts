/**
 * 軌道(線路・モノレール・ロープウェイ)の複線敷設ロジック
 *
 * 建設パネルの軌道カテゴリでは、プリセットの複合パス(combined_paths)を選ぶ代わりに、
 * プレイヤーが「線路数・各線の進行方向」を直接指定して複線を構成する。
 * 線路間隔はバリアントのマスター幅(width)から自動で決まる(コリドーが隙間なく隣接)。
 * 本モジュールはその設定から、1本の中心線を平行な単線パス群へ展開するための
 * コンポーネント定義(CombinedPathComponent 相当)を生成する純粋関数群を提供する。
 *
 * ロープウェイ(PATH_ROPEWAY)は軌道カテゴリに属するが、描画・敷設とも道路と同じ扱い
 * (両方向のロープが張られたメッシュを単一パス1本で配置)のため、複線展開の対象外。
 *
 * 展開後の敷設・プレビュー・コスト見積りは既存の複合パス機構(pathOffset / confirmPath)を
 * そのまま再利用する。ここで生成する components は combined_paths のコンポーネントと同型。
 */

import type { MasterData, PathMasterEntry, CombinedPathComponent } from "../../types";
import {
  PATH_TRACK,
  PATH_MONORAIL,
  PATH_ROPEWAY,
  TRACK_NORMAL,
  TRACK_BRIDGE,
  TRACK_STATION,
  DIR_FORWARD,
  DIR_BACKWARD,
  DIR_BOTH,
  TRAFFIC_HAND_LEFT,
  PRODUCT_HIGH_ELECTRICITY,
} from "../../constants";

/** 軌道カテゴリに属する path_type(鉄道・モノレール・ロープウェイ) */
export const RAIL_TYPES: readonly number[] = [PATH_TRACK, PATH_MONORAIL, PATH_ROPEWAY];

/** track_type ボタンの並び順(通常→橋梁→駅) */
export const TRACK_TYPE_ORDER: readonly number[] = [TRACK_NORMAL, TRACK_BRIDGE, TRACK_STATION];

/** 線路数の下限・上限(パネルのステッパー) */
export const MIN_TRACK_COUNT = 1;
export const MAX_TRACK_COUNT = 10;

/**
 * 線路間隔のフォールバック値(m)。
 * 間隔は通常バリアントのマスター幅(width)をそのまま使う(=各線のコリドーが隙間なく隣接する)。
 * マスターに width が無い異常時のみこの値を使う。
 */
export const FALLBACK_TRACK_SPACING = 4;

/** path_type が軌道カテゴリ(鉄道・モノレール・ロープウェイ)かを返す。 */
export function isRailType(type: number | null | undefined): boolean {
  return type != null && RAIL_TYPES.includes(type);
}

/** バリアントの track_type(未定義は TRACK_NORMAL)を返す。 */
export function variantTrackType(entry: PathMasterEntry | null | undefined): number {
  return entry?.track_type ?? TRACK_NORMAL;
}

/** バリアントが電化(infra_products に高圧電力を含む)かを返す。 */
export function isElectrifiedVariant(entry: PathMasterEntry | null | undefined): boolean {
  return !!entry?.infra_products?.includes(PRODUCT_HIGH_ELECTRICITY);
}

/**
 * (軌道カテゴリ, track_type, 電化) に一致する単線バリアントを全て返す。
 * 同一の種別・構造・電化でも、線路規格の違い(地上/高架/地下、軌間・設計速度など)で
 * 複数のバリアントが存在しうるため、パネルのプルダウンで最終バリアントをプレイヤーに選ばせる。
 * 並び順は、高さで地上/高架/地下を表現できる height_editable=true を優先し、次点でID昇順。
 */
export function railVariantsForCombo(
  masterData: MasterData | null,
  railType: number,
  trackType: number,
  electrified: boolean
): { id: number; data: PathMasterEntry }[] {
  if (!masterData?.paths) return [];
  const matches: { id: number; data: PathMasterEntry }[] = [];
  for (const key in masterData.paths) {
    const data = masterData.paths[key];
    if (data.type !== railType) continue;
    if (variantTrackType(data) !== trackType) continue;
    if (isElectrifiedVariant(data) !== electrified) continue;
    matches.push({ id: parseInt(key), data });
  }
  matches.sort((a, b) => {
    const ha = a.data.height_editable ? 0 : 1;
    const hb = b.data.height_editable ? 0 : 1;
    if (ha !== hb) return ha - hb;
    return a.id - b.id;
  });
  return matches;
}

/**
 * (軌道カテゴリ, track_type, 電化) から敷設する単線バリアントの既定値を1つ解決する。
 * railVariantsForCombo の並び順の先頭(＝高さ編集可能を優先、次点でID昇順)を採る。
 * 一致が無ければ null。プルダウンで別のバリアントを選ぶ前の初期選択に使う。
 */
export function resolveRailVariant(
  masterData: MasterData | null,
  railType: number,
  trackType: number,
  electrified: boolean
): { id: number; data: PathMasterEntry } | null {
  const matches = railVariantsForCombo(masterData, railType, trackType, electrified);
  return matches.length > 0 ? matches[0] : null;
}

/** ある軌道カテゴリで選択可能な track_type の一覧(バリアントが存在するもののみ、規定順)。 */
export function availableTrackTypes(masterData: MasterData | null, railType: number): number[] {
  if (!masterData?.paths) return [];
  const present = new Set<number>();
  for (const key in masterData.paths) {
    const data = masterData.paths[key];
    if (data.type === railType) present.add(variantTrackType(data));
  }
  return TRACK_TYPE_ORDER.filter((tt) => present.has(tt));
}

/**
 * ある(軌道カテゴリ, track_type)で電化トグルを操作できるか。
 * 電化・非電化の両方のバリアントが存在するときだけトグルが意味を持つ。
 * どちらか一方しか無い場合は、その存在する側に固定される(hasElectrified で判定)。
 */
export function electrificationOptions(
  masterData: MasterData | null,
  railType: number,
  trackType: number
): { hasElectrified: boolean; hasNonElectrified: boolean } {
  let hasElectrified = false;
  let hasNonElectrified = false;
  if (masterData?.paths) {
    for (const key in masterData.paths) {
      const data = masterData.paths[key];
      if (data.type !== railType) continue;
      if (variantTrackType(data) !== trackType) continue;
      if (isElectrifiedVariant(data)) hasElectrified = true;
      else hasNonElectrified = true;
    }
  }
  return { hasElectrified, hasNonElectrified };
}

/**
 * 線路数 count・線路間隔 spacing(m) から、中心線を基準とした各線の横方向オフセット(m)を返す。
 * 中心線の左右に均等配置する。index 0 が最も左(オフセット最小)、末尾が最も右。
 * 例: count=2, spacing=4 → [-2, 2]。count=3, spacing=4 → [-4, 0, 4]。
 */
export function computeLateralOffsets(count: number, spacing: number): number[] {
  const mid = (count - 1) / 2;
  return Array.from({ length: count }, (_, i) => (i - mid) * spacing);
}

/**
 * 線路数 count と都市の通行方向 trafficHand から、各線の既定の進行方向を返す。
 * index 0 = 最も左(オフセット最小)、末尾 = 最も右。
 *
 * - 偶数本: 半分が順方向(DIR_FORWARD)・残り半分が逆方向(DIR_BACKWARD)。
 *   左側通行(TRAFFIC_HAND_LEFT)では左半分が順方向、右側通行では右半分が順方向。
 * - 奇数本: 中央の1本は両方向(DIR_BOTH)。残りを左右で半分ずつ順/逆に割り当てる。
 *
 * 「順方向」は幾何の始点→終点(DIR_FORWARD)。combined_paths のプリセット
 * (複線=左順/右逆, 複々線=左2順/右2逆)と整合する。
 */
export function defaultTravelDirections(count: number, trafficHand: number): number[] {
  const mid = (count - 1) / 2;
  const leftIsForward = trafficHand === TRAFFIC_HAND_LEFT;
  const dirs: number[] = [];
  for (let i = 0; i < count; i++) {
    if (count % 2 === 1 && i === mid) {
      dirs.push(DIR_BOTH);
      continue;
    }
    const isLeft = i < mid;
    const forward = isLeft ? leftIsForward : !leftIsForward;
    dirs.push(forward ? DIR_FORWARD : DIR_BACKWARD);
  }
  return dirs;
}

/** 進行方向トグルの循環順: 順方向 → 逆方向 → 両方向 → 順方向 … */
export function cycleDirection(dir: number): number {
  if (dir === DIR_FORWARD) return DIR_BACKWARD;
  if (dir === DIR_BACKWARD) return DIR_BOTH;
  return DIR_FORWARD;
}

/**
 * 軌道の敷設設定から、中心線を展開するためのコンポーネント定義群を生成する。
 * 生成物は combined_paths のコンポーネントと同型で、confirmPath / コスト見積り /
 * プレビューの複合パス展開ロジックがそのまま利用できる。
 */
export function buildRailComponents(
  variantId: number,
  count: number,
  spacing: number,
  directions: number[]
): CombinedPathComponent[] {
  const offsets = computeLateralOffsets(count, spacing);
  return offsets.map((offset, i) => ({
    path_variant: variantId,
    lateral_offset: offset,
    height_offset: 0,
    travel_direction: directions[i] ?? DIR_BOTH,
  }));
}
