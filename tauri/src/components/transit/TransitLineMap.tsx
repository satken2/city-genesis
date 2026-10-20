/**
 * Line map (geographic route)
 *
 * Map embedded in TransitDetailPanel focusing on one line. Embed shared CityMapCanvas with
 * interactive={false}, enable transitLines, and set transitFocusLineId to this line.
 * The layer (map/transitLineDraw.ts) owns all rendering rules: bands following underlying
 * roads/tracks, direction arrows, stop rings (start=green, end=red), suspended dashes,
 * and faint neighboring lines. This component draws nothing itself; CityMapCanvas layers
 * also draw surrounding roads, tracks, parcels, and buildings.
 *
 * Fit to the stops' bounding box plus padding, using stops from the detail API.
 * The layer fetches running geometry from GET /api/v1/transit/lines/geometry.
 * After settings save, the layer receives transit:lines_changed and refetches changed colors.
 */

import { useEffect, useMemo, useRef } from "react";
import {
  CityMapCanvas,
  type CityMapCanvasHandle,
  type CityMapBounds,
  type CityMapLayerVisibility,
} from "../map/CityMapCanvas";
import type { TransitStop } from "./TransitDetailPanel";
import "./TransitLineMap.css";

/** Padding around stop bounds (world meters), preserving a visible area even for a single stop. */
const FIT_MARGIN = 60;

type TransitLineMapProps = {
  apiBaseUrl: string;
  /** line_agent_id of the focused line. */
  lineAgentId: number;
  /** Stops determining view bounds (detail API stops). */
  stops: TransitStop[];
};

/**
 * Line map layers. Show surrounding roads (including main roads), tracks, parcels, and buildings
 * for geographic context. Hide elevation coloring, contours, grid, and district outlines
 * for route readability. Keep coastlines as location references.
 */
const MAP_LAYERS: Partial<CityMapLayerVisibility> = {
  coastline: true,
  trunkRoads: true,
  allRoads: true,
  railways: true,
  transitLines: true,
  parcels: true,
  buildings: true,
  stations: true,
  zoneBorders: false,
  elevation: false,
  contours: false,
  grid: false,
};

/** World bounds enclosing stops, including padding. Null if empty. */
function computeStopsBounds(stops: TransitStop[]): CityMapBounds | null {
  if (stops.length === 0) return null;
  let minX = Infinity;
  let minZ = Infinity;
  let maxX = -Infinity;
  let maxZ = -Infinity;
  for (const s of stops) {
    const x = s.position[0];
    const z = s.position[2];
    if (x < minX) minX = x;
    if (z < minZ) minZ = z;
    if (x > maxX) maxX = x;
    if (z > maxZ) maxZ = z;
  }
  return {
    x1: minX - FIT_MARGIN,
    z1: minZ - FIT_MARGIN,
    x2: maxX + FIT_MARGIN,
    z2: maxZ + FIT_MARGIN,
  };
}

export function TransitLineMap({ apiBaseUrl, lineAgentId, stops }: TransitLineMapProps) {
  const mapRef = useRef<CityMapCanvasHandle>(null);

  // Initial fitted bounds. Later changes follow through fitBounds(), so keep this value unchanged
  // to avoid rerunning CityMapCanvas's initialization effect (intentionally empty dependencies).
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const initialBounds = useMemo(() => computeStopsBounds(stops) ?? undefined, []);

  /* ---- Follow stop changes in the view ---- */
  useEffect(() => {
    const bounds = computeStopsBounds(stops);
    if (bounds) {
      mapRef.current?.fitBounds(bounds);
    }
  }, [stops]);

  return (
    <div className="transit-line-map">
      <CityMapCanvas
        ref={mapRef}
        apiBaseUrl={apiBaseUrl}
        layers={MAP_LAYERS}
        transitFocusLineId={lineAgentId}
        initialBounds={initialBounds}
        interactive={false}
        className="transit-line-map__canvas"
      />
    </div>
  );
}
