/**
 * Facility information card dispatcher
 *
 * "Facility information" card in the building construction panel. Unlike construction information,
 * its content varies with agent type (TYPE_STANDARD / TYPE_BUILDING / infrastructure producers, etc.).
 *
 * Only dispatch to dedicated child components by type, matching AgentDetailPanel's structure.
 * To add detailed information for another type, add one switch case and its child component.
 *
 * Currently:
 * - TYPE_STANDARD: StandardFacilityInfo (estimated production, employment, operating costs).
 * - Others: default fallback (null, omitting the card frame itself).
 */

import { useTranslation } from "react-i18next";
import type { AgentMasterEntry } from "../../types";
import { TYPE_STANDARD } from "../../constants";
import { Money } from "../common/Money";
import { resolveLedgerIcon } from "../../utils/ledgerIcon";

type BuildingFacilityInfoCardProps = {
  /** Master data for the selected variant, used to choose content by type. */
  variantData: AgentMasterEntry | null;
  /** Estimated maximum production for TYPE_STANDARD. */
  estimatedMaxProduction: number | null;
  /** Estimated employment for TYPE_STANDARD. */
  estimatedEmployment: number | null;
  /** Estimated TYPE_STANDARD operating cost per epoch (labor + materials, with no rent for dedicated buildings). */
  estimatedOperatingCost: number | null;
  /** Operating cost breakdown: labor cost per epoch. */
  operatingWageCost: number | null;
  /** Operating cost breakdown: material cost per epoch. */
  operatingMaterialCost: number | null;
  /** Operating cost source budget category (LEDGER_*). Null if undefined. */
  operatingCostCategory: number | null;
  /** Days per epoch, used in the "per epoch (n days)" note. */
  simulationDays: number | null;
  /** Whether construction cost is being fetched. */
  isCostLoading: boolean;
};

export function BuildingFacilityInfoCard({
  variantData,
  estimatedMaxProduction,
  estimatedEmployment,
  estimatedOperatingCost,
  operatingWageCost,
  operatingMaterialCost,
  operatingCostCategory,
  simulationDays,
  isCostLoading,
}: BuildingFacilityInfoCardProps) {
  switch (variantData?.type) {
    case TYPE_STANDARD:
      return (
        <StandardFacilityInfo
          estimatedMaxProduction={estimatedMaxProduction}
          estimatedEmployment={estimatedEmployment}
          estimatedOperatingCost={estimatedOperatingCost}
          operatingWageCost={operatingWageCost}
          operatingMaterialCost={operatingMaterialCost}
          operatingCostCategory={operatingCostCategory}
          simulationDays={simulationDays}
          isCostLoading={isCostLoading}
        />
      );
    // Future: add case TYPE_BUILDING (floor breakdown), infrastructure producers (service areas), etc.
    default:
      return null;
  }
}

/* ---- TYPE_STANDARD (ordinary facility) information ---- */

type StandardFacilityInfoProps = {
  estimatedMaxProduction: number | null;
  estimatedEmployment: number | null;
  estimatedOperatingCost: number | null;
  operatingWageCost: number | null;
  operatingMaterialCost: number | null;
  operatingCostCategory: number | null;
  simulationDays: number | null;
  isCostLoading: boolean;
};

/**
 * Information for ordinary facilities (TYPE_STANDARD).
 *
 * Along with estimated production/employment at full operation, display recurring per-epoch
 * operating costs (labor + materials) with a breakdown. The construction-cost backend endpoint
 * uses calc_rough_cost, shared with new spawn evaluation. No rent is included for dedicated buildings.
 * If a source budget category (LEDGER_*) exists, also display its icon/label so players can
 * understand the recurring budget expense before building.
 */
function StandardFacilityInfo({
  estimatedMaxProduction,
  estimatedEmployment,
  estimatedOperatingCost,
  operatingWageCost,
  operatingMaterialCost,
  operatingCostCategory,
  simulationDays,
  isCostLoading,
}: StandardFacilityInfoProps) {
  const { t } = useTranslation();

  /** Shared loading text while fetching, "—" for null, otherwise Money. */
  const renderMoney = (value: number | null) =>
    isCostLoading ? (
      t("common.loading")
    ) : value != null ? (
      <Money value={value} unit />
    ) : (
      "—"
    );

  const ledgerIcon =
    operatingCostCategory != null ? resolveLedgerIcon(operatingCostCategory) : "";

  return (
    <div className="bc-section">
      <div className="bc-section-title">{t("building_construction.facilityInfo")}</div>
      <div className="bc-info-grid">
        <span className="bc-info-label">{t("building_construction.estimatedMaxProduction")}</span>
        <span className="bc-info-value">
          {isCostLoading
            ? t("common.loading")
            : estimatedMaxProduction != null
            ? Math.round(estimatedMaxProduction).toLocaleString()
            : "—"}
        </span>

        <span className="bc-info-label">{t("building_construction.estimatedEmployment")}</span>
        <span className="bc-info-value">
          {isCostLoading
            ? t("common.loading")
            : estimatedEmployment != null
            ? t("building_construction.unitPeopleValue", { value: Math.round(estimatedEmployment) })
            : "—"}
        </span>

        <span className="bc-info-label">{t("building_construction.estimatedRunningCost")}</span>
        <span className="bc-info-value">{renderMoney(estimatedOperatingCost)}</span>

        {/* Breakdown (labor/material costs). Display only after fetching. */}
        {!isCostLoading && operatingWageCost != null && (
          <>
            <span className="bc-info-label bc-info-label--sub">
              {t("building_construction.runningCostWage")}
            </span>
            <span className="bc-info-value bc-info-value--sub">
              <Money value={operatingWageCost} unit />
            </span>
          </>
        )}
        {!isCostLoading && operatingMaterialCost != null && (
          <>
            <span className="bc-info-label bc-info-label--sub">
              {t("building_construction.runningCostMaterial")}
            </span>
            <span className="bc-info-value bc-info-value--sub">
              <Money value={operatingMaterialCost} unit />
            </span>
          </>
        )}

        {/* Source budget category (operating expense category when built as a public facility). */}
        {!isCostLoading && operatingCostCategory != null && (
          <>
            <span className="bc-info-label">{t("building_construction.runningCostBudget")}</span>
            <span className="bc-info-value bc-ledger-value">
              {ledgerIcon && <img src={ledgerIcon} alt="" className="bc-ledger-icon" />}
              {t(`budget.category.${operatingCostCategory}`)}
            </span>
          </>
        )}
      </div>

      {/* Operating cost assumption note (per epoch at full operation). */}
      {!isCostLoading && estimatedOperatingCost != null && (
        <div className="bc-muted bc-running-cost-note">
          {simulationDays != null
            ? t("building_construction.runningCostNote", { days: Math.round(simulationDays) })
            : t("building_construction.runningCostNoteNoDays")}
        </div>
      )}
    </div>
  );
}
