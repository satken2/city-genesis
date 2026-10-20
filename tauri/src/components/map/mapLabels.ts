import { TYPE_EDUCATION, VARIANT_CITY, VARIANT_FIRE_STATION, VARIANT_POLICE_STATION, VARIANT_SUBSTATION } from "../../constants";
import { MAP_SYMBOLS, type MapSymbol } from "./mapSymbols";

/** station / interchange determine label priority and symbol colors (stations → interchanges → facilities). */
export type MapPlace = { id: number; x: number; z: number; name: string; symbol: MapSymbol; station?: boolean; interchange?: boolean };
export type MapDistrict = { name?: string | null; center_point?: number[] | null };
/** One road name label position. angleDeg is road orientation in world XZ (atan2(dz, dx) in degrees). */
export type MapRoadLabel = { roadId: number; name: string; color: string; x: number; z: number; angleDeg: number };

/**
 * Convert a world-XZ direction (degrees) to a screen text rotation (radians).
 * The map draws +Z upward, reversing the sign. Fold into (-90°, 90°] to keep text upright.
 */
export function roadLabelScreenAngle(angleDeg: number): number {
  let deg = ((-angleDeg % 360) + 360) % 360;
  if (deg > 270) deg -= 360;
  else if (deg > 90) deg -= 180;
  return (deg * Math.PI) / 180;
}

/** Do not classify by names or translations. Also include generic facilities based on the public ownership flag. */
export function facilitySymbol(variant: number | undefined, type: number | undefined, isPublic: boolean): MapSymbol | null {
  if (type === TYPE_EDUCATION) return "school";
  if (variant === VARIANT_CITY) return "cityHall";
  if (variant === VARIANT_FIRE_STATION) return "fire";
  if (variant === VARIANT_POLICE_STATION) return "police";
  if (variant === VARIANT_SUBSTATION) return "substation";
  const variants: Record<number, MapSymbol> = {
    1048: "intake", 1049: "water", 1051: "hospital", 1054: "power", 1055: "power",
    1056: "power", 1057: "power", 1058: "sewage", 1059: "waste", 1063: "water",
  };
  return (variant === undefined ? undefined : variants[variant]) ?? (isPublic ? "generic" : null);
}

const paths = new Map<MapSymbol, Path2D>();
type Box = { x: number; y: number; w: number; h: number };

const placeRank = (place: MapPlace) => (place.station ? 2 : place.interchange ? 1 : 0);

/**
 * Keep screen dimensions fixed and omit only overlapping names.
 * Placement priority: district names → station/interchange symbols → station/interchange names → road names → facility symbols → facility names.
 * Do not draw unnamed roads (empty name).
 */
export function drawMapLabels(ctx: CanvasRenderingContext2D, view: { offsetX: number; offsetY: number; zoom: number },
  width: number, height: number, districts: MapDistrict[], places: MapPlace[], roads: MapRoadLabel[] = []): void {
  const occupied: Box[] = [];
  const overlaps = (box: Box) => occupied.some((b) => box.x < b.x + b.w && box.x + box.w > b.x && box.y < b.y + b.h && box.y + box.h > b.y);
  const visible = (x: number, y: number) => x >= 20 && y >= 16 && x <= width - 20 && y <= height - 16;
  const text = (name: string, x: number, y: number, color: string) => {
    ctx.lineWidth = 3.5;
    ctx.strokeStyle = "rgba(255,255,255,0.94)";
    ctx.strokeText(name, x, y);
    ctx.fillStyle = color;
    ctx.fillText(name, x, y);
  };
  ctx.save();
  // The map CTM includes a Y flip for +Z up; cancel it together with scaling to return to screen-pixel coordinates.
  ctx.translate(view.offsetX, view.offsetY);
  ctx.scale(1 / view.zoom, -1 / view.zoom);
  ctx.setLineDash([]);
  ctx.textBaseline = "middle";
  ctx.textAlign = "center";
  ctx.lineJoin = "round";
  ctx.font = '600 16px system-ui, sans-serif';
  for (const district of districts) {
    const point = district.center_point;
    if (!district.name || !point || point.length < 2) continue;
    const x = (point[0] - view.offsetX) * view.zoom;
    const y = (view.offsetY - point[point.length >= 3 ? 2 : 1]) * view.zoom;
    if (!visible(x, y)) continue;
    const w = ctx.measureText(district.name).width;
    const box = { x: x - w / 2 - 5, y: y - 12, w: w + 10, h: 24 };
    if (overlaps(box)) continue;
    occupied.push(box);
    text(district.name, x, y, "#49595c");
  }
  const shown: { place: MapPlace; x: number; y: number; color: string }[] = [];
  // Keep ID order so fetch order does not affect label priority.
  const sorted = view.zoom >= 0.04 ? [...places].sort((a, b) => placeRank(b) - placeRank(a) || a.id - b.id) : [];
  const drawSymbols = (targets: MapPlace[]) => {
    for (const place of targets) {
      const x = (place.x - view.offsetX) * view.zoom, y = (view.offsetY - place.z) * view.zoom;
      if (!visible(x, y)) continue;
      const box = { x: x - 13, y: y - 13, w: 26, h: 26 };
      if (overlaps(box)) continue;
      occupied.push(box);
      const color = place.symbol === "hospital" || place.symbol === "fire" ? "#d95865"
        : place.station ? "#657f91" : place.interchange ? "#3f8660" : "#78939c";
      ctx.beginPath(); ctx.arc(x, y, 11, 0, Math.PI * 2);
      ctx.fillStyle = color; ctx.fill(); ctx.strokeStyle = "white"; ctx.lineWidth = 2.5; ctx.stroke();
      let path = paths.get(place.symbol);
      if (!path) { path = new Path2D(MAP_SYMBOLS[place.symbol]); paths.set(place.symbol, path); }
      ctx.save(); ctx.translate(x - 7, y - 7); ctx.scale(14 / 24, 14 / 24);
      ctx.strokeStyle = "white"; ctx.lineWidth = 2.3; ctx.lineCap = "round"; ctx.stroke(path); ctx.restore();
      shown.push({ place, x, y, color });
    }
  };
  /** Place names for successfully placed symbols in free space, trying the right side before the left. */
  const drawNames = (targets: typeof shown) => {
    if (view.zoom < 0.1) return;
    ctx.font = '12px system-ui, sans-serif'; ctx.textAlign = "left";
    for (const { place, x, y, color } of targets) {
      if (!place.name) continue;
      const w = ctx.measureText(place.name).width;
      for (const tx of [x + 17, x - 17 - w]) {
        const box = { x: tx - 3, y: y - 9, w: w + 6, h: 18 };
        if (box.x < 4 || box.x + box.w > width - 4 || overlaps(box)) continue;
        occupied.push(box); text(place.name, tx, y, color); break;
      }
    }
  };
  // Place all station/interchange symbols before their names, and reserve space for those names before road names.
  drawSymbols(sorted.filter((place) => placeRank(place) > 0));
  drawNames(shown.filter(({ place }) => placeRank(place) > 0));
  // Rotate road names along their roads. Approximate rotated-text collisions with text-height squares
  // along the baseline (one bounding rectangle would let long diagonal names block too much surrounding space).
  ctx.font = '600 13px system-ui, sans-serif';
  ctx.textAlign = "center";
  // Sort by road ID and position so placement outcomes do not depend on fetch order.
  const roadLabels = [...roads].sort((a, b) => a.roadId - b.roadId || a.x - b.x || a.z - b.z);
  for (const road of roadLabels) {
    if (!road.name) continue;
    const x = (road.x - view.offsetX) * view.zoom, y = (view.offsetY - road.z) * view.zoom;
    const angle = roadLabelScreenAngle(road.angleDeg);
    const w = ctx.measureText(road.name).width + 8;
    const size = 18;
    const count = Math.max(1, Math.ceil(w / size));
    const ux = Math.cos(angle), uy = Math.sin(angle);
    const boxes: Box[] = [];
    for (let i = 0; i < count; i++) {
      const t = -w / 2 + ((i + 0.5) * w) / count;
      boxes.push({ x: x + ux * t - size / 2, y: y + uy * t - size / 2, w: size, h: size });
    }
    if (!visible(x - (ux * w) / 2, y - (uy * w) / 2) || !visible(x + (ux * w) / 2, y + (uy * w) / 2)) continue;
    if (boxes.some(overlaps)) continue;
    occupied.push(...boxes);
    ctx.save();
    ctx.translate(x, y);
    ctx.rotate(angle);
    text(road.name, 0, 0, road.color);
    ctx.restore();
  }
  drawSymbols(sorted.filter((place) => placeRank(place) === 0));
  drawNames(shown.filter(({ place }) => placeRank(place) === 0));
  ctx.restore();
}
