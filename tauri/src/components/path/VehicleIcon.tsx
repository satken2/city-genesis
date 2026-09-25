/**
 * 車種アイコン(パス詳細の交通量リスト用)
 *
 * vehicle_types の車種IDごとに、24×24 の線画アイコンを返す。外部素材を使わない自作の図形で、
 * 色は currentColor に従う。道路を走る車両は横から見た姿、鉄道は線路の上に描いて区別する。
 * 未知の車種は汎用の丸印にフォールバックする。
 */

import type { ReactNode } from "react";
import {
  VEHICLE_BUS,
  VEHICLE_COMMERCIAL,
  VEHICLE_FREIGHT_TRAIN,
  VEHICLE_HIGH_SPEED_TRAIN,
  VEHICLE_METRO_TRAIN,
  VEHICLE_PASSANGER_TRAIN,
  VEHICLE_PEDESTRIAN,
  VEHICLE_STANDARD,
  VEHICLE_TRAM,
  VEHICLE_TRUCK,
} from "../../constants";

const RAIL = <path d="M1.5 21.5H22.5" />;

const VEHICLE_SHAPES: Record<number, ReactNode> = {
  [VEHICLE_PEDESTRIAN]: (
    <>
      <circle cx="12.5" cy="4" r="2" />
      <path d="M12 7.5L10.8 13.5L8.5 17L7.5 21.5M10.8 13.5L13.8 16.8L14.8 21.5M11.8 9L8.3 11.8M11.8 9L15.3 11.5" />
    </>
  ),
  [VEHICLE_STANDARD]: (
    <>
      <path d="M2.5 16.5V12.8Q2.5 11.8 3.5 11.6L6.2 11L8.8 7.6H15.2L18.6 11L20.6 11.5Q21.5 11.8 21.5 12.8V16.5Z" />
      <path d="M9.5 11H18" />
      <circle cx="7" cy="16.8" r="2" />
      <circle cx="17" cy="16.8" r="2" />
    </>
  ),
  [VEHICLE_TRUCK]: (
    <>
      <path d="M2 5.5H14V16.5H2Z" />
      <path d="M14 9H18.5L21.5 12.5V16.5H14" />
      <circle cx="6" cy="17.5" r="2" />
      <circle cx="17.5" cy="17.5" r="2" />
    </>
  ),
  [VEHICLE_BUS]: (
    <>
      <path d="M2 16.5V6.5Q2 5 3.5 5H19Q22 5 22 8V16.5Z" />
      <path d="M2 10.5H22M17.5 5V16.5" />
      <circle cx="6.5" cy="17.5" r="1.8" />
      <circle cx="17.5" cy="17.5" r="1.8" />
    </>
  ),
  [VEHICLE_COMMERCIAL]: (
    <>
      <path d="M2 16.5V8.5Q2 7 3.5 7H14.5L18.5 11L20.8 11.7Q22 12.1 22 13.3V16.5Z" />
      <path d="M13.5 7V11H18.5" />
      <circle cx="6.5" cy="16.8" r="2" />
      <circle cx="17" cy="16.8" r="2" />
    </>
  ),
  [VEHICLE_TRAM]: (
    <>
      <path d="M9 6.5L12 3L15 6.5M10 3H14" />
      <path d="M3 18V8.5Q3 6.5 5 6.5H19Q21 6.5 21 8.5V18Z" />
      <path d="M3 11.5H21" />
      {RAIL}
    </>
  ),
  [VEHICLE_METRO_TRAIN]: (
    <>
      <path d="M6 2.5H18Q20 2.5 20 4.5V15.5Q20 17.5 18 17.5H6Q4 17.5 4 15.5V4.5Q4 2.5 6 2.5Z" />
      <path d="M6.5 5.5H17.5V10.5H6.5Z" />
      <path d="M8 14H8.1M16 14H16.1" />
      <path d="M7 17.5L5 21.5M17 17.5L19 21.5" />
    </>
  ),
  [VEHICLE_PASSANGER_TRAIN]: (
    <>
      <path d="M2 17.5V7H19.5Q22 7 22 9.5V17.5Z" />
      <path d="M4.5 10H7.5M10 10H13M15.5 10H18.5" />
      {RAIL}
    </>
  ),
  [VEHICLE_FREIGHT_TRAIN]: (
    <>
      <path d="M1.5 17.5V8.5H11V17.5ZM13 17.5V8.5H22.5V17.5Z" />
      <path d="M4.5 8.5V17.5M8 8.5V17.5M16 8.5V17.5M19.5 8.5V17.5" />
      {RAIL}
    </>
  ),
  [VEHICLE_HIGH_SPEED_TRAIN]: (
    <>
      <path d="M1.5 17.5V9H12.5Q19 9 22.5 16.2Q22.9 17.5 21.5 17.5Z" />
      <path d="M4 12H14.5" />
      {RAIL}
    </>
  ),
};

const FALLBACK_SHAPE = <circle cx="12" cy="12" r="7" />;

type VehicleIconProps = {
  vehicleType: number;
  /** 読み上げとツールチップに使う車種名 */
  label: string;
};

export function VehicleIcon({ vehicleType, label }: VehicleIconProps) {
  return (
    <svg
      className="vehicle-icon"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.7"
      strokeLinecap="round"
      strokeLinejoin="round"
      role="img"
      aria-label={label}
    >
      <title>{label}</title>
      {VEHICLE_SHAPES[vehicleType] ?? FALLBACK_SHAPE}
    </svg>
  );
}
