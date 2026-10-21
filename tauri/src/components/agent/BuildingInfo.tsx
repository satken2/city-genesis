/**
 * Floor composition component
 *
 * Displays the building cross-section on the right. For TYPE_BUILDING buildings, the left
 * shows rent, occupied area by floor type, and the tenant list (labeled as guests for hotels).
 * Reuses agent-detail-section styles to match the basic information section when displaying
 * building-specific information.
 */

import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import { SPACE_LODGING, TYPE_BUILDING } from "../../constants";
import { AgentLink } from "../common/AgentLink";
import { SpaceAmountChart } from "../common/SpaceAmountChart";
import { BuildingDiagram } from "./BuildingDiagram";
import "./BuildingInfoPanel.css";

type BuildingInfoProps = {
  agent: AgentData;
  /** Navigation callback when an agent link is clicked. */
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
  const isHotel = (agent.capacity?.[SPACE_LODGING] ?? 0) > 0;
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
              <span className="agent-detail-label">
                {isHotel ? t("agent.guests") : t("agent.tenants")}
              </span>
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
