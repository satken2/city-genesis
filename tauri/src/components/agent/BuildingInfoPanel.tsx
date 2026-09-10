/**
 * 建物情報コンポーネント
 *
 * 経済主体が建物(is_bd === true)の場合に表示される詳細情報パネル。
 * 建物タイプ・耐久度・階数・床面積などの基本情報テーブルと、
 * 建物の断面図(BuildingDiagram)を左右に並べて表示する。
 *
 * 修繕中(under_maintenance === true)の場合は、
 * パネル上部に黄色い工事中バッジを表示する。
 */

import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import { useMasterData } from "../../contexts/MasterDataContext";
import { Meter } from "../common/Meter";
import { BuildingDiagram } from "./BuildingDiagram";
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

  // 構造種別(0=構造の区別なし。農地・変電所などは行自体を出さない)
  const structureName =
    agent.structure != null && agent.structure > 0
      ? (masterData?.structures?.[agent.structure]?.display_name ?? String(agent.structure))
      : null;

  const hasDiagramData =
    agent.capacity &&
    Object.keys(agent.capacity).length > 0 &&
    agent.height != null &&
    agent.base_area != null;

  return (
    <section className="agent-detail-section building-info-section">
      <h3 className="agent-detail-section-title">{t("building.info", "建物情報")}</h3>
      {agent.under_maintenance && (
        <div className="maintenance-badge">
          {t("building.underMaintenance")}
        </div>
      )}

      <div className="building-info-content">
        <div className="building-info-table">
          <div className="agent-detail-grid">
            <span className="agent-detail-label">
              {t("building.type")}
            </span>
            <span className="agent-detail-value">{bdTypeName}</span>

            {structureName != null && (
              <>
                <span className="agent-detail-label">
                  {t("building.structure")}
                </span>
                <span className="agent-detail-value">{structureName}</span>
              </>
            )}

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
