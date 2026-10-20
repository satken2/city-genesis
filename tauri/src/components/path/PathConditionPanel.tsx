/**
 * Path condition tiles (durability, noise, air pollution).
 *
 * Show three side-by-side tiles inside the path detail basic information section.
 * Each features an icon for its condition level, with a numeric value below for reference.
 * - Durability: 0–1, with cracks increasing over five 20% steps. Display the value as a percentage.
 * - Noise/air pollution: unbounded values determined by traffic. Divide into four levels by the fraction of the saturation
 *   level above which living environment evaluation considers the condition worst (NOISE_SATURATION_LEVEL /
 *   POLLUTION_SATURATION_LEVEL in GENERAL_PARAMS). Display raw values. If saturation levels are unavailable,
 *   draw basic gray icons.
 *
 * Show a construction badge above tiles during maintenance. Render nothing if no items are available.
 */

import { useEffect, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import type { PathData } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { resolveIntlLocale } from "../../utils/locale";
import { DURABILITY_ICON_MAX_LEVEL, DurabilityIcon, NoiseIcon, PollutionIcon } from "./PathConditionIcons";
import "./PathConditionPanel.css";

/** Fields used here from GET /api/v1/parameters. */
type ParametersResponse = {
  parameters?: { GENERAL_PARAMS?: Record<string, unknown> };
};

type SaturationLevels = { noise: number | null; pollution: number | null };

/** Durability thresholds for levels 0, 1, 2, 3. Below all thresholds is the worst level (broken corner). */
const DURABILITY_THRESHOLDS = [0.8, 0.6, 0.4, 0.2];
const DURABILITY_COLORS = ["#22c55e", "#84cc16", "#eab308", "#f97316", "#ef4444"];
const EMISSION_COLORS = ["#22c55e", "#84cc16", "#f59e0b", "#ef4444"];
const UNKNOWN_COLOR = "#93a9c0";

function durabilityLevel(durability: number): number {
  const index = DURABILITY_THRESHOLDS.findIndex((threshold) => durability >= threshold);
  return index === -1 ? DURABILITY_ICON_MAX_LEVEL : index;
}

/** Determine noise/air pollution levels from saturation fractions. Near zero is level 0 (no waves/particles). */
function emissionLevel(ratio: number): number {
  if (ratio < 0.01) return 0;
  if (ratio < 1 / 3) return 1;
  if (ratio < 2 / 3) return 2;
  return 3;
}

function positiveNumberOrNull(value: unknown): number | null {
  return typeof value === "number" && value > 0 ? value : null;
}

type ConditionTileProps = {
  label: string;
  color: string;
  icon: ReactNode;
  value: string;
  tooltip?: string;
};

function ConditionTile({ label, color, icon, value, tooltip }: ConditionTileProps) {
  return (
    <div className="path-condition-tile" title={tooltip}>
      <span className="path-condition-tile__label">{label}</span>
      <span className="path-condition-tile__icon" style={{ color }}>
        {icon}
      </span>
      <span className="path-condition-tile__value">{value}</span>
    </div>
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

  if (!path.under_maintenance && path.durability == null && !hasEmission) return null;

  /** Noise/air pollution tile. The fraction determines the level, so expose it in a tooltip. */
  const renderEmissionTile = (
    label: string,
    value: number,
    saturationLevel: number | null,
    renderIcon: (level: number | null) => ReactNode,
  ) => {
    const ratio = saturationLevel != null ? value / saturationLevel : null;
    const level = ratio != null ? emissionLevel(ratio) : null;
    return (
      <ConditionTile
        label={label}
        color={level != null ? EMISSION_COLORS[level] : UNKNOWN_COLOR}
        icon={renderIcon(level)}
        value={value.toLocaleString(locale, { maximumFractionDigits: 1 })}
        tooltip={ratio != null ? t("path.saturationRatio", { label, percent: Math.round(ratio * 100) }) : undefined}
      />
    );
  };

  const durability = path.durability;
  const durabilityTileLevel = durability != null ? durabilityLevel(durability) : null;

  return (
    <div className="path-condition">
      {!!path.under_maintenance && (
        <div className="maintenance-badge">
          {t("building.underMaintenance")}
        </div>
      )}
      <div className="path-condition-tiles">
        {durability != null && durabilityTileLevel != null && (
          <ConditionTile
            label={t("path.durability")}
            color={DURABILITY_COLORS[durabilityTileLevel]}
            icon={<DurabilityIcon level={durabilityTileLevel} />}
            value={`${Math.round(durability * 100)}%`}
          />
        )}
        {path.noise != null &&
          renderEmissionTile(t("path.noise"), path.noise, saturation.noise, (level) => <NoiseIcon level={level} />)}
        {path.air_pollution != null &&
          renderEmissionTile(t("path.airPollution"), path.air_pollution, saturation.pollution, (level) => (
            <PollutionIcon level={level} />
          ))}
      </div>
    </div>
  );
}
