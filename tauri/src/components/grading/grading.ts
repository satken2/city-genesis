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
 * 高さの指定(0)の基準。frontage=接する道路の路面の高さ、terrain_mean=範囲の現在の平均の高さ(接道の無い整地)、
 * sea_top=埋立で接道が無いときの既定の高さ(埋立は高さを指定できない)。
 */
export type GradingBaseKind = "frontage" | "terrain_mean" | "sea_top";

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
  /** 目標の高さ H(海面基準の絶対標高 m)。整地は base_height + height_offset */
  target_height: number | null;
  /** 基準の高さ(絶対標高 m)。範囲を見積もれないときと GET /grading/{agent_id} では null */
  base_height: number | null;
  base_kind: GradingBaseKind | null;
  /** 適用された高さの指定(基準との差 m)。埋立は常に 0。GET /grading/{agent_id} では null */
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

/** 発注(POST /api/v1/grading)の応答 */
export type GradingCreateResponse = GradingEstimate & {
  agent_id: number;
  parcel_id: number;
};

/**
 * 高さの指定欄の下に出す「0 の意味」。基準の高さを小数1桁で添える。
 * 基準が接道でも範囲の平均でもない(埋立の既定の高さ・見積もれない)ときは null。
 */
export function gradingBaseNote(t: TFunction, estimate: GradingEstimate | null): string | null {
  if (estimate?.base_height == null) return null;
  if (estimate.base_kind !== "frontage" && estimate.base_kind !== "terrain_mean") return null;
  return t(`grading.baseNote.${estimate.base_kind}`, { value: estimate.base_height.toFixed(1) });
}

/**
 * ConstructionInfoCard の先頭に並べる造成固有の行(種類・基準の高さ・目標の高さ・切土・盛土、埋立なら岸壁の長さと面積)。
 * 基準の高さは見積もりが返したときだけ出す(詳細ウィンドウは目標の高さだけ)。陸地整地の岸壁は常に0なので行ごと出さない。
 * estimate が null なら "—"、loading 中は読み込み中の表示にする。
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
