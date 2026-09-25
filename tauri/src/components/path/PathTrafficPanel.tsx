/**
 * パスの交通・速度設定セクション
 *
 * パス詳細の基本情報タブに置く、制限速度と交通の状況をまとめたセクション。
 * - 制限速度・通過速度: PathSpeedLimitEditor(道路標識+都市の値+この道路の入力欄。入力は自動で反映する)
 * - 利用率・路上駐車量: 横並びのドーナツ。どちらも値が大きいほど逼迫している指標なので色は negative の向き。
 *   路上駐車は台数が容量を超えうる(建物の駐車場からあふれた車が停まる)ので実数を添え、
 *   容量を持つ道路(稼働中の地上の生活道路)と実際に車が停まっている道路だけに出す
 * - 交通量(1日あたり): 車種をアイコンで示した1本のリスト。棒の長さはリスト内の最大値に対する比
 *
 * 制限速度は PATH_TYPES_WITH_SPEED_LIMIT、通過速度は PATH_TYPES_WITH_BASE_SPEED、
 * 利用率は PATH_TYPES_WITH_UTILIZATION の種別だけに出す(それ以外の種別では意味を持たない値のため)。
 * 表示する項目が1つも無ければセクションごと出さない。
 */

import { useTranslation } from "react-i18next";
import type { PathData } from "../../types";
import { useMasterData } from "../../contexts/MasterDataContext";
import { resolveIntlLocale } from "../../utils/locale";
import { parkingUsage } from "../../utils/parking";
import {
  PATH_TYPES_WITH_BASE_SPEED,
  PATH_TYPES_WITH_SPEED_LIMIT,
  PATH_TYPES_WITH_UTILIZATION,
} from "../../local-constants";
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
  VEHICLE_TO_TRAFFIC,
  VEHICLE_TRAM,
} from "../../constants";
import { PathSpeedLimitEditor } from "./PathSpeedLimitEditor";
import { VehicleIcon } from "./VehicleIcon";
import "./PathTrafficPanel.css";

/**
 * 交通量リストに常に並べる車種(交通量が0でも行を出し、道路どうしで見比べやすくする)。
 * ここに無い車種は交通量があるときだけ出す。LRTは一部のバリアント(LRT線路付きの街路・
 * 歩行者専用道路・LRT専用軌道)にしか走らないため、常設の行には含めない。
 */
function listedVehicleKeys(pathType: number): number[] {
  if (pathType === PATH_STREET || pathType === PATH_MOTORWAY) {
    return [...PEDESTRIAN_VEHICLE_KEYS, ...ROAD_VEHICLE_KEYS.filter((key) => key !== VEHICLE_TRAM)];
  }
  if (pathType === PATH_WALKWAY || pathType === PATH_CONVEYOR || pathType === PATH_TRANSPORT) {
    return [...PEDESTRIAN_VEHICLE_KEYS];
  }
  if (pathType === PATH_TRACK || pathType === PATH_MONORAIL) {
    return [...RAIL_VEHICLE_KEYS];
  }
  return [];
}

/** 交通カテゴリ(徒歩・道路・鉄道)ごとのアイコンと棒の色 */
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

  const showSpeedLimit = PATH_TYPES_WITH_SPEED_LIMIT.includes(path.path_type);
  const showUtilization = path.utilization != null && PATH_TYPES_WITH_UTILIZATION.includes(path.path_type);

  // 路上駐車は道路1本あたりの台数が小さく端数が意味を持つため、小数1桁で表示する
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

  if (!showSpeedLimit && !showUtilization && !streetParking && trafficRows.length === 0) return null;

  const vehicleName = (vehicleType: number) =>
    t(`path.vehicles.${vehicleType}`, {
      defaultValue: masterData?.vehicle_types?.[vehicleType]?.display_name ?? `ID:${vehicleType}`,
    });

  return (
    <section className="agent-detail-section path-traffic-section">
      <h3 className="agent-detail-section-title">{t("path.trafficSettings")}</h3>

      {showSpeedLimit && (
        <PathSpeedLimitEditor
          path={path}
          apiBaseUrl={apiBaseUrl}
          showBaseSpeed={PATH_TYPES_WITH_BASE_SPEED.includes(path.path_type)}
          onSaved={onSaved}
        />
      )}

      {(showUtilization || streetParking) && (
        <div className="path-traffic-gauges">
          {showUtilization && path.utilization != null && (
            <DonutGauge value={path.utilization} label={t("path.utilization")} polarity="negative" />
          )}
          {streetParking && (
            <DonutGauge
              value={streetParking.ratio}
              label={t("path.streetParking")}
              polarity="negative"
              detail={t("path.streetParkingUsage", {
                parked: formatParkingCars(streetParking.used),
                capacity: formatParkingCars(streetParking.capacity),
              })}
            />
          )}
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
                  {/* 交通量は日次の実数(端数あり)なので小数1桁で丸めて単位を付ける */}
                  <span className="path-traffic-row__count">
                    {count.toLocaleString(locale, { maximumFractionDigits: 1 })}
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
