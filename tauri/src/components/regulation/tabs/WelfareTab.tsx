/**
 * 社会保障タブ
 *
 * 市政府から世帯への現金給付（失業給付・年金・生活扶助）の政策値を設定する。
 * 退職年齢と各給付率は City の政策プロパティで、バックエンドの社会保障予算
 * （カテゴリ12）の必要額算出と給付の両方が参照する。
 *
 * API では給付率を 0〜1 の比率でやり取りするが、UI 上では 0〜100 のパーセント表記で扱う。
 */

import { useCallback } from "react";
import { useTranslation } from "react-i18next";
import type { WelfareRegulations } from "../regulationTypes";

/** 保存ペイロードの構築や差分判定で使うキー一覧 */
export const WELFARE_KEYS = [
  "retirement_age",
  "unemployment_benefit_ratio",
  "pension_benefit_ratio",
  "livelihood_assistance_ratio",
] as const;

export type WelfareKey = (typeof WELFARE_KEYS)[number];

/** 給付率（API 0〜1）とパーセント表記（UI 0〜100）の相互変換 */
const RATIO_KEYS: ReadonlySet<WelfareKey> = new Set([
  "unemployment_benefit_ratio",
  "pension_benefit_ratio",
  "livelihood_assistance_ratio",
]);

export function welfareApiToUi(key: WelfareKey, apiValue: number): number {
  return RATIO_KEYS.has(key) ? Math.round(apiValue * 100) : apiValue;
}

export function welfareUiToApi(key: WelfareKey, uiValue: number): number {
  return RATIO_KEYS.has(key) ? uiValue / 100 : uiValue;
}

/** 行の並び。単位ラベルと入力範囲は項目ごとに異なる */
const ROWS: { key: WelfareKey; labelKey: string; unitKey: string; min: number; max: number; step: number }[] = [
  { key: "retirement_age", labelKey: "regulation.retirementAge", unitKey: "regulation.retirementAgeUnit", min: 0, max: 120, step: 1 },
  { key: "unemployment_benefit_ratio", labelKey: "regulation.unemploymentBenefitRatio", unitKey: "regulation.welfareWageRatioUnit", min: 0, max: 150, step: 5 },
  { key: "pension_benefit_ratio", labelKey: "regulation.pensionBenefitRatio", unitKey: "regulation.welfareWageRatioUnit", min: 0, max: 150, step: 5 },
  { key: "livelihood_assistance_ratio", labelKey: "regulation.livelihoodAssistanceRatio", unitKey: "regulation.welfareShortfallRatioUnit", min: 0, max: 100, step: 5 },
];

export function WelfareTab({
  cityWelfare,
  pendingWelfare,
  onChange,
}: {
  /** API から取得した確定値（API 単位） */
  cityWelfare: WelfareRegulations;
  /** 編集中値（API 単位。キーが無い項目は確定値を表示する） */
  pendingWelfare: Partial<WelfareRegulations>;
  onChange: (key: WelfareKey, apiValue: number) => void;
}) {
  const { t } = useTranslation();

  /** 数値入力の共通ハンドラ。負値や非数値は無視して直前の値を保つ */
  const handleChange = useCallback(
    (key: WelfareKey, rawValue: string) => {
      const parsed = key === "retirement_age" ? parseInt(rawValue, 10) : parseFloat(rawValue);
      if (isNaN(parsed) || parsed < 0) return;
      onChange(key, welfareUiToApi(key, parsed));
    },
    [onChange],
  );

  return (
    <>
      <p className="regulation-pane__description">
        {t("regulation.welfareDescription")}
      </p>

      <div className="regulation-building-list">
        {ROWS.map((row) => {
          const apiValue = pendingWelfare[row.key] ?? cityWelfare[row.key];
          const modified = apiValue !== cityWelfare[row.key];
          return (
            <div key={row.key} className="regulation-building-row">
              <span
                className={[
                  "regulation-building-row__name",
                  modified && "regulation-building-row__name--modified",
                ]
                  .filter(Boolean)
                  .join(" ")}
              >
                {t(row.labelKey)}
              </span>
              <div className="regulation-building-row__input-group">
                <input
                  type="number"
                  min={row.min}
                  max={row.max}
                  step={row.step}
                  className={[
                    "regulation-building-input",
                    modified && "regulation-building-input--modified",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                  value={welfareApiToUi(row.key, apiValue)}
                  onChange={(e) => handleChange(row.key, e.target.value)}
                />
                <span className="regulation-building-unit">{t(row.unitKey)}</span>
              </div>
            </div>
          );
        })}
      </div>
      <p className="regulation-immigration-quota-hint">
        {t("regulation.welfareHint")}
      </p>
    </>
  );
}
