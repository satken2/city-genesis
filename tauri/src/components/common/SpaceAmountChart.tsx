/**
 * Displays quantities by floor type (supply/demand or capacity/occupancy)
 * as paired horizontal bars.
 */

import { useTranslation } from "react-i18next";
import { useMasterData } from "../../contexts/MasterDataContext";
import "./SpaceAmountChart.css";

type SpaceAmountChartProps = {
  /** Supply/capacity dictionary, keyed by string representations of space type IDs. */
  supplies: Record<string, number>;
  /** Demand/occupancy dictionary, keyed by string representations of space type IDs. */
  demands: Record<string, number>;
  /** First bar label (e.g. "Supply", "Capacity"). */
  supplyLabel?: string;
  /** Second bar label (e.g. "Demand", "Occupancy"). */
  demandLabel?: string;
};

export function SpaceAmountChart({
  supplies,
  demands,
  supplyLabel,
  demandLabel,
}: SpaceAmountChartProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  // Get all space types with positive supply or demand.
  const types = Array.from(
    new Set([...Object.keys(supplies), ...Object.keys(demands)])
  ).filter((type) => (supplies[type] || 0) > 0 || (demands[type] || 0) > 0);

  if (types.length === 0) return null;

  // Calculate the chart maximum (maximum of supply and demand).
  const maxAmount = Math.max(
    ...types.map((type) => Math.max(supplies[type] || 0, demands[type] || 0))
  );

  // Display nothing when the maximum is 0.
  if (maxAmount === 0) return null;

  const defaultSupplyLabel = supplyLabel || t("common.supply", "供給");
  const defaultDemandLabel = demandLabel || t("common.demand", "需要");

  return (
    <div className="space-amount-chart">
      <div className="space-amount-chart-legend">
        <div className="space-amount-chart-legend-item">
          <span className="space-amount-chart-swatch supply-swatch"></span>
          {defaultSupplyLabel}
        </div>
        <div className="space-amount-chart-legend-item">
          <span className="space-amount-chart-swatch demand-swatch"></span>
          {defaultDemandLabel}
        </div>
      </div>
      <div className="space-amount-chart-grid">
        {types.map((type) => {
          const supply = supplies[type] || 0;
          const demand = demands[type] || 0;
          const supplyPct = (supply / maxAmount) * 100;
          const demandPct = (demand / maxAmount) * 100;
          
          const categoryName =
            masterData?.space_categories?.[Number(type)]?.display_name ||
            t(`spaceType.${type}`, { defaultValue: `Type ${type}` });

          return (
            <div key={type} className="space-amount-chart-row">
              <div className="space-amount-chart-label">{categoryName}</div>
              <div className="space-amount-chart-bars">
                <div className="space-amount-chart-bar-container">
                  <div
                    className="space-amount-chart-bar supply-bar"
                    style={{ width: `${supplyPct}%` }}
                  ></div>
                  <span className="space-amount-chart-value">
                    {t("common.sqm", { value: Math.round(supply) })}
                  </span>
                </div>
                <div className="space-amount-chart-bar-container">
                  <div
                    className="space-amount-chart-bar demand-bar"
                    style={{ width: `${demandPct}%` }}
                  ></div>
                  <span className="space-amount-chart-value">
                    {t("common.sqm", { value: Math.round(demand) })}
                  </span>
                </div>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
