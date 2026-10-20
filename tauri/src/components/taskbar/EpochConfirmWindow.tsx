/**
 * Epoch execution confirmation window
 *
 * Popup showing current and post-execution game dates for final confirmation before simulation.
 * No title bar (registry chromeless); close through No or Esc, but not during execution.
 * Fit window height to content because font size and language affect height; fixed height would leave gaps or
 * overflow. Resize only; retain the position chosen by Rust.
 *
 * Select 1–10 consecutive epochs; the resulting date advances accordingly. Connect the dates with a seasonal
 * band showing the seasons crossed (boundaries from `utils/season`).
 * Scenario authoring mode removes the upper limit and allows direct numeric input, supporting hundreds of epochs
 * to develop a city in one HTTP request that waits for completion.
 *
 * Confirm starts epoch execution and result display through executeEpochs in `utils/epochExecution`, hiding this
 * confirmation window while execution continues.
 * Close it once execution has occurred; redisplay if execution did not occur.
 */
import { PLAY_MODE_SCENARIO } from "../../constants";


import { useEffect, useRef, useState, type CSSProperties } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow, LogicalSize } from "@tauri-apps/api/window";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { executeEpochs, MAX_EPOCH_COUNT, SCENARIO_MAX_EPOCH_COUNT } from "../../utils/epochExecution";
import { formatEpochDate } from "../../utils/date";
import { resolveIntlLocale } from "../../utils/locale";
import { usePlayMode } from "../../hooks/usePlayMode";
import { useFontSize } from "../../contexts/FontSizeContext";
import { SaveDialogButton } from "./SaveDialog";
import { ClockIcon, MinusIcon, PlusIcon, SeasonIcon } from "./SaveIcons";
import { DAYS_PER_YEAR, SEASON_STARTS, dayOfYear, seasonOf, type Season } from "../../utils/season";
import "./EpochConfirmWindow.css";

type CityData = {
  name: string;
  current_date: number;
  simulation_days: number;
};

function formatDate(daysSinceEpoch: number, locale: string): string {
  return formatEpochDate(daysSinceEpoch, locale);
}

type EpochConfirmWindowProps = {
  apiBaseUrl: string;
  onClose: () => void;
};

/** Half-width in days of the blurred transition at season boundaries in the band */
const SEASON_BLEND_DAYS = 8;

/**
 * Seasonal band fill. Repeat one year's colors and position them against total execution days (CSS
 * --epoch-confirm-span).
 * Passing days as a CSS variable lets epoch count changes animate the scale smoothly.
 */
function seasonBandGradient(currentDate: number): string {
  const start = dayOfYear(currentDate);
  const at = (day: number) => `calc(${day - start} / var(--epoch-confirm-span) * 100%)`;
  const color = (season: Season) => `var(--epoch-confirm-${season})`;
  let previous: Season = "winter";
  const stops = [`${color(previous)} ${at(0)}`];
  for (const { season, day } of SEASON_STARTS) {
    stops.push(`${color(previous)} ${at(day - SEASON_BLEND_DAYS)}`, `${color(season)} ${at(day + SEASON_BLEND_DAYS)}`);
    previous = season;
  }
  stops.push(`${color(previous)} ${at(DAYS_PER_YEAR)}`);
  return `repeating-linear-gradient(to bottom, ${stops.join(", ")})`;
}

/** Season marker at a band endpoint. Show no season before loading a city. */
function SeasonBadge({ className, season }: { className: string; season?: Season }) {
  return (
    <span className={`epoch-confirm-badge ${className} epoch-confirm-badge--${season ?? "none"}`} aria-hidden={true}>
      {season && <SeasonIcon season={season} />}
    </span>
  );
}

export function EpochConfirmWindow({ apiBaseUrl, onClose }: EpochConfirmWindowProps) {
  const { i18n, t } = useTranslation();
  const [city, setCity] = useState<CityData | null>(null);
  const [executing, setExecuting] = useState(false);
  const executingRef = useRef(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const [epochCount, setEpochCount] = useState(1);
  const scenarioMode = usePlayMode(apiBaseUrl) === PLAY_MODE_SCENARIO;
  const { fontSize } = useFontSize();
  const maxEpochCount = scenarioMode ? SCENARIO_MAX_EPOCH_COUNT : MAX_EPOCH_COUNT;

  /** Direct input in scenario mode. Clamp blank/out-of-range values to 1 through the upper limit. */
  function handleCountInput(raw: string) {
    const parsed = Number.parseInt(raw, 10);
    if (Number.isNaN(parsed)) {
      setEpochCount(1);
      return;
    }
    setEpochCount(Math.max(1, Math.min(maxEpochCount, parsed)));
  }

  useEffect(() => {
    fetchApi<{ city: CityData }>(buildApiUrl(apiBaseUrl, "/api/v1/city/"))
      .then((data) => setCity(data.city))
      .catch(console.error);
  }, [apiBaseUrl]);

  // Measure height only when content changes, not on window resizing. Closed windows may reuse this popup
  // while the panel remains mounted; observing window size would overwrite the new popup's size.
  useEffect(() => {
    const root = rootRef.current;
    const shell = root?.closest<HTMLElement>(".popup-shell");
    if (!root || !shell) return;
    const border = shell.offsetHeight - shell.clientHeight;
    const height = Math.min(root.scrollHeight + border, window.screen.availHeight || Infinity);
    if (Math.abs(height - window.innerHeight) < 1) return;
    void getCurrentWindow().setSize(new LogicalSize(window.innerWidth, height)).catch(console.error);
  }, [city, epochCount, scenarioMode, i18n.language, fontSize]);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== "Escape" || executingRef.current) return;
      event.preventDefault();
      onClose();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onClose]);

  const locale = resolveIntlLocale(i18n.language);
  const currentDateStr = city ? formatDate(city.current_date, locale) : "—";
  const nextDateStr = city
    ? formatDate(city.current_date + city.simulation_days * epochCount, locale)
    : "—";

  async function handleConfirm() {
    if (executingRef.current) return;
    executingRef.current = true;
    setExecuting(true);
    const outcome = await executeEpochs({
      apiBaseUrl,
      count: epochCount,
      t,
      language: i18n.language,
      onStarted: () => void getCurrentWindow().hide().catch(console.error),
    });
    if (outcome === "completed") {
      onClose();
    } else if (outcome === "failed") {
      await getCurrentWindow().show().catch(console.error);
      setExecuting(false);
      executingRef.current = false;
    }
  }

  return (
    <div className="epoch-confirm" ref={rootRef}>
      <header className="epoch-confirm-header">
        <h1 className="epoch-confirm-title"><ClockIcon /><span>{t("windowType.epoch_confirm")}</span></h1>
        <p className="epoch-confirm-message">{t("epochConfirm.message")}</p>
      </header>

      {/* Connect current date → epoch count → resulting date with a seasonal band showing elapsed time and seasons. */}
      <div className="epoch-confirm-timeline">
        <span
          className="epoch-confirm-band"
          style={city ? {
            "--epoch-confirm-span": String(city.simulation_days * epochCount),
            backgroundImage: seasonBandGradient(city.current_date),
          } as CSSProperties : undefined}
          aria-hidden={true}
        />
        <div className="epoch-confirm-step epoch-confirm-step--from">
          <SeasonBadge className="epoch-confirm-badge--from" season={city ? seasonOf(city.current_date) : undefined} />
          <span className="epoch-confirm-label">{t("epochConfirm.currentDate")}</span>
          <span className="epoch-confirm-date">{currentDateStr}</span>
        </div>

        {/* Select 1–MAX_EPOCH_COUNT consecutive epochs; scenario mode has no upper limit and permits direct input. */}
        <div className="epoch-confirm-step epoch-confirm-step--count">
          <span className="epoch-confirm-label">{t("epochConfirm.epochCount")}</span>
          <div className="epoch-confirm-stepper">
            <button
              className="epoch-confirm-stepper-button"
              onClick={() => setEpochCount((c) => Math.max(1, c - 1))}
              disabled={executing || epochCount <= 1}
              type="button"
              aria-label="−"
            >
              <MinusIcon />
            </button>
            {scenarioMode ? (
              <input
                className="epoch-confirm-count epoch-confirm-count--input"
                type="number"
                min={1}
                max={SCENARIO_MAX_EPOCH_COUNT}
                value={epochCount}
                disabled={executing}
                onChange={(e) => handleCountInput(e.target.value)}
              />
            ) : (
              <span className="epoch-confirm-count">{epochCount}</span>
            )}
            <button
              className="epoch-confirm-stepper-button"
              onClick={() => setEpochCount((c) => Math.min(maxEpochCount, c + 1))}
              disabled={executing || epochCount >= maxEpochCount}
              type="button"
              aria-label="+"
            >
              <PlusIcon />
            </button>
          </div>
        </div>

        <div className="epoch-confirm-step epoch-confirm-step--to">
          <SeasonBadge
            className="epoch-confirm-badge--to"
            season={city ? seasonOf(city.current_date + city.simulation_days * epochCount) : undefined}
          />
          <span className="epoch-confirm-label">{t("epochConfirm.afterDate")}</span>
          {/* Replace on each date change to animate the transition. */}
          <span key={nextDateStr} className="epoch-confirm-date">{nextDateStr}</span>
        </div>
      </div>

      <div className="epoch-confirm-actions">
        <SaveDialogButton disabled={executing} onClick={onClose}>{t("epochConfirm.no")}</SaveDialogButton>
        <SaveDialogButton kind="primary" busy={executing} onClick={() => void handleConfirm()}>
          {executing ? t("epochConfirm.executing") : t("epochConfirm.yes")}
        </SaveDialogButton>
      </div>
    </div>
  );
}
