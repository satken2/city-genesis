/**
 * Building regulations tab
 *
 * Set citywide height limits, volume ratios, and building coverage ratios.
 * These settings are defaults for districts created from this point onward; they do not apply retroactively to
 * existing districts/parcels. Edit existing districts through their detail panels.
 *
 * The API stores ratios, but display/edit volume and building coverage ratios as percentages, following Japanese
 * building regulation conventions. This file owns unit conversion.
 */

import { useCallback } from "react";
import { useTranslation } from "react-i18next";
import type { BuildingRegKey, RegulationsData } from "../regulationTypes";

/** Building regulation keys used for save payloads and change detection. */
export const BUILDING_REG_KEYS: BuildingRegKey[] = [
  "limit_height",
  "limit_volume_ratio",
  "limit_coverage_ratio",
];

/**
 * Building regulation keys converted to percentages in inputs.
 * The API stores ratios (volume ratio 30.0 = 3000%; building coverage ratio 0.5 = 50%).
 * Exclude limit_height, which is measured in meters.
 */
const PERCENT_REG_KEYS = new Set<BuildingRegKey>([
  "limit_volume_ratio",
  "limit_coverage_ratio",
]);

/**
 * Convert API values to input display units.
 * Round to two decimal places to avoid percentage conversion errors such as 30.000000000000004.
 */
function toDisplayUnit(key: BuildingRegKey, apiValue: number): number {
  if (!PERCENT_REG_KEYS.has(key)) return apiValue;
  return Math.round(apiValue * 100 * 100) / 100;
}

/** Convert input display units to API values. */
function toApiUnit(key: BuildingRegKey, displayValue: number): number {
  return PERCENT_REG_KEYS.has(key) ? displayValue / 100 : displayValue;
}

/**
 * Input settings per row.
 * Do not cap volume ratio, which can greatly exceed 100% (e.g. 3000% in commercial districts).
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
  /** Saved values retrieved from the API */
  cityRegulations: RegulationsData;
  /** Draft values in API units; absent keys display saved values. */
  pendingRegulations: Partial<RegulationsData>;
  /** Report values already converted to API units. */
  onChange: (key: BuildingRegKey, apiValue: number) => void;
}) {
  const { t } = useTranslation();

  /** Current value in API units, preferring the draft */
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
