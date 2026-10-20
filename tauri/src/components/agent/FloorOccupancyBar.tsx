/**
 * Horizontal bar chart of occupied area by floor type
 *
 * Represents full building capacity in one horizontal bar, colored by space type
 * (residential, commercial, office, etc.).
 *
 * Rendering:
 * - Pale background: capacity (maximum available area).
 * - Dark foreground: actual occupied area.
 * - The occupied portion is slightly shorter vertically so a fully filled bar (fully occupied)
 *   remains distinguishable from an entirely unfilled bar (vacant), even at 100% occupancy.
 */

import { useTranslation } from "react-i18next";
import "./FloorOccupancyBar.css";

/**
 * Chart colors for each space type.
 * light is the pale capacity background; dark is the occupied-area foreground.
 */
const SPACE_COLORS: Record<string, { light: string; dark: string }> = {
  "0": {
    light: "rgba(156, 163, 175, 0.25)",
    dark: "rgba(156, 163, 175, 0.8)",
  },
  "1": {
    light: "rgba(59, 130, 246, 0.25)",
    dark: "rgba(59, 130, 246, 0.8)",
  },
  "2": {
    light: "rgba(249, 115, 22, 0.25)",
    dark: "rgba(249, 115, 22, 0.8)",
  },
  "3": {
    light: "rgba(168, 85, 247, 0.25)",
    dark: "rgba(168, 85, 247, 0.8)",
  },
  "4": {
    light: "rgba(20, 184, 166, 0.25)",
    dark: "rgba(20, 184, 166, 0.8)",
  },
  "5": {
    light: "rgba(34, 197, 94, 0.25)",
    dark: "rgba(34, 197, 94, 0.8)",
  },
};

const DEFAULT_COLOR = {
  light: "rgba(156, 163, 175, 0.25)",
  dark: "rgba(156, 163, 175, 0.8)",
};

type FloorOccupancyBarProps = {
  /** Capacity by space type (keys are string representations of space type IDs). */
  capacity: Record<string, number>;
  /** Occupied area by space type (keys are string representations of space type IDs). */
  occupied: Record<string, number>;
};

export function FloorOccupancyBar({ capacity, occupied }: FloorOccupancyBarProps) {
  const { t } = useTranslation();

  const totalCapacity = Object.values(capacity).reduce(
    (sum, v) => sum + v,
    0,
  );
  if (totalCapacity === 0) return null;

  const types = Object.keys(capacity).filter((k) => capacity[k] > 0);

  return (
    <div className="floor-occupancy">
      <div className="floor-occupancy__bar">
        {types.map((type) => {
          const cap = capacity[type];
          const occ = occupied[type] ?? 0;
          const widthPct = (cap / totalCapacity) * 100;
          const colors = SPACE_COLORS[type] ?? DEFAULT_COLOR;
          const occWidthPct = cap > 0 ? (occ / cap) * 100 : 0;

          return (
            <div
              key={type}
              className="floor-occupancy__segment"
              style={{
                width: `${widthPct}%`,
                backgroundColor: colors.light,
              }}
            >
              {occ > 0 && (
                <div
                  className="floor-occupancy__filled"
                  style={{
                    width: `${occWidthPct}%`,
                    backgroundColor: colors.dark,
                  }}
                >
                  <span className="floor-occupancy__label">
                    {t("common.sqm", { value: occ })}
                  </span>
                </div>
              )}
            </div>
          );
        })}
      </div>
      <div className="floor-occupancy__legend">
        {types.map((type) => {
          const colors = SPACE_COLORS[type] ?? DEFAULT_COLOR;
          return (
            <span key={type} className="floor-occupancy__legend-item">
              <span
                className="floor-occupancy__legend-swatch"
                style={{ backgroundColor: colors.dark }}
              />
              {t(`spaceType.${type}`, { defaultValue: `Type ${type}` })}
            </span>
          );
        })}
      </div>
    </div>
  );
}
