/**
 * Supply information component
 *
 * Supply section for TYPE_STANDARD agents. Displays product name, supply price, supply amount,
 * sales volume, profit margin, and utilization in a grid. Names and units come from master data (products).
 * supply_sold_amount and profit_rates use DataList so their history can be inspected.
 * Profit margin has the same definition as the income/expenditure balance ratio, so also show
 * its latest value with BalanceMeter. Display the latest epoch's utilization (first element of
 * utilization) using the shared meter and a percentage.
 */

import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import { useMasterData } from "../../contexts/MasterDataContext";
import { BalanceMeter } from "../common/BalanceMeter";
import { DataList } from "../common/DataList";
import { Meter } from "../common/Meter";
import "./SupplyInfoPanel.css";

type SupplyInfoPanelProps = {
  agent: AgentData;
};

export function SupplyInfoPanel({ agent }: SupplyInfoPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const product = agent.supply_product_id != null
    ? masterData?.products[agent.supply_product_id]
    : null;

  const productName =
    product?.display_name ??
    (agent.supply_product_id != null
      ? String(agent.supply_product_id)
      : null);
  const unit = product?.unit ?? "";

  // profit_rates arrives as (revenue-cost)/revenue, a ratio (-1 to 1); convert to percentages for display.
  const formatPercent = (value: number) => `${(value * 100).toFixed(2)}%`;
  const formatCurrency = (value: number) => t("common.currency", { value: value.toFixed(2) });
  const utilization = agent.utilization?.[0];

  return (
    <section className="agent-detail-section">
      <h3 className="agent-detail-section-title">{t("supply.title")}</h3>
      <div className="agent-detail-grid">
        {productName != null && (
          <>
            <span className="agent-detail-label">
              {t("supply.product")}
            </span>
            <span className="agent-detail-value">{productName}</span>
          </>
        )}

        {agent.supply_price != null && (
          <>
            <span className="agent-detail-label">
              {t("supply.price")}
            </span>
            <span className="agent-detail-value">
              {formatCurrency(agent.supply_price)}
            </span>
          </>
        )}

        {agent.supply_amount != null && (
          <>
            <span className="agent-detail-label">
              {t("supply.amount")}
            </span>
            <span className="agent-detail-value">
              {`${agent.supply_amount.toFixed(2)} ${unit}`}
            </span>
          </>
        )}

        {agent.supply_sold_amount != null && (
          <>
            <span className="agent-detail-label">
              {t("supply.soldAmount")}
            </span>
            <span className="agent-detail-value">
              <DataList
                values={agent.supply_sold_amount}
                formatter={(v) => `${v.toFixed(2)} ${unit}`}
              />
            </span>
          </>
        )}

        {agent.profit_rates != null && (
          <>
            <span className="agent-detail-label">
              {t("supply.profitRate")}
            </span>
            <span className="agent-detail-value supply-profit-rate">
              {agent.profit_rates.length > 0 && (
                <BalanceMeter value={agent.profit_rates[0]} />
              )}
              {/*
 * Profit margin ranges within ±100%. Old saves may retain unclamped outliers in history
 * (e.g. -895), so clamp only for chart rendering.
 */}
              <DataList
                values={agent.profit_rates}
                formatter={formatPercent}
                clampRange={[-1, 1]}
              />
            </span>
          </>
        )}

        {utilization != null && (
          <>
            <span className="agent-detail-label">
              {t("supply.utilization")}
            </span>
            <span className="agent-detail-value supply-info-meter">
              <Meter value={utilization} />
              <span className="supply-info-meter-value">{`${Math.round(utilization * 100)}%`}</span>
            </span>
          </>
        )}
      </div>
    </section>
  );
}
