/**
 * 都市マップウィンドウ (window_type="city_map")
 *
 * 共通地図コンポーネント(CityMapCanvas)をウィンドウとして表示し、
 * その上部にレイヤー表示切り替えのツールバーを配置する。
 * レイヤーのON/OFF・全体表示・再読み込みはすべてこのウィンドウのUIであり、
 * 地図描画そのものはCityMapCanvasに委譲している。
 *
 * 将来的にはウィンドウ左側に検索・2点間経路探索のパネルを追加する予定
 * (現時点では未実装)。
 */

import { useCallback, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  CityMapCanvas,
  DEFAULT_CITY_MAP_LAYERS,
  getHeatmapRampStops,
  type CityMapCanvasHandle,
  type CityMapHeatmapKind,
  type CityMapHeatmapStats,
  type CityMapLayerVisibility,
} from "./CityMapCanvas";
import "./CityMapWindow.css";

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
  "allRoads",
  "trunkRoads",
  "railways",
];

/** ヒートマップ切り替えの表示順。複数レイヤーと違い1つだけ選ぶ排他選択 */
const HEATMAP_ORDER: CityMapHeatmapKind[] = ["none", "noise", "pollution", "crime", "sunshine"];

export function CityMapWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const mapRef = useRef<CityMapCanvasHandle>(null);
  const coordRef = useRef<HTMLSpanElement | null>(null);
  const zoomRef = useRef<HTMLSpanElement | null>(null);

  const [layers, setLayers] = useState<CityMapLayerVisibility>({
    ...DEFAULT_CITY_MAP_LAYERS,
  });
  const [heatmap, setHeatmap] = useState<CityMapHeatmapKind>("none");
  const [heatmapStats, setHeatmapStats] = useState<CityMapHeatmapStats | null>(null);

  const toggleLayer = useCallback((key: keyof CityMapLayerVisibility) => {
    setLayers((prev) => ({ ...prev, [key]: !prev[key] }));
  }, []);

  /**
   * カーソル座標・ズーム率の表示はマウス移動のたびに更新されるため、
   * Reactのstateを経由せずDOMへ直接書き込んで再レンダリングを避ける。
   */
  const handleCursor = useCallback((pos: { x: number; z: number } | null) => {
    if (!coordRef.current) return;
    coordRef.current.textContent = pos
      ? `${pos.x.toFixed(1)}, ${pos.z.toFixed(1)}`
      : "---, ---";
  }, []);

  const handleZoom = useCallback((zoom: number) => {
    if (!zoomRef.current) return;
    zoomRef.current.textContent = `${Math.round(zoom * 100)}%`;
  }, []);

  return (
    <div className="city-map-window">
      {/* ツールバー: レイヤー表示切り替えと操作ボタン */}
      <div className="city-map-window__toolbar">
        <div className="city-map-window__layer-toggles">
          {LAYER_ORDER.map((key) => (
            <label
              key={key}
              className={`city-map-toggle ${layers[key] ? "city-map-toggle--on" : ""}`}
            >
              <input
                type="checkbox"
                checked={layers[key]}
                onChange={() => toggleLayer(key)}
              />
              <span>{t(`cityMap.layers.${key}`)}</span>
            </label>
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
              onClick={() => setHeatmap(kind)}
            >
              {t(`cityMap.heatmap.${kind}`)}
            </button>
          ))}
        </div>
        <div className="city-map-window__actions">
          <button
            type="button"
            className="city-map-window__btn"
            onClick={() => mapRef.current?.resetView()}
          >
            {t("cityMap.fitView")}
          </button>
          <button
            type="button"
            className="city-map-window__btn"
            onClick={() => mapRef.current?.reload()}
          >
            {t("cityMap.reload")}
          </button>
        </div>
      </div>

      {/* 地図本体 */}
      <div className="city-map-window__map">
        <CityMapCanvas
          ref={mapRef}
          apiBaseUrl={apiBaseUrl}
          layers={layers}
          heatmap={heatmap}
          interactive
          onCursorWorldPos={handleCursor}
          onZoomChange={handleZoom}
          onHeatmapStats={setHeatmapStats}
        />
        {/* ヒートマップの凡例。塗り色はエポックごとのmin〜maxを両端まで
            引き伸ばした相対値なので、実値レンジを併記して絶対水準を伝える */}
        {heatmapStats && (
          <div className="city-map-window__legend">
            {heatmapStats.count === 0 ? (
              <span className="city-map-window__legend-empty">
                {t("cityMap.heatmap.noData")}
              </span>
            ) : (
              <>
                <span className="city-map-window__legend-title">
                  {t(`cityMap.heatmap.${heatmapStats.kind}`)}
                </span>
                <span className="city-map-window__legend-value">
                  {heatmapStats.min.toFixed(2)}
                </span>
                <span
                  className="city-map-window__legend-bar"
                  style={{
                    background: `linear-gradient(to right, ${getHeatmapRampStops(
                      heatmapStats.kind
                    ).join(", ")})`,
                  }}
                />
                <span className="city-map-window__legend-value">
                  {heatmapStats.max.toFixed(2)}
                </span>
              </>
            )}
          </div>
        )}
        {/* ステータスオーバーレイ(カーソルのワールド座標とズーム率) */}
        <div className="city-map-window__status">
          <span ref={coordRef}>---, ---</span>
          <span ref={zoomRef} className="city-map-window__status-zoom">
            100%
          </span>
        </div>
      </div>
    </div>
  );
}
