import { TYPE_EDUCATION, VARIANT_CITY, VARIANT_FIRE_STATION, VARIANT_POLICE_STATION, VARIANT_SUBSTATION } from "../../constants";
import { MAP_SYMBOLS, type MapSymbol } from "./mapSymbols";

/** station / interchange はラベル配置の優先度と記号色を決める(駅 → インターチェンジ → 施設の順)。 */
export type MapPlace = { id: number; x: number; z: number; name: string; symbol: MapSymbol; station?: boolean; interchange?: boolean };
export type MapDistrict = { name?: string | null; center_point?: number[] | null };

/** 名前や翻訳文で判定しない。汎用施設も公共所有フラグから拾う。 */
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

/** 画面上の寸法を固定し、重なる名称だけを省く。地区名と施設記号を優先する。 */
export function drawMapLabels(ctx: CanvasRenderingContext2D, view: { offsetX: number; offsetY: number; zoom: number },
  width: number, height: number, districts: MapDistrict[], places: MapPlace[]): void {
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
  ctx.translate(view.offsetX, view.offsetY);
  ctx.scale(1 / view.zoom, 1 / view.zoom);
  ctx.setLineDash([]);
  ctx.textBaseline = "middle";
  ctx.textAlign = "center";
  ctx.lineJoin = "round";
  ctx.font = '600 16px system-ui, sans-serif';
  for (const district of districts) {
    const point = district.center_point;
    if (!district.name || !point || point.length < 2) continue;
    const x = (point[0] - view.offsetX) * view.zoom;
    const y = (point[point.length >= 3 ? 2 : 1] - view.offsetY) * view.zoom;
    if (!visible(x, y)) continue;
    const w = ctx.measureText(district.name).width;
    const box = { x: x - w / 2 - 5, y: y - 12, w: w + 10, h: 24 };
    if (overlaps(box)) continue;
    occupied.push(box);
    text(district.name, x, y, "#49595c");
  }
  if (view.zoom >= 0.04) {
    const shown: { place: MapPlace; x: number; y: number; color: string }[] = [];
    // ID順を保ち、取得順序によってラベルの優先順位が揺れないようにする。
    for (const place of [...places].sort((a, b) => placeRank(b) - placeRank(a) || a.id - b.id)) {
      const x = (place.x - view.offsetX) * view.zoom, y = (place.z - view.offsetY) * view.zoom;
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
    ctx.font = '12px system-ui, sans-serif'; ctx.textAlign = "left";
    for (const { place, x, y, color } of shown) {
      if (!place.name || view.zoom < 0.1) continue;
      const w = ctx.measureText(place.name).width;
      for (const tx of [x + 17, x - 17 - w]) {
        const box = { x: tx - 3, y: y - 9, w: w + 6, h: 18 };
        if (box.x < 4 || box.x + box.w > width - 4 || overlaps(box)) continue;
        occupied.push(box); text(place.name, tx, y, color); break;
      }
    }
  }
  ctx.restore();
}
