/**
 * 供給情報コンポーネント
 *
 * 経済主体がTYPE_STANDARDの場合に表示される供給情報セクション。
 * 供給製品名・供給価格・供給量・販売量・利益率をグリッド形式で表示する。
 * 製品名と単位はマスターデータ(products)から取得する。
 * 販売量(supply_sold_amount)と利益率(profit_rates)はDataListで表示し、履歴推移を確認できる。
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

  // profit_rates は (収益-費用)/収益 の比率(-1〜1)で届くため、表示時に百分率へ換算する
  const formatPercent = (value: number) => `${(value * 100).toFixed(2)}%`;
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
              {/* 利益率の意味域は±100%。過去セーブの履歴にはクランプ前の外れ値
                  (例: -895)が残り得るため、チャート描画のみクランプする */}
              <DataList
                values={agent.profit_rates}
                formatter={formatPercent}
                clampRange={[-1, 1]}
              />
            </span>
          </>
        )}
      </div>
    </section>
  );
}
