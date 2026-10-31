/**
 * Supply information component
 *
 * Supply section for TYPE_STANDARD agents. Displays product name, supply price, supply amount,
 * sales volume, profit margin, and utilization in a grid. Names and units come from master data (products).
 * supply_sold_amount and profit_rates use DataList so their history can be inspected.
 * Profit margin has the same definition as the income/expenditure balance ratio, so also show
 * its latest value with BalanceMeter. Display the latest epoch's utilization (first element of
 * utilization) using the shared meter and a percentage.
 *
 * 観光客の受入可能人数を決める施設(観光地・玄関口。コアが tourist_capacity を返す施設)は、
 * 見出しと行名を「観光客の受け入れ」として出し、施設1つ分の受入可能人数の行を足す。
 * これらの財は1単位が1人1回の入場・乗車なので、量を人数として読める。行名は財IDごとの訳語を使う。
 */

import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import { useMasterData } from "../../contexts/MasterDataContext";
import { resolveIntlLocale } from "../../utils/locale";
import { BalanceMeter } from "../common/BalanceMeter";
import { DataList } from "../common/DataList";
import { Meter } from "../common/Meter";
import "./SupplyInfoPanel.css";

type SupplyInfoPanelProps = {
  agent: AgentData;
};

export function SupplyInfoPanel({ agent }: SupplyInfoPanelProps) {
  const { t, i18n } = useTranslation();
  const masterData = useMasterData();
  const locale = resolveIntlLocale(i18n.language);

  const product = agent.supply_product_id != null
    ? masterData?.products[agent.supply_product_id]
    : null;

  const productName =
    product?.display_name ??
    (agent.supply_product_id != null
      ? String(agent.supply_product_id)
      : null);
  const unit = product?.unit ?? "";

  const touristCapacity: number | null = agent.tourist_capacity ?? null;
  const hostsTourists = touristCapacity != null;
  const labelKey = (key: string) =>
    hostsTourists ? `tourismSupply.${agent.supply_product_id}.${key}` : `supply.${key}`;
  const formatPersons = (value: number) =>
    t("tourismSupply.persons", { value: Math.round(value).toLocaleString(locale) });
  const formatAmount = (value: number) =>
    hostsTourists ? formatPersons(value) : `${value.toFixed(2)} ${unit}`;

  // profit_rates arrives as (revenue-cost)/revenue, a ratio (-1 to 1); convert to percentages for display.
  const formatPercent = (value: number) => `${(value * 100).toFixed(2)}%`;
  const formatCurrency = (value: number) => t("common.currency", { value: value.toFixed(2) });
  const utilization = agent.utilization?.[0];

  return (
    <section className="agent-detail-section">
      <h3 className="agent-detail-section-title">
        {t(hostsTourists ? "tourismSupply.title" : "supply.title")}
      </h3>
      <div className="agent-detail-grid">
        {!hostsTourists && productName != null && (
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
              {t(labelKey("price"))}
            </span>
            <span className="agent-detail-value">
              {formatCurrency(agent.supply_price)}
            </span>
          </>
        )}

        {agent.supply_amount != null && (
          <>
            <span className="agent-detail-label">
              {t(labelKey("amount"))}
            </span>
            <span className="agent-detail-value">
              {formatAmount(agent.supply_amount)}
            </span>
          </>
        )}

        {agent.supply_sold_amount != null && (
          <>
            <span className="agent-detail-label">
              {t(labelKey("soldAmount"))}
            </span>
            <span className="agent-detail-value">
              <DataList
                values={agent.supply_sold_amount}
                formatter={formatAmount}
              />
            </span>
          </>
        )}

        {touristCapacity != null && (
          <>
            <span className="agent-detail-label">
              {t("tourismSupply.touristCapacity")}
            </span>
            <span className="agent-detail-value">
              {formatPersons(touristCapacity)}
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
