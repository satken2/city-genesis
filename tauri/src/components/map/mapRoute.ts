import type { CityMapViewState } from "./CityMapCanvas";

export type MapRouteLeg = {
  kind: string; points: number[][]; path_ids: number[]; line_id: number | null;
  name: string; color: string | null; minutes: number; distance_m: number;
  from_stop: string | null; to_stop: string | null;
};

export type MapRoute = {
  status: string; legs?: MapRouteLeg[]; minutes?: number; distance_m?: number;
  fare?: number; epoch?: number; snapshot?: string;
};

export type MapRouteDrawLeg = Pick<MapRouteLeg, "kind" | "points" | "color">;

/** 徒歩と道路への接続は点線、公共交通は路線色で示す。線幅はズームによらず画面上で一定にする。 */
export function drawMapRoute(
  ctx: CanvasRenderingContext2D,
  view: CityMapViewState,
  legs: MapRouteDrawLeg[],
  activeLeg: number | null = null,
) {
  const px = (n: number) => n / view.zoom;
  const style = getComputedStyle(document.documentElement);
  const color = (token: string) => style.getPropertyValue(`--flat-${token}`).trim();
  ctx.save();
  ctx.lineJoin = "round";
  ctx.lineCap = "round";
  legs.forEach((leg, index) => {
    if (!leg.points.length) return;
    const walk = leg.kind === "walk" || leg.kind === "access";
    ctx.beginPath();
    leg.points.forEach((p, i) => i ? ctx.lineTo(p[0], p[1]) : ctx.moveTo(p[0], p[1]));
    ctx.globalAlpha = activeLeg !== null && activeLeg !== index ? 0.35 : 1;
    ctx.setLineDash([]);
    ctx.strokeStyle = color("sunken");
    ctx.lineWidth = px(activeLeg === index ? 10 : 8);
    ctx.stroke();
    ctx.setLineDash(walk ? [px(2), px(8)] : []);
    ctx.strokeStyle = leg.color && /^#[0-9a-f]{6}$/i.test(leg.color) ? leg.color : color(walk ? "text-label" : "accent");
    ctx.lineWidth = px(activeLeg === index ? 7 : 5);
    ctx.stroke();
  });
  ctx.restore();
}
