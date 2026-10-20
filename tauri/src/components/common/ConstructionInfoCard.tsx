/**
 * Shared construction information card
 *
 * Displays construction estimates (time, approximate cost with standard-cost multiplier badge,
 * and approximate maintenance cost), plus actual construction/demolition information
 * (cost per epoch, remaining epochs). Shared by:
 * - Building construction panel (building/BuildingConstructionPanel).
 * - Road construction panel (path/PathConstructionPanel).
 * - Agent detail construction tab (agent/BuildingConstructionInfoPanel).
 * - Path detail construction information (path/PathConstructionInfoPanel).
 * - Path variant change (path/PathRebuildPanel).
 * - Earthworks panel (grading/GradingPanel).
 * - Agent detail earthworks construction tab (grading/GradingConstructionInfoPanel).
 *
 * Display rules:
 * - leadingRows prepends caller-built rows, allowing domain-specific quantities such as earthworks
 *   type/cut volume to share the cost table's style.
 * - buildingArea / totalHeight / totalFloorArea prepend physical information (buildings only;
 *   paths omit them). Null means loading/unconfirmed and displays "—".
 * - Show a multiplier badge such as "x1.3" beside estimated cost only when standard cost is positive.
 *   Zero/null standard costs occur with cost_ratio=0 (unpaved roads, etc.); avoid division by zero.
 * - costRatioHelp receives a translated hover explanation, since reasons for premiums vary by domain.
 * - nextEpochConstructionBudget adds the "X% of next period's construction budget" note for construction panels.
 * - maintenanceCost adds approximate maintenance cost per epoch for path construction panels.
 * - Positive epochRevenue (total city income in the latest completed epoch) adds revenue proportions
 *   beside approximate construction and maintenance costs.
 * - perEpoch shows cost per epoch/remaining time for construction/demolition detail panels.
 * - supplyRatio / expectedDurationDays show prospects based on city construction service capacity.
 *   Only fulfillment below SUPPLY_RATIO_OK adds expected time and a warning banner;
 *   thresholds are the constants below.
 * - availableFunds / requiredTotal add an insufficient-funds banner when the required total exceeds
 *   available funds. Construction panel callers disable confirmation with the same check from
 *   utils/constructionFunding. Also show "Available funds" unless showAvailableFunds is false.
 */

import { Fragment, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Money } from "./Money";
import { costRatioColor } from "../../utils/costRatio";
import { constructionFundsShortfall } from "../../utils/constructionFunding";
import "./ConstructionInfoCard.css";

// Display thresholds for construction service fulfillment (construction_supply_ratio).
// At least OK: no display / at least MILD: yellow caution / below MILD: orange warning.
// These are presentation thresholds rather than game rules, so keep them as UI constants.
const SUPPLY_RATIO_OK = 0.9;
const SUPPLY_RATIO_MILD = 0.5;

/** Proportion of revenue (%). Use two significant digits below 1% so small maintenance ratios do not collapse to 0%. */
function formatRevenuePercent(amount: number, revenue: number): string {
  const percent = (amount / revenue) * 100;
  if (percent >= 100) return percent.toFixed(0);
  if (percent >= 1) return percent.toFixed(1);
  if (percent > 0 && percent < 0.01) return "<0.01";
  return String(Number(percent.toPrecision(2)));
}

type ConstructionPerEpochInfo = {
  /** Construction/demolition cost per epoch. */
  costPerEpoch: number | null;
  /** Remaining epoch count. */
  remainingEpochs: number | null;
  /** During demolition, change labels to "Demolition cost" and "Remaining demolition time". */
  isDemolishing: boolean;
};

export type ConstructionInfoRow = {
  label: string;
  value: ReactNode;
};

type ConstructionInfoCardProps = {
  /** Card heading. Omit to use no heading and rely on the caller's section heading. */
  title?: string;
  /** Rows prepended to the grid (translated labels and formatted values). */
  leadingRows?: ConstructionInfoRow[];
  /** Building footprint area (m²). Undefined hides the row (paths); null displays "—". */
  buildingArea?: number | null;
  /** Building height (m). Undefined hides the row; null displays "—". */
  totalHeight?: number | null;
  /** Total floor area (m²). Undefined hides the row; null displays "—". */
  totalFloorArea?: number | null;
  /** Path length (m). Undefined hides the row (buildings); null displays "—" (unconfirmed paths). */
  pathLength?: number | null;
  /** Approximate total cost. */
  estimatedCost: number | null;
  /** Standard construction cost on perfectly flat terrain; denominator of the multiplier badge. */
  standardCost: number | null;
  /** Translated hover explanation for the multiplier badge. */
  costRatioHelp?: string;
  /** Construction time (days). */
  estimatedDurationDays: number | null;
  /** Whether construction cost is being fetched. */
  isCostLoading: boolean;
  /** Next period's construction budget. Supplying it shows a budget proportion note. */
  nextEpochConstructionBudget?: number | null;
  /** Approximate maintenance cost per epoch. Undefined hides the row; null displays "—". */
  maintenanceCost?: number | null;
  /** Total city income in the latest completed epoch. If positive, append proportions to approximate construction/maintenance costs. */
  epochRevenue?: number | null;
  /** Construction service procurement fulfillment (0-1). 1.0 means construction is expected to match the estimate. */
  supplyRatio?: number | null;
  /** Expected construction time accounting for fulfillment (days). */
  expectedDurationDays?: number | null;
  /** City funds available for orders (cash minus savings target minus existing commitments). Supplying it shows the row. */
  availableFunds?: number | null;
  /** Required total for the funding check (construction plus estimated land acquisition). Compared with availableFunds. */
  requiredTotal?: number | null;
  /** False hides the "Available funds" row while retaining insufficient-funds warnings. */
  showAvailableFunds?: boolean;
  /** Actual construction/demolition information. Supplying it shows cost per epoch/remaining time. */
  perEpoch?: ConstructionPerEpochInfo;
};

export function ConstructionInfoCard({
  title,
  leadingRows,
  buildingArea,
  totalHeight,
  totalFloorArea,
  pathLength,
  estimatedCost,
  standardCost,
  costRatioHelp,
  estimatedDurationDays,
  isCostLoading,
  nextEpochConstructionBudget,
  maintenanceCost,
  epochRevenue,
  supplyRatio,
  expectedDurationDays,
  availableFunds,
  requiredTotal,
  showAvailableFunds = true,
  perEpoch,
}: ConstructionInfoCardProps) {
  const { t } = useTranslation();

  // Funding shortfall. Positive only when all check inputs exist and required total exceeds available funds.
  const fundsShortfall = isCostLoading ? null : constructionFundsShortfall(requiredTotal, availableFunds);

  // Construction service supply/demand warning level. Show nothing if fulfillment is sufficient (at least OK).
  const supplyShortageLevel: "mild" | "severe" | null =
    !isCostLoading && supplyRatio != null && supplyRatio < SUPPLY_RATIO_OK
      ? supplyRatio < SUPPLY_RATIO_MILD
        ? "severe"
        : "mild"
      : null;

  const revenueRatio = (amount: number) =>
    epochRevenue != null && epochRevenue > 0 ? (
      <span className="construction-revenue-ratio">
        {t("construction.revenueRatio", { percent: formatRevenuePercent(amount, epochRevenue) })}
      </span>
    ) : null;

  return (
    <div className="construction-info-card">
      {title && <div className="construction-info-title">{title}</div>}

      <div className="construction-info-grid">
        {leadingRows?.map((row) => (
          <Fragment key={row.label}>
            <span className="construction-info-label">{row.label}</span>
            <span className="construction-info-value">{row.value}</span>
          </Fragment>
        ))}

        {buildingArea !== undefined && (
          <>
            <span className="construction-info-label">{t("construction.buildingArea")}</span>
            <span className="construction-info-value">
              {buildingArea != null
                ? `${buildingArea.toFixed(1)} ${t("construction.unitSqm")}`
                : "—"}
            </span>
          </>
        )}

        {totalHeight !== undefined && (
          <>
            <span className="construction-info-label">{t("construction.height")}</span>
            <span className="construction-info-value">
              {totalHeight != null
                ? `${totalHeight.toFixed(1)} ${t("construction.unitMeter")}`
                : "—"}
            </span>
          </>
        )}

        {totalFloorArea !== undefined && (
          <>
            <span className="construction-info-label">{t("construction.floorArea")}</span>
            <span className="construction-info-value">
              {totalFloorArea != null
                ? `${totalFloorArea.toFixed(1)} ${t("construction.unitSqm")}`
                : "—"}
            </span>
          </>
        )}

        {pathLength !== undefined && (
          <>
            <span className="construction-info-label">{t("construction.pathLength")}</span>
            <span className="construction-info-value">
              {pathLength != null
                ? `${pathLength.toFixed(1)} ${t("construction.unitMeter")}`
                : "—"}
            </span>
          </>
        )}

        <span className="construction-info-label">{t("construction.estimatedDuration")}</span>
        <span className="construction-info-value">
          {isCostLoading
            ? t("common.loading")
            : estimatedDurationDays != null
            ? t("construction.durationDays", { value: Math.ceil(estimatedDurationDays) })
            : "—"}
        </span>

        {/* Expected construction time based on city construction capacity. Omit the row when fulfillment is sufficient. */}
        {supplyShortageLevel && expectedDurationDays != null && (
          <>
            <span className="construction-info-label">
              {t("construction.expectedDuration")}
            </span>
            <span
              className={`construction-info-value construction-supply-value-${supplyShortageLevel}`}
            >
              {t("construction.expectedDurationDays", {
                value: Math.ceil(expectedDurationDays),
              })}
            </span>
          </>
        )}

        <span className="construction-info-label">{t("construction.estimatedCost")}</span>
        <span className="construction-info-value">
          {isCostLoading ? (
            t("common.loading")
          ) : estimatedCost != null ? (
            <>
              <Money value={estimatedCost} unit />
              {standardCost != null && standardCost > 0 && (
                <span
                  className="construction-cost-ratio-badge"
                  title={costRatioHelp}
                  style={{ backgroundColor: costRatioColor(estimatedCost / standardCost) }}
                >
                  x{(estimatedCost / standardCost).toFixed(1)}
                </span>
              )}
              {revenueRatio(estimatedCost)}
            </>
          ) : (
            "—"
          )}
        </span>

        {maintenanceCost !== undefined && (
          <>
            <span className="construction-info-label">{t("construction.estimatedMaintenanceCost")}</span>
            <span className="construction-info-value">
              {isCostLoading ? (
                t("common.loading")
              ) : maintenanceCost != null ? (
                <>
                  <Money value={maintenanceCost} unit />
                  {revenueRatio(maintenanceCost)}
                </>
              ) : (
                "—"
              )}
            </span>
          </>
        )}
      </div>

      {/* Construction service shortage warning. */}
      {supplyShortageLevel && (
        <div className={`construction-supply-note construction-supply-note-${supplyShortageLevel}`}>
          {supplyShortageLevel === "severe"
            ? t("construction.supplyShortageSevere")
            : t("construction.supplyShortageMild")}
        </div>
      )}

      {/* Funds available for orders and insufficient-funds warning (preview of order eligibility in construction panels). */}
      {showAvailableFunds && availableFunds !== undefined && (
        <div className="construction-info-grid">
          <span className="construction-info-label">{t("construction.availableFunds")}</span>
          <span className="construction-info-value">
            {availableFunds != null
              ? t("common.currency", { value: Math.round(availableFunds) })
              : "—"}
          </span>
        </div>
      )}
      {fundsShortfall != null && fundsShortfall > 0 && (
        <div className="construction-supply-note construction-supply-note-severe">
          {t("construction.insufficientFunds", {
            value: t("common.currency", { value: Math.round(fundsShortfall) }),
          })}
        </div>
      )}

      {/* Estimated cost as a proportion of next period's construction budget (to support construction panel decisions). */}
      {!isCostLoading
        && estimatedCost != null
        && nextEpochConstructionBudget != null
        && nextEpochConstructionBudget > 0 && (
        <div className="construction-budget-note">
          {t("construction.budgetRatioNote", {
            percent: ((estimatedCost / nextEpochConstructionBudget) * 100).toFixed(1),
          })}
        </div>
      )}

      {/* During construction/demolition: cost per epoch and remaining time. */}
      {perEpoch && (
        <div className="construction-info-grid construction-per-epoch">
          <span className="construction-info-label">
            {perEpoch.isDemolishing
              ? t("construction.demolitionCostPerEpoch")
              : t("construction.constructionCostPerEpoch")}
          </span>
          <span className="construction-info-value">
            {perEpoch.costPerEpoch != null
              ? t("common.currency", { value: Math.round(perEpoch.costPerEpoch) })
              : "—"}
          </span>

          <span className="construction-info-label">
            {perEpoch.isDemolishing
              ? t("construction.remainingDemolition")
              : t("construction.remainingConstruction")}
          </span>
          <span className="construction-info-value">
            {perEpoch.remainingEpochs != null
              ? t("construction.remainingEpochs", { n: Math.ceil(perEpoch.remainingEpochs) })
              : "—"}
          </span>
        </div>
      )}
    </div>
  );
}
