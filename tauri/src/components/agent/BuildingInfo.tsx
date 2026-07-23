/**
 * 建物情報コンポーネント
 *
 * agent.type が TYPE_BUILDING の場合に表示する建物固有の詳細情報。
 * 家賃・フロアタイプごとの占有量・入居テナント一覧を表示する。
 *
 * agent-detail-section のスタイルを流用し、
 * 基本情報セクションと同じ雰囲気で建物固有の情報を表示する。
 */

import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import { AgentLink } from "../common/AgentLink";
import { SpaceAmountChart } from "../common/SpaceAmountChart";

type BuildingInfoProps = {
  agent: AgentData;
  /** 経済主体リンク押下時のナビゲーションコールバック */
  onNavigate: (entityId: string) => void;
};

export function BuildingInfo({ agent, onNavigate }: BuildingInfoProps) {
  const { t } = useTranslation();

  const formatCurrency = (value: number) =>
    t("common.currency", { value });

  const hasRent = agent.rent != null;
  const hasOccupancy =
    agent.capacity != null && agent.occupied != null;
  const hasTenants = agent.tenants != null && agent.tenants.length > 0;

  if (!hasRent && !hasOccupancy && !hasTenants) return null;

  return (
    <section className="agent-detail-section">
      <h3 className="agent-detail-section-title">{t("building.details", "建物詳細")}</h3>
      {hasRent && (
        <div className="agent-detail-grid">
          <span className="agent-detail-label">{t("agent.rent")}</span>
          <span className="agent-detail-value">
            {formatCurrency(agent.rent!)}
          </span>
        </div>
      )}

      {hasOccupancy && (
        <div className="building-info-field">
          <span className="agent-detail-label">
            {t("agent.floorOccupancy")}
          </span>
          <SpaceAmountChart
            supplies={agent.capacity!}
            demands={agent.occupied!}
            supplyLabel={t("agent.capacity", "キャパシティ")}
            demandLabel={t("agent.occupied", "占有量")}
          />
        </div>
      )}

      {hasTenants && (
        <div className="building-info-field">
          <span className="agent-detail-label">{t("agent.tenants")}</span>
          <div className="building-info-tenants">
            {agent.tenants!.map((tenant) => (
              <AgentLink
                key={tenant.id}
                agentId={tenant.id}
                agentName={tenant.name ?? ""}
                onNavigate={onNavigate}
              />
            ))}
          </div>
        </div>
      )}
    </section>
  );
}
