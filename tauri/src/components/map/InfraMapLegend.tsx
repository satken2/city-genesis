/**
 * 都市地図のインフラ表示(infraOverlay.ts)の凡例。都市マップウィンドウと統計のインフラ網タブが使う。
 * 文字の大きさと色は置き場所の側で決める。
 */

import { useTranslation } from "react-i18next";
import {
  COVERAGE_IN_RANGE_COLOR,
  COVERAGE_OUT_OF_RANGE_COLOR,
  COVERAGE_OUTLINE_COLOR,
  INFRA_COLOR_CONSUMER,
  INFRA_COLOR_DISCONNECTED,
  INFRA_COLOR_PRODUCER,
  type InfraProductKind,
} from "./infraOverlay";
import "./InfraMapLegend.css";

export function InfraMapLegend({ kind }: { kind: InfraProductKind }) {
  const { t } = useTranslation();

  if (kind === "coverage") {
    return (
      <div className="infra-map-legend">
        <span className="infra-map-legend__item">
          <span className="infra-map-legend__swatch" style={{ background: COVERAGE_IN_RANGE_COLOR }} />
          {t("cityMap.infra.legendInRange")}
        </span>
        <span className="infra-map-legend__item">
          <span className="infra-map-legend__swatch" style={{ background: COVERAGE_OUT_OF_RANGE_COLOR }} />
          {t("cityMap.infra.legendOutOfRange")}
        </span>
        <span className="infra-map-legend__item">
          <span className="infra-map-legend__line" style={{ borderColor: COVERAGE_OUTLINE_COLOR }} />
          {t("cityMap.infra.legendRange")}
        </span>
        <span className="infra-map-legend__item">
          <span className="infra-map-legend__line infra-map-legend__line--dashed" style={{ borderColor: COVERAGE_OUTLINE_COLOR }} />
          {t("cityMap.infra.legendInactiveRange")}
        </span>
      </div>
    );
  }

  return (
    <div className="infra-map-legend">
      <span className="infra-map-legend__item">
        <span className="infra-map-legend__gradient" />
        {t("cityMap.infra.legendUtilization")}
      </span>
      <span className="infra-map-legend__item">
        <span className="infra-map-legend__stripes" />
        {t("cityMap.infra.legendFlow")}
      </span>
      <span className="infra-map-legend__item">
        <span className="infra-map-legend__marker infra-map-legend__marker--circle" style={{ background: INFRA_COLOR_PRODUCER }} />
        {t("cityMap.infra.legendProducers")}
      </span>
      <span className="infra-map-legend__item">
        <span className="infra-map-legend__marker infra-map-legend__marker--diamond" style={{ background: INFRA_COLOR_CONSUMER }} />
        {t("cityMap.infra.legendConsumers")}
      </span>
      <span className="infra-map-legend__item">
        <span className="infra-map-legend__marker infra-map-legend__marker--circle" style={{ background: INFRA_COLOR_DISCONNECTED }} />
        {t("cityMap.infra.legendDisconnected")}
      </span>
    </div>
  );
}
