/**
 * City map window (window_type="city_map").
 *
 * Display the shared CityMapCanvas component as a window with the CityMapControls layer toolbar above it.
 * Delegate map rendering itself to CityMapCanvas.
 *
 * Overlay the 3D view camera's position and direction as a marker (see "Camera marker" below).
 *
 * Search and directions on the left show batch route calculation results read-only.
 */

import { useCallback, useEffect, useRef, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import {
  CityMapCanvas,
  type CityMapCanvasHandle,
  type CityMapViewState,
} from "./CityMapCanvas";
import { IPC_EVENT_CAMERA_STATE, sendCameraQueryState } from "../../utils/ipc";
import "./CityMapWindow.css";
import { useCityMapControls } from "./CityMapControls";
import { useMapNavigation } from "./CityMapNavigation";

/*
 * ---- Camera marker ----
 * An arrow on the map indicates where the 3D camera points. Do not track its movement continuously;
 * query Unity once when opening or reloading the map (continuous streaming would needlessly fill MMF).
 */

/** camera:state payload. */
type CameraStatePayload = {
  /** Camera world coordinates [x, y, z]. */
  position?: number[];
  /** Viewing azimuth (clockwise degrees from north = world +Z = 0). */
  yaw?: number;
};

/**
 * Camera marker to draw. seq increments on each received update and is passed to CityMapCanvas
 * as overlayVersion to request an overlay redraw.
 */
type CameraMarker = { x: number; z: number; yaw: number; seq: number };

/** Camera marker color. Use a warm color that remains visible over terrain and heatmaps. */
const CAMERA_MARKER = "#f9e2af";
const CAMERA_MARKER_EDGE = "#1e1e2e";

/** Camera marker dimensions (screen pixels, independent of zoom). */
const CAMERA_MARKER_TIP_PX = 17;
const CAMERA_MARKER_HALF_WIDTH_PX = 8;
const CAMERA_MARKER_RADIUS_PX = 4;

export function CityMapWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const mapRef = useRef<CityMapCanvasHandle>(null);
  const coordRef = useRef<HTMLSpanElement | null>(null);
  const zoomRef = useRef<HTMLSpanElement | null>(null);
  const navigation = useMapNavigation(apiBaseUrl, mapRef);
  const [camera, setCamera] = useState<CameraMarker | null>(null);

  /**
 * Cursor coordinates and zoom update on every mouse movement, so write directly to the DOM
 * without React state to avoid rerendering.
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

  /** Receive camera position/direction from Unity and request a marker redraw. */
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

    // Fetch the camera position when the map opens.
    void sendCameraQueryState().catch(console.error);

    return () => {
      unlisten?.();
    };
  }, []);

  /** Reload camera position together with map data. */
  const handleReload = useCallback(() => {
    mapRef.current?.reload();
    navigation.reload();
    void sendCameraQueryState().catch(console.error);
  }, [navigation.reload]);
  const controls = useCityMapControls(apiBaseUrl, mapRef, handleReload);

  /**
 * Camera position marker overlay. ctx already has the world-coordinate view transform, allowing direct world-coordinate
 * drawing (divide only dimensions that must stay constant on screen by zoom).
 */
  const drawCameraMarker = useCallback(
    (ctx: CanvasRenderingContext2D, view: CityMapViewState) => {
      navigation.draw(ctx, view);
      if (!camera) return;
      const px = (v: number) => v / view.zoom;

      // Convert azimuth (clockwise from north = +Z) to a horizontal unit vector.
      const yawRad = (camera.yaw * Math.PI) / 180;
      const dx = Math.sin(yawRad);
      const dz = Math.cos(yawRad);
      const tip = px(CAMERA_MARKER_TIP_PX);
      const half = px(CAMERA_MARKER_HALF_WIDTH_PX);

      // Triangle pointing in the viewing direction, with its base perpendicular to that direction.
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

      // Circle marking the camera position itself (draw at the position so it does not shift to the triangle's centroid).
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
      {/* Toolbar: layer toggles and action buttons. */}
      {controls.toolbar}

      {/* Map content. */}
      <div className="city-map-window__map">
        <CityMapCanvas
          ref={mapRef}
          apiBaseUrl={apiBaseUrl}
          layers={controls.layers}
          heatmap={controls.heatmap}
          heatmapMode={controls.heatmapMode}
          transitVariant={controls.transitVariant}
          infraSelection={controls.infraSelection}
          interactive
          onCursorWorldPos={handleCursor}
          onZoomChange={handleZoom}
          onHeatmapStats={controls.onHeatmapStats}
          overlayDraw={drawCameraMarker}
          overlayVersion={(camera?.seq ?? 0) + navigation.overlayVersion}
          onMapClick={navigation.onMapClick}
          cursor={navigation.picking ? "crosshair" : undefined}
          focusPathIds={navigation.roadFocus?.pathIds ?? null}
          focusColor={navigation.roadFocus?.color ?? null}
        />
        {controls.legend}
        {/* Status overlay (cursor world coordinates and zoom). */}
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
