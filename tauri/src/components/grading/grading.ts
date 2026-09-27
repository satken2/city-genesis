/**
 * 造成(整地・埋立)の共通定義。造成ウィンドウ・詳細ウィンドウ・建物建設ウィンドウの一覧が共用する。
 *
 * 造成工事の主体はバリアントで判別する(TYPE_OPEN_SPACE の広場と type が同じため type では判別できない)。
 * 見積もりの算出はバックエンド(POST /grading/preview・GET /grading/{agent_id})に一元化されており、
 * UI 側では計算しない。
 */

import type { TFunction } from "i18next";
import { GRADING_VARIANTS } from "../../constants";
import type { ConstructionInfoRow } from "../common/ConstructionInfoCard";

export function isGradingVariant(variant: number | null | undefined): boolean {
  return variant != null && GRADING_VARIANTS.includes(variant);
}

export type GradingKind = "land" | "shore";

/**
 * 造成の見積もり。POST /api/v1/grading/preview・POST /api/v1/grading・GET /api/v1/grading/{agent_id} が同じキーで返す。
 * 資金の5項目(affordable〜unlimited)は POST /buildings/construction-cost と同じ形。
 */
export type GradingEstimate = {
  valid: boolean;
  /** 発注できない理由コード(バックエンドの判定順)。i18n の grading.reasons.<code> で表示する */
  reasons: string[];
  kind: GradingKind | null;
  variant: number | null;
  parcel_type: number | null;
  /** 目標の高さ H(海面基準の絶対標高 m) */
  target_height: number | null;
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

/** 発注(POST /api/v1/grading)の応答 */
export type GradingCreateResponse = GradingEstimate & {
  agent_id: number;
  parcel_id: number;
};

/**
 * ConstructionInfoCard の先頭に並べる造成固有の行(種類・目標の高さ・切土・盛土、埋立なら岸壁の長さと面積)。
 * 陸地整地の岸壁は常に0なので行ごと出さない。estimate が null なら "—"、loading 中は読み込み中の表示にする。
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
