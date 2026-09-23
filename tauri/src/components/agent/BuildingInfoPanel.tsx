/**
 * 建物情報コンポーネント
 *
 * 経済主体が建物(is_bd === true)の場合に表示される詳細情報パネル。
 * 建物タイプ・耐久度・階数・床面積などの基本情報テーブルと、
 * 建物の断面図(BuildingDiagram)を左右に並べて表示する。
 *
 * 修繕中(under_maintenance === true)の場合は、
 * パネル上部に黄色い工事中バッジを表示する。
 *
 * 駐車容量は昼間(通勤・通学・来客)と夜間(居住世帯の保有車)の使用量を別々のメーターで出す。
 * 同じ車が昼は勤め先に、夜は自宅に停まるため、2帯を合算すると実際には起きない需要になる。
 */

import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import { useMasterData } from "../../contexts/MasterDataContext";
import { resolveIntlLocale } from "../../utils/locale";
import { parkingUsage } from "../../utils/parking";
import { Meter } from "../common/Meter";
import { BuildingDiagram } from "./BuildingDiagram";
import "./BuildingInfoPanel.css";

type BuildingInfoPanelProps = {
  agent: AgentData;
};

export function BuildingInfoPanel({ agent }: BuildingInfoPanelProps) {
  const { t, i18n } = useTranslation();
  const masterData = useMasterData();
  const locale = resolveIntlLocale(i18n.language);

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

  // 建物1棟の台数は整数で表示する。容量0で昼夜とも使用が無い建物(敷地いっぱいの農地など)は出さない。
  const parkingRows =
    agent.parking_capacity != null
      ? [
          { key: "day", label: t("building.parkingDay"), usage: parkingUsage(agent.parking_use_day ?? 0, agent.parking_capacity, 0) },
          { key: "night", label: t("building.parkingNight"), usage: parkingUsage(agent.parking_use_night ?? 0, agent.parking_capacity, 0) },
        ]
      : [];
  const parkingCapacity = parkingRows[0]?.usage.capacity ?? 0;
  const showParking = parkingRows.some((row) => row.usage.capacity > 0 || row.usage.used > 0);

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

          {showParking && (
            <div className="building-parking">
              <div className="building-parking-header">
                <span className="building-parking-title">{t("building.parkingCapacity")}</span>
                <span className="building-parking-capacity">
                  {t("building.parkingCapacityValue", { capacity: parkingCapacity.toLocaleString(locale) })}
                </span>
              </div>
              <div className="building-parking-grid">
                {parkingRows.map((row) => (
                  <div key={row.key} className="building-parking-row">
                    <span className="building-parking-label">{row.label}</span>
                    <Meter value={row.usage.ratio} polarity="negative" />
                    <span className="building-parking-value">
                      {t("building.parkingUsage", {
                        used: row.usage.used.toLocaleString(locale),
                        capacity: row.usage.capacity.toLocaleString(locale),
                      })}
                    </span>
                  </div>
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
