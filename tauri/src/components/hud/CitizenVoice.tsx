/**
 * Citizen voice (persistent top-right card)
 *
 * Slides through cards of residents picked at random: face, name, age/gender/district, and a remark.
 * Voices come from `/api/v1/citizens/voices`, and each card's remark is composed once when it arrives
 * (`citizen/citizenVoiceRemark.ts`).
 *
 * - Fetch VOICE_BATCH voices at a time and fetch again when VOICE_REFILL_AT or fewer are queued, so cards keep flowing.
 *   An empty batch (no adult residents yet) stops fetching until the next epoch ends.
 * - Voices describe the city when they were fetched, so an epoch run is treated like no session: the deck is dropped,
 *   the card on screen included, and fetched afresh when the run ends.
 * - Each card stays SLIDE_MS; the pie at the top right drains over that time. Hovering pauses the countdown so a
 *   click always opens the citizen under the pointer, and it resumes where it stopped on leaving.
 * - The arrows over the left and right edges go back through the cards already shown and forward through the queue.
 *   A card reached either way gets a fresh countdown.
 * - Clicking the card opens the citizen detail window with the card's remark, so the window repeats the same words.
 * - `citizen_voice_visible` in settings turns the card off and on, applied at once through `settings-updated`. While
 *   off, it is treated like no session: the deck is dropped and nothing is fetched.
 * - Report the measured size through `resize_citizen_voice` (0×0 without a card). Text metrics make the height
 *   fractional, so round it up to whole device pixels: a window even slightly shorter than the panel shows the
 *   page's scrollbars, which hide the panel's right and bottom edges.
 */
import { useEffect, useMemo, useReducer, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { i18n as I18n } from "i18next";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";

import { buildApiUrl, fetchApi } from "../../utils/api";
import { loadAppSettings, type AppSettings } from "../../utils/settings";
import { citizenFaceUrl, citySkinTone } from "../../utils/citizenFace";
import { useCityClimate } from "../../hooks/useCityClimate";
import { useHudSession } from "./useHudSession";
import { composeRemark, remarkText, type Voice, type VoiceRemark } from "../citizen/citizenVoiceRemark";
import "./hud.css";
import "./CitizenVoice.css";

const SLIDE_MS = 15000;
const VOICE_BATCH = 30;
const VOICE_REFILL_AT = 5;

type Card = VoiceRemark & { key: number };

/** `history` holds the cards already shown, oldest first. `back` is the direction of the last slide. */
type Deck = { history: Card[]; queue: Card[]; current: Card | null; leaving: Card | null; back: boolean };

type DeckAction =
  | { type: "add"; cards: Card[] }
  | { type: "next" }
  | { type: "previous" }
  | { type: "left" }
  | { type: "reset" };

const EMPTY_DECK: Deck = { history: [], queue: [], current: null, leaving: null, back: false };

function deckReducer(deck: Deck, action: DeckAction): Deck {
  switch (action.type) {
    case "add": {
      const queue = [...deck.queue, ...action.cards];
      return deck.current ? { ...deck, queue } : { ...deck, current: queue[0] ?? null, queue: queue.slice(1) };
    }
    case "next": {
      if (deck.queue.length === 0 || !deck.current) return deck;
      return { history: [...deck.history, deck.current], queue: deck.queue.slice(1), current: deck.queue[0], leaving: deck.current, back: false };
    }
    case "previous": {
      const previous = deck.history[deck.history.length - 1];
      if (!previous || !deck.current) return deck;
      return { history: deck.history.slice(0, -1), queue: [deck.current, ...deck.queue], current: previous, leaving: deck.current, back: true };
    }
    case "left":
      return { ...deck, leaving: null };
    case "reset":
      return EMPTY_DECK;
  }
}

let nextCardKey = 1;

function toCard(i18n: I18n, voice: Voice): Card | null {
  const remark = composeRemark(i18n, voice);
  return remark && { ...remark, key: nextCardKey++ };
}

/** Round a CSS length up to whole device pixels, so the window Rust derives from it is never shorter than the panel. */
function ceilToDevicePixel(length: number): number {
  const ratio = window.devicePixelRatio || 1;
  return Math.ceil(length * ratio) / ratio;
}

/** Chevron for the slide arrows, drawn here so no asset credit is needed. */
function ChevronIcon({ back }: { back: boolean }) {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d={back ? "M14.5 6l-6 6 6 6" : "M9.5 6l6 6-6 6"} />
    </svg>
  );
}

export function CitizenVoice() {
  const { t, i18n } = useTranslation();
  const { visible, apiBaseUrl, epochExecuting } = useHudSession();
  const [enabled, setEnabled] = useState(true);
  const active = visible && enabled && !epochExecuting;
  const climate = useCityClimate(apiBaseUrl);
  const [deck, dispatch] = useReducer(deckReducer, EMPTY_DECK);
  const [paused, setPaused] = useState(false);
  const [exhausted, setExhausted] = useState(false);
  const fetching = useRef(false);
  /** Incremented on every reset so a batch requested before it is dropped. */
  const generation = useRef(0);
  const panelRef = useRef<HTMLDivElement | null>(null);

  function reset() {
    generation.current += 1;
    fetching.current = false;
    setExhausted(false);
    setPaused(false);
    dispatch({ type: "reset" });
  }

  useEffect(() => {
    let unlisten: (() => void) | undefined;
    loadAppSettings()
      .then((settings) => setEnabled(settings.citizen_voice_visible !== false))
      .catch(console.error);
    listen<AppSettings>("settings-updated", (event) => setEnabled(event.payload.citizen_voice_visible !== false))
      .then((fn) => { unlisten = fn; })
      .catch(console.error);
    return () => unlisten?.();
  }, []);

  useEffect(() => {
    if (!active) reset();
  }, [active]);

  useEffect(() => {
    if (!active || !apiBaseUrl || exhausted || fetching.current) return;
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
  }, [active, apiBaseUrl, exhausted, deck.queue.length, i18n]);

  const hasNext = deck.queue.length > 0;
  const hasPrevious = deck.history.length > 0;
  const currentKey = deck.current?.key;
  /** Time left on the current card. Reset when the card changes and consumed only while counting, so a pause resumes where it stopped. */
  const remaining = useRef(SLIDE_MS);
  useEffect(() => {
    remaining.current = SLIDE_MS;
  }, [currentKey]);
  // The pie animation and this timer both advance only while counting, which keeps them in step.
  const counting = currentKey !== undefined && hasNext && !paused;
  useEffect(() => {
    if (!counting) return;
    const startedAt = performance.now();
    const timer = window.setTimeout(() => dispatch({ type: "next" }), remaining.current);
    return () => {
      window.clearTimeout(timer);
      remaining.current = Math.max(0, remaining.current - (performance.now() - startedAt));
    };
  }, [counting, currentKey]);

  const shown = active && deck.current !== null;
  useEffect(() => {
    const element = panelRef.current;
    if (!shown || !element) {
      void invoke("resize_citizen_voice", { width: 0, height: 0 }).catch(console.error);
      return;
    }
    let lastSent = { width: 0, height: 0 };
    const observer = new ResizeObserver(() => {
      const rect = element.getBoundingClientRect();
      const width = ceilToDevicePixel(rect.width);
      const height = ceilToDevicePixel(rect.height);
      if (width <= 0 || height <= 0 || (width === lastSent.width && height === lastSent.height)) return;
      lastSent = { width, height };
      void invoke("resize_citizen_voice", { width, height }).catch(console.error);
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, [shown]);

  const skinTone = useMemo(() => citySkinTone(climate), [climate]);

  function handleCardClick(card: Card) {
    void invoke("open_registered_window", {
      windowType: "citizen_detail",
      entityId: String(card.citizen_id),
      params: { extra: { voice: card } },
    }).catch(console.error);
  }

  function renderCard(card: Card, leaving: boolean) {
    const message = remarkText(card, t, i18n.language);
    const meta = [
      t("citizen.ageValue", { age: card.age }),
      card.gender === 0 ? t("citizen.male") : t("citizen.female"),
      card.zone_name,
    ].filter(Boolean).join(t("citizenVoice.separator"));
    const className = ["citizen-voice-card", leaving && "citizen-voice-card--leaving", deck.back && "citizen-voice-card--back"]
      .filter(Boolean).join(" ");
    return (
      <article
        key={card.key}
        className={className}
        onAnimationEnd={leaving ? (event) => { if (event.target === event.currentTarget) dispatch({ type: "left" }); } : undefined}
        onClick={leaving ? undefined : () => handleCardClick(card)}
        aria-hidden={leaving || undefined}
      >
        <img
          className="citizen-voice-face"
          src={citizenFaceUrl({ citizenId: card.citizen_id, gender: card.gender, age: card.age, ...skinTone })}
          alt=""
          draggable={false}
        />
        <div className="citizen-voice-body">
          <div className="citizen-voice-header">
            <div className="citizen-voice-heading">
              <div className="citizen-voice-name">{card.name}</div>
              <div className="citizen-voice-meta">{meta}</div>
            </div>
            {/* Pie of the time left. pathLength lets the keyframes count the sector in percent. */}
            <svg className="citizen-voice-countdown" viewBox="0 0 20 20" aria-hidden="true">
              <circle className="citizen-voice-countdown-track" cx="10" cy="10" r="9" />
              <circle
                className="citizen-voice-countdown-pie"
                cx="10"
                cy="10"
                r="4.5"
                pathLength="100"
                transform="rotate(-90 10 10)"
                style={{ animationDuration: `${SLIDE_MS}ms`, animationPlayState: counting && !leaving ? "running" : "paused" }}
              />
            </svg>
          </div>
          <p className="citizen-voice-message">{message}</p>
        </div>
      </article>
    );
  }

  if (!shown || !deck.current) return null;

  return (
    <div
      ref={panelRef}
      className="hud-panel citizen-voice"
      onMouseEnter={() => setPaused(true)}
      onMouseLeave={() => setPaused(false)}
    >
      {deck.leaving && renderCard(deck.leaving, true)}
      {renderCard(deck.current, false)}
      <button
        type="button"
        className="hud-icon-btn citizen-voice-nav citizen-voice-nav--previous"
        title={t("citizenVoice.previous")}
        aria-label={t("citizenVoice.previous")}
        disabled={!hasPrevious}
        onClick={() => dispatch({ type: "previous" })}
      >
        <ChevronIcon back />
      </button>
      <button
        type="button"
        className="hud-icon-btn citizen-voice-nav citizen-voice-nav--next"
        title={t("citizenVoice.next")}
        aria-label={t("citizenVoice.next")}
        disabled={!hasNext}
        onClick={() => dispatch({ type: "next" })}
      >
        <ChevronIcon back={false} />
      </button>
    </div>
  );
}
