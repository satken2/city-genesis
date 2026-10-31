/**
 * Path traffic/speed settings section.
 *
 * Combines direction, speed limits, and traffic conditions in path detail's basic information tab.
 * - Direction: track/monorail direction toggle. Combined double-track paths are fixed to both directions.
 * - Speed limit/passing speed: PathSpeedLimitEditor (road sign + city value + this road's input, applied automatically).
 * - On-street parking: a donut colored by congestionColor. Parking counts can exceed capacity (overflow from
 *   building parking), so show actual counts too. Show only for roads with capacity (active ground-level local
 *   roads) or roads with parked vehicles. Utilization is a meter in the basic information section (PathConditionPanel).
 * - Daily traffic: one icon-based vehicle list, with bar lengths relative to the list maximum.
 *
 * Show speed limits and passing speeds only for PATH_TYPES_WITH_SPEED_LIMIT, since other types have no
 * meaningful values.
 * Hide the section if no items apply.
 */

import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { PathData } from "../../types";
import { useMasterData } from "../../contexts/MasterDataContext";
import { resolveIntlLocale } from "../../utils/locale";
import { parkingUsage } from "../../utils/parking";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { PATH_TYPES_WITH_SPEED_LIMIT } from "../../local-constants";
import { DonutGauge } from "../common/DonutGauge";
import {
  PATH_CONVEYOR,
  PATH_MONORAIL,
  PATH_MOTORWAY,
  PATH_STREET,
  PATH_TRACK,
  PATH_TRANSPORT,
  PATH_WALKWAY,
  PEDESTRIAN_VEHICLE_KEYS,
  RAIL_VEHICLE_KEYS,
  ROAD_VEHICLE_KEYS,
  TRAFFIC_PEDESTRIAN,
  TRAFFIC_RAIL,
  VEHICLE_MONORAIL,
  VEHICLE_TO_TRAFFIC,
  VEHICLE_TRAM,
  STATUS_ACTIVE,
} from "../../constants";
import { PathDirectionButton } from "./PathDirectionButton";
import { cycleDirection, isMultiTrackRailVariant } from "./railBuild";
import { PathSpeedLimitEditor } from "./PathSpeedLimitEditor";
import { VehicleIcon } from "./VehicleIcon";
import "./PathTrafficPanel.css";

// The API caps overflowing traffic at the float16 maximum; show capped values as lower bounds.
const TRAFFIC_DISPLAY_LIMIT = 65504;

/**
 * Vehicle types always included in traffic lists (keep zero rows to compare roads).
 * Other types appear only with traffic. LRT operates on only some variants (streets with LRT tracks, pedestrian-only
 * roads, dedicated LRT tracks), and highways have no pedestrians, so omit them from always-present rows as appropriate.
 * Only monorail vehicles use monorail tracks; monorail vehicles do not use railway tracks.
 */
function listedVehicleKeys(pathType: number): number[] {
  const roadVehicles = ROAD_VEHICLE_KEYS.filter((key) => key !== VEHICLE_TRAM);
  if (pathType === PATH_STREET) {
    return [...PEDESTRIAN_VEHICLE_KEYS, ...roadVehicles];
  }
  if (pathType === PATH_MOTORWAY) {
    return roadVehicles;
  }
  if (pathType === PATH_WALKWAY || pathType === PATH_CONVEYOR || pathType === PATH_TRANSPORT) {
    return [...PEDESTRIAN_VEHICLE_KEYS];
  }
  if (pathType === PATH_TRACK) {
    return RAIL_VEHICLE_KEYS.filter((key) => key !== VEHICLE_MONORAIL);
  }
  if (pathType === PATH_MONORAIL) {
    return [VEHICLE_MONORAIL];
  }
  return [];
}

/**
 * Colors for usage relative to capacity (on-street parking, utilization). Up to half has spare capacity; above 80%
 * is pressured (same thresholds as UsageMeter in statistics/tabs/AccidentsTab for road congestion).
 */
export function congestionColor(ratio: number): string {
  if (ratio > 0.8) return "#ef4444";
  if (ratio > 0.5) return "#f59e0b";
  return "#10b981";
}

/** Icons and bar colors by traffic category (walking/road/rail). */
function trafficCategoryModifier(vehicleType: number): string {
  const category = VEHICLE_TO_TRAFFIC[vehicleType];
  if (category === TRAFFIC_PEDESTRIAN) return "pedestrian";
  if (category === TRAFFIC_RAIL) return "rail";
  return "road";
}

type PathTrafficPanelProps = {
  path: PathData;
  apiBaseUrl: string;
  onSaved: (updated: PathData) => void;
};

export function PathTrafficPanel({ path, apiBaseUrl, onSaved }: PathTrafficPanelProps) {
  const { t, i18n } = useTranslation();
  const masterData = useMasterData();
  const locale = resolveIntlLocale(i18n.language);
  const [directionSaving, setDirectionSaving] = useState(false);
  const [directionError, setDirectionError] = useState<string | null>(null);

  const showDirection = path.path_type === PATH_TRACK || path.path_type === PATH_MONORAIL;
  const directionFixed = !masterData || isMultiTrackRailVariant(masterData.paths[path.path_variant]);

  const changeDirection = async () => {
    if (directionSaving || directionFixed || path.status !== STATUS_ACTIVE) return;
    setDirectionSaving(true);
    setDirectionError(null);
    try {
      const updated = await fetchApi<PathData>(buildApiUrl(apiBaseUrl, "/api/v1/paths/{id}", String(path.path_id)), {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ travel_direction: cycleDirection(path.travel_direction) }),
      });
      onSaved(updated);
    } catch (err) {
      setDirectionError(err instanceof Error ? err.message : t("common.fetchError"));
    } finally {
      setDirectionSaving(false);
    }
  };

  const showSpeedLimit = PATH_TYPES_WITH_SPEED_LIMIT.includes(path.path_type);

  // Show on-street parking to one decimal because per-road counts are small and fractions matter.
  const parkingOnPath =
    path.street_parking_capacity != null
      ? parkingUsage(path.parked_cars ?? 0, path.street_parking_capacity, 1)
      : null;
  const streetParking =
    parkingOnPath && (parkingOnPath.capacity > 0 || parkingOnPath.used > 0) ? parkingOnPath : null;
  const formatParkingCars = (value: number) =>
    value.toLocaleString(locale, { minimumFractionDigits: 1, maximumFractionDigits: 1 });

  const trafficCounts = path.path_traffic ?? {};
  const vehicleKeys = new Set(listedVehicleKeys(path.path_type));
  for (const [key, count] of Object.entries(trafficCounts)) {
    if (count > 0) vehicleKeys.add(Number(key));
  }
  const trafficRows = [...vehicleKeys]
    .sort((a, b) => a - b)
    .map((vehicleType) => ({ vehicleType, count: trafficCounts[String(vehicleType)] ?? 0 }));
  const maxTraffic = Math.max(0, ...trafficRows.map((row) => row.count));

  if (!showDirection && !showSpeedLimit && !streetParking && trafficRows.length === 0) return null;

  const vehicleName = (vehicleType: number) =>
    t(`path.vehicles.${vehicleType}`, {
      defaultValue: masterData?.vehicle_types?.[vehicleType]?.display_name ?? `ID:${vehicleType}`,
    });

  return (
    <section className="agent-detail-section path-traffic-section">
      <h3 className="agent-detail-section-title">{t("path.trafficSettings")}</h3>

      {showDirection && (
        <div className="path-traffic-direction">
          <span className="agent-detail-label">{t("path.travelDirection")}</span>
          <PathDirectionButton direction={path.travel_direction}
            disabled={directionSaving || directionFixed || path.status !== STATUS_ACTIVE}
            onClick={() => void changeDirection()} />
        </div>
      )}
      {directionError && <div className="agent-detail-error" role="alert">{directionError}</div>}

      {showSpeedLimit && (
        <PathSpeedLimitEditor path={path} apiBaseUrl={apiBaseUrl} onSaved={onSaved} />
      )}

      {streetParking && (
        <div className="path-traffic-gauges">
          <DonutGauge
            value={streetParking.ratio}
            label={t("path.streetParking")}
            color={congestionColor(streetParking.ratio)}
            detail={t("path.streetParkingUsage", {
              parked: formatParkingCars(streetParking.used),
              capacity: formatParkingCars(streetParking.capacity),
            })}
          />
        </div>
      )}

      {trafficRows.length > 0 && (
        <div className="path-traffic-list-block">
          <span className="path-traffic-list-title">{t("path.trafficByVehicle")}</span>
          <ul className="path-traffic-list">
            {trafficRows.map(({ vehicleType, count }) => {
              const name = vehicleName(vehicleType);
              const modifier = trafficCategoryModifier(vehicleType);
              const share = maxTraffic > 0 ? (count / maxTraffic) * 100 : 0;
              return (
                <li
                  key={vehicleType}
                  className={`path-traffic-row path-traffic-row--${modifier}${count > 0 ? "" : " path-traffic-row--zero"}`}
                  title={name}
                >
                  <span className="path-traffic-row__icon">
                    <VehicleIcon vehicleType={vehicleType} label={name} />
                  </span>
                  <span className="path-traffic-row__bar">
                    <span className="path-traffic-row__fill" style={{ width: `${share}%` }} />
                  </span>
                  {/* Daily traffic values include fractions, so round to one decimal and append units. */}
                  <span className="path-traffic-row__count">
                    {count >= TRAFFIC_DISPLAY_LIMIT
                      ? t("path.trafficAtLeast", { count: TRAFFIC_DISPLAY_LIMIT.toLocaleString(locale) })
                      : count.toLocaleString(locale, { maximumFractionDigits: 1 })}
                    {t("path.trafficUnitPerDay")}
                  </span>
                </li>
              );
            })}
          </ul>
        </div>
      )}
    </section>
  );
}
