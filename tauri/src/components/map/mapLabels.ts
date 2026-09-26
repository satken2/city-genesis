import { TYPE_EDUCATION, VARIANT_CITY, VARIANT_FIRE_STATION, VARIANT_POLICE_STATION, VARIANT_SUBSTATION } from "../../constants";
import { MAP_SYMBOLS, type MapSymbol } from "./mapSymbols";

/** station / interchange はラベル配置の優先度と記号色を決める(駅 → インターチェンジ → 施設の順)。 */
export type MapPlace = { id: number; x: number; z: number; name: string; symbol: MapSymbol; station?: boolean; interchange?: boolean };
export type MapDistrict = { name?: string | null; center_point?: number[] | null };
/** 道路名ラベル1か所。angleDeg はワールドXZ平面での道路の向き(atan2(dz, dx) の度)。 */
export type MapRoadLabel = { roadId: number; name: string; color: string; x: number; z: number; angleDeg: number };

/**
 * ワールドXZの向き(度)を、画面上で文字を回転させる角度(ラジアン)へ直す。
 * 地図は+Zを上に描くので画面の角度は符号が逆になる。文字が逆さにならないよう(-90°, 90°]へ畳む。
 */
export function roadLabelScreenAngle(angleDeg: number): number {
  let deg = ((-angleDeg % 360) + 360) % 360;
  if (deg > 270) deg -= 360;
  else if (deg > 90) deg -= 180;
  return (deg * Math.PI) / 180;
}

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

/**
 * 画面上の寸法を固定し、重なる名称だけを省く。
 * 配置の優先度は 地区名 → 駅・インターチェンジの記号 → 駅・インターチェンジの名前 → 道路名 → 施設記号 → 施設名。
 * 名前の無い道路(name が空)は描かない。
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
  // 地図のCTMは+Zを上にするY反転を含むので、拡縮と一緒にY反転も打ち消して画面ピクセル座標へ戻す。
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
  // ID順を保ち、取得順序によってラベルの優先順位が揺れないようにする。
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
  /** 記号を置けた場所の名前を、記号の右→左の順に空いている側へ置く */
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
  // 駅・インターチェンジは記号をすべて置いてから名前を置き、名前まで道路名より先に場所を取る
  drawSymbols(sorted.filter((place) => placeRank(place) > 0));
  drawNames(shown.filter(({ place }) => placeRank(place) > 0));
  // 道路名は道路の向きに沿って回転させる。回転した文字の当たり判定は、文字の高さ四方の箱を
  // 基線に沿って並べて近似する(外接矩形1つだと斜めの長い名前が周囲を広く塞いでしまうため)。
  ctx.font = '600 13px system-ui, sans-serif';
  ctx.textAlign = "center";
  // 道路IDと位置の順に並べ、取得順で配置の勝ち負けが揺れないようにする。
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
