/**
 * Demand information component
 *
 * Displays demand_amount, received_amount, and remain_amount by product for the agent in a table.
 * Hides rows where all three are 0. Product names and units come from master data (products).
 *
 * Received amount is the gross amount obtained this epoch: market procurement, infrastructure and
 * vehicle deliveries, and natural resource distribution combined; consumption does not reduce it.
 * Inventory is the amount left after consumption (industrial raw materials carry over between epochs).
 * These are distinct quantities.
 */

import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import { useMasterData } from "../../contexts/MasterDataContext";
import "./DemandInfoPanel.css";

type DemandInfoPanelProps = {
  agent: AgentData;
};

export function DemandInfoPanel({ agent }: DemandInfoPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const demandAmount: Record<string, number> = agent.demand_amount ?? {};
  const receivedAmount: Record<string, number> = agent.received_amount ?? {};
  const acquiredAmount: Record<string, number> = agent.remain_amount ?? {};

  const productIds = new Set([
    ...Object.keys(demandAmount),
    ...Object.keys(receivedAmount),
    ...Object.keys(acquiredAmount),
  ]);

  const rows = Array.from(productIds)
    .map((id) => ({
      productId: id,
      demand: demandAmount[id] ?? 0,
      received: receivedAmount[id] ?? 0,
      acquired: acquiredAmount[id] ?? 0,
    }))
    .filter((row) => row.demand !== 0 || row.received !== 0 || row.acquired !== 0);

  if (rows.length === 0) {
    return null;
  }

  return (
    <section className="agent-detail-section">
      <h3 className="agent-detail-section-title">{t("demand.title", "需要情報")}</h3>
      <table className="demand-info-table">
        <thead>
          <tr>
            <th>{t("demand.product")}</th>
            <th className="demand-info-num">{t("demand.demandAmount")}</th>
            <th className="demand-info-num">{t("demand.receivedAmount")}</th>
            <th className="demand-info-num">{t("demand.acquiredAmount")}</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => {
            const product = masterData?.products[Number(row.productId)];
            const unit = product?.unit ?? "";
            const displayName =
              product?.display_name ?? String(row.productId);
            return (
              <tr key={row.productId}>
                <td>{displayName}</td>
                <td className="demand-info-num">
                  {row.demand !== 0 ? `${row.demand.toFixed(2)} ${unit}` : "-"}
                </td>
                <td className="demand-info-num">
                  {row.received !== 0 ? `${row.received.toFixed(2)} ${unit}` : "-"}
                </td>
                <td className="demand-info-num">
                  {row.acquired !== 0 ? `${row.acquired.toFixed(2)} ${unit}` : "-"}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </section>
  );
}
