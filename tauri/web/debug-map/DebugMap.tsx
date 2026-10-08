/**
 * 開発用の地図ページで、3D 画面(Unity)の代わりに上部バーの下の全面へ置く都市の地図。
 *
 * 3D 画面と同じく、建物・パスをタップするとその詳細ウィンドウを開く(Unity の world:select_object と同じ
 * agent_detail・path_detail)。当たり判定は地図に描いているベクターで行い、タップ位置から画面上で
 * TAP_TOLERANCE_PX 以内のうち最も近いものを選ぶ。指で細い道路も拾えるよう、図形の外側も許容する。
 * 内側(距離0)同士なら先に調べる建物を選ぶ。
 *
 * Unity が受けるセッションの再読み込み(session:reload)は、Web シェルが同じ名前のイベントでこの地図へ渡す。
 */

import { useCallback, useEffect, useRef } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import {
  CityMapCanvas,
  type CityMapCanvasHandle,
  type CityMapPointerEvent,
  type CityMapVectors,
} from "../../src/components/map/CityMapCanvas";
import { useCityMapControls } from "../../src/components/map/CityMapControls";
import { pointInRing, type Ring } from "../../src/components/zone/zoneBrushTargets";
import "../../src/components/map/CityMapWindow.css";

/** タップの当たり判定の許容距離(画面ピクセル) */
const TAP_TOLERANCE_PX = 12;

/** 点から多角形までの距離。内側は0。多角形はローカル座標で、(ox, oz) がその原点 */
function distanceToPolygon(x: number, z: number, ring: Ring, ox: number, oz: number): number {
  const px = x - ox;
  const pz = z - oz;
  if (pointInRing(px, pz, ring)) return 0;
  let best = Infinity;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const [ax, az] = ring[j];
    const [bx, bz] = ring[i];
    const dx = bx - ax;
    const dz = bz - az;
    const lengthSq = dx * dx + dz * dz;
    const t = lengthSq > 0 ? Math.max(0, Math.min(1, ((px - ax) * dx + (pz - az) * dz) / lengthSq)) : 0;
    best = Math.min(best, Math.hypot(px - (ax + dx * t), pz - (az + dz * t)));
  }
  return best;
}

export function DebugMap({ apiBaseUrl }: { apiBaseUrl: string }) {
  const mapRef = useRef<CityMapCanvasHandle>(null);
  const vectorsRef = useRef<CityMapVectors | null>(null);
  const handleReload = useCallback(() => mapRef.current?.reload(), []);
  const controls = useCityMapControls(mapRef, handleReload);

  useEffect(() => {
    const unlisten = listen("session:reload", handleReload);
    return () => {
      void unlisten.then((fn) => fn());
    };
  }, [handleReload]);

  const handleVectorsLoaded = useCallback((vectors: CityMapVectors) => {
    vectorsRef.current = vectors;
  }, []);

  const handleMapClick = useCallback((event: CityMapPointerEvent) => {
    const vectors = vectorsRef.current;
    if (!vectors) return;
    const { x, z } = event.world;
    const candidates = [
      ...vectors.buildings.map((building) => ({
        windowType: "agent_detail",
        id: building.agent_id,
        ring: building.polygon,
        origin: building.center_point ?? [0, 0],
      })),
      ...vectors.paths.map((path) => ({ windowType: "path_detail", id: path.path_id, ring: path.polygon, origin: [0, 0] })),
    ];
    let best: { windowType: string; id: number; distance: number } | null = null;
    for (const { windowType, id, ring, origin } of candidates) {
      if (id === undefined || !ring || ring.length < 3) continue;
      const distance = distanceToPolygon(x, z, ring, origin[0], origin[1]);
      if (distance <= TAP_TOLERANCE_PX / event.view.zoom && (!best || distance < best.distance)) best = { windowType, id, distance };
    }
    if (best) {
      void invoke("open_registered_window", { windowType: best.windowType, entityId: String(best.id) }).catch(console.error);
    }
  }, []);

  return (
    <div className="city-map-window">
      <div className="city-map-window__main">
        {controls.toolbar}
        <div className="city-map-window__map">
          <CityMapCanvas
            ref={mapRef}
            apiBaseUrl={apiBaseUrl}
            layers={controls.layers}
            heatmap={controls.heatmap}
            heatmapMode={controls.heatmapMode}
            interactive
            onHeatmapStats={controls.onHeatmapStats}
            onVectorsLoaded={handleVectorsLoaded}
            onMapClick={handleMapClick}
          />
          {controls.legend}
        </div>
      </div>
    </div>
  );
}
