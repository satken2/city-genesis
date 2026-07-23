/**
 * 需要情報コンポーネント
 *
 * 経済主体の商品ごとの需要量(demand_amount)・獲得量(received_amount)・
 * 在庫(remain_amount)を表形式で表示する。3値すべて 0 の商品行は表示しない。
 * マスターデータ(products)から商品名・単位を取得して表示に反映する。
 *
 * 獲得量は「当エポックにどれだけ手に入れたか」(市場調達・インフラ配送・車両配送・
 * 天然資源分配の合算。消費で減らないグロス入荷量)、在庫は「消費後に手元へ残っている量」
 * (工業原料はエポックをまたいで持ち越される)という別の量である点に注意。
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
