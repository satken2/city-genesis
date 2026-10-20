/**
 * Building information component
 *
 * Detail panel for building agents (is_bd === true).
 * Displays building type, durability, floor count, floor area, and other basic information.
 * When under repair (under_maintenance === true), shows a yellow construction badge at the top.
 */

import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import { useMasterData } from "../../contexts/MasterDataContext";
import { Meter } from "../common/Meter";
import "./BuildingInfoPanel.css";

type BuildingInfoPanelProps = {
  agent: AgentData;
};

export function BuildingInfoPanel({ agent }: BuildingInfoPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const bdTypeName =
    masterData?.bd_types[agent.bd_type]?.display_name ??
    String(agent.bd_type);

  return (
    <section className="agent-detail-section building-info-section">
      <h3 className="agent-detail-section-title">{t("building.info", "建物情報")}</h3>
      {agent.under_maintenance && (
        <div className="maintenance-badge">
          {t("building.underMaintenance")}
        </div>
      )}

      <div className="agent-detail-grid">
        <span className="agent-detail-label">
          {t("building.type")}
        </span>
        <span className="agent-detail-value">{bdTypeName}</span>

        {agent.floors != null && (
          <>
            <span className="agent-detail-label">
              {t("building.floors")}
            </span>
            <span className="agent-detail-value">
              {t("building.floorsValue", {
                floors: agent.floors,
                area: Math.round(agent.floor_area ?? 0),
              })}
            </span>
          </>
        )}
      </div>

      {agent.durability != null && (
        <div className="building-info-meter">
          <Meter
            label={t("building.durability")}
            value={agent.durability}
          />
        </div>
      )}
    </section>
  );
}
