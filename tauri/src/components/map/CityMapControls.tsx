/**
 * 都市地図の表示切り替え(レイヤー・ヒートマップ・インフラ表示・全体表示・再読み込み)のツールバーと、
 * ヒートマップ・インフラ表示の凡例。
 *
 * 都市マップウィンドウ(CityMapWindow)と、ブラウザ版の開発用の地図ページ(`web/debug-map/`)が同じツールバーを使う。
 * 表示状態はこのフックが持ち、地図本体(CityMapCanvas)へは呼び出し側が渡す。
 * ヒートマップとインフラ表示はどちらも地図を塗るので、片方を選ぶともう片方を外す。
 */

import { useCallback, useState, type ReactNode, type RefObject } from "react";
import { useTranslation } from "react-i18next";
import {
  DEFAULT_CITY_MAP_LAYERS,
  getHeatmapRampStops,
  type CityMapCanvasHandle,
  type CityMapHeatmapKind,
  type CityMapHeatmapMode,
  type CityMapHeatmapStats,
  type CityMapLayerVisibility,
} from "./CityMapCanvas";
import "./CityMapWindow.css";
import { MAP_SYMBOLS, type MapSymbol } from "./mapSymbols";
import { useInfraProducts } from "./infraOverlay";
import { InfraMapLegend } from "./InfraMapLegend";
import { useMasterData } from "../../contexts/MasterDataContext";

export function MapIcon({ name }: { name: MapSymbol }) {
  return <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={MAP_SYMBOLS[name]} /></svg>;
}

/** レイヤーキーとツールバー記号の対応。記号名が単数形のものだけ読み替える */
const layerSymbol = (key: keyof CityMapLayerVisibility): MapSymbol =>
  key === "stations" ? "station" : key === "interchanges" ? "interchange" : key;

/** ツールバーに並べるレイヤーの表示順 */
const LAYER_ORDER: (keyof CityMapLayerVisibility)[] = [
  "elevation",
  "contours",
  "grid",
  "coastline",
  "zoneBorders",
  "parcels",
  "buildings",
  "stations",
  "interchanges",
  "allRoads",
  "trunkRoads",
  "railways",
  "transitLines",
];

/** ヒートマップ切り替えの表示順。複数レイヤーと違い1つだけ選ぶ排他選択 */
const HEATMAP_ORDER: CityMapHeatmapKind[] = ["none", "noise", "pollution", "crime", "sunshine"];

export type CityMapControls = {
  layers: CityMapLayerVisibility;
  heatmap: CityMapHeatmapKind;
  heatmapMode: CityMapHeatmapMode;
  /** CityMapCanvas の infraProductId に渡す */
  infraProductId: number | null;
  /** CityMapCanvas の onHeatmapStats に渡す */
  onHeatmapStats: (stats: CityMapHeatmapStats | null) => void;
  toolbar: ReactNode;
  /** 地図の上に重ねる凡例。ヒートマップもインフラ表示もしていないときは null */
  legend: ReactNode;
};

export function useCityMapControls(
  apiBaseUrl: string,
  mapRef: RefObject<CityMapCanvasHandle | null>,
  onReload: () => void
): CityMapControls {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const infraProducts = useInfraProducts(apiBaseUrl);
  const [layers, setLayers] = useState<CityMapLayerVisibility>({ ...DEFAULT_CITY_MAP_LAYERS });
  const [heatmap, setHeatmap] = useState<CityMapHeatmapKind>("none");
  const [heatmapMode, setHeatmapMode] = useState<CityMapHeatmapMode>("overlay");
  const [heatmapStats, setHeatmapStats] = useState<CityMapHeatmapStats | null>(null);
  const [infraProductId, setInfraProductId] = useState<number | null>(null);
  const infraKind = infraProducts.find((p) => p.productId === infraProductId)?.kind ?? null;

  const selectHeatmap = useCallback((kind: CityMapHeatmapKind) => {
    setHeatmap(kind);
    if (kind !== "none") setInfraProductId(null);
  }, []);

  const selectInfraProduct = useCallback((productId: number | null) => {
    setInfraProductId(productId);
    if (productId !== null) setHeatmap("none");
  }, []);

  const toggleLayer = useCallback((key: keyof CityMapLayerVisibility) => {
    setLayers((prev) => ({ ...prev, [key]: !prev[key] }));
  }, []);

  const toolbar = (
    <div className="city-map-window__toolbar">
      <div className="city-map-window__layer-toggles">
        {LAYER_ORDER.map((key) => (
          <button
            key={key}
            type="button"
            className={`city-map-toggle ${layers[key] ? "city-map-toggle--on" : ""}`}
            aria-pressed={layers[key]}
            aria-label={t(`cityMap.layers.${key}`)}
            title={t(`cityMap.layers.${key}`)}
            onClick={() => toggleLayer(key)}
          >
            <MapIcon name={layerSymbol(key)} />
          </button>
        ))}
      </div>
      {/* ヒートマップ。2種を重ねると色が混ざって読めないため排他選択にする */}
      <div className="city-map-window__heatmap">
        <span className="city-map-window__heatmap-label">
          {t("cityMap.heatmap.title")}
        </span>
        {HEATMAP_ORDER.map((kind) => (
          <button
            key={kind}
            type="button"
            className={`city-map-heatmap-btn ${
              heatmap === kind ? "city-map-heatmap-btn--on" : ""
            }`}
            aria-pressed={heatmap === kind}
            aria-label={`${t("cityMap.heatmap.title")}: ${t(`cityMap.heatmap.${kind}`)}`}
            title={`${t("cityMap.heatmap.title")}: ${t(`cityMap.heatmap.${kind}`)}`}
            onClick={() => selectHeatmap(kind)}
          >
            <MapIcon name={kind} />
          </button>
        ))}
        <label className="city-map-window__source-toggle">
          <button
            type="button"
            role="switch"
            aria-checked={heatmapMode === "sources"}
            aria-label={t("cityMap.heatmap.sources")}
            title={t("cityMap.heatmap.sources")}
            className="city-map-source-switch"
            onClick={() => setHeatmapMode((mode) => mode === "overlay" ? "sources" : "overlay")}
          >
            <span />
          </button>
          <span>{t("cityMap.heatmap.sources")}</span>
        </label>
      </div>
      <label className="city-map-window__infra">
        <span className="city-map-window__heatmap-label">{t("cityMap.infra.title")}</span>
        <select
          className="city-map-window__infra-select"
          value={infraProductId ?? ""}
          onChange={(e) => selectInfraProduct(e.target.value === "" ? null : Number(e.target.value))}
        >
          <option value="">{t("cityMap.infra.none")}</option>
          {infraProducts.map((p) => (
            <option key={p.productId} value={p.productId}>
              {masterData?.products[p.productId]?.display_name ?? String(p.productId)}
            </option>
          ))}
        </select>
      </label>
      <div className="city-map-window__actions">
        <button
          type="button"
          className="city-map-window__btn"
          title={t("cityMap.fitView")}
          aria-label={t("cityMap.fitView")}
          onClick={() => mapRef.current?.resetView()}
        >
          <MapIcon name="fit" />
        </button>
        <button
          type="button"
          className="city-map-window__btn"
          title={t("cityMap.reload")}
          aria-label={t("cityMap.reload")}
          onClick={onReload}
        >
          <MapIcon name="reload" />
        </button>
      </div>
    </div>
  );

  // 発生源は都市全体の強度レンジ、オーバーレイは固定の悪さスコアを示す。
  const legend = infraKind ? (
    <div className="city-map-window__legend">
      <InfraMapLegend kind={infraKind} />
    </div>
  ) : heatmapStats && (
    <div className="city-map-window__legend">
      {heatmapStats.count === 0 ? (
        <span className="city-map-window__legend-empty">
          {t("cityMap.heatmap.noData")}
        </span>
      ) : (
        <>
          <span className="city-map-window__legend-title">
            {t(`cityMap.heatmap.${heatmapStats.kind}`)}
            {heatmapStats.mode === "sources" && ` · ${t("cityMap.heatmap.sources")}`}
          </span>
          <span className="city-map-window__legend-value">
            {heatmapStats.min.toFixed(2)}
          </span>
          <span
            className="city-map-window__legend-bar"
            style={{
              background: `linear-gradient(to right, ${getHeatmapRampStops(
                heatmapStats.kind, heatmapStats.mode
              ).join(", ")})`,
            }}
          />
          <span className="city-map-window__legend-value">
            {heatmapStats.max.toFixed(2)}
          </span>
        </>
      )}
    </div>
  );

  return { layers, heatmap, heatmapMode, infraProductId, onHeatmapStats: setHeatmapStats, toolbar, legend };
}
