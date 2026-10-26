/**
 * Citizen voice remarks, shared by the voice card (`hud/CitizenVoice.tsx`) and the citizen detail panel.
 *
 * The core picks the citizen, a topic, the mood on it (0 to 1), and values to fill in (`/api/v1/citizens/voices`, or
 * `/api/v1/citizens/{id}/voice` for one citizen). `composeRemark` picks the remark once: a line for the topic at the
 * mood's tier, optionally wrapped in an opener and a closer for the mood. Only lines whose placeholders the voice can
 * fill are candidates; a topic without its own line at that tier uses the nearest tier, and a topic without its own
 * lines uses the group's `any` lines.
 *
 * A remark keeps i18n keys rather than text, so `remarkText` follows language changes, and handing the remark to
 * another window shows the same words there.
 */
import type { i18n as I18n, TFunction } from "i18next";

import { formatCompactAmount } from "../../utils/money";
import { resolveIntlLocale } from "../../utils/locale";

const TIER_COUNT = 5;
const OPENER_CHANCE = 0.4;
const CLOSER_CHANCE = 0.45;
const MONEY_PARAMS = new Set(["savings", "debt"]);
const PLACEHOLDER = /\{\{\s*(\w+)/g;

export type Voice = {
  citizen_id: number;
  name: string;
  age: number;
  /** 0 = male, 1 = female */
  gender: number;
  household_agent_id: number;
  zone_name: string | null;
  /** "<group>.<name>", e.g. "goods.food_retail" */
  topic: string;
  /** Mood on the topic, 0 (worst) to 1 (best) */
  level: number;
  /** Values for placeholders. Only those the citizen has are present. */
  params: Record<string, string | number>;
};

/** `parts` are the i18n keys of the opener, line, and closer, in that order. */
export type VoiceRemark = Voice & { parts: string[] };

function randomItem<T>(items: T[]): T | undefined {
  return items[Math.floor(Math.random() * items.length)];
}

/** Placeholder names the voice can fill, independent of the display language. */
function availableParams(voice: Voice): Set<string> {
  const names = new Set(["name", "age", ...Object.keys(voice.params)]);
  if (voice.zone_name) names.add("zone");
  if (typeof voice.params.partnerGender === "number") names.add("partnerRelation");
  if (typeof voice.params.childGender === "number") names.add("childRelation");
  return names;
}

/** Keys of the templates under `base` whose placeholders are all available, in the first language that has them. */
function usableKeys(i18n: I18n, base: string, available: Set<string>): string[] {
  for (const language of i18n.languages) {
    const templates = i18n.getResource(language, "translation", base) as Record<string, unknown> | undefined;
    if (!templates || typeof templates !== "object") continue;
    return Object.entries(templates)
      .filter(([, text]) => typeof text === "string" && [...text.matchAll(PLACEHOLDER)].every(([, name]) => available.has(name)))
      .map(([index]) => `${base}.${index}`);
  }
  return [];
}

function lineKey(i18n: I18n, voice: Voice, tier: number, available: Set<string>): string | undefined {
  const [group, name] = voice.topic.split(".");
  for (const base of [`citizenVoice.lines.${group}.${name}`, `citizenVoice.lines.${group}.any`]) {
    for (let distance = 0; distance < TIER_COUNT; distance += 1) {
      const keys = [...new Set([tier - distance, tier + distance])]
        .filter((t) => t >= 0 && t < TIER_COUNT)
        .flatMap((t) => usableKeys(i18n, `${base}.${t}`, available));
      if (keys.length > 0) return randomItem(keys);
    }
  }
  return undefined;
}

/** Pick the remark's parts. Null when no line fits the voice. */
export function composeRemark(i18n: I18n, voice: Voice): VoiceRemark | null {
  const available = availableParams(voice);
  const tier = Math.min(TIER_COUNT - 1, Math.max(0, Math.floor(voice.level * TIER_COUNT)));
  const line = lineKey(i18n, voice, tier, available);
  if (!line) return null;
  const mood = tier <= 1 ? "bad" : tier === 2 ? "neutral" : "good";
  const opener = Math.random() < OPENER_CHANCE ? randomItem(usableKeys(i18n, `citizenVoice.openers.${mood}`, available)) : undefined;
  const closer = Math.random() < CLOSER_CHANCE ? randomItem(usableKeys(i18n, `citizenVoice.closers.${mood}`, available)) : undefined;
  return { ...voice, parts: [opener, line, closer].filter((key): key is string => Boolean(key)) };
}

/** Interpolation values formatted for the display language. */
function displayParams(voice: Voice, t: TFunction, language: string): Record<string, string> {
  const locale = resolveIntlLocale(language);
  const number = new Intl.NumberFormat(locale, { maximumFractionDigits: 1 });
  const values: Record<string, string> = {};
  for (const [name, value] of Object.entries({ ...voice.params, name: voice.name, age: voice.age, zone: voice.zone_name })) {
    if (value === null || value === undefined || value === "") continue;
    values[name] = typeof value !== "number" ? String(value)
      : MONEY_PARAMS.has(name) ? formatCompactAmount(value, locale, true) : number.format(value);
  }
  if (typeof voice.params.partnerGender === "number") values.partnerRelation = t(`citizenVoice.relations.partner.${voice.params.partnerGender}`);
  if (typeof voice.params.childGender === "number") values.childRelation = t(`citizenVoice.relations.child.${voice.params.childGender}`);
  return values;
}

/** The remark in the display language. */
export function remarkText(remark: VoiceRemark, t: TFunction, language: string): string {
  const values = displayParams(remark, t, language);
  return remark.parts.map((key) => t(key, values)).join(t("citizenVoice.joiner"));
}
