/**
 * 建築規制タブ
 *
 * 都市全体の建築規制（高さ規制・容積率・建ぺい率）を設定する。
 * ここでの設定は「これ以降に作成される地区の既定値」であり、既存の地区・土地区画には
 * 遡及しない（既存地区の変更は地区詳細パネルから行う）。
 *
 * API は比率で保持するが、容積率・建ぺい率は日本の建築規制の慣習どおり
 * パーセント表記で表示・編集する。単位変換はこのファイルが持つ。
 */

import { useCallback } from "react";
import { useTranslation } from "react-i18next";
import type { BuildingRegKey, RegulationsData } from "../regulationTypes";

/** 保存ペイロードの構築や差分判定で使う、建築規制キーの一覧 */
export const BUILDING_REG_KEYS: BuildingRegKey[] = [
  "limit_height",
  "limit_volume_ratio",
  "limit_coverage_ratio",
];

/**
 * 入力欄でパーセント表記に変換する建築規制のキー。
 * API は比率（容積率 30.0 = 3000%、建ぺい率 0.5 = 50%）で保持する。
 * limit_height はメートル単位なので変換対象外。
 */
const PERCENT_REG_KEYS = new Set<BuildingRegKey>([
  "limit_volume_ratio",
  "limit_coverage_ratio",
]);

/**
 * API の値を入力欄の表示単位へ変換する。
 * パーセント換算で 30.000000000000004 のような誤差が出ないよう小数第2位で丸める。
 */
function toDisplayUnit(key: BuildingRegKey, apiValue: number): number {
  if (!PERCENT_REG_KEYS.has(key)) return apiValue;
  return Math.round(apiValue * 100 * 100) / 100;
}

/** 入力欄の表示単位を API の値へ変換する。 */
function toApiUnit(key: BuildingRegKey, displayValue: number): number {
  return PERCENT_REG_KEYS.has(key) ? displayValue / 100 : displayValue;
}

/**
 * 各行の入力欄の設定。
 * 容積率は 100% を大きく超える（例: 商業地域 3000%）ため上限を設けない。
 */
const ROWS: {
  key: BuildingRegKey;
  labelKey: string;
  unitKey: string;
  step: number;
  max?: number;
}[] = [
  { key: "limit_height", labelKey: "regulation.limitHeight", unitKey: "parcel.unitMeter", step: 0.5 },
  { key: "limit_volume_ratio", labelKey: "regulation.limitVolumeRatio", unitKey: "parcel.unitPercent", step: 10 },
  { key: "limit_coverage_ratio", labelKey: "regulation.limitCoverageRatio", unitKey: "parcel.unitPercent", step: 1, max: 100 },
];

export function BuildingRegulationTab({
  cityRegulations,
  pendingRegulations,
  onChange,
}: {
  /** API から取得した確定値 */
  cityRegulations: RegulationsData;
  /** 編集中値（API 単位。キーが無い項目は確定値を表示する） */
  pendingRegulations: Partial<RegulationsData>;
  /** API 単位へ変換済みの値を通知する */
  onChange: (key: BuildingRegKey, apiValue: number) => void;
}) {
  const { t } = useTranslation();

  /** API 単位での現在値（編集中値を優先） */
  const getValue = useCallback(
    (key: BuildingRegKey): number => pendingRegulations[key] ?? cityRegulations[key],
    [pendingRegulations, cityRegulations],
  );

  const handleChange = useCallback(
    (key: BuildingRegKey, rawValue: string) => {
      const parsed = parseFloat(rawValue);
      if (isNaN(parsed) || parsed < 0) return;
      onChange(key, toApiUnit(key, parsed));
    },
    [onChange],
  );

  return (
    <>
      <p className="regulation-pane__description">
        {t("regulation.buildingRegulationDescription")}
      </p>

      <div className="regulation-building-list">
        {ROWS.map((row) => {
          const modified = getValue(row.key) !== cityRegulations[row.key];
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
                  min={0}
                  max={row.max}
                  step={row.step}
                  className={[
                    "regulation-building-input",
                    modified && "regulation-building-input--modified",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                  value={toDisplayUnit(row.key, getValue(row.key))}
                  onChange={(e) => handleChange(row.key, e.target.value)}
                />
                <span className="regulation-building-unit">{t(row.unitKey)}</span>
              </div>
            </div>
          );
        })}
      </div>
    </>
  );
}
