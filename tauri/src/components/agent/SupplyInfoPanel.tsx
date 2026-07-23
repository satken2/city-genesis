/**
 * 供給情報コンポーネント
 *
 * 経済主体がTYPE_STANDARDの場合に表示される供給情報セクション。
 * 供給製品名・供給量・供給価格・生産量%・利益率をグリッド形式で表示する。
 * 製品名と単位はマスターデータ(products)から取得する。
 * 利益率(profit_rates)はDataListで表示し、履歴推移を確認できる。
 */

import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import { useMasterData } from "../../contexts/MasterDataContext";
import { DataList } from "../common/DataList";

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

  const formatPercent = (value: number) => `${value.toFixed(2)}%`;
  const formatCurrency = (value: number) => t("common.currency", { value: value.toFixed(2) });

  return (
    <section className="agent-detail-section">
      <h3 className="agent-detail-section-title">{t("supply.title", "供給情報")}</h3>
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
            <span className="agent-detail-value">
              <DataList
                values={agent.profit_rates}
                formatter={formatPercent}
              />
            </span>
          </>
        )}
      </div>
    </section>
  );
}
