/**
 * Shared earthworks definitions (land grading and land reclamation), used by the earthworks window, detail
 * window, and building construction list.
 *
 * Identify earthworks agents by variant, since they share TYPE_OPEN_SPACE with plazas.
 * Estimates are calculated exclusively in the backend (POST /grading/preview and GET /grading/{agent_id}); the
 * UI does not calculate them.
 */

import type { TFunction } from "i18next";
import { GRADING_VARIANTS } from "../../constants";
import type { ConstructionInfoRow } from "../common/ConstructionInfoCard";

export function isGradingVariant(variant: number | null | undefined): boolean {
  return variant != null && GRADING_VARIANTS.includes(variant);
}

export type GradingKind = "land" | "shore";

/**
 * Reference for a height offset of zero. frontage: adjoining road surface elevation; terrain_mean: current mean
 * elevation of the area (land grading without road frontage);
 * sea_top: default elevation for reclamation without road frontage (reclamation does not accept a height
 * offset).
 */
export type GradingBaseKind = "frontage" | "terrain_mean" | "sea_top";

/**
 * Earthworks estimate. POST /api/v1/grading/preview, POST /api/v1/grading, and GET /api/v1/grading/{agent_id}
 * return the same keys.
 * The five funding fields (affordable through unlimited) match POST /buildings/construction-cost.
 */
export type GradingEstimate = {
  valid: boolean;
  /** Reason codes preventing an order, in backend evaluation order. Display via i18n grading.reasons.<code>. */
  reasons: string[];
  kind: GradingKind | null;
  variant: number | null;
  parcel_type: number | null;
  /** Target height H (absolute elevation in meters relative to sea level). For land grading: base_height + height_offset. */
  target_height: number | null;
  /** Reference height (absolute elevation in meters). null when the area cannot be estimated and for GET /grading/{agent_id}. */
  base_height: number | null;
  base_kind: GradingBaseKind | null;
  /** Applied height offset (meters relative to the reference). Always 0 for reclamation; null for GET /grading/{agent_id}. */
  height_offset: number | null;
  area: number;
  cut_volume: number;
  fill_volume: number;
  quay_length: number;
  quay_area: number;
  work_volume: number;
  estimated_construction_cost: number | null;
  standard_construction_cost: number | null;
  estimated_duration_days: number | null;
  next_epoch_construction_budget: number | null;
  construction_supply_ratio: number | null;
  expected_duration_days: number | null;
  affordable: boolean;
  required_total: number;
  available_funds: number | null;
  funds_shortfall: number;
  unlimited: boolean;
};

/** Order response (POST /api/v1/grading) */
export type GradingCreateResponse = GradingEstimate & {
  agent_id: number;
  parcel_id: number;
};

/**
 * Explain what zero means below the height offset input, including the reference height to one decimal place.
 * Return null unless the reference is road frontage or the area mean (e.g. default reclamation height or
 * unavailable estimate).
 */
export function gradingBaseNote(t: TFunction, estimate: GradingEstimate | null): string | null {
  if (estimate?.base_height == null) return null;
  if (estimate.base_kind !== "frontage" && estimate.base_kind !== "terrain_mean") return null;
  return t(`grading.baseNote.${estimate.base_kind}`, { value: estimate.base_height.toFixed(1) });
}

/**
 * Earthworks-specific rows at the start of ConstructionInfoCard (type, reference and target heights, cut and
 * fill; quay length and area for reclamation).
 * Show the reference height only when returned in an estimate; the detail window shows target height only. Omit
 * the quay row for land grading, where it is always zero.
 * Show an em dash for a null estimate, or a loading indicator while loading.
 */
export function gradingInfoRows(
  t: TFunction,
  estimate: GradingEstimate | null,
  loading: boolean,
): ConstructionInfoRow[] {
  const show = (value: string | null): string =>
    loading ? t("common.loading") : value ?? "—";
  const kind = estimate?.kind ?? null;
  const rows: ConstructionInfoRow[] = [
    { label: t("grading.kindLabel"), value: show(kind ? t(`grading.kind.${kind}`) : null) },
    ...(estimate?.base_height != null
      ? [
          {
            label: t("grading.baseHeight"),
            value: show(t("grading.unit.height", { value: estimate.base_height.toFixed(1) })),
          },
        ]
      : []),
    {
      label: t("grading.targetHeight"),
      value: show(
        estimate?.target_height != null
          ? t("grading.unit.height", { value: estimate.target_height.toFixed(1) })
          : null,
      ),
    },
    {
      label: t("grading.cutVolume"),
      value: show(estimate && kind ? t("grading.unit.volume", { value: Math.round(estimate.cut_volume) }) : null),
    },
    {
      label: t("grading.fillVolume"),
      value: show(estimate && kind ? t("grading.unit.volume", { value: Math.round(estimate.fill_volume) }) : null),
    },
  ];
  if (kind === "shore" && estimate) {
    rows.push(
      {
        label: t("grading.quayLength"),
        value: show(t("grading.unit.length", { value: Math.round(estimate.quay_length) })),
      },
      {
        label: t("grading.quayArea"),
        value: show(t("grading.unit.area", { value: Math.round(estimate.quay_area) })),
      },
    );
  }
  return rows;
}
