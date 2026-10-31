/**
 * 紋章・市旗の図柄の描画と、保存用の画像への焼き込み。
 *
 * 紋章は盾の形の地に図柄を1つ置き、市旗は地模様の上に紋章を置く。どちらも「図柄の組み合わせ」か
 * 「読み込んだ画像」のどちらかで、画像があれば画像を使う。コアには焼き込んだ PNG だけを保存し、
 * 図柄の組み合わせは保存しない。
 *
 * 図柄は 100×100 の座標で描く。図柄の穴(花の芯・歯車の穴など)は destination-out で抜くため、
 * 図柄だけを別のキャンバスに描いてから盾に重ねる。
 */

export const EMBLEM_SIZE = 256;
export const FLAG_WIDTH = 384;
export const FLAG_HEIGHT = 256;

export const MOTIF_IDS = [
  "wave", "mountain", "star", "tree", "sun", "moon", "flower", "leaf", "bird", "fish",
  "anchor", "gear", "tower", "bridge", "key", "crown", "snowflake", "flame", "diamond", "cross",
] as const;
export type MotifId = typeof MOTIF_IDS[number];

export const PATTERN_IDS = ["plain", "bicolor", "triband", "diagonal", "border", "bands"] as const;
export type PatternId = typeof PATTERN_IDS[number];

export const PLACEMENT_IDS = ["center", "canton", "none"] as const;
export type PlacementId = typeof PLACEMENT_IDS[number];

export type EmblemDesign = { motif: MotifId; field: string; color: string };
export type FlagDesign = { pattern: PatternId; field: string; accent: string; placement: PlacementId };

/** 図柄の組み合わせと、それに代わる読み込んだ画像。 */
export type Insignia<D> = { design: D; image: ImageBitmap | null };

export const INSIGNIA_COLOR_PRESETS = [
  "#9b1c2c", "#d62828", "#f77f00", "#f2c14e", "#2a9d4f", "#1b4332", "#3a86c8",
  "#1d3557", "#5a2a82", "#6b4226", "#8d99ae", "#f4f1e8", "#1a1a1a",
] as const;

/** おまかせの配色 [濃い色, 明るい色, 差し色]。紋章は濃い地に明るい図柄、市旗はどちらかの地に差し色を合わせる。 */
const PALETTES: readonly (readonly [string, string, string])[] = [
  ["#1d3557", "#f4f1e8", "#d62828"],
  ["#1b4332", "#f2c14e", "#f4f1e8"],
  ["#9b1c2c", "#f2c14e", "#1a1a1a"],
  ["#3a86c8", "#f4f1e8", "#f2c14e"],
  ["#5a2a82", "#f2c14e", "#f4f1e8"],
  ["#1a1a1a", "#f2c14e", "#d62828"],
  ["#2a9d4f", "#f4f1e8", "#1d3557"],
  ["#6b4226", "#f2c14e", "#2a9d4f"],
  ["#d62828", "#f4f1e8", "#1d3557"],
  ["#1d3557", "#f2c14e", "#3a86c8"],
  ["#f77f00", "#1a1a1a", "#f4f1e8"],
  ["#8d99ae", "#1d3557", "#d62828"],
];

type Ctx = CanvasRenderingContext2D;

const SHIELD = new Path2D("M12 8 H88 V46 C88 70 72 86 50 95 C28 86 12 70 12 46 Z");
const SHIELD_RIM = new Path2D("M17 13 H83 V46 C83 66 69 80 50 89 C31 80 17 66 17 46 Z");
/** 盾の中で図柄を描く範囲(盾の 100×100 座標での左上と一辺)。 */
const MOTIF_BOX = { x: 24, y: 21, size: 52 };

function fillPath(c: Ctx, d: string) {
  c.fill(new Path2D(d));
}

function strokePath(c: Ctx, d: string, width: number) {
  c.lineWidth = width;
  c.stroke(new Path2D(d));
}

function circle(c: Ctx, x: number, y: number, r: number) {
  c.beginPath();
  c.arc(x, y, r, 0, Math.PI * 2);
  c.fill();
}

function star(c: Ctx, x: number, y: number, outer: number, inner: number, points: number) {
  c.beginPath();
  for (let i = 0; i < points * 2; i++) {
    const r = i % 2 === 0 ? outer : inner;
    const angle = -Math.PI / 2 + (i * Math.PI) / points;
    c.lineTo(x + r * Math.cos(angle), y + r * Math.sin(angle));
  }
  c.closePath();
  c.fill();
}

/** 図柄から形を抜く。図柄は専用のキャンバスに描くので、下の盾までは抜けない。 */
function cut(c: Ctx, draw: () => void) {
  c.save();
  c.globalCompositeOperation = "destination-out";
  draw();
  c.restore();
}

/** 図柄の色を fillStyle・strokeStyle に設定済みの文脈へ、100×100 の座標で描く。 */
const MOTIFS: Record<MotifId, (c: Ctx) => void> = {
  wave: c => {
    for (const y of [28, 50, 72]) strokePath(c, `M8 ${y} q10.5 -13 21 0 t21 0 t21 0 t21 0`, 8);
  },
  mountain: c => fillPath(c, "M4 86 L36 26 L52 54 L66 32 L96 86 Z"),
  star: c => star(c, 50, 54, 46, 19, 5),
  tree: c => {
    fillPath(c, "M50 6 L74 36 H62 L82 60 H66 L88 84 H12 L34 60 H18 L38 36 H26 Z");
    c.fillRect(43, 84, 14, 12);
  },
  sun: c => {
    circle(c, 50, 50, 21);
    for (let i = 0; i < 12; i++) {
      const angle = (i * Math.PI) / 6;
      const [dx, dy] = [Math.cos(angle), Math.sin(angle)];
      strokePath(c, `M${50 + dx * 30} ${50 + dy * 30} L${50 + dx * 44} ${50 + dy * 44}`, 6);
    }
  },
  moon: c => {
    circle(c, 46, 52, 40);
    cut(c, () => circle(c, 64, 40, 34));
  },
  flower: c => {
    for (let i = 0; i < 5; i++) {
      const angle = -Math.PI / 2 + (i * 2 * Math.PI) / 5;
      circle(c, 50 + 24 * Math.cos(angle), 52 + 24 * Math.sin(angle), 17);
    }
    cut(c, () => circle(c, 50, 52, 11));
    circle(c, 50, 52, 6);
  },
  leaf: c => {
    fillPath(c, "M50 6 C82 24 86 62 50 94 C14 62 18 24 50 6 Z");
    cut(c, () => {
      strokePath(c, "M50 20 V86", 4);
      strokePath(c, "M50 44 L66 32 M50 44 L34 32 M50 64 L68 52 M50 64 L32 52", 3);
    });
  },
  bird: c => fillPath(c, "M6 46 Q28 22 50 48 Q72 22 94 46 Q72 36 50 66 Q28 36 6 46 Z"),
  fish: c => {
    c.beginPath();
    c.ellipse(42, 50, 32, 20, 0, 0, Math.PI * 2);
    c.fill();
    fillPath(c, "M66 50 L94 28 L88 50 L94 72 Z");
    cut(c, () => circle(c, 24, 45, 4.5));
  },
  anchor: c => {
    c.lineWidth = 6;
    c.beginPath();
    c.arc(50, 16, 9, 0, Math.PI * 2);
    c.stroke();
    c.fillRect(46, 24, 8, 64);
    c.fillRect(30, 32, 40, 8);
    strokePath(c, "M18 58 Q22 86 50 90 Q78 86 82 58", 8);
    fillPath(c, "M10 62 L18 48 L26 62 Z M74 62 L82 48 L90 62 Z");
  },
  gear: c => {
    c.save();
    c.translate(50, 50);
    for (let i = 0; i < 8; i++) {
      c.rotate(Math.PI / 4);
      c.fillRect(-7, -46, 14, 16);
    }
    c.restore();
    circle(c, 50, 50, 34);
    cut(c, () => circle(c, 50, 50, 13));
  },
  tower: c => {
    c.fillRect(18, 52, 64, 40);
    c.fillRect(18, 42, 12, 10);
    c.fillRect(70, 42, 12, 10);
    c.fillRect(34, 22, 32, 70);
    for (const x of [34, 46, 58]) c.fillRect(x, 12, 8, 10);
    cut(c, () => {
      fillPath(c, "M42 92 V72 A8 8 0 0 1 58 72 V92 Z");
      c.fillRect(46, 34, 8, 12);
    });
  },
  bridge: c => fillPath(c, "M4 38 H96 V50 H90 V88 H76 V74 A26 26 0 0 0 24 74 V88 H10 V50 H4 Z"),
  key: c => {
    circle(c, 28, 50, 20);
    cut(c, () => circle(c, 28, 50, 9));
    c.fillRect(44, 45, 50, 10);
    c.fillRect(72, 55, 7, 14);
    c.fillRect(84, 55, 7, 10);
  },
  crown: c => {
    fillPath(c, "M14 76 L8 30 L32 50 L50 18 L68 50 L92 30 L86 76 Z");
    c.fillRect(14, 80, 72, 10);
    circle(c, 8, 28, 6);
    circle(c, 50, 16, 6);
    circle(c, 92, 28, 6);
  },
  snowflake: c => {
    c.save();
    c.translate(50, 50);
    for (let i = 0; i < 6; i++) {
      c.rotate(Math.PI / 3);
      strokePath(c, "M0 0 V-44 M0 -26 L-12 -38 M0 -26 L12 -38", 6);
    }
    c.restore();
  },
  flame: c => {
    fillPath(c, "M50 4 C58 24 82 38 80 64 A30 30 0 0 1 20 64 C20 46 32 38 36 22 C40 34 44 40 48 42 C50 30 48 16 50 4 Z");
    cut(c, () => fillPath(c, "M50 50 C56 60 66 66 64 76 A14 14 0 0 1 36 76 C34 66 44 60 50 50 Z"));
  },
  diamond: c => {
    fillPath(c, "M50 4 L96 50 L50 96 L4 50 Z");
    cut(c, () => fillPath(c, "M50 18 L82 50 L50 82 L18 50 Z"));
    fillPath(c, "M50 32 L68 50 L50 68 L32 50 Z");
  },
  cross: c => {
    c.fillRect(38, 8, 24, 84);
    c.fillRect(8, 38, 84, 24);
  },
};

/** 地の色の上に差し色の部分を描く(fillStyle は差し色に設定済み)。 */
const PATTERNS: Record<PatternId, (c: Ctx, w: number, h: number) => void> = {
  plain: () => {},
  bicolor: (c, w, h) => c.fillRect(0, h / 2, w, h / 2),
  triband: (c, w, h) => {
    c.fillRect(0, 0, w / 4, h);
    c.fillRect((w * 3) / 4, 0, w / 4, h);
  },
  diagonal: (c, w, h) => {
    c.beginPath();
    c.moveTo(w, 0);
    c.lineTo(w, h);
    c.lineTo(0, h);
    c.closePath();
    c.fill();
  },
  border: (c, w, h) => {
    const t = h * 0.1;
    c.fillRect(0, 0, w, t);
    c.fillRect(0, h - t, w, t);
    c.fillRect(0, 0, t, h);
    c.fillRect(w - t, 0, t, h);
  },
  bands: (c, w, h) => {
    c.fillRect(0, h * 0.1, w, h * 0.14);
    c.fillRect(0, h * 0.76, w, h * 0.14);
  },
};

/** 市旗の中の紋章の位置(中心と一辺。旗の高さに対する比)。 */
const PLACEMENTS: Record<PlacementId, ((w: number, h: number) => { x: number; y: number; size: number }) | null> = {
  center: (w, h) => ({ x: w / 2, y: h / 2, size: h * 0.64 }),
  canton: (_w, h) => ({ x: h * 0.36, y: h * 0.32, size: h * 0.44 }),
  none: null,
};

function pick<T>(items: readonly T[]): T {
  return items[Math.floor(Math.random() * items.length)];
}

/** 配色をそろえた図柄の組み合わせを1つ選ぶ。 */
export function randomDesigns(): { emblem: EmblemDesign; flag: FlagDesign } {
  const [deep, light, accent] = pick(PALETTES);
  return {
    emblem: { motif: pick(MOTIF_IDS), field: deep, color: light },
    flag: { pattern: pick(PATTERN_IDS), field: pick([deep, light]), accent, placement: pick(["center", "canton"] as const) },
  };
}

function makeCanvas(width: number, height: number): HTMLCanvasElement {
  const canvas = document.createElement("canvas");
  canvas.width = Math.max(1, Math.round(width));
  canvas.height = Math.max(1, Math.round(height));
  return canvas;
}

/** 縦横比を保って枠に収まる大きさ。 */
function containSize(width: number, height: number, boxWidth: number, boxHeight: number) {
  const scale = Math.min(boxWidth / width, boxHeight / height);
  return { width: width * scale, height: height * scale };
}

/** 読み込んだ画像は縦横比を保って枠に収め、余白を付けずにその大きさで焼く。 */
function fitImage(image: ImageBitmap, boxWidth: number, boxHeight: number): HTMLCanvasElement {
  const size = containSize(image.width, image.height, boxWidth, boxHeight);
  const canvas = makeCanvas(size.width, size.height);
  canvas.getContext("2d")!.drawImage(image, 0, 0, canvas.width, canvas.height);
  return canvas;
}

export function renderEmblem(emblem: Insignia<EmblemDesign>, size = EMBLEM_SIZE): HTMLCanvasElement {
  if (emblem.image) return fitImage(emblem.image, size, size);
  const { design } = emblem;
  const canvas = makeCanvas(size, size);
  const c = canvas.getContext("2d")!;
  c.scale(size / 100, size / 100);
  c.fillStyle = design.field;
  c.fill(SHIELD);
  c.strokeStyle = design.color;
  c.lineWidth = 2.5;
  c.stroke(SHIELD_RIM);

  const layer = makeCanvas(size, size);
  const m = layer.getContext("2d")!;
  m.scale(size / 100, size / 100);
  m.translate(MOTIF_BOX.x, MOTIF_BOX.y);
  m.scale(MOTIF_BOX.size / 100, MOTIF_BOX.size / 100);
  m.fillStyle = m.strokeStyle = design.color;
  m.lineCap = m.lineJoin = "round";
  MOTIFS[design.motif](m);
  c.setTransform(1, 0, 0, 1, 0, 0);
  c.drawImage(layer, 0, 0);
  return canvas;
}

/** 市旗。紋章は位置の枠に縦横比を保って収める。 */
export function renderFlag(flag: Insignia<FlagDesign>, emblem: HTMLCanvasElement | null,
  width = FLAG_WIDTH, height = FLAG_HEIGHT): HTMLCanvasElement {
  if (flag.image) return fitImage(flag.image, width, height);
  const { design } = flag;
  const canvas = makeCanvas(width, height);
  const c = canvas.getContext("2d")!;
  c.fillStyle = design.field;
  c.fillRect(0, 0, width, height);
  c.fillStyle = design.accent;
  PATTERNS[design.pattern](c, width, height);
  const place = PLACEMENTS[design.placement]?.(width, height);
  if (place && emblem) {
    const size = containSize(emblem.width, emblem.height, place.size, place.size);
    c.drawImage(emblem, place.x - size.width / 2, place.y - size.height / 2, size.width, size.height);
  }
  return canvas;
}

export function canvasToPng(canvas: HTMLCanvasElement): Promise<Blob> {
  return new Promise((resolve, reject) =>
    canvas.toBlob(blob => (blob ? resolve(blob) : reject(new Error("PNG encoding failed"))), "image/png"));
}
