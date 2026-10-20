/**
 * Repair settings tab
 *
 * Set durability thresholds for initiating public building/road repairs.
 * Below the threshold, repairs begin probabilistically.
 *
 * The API exchanges 0–1 ratios, while the UI uses 0–100 percentages.
 * The window converts to ratios on save; this tab handles percentages only.
 */

import { useCallback } from "react";
import { useTranslation } from "react-i18next";

/** Repair threshold target; roads and public buildings have separate thresholds. */
export type RepairTarget = "building" | "path";

const ROWS: { target: RepairTarget; labelKey: string }[] = [
  { target: "building", labelKey: "regulation.repairThresholdBuilding" },
  { target: "path", labelKey: "regulation.repairThresholdPath" },
];

export function RepairTab({
  cityThresholds,
  pendingThresholds,
  onChange,
}: {
  /** Saved values retrieved from the API (0–100 percentages) */
  cityThresholds: Record<RepairTarget, number>;
  /** Draft values; null for unedited targets. */
  pendingThresholds: Record<RepairTarget, number | null>;
  onChange: (target: RepairTarget, value: number) => void;
}) {
  const { t } = useTranslation();

  const handleChange = useCallback(
    (target: RepairTarget, rawValue: string) => {
      const parsed = parseFloat(rawValue);
      if (isNaN(parsed) || parsed < 0 || parsed > 100) return;
      onChange(target, parsed);
    },
    [onChange],
  );

  return (
    <>
      <p className="regulation-pane__description">
        {t("regulation.repairDescription")}
      </p>

      <div className="regulation-building-list">
        {ROWS.map((row) => {
          const value = pendingThresholds[row.target] ?? cityThresholds[row.target];
          const modified = value !== cityThresholds[row.target];
          return (
            <div key={row.target} className="regulation-building-row">
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
                  max={100}
                  step={5}
                  className={[
                    "regulation-building-input",
                    modified && "regulation-building-input--modified",
                  ]
                    .filter(Boolean)
                    .join(" ")}
                  value={value}
                  onChange={(e) => handleChange(row.target, e.target.value)}
                />
                <span className="regulation-building-unit">{t("parcel.unitPercent")}</span>
              </div>
            </div>
          );
        })}
      </div>
    </>
  );
}
