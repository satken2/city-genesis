/**
 * Deterministic citizen portraits drawn as SVG.
 *
 * Each trait hashes the citizen ID with its own fixed slot in TRAIT, so a citizen always gets the same face and
 * adding a trait does not disturb the others. Never renumber the slots: that would change every citizen's face.
 * Gender and age are not part of the seed. They shape the traits through continuous rules, so the same face grows
 * up and ages as epochs pass.
 *
 * Age has to read at thumbnail size, so every life stage changes silhouettes, not only fine lines:
 * - child: large head on small shoulders, low and large eyes, short chin, small nose and mouth
 * - adult: sex differences in bone structure, facial hair, makeup
 * - mature: nasolabial folds, heavier jaw and eyelids, receding hairline, first gray
 * - elder: forehead lines, crow's feet, thin lips, hooded eyes, flatter and shorter hair
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
  earring: 35, clothHue: 36, clothSat: 37, clothLight: 38, clothStyle: 39, lipFull: 40,
} as const;

/** Light to dark. Pigment picks a point on this gradient and also limits how light hair and eyes can be. */
const SKIN = ["#f7dfcc", "#f0c8a8", "#e2ae88", "#c98d66", "#a66c47", "#7d4c31", "#58331f"];
const HAIR = ["#17110f", "#2f1e15", "#55341f", "#86592f", "#b88a4e", "#dcbb7c"];
const EYE = ["#2b1a12", "#4a2c1a", "#6d4a26", "#7b7536", "#4d7a52", "#4c7aa8"];
const GRAY_HAIR = "#dcdcdc";
const RED_HAIR = "#a4472a";
const GLASSES = ["#1d1d1f", "#5a3a22", "#a9a9b2"];
const SHADOW = "#6b3a26";
const INK = "#3a1d12";

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

/**
 * Hair is drawn in head coordinates: origin at the head center, half width W, crown at y = -T, eyes near y = 0.
 * volume runs from 1 (full) to 0 (lying flat on the scalp) and scales everything that stands off the head.
 * length runs from 1 (full) to 0 (cropped) and scales how far long hair hangs.
 */
type HairDraw = (W: number, T: number, volume: number, length: number) => HairShape;

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

/** Short hair hugging the skull. Sideburns end at sideY and hairlineY is the hairline height at the forehead center. */
function cap(W: number, T: number, rise: number, hairlineY: number, sideY = 3): string {
  return svg`M ${-W - 1.3} ${sideY} C ${-W - 2.3} ${-T * 0.7} ${-W * 0.6} ${-T - rise} 0 ${-T - rise} C ${W * 0.6} ${-T - rise} ${W + 2.3} ${-T * 0.7} ${W + 1.3} ${sideY} L ${W - 2.5} ${sideY} C ${W - 2.5} ${hairlineY * 0.4} ${W * 0.55} ${hairlineY} 0 ${hairlineY} C ${-W * 0.55} ${hairlineY} ${-W + 2.5} ${hairlineY * 0.4} ${-W + 2.5} ${sideY} Z`;
}

function curlyCap(W: number, T: number, rise: number, hairlineY: number): string {
  return svg`M ${-W - 1 - rise * 0.75} -2` + scallop(0, -2, W + 1 + rise * 0.75, T + rise - 2, Math.PI, Math.PI * 2, 9)
    + svg` L ${W + 1.5} 4 L ${W - 2.5} 4` + scallop(0, 4, W - 2.5, 4 - hairlineY, 0, -Math.PI, 6) + " Z";
}

const shortHair: HairDraw = (W, T, volume) => ({ front: cap(W, T, 3.5 * volume, -13.5) });
const crewCut: HairDraw = (W, T, volume) => ({ front: cap(W, T, 1.2 * volume, -15, 1), scalp: 0.35 });
const buzzCut: HairDraw = (W, T, volume) => ({ front: cap(W, T, 0.5 * volume, -16, 0), scalp: 0.7 });

const spikyHair: HairDraw = (W, T, volume) => {
  const count = 14;
  let front = svg`M ${-W - 1.3} 3`;
  for (let i = 0; i <= count; i++) {
    const a = Math.PI * (1.08 + (0.84 * i) / count);
    const r = 1 + (i % 2 ? 0.2 : 0.03) * volume;
    front += svg` L ${Math.cos(a) * (W + 1) * r} ${-3 + Math.sin(a) * (T - 2) * r}`;
  }
  return { front: front + svg` L ${W + 1.3} 3 L ${W - 2.5} 3 C ${W - 2.5} -6 ${W * 0.55} -14 0 -14 C ${-W * 0.55} -14 ${-W + 2.5} -6 ${-W + 2.5} 3 Z` };
};

const curlyHair: HairDraw = (W, T, volume) => ({ front: curlyCap(W, T, 4 * volume, -13) });

const sidePart: HairDraw = (W, T, volume) => {
  const top = -T - 5 * volume;
  return {
    front: svg`M ${-W - 1.3} 3 C ${-W - 2.8} ${-T * 0.75} ${-W * 0.55} ${top} 2 ${top} C ${W * 0.65} ${top} ${W + 2.8} ${-T * 0.7} ${W + 1.3} 3 L ${W - 2.5} 3 C ${W - 2.5} -9 ${W * 0.4} -16 ${-W * 0.15} -14.5 C ${-W * 0.5} -13.5 ${-W + 2.5} -8 ${-W + 2.5} 3 Z`,
    line: svg`M ${-W * 0.35} ${top + 1} Q ${-W * 0.3} ${-T + 2} ${-W * 0.4} -15.5`,
  };
};

const afro: HairDraw = (W, T, volume) => ({
  back: svg`M ${W - 3 + 12 * volume} -7` + scallop(0, -7, W - 3 + 12 * volume, T - 10 + 16 * volume, 0, Math.PI * 2, 18) + " Z",
  front: curlyCap(W, T, 3 * volume, -13),
});

const bob: HairDraw = (W, T, volume, length) => {
  const top = -T - 1 - 4.5 * volume;
  const end = lerp(9, 21, length);
  const side = Math.min(10, end - 2);
  return {
    back: svg`M ${-W - 4.5} ${end} C ${-W - 6} ${-T * 0.3} ${-W * 0.75} ${top - 1} 0 ${top - 1} C ${W * 0.75} ${top - 1} ${W + 6} ${-T * 0.3} ${W + 4.5} ${end} Q ${W + 1} ${end + 2.5} ${W - 3} ${end} L ${-W + 3} ${end} Q ${-W - 1} ${end + 2.5} ${-W - 4.5} ${end} Z`,
    front: svg`M ${-W - 2.5} ${side} C ${-W - 3.5} ${-T * 0.75} ${-W * 0.6} ${top} 0 ${top} C ${W * 0.6} ${top} ${W + 3.5} ${-T * 0.75} ${W + 2.5} ${side} L ${W - 1} ${side} C ${W - 1.5} -4 ${W - 3} -9 ${W * 0.55} -9.5 Q 0 -8.5 ${-W * 0.55} -9.5 C ${-W + 3} -9 ${-W + 1.5} -4 ${-W + 1} ${side} Z`,
  };
};

const longHair: HairDraw = (W, T, volume, length) => {
  const top = -T - 1 - 4 * volume;
  const end = lerp(15, 46, length);
  const strand = lerp(9, 30, length);
  return {
    back: svg`M ${-W - 5} ${end} C ${-W - 7} ${end * 0.2} ${-W - 5} ${top + 2} 0 ${top} C ${W + 5} ${top + 2} ${W + 7} ${end * 0.2} ${W + 5} ${end} Z`,
    front: svg`M 0 ${top} C ${-W * 0.7} ${top} ${-W - 3.5} ${-T * 0.6} ${-W - 3} ${strand * 0.4} L ${-W - 1.5} ${strand} C ${-W + 0.5} ${strand * 0.47} ${-W + 1} -4 ${-W * 0.45} -14 C -5 -19 -1 -20 0 ${-T + 3} C 1 -20 5 -19 ${W * 0.45} -14 C ${W - 1} -4 ${W - 0.5} ${strand * 0.47} ${W + 1.5} ${strand} L ${W + 3} ${strand * 0.4} C ${W + 3.5} ${-T * 0.6} ${W * 0.7} ${top} 0 ${top} Z`,
  };
};

const ponytail: HairDraw = (W, T, volume, length) => {
  const end = lerp(10, 32, length);
  const out = 3 + 8 * volume;
  return {
    back: svg`M ${W - 6} ${-T + 1} C ${W + out} ${-T + 4} ${W + out - 2} ${end * 0.5} ${W + 3} ${end} C ${W + 3.5} ${end * 0.5} ${W + 1} 0 ${W - 8} ${-T + 7} Z`,
    front: cap(W, T, 3 * volume, -15, 2),
  };
};

const bun: HairDraw = (W, T, volume) => {
  const r = 8.5 * volume;
  return {
    back: svg`M ${-r} ${-T - 6 * volume} a ${r} ${r} 0 1 0 ${r * 2} 0 a ${r} ${r} 0 1 0 ${-r * 2} 0 Z`,
    front: cap(W, T, 2.5 * volume, -15, 2),
  };
};

const sideSwept: HairDraw = (W, T, volume) => {
  const top = -T - 4 * volume;
  return {
    front: svg`M ${-W - 1.3} 4 C ${-W - 2.5} ${-T * 0.7} ${-W * 0.6} ${top} 0 ${top} C ${W * 0.6} ${top} ${W + 2.5} ${-T * 0.7} ${W + 1.3} 4 L ${W - 2.5} 4 C ${W - 2.5} -6 ${W * 0.6} -15 ${W * 0.2} -15 C ${-W * 0.2} -13 ${-W * 0.55} -6 ${-W + 1} -2 L ${-W + 2} 4 Z`,
  };
};

const HAIR_STYLES: readonly (Weighted & { draw: HairDraw })[] = [
  { weights: [1.0, 0.1], draw: shortHair },
  { weights: [0.5, 0.03], draw: crewCut },
  { weights: [0.15, 0], draw: buzzCut },
  { weights: [0.35, 0.05], draw: spikyHair },
  { weights: [0.45, 0.35], draw: curlyHair },
  { weights: [0.9, 0.15], draw: sidePart },
  { weights: [0.12, 0.15], draw: afro },
  { weights: [0.05, 0.9], draw: bob },
  { weights: [0.12, 1.1], draw: longHair },
  { weights: [0.03, 0.6], draw: ponytail },
  { weights: [0.05, 0.45], draw: bun },
  { weights: [0.1, 0.4], draw: sideSwept },
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
  const child = 1 - smoothstep(2, 19, age);
  const adult = smoothstep(12, 20, age);
  const mature = smoothstep(25, 55, age);
  const elder = smoothstep(55, 85, age);
  // Sex differences in bone structure and grooming appear with puberty; children differ only by hair.
  const man = masc * adult;
  const woman = (1 - masc) * adult;

  // Logit-scale stretch around the median: the median is kept exactly and the tone stays within (0, 1).
  const quantile = Math.min(1 - 1e-6, Math.max(1e-6, p("pigment")));
  const pigment = 1 / (1 + Math.exp(-(logit(skinMedian) + skinSpread * logit(quantile))));
  const skin = gradient(SKIN, pigment);
  const shade = (t: number) => mix(skin, SHADOW, t);
  const ink = (t: number) => mix(skin, INK, t);
  const grayness = smoothstep(0, 25, age - lerp(30, 58, p("grayOnset")));
  const recession = masc * smoothstep(0, 28, age - lerp(22, 110, p("baldOnset")));
  const hairBase = gradient(HAIR, p("hairTone") * (1 - pigment) ** 1.5);
  const hairColor = mix(mix(hairBase, RED_HAIR, smoothstep(0.85, 1, p("hairRed")) * (1 - pigment) * 0.85), GRAY_HAIR, grayness);
  const eyeColor = gradient(EYE, p("eyeTone") * (1 - pigment) ** 2);

  const scale = 1 + child * 0.12;
  const headY = 43 + child * 4;
  const W = 20 + p("faceWidth") * 2.5 + child * 1.2 + man * 0.6;
  const T = 25;
  const C = lerp(25.5, 28.5, p("chin")) - child * 3.5 + man * 1.2;
  const jaw = lerp(0.42, 0.62, p("jaw")) + man * 0.1 + child * 0.14 + mature * 0.05 + elder * 0.06;
  const earRy = 5.5 * lerp(0.9, 1.15, p("ear")) * (1 + elder * 0.1);

  const ey = 1 + child * 2.5 + lerp(-1, 1, p("eyeY"));
  const ex = lerp(7.8, 9.6, p("eyeGap"));
  const eyeRx = lerp(3.4, 4.1, p("eyeSize")) + child * 0.7 - elder * 0.25;
  const eyeRy = eyeRx * lerp(0.66, 0.84, p("eyeOpen"));
  // Share of the eye height hidden under the upper lid. Lids grow heavier with age.
  const lid = lerp(0.3, 0.14, p("eyeOpen")) - child * 0.1 + mature * 0.08 + elder * 0.12;
  const lidY = -2 * eyeRy * (1 - lid);
  const iris = Math.min(eyeRx * 0.62, eyeRy);
  const tilt = lerp(-6, 6, p("eyeTilt"));
  const gaze = lerp(-0.6, 0.6, p("gaze"));

  const nose = lerp(0.85, 1.2, p("noseSize")) * (1 - child * 0.3) * (1 + elder * 0.12) + man * 0.08;
  const ny = ey + lerp(8, 10.5, p("noseLength")) * (1 - child * 0.3);
  const my = ny + lerp(5.5, 7, p("mouthY")) * (1 - child * 0.1);
  const mw = lerp(5, 7.2, p("mouthWidth")) * (1 - child * 0.28);
  const smile = lerp(-0.2, 2.4, p("smile")) - elder * 0.4;
  const lipBase = mix(skin, "#a8423f", 0.32 - 0.06 * man + 0.33 * woman * p("lipTint"));
  const lipLine = mix(lipBase, INK, 0.4);
  const upperLip = lerp(0.9, 1.5, p("lipFull")) * (1 + 0.25 * woman - 0.2 * man) * (1 - 0.45 * elder);

  // Thinning hair lies flat, and both small children and the old wear their hair shorter.
  const hairVolume = (1 - 0.25 * elder) * (1 - smoothstep(0.1, 0.5, recession));
  const hairLength = lerp(0.35, 1, smoothstep(1, 12, age)) * (1 - 0.5 * elder);
  const hair = pick(HAIR_STYLES, p("hairStyle"), masc).draw(W, T, hairVolume, hairLength);
  const hairFill = mix(hairColor, skin, hair.scalp ?? 0);
  const facialHair = pick(FACIAL_HAIR, p("facialHair"), masc).kind;
  const hasGlasses = p("glasses") < 0.08 + 0.42 * smoothstep(35, 70, age);
  const hasEarrings = p("earring") < 0.45 * woman;

  const cloth = hsl(p("clothHue") * 360, lerp(0.25, 0.6, p("clothSat")), lerp(0.3, 0.55, p("clothLight")));
  const shoulder = lerp(29, 35, man) * (1 - child * 0.38) * (1 - elder * 0.08);
  const neck = (5.6 + man * 1.6) * (1 - child * 0.25);
  const necklineY = 79.5 + child * 3.5;
  const necklineHalf = neck + 4;

  const parts: string[] = [];
  const head = svg`translate(50 ${headY}) scale(${scale})`;
  const hairMask = recession > 0.01 ? ' mask="url(#bald)"' : "";

  if (hairMask) {
    // The temples go first, the bare forehead then climbs over the crown, and at last the hair left on the sides
    // sinks under a shrinking dome until only a fringe above the ears stays.
    const temples = smoothstep(0, 0.55, recession);
    const climb = smoothstep(0.15, 0.65, recession);
    const sink = smoothstep(0.5, 0.95, recession);
    const hole = (cx: number, rx: number, top: number, bottom: number) =>
      svg`<ellipse cx="${cx}" cy="${(top + bottom) / 2}" rx="${rx}" ry="${(bottom - top) / 2}" fill="#000"/>`;
    const templeRx = lerp(4, 7.5, temples);
    const temple = (s: number) => hole(s * (W - 2.5 - templeRx), templeRx, lerp(-9, -T + 3, temples), 4);
    const domeRx = lerp(W + 8, W + 2.4, Math.min(1, sink * 3));
    parts.push(svg`<defs><mask id="bald"><rect x="-60" y="-60" width="120" height="120" fill="#fff"/>`
      + temple(-1) + temple(1) + hole(0, lerp(W * 0.35, W - 2.2, climb) + 1.8 * sink, lerp(-13, -T - 5, climb), 2)
      + (sink > 0 ? svg`<path d="M -60 -60 L 60 -60 L 60 3 L ${domeRx} 3 A ${domeRx} ${lerp(60, 29, sink)} 0 0 0 ${-domeRx} 3 L -60 3 Z" fill="#000"/>` : "")
      + "</mask></defs>");
  }
  if (hair.back) parts.push(svg`<g transform="${head}"><path d="${hair.back}" fill="${hairFill}"${hairMask}/></g>`);

  const chinY = headY + C * scale;
  parts.push(svg`<rect x="${50 - neck}" y="${chinY - 10}" width="${neck * 2}" height="${necklineY - chinY + 10 + necklineHalf}" fill="${shade(0.14)}"/>`);
  parts.push(svg`<ellipse cx="50" cy="${chinY + 0.5}" rx="${neck}" ry="4.5" fill="${shade(0.34)}"/>`);
  parts.push(svg`<path d="M ${50 - shoulder} 100 L ${50 - shoulder} ${necklineY + 15.5} C ${50 - shoulder} ${necklineY + 6.5} ${56 - shoulder} ${necklineY + 1.5} ${50 - necklineHalf} ${necklineY} L ${50 + necklineHalf} ${necklineY} C ${44 + shoulder} ${necklineY + 1.5} ${50 + shoulder} ${necklineY + 6.5} ${50 + shoulder} ${necklineY + 15.5} L ${50 + shoulder} 100 Z" fill="${cloth}"/>`);
  const vNeck = svg`<path d="M ${50 - necklineHalf} ${necklineY + 0.1} L 50 ${necklineY + necklineHalf * 1.2} L ${50 + necklineHalf} ${necklineY + 0.1} Z" fill="${shade(0.14)}"/>`;
  const lapel = (s: number) => svg`M ${50 + s * (necklineHalf + 0.8)} ${necklineY - 0.8} L ${50 + s * 0.8} ${necklineY + necklineHalf * 1.15} L ${50 + s * (necklineHalf + 3.5)} ${necklineY + necklineHalf * 0.75} Z`;
  parts.push([
    svg`<path d="M ${50 - necklineHalf} ${necklineY + 0.1} Q 50 ${necklineY + necklineHalf * 0.8} ${50 + necklineHalf} ${necklineY + 0.1} Z" fill="${shade(0.14)}"/>`,
    vNeck,
    vNeck + svg`<path d="${lapel(-1)} ${lapel(1)}" fill="${mix(cloth, "#ffffff", 0.7)}"/>`,
    svg`<path d="M ${49 - neck} ${necklineY + 2.5} L ${49 - neck} ${necklineY - 6.5} Q 50 ${necklineY - 8.5} ${51 + neck} ${necklineY - 6.5} L ${51 + neck} ${necklineY + 2.5} Z" fill="${mix(cloth, "#000000", 0.18)}"/>`,
  ][Math.floor(p("clothStyle") * 4)]);

  const face: string[] = [];
  face.push(svg`<ellipse cx="${-W + 0.5}" cy="5" rx="3.6" ry="${earRy}" fill="${skin}"/><ellipse cx="${W - 0.5}" cy="5" rx="3.6" ry="${earRy}" fill="${skin}"/>`);
  face.push(svg`<ellipse cx="${-W - 0.2}" cy="5" rx="1.6" ry="${earRy * 0.55}" fill="${shade(0.22)}"/><ellipse cx="${W + 0.2}" cy="5" rx="1.6" ry="${earRy * 0.55}" fill="${shade(0.22)}"/>`);
  if (hasEarrings) {
    face.push(svg`<circle cx="${-W - 0.2}" cy="${5 + earRy * 0.85}" r="1.2" fill="#e8c25a"/><circle cx="${W + 0.2}" cy="${5 + earRy * 0.85}" r="1.2" fill="#e8c25a"/>`);
  }
  face.push(svg`<path d="M ${-W} 0 C ${-W} ${-T * 0.56} ${-W * 0.56} ${-T} 0 ${-T} C ${W * 0.56} ${-T} ${W} ${-T * 0.56} ${W} 0 C ${W} ${C * 0.55} ${W * jaw} ${C} 0 ${C} C ${-W * jaw} ${C} ${-W} ${C * 0.55} ${-W} 0 Z" fill="${skin}"/>`);
  if (hairMask) face.push(svg`<ellipse cx="-6" cy="-19" rx="5" ry="2.6" fill="#ffffff" opacity="${0.18 * smoothstep(0.4, 0.8, recession)}"/>`);

  const blush = 0.26 * child + 0.1 * p("blush") * (1 - man);
  if (blush > 0.01) {
    face.push(svg`<g fill="#ff6b6b" opacity="${blush}"><ellipse cx="${-ex - 1.5}" cy="${ny + 1.5}" rx="3.8" ry="2.2"/><ellipse cx="${ex + 1.5}" cy="${ny + 1.5}" rx="3.8" ry="2.2"/></g>`);
  }
  const freckles = smoothstep(0.8, 1, p("freckles")) * (1 - pigment);
  if (freckles > 0) {
    const dots = [[-3, -1], [-5.5, 0.5], [-8, -0.5], [-6.5, 2.5], [-10, 1.5]].flatMap(([x, y]) => [[x, y], [-x, y + 0.3]]);
    face.push(`<g fill="${ink(0.4)}" opacity="${(0.5 * freckles).toFixed(2)}">` + dots.map(([x, y]) => svg`<circle cx="${x}" cy="${ny - 2 + y}" r="0.5"/>`).join("") + "</g>");
  }

  // Age lines, each fading in over its own span of years. Paths are written for the right side and mirrored.
  const crease = (opacity: number, width: number, d: (s: number) => string) => {
    if (opacity > 0.02) face.push(svg`<path d="${d(-1)} ${d(1)}" fill="none" stroke="${ink(0.45)}" stroke-width="${width}" stroke-linecap="round" opacity="${opacity}"/>`);
  };
  const foldX = 2.9 * nose + 1;
  const foldEnd = lerp(0.55, 1, Math.max(mature, elder));
  crease(0.7 * mature + 0.2 * elder, 0.9, (s) => svg`M ${s * foldX} ${ny - 1} Q ${s * (Math.max(foldX, mw + 2.2) + 1.4)} ${lerp(ny - 1, my + 1.4, 0.45 * foldEnd)} ${s * lerp(foldX + 1.4, mw + 2.4, foldEnd)} ${lerp(ny + 1.5, my + 1.4, foldEnd)}`);
  crease(0.3 * mature + 0.45 * elder, 0.8, (s) => svg`M ${s * (ex - eyeRx * 0.8)} ${ey + eyeRy + 1.3} Q ${s * ex} ${ey + eyeRy + 2.5} ${s * (ex + eyeRx * 0.8)} ${ey + eyeRy + 1.3}`);
  crease(0.7 * smoothstep(42, 75, age), 0.8, (s) => svg`M 0 ${ey - 13.5} Q ${s * 4} ${ey - 14.2} ${s * 8} ${ey - 13} M 0 ${ey - 10.5} Q ${s * 3.5} ${ey - 11.1} ${s * 7} ${ey - 10.1}`);
  crease(0.75 * elder, 0.8, (s) => svg`M ${s * (ex + eyeRx + 1.2)} ${ey - 0.8} l ${s * 2} -1 M ${s * (ex + eyeRx + 1.2)} ${ey + 0.8} l ${s * 2.2} 0.5 M ${s * (mw + 1)} ${my + 1.6} q ${s * 0.7} 2 ${s * 0.1} 3.8`);

  // Each eye and brow is drawn for the right side and mirrored, so the outer corner is always +x.
  const browColor = mix(mix(hairBase, "#000000", 0.15), GRAY_HAIR, grayness * 0.8);
  const browThick = lerp(1.3, 2.2, p("browThick")) + man * 0.6 - child * 0.4 - woman * 0.2;
  const browArch = lerp(0.6, 2.2, p("browArch"));
  const browInner = lerp(-0.8, 1.2, p("browAngle"));
  const browY = ey - eyeRy - lerp(2.8, 4.4, p("browY")) + mature * 0.4;
  for (const side of [-1, 1]) {
    face.push(svg`<g transform="translate(${side * ex} ${ey}) scale(${side} 1) rotate(${-tilt})">`
      + svg`<ellipse rx="${eyeRx}" ry="${eyeRy}" fill="#fbfaf6"/>`
      + svg`<circle cx="${side * gaze}" r="${iris}" fill="${eyeColor}"/>`
      + svg`<circle cx="${side * gaze}" r="${iris * 0.48}" fill="#111111"/>`
      + svg`<circle cx="${side * gaze + side * iris * 0.38}" cy="${-iris * 0.12}" r="${iris * 0.26}" fill="#ffffff" opacity="0.9"/>`
      + svg`<path d="M ${-eyeRx - 0.5} 0.2 A ${eyeRx + 0.5} ${eyeRy + 0.6} 0 0 1 ${eyeRx + 0.5} 0.2 Q 0 ${lidY} ${-eyeRx - 0.5} 0.2 Z" fill="${skin}"/>`
      + svg`<path d="M ${-eyeRx - 0.3} 0.2 Q 0 ${lidY} ${eyeRx + 0.3} 0.2${woman > 0.5 ? svg` l 1.5 -1.3` : ""}" fill="none" stroke="${ink(0.78)}" stroke-width="${1 + 0.3 * woman}" stroke-linecap="round" stroke-linejoin="round"/>`
      + "</g>");
    face.push(svg`<path transform="translate(${side * ex} ${browY}) scale(${side} 1)" d="M -4.4 ${browInner - browThick * 0.5} Q 0.5 ${-browArch - browThick * 0.5} 5.2 0.5 Q 0.5 ${-browArch + browThick * 0.5} -4.4 ${browInner + browThick * 0.5} Z" fill="${browColor}" stroke="${browColor}" stroke-width="0.5" stroke-linejoin="round"/>`);
  }

  const k = nose;
  face.push(svg`<path d="${[
    svg`M ${-2.4 * k} ${ny - 0.5} Q 0 ${ny + 2.3 * k} ${2.4 * k} ${ny - 0.5}`,
    svg`M ${-3 * k} ${ny - 1.7 * k} Q ${-3.6 * k} ${ny + 0.6 * k} ${-1.7 * k} ${ny + 0.2 * k} Q 0 ${ny + 1.9 * k} ${1.7 * k} ${ny + 0.2 * k} Q ${3.6 * k} ${ny + 0.6 * k} ${3 * k} ${ny - 1.7 * k}`,
    svg`M ${1.1 * k} ${ny - 6 * k} Q ${0.6 * k} ${ny - 2.5 * k} ${2.6 * k} ${ny - 0.7 * k} Q ${1.4 * k} ${ny + 1.7 * k} ${-2.2 * k} ${ny + 0.3 * k}`,
  ][Math.floor(p("noseType") * 3)]}" fill="none" stroke="${shade(0.4)}" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round"/>`);

  const beard = svg`M ${-W + 0.5} 2 C ${-W + 0.5} ${C * 0.55} ${-W * jaw} ${C + 1.5} 0 ${C + 1.5} C ${W * jaw} ${C + 1.5} ${W - 0.5} ${C * 0.55} ${W - 0.5} 2 C ${W * 0.84} 12 ${mw + 6} ${my - 0.5} ${mw + 1.5} ${my - 2.6} Q 0 ${my - 5.4} ${-mw - 1.5} ${my - 2.6} C ${-mw - 6} ${my - 0.5} ${-W * 0.84} 12 ${-W + 0.5} 2 Z`;
  const mustache = svg`M ${-mw - 1.6} ${my - 0.2} Q ${-mw * 0.55} ${ny + 1.6} 0 ${ny + 2.8} Q ${mw * 0.55} ${ny + 1.6} ${mw + 1.6} ${my - 0.2} Q ${mw * 0.5} ${my - 1.9} 0 ${my - 1.5} Q ${-mw * 0.5} ${my - 1.9} ${-mw - 1.6} ${my - 0.2} Z`;
  const goatee = svg`M ${-mw * 0.7} ${my + 2.5} Q 0 ${my + 1.5} ${mw * 0.7} ${my + 2.5} Q ${mw * 0.8} ${C - 1} 0 ${C + 0.5} Q ${-mw * 0.8} ${C - 1} ${-mw * 0.7} ${my + 2.5} Z`;
  const facialHairPath = { none: "", stubble: beard, mustache, goatee: goatee + " " + mustache, beard }[facialHair];
  if (facialHairPath) {
    face.push(svg`<path d="${facialHairPath}" fill="${hairColor}" opacity="${adult * (facialHair === "stubble" ? 0.11 : 1)}"/>`);
  }

  if (p("mouthOpen") > 0.75) {
    const depth = 1.6 * Math.max(smile, 1) + 4;
    face.push(svg`<path d="M ${-mw} ${my} Q 0 ${my + 0.9} ${mw} ${my} Q 0 ${my + depth} ${-mw} ${my} Z" fill="#5c2023" stroke="${lipBase}" stroke-width="1.1" stroke-linejoin="round"/>`
      + svg`<path d="M ${-mw * 0.86} ${my + 0.5} Q 0 ${my + 1.2} ${mw * 0.86} ${my + 0.5} Q 0 ${my + depth * 0.55} ${-mw * 0.86} ${my + 0.5} Z" fill="#ffffff"/>`);
  } else {
    const dip = my + 1.6 * smile;
    const mid = my + 0.8 * smile;
    face.push(svg`<path d="M ${-mw} ${my} Q 0 ${dip} ${mw} ${my} Q 0 ${dip + 3.5 * upperLip} ${-mw} ${my} Z" fill="${lipBase}"/>`
      + svg`<path d="M ${-mw} ${my} Q ${-mw * 0.5} ${mid - upperLip - 0.3} ${-mw * 0.18} ${mid - upperLip} Q 0 ${mid - upperLip * 0.55} ${mw * 0.18} ${mid - upperLip} Q ${mw * 0.5} ${mid - upperLip - 0.3} ${mw} ${my} Q 0 ${dip} ${-mw} ${my} Z" fill="${mix(lipBase, INK, 0.1)}"/>`
      + svg`<path d="M ${-mw} ${my} Q 0 ${dip} ${mw} ${my}" fill="none" stroke="${lipLine}" stroke-width="1" stroke-linecap="round"/>`);
  }

  face.push(svg`<g${hairMask}><path d="${hair.front}" fill="${hairFill}"/>${hair.line ? svg`<path d="${hair.line}" fill="none" stroke="${mix(hairFill, "#000000", 0.35)}" stroke-width="0.8" stroke-linecap="round"/>` : ""}</g>`);

  if (hasGlasses) {
    const frame = GLASSES[Math.floor(p("glassesColor") * GLASSES.length)];
    const R = eyeRx + 2.3;
    const lens = p("glassesShape") < 0.5
      ? svg`<circle cx="${-ex}" cy="${ey}" r="${R}"/><circle cx="${ex}" cy="${ey}" r="${R}"/>`
      : svg`<rect x="${-ex - R}" y="${ey - R * 0.75}" width="${R * 2}" height="${R * 1.5}" rx="1.5"/><rect x="${ex - R}" y="${ey - R * 0.75}" width="${R * 2}" height="${R * 1.5}" rx="1.5"/>`;
    face.push(svg`<g fill="#ffffff" fill-opacity="0.12" stroke="${frame}" stroke-width="1.1">${lens}</g>`
      + svg`<path d="M ${-ex + R} ${ey - 0.5} Q 0 ${ey - 2} ${ex - R} ${ey - 0.5} M ${-ex - R} ${ey - 0.5} L ${-W + 0.5} ${ey + 0.5} M ${ex + R} ${ey - 0.5} L ${W - 0.5} ${ey + 0.5}" fill="none" stroke="${frame}" stroke-width="1.1"/>`);
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
