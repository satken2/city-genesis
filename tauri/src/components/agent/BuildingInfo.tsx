/**
 * フロア構成コンポーネント
 *
 * 建物の断面図を右側に表示する。TYPE_BUILDING の建物では、
 * 左側に家賃・フロアタイプごとの占有量・入居テナント一覧を表示する。
 *
 * agent-detail-section のスタイルを流用し、
 * 基本情報セクションと同じ雰囲気で建物固有の情報を表示する。
 */

import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import { TYPE_BUILDING } from "../../constants";
import { AgentLink } from "../common/AgentLink";
import { SpaceAmountChart } from "../common/SpaceAmountChart";
import { BuildingDiagram } from "./BuildingDiagram";
import "./BuildingInfoPanel.css";

type BuildingInfoProps = {
  agent: AgentData;
  /** 経済主体リンク押下時のナビゲーションコールバック */
  onNavigate: (entityId: string) => void;
};

export function BuildingInfo({ agent, onNavigate }: BuildingInfoProps) {
  const { t } = useTranslation();

  const formatCurrency = (value: number) =>
    t("common.currency", { value });

  const hasFloorDetails = agent.type === TYPE_BUILDING;
  const hasRent = hasFloorDetails && agent.rent != null;
  const hasOccupancy =
    hasFloorDetails && agent.capacity != null && agent.occupied != null;
  const hasTenants = hasFloorDetails && agent.tenants != null && agent.tenants.length > 0;
  const hasDiagramData =
    agent.is_bd &&
    agent.capacity != null &&
    Object.keys(agent.capacity).length > 0 &&
    agent.height != null &&
    agent.base_area != null;

  if (!hasRent && !hasOccupancy && !hasTenants && !hasDiagramData) return null;

  return (
    <section className="agent-detail-section">
      <h3 className="agent-detail-section-title">{t("building.floorConfiguration")}</h3>
      <div className="building-info-content">
        <div className="building-info-table">
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
        </div>

        {hasDiagramData && (
          <div className="building-info-diagram">
            <BuildingDiagram
              capacity={agent.capacity!}
              height={agent.height!}
              baseArea={agent.base_area!}
            />
          </div>
        )}
      </div>
    </section>
  );
}
