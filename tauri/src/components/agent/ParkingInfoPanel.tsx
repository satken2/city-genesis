/**
 * 建物の駐車容量と昼間・夜間の使用量を表示する。
 * 同じ車が昼は勤め先に、夜は自宅に停まるため、2帯を合算せず別々のメーターで示す。
 */

import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import { resolveIntlLocale } from "../../utils/locale";
import { parkingUsage } from "../../utils/parking";
import { Meter } from "../common/Meter";
import "./BuildingInfoPanel.css";

type ParkingInfoPanelProps = {
  agent: AgentData;
};

export function ParkingInfoPanel({ agent }: ParkingInfoPanelProps) {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);

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

  if (!showParking) return null;

  return (
    <section className="agent-detail-section">
      <h3 className="agent-detail-section-title">{t("building.parking")}</h3>
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
    </section>
  );
}
