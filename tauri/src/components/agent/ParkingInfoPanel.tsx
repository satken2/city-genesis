/**
 * Display building parking capacity and daytime/nighttime use.
 * The same car parks at work by day and at home by night, so show separate meters rather than sum them.
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

  // Show per-building vehicle counts as integers. Omit buildings with zero capacity and no use in either period (e.g. farmland filling the site).
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
