/**
 * パスの物理状態パネル
 *
 * BuildingInfoPanel と同じ構成で、パスの物理的な状態情報を表示する。
 * - under_maintenance: 修繕中の場合、工事バッジを表示する
 * - durability: 耐久度をメーターで表示する
 * - noise: 騒音値を数値で表示する
 * - air_pollution: 大気汚染値を数値で表示する
 *
 * 全フィールドは任意であり、null の場合は該当行を非表示にする。
 */

import { useTranslation } from "react-i18next";
import type { PathData } from "../../types";
import { Meter } from "../common/Meter";
import "./PathConditionPanel.css";

type PathConditionPanelProps = {
  path: PathData;
};

export function PathConditionPanel({ path }: PathConditionPanelProps) {
  const { t } = useTranslation();

  const hasAnyContent =
    !!path.under_maintenance ||
    path.durability != null ||
    path.noise != null ||
    path.air_pollution != null;

  if (!hasAnyContent) return null;

  return (
    <section className="agent-detail-section path-condition-section">
      <h3 className="agent-detail-section-title">{t("path.condition", "物理状態")}</h3>
      {!!path.under_maintenance && (
        <div className="maintenance-badge">
          {t("building.underMaintenance")}
        </div>
      )}

      {path.durability != null && (
        <div className="path-condition-meter">
          <Meter
            label={t("path.durability")}
            value={path.durability}
          />
        </div>
      )}

      {(path.noise != null || path.air_pollution != null) && (
        <div className="agent-detail-grid path-condition-grid">
          {path.noise != null && (
            <>
              <span className="agent-detail-label">{t("path.noise")}</span>
              <span className="agent-detail-value">
                {path.noise.toFixed(2)}
              </span>
            </>
          )}
          {path.air_pollution != null && (
            <>
              <span className="agent-detail-label">{t("path.airPollution")}</span>
              <span className="agent-detail-value">
                {path.air_pollution.toFixed(2)}
              </span>
            </>
          )}
        </div>
      )}
    </section>
  );
}
