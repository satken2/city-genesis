/**
 * Suggested presets for natural resource area maximum reserves and recovery rates. The scenario authoring
 * resource panel uses them as initial inputs (the simulation does not read them). Recovery depends on resource
 * type: zero for nonrenewables such as oil fields/mineral deposits, enough to refill each epoch for flow-based
 * sources such as rivers, and inflow/recharge rates for stored sources such as lakes/groundwater.
 *
 * Keyed by supplied product (resource_master product_id), not resource type, because reserve density and
 * recovery depend on what is extracted. Amounts use output units (oil: crude oil barrels, ore: kg, water: m³),
 * matching current master data where facilities produce one output unit per resource unit.
 */

import { RESOURCE_OIL, RESOURCE_ORE, RESOURCE_WATER } from "../constants";

export type ResourcePreset = {
  /** Identifier for i18n key resourcePreset.presets.<key>. */
  key: string;
  /** "area" multiplies amount per 1m² by shape area. "fixed" uses amount directly as the maximum. */
  basis: "area" | "fixed";
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
    { key: "oilField", basis: "area", amount: 5, recoveryRate: 0 }, // Recoverable reserves: 5 million barrels/km² (midrange conventional oil field).
  ],
  [RESOURCE_ORE]: [
    { key: "oreDeposit", basis: "area", amount: 200_000, recoveryRate: 0 }, // 200 million t/km² (75m extraction depth × density 2.7).
  ],
  [RESOURCE_WATER]: [
    { key: "river", basis: "fixed", amount: 100_000 * RESOURCE_PRESET_EPOCH_DAYS, recoveryRate: 0.0334 }, // Available withdrawal: 100,000m³/day. Recovery slightly exceeds 1/30 to guarantee full replenishment in 30 days.
    { key: "lake", basis: "area", amount: 5, recoveryRate: 0.01 }, // Effective storage depth 5m; inflow 1% per day.
    { key: "groundwater", basis: "area", amount: 5, recoveryRate: 0.0002 }, // Effective storage 5m³/m² (specific yield 0.1 × thickness 50m); recharge 1mm/day.
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
