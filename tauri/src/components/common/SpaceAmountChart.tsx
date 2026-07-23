/**
 * フロアタイプごとの量(供給/需要、またはキャパシティ/占有量)を
 * 2本1組の横棒グラフで表示するコンポーネント。
 */

import { useTranslation } from "react-i18next";
import { useMasterData } from "../../contexts/MasterDataContext";
import "./SpaceAmountChart.css";

type SpaceAmountChartProps = {
  /** 供給量(またはキャパシティ)の辞書。キーはスペースタイプIDの文字列表現 */
  supplies: Record<string, number>;
  /** 需要量(または占有量)の辞書。キーはスペースタイプIDの文字列表現 */
  demands: Record<string, number>;
  /** 1本目の棒のラベル(例: "供給", "キャパシティ") */
  supplyLabel?: string;
  /** 2本目の棒のラベル(例: "需要", "占有量") */
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

  // 全てのスペースタイプを取得(供給または需要が0より大きいもの)
  const types = Array.from(
    new Set([...Object.keys(supplies), ...Object.keys(demands)])
  ).filter((type) => (supplies[type] || 0) > 0 || (demands[type] || 0) > 0);

  if (types.length === 0) return null;

  // グラフの最大値を計算(供給と需要の最大値)
  const maxAmount = Math.max(
    ...types.map((type) => Math.max(supplies[type] || 0, demands[type] || 0))
  );

  // 最大値が0の場合は何も表示しない
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
