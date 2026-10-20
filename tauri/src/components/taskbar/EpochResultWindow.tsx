/**
 * Epoch results window
 *
 * Opens separately after epoch execution, displaying epoch_report from the backend POST /api/v1/epochs response.
 *
 * No title bar (registry chromeless); close using the footer button or Esc. Layout: hero, quest strip,
 * two-column main area, footer.
 * - Background: resulting month's image (seasonImageUrl from `utils/season`, falling back to the default image
 *   or none), faintly filling the upper half and fading out at mid-height.
 * - Hero: resulting game date on the left and a large score on the right. Show the current score and composition
 *   (living standards + living environment − disaster penalty), using latest from GET /api/v1/score with the
 *   achievements window's breakdown, without a chart library.
 * - Quest strip: quests completed this epoch, previewing celebration windows, plus progress bars for active
 *   quests at least 70% complete. Use server-calculated progress from GET /api/v1/quests.
 * - Left, primary column: statistics tiles above achievements. Blocks have no card frames because their tiles
 *   are already cards, avoiding nested frames.
 *   Statistics cover population/GDP/prices/income/unemployment/medical, nutrition, and leisure
 *   adequacy/fires/traffic accidents/crime.
 *   Achievements show active descriptions, current/target values per condition, progress, and completed/failed
 *   quests from GET /api/v1/quests. Fill remaining height and scroll overflow.
 * - Right: one construction list combining roads/public facilities with type badges. Group tagged work into one
 *   row per tag (construction project), before untagged rows. Below each project's volume-weighted progress bar,
 *   show member road/building icons (gray incomplete, green complete). Road badges use master variant categories
 *   such as streets, railways, or electricity.
 *
 * Statistics tiles have two independent visual dimensions:
 * - Trend arrows use the regression slope of each metric's backend history (`statistics.history`, latest
 *   `trend_window` epochs), pointing diagonally up, horizontally, or diagonally down. A window slope avoids the
 *   large fluctuations and frequent reversals of single-epoch changes. Metric polarity determines color: green
 *   good, red bad, gray neutral.
 * - Absolute-level warning colors assess the current value independently of trend, showing dangerous levels such
 *   as 15% unemployment in red. Thresholds are UI constants STAT_*_WARN / STAT_*_DANGER in this file.
 * Draw the same latest trend_window history faintly behind each tile in its arrow color.
 *
 * Closing calls the parent's onClose exactly once. If completed quests have notify="modal", the parent then
 * shows QuestCompleteWindow, sequencing results and celebration.
 */
import { ReactNode, useCallback, useEffect, useId, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { emit } from "@tauri-apps/api/event";
import { EpochDate } from "../common/EpochDate";
import { useMasterData } from "../../contexts/MasterDataContext";
import { STATUS_STYLE_MAP } from "../../local-constants";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { formatCompactAmount } from "../../utils/money";
import { resolveIntlLocale, resolveLocalizedText } from "../../utils/locale";
import { seasonImageUrl } from "../../utils/season";
import { formatQuestValue, questCompareLabel } from "../../utils/quest";
import type { Quest, QuestClauseState, QuestNotification } from "../../types";
import { SaveDialogButton } from "./SaveDialog";
import populationIcon from "../../assets/images/stats/population.png";
import tourismIcon from "../../assets/images/stats/tourism.png";
import gdpIcon from "../../assets/images/stats/gdp.png";
import priceIcon from "../../assets/images/stats/price.png";
import incomeIcon from "../../assets/images/stats/income.png";
import unemploymentIcon from "../../assets/images/stats/unemployment.png";
import healthcareIcon from "../../assets/images/stats/healthcare.png";
import leisureIcon from "../../assets/images/stats/entertainment.png";
import fireIcon from "../../assets/images/stats/fire.png";
import trafficIcon from "../../assets/images/stats/traffic_accident.png";
import crimeIcon from "../../assets/images/stats/crime.png";
import placeholderIcon from "../../assets/images/ui/help.png";
import "./EpochResultWindow.css";

export type ConstructionProgressItem = {
  entity_type: "agent" | "path";
  id: number;
  name: string;
  /** Current variant, used to resolve a road's type badge from master categories. */
  variant: number;
  before_pct: number;
  before_status: number;
  after_pct: number;
  after_status: number;
  completed: boolean;
};

/** Progress of one tagged construction project, averaged with members' construction volumes as weights. */
export type ConstructionProjectReport = {
  tag: string;
  before_pct: number;
  after_pct: number;
  /** All members complete */
  completed: boolean;
  members: { entity_type: "agent" | "path"; completed: boolean }[];
};

export type PopulationReport = {
  before: number;
  after: number;
  births: number;
  deaths: number;
  immigrations: number;
  immigration_applicants?: number;
  emigrations: number;
  /** Tourist arrivals (check-in to accommodation floors), departures, and visitors remaining at epoch end */
  tourist_arrivals?: number;
  tourist_departures?: number;
  tourist_count?: number;
};

/** City statistics at epoch completion, displayed in statistics tiles. */
export type StatisticsReport = {
  price: number;                   // Average price of retail goods (GOODS_RETAIL)
  income: number;                  // Average household income
  unemployment_rate: number;       // Unemployment rate (0..1 ratio)
  avg_healthcare: number;          // Mean household medical adequacy (0..1)
  avg_nutrition: number;           // Mean household nutrition adequacy (0..1)
  avg_leisure: number;             // Mean household leisure adequacy (0..1)
  gdp: number;                     // Nominal GDP of the most recently completed epoch
  fire_count: number;              // Total fires across executed epochs
  traffic_accident_count: number;  // Total traffic accidents across executed epochs
  crime_count: number;             // Total crimes across executed epochs
  /** History window length in epochs: N in "trend over the latest N epochs". */
  trend_window?: number;
  /**
   * Time series per metric, oldest to newest, ending with the most recently completed epoch.
   * If the city is younger than the window, pad older slots with null. Keys are the statistic names plus
   * `population`.
   */
  history?: Record<string, (number | null)[]>;
};

/**
 * Only the fields from latest in GET /api/v1/score used by the score block.
 * A partial definition suffices because the full set used by AchievementsWindow is unnecessary here.
 */
type ScoreLatest = {
  city_score: number;
  city_score_qol: number;
  city_score_per_household: number;
  city_score_collapse_penalty: number;
};

export type EpochReport = {
  construction_progress: ConstructionProgressItem[];
  construction_projects: ConstructionProjectReport[];
  population: PopulationReport;
  statistics: StatisticsReport;
};

type EpochResultWindowProps = {
  report: EpochReport;
  /** Number of executed epochs, shown as supplementary title text. */
  epochCount: number;
  /** Resulting game date, in days since January 1, 1800, shown prominently at the top left. */
  currentDate: number;
  onClose: () => void | Promise<void>;
  apiBaseUrl: string;
  /** Quests completed this epoch, or accumulated across all epochs when count > 1 */
  completedQuests?: QuestNotification[];
  /** Quests that failed due to expiration this epoch */
  failedQuests?: QuestNotification[];
};

/** Progress threshold for showing a quest as nearly complete in the strip */
const NEAR_COMPLETION_THRESHOLD = 0.7;

/**
 * Absolute-level warning thresholds.
 * Higher unemployment/incident counts are worse; lower adequacy is worse.
 */
const STAT_UNEMPLOYMENT_WARN = 0.08;
const STAT_UNEMPLOYMENT_DANGER = 0.15;
const STAT_SATISFACTION_WARN = 0.6;
const STAT_SATISFACTION_DANGER = 0.4;
const STAT_INCIDENT_WARN = 1;
const STAT_INCIDENT_DANGER = 5;

/**
 * Incident tiles show totals across consecutive epochs, so scale thresholds by the executed epoch count.
 * Use STAT_INCIDENT_* incidents per epoch as the baseline.
 */
const scaleIncidentThreshold = (threshold: number, epochCount: number) =>
  threshold * Math.max(1, epochCount);

/** Minimum points for trend evaluation. Omit arrows while a young city has insufficient history. */
const MIN_TREND_POINTS = 3;
/** Treat relative change across the window (as a fraction of the mean) below this value as flat. */
const TREND_FLAT_RATIO = 0.02;

/**
 * Tile icons (`assets/images/stats/`).
 * Use a question-mark placeholder for metrics without dedicated images (nutrition) to make missing assets
 * apparent.
 */
const STAT_ICONS: Record<string, string> = {
  population: populationIcon,
  tourists: tourismIcon,
  gdp: gdpIcon,
  price: priceIcon,
  income: incomeIcon,
  unemployment_rate: unemploymentIcon,
  avg_healthcare: healthcareIcon,
  avg_nutrition: placeholderIcon,
  avg_leisure: leisureIcon,
  fire_count: fireIcon,
  traffic_accident_count: trafficIcon,
  crime_count: crimeIcon,
};

/** Tile level assessment: normal / warn (orange) / danger (red) */
type StatTone = "normal" | "warn" | "danger";
/** Trend direction; none means insufficient history. */
type TrendDirection = "up" | "flat" | "down" | "none";
/** Metric polarity: whether increasing values are favorable, used to color arrows. */
type StatPolarity = "up-good" | "up-bad" | "neutral";
/** Arrow color: good = green / bad = red / neutral = gray */
type TrendTone = "good" | "bad" | "neutral";

/** Level assessment for metrics where higher values are worse. */
function toneAbove(value: number, warn: number, danger: number): StatTone {
  if (value >= danger) return "danger";
  if (value >= warn) return "warn";
  return "normal";
}

/** Level assessment for metrics where lower values are worse. */
function toneBelow(value: number, warn: number, danger: number): StatTone {
  if (value <= danger) return "danger";
  if (value <= warn) return "warn";
  return "normal";
}

/**
 * Determine trend direction from history.
 *
 * Use least squares to estimate the slope per epoch, then assess the window's total change relative to the
 * series mean. Absolute changes are incomparable because metrics differ in units and magnitude.
 * Exclude null slots predating city history; do not classify if too few points remain.
 */
function computeTrend(series: (number | null)[] | undefined): TrendDirection {
  const values = (series ?? []).filter(
    (v): v is number => typeof v === "number" && Number.isFinite(v),
  );
  if (values.length < MIN_TREND_POINTS) return "none";

  const n = values.length;
  const meanX = (n - 1) / 2;
  const meanY = values.reduce((sum, v) => sum + v, 0) / n;
  let covariance = 0;
  let variance = 0;
  values.forEach((y, i) => {
    covariance += (i - meanX) * (y - meanY);
    variance += (i - meanX) ** 2;
  });
  if (variance === 0) return "none";

  const span = (covariance / variance) * (n - 1); // Change across the entire window
  const ratio = span / Math.max(Math.abs(meanY), 1e-9);
  if (Math.abs(ratio) < TREND_FLAT_RATIO) return "flat";
  return ratio > 0 ? "up" : "down";
}

/** Determine arrow color from trend direction and metric polarity. */
function resolveTrendTone(direction: TrendDirection, polarity: StatPolarity): TrendTone {
  if (direction === "flat" || direction === "none" || polarity === "neutral") return "neutral";
  const isGood = polarity === "up-good" ? direction === "up" : direction === "down";
  return isGood ? "good" : "bad";
}

export function EpochResultWindow({
  report,
  epochCount,
  currentDate,
  onClose,
  apiBaseUrl,
  completedQuests = [],
  failedQuests = [],
}: EpochResultWindowProps) {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);
  const [quests, setQuests] = useState<Quest[]>([]);
  const [scoreLatest, setScoreLatest] = useState<ScoreLatest | null>(null);

  // Run close handling (open celebration, then close) only once even if button and Esc overlap.
  const closingRef = useRef(false);
  const close = useCallback(() => {
    if (closingRef.current) return;
    closingRef.current = true;
    Promise.resolve(onClose()).catch((err) => {
      console.error("failed to close epoch result", err);
      closingRef.current = false;
    });
  }, [onClose]);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== "Escape") return;
      event.preventDefault();
      close();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [close]);

  // Switch to dedicated result BGM (bgm-result.opus) while epoch results are displayed.
  useEffect(() => {
    void emit("bgm:request", { id: "epoch_result", track: "result", priority: 10 });
    return () => {
      void emit("bgm:release", { id: "epoch_result" });
    };
  }, []);

  // Fetch achievements as source data for the lower-left list and nearly-complete strip.
  useEffect(() => {
    const controller = new AbortController();
    (async () => {
      try {
        const data = await fetchApi<{ quests: Quest[] }>(
          buildApiUrl(apiBaseUrl, "/api/v1/quests"),
          { signal: controller.signal },
        );
        setQuests(data.quests);
      } catch (err) {
        if (!controller.signal.aborted) {
          console.error("failed to fetch quests", err);
        }
      }
    })();
    return () => controller.abort();
  }, [apiBaseUrl]);

  // Fetch the governance score (current score and composition).
  useEffect(() => {
    const controller = new AbortController();
    (async () => {
      try {
        const data = await fetchApi<{ latest: ScoreLatest }>(
          buildApiUrl(apiBaseUrl, "/api/v1/score/"),
          { signal: controller.signal },
        );
        setScoreLatest(data.latest ?? null);
      } catch (err) {
        if (!controller.signal.aborted) {
          console.error("failed to fetch city score", err);
        }
      }
    })();
    return () => controller.abort();
  }, [apiBaseUrl]);

  // Nearly completed quests: progress >= 70%, active, and not hidden.
  // Showing how close they are motivates the next epoch.
  const nearQuests = useMemo(() => {
    const completedIds = new Set(completedQuests.map((q) => q.id));
    return quests
      .filter(
        (q) =>
          q.status === "active" &&
          !q.hidden &&
          !q.is_force_completable &&
          !completedIds.has(q.id) &&
          q.progress >= NEAR_COMPLETION_THRESHOLD &&
          q.progress < 1,
      )
      .sort((a, b) => b.progress - a.progress)
      .slice(0, 4);
    // Exclude completedQuests from dependencies: epoch_confirm passes this static array only once.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [quests]);

  // Display road/public facility construction together in one list with type badges.
  const constructionItems = report.construction_progress;
  const constructionProjects = report.construction_projects;

  // Population change (after − before), used for positive/negative coloring.
  const populationDelta = report.population.after - report.population.before;

  const stats = report.statistics;
  const trendWindow = stats.trend_window ?? 0;

  // Order achievements by active (highest progress first), completed, then failed.
  // Omit locked and secret-until-complete achievements, since their conditions are also hidden in the achievements window.
  const listedQuests = useMemo(() => {
    const order: Partial<Record<Quest["status"], number>> = { active: 0, completed: 1, failed: 2 };
    return quests
      .filter((q) => order[q.status] !== undefined && !(q.hidden && q.status !== "completed"))
      .sort((a, b) => (order[a.status] ?? 0) - (order[b.status] ?? 0) || b.progress - a.progress);
  }, [quests]);

  // Statistics tile data combines value, absolute-level warning color, and trend direction.
  const statTiles = useMemo(() => {
    const history = stats.history ?? {};
    const pct = (v: number) => `${Math.round(v * 100)}%`;
    const signed = (v: number) => `${v >= 0 ? "+" : ""}${v.toLocaleString()}`;
    const incidentWarn = scaleIncidentThreshold(STAT_INCIDENT_WARN, epochCount);
    const incidentDanger = scaleIncidentThreshold(STAT_INCIDENT_DANGER, epochCount);

    const specs: {
      key: string;
      label: string;
      value: string;
      sub?: string;
      tone: StatTone;
      polarity: StatPolarity;
    }[] = [
      {
        key: "population",
        label: t("epochResult.statPopulation"),
        value: report.population.after.toLocaleString(),
        sub: signed(populationDelta),
        tone: "normal",
        polarity: "up-good",
      },
      // Tourists currently staying, with arrivals minus departures as supplementary net change in people. Omit while tourism is undefined/inactive.
      ...(report.population.tourist_count != null
        ? [
            {
              key: "tourists",
              label: t("epochResult.statTourists"),
              value: report.population.tourist_count.toLocaleString(),
              sub: signed((report.population.tourist_arrivals ?? 0) - (report.population.tourist_departures ?? 0)),
              tone: "normal" as StatTone,
              polarity: "up-good" as StatPolarity,
            },
          ]
        : []),
      {
        key: "gdp",
        label: t("epochResult.statGdp"),
        value: formatCompactAmount(stats.gdp, locale),
        tone: "normal",
        polarity: "up-good",
      },
      {
        key: "price",
        label: t("epochResult.statPrice"),
        value: stats.price.toLocaleString(undefined, { maximumFractionDigits: 1 }),
        tone: "normal",
        polarity: "neutral",
      },
      {
        key: "income",
        label: t("epochResult.statIncome"),
        value: t("common.currency", { value: Math.round(stats.income) }),
        tone: "normal",
        polarity: "up-good",
      },
      {
        key: "unemployment_rate",
        label: t("epochResult.statUnemployment"),
        value: `${(stats.unemployment_rate * 100).toFixed(1)}%`,
        tone: toneAbove(stats.unemployment_rate, STAT_UNEMPLOYMENT_WARN, STAT_UNEMPLOYMENT_DANGER),
        polarity: "up-bad",
      },
      {
        key: "avg_healthcare",
        label: t("epochResult.statHealthcare"),
        value: pct(stats.avg_healthcare),
        tone: toneBelow(stats.avg_healthcare, STAT_SATISFACTION_WARN, STAT_SATISFACTION_DANGER),
        polarity: "up-good",
      },
      {
        key: "avg_nutrition",
        label: t("epochResult.statNutrition"),
        value: pct(stats.avg_nutrition),
        tone: toneBelow(stats.avg_nutrition, STAT_SATISFACTION_WARN, STAT_SATISFACTION_DANGER),
        polarity: "up-good",
      },
      {
        key: "avg_leisure",
        label: t("epochResult.statLeisure"),
        value: pct(stats.avg_leisure),
        tone: toneBelow(stats.avg_leisure, STAT_SATISFACTION_WARN, STAT_SATISFACTION_DANGER),
        polarity: "up-good",
      },
      {
        key: "fire_count",
        label: t("epochResult.statFire"),
        value: stats.fire_count.toLocaleString(),
        tone: toneAbove(stats.fire_count, incidentWarn, incidentDanger),
        polarity: "up-bad",
      },
      {
        key: "traffic_accident_count",
        label: t("epochResult.statTraffic"),
        value: stats.traffic_accident_count.toLocaleString(),
        tone: toneAbove(stats.traffic_accident_count, incidentWarn, incidentDanger),
        polarity: "up-bad",
      },
      {
        key: "crime_count",
        label: t("epochResult.statCrime"),
        value: stats.crime_count.toLocaleString(),
        tone: toneAbove(stats.crime_count, incidentWarn, incidentDanger),
        polarity: "up-bad",
      },
    ];

    return specs.map((spec) => {
      const direction = computeTrend(history[spec.key]);
      return {
        ...spec,
        direction,
        trendTone: resolveTrendTone(direction, spec.polarity),
        trendLabel: t(`epochResult.trend.${direction}`),
      };
    });
  }, [stats, report.population.after, populationDelta, epochCount, locale, t]);

  const seasonImage = seasonImageUrl(currentDate);

  return (
    <div className="epoch-result">
      {/* Resulting month's image, cropped to full width and half window height, faintly fading downward into the base. */}
      {seasonImage && (
        <img className="epoch-result-backdrop" src={seasonImage} alt="" aria-hidden={true} draggable={false} />
      )}

      {/* Hero: large date on the left, score on the right. */}
      <header className="epoch-result-hero">
        <div className="epoch-result-date-block">
          <span className="epoch-result-overline">{t("windowType.epoch_result")}</span>
          <EpochDate daysSinceEpoch={currentDate} />
          {epochCount > 1 ? (
            <span className="epoch-result-date-sub">
              {t("epochResult.epochCountSuffix", { count: epochCount })}
            </span>
          ) : null}
        </div>
        <ScoreBlock latest={scoreLatest} />
      </header>

      <div className="epoch-result-body">
        {/* Quest strip: horizontal chips for completed, failed, and nearly completed quests. */}
        {(completedQuests.length > 0 || failedQuests.length > 0 || nearQuests.length > 0) && (
          <div className="epoch-result-quest-strip">
            {completedQuests.map((q) => (
              <div
                key={`done-${q.id}`}
                className="epoch-result-quest-chip epoch-result-quest-chip--completed"
                title={resolveLocalizedText(q.description, i18n.language)}
              >
                <span className="epoch-result-quest-chip-icon">&#10003;</span>
                <span className="epoch-result-quest-chip-name">{resolveLocalizedText(q.name, i18n.language)}</span>
                <span className="epoch-result-quest-chip-label">{t("epochResult.questCompleted")}</span>
              </div>
            ))}
            {failedQuests.map((q) => (
              <div
                key={`failed-${q.id}`}
                className="epoch-result-quest-chip epoch-result-quest-chip--failed"
                title={resolveLocalizedText(q.description, i18n.language)}
              >
                <span className="epoch-result-quest-chip-icon">&#10005;</span>
                <span className="epoch-result-quest-chip-name">{resolveLocalizedText(q.name, i18n.language)}</span>
                <span className="epoch-result-quest-chip-label">{t("epochResult.questFailed")}</span>
              </div>
            ))}
            {nearQuests.map((q) => (
              <div
                key={`near-${q.id}`}
                className="epoch-result-quest-chip epoch-result-quest-chip--near"
                title={resolveLocalizedText(q.description, i18n.language)}
              >
                <span className="epoch-result-quest-chip-name">{resolveLocalizedText(q.name, i18n.language)}</span>
                <span className="epoch-result-quest-chip-bar">
                  <span
                    className="epoch-result-quest-chip-bar-fill"
                    style={{ width: `${Math.round(q.progress * 100)}%` }}
                  />
                </span>
                <span className="epoch-result-quest-chip-pct">{Math.round(q.progress * 100)}%</span>
              </div>
            ))}
          </div>
        )}

        {/* Main area: primary statistics/achievements column and construction status column. */}
        <div className="epoch-result-main">
          {/* ── Left column: stacked blocks without frames ── */}
          <div className="epoch-result-col epoch-result-col-stats">
            {/* Statistics tiles */}
            <section className="epoch-result-block">
              <h3 className="epoch-result-section-title">{t("epochResult.sectionStatistics")}</h3>
              <div className="epoch-result-stat-grid">
                {statTiles.map((tile) => (
                  <StatTile
                    key={tile.key}
                    icon={STAT_ICONS[tile.key] ?? placeholderIcon}
                    label={tile.label}
                    value={tile.value}
                    sub={tile.sub}
                    tone={tile.tone}
                    direction={tile.direction}
                    trendTone={tile.trendTone}
                    series={stats.history?.[tile.key]}
                    trendTitle={
                      trendWindow > 0
                        ? t("epochResult.trendHint", { count: trendWindow, trend: tile.trendLabel })
                        : tile.trendLabel
                    }
                  />
                ))}
              </div>
            </section>

            {/* Achievements: active conditions/current values, filling the left column's remaining height and scrolling overflow. */}
            <section className="epoch-result-block epoch-result-block--fill">
              <h3 className="epoch-result-section-title">{t("achievements.title")}</h3>
              <div className="epoch-result-scroll">
                <QuestList quests={listedQuests} />
              </div>
            </section>
          </div>{/* /.epoch-result-col-stats */}

          {/* ── Right column: combined road/public facility construction list, with heading outside its card ── */}
          <div className="epoch-result-col epoch-result-col-right">
            <section className="epoch-result-block epoch-result-block--fill">
              <h3 className="epoch-result-section-title">{t("epochResult.sectionConstruction")}</h3>
              <div className="epoch-result-scroll epoch-result-panel">
                <ConstructionList projects={constructionProjects} items={constructionItems} />
              </div>
            </section>
          </div>{/* /.epoch-result-col-right */}
        </div>{/* /.epoch-result-main */}
      </div>{/* /.epoch-result-body */}

      <footer className="epoch-result-footer">
        <SaveDialogButton kind="primary" onClick={close}>{t("common.close")}</SaveDialogButton>
      </footer>
    </div>
  );
}

/**
 * Achievements list. Active quests show name, description, deadline, overall progress, and each condition's
 * progress bar/current/target values.
 * Display completed/failed quests subtly as one line containing name and state.
 * Omit condition metric names, which are internal paths; author-written names/descriptions communicate the
 * objective.
 */
function QuestList({ quests }: { quests: Quest[] }): ReactNode {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);

  if (quests.length === 0) {
    return <p className="epoch-result-empty">{t("achievements.noQuests")}</p>;
  }

  return (
    <ul className="epoch-result-quests">
      {quests.map((q) => {
        const name = resolveLocalizedText(q.name, i18n.language);
        if (q.status !== "active") {
          return (
            <li key={q.id} className={`epoch-result-quest epoch-result-quest--${q.status}`}>
              <div className="epoch-result-quest-head">
                <span className="epoch-result-quest-mark" aria-hidden={true}>
                  {q.status === "completed" ? "✓" : "✕"}
                </span>
                <span className="epoch-result-quest-name">{name}</span>
                <span className="epoch-result-quest-state">{t(`epochResult.questState.${q.status}`)}</span>
              </div>
            </li>
          );
        }
        const description = resolveLocalizedText(q.description, i18n.language);
        return (
          <li key={q.id} className="epoch-result-quest epoch-result-quest--active">
            <div className="epoch-result-quest-head">
              <span className="epoch-result-quest-name">{name}</span>
              {description && <span className="epoch-result-quest-desc" title={description}>{description}</span>}
              {q.deadline_epoch !== null && (
                <span className="epoch-result-quest-deadline">
                  {t("achievements.deadlineEpoch", { epoch: q.deadline_epoch })}
                </span>
              )}
              {/* Truncate so incomplete achievements never display 100%. */}
              <span className="epoch-result-quest-pct">{Math.floor(q.progress * 100)}%</span>
            </div>
            {q.clause_states.map((clause) => (
              <QuestClauseLine key={clause.clause_key} clause={clause} locale={locale} />
            ))}
          </li>
        );
      })}
    </ul>
  );
}

/**
 * One condition row, combining a progress bar with "current value / comparison target".
 * Use a small label for temporal conditions such as change from the previous epoch or consecutive streaks. Color
 * fulfilled condition bars as completed.
 */
function QuestClauseLine({ clause, locale }: { clause: QuestClauseState; locale: string }): ReactNode {
  const { t } = useTranslation();
  const modeLabel =
    clause.mode === "streak"
      ? t("achievements.streakProgress", { current: clause.streak ?? 0, window: clause.window })
      : clause.mode !== "value"
        ? t(`achievements.mode.${clause.mode}`, { window: clause.window })
        : null;
  return (
    <div className={`epoch-result-quest-clause${clause.satisfied ? " epoch-result-quest-clause--ok" : ""}`}>
      <span className="epoch-result-quest-bar" aria-hidden={true}>
        <span style={{ width: `${Math.round(Math.min(1, Math.max(0, clause.progress)) * 100)}%` }} />
      </span>
      <span className="epoch-result-quest-values">
        <span className="epoch-result-quest-current">{formatQuestValue(clause.current, locale)}</span>
        {" / "}
        {questCompareLabel(clause.compare)} {formatQuestValue(clause.target, locale)}
      </span>
      {modeLabel && <span className="epoch-result-quest-mode">{modeLabel}</span>}
    </div>
  );
}

/**
 * Score block: large current score and its composition band.
 *
 * Use the achievements waterfall's breakdown (living standards + living environment − disaster penalty = final
 * score).
 * Separate positive contributions and penalties into two rows, sharing the scale where total positive
 * contributions = 100%.
 */
function ScoreBlock({ latest }: { latest: ScoreLatest | null }): ReactNode {
  const { t } = useTranslation();

  const overline = <span className="epoch-result-overline">{t("epochResult.sectionScore")}</span>;
  if (!latest) {
    return (
      <div className="epoch-result-score">
        {overline}
        <p className="epoch-result-empty">{t("common.loading")}</p>
      </div>
    );
  }

  const fmt = (v: number) => v.toLocaleString(undefined, { maximumFractionDigits: 1 });
  const qol = latest.city_score_qol;
  const penalty = latest.city_score_collapse_penalty;
  const pct = (v: number) => (qol > 0 ? Math.min(100, Math.max(0, (v / qol) * 100)) : 0);

  return (
    <div className="epoch-result-score">
      {overline}
      <div className="epoch-result-score-head">
        <span className="epoch-result-score-label">{t("achievements.currentScore")}</span>
        <span className="epoch-result-score-value">{fmt(latest.city_score)}</span>
        <span className="epoch-result-score-unit">{t("achievements.points")}</span>
        <span className="epoch-result-score-per-household">
          {t("achievements.perHouseholdScore")} {latest.city_score_per_household.toFixed(2)}
        </span>
      </div>

      {/*
 * Separate positive contributions and deductions into rows sharing one scale (total contributions = 100%),
 * so band lengths directly show the penalty's weight.
 */}
      <div className="epoch-result-score-rows">
        <span className="epoch-result-score-row-label">{t("epochResult.scoreGain")}</span>
        <div className="epoch-result-score-bar">
          <span className="epoch-result-score-seg epoch-result-score-qol" style={{ width: `${pct(qol)}%` }} />
        </div>

        <span className="epoch-result-score-row-label">{t("epochResult.scoreLoss")}</span>
        <div className="epoch-result-score-bar">
          <span
            className="epoch-result-score-seg epoch-result-score-penalty"
            style={{ width: `${pct(penalty)}%` }}
          />
        </div>
      </div>

      <div className="epoch-result-score-legend">
        <span className="epoch-result-score-legend-item">
          <span className="epoch-result-score-swatch epoch-result-score-qol" />
          {t("achievements.qolScore")} +{fmt(qol)}
        </span>
        <span className="epoch-result-score-legend-item">
          <span className="epoch-result-score-swatch epoch-result-score-penalty" />
          {t("achievements.collapsePenalty")} {penalty > 0 ? `-${fmt(penalty)}` : "0"}
        </span>
      </div>
    </div>
  );
}

/**
 * Trend arrows: rotate one shape to show horizontal (flat), diagonally up (rising), or diagonally down
 * (falling).
 * Supply color through CSS (currentColor). Render nothing for direction="none" (insufficient history).
 */
function TrendArrow({
  direction,
  tone,
}: {
  direction: TrendDirection;
  tone: TrendTone;
}): ReactNode {
  if (direction === "none") return null;
  const angle = direction === "up" ? -35 : direction === "down" ? 35 : 0;
  return (
    <svg
      className={`epoch-result-trend epoch-result-trend-${tone}`}
      viewBox="0 0 24 24"
      width="16"
      height="16"
      aria-hidden={true}
    >
      <g transform={`rotate(${angle} 12 12)`} fill="none" stroke="currentColor" strokeWidth="2.4">
        <line x1="4" y1="12" x2="18" y2="12" strokeLinecap="round" />
        <polyline points="13,7 19,12 13,17" strokeLinecap="round" strokeLinejoin="round" />
      </g>
    </svg>
  );
}

/** History line's vertical range (viewBox 0–100). Keep it in the tile's lower half, clear of top-row text. */
const SPARK_TOP = 48;
const SPARK_BOTTOM = 90;

/**
 * Faint history behind statistics tiles (latest trend_window epochs in `statistics.history`).
 * Stretch minimum to maximum over the vertical range to show shape only; flat series become a centered
 * horizontal line.
 * Exclude null points predating city history and draw nothing with fewer than two points. Use the trend arrow's
 * color.
 */
function StatSparkline({ series, tone }: { series?: (number | null)[]; tone: TrendTone }): ReactNode {
  const gradientId = `epoch-result-spark-${useId().replace(/[^a-zA-Z0-9_-]/g, "")}`;
  const values = (series ?? []).filter((v): v is number => typeof v === "number" && Number.isFinite(v));
  if (values.length < 2) return null;

  const min = Math.min(...values);
  const range = Math.max(...values) - min;
  const x = (i: number) => (i / (values.length - 1)) * 100;
  const y = (v: number) =>
    range > 0 ? SPARK_BOTTOM - ((v - min) / range) * (SPARK_BOTTOM - SPARK_TOP) : (SPARK_TOP + SPARK_BOTTOM) / 2;
  const line = values.map((v, i) => `${i === 0 ? "M" : "L"}${x(i).toFixed(2)},${y(v).toFixed(2)}`).join(" ");

  return (
    <svg className={`epoch-result-spark epoch-result-spark-${tone}`} viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden={true}>
      <defs>
        <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="currentColor" stopOpacity="0.22" />
          <stop offset="1" stopColor="currentColor" stopOpacity="0" />
        </linearGradient>
      </defs>
      <path d={`${line} L100,100 L0,100 Z`} fill={`url(#${gradientId})`} />
      <path d={line} fill="none" stroke="currentColor" strokeOpacity="0.55" strokeWidth="1.5" vectorEffect="non-scaling-stroke" />
    </svg>
  );
}

/**
 * One statistics tile: icon, label, and trend arrow above a large value, with faint history behind.
 * tone assesses the absolute current level; warn/danger colors the whole tile.
 * sub adds supplementary text to the value's right, such as population change.
 */
function StatTile({
  icon,
  label,
  value,
  sub,
  tone,
  direction,
  trendTone,
  series,
  trendTitle,
}: {
  icon: string;
  label: string;
  value: string;
  sub?: string;
  tone: StatTone;
  direction: TrendDirection;
  trendTone: TrendTone;
  series?: (number | null)[];
  trendTitle: string;
}): ReactNode {
  return (
    <div className={`epoch-result-stat-tile epoch-result-stat-tile-${tone}`} title={trendTitle}>
      <StatSparkline series={series} tone={trendTone} />
      <span className="epoch-result-stat-head">
        <span className="epoch-result-stat-icon">
          <img src={icon} alt="" aria-hidden={true} />
        </span>
        <span className="epoch-result-stat-label">{label}</span>
        <TrendArrow direction={direction} tone={trendTone} />
      </span>
      <span className="epoch-result-stat-value-row">
        <span className="epoch-result-stat-value">{value}</span>
        {sub != null ? <span className="epoch-result-stat-sub">{sub}</span> : null}
      </span>
    </div>
  );
}

/**
 * Construction progress list: tagged projects first, followed by combined untagged roads/public facilities.
 * Each item has two rows; projects add member icons below.
 * Top: type badge, name, and after_status badge (no status badge for projects).
 * Bottom: progress bar, before/after percentages, and completion label.
 * Show a No Items message if empty.
 */
function ConstructionList({
  projects,
  items,
}: {
  projects: ConstructionProjectReport[];
  items: ConstructionProgressItem[];
}): ReactNode {
  const { t } = useTranslation();
  const masterData = useMasterData();

  if (projects.length === 0 && items.length === 0) {
    return <p className="epoch-result-empty">{t("epochResult.noItems")}</p>;
  }

  return (
    <ul className="epoch-result-construction-list">
      {projects.map((project) => (
        <li key={`project-${project.tag}`} className="epoch-result-construction-item">
          <div className="epoch-result-construction-head">
            <span className="epoch-result-kind-badge epoch-result-kind-badge-project">
              {t("epochResult.kindProject")}
            </span>
            <span className="epoch-result-name-text" title={project.tag}>{project.tag}</span>
          </div>
          <ProgressRow before={project.before_pct} after={project.after_pct} completed={project.completed} />
          <div className="epoch-result-project-members" aria-hidden={true}>
            {project.members.map((member, index) => (
              <ProjectMemberIcon key={index} kind={member.entity_type} completed={member.completed} />
            ))}
          </div>
        </li>
      ))}
      {items.map((item) => {
        const displayName = item.name || `#${item.id}`;
        const statusStyle = STATUS_STYLE_MAP[item.after_status] ?? { bg: "#6b7280", text: "#ffffff" };
        // For roads, use the variant category name. Fall back to generic Road only when unavailable, e.g. before master data loads.
        const pathType = masterData?.paths?.[item.variant]?.type;
        const kindLabel =
          item.entity_type === "path"
            ? t(`path_construction.category.${pathType}`, { defaultValue: t("epochResult.kindPath") })
            : t("epochResult.kindBuilding");
        return (
          <li key={`${item.entity_type}-${item.id}`} className="epoch-result-construction-item">
            <div className="epoch-result-construction-head">
              <span className={`epoch-result-kind-badge epoch-result-kind-badge-${item.entity_type}`}>
                {kindLabel}
              </span>
              <span className="epoch-result-name-text" title={displayName}>{displayName}</span>
              <span
                className="epoch-result-status-badge"
                style={{ backgroundColor: statusStyle.bg, color: statusStyle.text }}
              >
                {t(`status.${item.after_status}`)}
              </span>
            </div>
            <ProgressRow before={item.before_pct} after={item.after_pct} completed={item.completed} />
          </li>
        );
      })}
    </ul>
  );
}

/**
 * One row of progress bar, before/after percentages, and completion label.
 * Overlay pale progress after execution with even paler progress before execution, highlighting the advance
 * during this epoch.
 */
function ProgressRow({
  before,
  after,
  completed,
}: {
  before: number;
  after: number;
  completed: boolean;
}): ReactNode {
  const { t } = useTranslation();
  return (
    <div className="epoch-result-progress">
      <div className="epoch-result-progress-bar" aria-hidden={true}>
        <div
          className="epoch-result-progress-after"
          style={{ width: `${Math.min(100, Math.max(0, after))}%` }}
        />
        <div
          className="epoch-result-progress-before"
          style={{ width: `${Math.min(100, Math.max(0, before))}%` }}
        />
      </div>
      <span className="epoch-result-progress-text">
        {t("epochResult.progressFormat", {
          before: before.toFixed(0),
          after: after.toFixed(0),
        })}
      </span>
      {completed && <span className="epoch-result-completed">{t("epochResult.completed")}</span>}
    </div>
  );
}

/**
 * Small icon for one project member (road/building).
 * Color comes from CSS (currentColor): gray incomplete, green complete.
 */
function ProjectMemberIcon({
  kind,
  completed,
}: {
  kind: "agent" | "path";
  completed: boolean;
}): ReactNode {
  return (
    <svg
      className={`epoch-result-member-icon${completed ? " epoch-result-member-icon-done" : ""}`}
      viewBox="0 0 24 24"
      width="14"
      height="14"
    >
      {kind === "path" ? (
        <g>
          <polygon points="9,3 15,3 22,21 2,21" fill="currentColor" />
          <line x1="12" y1="6" x2="12" y2="10" stroke="var(--flat-sunken)" strokeWidth="1.6" />
          <line x1="12" y1="13" x2="12" y2="17" stroke="var(--flat-sunken)" strokeWidth="1.6" />
        </g>
      ) : (
        <g fill="currentColor">
          <rect x="5" y="3" width="14" height="18" />
          <rect x="8" y="6" width="3" height="3" fill="var(--flat-sunken)" />
          <rect x="13" y="6" width="3" height="3" fill="var(--flat-sunken)" />
          <rect x="8" y="11" width="3" height="3" fill="var(--flat-sunken)" />
          <rect x="13" y="11" width="3" height="3" fill="var(--flat-sunken)" />
        </g>
      )}
    </svg>
  );
}
