/**
 * Pins tab of the city map. Clicking the map places a pin in the chosen color and right-clicking a pin removes it.
 *
 * Unity shows the same pins in 3D and holds the list for the session, because the pins must stay after the map closes.
 * The map therefore reads the list back from Unity when it opens and sends the whole list after each change.
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { listen } from "@tauri-apps/api/event";
import { ROAD_COLOR_PALETTE } from "../../constants";
import { IPC_EVENT_MAP_PINS, sendQueryMapPins, sendSetMapPins, type MapPin } from "../../utils/ipc";
import { ColorSwatchPicker } from "../common/ColorSwatchPicker";
import type { CityMapPointerEvent, CityMapViewState } from "./CityMapCanvas";
import "./CityMapPins.css";

const PIN_PRESETS = ROAD_COLOR_PALETTE.map((c) => c.toLowerCase());

/** 2D marker: an inverted triangle with its tip on the pin, matching the inverted pyramid in 3D (screen pixels). */
const PIN_HEIGHT_PX = 22;
const PIN_HALF_WIDTH_PX = 9;
const PIN_EDGE = "#1e1e2e";
/** Right-click radius around the marker's center (screen pixels). */
const PIN_HIT_RADIUS_PX = 12;

export function useMapPins() {
  const { t } = useTranslation();
  const [pins, setPins] = useState<MapPin[]>([]);
  const [color, setColor] = useState(PIN_PRESETS[0]);
  const [overlayVersion, setOverlayVersion] = useState(0);

  useEffect(() => {
    let disposed = false;
    let unlisten: (() => void) | undefined;
    listen<{ pins?: MapPin[] }>(IPC_EVENT_MAP_PINS, (event) => setPins(event.payload?.pins ?? []))
      .then((fn) => {
        if (disposed) { fn(); return; }
        unlisten = fn;
        return sendQueryMapPins();
      })
      .catch(console.error);
    return () => { disposed = true; unlisten?.(); };
  }, []);

  useEffect(() => { setOverlayVersion((v) => v + 1); }, [pins]);

  const update = useCallback((next: MapPin[]) => {
    setPins(next);
    void sendSetMapPins(next).catch(console.error);
  }, []);

  const onMapClick = useCallback((event: CityMapPointerEvent) => {
    update([...pins, { x: event.world.x, z: event.world.z, color }]);
  }, [pins, color, update]);

  const onContextMenu = useCallback((event: CityMapPointerEvent) => {
    const zoom = event.view.zoom;
    let hit = -1;
    let hitDistance = PIN_HIT_RADIUS_PX;
    pins.forEach((pin, i) => {
      const distance = Math.hypot((event.world.x - pin.x) * zoom, (event.world.z - pin.z) * zoom - PIN_HEIGHT_PX / 2);
      if (distance <= hitDistance) { hit = i; hitDistance = distance; }
    });
    if (hit >= 0) update(pins.filter((_, i) => i !== hit));
  }, [pins, update]);

  const draw = useCallback((ctx: CanvasRenderingContext2D, view: CityMapViewState) => {
    const px = (n: number) => n / view.zoom;
    ctx.save();
    ctx.globalAlpha = 1;
    ctx.setLineDash([]);
    ctx.lineJoin = "round";
    ctx.strokeStyle = PIN_EDGE;
    ctx.lineWidth = px(1.5);
    for (const pin of pins) {
      ctx.beginPath();
      ctx.moveTo(pin.x, pin.z);
      ctx.lineTo(pin.x - px(PIN_HALF_WIDTH_PX), pin.z + px(PIN_HEIGHT_PX));
      ctx.lineTo(pin.x + px(PIN_HALF_WIDTH_PX), pin.z + px(PIN_HEIGHT_PX));
      ctx.closePath();
      ctx.fillStyle = pin.color;
      ctx.fill();
      ctx.stroke();
    }
    ctx.restore();
  }, [pins]);

  const panel = (
    <div className="map-pins">
      <ColorSwatchPicker value={color} onChange={setColor} presets={PIN_PRESETS} />
      <p className="map-pins__hint">{t("mapNavigation.pins.hint")}</p>
      <ul className="map-pins__list" aria-label={t("mapNavigation.pins.list")}>
        {pins.map((pin, i) => (
          <li key={i}>
            <span className="map-pins__swatch" style={{ background: pin.color }} aria-hidden="true" />
            <span className="map-pins__coord">{`${pin.x.toFixed(0)}, ${pin.z.toFixed(0)}`}</span>
            <button type="button" aria-label={t("mapNavigation.pins.remove")}
              onClick={() => update(pins.filter((_, j) => j !== i))}>×</button>
          </li>
        ))}
      </ul>
    </div>
  );

  return { panel, draw, overlayVersion, onMapClick, onContextMenu };
}
