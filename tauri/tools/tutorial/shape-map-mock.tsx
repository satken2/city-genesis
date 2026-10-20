import React, { forwardRef, useImperativeHandle } from "react";
import type { CityMapCanvasHandle, CityMapPointerEvent } from "../../src/components/map/CityMapCanvas";
export const DEFAULT_CITY_MAP_LAYERS = {};
// 地形取得と描画だけを省き、実パネルのノード操作へ座標付きイベントを渡す。
export const CityMapCanvas = forwardRef<CityMapCanvasHandle, any>((props, ref) => {
  useImperativeHandle(ref, () => ({ resetView() {}, reload() {}, fitBounds() {}, getView: () => ({ offsetX: -100, offsetY: 100, zoom: 1 }) }));
  const pointer = (e: React.PointerEvent): CityMapPointerEvent => ({ screen: { x: e.clientX, y: e.clientY }, world: { x: e.clientX - 100, z: 100 - e.clientY }, view: { offsetX: -100, offsetY: 100, zoom: 1 }, button: e.button, event: e.nativeEvent });
  return <canvas data-testid="plan" onPointerDown={e => props.onPointerDown?.(pointer(e))} onPointerMove={e => props.onPointerMove?.(pointer(e))} onPointerUp={e => props.onPointerUp?.(pointer(e))} />;
});
