/**
 * 都市マップウィンドウ (window_type="city_map")
 *
 * 共通地図コンポーネント(CityMapCanvas)をウィンドウとして表示し、
 * その上部にレイヤー表示切り替えのツールバーを配置する。
 * レイヤーのON/OFF・全体表示・再読み込みはすべてこのウィンドウのUIであり、
 * 地図描画そのものはCityMapCanvasに委譲している。
 *
 * 3Dビューのカメラ位置・向きもマーカーとして重ねて描く(下記「カメラマーカー」)。
 *
 * 左側の検索・経路案内は、一括経路計算の結果を読み取り専用で表示する。
 */

import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { listen } from "@tauri-apps/api/event";
import {
  CityMapCanvas,
  DEFAULT_CITY_MAP_LAYERS,
  getHeatmapRampStops,
  type CityMapCanvasHandle,
  type CityMapHeatmapKind,
  type CityMapHeatmapStats,
  type CityMapLayerVisibility,
  type CityMapViewState,
} from "./CityMapCanvas";
import { IPC_EVENT_CAMERA_STATE, sendCameraQueryState } from "../../utils/ipc";
import "./CityMapWindow.css";
import { MAP_SYMBOLS, type MapSymbol } from "./mapSymbols";

function MapIcon({ name }: { name: MapSymbol }) {
  return <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={MAP_SYMBOLS[name]} /></svg>;
}
import { useMapNavigation } from "./CityMapNavigation";

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

/* ---- カメラマーカー ----
 * 3Dビューのカメラがどこを向いているかを地図上の矢印マーカーで示す。
 * カメラの動きに追従はさせず、地図を開いたときと再読み込みしたときに
 * Unityへ現在値を1回問い合わせて描く(常時配信はMMFを無駄に埋めるため)。 */

/** camera:state のペイロード */
type CameraStatePayload = {
  /** カメラのワールド座標 [x, y, z] */
  position?: number[];
  /** 視線の方位角(北=ワールド+Zを0とする時計回り・度) */
  yaw?: number;
};

/**
 * 描画するカメラマーカー。seqは受信のたびに増える通し番号で、
 * CityMapCanvasへ渡す overlayVersion(重ね描きの再描画要求)に使う。
 */
type CameraMarker = { x: number; z: number; yaw: number; seq: number };

/** カメラマーカーの色。地形やヒートマップの上でも埋もれない暖色にする */
const CAMERA_MARKER = "#f9e2af";
const CAMERA_MARKER_EDGE = "#1e1e2e";

/** カメラマーカーの寸法(画面ピクセル。ズームによらず一定の大きさで描く) */
const CAMERA_MARKER_TIP_PX = 17;
const CAMERA_MARKER_HALF_WIDTH_PX = 8;
const CAMERA_MARKER_RADIUS_PX = 4;

export function CityMapWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const mapRef = useRef<CityMapCanvasHandle>(null);
  const coordRef = useRef<HTMLSpanElement | null>(null);
  const zoomRef = useRef<HTMLSpanElement | null>(null);
  const navigation = useMapNavigation(apiBaseUrl, mapRef);

  const [layers, setLayers] = useState<CityMapLayerVisibility>({ ...DEFAULT_CITY_MAP_LAYERS });
  const [heatmap, setHeatmap] = useState<CityMapHeatmapKind>("none");
  const [heatmapStats, setHeatmapStats] = useState<CityMapHeatmapStats | null>(null);
  const [camera, setCamera] = useState<CameraMarker | null>(null);

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

  /** Unityから届いたカメラ位置・方位を受け取り、マーカーの再描画を要求する */
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    listen<CameraStatePayload>(IPC_EVENT_CAMERA_STATE, (event) => {
      const pos = event.payload?.position;
      if (!pos || pos.length < 3) return;
      setCamera((prev) => ({
        x: pos[0],
        z: pos[2],
        yaw: event.payload.yaw ?? 0,
        seq: (prev?.seq ?? 0) + 1,
      }));
    })
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);

    // 地図を開いた時点のカメラ位置を取得する。
    void sendCameraQueryState().catch(console.error);

    return () => {
      unlisten?.();
    };
  }, []);

  /** 再読み込みでは地図データと合わせてカメラ位置も取り直す */
  const handleReload = useCallback(() => {
    mapRef.current?.reload();
    navigation.reload();
    void sendCameraQueryState().catch(console.error);
  }, [navigation.reload]);

  /**
   * カメラ位置マーカーの重ね描き。ctxにはワールド座標系のビュー変換が適用済みなので、
   * ワールド座標のまま描ける(画面上で一定の大きさにしたい量だけzoomで割る)。
   */
  const drawCameraMarker = useCallback(
    (ctx: CanvasRenderingContext2D, view: CityMapViewState) => {
      navigation.draw(ctx, view);
      if (!camera) return;
      const px = (v: number) => v / view.zoom;

      // 方位角(北=+Z、時計回り)を水平面の単位ベクトルへ。
      const yawRad = (camera.yaw * Math.PI) / 180;
      const dx = Math.sin(yawRad);
      const dz = Math.cos(yawRad);
      const tip = px(CAMERA_MARKER_TIP_PX);
      const half = px(CAMERA_MARKER_HALF_WIDTH_PX);

      // 視線方向を向いた三角形。底辺は視線に直交する向きに取る。
      ctx.beginPath();
      ctx.moveTo(camera.x + dx * tip, camera.z + dz * tip);
      ctx.lineTo(camera.x + dz * half, camera.z - dx * half);
      ctx.lineTo(camera.x - dz * half, camera.z + dx * half);
      ctx.closePath();
      ctx.fillStyle = CAMERA_MARKER;
      ctx.fill();
      ctx.strokeStyle = CAMERA_MARKER_EDGE;
      ctx.lineWidth = px(1);
      ctx.setLineDash([]);
      ctx.stroke();

      // カメラ位置そのものを示す円(三角形の重心とずれないよう位置に重ねて描く)
      ctx.beginPath();
      ctx.arc(camera.x, camera.z, px(CAMERA_MARKER_RADIUS_PX), 0, Math.PI * 2);
      ctx.fillStyle = CAMERA_MARKER;
      ctx.fill();
      ctx.stroke();
    },
    [camera, navigation.draw]
  );

  return (
    <div className="city-map-window">
      {navigation.sidebar}
      <div className="city-map-window__main">
      {/* ツールバー: レイヤー表示切り替えと操作ボタン */}
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
              onClick={() => setHeatmap(kind)}
            >
              <MapIcon name={kind} />
            </button>
          ))}
        </div>
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
            onClick={handleReload}
          >
            <MapIcon name="reload" />
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
          overlayDraw={drawCameraMarker}
          overlayVersion={(camera?.seq ?? 0) + navigation.overlayVersion}
          onMapClick={navigation.onMapClick}
          cursor={navigation.picking ? "crosshair" : undefined}
          focusPathIds={navigation.roadFocus?.pathIds ?? null}
          focusColor={navigation.roadFocus?.color ?? null}
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
    </div>
  );
}
