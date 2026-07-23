/**
 * フロアタイプごとの占有量を示す横棒グラフコンポーネント
 *
 * 建物のキャパシティ全体を1本の横棒で表現し、
 * スペースタイプ(住居・商業・オフィス等)ごとに色分けして表示する。
 *
 * 描画の仕組み:
 * - 薄い色の背景: キャパシティ(最大収容可能量)を示す
 * - 濃い色の前景: 実際の占有量を示す
 * - 占有部分は高さを少し下げて描画する。
 *   これにより、100%占有時でも「すべて塗りつぶされている(=満室)」のか
 *   「まったく塗りつぶされていない(=空室)」のかが視覚的に区別できる。
 */

import { useTranslation } from "react-i18next";
import "./FloorOccupancyBar.css";

/**
 * スペースタイプごとのグラフ描画色
 * light はキャパシティ(背景)用の薄い色、dark は占有部分(前景)用の濃い色
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
  /** スペースタイプごとのキャパシティ(キーはスペースタイプIDの文字列表現) */
  capacity: Record<string, number>;
  /** スペースタイプごとの占有量(キーはスペースタイプIDの文字列表現) */
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
