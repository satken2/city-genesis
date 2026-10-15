/**
 * 建物情報コンポーネント
 *
 * 経済主体が建物(is_bd === true)の場合に表示される詳細情報パネル。
 * 建物タイプ・耐久度・階数・床面積などの基本情報を表示する。
 *
 * 修繕中(under_maintenance === true)の場合は、
 * パネル上部に黄色い工事中バッジを表示する。
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
