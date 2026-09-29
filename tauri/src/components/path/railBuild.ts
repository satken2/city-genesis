/**
 * 軌道(線路・モノレール・人員搬送)の複線敷設ロジック
 *
 * パス建設パネルで軌道のバリアントを選ぶと、プリセットの複合パス(combined_paths)を選ぶ代わりに、
 * プレイヤーが「線路数・各線の進行方向・線路間隔」を直接指定して複線を構成する。
 * 線路間隔の既定はバリアントのマスター幅(width)(=コリドーが隙間なく隣接)で、
 * パネルの広げる/縮めるボタンで一定範囲内を調整できる。
 * 本モジュールはその設定から、1本の中心線を平行な単線パス群へ展開するための
 * コンポーネント定義(CombinedPathComponent 相当)を生成する純粋関数群を提供する。
 *
 * 人員搬送(PATH_CONVEYOR。ロープウェイ等)は軌道として扱うが、描画・敷設とも
 * 道路と同じ扱い(両方向のロープが張られたメッシュを単一パス1本で配置)のため、複線展開の対象外。
 * 経済上は路線ではなく「乗降区間からしか乗れない速い歩道」であり、路線・停留所は作らない。
 *
 * 線路系(線路・モノレール)でマスターの lanes が2以上のバリアント(複線一体の線路。鉄道・モノレールの
 * 1本で敷く複線やLRT専用軌道)も複線展開しない。1本のパスが上下線(複線)を持ち、Unity が lanes 本の
 * 軌道を描き、バックエンドは通行方向を両方向(DIR_BOTH)に固定して一方通行を拒否するため、単線パスを
 * 並べる方式とは両立しない。どの軌道を走るかは進む向きで決まる。判定は isSingleRailPathVariant に集約している。
 * 複線一体の線路の駅は両外側にホームを持ち(相対式)、ホーム設置側を選ばない(hasPlatformSideChoice)。
 *
 * 展開後の敷設・プレビュー・コスト見積りは既存の複合パス機構(pathOffset / confirmPath)を
 * そのまま再利用する。ここで生成する components は combined_paths のコンポーネントと同型。
 */

import type { MasterData, PathMasterEntry, CombinedPathComponent } from "../../types";
import {
  PATH_TRACK,
  PATH_MONORAIL,
  PATH_CONVEYOR,
  TRACK_NORMAL,
  TRACK_BRIDGE,
  TRACK_STATION,
  DIR_FORWARD,
  DIR_BACKWARD,
  DIR_BOTH,
  TRAFFIC_HAND_LEFT,
  PRODUCT_HIGH_ELECTRICITY,
} from "../../constants";

/** 軌道として敷設する path_type(鉄道・モノレール・人員搬送) */
export const RAIL_TYPES: readonly number[] = [PATH_TRACK, PATH_MONORAIL, PATH_CONVEYOR];

/**
 * マスターの lanes を軌道数として読む path_type(線路系)。
 * 道路の lanes は車線数なので読まない。人員搬送は lanes の値によらず常に単一パスで敷くので対象外。
 */
const TRACK_COUNT_PATH_TYPES: readonly number[] = [PATH_TRACK, PATH_MONORAIL];

/** バリアント一覧における構造(track_type)の並び順(通常→橋梁→駅) */
const TRACK_TYPE_ORDER: readonly number[] = [TRACK_NORMAL, TRACK_BRIDGE, TRACK_STATION];

/** 線路数の下限・上限(パネルのステッパー) */
export const MIN_TRACK_COUNT = 1;
export const MAX_TRACK_COUNT = 10;

/**
 * 線路間隔のフォールバック値(m)。
 * 間隔の既定はバリアントのマスター幅(width)(=各線のコリドーが隙間なく隣接する)。
 * マスターに width が無い異常時のみこの値を使う。
 */
export const FALLBACK_TRACK_SPACING = 4;

/** 線路間隔の広げる/縮めるボタン1回あたりの増減量(m) */
export const TRACK_SPACING_STEP = 0.5;

/**
 * 線路間隔の調整可能範囲(既定間隔=マスター幅に対する倍率)。
 * 下限を幅の半分まで許すのは、コリドーを重ねて敷いても近接線路の結合描画
 * (道床フィラー)が働き、実物の複線らしい狭い間隔で見せられるため。
 */
export const TRACK_SPACING_MIN_FACTOR = 0.5;
export const TRACK_SPACING_MAX_FACTOR = 3;

/**
 * 既定間隔 base(m) と調整量 delta(m) から実効の線路間隔を返す。
 * base の [TRACK_SPACING_MIN_FACTOR, TRACK_SPACING_MAX_FACTOR] 倍の範囲へクランプする。
 */
export function clampTrackSpacing(base: number, delta: number): number {
  return Math.min(
    base * TRACK_SPACING_MAX_FACTOR,
    Math.max(base * TRACK_SPACING_MIN_FACTOR, base + delta)
  );
}

/** path_type が軌道カテゴリ(鉄道・モノレール・人員搬送)かを返す。 */
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
 * 1本のパスが持つ軌道の本数を返す。線路系は max(1, lanes)、それ以外は1。
 * バックエンドの MasterData.get_track_count と同じ定義。
 */
export function variantTrackCount(entry: PathMasterEntry | null | undefined): number {
  if (!entry || !TRACK_COUNT_PATH_TYPES.includes(entry.type)) return 1;
  return Math.max(1, entry.lanes ?? 1);
}

/**
 * 1本のパスで上下線(複線)を表す線路バリアント(複線一体の線路)かを返す。
 * バックエンドの MasterData.is_multi_track_variant と同じ定義で、バックエンドはこのバリアントの
 * 通行方向を DIR_BOTH に固定する。
 */
export function isMultiTrackRailVariant(entry: PathMasterEntry | null | undefined): boolean {
  return variantTrackCount(entry) >= 2;
}

/**
 * 建設時にホーム設置側(外側/内側/両側)を選ぶ駅バリアントかを返す(platform_width を持つ単線の駅)。
 * 複線一体の線路の駅は両外側にホームを持つので設置側を選ばず、platform_side も送らない。
 */
export function hasPlatformSideChoice(entry: PathMasterEntry | null | undefined): boolean {
  const width = entry?.platform_width;
  return typeof width === "number" && width > 0 && !isMultiTrackRailVariant(entry);
}

/**
 * 複線展開せず、両方向(DIR_BOTH)の単一パス1本で敷く軌道バリアントかを返す
 * (人員搬送、または複線一体の線路)。線路数・進行方向・線路間隔の設定を持たない。
 */
export function isSingleRailPathVariant(entry: PathMasterEntry | null | undefined): boolean {
  return entry?.type === PATH_CONVEYOR || isMultiTrackRailVariant(entry);
}

/**
 * 軌道の種別(鉄道/モノレール/人員搬送)のバリアント(複線一体の線路を含む)を、パス建設パネルの
 * カード一覧の並び順で返す。構造(通常/橋梁/駅)・電化/非電化・線路規格(軌間・設計速度など)の違いは
 * すべてバリアントの違いとして並べ、プレイヤーに直接選ばせる。
 * 地上/高架/地下はバリアントではなくノードの高さで決まるので、一覧には現れない。
 * 並び順は 構造(通常→橋梁→駅) → 複線一体の線路は後ろ → 非電化→電化 → ID昇順。
 * 同じ構造の中では、線路数などを設定して並べる単線を先に、1本で上下線を持つ複線一体の線路を後ろに置く。
 */
export function railVariantsInOrder(
  masterData: MasterData | null,
  railType: number,
): { id: number; data: PathMasterEntry }[] {
  if (!masterData?.paths) return [];
  const matches: { id: number; data: PathMasterEntry }[] = [];
  for (const key in masterData.paths) {
    const data = masterData.paths[key];
    if (data.type !== railType) continue;
    matches.push({ id: parseInt(key), data });
  }
  matches.sort((a, b) => {
    const ta = TRACK_TYPE_ORDER.indexOf(variantTrackType(a.data));
    const tb = TRACK_TYPE_ORDER.indexOf(variantTrackType(b.data));
    if (ta !== tb) return ta - tb;
    const ma = isMultiTrackRailVariant(a.data) ? 1 : 0;
    const mb = isMultiTrackRailVariant(b.data) ? 1 : 0;
    if (ma !== mb) return ma - mb;
    const ea = isElectrifiedVariant(a.data) ? 1 : 0;
    const eb = isElectrifiedVariant(b.data) ? 1 : 0;
    if (ea !== eb) return ea - eb;
    return a.id - b.id;
  });
  return matches;
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
