/**
 * Toolbar for city map layers, overlays, overview reset, and reload, plus heatmap/infrastructure legends.
 *
 * CityMapWindow and the browser's development map page (web/debug-map/) share this toolbar.
 * This hook owns display state; callers pass it to CityMapCanvas.
 *
 * The toolbar has two rows: layer toggles and overlay selection first, then settings for the selected overlay
 * (public transport type, heatmap type, or infrastructure goods). Hide the second row for overlays without settings.
 * Public transport, heatmap, and infrastructure overlays all color the map, so select only one at a time.
 * Infrastructure choices (useInfraProducts) and the legend (InfraMapLegend) are shared with the public facility map.
 */

import { useCallback, useMemo, useState, type ReactNode, type RefObject } from "react";
import { useTranslation } from "react-i18next";
import {
  DEFAULT_CITY_MAP_LAYERS,
  getHeatmapRampStops,
  type CityMapCanvasHandle,
  type CityMapHeatmapActiveKind,
  type CityMapHeatmapKind,
  type CityMapHeatmapMode,
  type CityMapHeatmapStats,
  type CityMapLayerVisibility,
} from "./CityMapCanvas";
import "./CityMapWindow.css";
import { MAP_SYMBOLS, type MapSymbol } from "./mapSymbols";
import { useInfraSelection, type InfraSelection } from "./infraOverlay";
import { InfraMapLegend } from "./InfraMapLegend";
import { useMasterData } from "../../contexts/MasterDataContext";

export function MapIcon({ name }: { name: MapSymbol }) {
  return <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={MAP_SYMBOLS[name]} /></svg>;
}

/**
 * Toolbar layer button order and the layers each button toggles. Use the first layer's name.
 * Tracks and arterial roads both show the city's structure, so toggle them together with one button.
 */
const LAYER_BUTTONS: { symbol: MapSymbol; layers: (keyof CityMapLayerVisibility)[] }[] = [
  { symbol: "elevation", layers: ["elevation"] },
  { symbol: "contours", layers: ["contours"] },
  { symbol: "grid", layers: ["grid"] },
  { symbol: "zoneBorders", layers: ["zoneBorders"] },
  { symbol: "parcels", layers: ["parcels"] },
  { symbol: "buildings", layers: ["buildings"] },
  { symbol: "buildings3d", layers: ["buildings3d"] },
  { symbol: "station", layers: ["stations"] },
  { symbol: "interchange", layers: ["interchanges"] },
  { symbol: "allRoads", layers: ["allRoads"] },
  { symbol: "trunkRoads", layers: ["trunkRoads", "railways"] },
  { symbol: "labels", layers: ["labels"] },
];

type CityMapOverlay = "none" | "transit" | "heatmap" | "infra";
const OVERLAYS: CityMapOverlay[] = ["none", "transit", "heatmap", "infra"];

/** Display order of heatmap types. */
const HEATMAP_KINDS: CityMapHeatmapActiveKind[] = ["noise", "pollution", "crime", "sunshine"];

export type CityMapControls = {
  layers: CityMapLayerVisibility;
  heatmap: CityMapHeatmapKind;
  heatmapMode: CityMapHeatmapMode;
  /** Pass to CityMapCanvas's transitVariant. */
  transitVariant: number | null;
  /** Pass to CityMapCanvas's infraSelection. */
  infraSelection: InfraSelection | null;
  /** Pass to CityMapCanvas's onHeatmapStats. */
  onHeatmapStats: (stats: CityMapHeatmapStats | null) => void;
  toolbar: ReactNode;
  /** Legend overlaid on the map. null when neither heatmap nor infrastructure is displayed. */
  legend: ReactNode;
};

export function useCityMapControls(
  apiBaseUrl: string,
  mapRef: RefObject<CityMapCanvasHandle | null>,
  onReload: () => void
): CityMapControls {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const infra = useInfraSelection(apiBaseUrl);
  const [layers, setLayers] = useState<CityMapLayerVisibility>({ ...DEFAULT_CITY_MAP_LAYERS });
  const [overlay, setOverlay] = useState<CityMapOverlay>("none");
  // Retain public transport type, heatmap type, and infrastructure goods separately to restore previous choices when switching overlays back.
  const [transitVariant, setTransitVariant] = useState<number | null>(null);
  const [heatmapKind, setHeatmapKind] = useState<CityMapHeatmapActiveKind>(HEATMAP_KINDS[0]);
  const [heatmapMode, setHeatmapMode] = useState<CityMapHeatmapMode>("overlay");
  const [heatmapStats, setHeatmapStats] = useState<CityMapHeatmapStats | null>(null);

  const toggleLayer = useCallback((keys: (keyof CityMapLayerVisibility)[]) => {
    setLayers((prev) => {
      const on = !prev[keys[0]];
      return { ...prev, ...Object.fromEntries(keys.map((key) => [key, on])) };
    });
  }, []);

  const transitTypes = Object.values(masterData?.transit ?? {});

  const overlayContent = overlay === "transit" ? (
    <select
      className="city-map-window__select"
      aria-label={t("cityMap.overlay.transit")}
      value={transitVariant ?? ""}
      onChange={(e) => setTransitVariant(e.target.value === "" ? null : Number(e.target.value))}
    >
      <option value="">{t("cityMap.overlay.transitAll")}</option>
      {transitTypes.map((type) => (
        <option key={type.transit_path_id} value={type.transit_path_id}>{type.display_name}</option>
      ))}
    </select>
  ) : overlay === "heatmap" ? (
    <div className="city-map-window__heatmap">
      {HEATMAP_KINDS.map((kind) => (
        <button
          key={kind}
          type="button"
          className={`city-map-heatmap-btn ${heatmapKind === kind ? "city-map-heatmap-btn--on" : ""}`}
          aria-pressed={heatmapKind === kind}
          aria-label={t(`cityMap.heatmap.${kind}`)}
          title={t(`cityMap.heatmap.${kind}`)}
          onClick={() => setHeatmapKind(kind)}
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
  ) : overlay === "infra" ? (
    <select
      className="city-map-window__select"
      aria-label={t("cityMap.overlay.infra")}
      value={infra.option?.productId ?? ""}
      onChange={(e) => infra.select(Number(e.target.value))}
    >
      {infra.options.map((p) => (
        <option key={p.productId} value={p.productId}>
          {masterData?.products[p.productId]?.display_name ?? String(p.productId)}
        </option>
      ))}
    </select>
  ) : null;

  const toolbar = (
    <div className="city-map-window__toolbar">
      <div className="city-map-window__toolbar-row">
        <div className="city-map-window__layer-toggles">
          {LAYER_BUTTONS.map(({ symbol, layers: keys }) => (
            <button
              key={keys[0]}
              type="button"
              className={`city-map-toggle ${layers[keys[0]] ? "city-map-toggle--on" : ""}`}
              aria-pressed={layers[keys[0]]}
              aria-label={t(`cityMap.layers.${keys[0]}`)}
              title={t(`cityMap.layers.${keys[0]}`)}
              onClick={() => toggleLayer(keys)}
            >
              <MapIcon name={symbol} />
            </button>
          ))}
        </div>
        <label className="city-map-window__overlay">
          <span className="city-map-window__overlay-label">{t("cityMap.overlay.title")}</span>
          <select
            className="city-map-window__select"
            value={overlay}
            onChange={(e) => setOverlay(e.target.value as CityMapOverlay)}
          >
            {OVERLAYS.map((mode) => (
              <option key={mode} value={mode}>{t(`cityMap.overlay.${mode}`)}</option>
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
      {overlayContent && <div className="city-map-window__toolbar-row">{overlayContent}</div>}
    </div>
  );

  const canvasLayers = useMemo(() => ({ ...layers, transitLines: overlay === "transit" }), [layers, overlay]);
  const infraSelection = overlay === "infra" ? infra.option?.selection ?? null : null;

  // Heatmap legend values use citywide intensity ranges for sources and fixed adverse-impact scores for grid display.
  const legend = infraSelection ? (
    <div className="city-map-window__legend city-map-window__legend--infra">
      <InfraMapLegend selection={infraSelection} />
    </div>
  ) : overlay === "heatmap" && heatmapStats && (
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

  return {
    layers: canvasLayers,
    heatmap: overlay === "heatmap" ? heatmapKind : "none",
    heatmapMode,
    transitVariant,
    infraSelection,
    onHeatmapStats: setHeatmapStats,
    toolbar,
    legend,
  };
}
