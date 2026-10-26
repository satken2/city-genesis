/**
 * Citizen voice (persistent top-right card)
 *
 * Slides through cards of residents picked at random: face, name, age/gender/district, a remark, and action buttons.
 * The core picks the citizen, a topic, the mood on it (0 to 1), and values to fill in (`/api/v1/citizens/voices`).
 * The remark is composed when the card arrives: a line for the topic at the mood's tier, optionally wrapped in an
 * opener and a closer for the mood. Only lines whose placeholders the voice can fill are candidates; a topic without
 * its own line at that tier uses the nearest tier, and a topic without its own lines uses the group's `any` lines.
 *
 * - Fetch VOICE_BATCH voices at a time and fetch again when VOICE_REFILL_AT or fewer are queued, so cards keep flowing.
 *   An empty batch (epoch running, or no adult residents yet) stops fetching until the next epoch ends.
 * - Voices describe the city when they were fetched, so the queue is discarded and refetched when an epoch ends.
 * - Hovering pauses the slide so the buttons always act on the citizen under the pointer.
 * - The buttons are the mayor's actions (`citizen/citizenActions.ts`). An expelled citizen's cards leave the deck.
 * - Report the measured size through `resize_citizen_voice` (0×0 without a card) so the window covers only the card.
 */
import { useEffect, useMemo, useReducer, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { i18n as I18n, TFunction } from "i18next";
import { invoke } from "@tauri-apps/api/core";

import { buildApiUrl, fetchApi } from "../../utils/api";
import { formatCompactAmount } from "../../utils/money";
import { resolveIntlLocale } from "../../utils/locale";
import { citizenFaceUrl, citySkinTone } from "../../utils/citizenFace";
import { useCityClimate } from "../../hooks/useCityClimate";
import { useHudSession } from "./useHudSession";
import { CITIZEN_ACTIONS, runCitizenAction, type CitizenActionId } from "../citizen/citizenActions";
import "./hud.css";
import "./CitizenVoice.css";

const SLIDE_MS = 8000;
const VOICE_BATCH = 30;
const VOICE_REFILL_AT = 5;
const TIER_COUNT = 5;
const OPENER_CHANCE = 0.4;
const CLOSER_CHANCE = 0.45;
const MONEY_PARAMS = new Set(["savings", "debt"]);

type Voice = {
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

/** i18n keys of the remark's parts (opener, line, closer), resolved again on every render so language changes apply. */
type Card = Voice & { key: number; parts: string[] };

type Deck = { queue: Card[]; current: Card | null; leaving: Card | null };

type DeckAction =
  | { type: "add"; cards: Card[] }
  | { type: "next" }
  | { type: "left" }
  | { type: "discardQueue" }
  | { type: "removeCitizen"; citizenId: number }
  | { type: "reset" };

const EMPTY_DECK: Deck = { queue: [], current: null, leaving: null };

function deckReducer(deck: Deck, action: DeckAction): Deck {
  switch (action.type) {
    case "add": {
      const queue = [...deck.queue, ...action.cards];
      return deck.current ? { ...deck, queue } : { ...deck, current: queue[0] ?? null, queue: queue.slice(1) };
    }
    case "next":
      return deck.queue.length === 0 ? deck : { queue: deck.queue.slice(1), current: deck.queue[0], leaving: deck.current };
    case "left":
      return { ...deck, leaving: null };
    case "discardQueue":
      return { ...deck, queue: [] };
    case "removeCitizen": {
      const queue = deck.queue.filter((card) => card.citizen_id !== action.citizenId);
      if (deck.current?.citizen_id !== action.citizenId) return { ...deck, queue };
      return { queue: queue.slice(1), current: queue[0] ?? null, leaving: deck.current };
    }
    case "reset":
      return EMPTY_DECK;
  }
}

let nextCardKey = 1;

const PLACEHOLDER = /\{\{\s*(\w+)/g;

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

function toCard(i18n: I18n, voice: Voice): Card | null {
  const available = availableParams(voice);
  const tier = Math.min(TIER_COUNT - 1, Math.max(0, Math.floor(voice.level * TIER_COUNT)));
  const line = lineKey(i18n, voice, tier, available);
  if (!line) return null;
  const mood = tier <= 1 ? "bad" : tier === 2 ? "neutral" : "good";
  const opener = Math.random() < OPENER_CHANCE ? randomItem(usableKeys(i18n, `citizenVoice.openers.${mood}`, available)) : undefined;
  const closer = Math.random() < CLOSER_CHANCE ? randomItem(usableKeys(i18n, `citizenVoice.closers.${mood}`, available)) : undefined;
  return { ...voice, key: nextCardKey++, parts: [opener, line, closer].filter((key): key is string => Boolean(key)) };
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

export function CitizenVoice() {
  const { t, i18n } = useTranslation();
  const { visible, apiBaseUrl, epochExecuting } = useHudSession();
  const climate = useCityClimate(apiBaseUrl);
  const [deck, dispatch] = useReducer(deckReducer, EMPTY_DECK);
  const [paused, setPaused] = useState(false);
  const [exhausted, setExhausted] = useState(false);
  const fetching = useRef(false);
  /** Incremented on every reset so a batch requested before it is dropped. */
  const generation = useRef(0);
  const wasExecuting = useRef(epochExecuting);
  const panelRef = useRef<HTMLDivElement | null>(null);

  function restart(action: DeckAction) {
    generation.current += 1;
    fetching.current = false;
    setExhausted(false);
    setPaused(false);
    dispatch(action);
  }

  useEffect(() => {
    if (!visible) restart({ type: "reset" });
  }, [visible]);

  useEffect(() => {
    if (wasExecuting.current && !epochExecuting) restart({ type: "discardQueue" });
    wasExecuting.current = epochExecuting;
  }, [epochExecuting]);

  useEffect(() => {
    if (!visible || !apiBaseUrl || epochExecuting || exhausted || fetching.current) return;
    if (deck.queue.length > VOICE_REFILL_AT) return;
    const requested = generation.current;
    fetching.current = true;
    fetchApi<{ voices: Voice[] }>(buildApiUrl(apiBaseUrl, `/api/v1/citizens/voices?count=${VOICE_BATCH}`))
      .then(({ voices }) => {
        if (requested !== generation.current) return;
        if (voices.length === 0) setExhausted(true);
        dispatch({ type: "add", cards: voices.map((voice) => toCard(i18n, voice)).filter((card): card is Card => card !== null) });
      })
      .catch((error) => console.error("failed to load citizen voices", error))
      .finally(() => {
        if (requested === generation.current) fetching.current = false;
      });
  }, [visible, apiBaseUrl, epochExecuting, exhausted, deck.queue.length, i18n]);

  const hasNext = deck.queue.length > 0;
  useEffect(() => {
    if (!deck.current || paused || !hasNext) return;
    const timer = window.setTimeout(() => dispatch({ type: "next" }), SLIDE_MS);
    return () => window.clearTimeout(timer);
  }, [deck.current, paused, hasNext]);

  const shown = visible && deck.current !== null;
  useEffect(() => {
    const element = panelRef.current;
    if (!shown || !element) {
      void invoke("resize_citizen_voice", { width: 0, height: 0 }).catch(console.error);
      return;
    }
    let lastSent = { width: 0, height: 0 };
    const observer = new ResizeObserver(() => {
      const { width, height } = element.getBoundingClientRect();
      if (width <= 0 || height <= 0) return;
      if (Math.abs(width - lastSent.width) < 1 && Math.abs(height - lastSent.height) < 1) return;
      lastSent = { width, height };
      void invoke("resize_citizen_voice", { width, height }).catch(console.error);
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, [shown]);

  const skinTone = useMemo(() => citySkinTone(climate), [climate]);

  async function handleAction(card: Card, action: CitizenActionId) {
    const target = { citizenId: card.citizen_id, name: card.name, householdAgentId: card.household_agent_id };
    const done = await runCitizenAction(apiBaseUrl, action, target, t, i18n.language);
    if (done && action === "expel") dispatch({ type: "removeCitizen", citizenId: card.citizen_id });
  }

  function renderCard(card: Card, leaving: boolean) {
    const values = displayParams(card, t, i18n.language);
    const message = card.parts.map((key) => t(key, values)).join(t("citizenVoice.joiner"));
    const meta = [
      t("citizen.ageValue", { age: card.age }),
      card.gender === 0 ? t("citizen.male") : t("citizen.female"),
      card.zone_name,
    ].filter(Boolean).join(t("citizenVoice.separator"));
    return (
      <article
        key={card.key}
        className={`citizen-voice-card${leaving ? " citizen-voice-card--leaving" : ""}`}
        onAnimationEnd={leaving ? () => dispatch({ type: "left" }) : undefined}
        aria-hidden={leaving || undefined}
      >
        <img
          className="citizen-voice-face"
          src={citizenFaceUrl({ citizenId: card.citizen_id, gender: card.gender, age: card.age, ...skinTone })}
          alt=""
          draggable={false}
        />
        <div className="citizen-voice-body">
          <div className="citizen-voice-name">{card.name}</div>
          <div className="citizen-voice-meta">{meta}</div>
          <p className="citizen-voice-message">{message}</p>
          <div className="citizen-voice-actions">
            {CITIZEN_ACTIONS.map(({ id, icon }) => (
              <button
                key={id}
                type="button"
                className="hud-icon-btn citizen-voice-action"
                title={t(`citizenAction.${id}.label`)}
                aria-label={t(`citizenAction.${id}.label`)}
                data-guide="citizenVoice.action"
                data-guide-value={id}
                tabIndex={leaving ? -1 : undefined}
                onClick={() => void handleAction(card, id)}
              >
                <img src={icon} alt="" className="hud-icon-img" draggable={false} />
              </button>
            ))}
          </div>
        </div>
      </article>
    );
  }

  if (!shown || !deck.current) return null;

  return (
    <div
      ref={panelRef}
      className={`hud-panel citizen-voice ${epochExecuting ? "hud-disabled" : ""}`}
      onMouseEnter={() => setPaused(true)}
      onMouseLeave={() => setPaused(false)}
    >
      {deck.leaving && renderCard(deck.leaving, true)}
      {renderCard(deck.current, false)}
    </div>
  );
}
