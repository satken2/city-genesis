/**
 * 行程の片道経路を自宅・訪問先と重ね、訪問先の番号を下のリストと対応させる。
 * 形状も行程APIの応答を使い、別の経路探索結果と混在させない。
 */
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import type { CitizenItinerary, CitizenVisit } from "../../utils/citizenTracking";
import { CityMapCanvas, type CityMapBounds, type CityMapViewState } from "../map/CityMapCanvas";
import { drawMapRoute, type MapRouteDrawLeg } from "../map/mapRoute";

type Props = { apiBaseUrl: string; itinerary: CitizenItinerary };
type PositionedVisit = CitizenVisit & { x: number; z: number };
const MAP_LAYERS = { coastline: true };

function hasPosition(visit: CitizenVisit): visit is PositionedVisit {
  return visit.x !== null && visit.z !== null && Number.isFinite(visit.x) && Number.isFinite(visit.z);
}

function mapBounds(points: number[][]): CityMapBounds | undefined {
  if (!points.length) return undefined;
  let x1 = Infinity, x2 = -Infinity, z1 = Infinity, z2 = -Infinity;
  for (const [x, z] of points) {
    x1 = Math.min(x1, x); x2 = Math.max(x2, x);
    z1 = Math.min(z1, z); z2 = Math.max(z2, z);
  }
  const margin = Math.max(60, Math.max(x2 - x1, z2 - z1) * 0.1);
  return { x1: x1 - margin, x2: x2 + margin, z1: z1 - margin, z2: z2 + margin };
}

export function CitizenActivityMap({ apiBaseUrl, itinerary }: Props) {
  const { t } = useTranslation();
  const [overlayVersion, setOverlayVersion] = useState(0);
  useEffect(() => setOverlayVersion(version => version + 1), [itinerary, t]);

  const visits = itinerary.visits ?? [];
  const legs: MapRouteDrawLeg[] = [];
  for (const trip of itinerary.trips ?? []) {
    if (trip.status !== "ok" || trip.from_visit !== 0 || trip.to_visit <= 0) continue;
    const route = (trip.legs ?? []).filter(leg => leg.points?.length > 0);
    if (!route.length) continue;
    const from = visits[trip.from_visit], to = visits[trip.to_visit];
    // 行程の形状は道路上で始まり終わるので、自宅・施設との接続だけ点線で補う。
    if (from && hasPosition(from)) {
      legs.push({ kind: "access", color: null, points: [[from.x, from.z], route[0].points[0]] });
    }
    legs.push(...route);
    if (to && hasPosition(to)) {
      const points = route[route.length - 1].points;
      legs.push({ kind: "access", color: null, points: [points[points.length - 1], [to.x, to.z]] });
    }
  }
  const bounds = mapBounds([
    ...visits.filter(hasPosition).map(visit => [visit.x, visit.z]),
    ...legs.flatMap(leg => leg.points),
  ]);
  if (!bounds) return null;

  function draw(ctx: CanvasRenderingContext2D, view: CityMapViewState) {
    drawMapRoute(ctx, view, legs);
    const px = (n: number) => n / view.zoom;
    const style = getComputedStyle(document.documentElement);
    const outline = style.getPropertyValue("--flat-sunken").trim();
    // 同一の建物に複数の訪問先が入る場合、最後の番号だけで他を隠さないよう1つの札にまとめる。
    const markers = new Map<string, { x: number; z: number; home: boolean; labels: string[] }>();
    visits.forEach((visit, index) => {
      if (!hasPosition(visit)) return;
      const key = `${visit.x},${visit.z}`;
      const marker = markers.get(key) ?? { x: visit.x, z: visit.z, home: false, labels: [] };
      marker.home ||= index === 0;
      marker.labels.push(index === 0 ? t("citizenTracking.visitKind.home") : String(index));
      markers.set(key, marker);
    });
    markers.forEach(marker => {
      ctx.save();
      ctx.translate(marker.x, marker.z);
      // 地図は+Zを上へ向けるためY反転されている。札と文字は反転を打ち消して描く。
      ctx.scale(1, -1);
      ctx.font = `bold ${px(12)}px sans-serif`;
      const label = marker.labels.join("・");
      const width = Math.max(px(24), ctx.measureText(label).width + px(12));
      ctx.beginPath();
      ctx.roundRect(-width / 2, px(-12), width, px(24), px(12));
      ctx.fillStyle = style.getPropertyValue(marker.home ? "--flat-good" : "--flat-warn").trim();
      ctx.fill();
      ctx.strokeStyle = outline;
      ctx.lineWidth = px(2);
      ctx.stroke();
      ctx.fillStyle = outline;
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.fillText(label, 0, 0);
      ctx.restore();
    });
  }

  return (
    <div className="citizen-activity-map" role="group" aria-label={t("citizenTracking.visits")}>
      <CityMapCanvas apiBaseUrl={apiBaseUrl} initialBounds={bounds} layers={MAP_LAYERS}
        overlayDraw={draw} overlayVersion={overlayVersion} interactive showCompass={false} />
    </div>
  );
}
