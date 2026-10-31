/**
 * Path condition meters (utilization, durability, noise, air pollution).
 *
 * Rows inside the path detail basic information section, drawn like the traffic-by-vehicle rows
 * (label, bar, value). Bar colors show the level of each value.
 * - Utilization: use relative to capacity, colored by congestionColor like the on-street parking donut. Only for
 *   PATH_TYPES_WITH_UTILIZATION; other types always have zero utilization.
 * - Durability: 0–1, colored over five 20% steps. Display the value as a percentage.
 * - Noise/air pollution: unbounded values determined by traffic. The bar is the fraction of the saturation level
 *   above which living environment evaluation considers the condition worst (NOISE_SATURATION_LEVEL /
 *   POLLUTION_SATURATION_LEVEL in GENERAL_PARAMS), colored over four levels. Display raw values.
 *   If saturation levels are unavailable, leave the bar empty.
 *
 * Show a construction badge above meters during maintenance. Render nothing if no items are available.
 */

import { useEffect, useState, type CSSProperties } from "react";
import { useTranslation } from "react-i18next";
import type { PathData } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { resolveIntlLocale } from "../../utils/locale";
import { PATH_TYPES_WITH_UTILIZATION } from "../../local-constants";
import { congestionColor } from "./PathTrafficPanel";
// Meter rows reuse the traffic-by-vehicle row styles.
import "./PathTrafficPanel.css";
import "./PathConditionPanel.css";

/** Fields used here from GET /api/v1/parameters. */
type ParametersResponse = {
  parameters?: { GENERAL_PARAMS?: Record<string, unknown> };
};

type SaturationLevels = { noise: number | null; pollution: number | null };

/** Durability thresholds for levels 0, 1, 2, 3. Below all thresholds is the worst level. */
const DURABILITY_THRESHOLDS = [0.8, 0.6, 0.4, 0.2];
const DURABILITY_COLORS = ["#22c55e", "#84cc16", "#eab308", "#f97316", "#ef4444"];
const EMISSION_COLORS = ["#22c55e", "#84cc16", "#f59e0b", "#ef4444"];
const UNKNOWN_COLOR = "#93a9c0";

function durabilityColor(durability: number): string {
  const index = DURABILITY_THRESHOLDS.findIndex((threshold) => durability >= threshold);
  return DURABILITY_COLORS[index === -1 ? DURABILITY_THRESHOLDS.length : index];
}

/** Noise/air pollution level from saturation fractions. Near zero is level 0. */
function emissionLevel(ratio: number): number {
  if (ratio < 0.01) return 0;
  if (ratio < 1 / 3) return 1;
  if (ratio < 2 / 3) return 2;
  return 3;
}

function positiveNumberOrNull(value: unknown): number | null {
  return typeof value === "number" && value > 0 ? value : null;
}

function hasUtilization(path: PathData): boolean {
  return path.utilization != null && PATH_TYPES_WITH_UTILIZATION.includes(path.path_type);
}

/** Whether PathConditionPanel renders anything (the caller decides whether to show the section). */
export function hasPathCondition(path: PathData): boolean {
  return (
    !!path.under_maintenance ||
    hasUtilization(path) ||
    path.durability != null ||
    path.noise != null ||
    path.air_pollution != null
  );
}

type MeterRowProps = {
  label: string;
  /** Bar length. Clamped to 0–1. */
  ratio: number;
  color: string;
  value: string;
  tooltip?: string;
};

function MeterRow({ label, ratio, color, value, tooltip }: MeterRowProps) {
  const width = Math.max(0, Math.min(1, ratio)) * 100;
  return (
    <li className="path-traffic-row" style={{ "--traffic-color": color } as CSSProperties} title={tooltip}>
      <span className="path-condition-meter__label">{label}</span>
      <span className="path-traffic-row__bar">
        <span className="path-traffic-row__fill" style={{ width: `${width}%` }} />
      </span>
      <span className="path-traffic-row__count">{value}</span>
    </li>
  );
}

type PathConditionPanelProps = {
  path: PathData;
  apiBaseUrl: string;
};

export function PathConditionPanel({ path, apiBaseUrl }: PathConditionPanelProps) {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);
  const [saturation, setSaturation] = useState<SaturationLevels>({ noise: null, pollution: null });

  const hasEmission = path.noise != null || path.air_pollution != null;

  useEffect(() => {
    if (!hasEmission) return;
    const controller = new AbortController();
    fetchApi<ParametersResponse>(buildApiUrl(apiBaseUrl, "/api/v1/parameters"), { signal: controller.signal })
      .then((res) => {
        const general = res.parameters?.GENERAL_PARAMS ?? {};
        setSaturation({
          noise: positiveNumberOrNull(general.NOISE_SATURATION_LEVEL),
          pollution: positiveNumberOrNull(general.POLLUTION_SATURATION_LEVEL),
        });
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) console.error("[PathConditionPanel] parameters fetch failed:", err);
      });
    return () => controller.abort();
  }, [apiBaseUrl, hasEmission]);

  if (!hasPathCondition(path)) return null;

  const formatPercent = (ratio: number) => `${Math.round(ratio * 100)}%`;

  /** Noise/air pollution row. The fraction determines the bar, so expose it in a tooltip. */
  const renderEmissionRow = (label: string, value: number, saturationLevel: number | null) => {
    const ratio = saturationLevel != null ? value / saturationLevel : null;
    return (
      <MeterRow
        label={label}
        ratio={ratio ?? 0}
        color={ratio != null ? EMISSION_COLORS[emissionLevel(ratio)] : UNKNOWN_COLOR}
        value={value.toLocaleString(locale, { maximumFractionDigits: 1 })}
        tooltip={ratio != null ? t("path.saturationRatio", { label, percent: Math.round(ratio * 100) }) : undefined}
      />
    );
  };

  return (
    <div className="path-condition">
      {!!path.under_maintenance && (
        <div className="maintenance-badge">
          {t("building.underMaintenance")}
        </div>
      )}
      <ul className="path-condition-meters">
        {hasUtilization(path) && path.utilization != null && (
          <MeterRow
            label={t("path.utilization")}
            ratio={path.utilization}
            color={congestionColor(path.utilization)}
            value={formatPercent(path.utilization)}
          />
        )}
        {path.durability != null && (
          <MeterRow
            label={t("path.durability")}
            ratio={path.durability}
            color={durabilityColor(path.durability)}
            value={formatPercent(path.durability)}
          />
        )}
        {path.noise != null && renderEmissionRow(t("path.noise"), path.noise, saturation.noise)}
        {path.air_pollution != null &&
          renderEmissionRow(t("path.airPollution"), path.air_pollution, saturation.pollution)}
      </ul>
    </div>
  );
}
