/**
 * Deterministic citizen portraits drawn as SVG.
 *
 * Each trait hashes the citizen ID with its own fixed slot in TRAIT, so a citizen always gets the same face and
 * adding a trait does not disturb the others. Never renumber the slots: that would change every citizen's face.
 * Gender and age are not part of the seed. They shape the traits through continuous rules (head proportions,
 * graying, receding hairline, wrinkles), so the same face grows up and ages as epochs pass.
 *
 * Skin tone follows the population given by skinMedian and skinSpread. A citizen's own trait value is a quantile of
 * that population, so a citizen darker than the median stays darker in any city.
 *
 * The result is a data URL usable anywhere an image path is accepted. Each image is a separate SVG document, so
 * element IDs inside it never collide with the page.
 */

import type { ClimateSettings } from "./climate";

export type CitizenFaceInput = {
  citizenId: number;
  /** 0 = male, 1 = female (backend gender). */
  gender: number;
  age: number;
  /** Median skin tone from 0 (lightest) to 1 (darkest). Default 0.5. */
  skinMedian?: number;
  /** Spread around the median. 0 gives everyone the median; 1 with median 0.5 spreads tones evenly. Default 1. */
  skinSpread?: number;
};

type SkinTone = Required<Pick<CitizenFaceInput, "skinMedian" | "skinSpread">>;

const TRAIT = {
  pigment: 0, hairTone: 1, hairRed: 2, hairStyle: 3, grayOnset: 4, baldOnset: 5,
  faceWidth: 6, chin: 7, jaw: 8, ear: 9,
  eyeY: 10, eyeGap: 11, eyeSize: 12, eyeOpen: 13, eyeTilt: 14, eyeTone: 15, gaze: 16,
  browY: 17, browArch: 18, browAngle: 19, browThick: 20,
  noseType: 21, noseLength: 22, noseSize: 23,
  mouthY: 24, mouthWidth: 25, smile: 26, mouthOpen: 27, lipTint: 28,
  blush: 29, freckles: 30, facialHair: 31, glasses: 32, glassesShape: 33, glassesColor: 34,
  earring: 35, clothHue: 36, clothSat: 37, clothLight: 38, clothStyle: 39,
} as const;

/** Light to dark. Pigment picks a point on this gradient and also limits how light hair and eyes can be. */
const SKIN = ["#f7dfcc", "#f0c8a8", "#e2ae88", "#c98d66", "#a66c47", "#7d4c31", "#58331f"];
const HAIR = ["#17110f", "#2f1e15", "#55341f", "#86592f", "#b88a4e", "#dcbb7c"];
const EYE = ["#2b1a12", "#4a2c1a", "#6d4a26", "#7b7536", "#4d7a52", "#4c7aa8"];
const GRAY_HAIR = "#dcdcdc";
const RED_HAIR = "#a4472a";
const GLASSES = ["#1d1d1f", "#5a3a22", "#a9a9b2"];

/** Weights are [male, female]; a style with 0 for a gender never appears for it. */
type Weighted = { weights: readonly [number, number] };

type HairShape = {
  /** Drawn behind the head and shoulders. */
  back?: string;
  front: string;
  /** Darker stroke such as a side part. */
  line?: string;
  /** Blend toward skin color for cropped hair showing scalp. */
  scalp?: number;
};

type HairStyle = Weighted & { draw: (W: number, T: number) => HairShape };

/** Hash from the citizen ID and a trait slot. Same mixing as the reference implementation in uint32. */
function hash(seed: number, index: number): number {
  let h = (seed ^ Math.imul(index, 0x9e3779b9)) >>> 0;
  h = Math.imul(h ^ (h >>> 16), 0x7feb352d) >>> 0;
  h = Math.imul(h ^ (h >>> 15), 0x846ca68b) >>> 0;
  return (h ^ (h >>> 16)) >>> 0;
}

/** Trait value in [0, 1). */
function param(seed: number, index: number): number {
  return (hash(seed, index) >>> 8) / 16777216;
}

const clamp01 = (v: number) => Math.min(1, Math.max(0, v));
const logit = (v: number) => Math.log(v / (1 - v));
const lerp = (a: number, b: number, t: number) => a + (b - a) * t;

function smoothstep(edge0: number, edge1: number, x: number): number {
  const t = clamp01((x - edge0) / (edge1 - edge0));
  return t * t * (3 - 2 * t);
}

/** Template tag that rounds interpolated numbers to keep the markup short. */
function svg(strings: TemplateStringsArray, ...values: (number | string)[]): string {
  let out = strings[0];
  values.forEach((value, i) => {
    out += (typeof value === "number" ? String(Math.round(value * 100) / 100) : value) + strings[i + 1];
  });
  return out;
}

function toRgb(hex: string): number[] {
  const n = parseInt(hex.slice(1), 16);
  return [n >> 16, (n >> 8) & 255, n & 255];
}

function mix(a: string, b: string, t: number): string {
  const ca = toRgb(a);
  const cb = toRgb(b);
  return "#" + ca.map((v, i) => Math.round(lerp(v, cb[i], clamp01(t))).toString(16).padStart(2, "0")).join("");
}

function gradient(stops: string[], t: number): string {
  const x = clamp01(t) * (stops.length - 1);
  const i = Math.min(stops.length - 2, Math.floor(x));
  return mix(stops[i], stops[i + 1], x - i);
}

function hsl(h: number, s: number, l: number): string {
  const a = s * Math.min(l, 1 - l);
  const channel = (n: number) => {
    const k = (n + h / 30) % 12;
    return Math.round((l - a * Math.max(-1, Math.min(k - 3, 9 - k, 1))) * 255).toString(16).padStart(2, "0");
  };
  return `#${channel(0)}${channel(8)}${channel(4)}`;
}

function pick<T extends Weighted>(items: readonly T[], p: number, masc: number): T {
  const weightOf = (item: T) => lerp(item.weights[1], item.weights[0], masc);
  let rest = p * items.reduce((sum, item) => sum + weightOf(item), 0);
  for (const item of items) {
    rest -= weightOf(item);
    if (rest < 0) return item;
  }
  return items[items.length - 1];
}

/** Bumpy outline along an ellipse arc; bumps bulge to the left of the travel direction. */
function scallop(cx: number, cy: number, rx: number, ry: number, from: number, to: number, count: number): string {
  let out = "";
  let px = cx + Math.cos(from) * rx;
  let py = cy + Math.sin(from) * ry;
  for (let i = 1; i <= count; i++) {
    const a = from + ((to - from) * i) / count;
    const x = cx + Math.cos(a) * rx;
    const y = cy + Math.sin(a) * ry;
    const r = Math.hypot(x - px, y - py) * 0.62;
    out += svg` A ${r} ${r} 0 0 1 ${x} ${y}`;
    px = x;
    py = y;
  }
  return out;
}

/*
 * Hair is drawn in head coordinates: origin at the head center, half width W, crown at y = -T, eyes near y = 0.
 * Sideburns end at sideY and hairlineY is the hairline height at the forehead center.
 */
function cap(W: number, T: number, volume: number, hairlineY: number, sideY = 3): string {
  return svg`M ${-W - 1.5} ${sideY} C ${-W - 2.5} ${-T * 0.7} ${-W * 0.6} ${-T - volume} 0 ${-T - volume} C ${W * 0.6} ${-T - volume} ${W + 2.5} ${-T * 0.7} ${W + 1.5} ${sideY} L ${W - 2.5} ${sideY} C ${W - 2.5} ${hairlineY * 0.4} ${W * 0.55} ${hairlineY} 0 ${hairlineY} C ${-W * 0.55} ${hairlineY} ${-W + 2.5} ${hairlineY * 0.4} ${-W + 2.5} ${sideY} Z`;
}

function curlyCap(W: number, T: number, volume: number, hairlineY: number): string {
  return svg`M ${-W - volume} -2` + scallop(0, -2, W + volume, T + volume - 2, Math.PI, Math.PI * 2, 9)
    + svg` L ${W + 1.5} 4 L ${W - 2.5} 4` + scallop(0, 4, W - 2.5, 4 - hairlineY, 0, -Math.PI, 6) + " Z";
}

function spikyCap(W: number, T: number): string {
  const count = 14;
  let out = svg`M ${-W - 1.5} 3`;
  for (let i = 0; i <= count; i++) {
    const a = Math.PI * (1.08 + (0.84 * i) / count);
    const r = i % 2 ? 1.2 : 1.03;
    out += svg` L ${Math.cos(a) * (W + 1) * r} ${-3 + Math.sin(a) * (T + 1) * r}`;
  }
  return out + svg` L ${W + 1.5} 3 L ${W - 2.5} 3 C ${W - 2.5} -6 ${W * 0.55} -14 0 -14 C ${-W * 0.55} -14 ${-W + 2.5} -6 ${-W + 2.5} 3 Z`;
}

const HAIR_STYLES: readonly HairStyle[] = [
  { weights: [1.0, 0.1], draw: (W, T) => ({ front: cap(W, T, 3.5, -13.5) }) },
  { weights: [0.5, 0.03], draw: (W, T) => ({ front: cap(W, T, 1.2, -15, 1), scalp: 0.35 }) },
  { weights: [0.15, 0], draw: (W, T) => ({ front: cap(W, T, 0.5, -16, 0), scalp: 0.7 }) },
  { weights: [0.35, 0.05], draw: (W, T) => ({ front: spikyCap(W, T) }) },
  { weights: [0.45, 0.35], draw: (W, T) => ({ front: curlyCap(W, T, 4, -13) }) },
  {
    weights: [0.9, 0.15],
    draw: (W, T) => ({
      front: svg`M ${-W - 1.5} 3 C ${-W - 3} ${-T * 0.75} ${-W * 0.55} ${-T - 5.5} 2 ${-T - 5.5} C ${W * 0.65} ${-T - 5.5} ${W + 3} ${-T * 0.7} ${W + 1.5} 3 L ${W - 2.5} 3 C ${W - 2.5} -9 ${W * 0.4} -16 ${-W * 0.15} -14.5 C ${-W * 0.5} -13.5 ${-W + 2.5} -8 ${-W + 2.5} 3 Z`,
      line: svg`M ${-W * 0.35} ${-T - 4.5} Q ${-W * 0.3} ${-T + 2} ${-W * 0.4} -15.5`,
    }),
  },
  {
    weights: [0.12, 0.15],
    draw: (W, T) => ({
      back: svg`M ${W + 9} -7` + scallop(0, -7, W + 9, T + 6, 0, Math.PI * 2, 18) + " Z",
      front: curlyCap(W, T, 3, -13),
    }),
  },
  {
    weights: [0.05, 0.9],
    draw: (W, T) => ({
      back: svg`M ${-W - 4.5} 21 C ${-W - 6} ${-T * 0.3} ${-W * 0.75} ${-T - 6} 0 ${-T - 6} C ${W * 0.75} ${-T - 6} ${W + 6} ${-T * 0.3} ${W + 4.5} 21 Q ${W + 1} 23.5 ${W - 3} 21 L ${-W + 3} 21 Q ${-W - 1} 23.5 ${-W - 4.5} 21 Z`,
      front: svg`M ${-W - 2.5} 10 C ${-W - 3.5} ${-T * 0.75} ${-W * 0.6} ${-T - 5} 0 ${-T - 5} C ${W * 0.6} ${-T - 5} ${W + 3.5} ${-T * 0.75} ${W + 2.5} 10 L ${W - 1} 10 C ${W - 1.5} -4 ${W - 3} -9 ${W * 0.55} -9.5 Q 0 -8.5 ${-W * 0.55} -9.5 C ${-W + 3} -9 ${-W + 1.5} -4 ${-W + 1} 10 Z`,
    }),
  },
  {
    weights: [0.12, 1.1],
    draw: (W, T) => ({
      back: svg`M ${-W - 5} 46 C ${-W - 7} 10 ${-W - 5} ${-T - 3} 0 ${-T - 5} C ${W + 5} ${-T - 3} ${W + 7} 10 ${W + 5} 46 Z`,
      front: svg`M 0 ${-T - 5} C ${-W * 0.7} ${-T - 5} ${-W - 3.5} ${-T * 0.6} ${-W - 3} 12 L ${-W - 1.5} 30 C ${-W + 0.5} 14 ${-W + 1} -4 ${-W * 0.45} -14 C -5 -19 -1 -20 0 ${-T + 3} C 1 -20 5 -19 ${W * 0.45} -14 C ${W - 1} -4 ${W - 0.5} 14 ${W + 1.5} 30 L ${W + 3} 12 C ${W + 3.5} ${-T * 0.6} ${W * 0.7} ${-T - 5} 0 ${-T - 5} Z`,
    }),
  },
  {
    weights: [0.03, 0.6],
    draw: (W, T) => ({
      back: svg`M ${W - 6} ${-T + 1} C ${W + 11} ${-T + 4} ${W + 9} 16 ${W + 3} 32 C ${W + 3.5} 16 ${W + 1} 0 ${W - 8} ${-T + 7} Z`,
      front: cap(W, T, 3, -15, 2),
    }),
  },
  {
    weights: [0.05, 0.45],
    draw: (W, T) => ({
      back: svg`M -8.5 ${-T - 6} a 8.5 8.5 0 1 0 17 0 a 8.5 8.5 0 1 0 -17 0 Z`,
      front: cap(W, T, 2.5, -15, 2),
    }),
  },
  {
    weights: [0.1, 0.4],
    draw: (W, T) => ({
      front: svg`M ${-W - 1.5} 4 C ${-W - 2.5} ${-T * 0.7} ${-W * 0.6} ${-T - 4} 0 ${-T - 4} C ${W * 0.6} ${-T - 4} ${W + 2.5} ${-T * 0.7} ${W + 1.5} 4 L ${W - 2.5} 4 C ${W - 2.5} -6 ${W * 0.6} -15 ${W * 0.2} -15 C ${-W * 0.2} -13 ${-W * 0.55} -6 ${-W + 1} -2 L ${-W + 2} 4 Z`,
    }),
  },
];

const FACIAL_HAIR: readonly (Weighted & { kind: "none" | "stubble" | "mustache" | "goatee" | "beard" })[] = [
  { weights: [0.5, 1], kind: "none" },
  { weights: [0.16, 0], kind: "stubble" },
  { weights: [0.12, 0], kind: "mustache" },
  { weights: [0.1, 0], kind: "goatee" },
  { weights: [0.12, 0], kind: "beard" },
];

/** Build the portrait SVG. The canvas is 100 x 100 and the head group is drawn in head coordinates. */
function buildFaceSvg({ citizenId, gender, age, skinMedian = 0.5, skinSpread = 1 }: CitizenFaceInput): string {
  const seed = citizenId >>> 0;
  const p = (trait: keyof typeof TRAIT) => param(seed, TRAIT[trait]);
  const masc = gender === 0 ? 1 : 0;
  const youth = 1 - smoothstep(2, 17, age);
  const adult = smoothstep(14, 20, age);
  const elder = smoothstep(45, 85, age);

  // Logit-scale stretch around the median: the median is kept exactly and the tone stays within (0, 1).
  const quantile = Math.min(1 - 1e-6, Math.max(1e-6, p("pigment")));
  const pigment = 1 / (1 + Math.exp(-(logit(skinMedian) + skinSpread * logit(quantile))));
  const skin = gradient(SKIN, pigment);
  const skinShadow = mix(skin, "#6b3a26", 0.22);
  const line = mix(skin, "#3a1d12", 0.5);
  const grayness = smoothstep(0, 25, age - lerp(35, 65, p("grayOnset")));
  const baldness = masc * smoothstep(0, 25, age - lerp(25, 110, p("baldOnset")));
  const hairBase = gradient(HAIR, p("hairTone") * (1 - pigment) ** 1.5);
  const hairColor = mix(mix(hairBase, RED_HAIR, smoothstep(0.85, 1, p("hairRed")) * (1 - pigment) * 0.85), GRAY_HAIR, grayness);
  const eyeColor = gradient(EYE, p("eyeTone") * (1 - pigment) ** 2);

  const scale = 1 + youth * 0.08;
  const W = 20 + p("faceWidth") * 2.5 + youth;
  const T = 25;
  const C = lerp(26, 29, p("chin")) - youth * 4 + masc;
  const jaw = lerp(0.42, 0.66, p("jaw")) + masc * 0.1 + youth * 0.12;
  const earRy = 5.5 * lerp(0.9, 1.15, p("ear"));

  const ey = 1 + youth * 3 + lerp(-1, 1, p("eyeY"));
  const ex = lerp(7.5, 9.5, p("eyeGap"));
  const eyeRx = lerp(3.2, 4.0, p("eyeSize")) + youth * 0.7 + (1 - masc) * 0.2;
  const eyeRy = eyeRx * lerp(0.55, 0.9, p("eyeOpen"));
  const iris = Math.min(eyeRx * 0.62, eyeRy * 1.05);
  const tilt = lerp(-8, 8, p("eyeTilt"));
  const gaze = lerp(-0.6, 0.6, p("gaze"));

  const noseSize = lerp(0.85, 1.25, p("noseSize")) * (1 - youth * 0.3);
  const ny = ey + lerp(8, 10.5, p("noseLength")) * (1 - youth * 0.2);
  const my = ny + lerp(5.5, 7, p("mouthY"));
  const mw = lerp(4.5, 7, p("mouthWidth")) * (1 - youth * 0.25);
  const smile = lerp(-0.3, 2.6, p("smile"));
  const lip = mix(mix(skin, "#3a1d12", 0.45), "#b03a44", 0.25 + 0.35 * (1 - masc) * adult * p("lipTint"));

  const hair = pick(HAIR_STYLES, p("hairStyle"), masc).draw(W, T);
  const hairFill = mix(hairColor, skin, hair.scalp ?? 0);
  const facialHair = pick(FACIAL_HAIR, p("facialHair"), masc).kind;
  const hasGlasses = p("glasses") < 0.08 + 0.42 * smoothstep(35, 70, age);
  const hasEarrings = p("earring") < 0.45 * (1 - masc) * adult;

  const cloth = hsl(p("clothHue") * 360, lerp(0.25, 0.6, p("clothSat")), lerp(0.3, 0.55, p("clothLight")));
  const shoulder = lerp(30, 36, masc) * (1 - youth * 0.22);
  const neck = (6 + masc * 1.5) * (1 - youth * 0.15);

  const parts: string[] = [];
  const head = svg`translate(50 43) scale(${scale})`;
  const hairMask = baldness > 0.01 ? ' mask="url(#bald)"' : "";
  // The hairline recedes from the forehead (y = -9) up past the crown, leaving a horseshoe around the sides.
  const baldTop = lerp(-14, -50, baldness);

  if (hairMask) {
    parts.push(svg`<defs><mask id="bald"><rect x="-60" y="-60" width="120" height="120" fill="#fff"/><ellipse cx="0" cy="${(baldTop - 9) / 2}" rx="${lerp(14, 40, baldness)}" ry="${(-9 - baldTop) / 2}" fill="#000"/></mask></defs>`);
  }
  if (hair.back) parts.push(svg`<g transform="${head}"><path d="${hair.back}" fill="${hairFill}"${hairMask}/></g>`);

  parts.push(svg`<rect x="${50 - neck}" y="60" width="${neck * 2}" height="26" fill="${skinShadow}"/>`);
  parts.push(svg`<path d="M ${50 - shoulder} 100 L ${50 - shoulder} 95 C ${50 - shoulder} 86 ${56 - shoulder} 81 39 79.5 L 61 79.5 C ${44 + shoulder} 81 ${50 + shoulder} 86 ${50 + shoulder} 95 L ${50 + shoulder} 100 Z" fill="${cloth}"/>`);
  const clothStyle = Math.floor(p("clothStyle") * 4);
  const clothDark = mix(cloth, "#000000", 0.18);
  parts.push([
    svg`<path d="M 42 79.6 Q 50 86 58 79.6 Z" fill="${skinShadow}"/>`,
    svg`<path d="M 42 79.6 L 50 90 L 58 79.6 Z" fill="${skinShadow}"/>`,
    svg`<path d="M 42 79.6 L 50 89 L 58 79.6 Z" fill="${skinShadow}"/><path d="M 41 79 L 49 88.5 L 38 85.5 Z M 59 79 L 51 88.5 L 62 85.5 Z" fill="${mix(cloth, "#ffffff", 0.7)}"/>`,
    svg`<path d="M ${49 - neck} 82 L ${49 - neck} 73 Q 50 71 ${51 + neck} 73 L ${51 + neck} 82 Z" fill="${clothDark}"/>`,
  ][clothStyle]);

  const face: string[] = [];
  face.push(svg`<ellipse cx="${-W + 0.5}" cy="5" rx="3.6" ry="${earRy}" fill="${skin}"/><ellipse cx="${W - 0.5}" cy="5" rx="3.6" ry="${earRy}" fill="${skin}"/>`);
  face.push(svg`<ellipse cx="${-W - 0.2}" cy="5" rx="1.6" ry="${earRy * 0.55}" fill="${skinShadow}"/><ellipse cx="${W + 0.2}" cy="5" rx="1.6" ry="${earRy * 0.55}" fill="${skinShadow}"/>`);
  if (hasEarrings) {
    face.push(svg`<circle cx="${-W - 0.2}" cy="${5 + earRy * 0.85}" r="1.2" fill="#e8c25a"/><circle cx="${W + 0.2}" cy="${5 + earRy * 0.85}" r="1.2" fill="#e8c25a"/>`);
  }
  face.push(svg`<path d="M ${-W} 0 C ${-W} ${-T * 0.56} ${-W * 0.56} ${-T} 0 ${-T} C ${W * 0.56} ${-T} ${W} ${-T * 0.56} ${W} 0 C ${W} ${C * 0.55} ${W * jaw} ${C} 0 ${C} C ${-W * jaw} ${C} ${-W} ${C * 0.55} ${-W} 0 Z" fill="${skin}"/>`);
  if (hairMask) face.push(svg`<ellipse cx="-6" cy="-19" rx="5" ry="2.6" fill="#ffffff" opacity="${0.18 * baldness}"/>`);

  face.push(svg`<g fill="#ff6b6b" opacity="${0.08 + 0.2 * youth + 0.12 * p("blush")}"><ellipse cx="${-ex - 1.5}" cy="${ny + 1.5}" rx="3.8" ry="2.2"/><ellipse cx="${ex + 1.5}" cy="${ny + 1.5}" rx="3.8" ry="2.2"/></g>`);
  const freckles = smoothstep(0.8, 1, p("freckles")) * (1 - pigment);
  if (freckles > 0) {
    const dots = [[-3, -1], [-5.5, 0.5], [-8, -0.5], [-6.5, 2.5], [-10, 1.5]].flatMap(([x, y]) => [[x, y], [-x, y + 0.3]]);
    face.push(`<g fill="${line}" opacity="${(0.6 * freckles).toFixed(2)}">` + dots.map(([x, y]) => svg`<circle cx="${x}" cy="${ny - 2 + y}" r="0.55"/>`).join("") + "</g>");
  }
  if (elder > 0) {
    const crow = (s: number) => svg`M ${s * (ex + eyeRx + 1)} ${ey - 1} l ${s * 2} -1 M ${s * (ex + eyeRx + 1)} ${ey + 0.6} l ${s * 2.2} 0.4`;
    const fold = (s: number) => svg`M ${s * (2.8 * noseSize + 1)} ${ny - 1} Q ${s * (mw + 3)} ${my - 2} ${s * (mw + 1.8)} ${my + 2.5}`;
    const bag = (s: number) => svg`M ${s * ex - eyeRx * 0.8} ${ey + eyeRy + 1.2} Q ${s * ex} ${ey + eyeRy + 2.2} ${s * ex + eyeRx * 0.8} ${ey + eyeRy + 1.2}`;
    face.push(svg`<path d="M -8 -12 Q 0 -13.5 8 -12 M -6 -8.8 Q 0 -10 6 -8.8 ${crow(-1)} ${crow(1)} ${fold(-1)} ${fold(1)} ${bag(-1)} ${bag(1)}" fill="none" stroke="${line}" stroke-width="0.9" stroke-linecap="round" opacity="${0.75 * elder}"/>`);
  }

  // Each eye and brow is drawn for the right side and mirrored, so the outer corner is always +x.
  for (const side of [-1, 1]) {
    const browY = ey - eyeRy - lerp(3, 4.6, p("browY"));
    const browWidth = lerp(1.2, 2.2, p("browThick")) + masc * 0.5 - youth * 0.3;
    face.push(svg`<g transform="translate(${side * ex} ${ey}) scale(${side} 1) rotate(${-tilt})">`
      + svg`<ellipse rx="${eyeRx}" ry="${eyeRy}" fill="#fbfaf6"/>`
      + svg`<ellipse cx="${side * gaze}" rx="${iris}" ry="${Math.min(iris, eyeRy)}" fill="${eyeColor}"/>`
      + svg`<circle cx="${side * gaze}" r="${iris * 0.45}" fill="#111111"/>`
      + svg`<circle cx="${side * gaze + side * iris * 0.35}" cy="${-iris * 0.35}" r="${iris * 0.28}" fill="#ffffff" opacity="0.9"/>`
      + svg`<path d="M ${-eyeRx} 0 Q 0 ${-eyeRy * 2} ${eyeRx} 0${masc ? "" : svg` l 1.4 -1.4`}" fill="none" stroke="${line}" stroke-width="1.1" stroke-linecap="round"/>`
      + "</g>");
    face.push(svg`<path transform="translate(${side * ex} ${browY}) scale(${side} 1)" d="M -4 ${lerp(-0.8, 1.2, p("browAngle"))} Q 0.5 ${-lerp(0.6, 2.2, p("browArch"))} 4.8 0.6" fill="none" stroke="${mix(hairColor, "#000000", 0.15)}" stroke-width="${browWidth}" stroke-linecap="round"/>`);
  }

  const k = noseSize;
  const noseStroke = `fill="none" stroke="${skinShadow}" stroke-width="1.3" stroke-linecap="round"`;
  face.push([
    svg`<path d="M ${-2.6 * k} ${ny} Q 0 ${ny + 2.4 * k} ${2.6 * k} ${ny}" ${noseStroke}/>`,
    svg`<path d="M ${0.6 * k} ${ny - 6 * k} Q ${-0.4 * k} ${ny - 1.5 * k} ${-2.2 * k} ${ny} Q ${-0.4 * k} ${ny + 1.6 * k} ${1.8 * k} ${ny + 0.6 * k}" ${noseStroke}/>`,
    svg`<g fill="${skinShadow}"><ellipse cx="${-1.6 * k}" cy="${ny}" rx="${k}" ry="${0.7 * k}"/><ellipse cx="${1.6 * k}" cy="${ny}" rx="${k}" ry="${0.7 * k}"/></g>`,
  ][Math.floor(p("noseType") * 3)]);

  const beardFill = `fill="${hairColor}"`;
  const beard = svg`M ${-W + 0.5} 2 C ${-W + 0.5} ${C * 0.55} ${-W * jaw} ${C + 1.5} 0 ${C + 1.5} C ${W * jaw} ${C + 1.5} ${W - 0.5} ${C * 0.55} ${W - 0.5} 2 C ${W * 0.7} 8 ${mw + 3} ${my - 5} 0 ${my - 3.5} C ${-mw - 3} ${my - 5} ${-W * 0.7} 8 ${-W + 0.5} 2 Z`;
  const mustache = svg`M ${-mw - 1.2} ${my + 0.3} Q ${-mw * 0.6} ${my - 4} 0 ${my - 2.6} Q ${mw * 0.6} ${my - 4} ${mw + 1.2} ${my + 0.3} Q 0 ${my - 1.4} ${-mw - 1.2} ${my + 0.3} Z`;
  const goatee = svg`M ${-mw * 0.7} ${my + 2.5} Q 0 ${my + 1.5} ${mw * 0.7} ${my + 2.5} Q ${mw * 0.8} ${C - 1} 0 ${C + 0.5} Q ${-mw * 0.8} ${C - 1} ${-mw * 0.7} ${my + 2.5} Z`;
  const facialHairPath = { none: "", stubble: beard, mustache, goatee: goatee + " " + mustache, beard }[facialHair];
  if (facialHairPath) {
    face.push(svg`<path d="${facialHairPath}" ${beardFill} opacity="${adult * (facialHair === "stubble" ? 0.22 : 1)}"/>`);
  }

  if (p("mouthOpen") > 0.72) {
    face.push(svg`<path d="M ${-mw} ${my} Q 0 ${my + smile * 1.6 + 4} ${mw} ${my} Q 0 ${my + 1} ${-mw} ${my} Z" fill="#6b2a2a"/>`
      + svg`<path d="M ${-mw * 0.85} ${my + 0.25} Q 0 ${my + 1.2} ${mw * 0.85} ${my + 0.25} L ${mw * 0.7} ${my + 1.4} Q 0 ${my + 2.4} ${-mw * 0.7} ${my + 1.4} Z" fill="#ffffff"/>`);
  } else {
    face.push(svg`<path d="M ${-mw} ${my} Q 0 ${my + smile * 1.6} ${mw} ${my}" fill="none" stroke="${lip}" stroke-width="1.5" stroke-linecap="round"/>`
      + svg`<path d="M ${-mw * 0.4} ${my + smile * 0.8 + 2} Q 0 ${my + smile * 0.8 + 2.8} ${mw * 0.4} ${my + smile * 0.8 + 2}" fill="none" stroke="${skinShadow}" stroke-width="1" stroke-linecap="round" opacity="0.6"/>`);
  }

  face.push(svg`<g${hairMask}><path d="${hair.front}" fill="${hairFill}"/>${hair.line ? svg`<path d="${hair.line}" fill="none" stroke="${mix(hairFill, "#000000", 0.35)}" stroke-width="0.8" stroke-linecap="round"/>` : ""}</g>`);

  if (hasGlasses) {
    const frame = GLASSES[Math.floor(p("glassesColor") * GLASSES.length)];
    const R = eyeRx + 2.3;
    const lens = p("glassesShape") < 0.5
      ? svg`<circle cx="${-ex}" cy="${ey}" r="${R}"/><circle cx="${ex}" cy="${ey}" r="${R}"/>`
      : svg`<rect x="${-ex - R}" y="${ey - R * 0.75}" width="${R * 2}" height="${R * 1.5}" rx="1.5"/><rect x="${ex - R}" y="${ey - R * 0.75}" width="${R * 2}" height="${R * 1.5}" rx="1.5"/>`;
    face.push(svg`<g fill="#ffffff" fill-opacity="0.12" stroke="${frame}" stroke-width="1.2">${lens}</g>`
      + svg`<path d="M ${-ex + R} ${ey - 0.5} Q 0 ${ey - 2} ${ex - R} ${ey - 0.5} M ${-ex - R} ${ey - 0.5} L ${-W + 0.5} ${ey + 0.5} M ${ex + R} ${ey - 0.5} L ${W - 0.5} ${ey + 0.5}" fill="none" stroke="${frame}" stroke-width="1.2"/>`);
  }

  parts.push(svg`<g transform="${head}">${face.join("")}</g>`);
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">${parts.join("")}</svg>`;
}

/**
 * Skin tone population of a city. Annual mean sunlight maps linearly from 20% (lightest median) to 80% (darkest),
 * so snowy, cloudy cities are mostly light-skinned. The spread keeps most citizens near the median while leaving a
 * few from elsewhere.
 */
export function citySkinTone(climate: ClimateSettings | null | undefined): SkinTone {
  const months = climate?.months ?? [];
  const sunlight = months.length ? months.reduce((sum, month) => sum + month.sunlight, 0) / months.length : 50;
  return { skinMedian: clamp01((sunlight - 20) / 60), skinSpread: 0.5 };
}

/** Portrait image URL for a citizen. */
export function citizenFaceUrl(input: CitizenFaceInput): string {
  return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(buildFaceSvg(input))}`;
}
