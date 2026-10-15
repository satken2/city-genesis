/**
 * 天然資源エリアの埋蔵量上限・回復率の目安(プリセット)。
 *
 * テンプレート作成モードの資源編集パネルが入力の初期値に使う
 * (シミュレーションは参照しない)。回復率は資源の型で決める。油田・鉱床のような非再生資源は0、
 * 河川のように流量で決まる資源は1エポックで満タンに戻る値、湖沼・地下水のような貯留は流入・涵養の速さにする。
 *
 * 資源種別ではなく提供物資(resource_master の product_id)ごとに持つ。埋蔵の密度や回復の速さは
 * 「何を採るか」で決まるため。数量は、採取施設が資源1単位から産出物1単位を作る現行マスターに
 * 合わせて産出物の単位(石油=原油バレル、鉱石=kg、水=m³)で書いている。
 */

import { RESOURCE_OIL, RESOURCE_ORE, RESOURCE_WATER } from "../constants";

export type ResourcePreset = {
  /** i18n キー resourcePreset.presets.<key> の識別子 */
  key: string;
  /** "area" は amount を面積1m²あたりの量として形状の面積に掛ける。"fixed" は amount をそのまま上限にする */
  basis: "area" | "fixed";
  amount: number;
  /** 埋蔵量上限に対する1日あたりの回復割合 */
  recoveryRate: number;
};

/**
 * 目安が前提にするエポック長(日)。新規都市の simulation_days の初期値と同じ。
 * 分配は回復より先に行われ、1エポックに採れるのは開始時点の埋蔵量(上限まで)に限られるため、
 * 流量で決まる水源は「上限=エポック長ぶんの流量、回復率=1/エポック長」で表す。
 */
export const RESOURCE_PRESET_EPOCH_DAYS = 30;

/** プリセットの無い資源や、面積比例のプリセットで形状がまだ無いときの埋蔵量上限の表示値 */
export const RESOURCE_FALLBACK_MAX_AMOUNT = 1000;

/** 埋蔵量上限の入力上限。広い鉱床(200t/m²×数十km²)が収まる桁にしてある */
export const RESOURCE_MAX_AMOUNT_LIMIT = 1e13;

const RESOURCE_PRESETS: Readonly<Record<number, readonly ResourcePreset[]>> = {
  [RESOURCE_OIL]: [
    { key: "oilField", basis: "area", amount: 5, recoveryRate: 0 }, // 可採埋蔵量500万バレル/km²(在来型油田の中位)
  ],
  [RESOURCE_ORE]: [
    { key: "oreDeposit", basis: "area", amount: 200_000, recoveryRate: 0 }, // 2億t/km²(採掘深75m×比重2.7)
  ],
  [RESOURCE_WATER]: [
    { key: "river", basis: "fixed", amount: 100_000 * RESOURCE_PRESET_EPOCH_DAYS, recoveryRate: 0.0334 }, // 取水可能量10万m³/日。回復率は1/30を僅かに上回らせ30日で必ず満タンに戻す
    { key: "lake", basis: "area", amount: 5, recoveryRate: 0.01 }, // 有効貯水深5m、流入で1日1%
    { key: "groundwater", basis: "area", amount: 5, recoveryRate: 0.0002 }, // 有効貯留5m³/m²(比産出率0.1×厚さ50m)、涵養1mm/日
  ],
};

/** 提供物資に対応する目安の一覧。先頭が種別を選んだときの既定。 */
export function resourcePresetsFor(productId: number | null | undefined): readonly ResourcePreset[] {
  if (productId == null) return [];
  return RESOURCE_PRESETS[productId] ?? [];
}

/** 目安と形状の面積(m²)から埋蔵量上限を求める。面積比例の目安で形状がまだ無いときは null。 */
export function presetMaxAmount(preset: ResourcePreset, areaM2: number): number | null {
  if (preset.basis === "fixed") return preset.amount;
  if (!(areaM2 > 0)) return null;
  return Math.max(1, Math.round(preset.amount * areaM2));
}

/**
 * 埋蔵量上限と回復率から、枯渇させずに採り続けられる1日あたりの量を求める。
 * 回復が速くても1エポックに採れるのは上限までなので、上限÷エポック長で頭打ちになる。
 */
export function sustainableDailyYield(maxAmount: number, recoveryRate: number): number {
  return Math.min(maxAmount * recoveryRate, maxAmount / RESOURCE_PRESET_EPOCH_DAYS);
}
