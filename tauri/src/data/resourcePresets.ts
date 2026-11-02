/**
 * シナリオ制作の資源量・回復率の入力目安。資源種別ではなく採取する財で引き、シミュレーションは参照しない。
 * 数量は原油バレル・鉱物kg・水m³で解釈する。採取施設の製品量へ換算するときはマスターの投入係数も必要になる。
 * 非再生資源は回復せず、河川はエポックごとの流量、湖沼・地下水は貯留と流入・涵養で表す。
 */

import { PRODUCT_HIGH_WATER, PRODUCT_WATER_SUPPLY, RESOURCE_OIL, RESOURCE_ORE, RESOURCE_WATER, RESOURCE_NONMETALLIC } from "../constants";
import type { MasterData } from "../types";

export type ResourcePreset = {
  /** Identifier for i18n key resourcePreset.presets.<key>. */
  key: string;
  /** "area" multiplies amount per 1m² by shape area. "fixed" uses amount directly as the maximum. */
  basis: "area" | "fixed";
  /** 資源財マスターの便宜的な kg 表記ではなく、採取量の意味に合う表示単位を使う。 */
  unit: "barrels" | "kg" | "m3";
  amount: number;
  /** Daily recovery fraction of maximum reserves. */
  recoveryRate: number;
};

/**
 * Reference epoch length in days, matching the initial simulation_days for new cities. Distribution precedes
 * recovery, and each epoch can extract only starting reserves (up to the maximum). Represent flow-based water
 * sources as maximum = epoch-length flow and recovery rate = 1/epoch length.
 */
export const RESOURCE_PRESET_EPOCH_DAYS = 30;

/** Displayed maximum reserves for resources without presets, or area-based presets with no shape yet. */
export const RESOURCE_FALLBACK_MAX_AMOUNT = 1000;

/** Maximum allowed reserve input. Large enough for extensive deposits (200t/m² × tens of km²). */
export const RESOURCE_MAX_AMOUNT_LIMIT = 1e13;

const RESOURCE_PRESETS: Readonly<Record<number, readonly ResourcePreset[]>> = {
  [RESOURCE_OIL]: [
    { key: "oilField", basis: "area", unit: "barrels", amount: 5, recoveryRate: 0 }, // Recoverable reserves: 5 million barrels/km² (midrange conventional oil field).
  ],
  [RESOURCE_ORE]: [
    { key: "oreDeposit", basis: "area", unit: "kg", amount: 200_000, recoveryRate: 0 }, // 200 million t/km² (75m extraction depth × density 2.7).
  ],
  [RESOURCE_NONMETALLIC]: [
    { key: "nonmetallicDeposit", basis: "area", unit: "kg", amount: 200_000, recoveryRate: 0 }, // 岩石の採掘深と密度は金属鉱床と同じ基準で扱う。
  ],
  [RESOURCE_WATER]: [
    { key: "river", basis: "fixed", unit: "m3", amount: 100_000 * RESOURCE_PRESET_EPOCH_DAYS, recoveryRate: 0.0334 }, // Available withdrawal: 100,000m³/day. Recovery slightly exceeds 1/30 to guarantee full replenishment in 30 days.
    { key: "lake", basis: "area", unit: "m3", amount: 5, recoveryRate: 0.01 }, // Effective storage depth 5m; inflow 1% per day.
    { key: "groundwater", basis: "area", unit: "m3", amount: 5, recoveryRate: 0.0002 }, // Effective storage 5m³/m² (specific yield 0.1 × thickness 50m); recharge 1mm/day.
  ],
};

/** Presets for the supplied product. The first is the default when selecting a type. */
export function resourcePresetsFor(productId: number | null | undefined): readonly ResourcePreset[] {
  if (productId == null) return [];
  return RESOURCE_PRESETS[productId] ?? [];
}

/** Calculate maximum reserves from a preset and shape area (m²). Return null for area-based presets without a shape. */
export function presetMaxAmount(preset: ResourcePreset, areaM2: number): number | null {
  if (preset.basis === "fixed") return preset.amount;
  if (!(areaM2 > 0)) return null;
  return Math.max(1, Math.round(preset.amount * areaM2));
}

/**
 * Calculate sustainable daily extraction from maximum reserves and recovery rate. Even with rapid recovery,
 * extraction per epoch is capped at maximum reserves, so the daily rate cannot exceed maximum / epoch length.
 */
export function sustainableDailyYield(maxAmount: number, recoveryRate: number): number {
  return Math.min(maxAmount * recoveryRate, maxAmount / RESOURCE_PRESET_EPOCH_DAYS);
}

/**
 * 水源を生活用水だけに使った場合の人数換算。マスターの取水・配水の投入係数と1人1日の基準最大需要を使う。
 * 複数の方式がある場合は原水を多く使う係数で控えめに見積もる。産業需要や施設能力を含む供給予測ではない。
 */
export function waterPopulationEstimate(maxAmount: number, recoveryRate: number, masterData: MasterData) {
  const dailyDemand = masterData.products[PRODUCT_WATER_SUPPLY]?.demand_max;
  const inputPerOutput = (supply: number, material: number) => Math.max(0, ...Object.values(masterData.agents)
    .filter((agent) => agent.supply === supply)
    .map((agent) => agent.materials?.[material] ?? 0)
    .filter((amount) => Number.isFinite(amount) && amount > 0));
  const rawPerHighWater = inputPerOutput(PRODUCT_HIGH_WATER, RESOURCE_WATER);
  const highWaterPerSupply = inputPerOutput(PRODUCT_WATER_SUPPLY, PRODUCT_HIGH_WATER);
  const rawPerPerson = dailyDemand * highWaterPerSupply * rawPerHighWater;
  if (!(rawPerPerson > 0) || !Number.isFinite(rawPerPerson)) return null;

  const population = sustainableDailyYield(maxAmount, recoveryRate) / rawPerPerson;
  return Number.isFinite(population) && population >= 0 ? { population, dailyDemand } : null;
}
