/**
 * パスの状態タイル(耐久度・騒音・大気汚染)
 *
 * パス詳細の基本情報セクションの中に、3つの状態を横並びのタイルで表示する。
 * 各タイルは状態の段階を描き分けたアイコンを主役にし、その下に参考として数値を添える。
 * - 耐久度: 0〜1 の値。20%刻みの5段階でひび割れが増える。数値は百分率
 * - 騒音・大気汚染: 交通量から決まる上限の無い値。住環境の評価でこれ以上を最悪とみなす飽和水準
 *   (GENERAL_PARAMS の NOISE_SATURATION_LEVEL / POLLUTION_SATURATION_LEVEL)に対する割合で4段階に分け、
 *   数値は生の値を出す。飽和水準が取得できないときはアイコンを基本形の灰色で描く
 *
 * 修繕中は工事バッジをタイルの上に出す。表示する項目が1つも無ければ何も描かない。
 */

import { useEffect, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import type { PathData } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { resolveIntlLocale } from "../../utils/locale";
import { DURABILITY_ICON_MAX_LEVEL, DurabilityIcon, NoiseIcon, PollutionIcon } from "./PathConditionIcons";
import "./PathConditionPanel.css";

/** GET /api/v1/parameters のうち、ここで使う部分 */
type ParametersResponse = {
  parameters?: { GENERAL_PARAMS?: Record<string, unknown> };
};

type SaturationLevels = { noise: number | null; pollution: number | null };

/** 耐久度がこの値以上なら段階0,1,2,3。すべて下回ると最悪の段階(欠け落ち) */
const DURABILITY_THRESHOLDS = [0.8, 0.6, 0.4, 0.2];
const DURABILITY_COLORS = ["#22c55e", "#84cc16", "#eab308", "#f97316", "#ef4444"];
const EMISSION_COLORS = ["#22c55e", "#84cc16", "#f59e0b", "#ef4444"];
const UNKNOWN_COLOR = "#93a9c0";

function durabilityLevel(durability: number): number {
  const index = DURABILITY_THRESHOLDS.findIndex((threshold) => durability >= threshold);
  return index === -1 ? DURABILITY_ICON_MAX_LEVEL : index;
}

/** 飽和水準に対する割合から騒音・大気汚染の段階を決める。ほぼ0なら段階0(波・粒子なし) */
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

  /** 騒音・大気汚染のタイル。割合は段階の根拠なので、ツールチップで読めるようにする */
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
