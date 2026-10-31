/**
 * Building regulations tab
 *
 * District defaults: height limits, volume ratios, and building coverage ratios for districts created from this
 * point onward; they do not apply retroactively to existing districts/parcels. Edit existing districts through
 * their detail panels.
 * Parking: the parking requirement has no district value and applies to every tenant building built afterward.
 * Repair: durability thresholds for initiating public building/road repairs. Below the threshold, repairs begin
 * probabilistically.
 *
 * The API stores ratios, but display/edit volume and building coverage ratios as percentages, following Japanese
 * building regulation conventions. This file owns that unit conversion. Repair thresholds are handled as 0–100
 * percentages here; the window converts them to 0–1 ratios on save.
 */

import { useCallback } from "react";
import { useTranslation } from "react-i18next";
import type { BuildingRegKey, RegulationsData } from "../regulationTypes";
import { RegulationNumberRow, RegulationSection } from "../RegulationForm";

/** Building regulation keys used for save payloads and change detection. */
export const BUILDING_REG_KEYS: BuildingRegKey[] = [
  "limit_height",
  "limit_volume_ratio",
  "limit_coverage_ratio",
  "required_parking_ratio",
];

/** Repair threshold target; roads and public buildings have separate thresholds. */
export type RepairTarget = "building" | "path";

/**
 * Building regulation keys converted to percentages in inputs.
 * The API stores ratios (volume ratio 30.0 = 3000%; building coverage ratio 0.5 = 50%).
 * Exclude limit_height, which is measured in meters.
 */
const PERCENT_REG_KEYS = new Set<BuildingRegKey>([
  "limit_volume_ratio",
  "limit_coverage_ratio",
  "required_parking_ratio",
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

type RegRow = { key: BuildingRegKey; labelKey: string; unitKey: string; step: number; max?: number };

/**
 * Building regulation sections and their rows.
 * Do not cap volume ratio, which can greatly exceed 100% (e.g. 3000% in commercial districts).
 */
const REG_SECTIONS: { titleKey: string; descriptionKey: string; rows: RegRow[] }[] = [
  {
    titleKey: "regulation.districtDefaultsTitle",
    descriptionKey: "regulation.districtDefaultsDescription",
    rows: [
      { key: "limit_height", labelKey: "regulation.limitHeight", unitKey: "parcel.unitMeter", step: 0.5 },
      { key: "limit_volume_ratio", labelKey: "regulation.limitVolumeRatio", unitKey: "parcel.unitPercent", step: 10 },
      { key: "limit_coverage_ratio", labelKey: "regulation.limitCoverageRatio", unitKey: "parcel.unitPercent", step: 1, max: 100 },
    ],
  },
  {
    titleKey: "regulation.parkingTitle",
    descriptionKey: "regulation.parkingDescription",
    rows: [
      { key: "required_parking_ratio", labelKey: "regulation.requiredParkingRatio", unitKey: "parcel.unitPercent", step: 1, max: 99 }, // 100% leaves no floor to rent; the core rejects it
    ],
  },
];

const REPAIR_ROWS: { target: RepairTarget; labelKey: string }[] = [
  { target: "building", labelKey: "regulation.repairThresholdBuilding" },
  { target: "path", labelKey: "regulation.repairThresholdPath" },
];

export function BuildingRegulationTab({
  cityRegulations,
  pendingRegulations,
  onRegulationChange,
  cityRepair,
  pendingRepair,
  onRepairChange,
}: {
  /** Saved values retrieved from the API */
  cityRegulations: RegulationsData;
  /** Draft values in API units; absent keys display saved values. */
  pendingRegulations: Partial<RegulationsData>;
  /** Report values already converted to API units. */
  onRegulationChange: (key: BuildingRegKey, apiValue: number) => void;
  /** Saved repair thresholds (0–100 percentages) */
  cityRepair: Record<RepairTarget, number>;
  /** Draft repair thresholds; null for unedited targets. */
  pendingRepair: Record<RepairTarget, number | null>;
  onRepairChange: (target: RepairTarget, value: number) => void;
}) {
  const { t } = useTranslation();

  /** Current value in API units, preferring the draft */
  const getValue = useCallback(
    (key: BuildingRegKey): number => pendingRegulations[key] ?? cityRegulations[key],
    [pendingRegulations, cityRegulations],
  );

  const handleRegulationChange = useCallback(
    (key: BuildingRegKey, rawValue: string) => {
      const parsed = parseFloat(rawValue);
      if (isNaN(parsed) || parsed < 0) return;
      onRegulationChange(key, toApiUnit(key, parsed));
    },
    [onRegulationChange],
  );

  const handleRepairChange = useCallback(
    (target: RepairTarget, rawValue: string) => {
      const parsed = parseFloat(rawValue);
      if (isNaN(parsed) || parsed < 0 || parsed > 100) return;
      onRepairChange(target, parsed);
    },
    [onRepairChange],
  );

  return (
    <>
      {REG_SECTIONS.map((section) => (
        <RegulationSection key={section.titleKey} title={t(section.titleKey)} description={t(section.descriptionKey)}>
          <div className="regulation-building-list">
            {section.rows.map((row) => (
              <RegulationNumberRow
                key={row.key}
                label={t(row.labelKey)}
                unit={t(row.unitKey)}
                value={toDisplayUnit(row.key, getValue(row.key))}
                modified={getValue(row.key) !== cityRegulations[row.key]}
                max={row.max}
                step={row.step}
                onChange={(raw) => handleRegulationChange(row.key, raw)}
              />
            ))}
          </div>
        </RegulationSection>
      ))}

      <RegulationSection title={t("regulation.repairTitle")} description={t("regulation.repairDescription")}>
        <div className="regulation-building-list">
          {REPAIR_ROWS.map((row) => {
            const value = pendingRepair[row.target] ?? cityRepair[row.target];
            return (
              <RegulationNumberRow
                key={row.target}
                label={t(row.labelKey)}
                unit={t("parcel.unitPercent")}
                value={value}
                modified={value !== cityRepair[row.target]}
                max={100}
                step={5}
                onChange={(raw) => handleRepairChange(row.target, raw)}
              />
            );
          })}
        </div>
      </RegulationSection>
    </>
  );
}
