/**
 * Road overview (road detail Overview tab).
 *
 * Embed CityMapCanvas and highlight road sections with road-colored bands (focusPathIds/focusColor), dimming other
 * roads/tracks with focusDimOthers. Overlay straight-ahead extension candidates as same-colored dashed lines
 * (previewPathIds). CityMapCanvas owns band rendering rules; this component only determines display bounds.
 *
 * CityMapCanvas fetches vectors by display bbox, so sections outside it cannot be loaded or highlighted.
 * Fit to the road bbox combined with candidate bounds, add padding, and track changes to those bounds.
 */

import { useEffect, useMemo, useRef } from "react";
import {
  CityMapCanvas,
  type CityMapCanvasHandle,
  type CityMapBounds,
  type CityMapLayerVisibility,
} from "../map/CityMapCanvas";
import "./RoadOverviewMap.css";

/** Minimum bbox padding (m), keeping surrounding streets visible even for short roads. */
const FIT_MARGIN_MIN = 80;
/** Padding fraction relative to the bbox's longer side. */
const FIT_MARGIN_RATIO = 0.08;

/** Keep nearby roads/tracks/parcels/buildings for spatial context; hide terrain coloring and grids. */
const MAP_LAYERS: Partial<CityMapLayerVisibility> = {
  coastline: true,
  trunkRoads: true,
  allRoads: true,
  railways: true,
  transitLines: false,
  parcels: false,
  buildings: true,
  stations: true,
  interchanges: true,
  zoneBorders: false,
  elevation: false,
  contours: false,
  grid: false,
};

type RoadOverviewMapProps = {
  apiBaseUrl: string;
  bbox: [number, number, number, number] | null;
  pathIds: number[];
  color: string;
  /** Unconfirmed addition candidates (automatic straight-ahead extension). */
  previewPathIds: number[] | null;
  /** Candidate bounds [minx, minz, maxx, maxz]. */
  previewBbox?: [number, number, number, number] | null;
};

type Bbox = [number, number, number, number];

function unionBbox(a: Bbox | null, b: Bbox | null | undefined): Bbox | null {
  if (!a) return b ?? null;
  if (!b) return a;
  return [Math.min(a[0], b[0]), Math.min(a[1], b[1]), Math.max(a[2], b[2]), Math.max(a[3], b[3])];
}

function boundsFromBbox(bbox: [number, number, number, number] | null): CityMapBounds | null {
  if (!bbox) return null;
  const [minX, minZ, maxX, maxZ] = bbox;
  const margin = Math.max(FIT_MARGIN_MIN, Math.max(maxX - minX, maxZ - minZ) * FIT_MARGIN_RATIO);
  return { x1: minX - margin, z1: minZ - margin, x2: maxX + margin, z2: maxZ + margin };
}

export function RoadOverviewMap({ apiBaseUrl, bbox, pathIds, color, previewPathIds, previewBbox }: RoadOverviewMapProps) {
  const mapRef = useRef<CityMapCanvasHandle>(null);
  // Fit candidates when they appear; return to road bounds when confirmation/cancellation removes them.
  const fitBbox = unionBbox(bbox, previewPathIds && previewPathIds.length > 0 ? previewBbox : null);
  const fitKey = fitBbox ? fitBbox.join(",") : "";

  // Pass initial bounds only on first mount; use fitBounds for later changes without rerunning initialization.
  // Candidates often appear in Settings before Overview first opens, so include them in initial bounds.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const initialBounds = useMemo(() => boundsFromBbox(fitBbox) ?? undefined, []);

  useEffect(() => {
    const bounds = boundsFromBbox(fitBbox);
    if (bounds) mapRef.current?.fitBounds(bounds);
    // bbox is a new array on every refetch; compare serialized values.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fitKey]);

  return (
    <div className="road-overview-map">
      <CityMapCanvas
        ref={mapRef}
        apiBaseUrl={apiBaseUrl}
        layers={MAP_LAYERS}
        initialBounds={initialBounds}
        interactive={true}
        showCompass={true}
        focusPathIds={pathIds}
        focusColor={color}
        focusDimOthers={true}
        previewPathIds={previewPathIds}
        className="road-overview-map__canvas"
      />
    </div>
  );
}
