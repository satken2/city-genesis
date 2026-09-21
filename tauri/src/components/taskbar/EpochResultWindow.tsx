/**
 * エポック実行結果ウィンドウ
 *
 * エポック実行完了後に別ウィンドウとして開かれ、バックエンドの
 * POST /api/v1/epochs レスポンスの epoch_report フィールドを受け取って表示する。
 *
 * レイアウトはヘッダ + クエストストリップ + 横2分割のメインエリア + フッタ(閉じるボタン):
 * - ヘッダ: 左上に大きく現在のゲーム内日付(実行後)
 * - クエストストリップ: このエポックで達成したクエスト(お祝いウィンドウの予告)と、
 *   達成間近(進捗70%以上)のアクティブなクエストの進捗バーを横並びで表示する。
 *   達成間近は GET /api/v1/quests のサーバ計算 progress を使う。
 * - 左(主役): 達成目標 / 統計タイル / スコア を縦に並べる。左カラムの各ブロックは
 *   カード(枠)を持たない。中身のタイルがカードなので、入れ子にすると二重枠になるため。
 *   - 達成目標: 全目標をチェックマーク1個ずつで表し、達成のみ色付きにした簡易表示。
 *     ホバーで目標名と状態の一覧をポップアップする(GET /api/v1/quests)。
 *   - 統計: 人口/GDP/物価/所得/失業率/医療・栄養・娯楽充足度/火災/交通事故/犯罪/為替レート。
 *   - スコア: 当期スコアとその構成(生活水準+住環境-災害ペナルティ)。GET /api/v1/score の
 *     latest を、達成目標ウィンドウと同じ内訳で軽量に描く(グラフライブラリは使わない)。
 * - 右カラム: 建設状況(道路と公共施設を種別バッジ付きの単一リストにまとめたもの)
 *
 * 統計タイルは2種類の視覚効果を持つ。この2つは独立した軸である。
 * - 変動方向(矢印): バックエンドが返す指標ごとの履歴(`statistics.history`、直近
 *   `trend_window` エポック分)の回帰傾きで、上昇(斜め上)/横ばい(横)/下降(斜め下)を示す。
 *   1エポックの差分は振れ幅が大きく毎回向きが反転するため、窓の傾きで判定する。
 *   矢印の色は指標の極性(値が増えて嬉しいか)で決まり、緑=good / 赤=bad / 灰=中立。
 * - 水準の警戒色(タイル自体の色): 現在値の絶対基準による評価。相対的な傾向がどうであれ
 *   「失業率15%」のような水準そのものが危険な状態を赤で示す。しきい値はこのファイルの
 *   UI定数(STAT_*_WARN / STAT_*_DANGER)。
 *
 * 閉じるボタンで親から渡された onClose を呼ぶ。notify="modal" の達成クエストがあれば
 * 親側で引き続き QuestCompleteWindow を表示する(結果→お祝いの連続演出)。
 */
import { ReactNode, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { emit } from "@tauri-apps/api/event";
import { EpochDate } from "../common/EpochDate";
import { STATUS_STYLE_MAP } from "../../local-constants";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { formatCompactAmount } from "../../utils/money";
import { resolveIntlLocale } from "../../utils/locale";
import type { Quest, QuestNotification } from "../../types";
import populationIcon from "../../assets/images/population.png";
import gdpIcon from "../../assets/images/menu/menu_stat.png";
import priceIcon from "../../assets/images/visual/price.png";
import incomeIcon from "../../assets/images/purchase.png";
import unemploymentIcon from "../../assets/images/citizen.png";
import healthcareIcon from "../../assets/images/visual/health.png";
import fireIcon from "../../assets/images/alerts/fire.png";
import trafficIcon from "../../assets/images/alerts/car-accident.png";
import crimeIcon from "../../assets/images/alerts/shooting.png";
import exchangeIcon from "../../assets/images/menu/menu_finance.png";
import placeholderIcon from "../../assets/images/ui/help.png";
import "./EpochResultWindow.css";

export type ConstructionProgressItem = {
  entity_type: "agent" | "path";
  id: number;
  name: string;
  before_pct: number;
  before_status: number;
  after_pct: number;
  after_status: number;
  completed: boolean;
};

export type PopulationReport = {
  before: number;
  after: number;
  births: number;
  deaths: number;
  immigrations: number;
  immigration_applicants?: number;
  emigrations: number;
  /** 観光客の到着人数(宿泊フロアへのチェックイン)・帰国人数・エポック末の滞在人数 */
  tourist_arrivals?: number;
  tourist_departures?: number;
  tourist_count?: number;
};

export type ExchangeRateReport = {
  before: number;
  after: number;
};

/** エポック実行完了時点の都市統計。統計タイルに表示する。 */
export type StatisticsReport = {
  price: number;                   // 小売財(GOODS_RETAIL)の平均価格
  income: number;                  // 世帯の平均収入
  unemployment_rate: number;       // 失業率(0..1比率)
  avg_healthcare: number;          // 世帯平均の医療充足度(0..1)
  avg_nutrition: number;           // 世帯平均の栄養充足度(0..1)
  avg_entertainment: number;       // 世帯平均の娯楽充足度(0..1)
  gdp: number;                     // 直近完了エポックの名目GDP
  fire_count: number;              // 実行エポック合計の火災件数
  traffic_accident_count: number;  // 実行エポック合計の交通事故件数
  crime_count: number;             // 実行エポック合計の犯罪件数
  /** history の窓幅(エポック数)。「直近Nエポックの傾向」の N。 */
  trend_window?: number;
  /**
   * 指標ごとの時系列(古い→新しい、末尾が直近完了エポック)。
   * 都市の歴史が窓より短い場合、古い側は null で埋まる。キーは統計値のキー名と
   * `population` を加えたもの。
   */
  history?: Record<string, (number | null)[]>;
};

/**
 * GET /api/v1/score の latest のうち、スコアブロックで使う項目だけを持つ型。
 * 達成目標ウィンドウ(AchievementsWindow)が使う全項目は必要ないため部分定義にしている。
 */
type ScoreLatest = {
  city_score: number;
  city_score_qol: number;
  city_score_env: number;
  city_score_per_household: number;
  city_score_collapse_penalty: number;
};

export type EpochReport = {
  construction_progress: ConstructionProgressItem[];
  population: PopulationReport;
  exchange_rate: ExchangeRateReport;
  statistics: StatisticsReport;
};

type EpochResultWindowProps = {
  report: EpochReport;
  /** 実行したエポック数。タイトルに補助表示する。 */
  epochCount: number;
  /** 実行後のゲーム内日付(1800年1月1日からの経過日数)。左上に大きく表示する。 */
  currentDate: number;
  onClose: () => void;
  apiBaseUrl: string;
  /** このエポック(count>1なら全エポック累積)で達成したクエスト */
  completedQuests?: QuestNotification[];
  /** このエポックで期限切れにより失敗したクエスト */
  failedQuests?: QuestNotification[];
};

/** 「達成間近」としてストリップに表示する進捗率のしきい値 */
const NEAR_COMPLETION_THRESHOLD = 0.7;

/**
 * 水準の警戒色のしきい値(絶対基準)。
 * 失業率・事故災害件数は「多いほど悪い」、充足度は「少ないほど悪い」。
 */
const STAT_UNEMPLOYMENT_WARN = 0.08;
const STAT_UNEMPLOYMENT_DANGER = 0.15;
const STAT_SATISFACTION_WARN = 0.6;
const STAT_SATISFACTION_DANGER = 0.4;
const STAT_INCIDENT_WARN = 1;
const STAT_INCIDENT_DANGER = 5;

/**
 * 事故災害の件数タイルは複数エポックまとめ実行の合計値なので、しきい値も実行エポック数で
 * 引き伸ばす(1エポックあたり STAT_INCIDENT_* 件を基準にする)。
 */
const scaleIncidentThreshold = (threshold: number, epochCount: number) =>
  threshold * Math.max(1, epochCount);

/** トレンド判定に必要な最小データ点数。都市が若く履歴が足りないうちは矢印を出さない。 */
const MIN_TREND_POINTS = 3;
/** 窓全体の相対変化(平均値に対する比)がこの値未満なら横ばいとみなす。 */
const TREND_FLAT_RATIO = 0.02;

/**
 * タイルのアイコン。専用の画像が用意されていない指標(栄養・娯楽)は、
 * 未整備であることが分かるようプレースホルダ(?アイコン)を出しておく。
 */
const STAT_ICONS: Record<string, string> = {
  population: populationIcon,
  gdp: gdpIcon,
  price: priceIcon,
  income: incomeIcon,
  unemployment_rate: unemploymentIcon,
  avg_healthcare: healthcareIcon,
  avg_nutrition: placeholderIcon,
  avg_entertainment: placeholderIcon,
  fire_count: fireIcon,
  traffic_accident_count: trafficIcon,
  crime_count: crimeIcon,
  exchange_rate: exchangeIcon,
};

/** タイルの水準評価。normal=通常 / warn=注意(橙) / danger=危険(赤) */
type StatTone = "normal" | "warn" | "danger";
/** 変動方向。none=履歴不足で判定不能 */
type TrendDirection = "up" | "flat" | "down" | "none";
/** 指標の極性(値が増えるのは良いことか)。矢印の色分けに使う。 */
type StatPolarity = "up-good" | "up-bad" | "neutral";
/** 矢印の色。good=緑 / bad=赤 / neutral=灰 */
type TrendTone = "good" | "bad" | "neutral";

/** 値が大きいほど悪い指標の水準評価。 */
function toneAbove(value: number, warn: number, danger: number): StatTone {
  if (value >= danger) return "danger";
  if (value >= warn) return "warn";
  return "normal";
}

/** 値が小さいほど悪い指標の水準評価。 */
function toneBelow(value: number, warn: number, danger: number): StatTone {
  if (value <= danger) return "danger";
  if (value <= warn) return "warn";
  return "normal";
}

/**
 * 履歴系列から変動方向を判定する。
 *
 * 最小二乗法で1エポックあたりの傾きを求め、窓全体の変化量を系列平均で割った
 * 相対変化として評価する(指標ごとに単位も桁も違うため、絶対量では比較できない)。
 * null(都市の歴史より古いスロット)は除外し、残り点数が足りなければ判定しない。
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

  const span = (covariance / variance) * (n - 1); // 窓全体での変化量
  const ratio = span / Math.max(Math.abs(meanY), 1e-9);
  if (Math.abs(ratio) < TREND_FLAT_RATIO) return "flat";
  return ratio > 0 ? "up" : "down";
}

/** 変動方向と指標の極性から矢印の色を決める。 */
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

  // エポック結果画面の表示中は専用BGM (bgm-result.opus) に切り替える
  useEffect(() => {
    void emit("bgm:request", { id: "epoch_result", track: "result", priority: 10 });
    return () => {
      void emit("bgm:release", { id: "epoch_result" });
    };
  }, []);

  // 達成目標の一覧(達成状況の簡易表示と達成間近ストリップの元データ)を取得する。
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

  // 施政スコア(当期スコアとその構成)を取得する。
  useEffect(() => {
    const controller = new AbortController();
    (async () => {
      try {
        const data = await fetchApi<{ latest: ScoreLatest }>(
          buildApiUrl(apiBaseUrl, "/api/v1/score"),
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

  // 達成間近(進捗70%以上・アクティブ・隠しでない)のクエスト。
  // 「あと少しで達成」を見せて次のエポックの動機付けにする。
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
    // completedQuests は epoch_confirm から一度だけ渡される静的な配列のため依存に含めない
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [quests]);

  // 建設状況は道路・公共施設を分けず、種別バッジ付きの単一リストとして表示する。
  const constructionItems = report.construction_progress;

  // 人口増減(後-前)。表示の+/-色分けに使用する
  const populationDelta = report.population.after - report.population.before;

  // 為替レート増減(後-前)。通貨安(レート上昇)で正、通貨高(レート下落)で負。
  const fxDelta = report.exchange_rate.after - report.exchange_rate.before;

  const stats = report.statistics;
  const trendWindow = stats.trend_window ?? 0;

  // 達成目標の簡易表示用データ。1目標=チェックマーク1個で、達成済みだけ色が付く。
  // 隠し目標は達成するまで名前を伏せる(達成目標ウィンドウと同じ扱い)。
  const questSummary = useMemo(() => {
    const items: QuestCheckItem[] = quests.map((q) => ({
      id: q.id,
      name: q.hidden && q.status !== "completed" ? t("achievements.hiddenQuestName") : q.name,
      state:
        q.status === "completed"
          ? "completed"
          : q.status === "failed"
            ? "failed"
            : q.status === "locked"
              ? "locked"
              : "active",
    }));
    return { items, completedCount: items.filter((i) => i.state === "completed").length };
  }, [quests, t]);

  // 統計タイルの表示データ。値・水準の警戒色・変動方向の3点を1タイルにまとめる。
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
      // 観光客の滞在人数。副表示は到着−帰国の純増減(人)。観光システム未稼働(未定義)の間は出さない。
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
        key: "avg_entertainment",
        label: t("epochResult.statEntertainment"),
        value: pct(stats.avg_entertainment),
        tone: toneBelow(stats.avg_entertainment, STAT_SATISFACTION_WARN, STAT_SATISFACTION_DANGER),
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
      {
        // 為替レートは実行前後の変動幅を補助表示する。通貨安(レート上昇)を bad とする。
        key: "exchange_rate",
        label: t("epochResult.exchangeRate"),
        value: report.exchange_rate.after.toFixed(4),
        sub: `${fxDelta >= 0 ? "+" : ""}${fxDelta.toFixed(4)}`,
        tone: "normal",
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
  }, [stats, report.population.after, report.exchange_rate.after, populationDelta, fxDelta, epochCount, locale, t]);

  return (
    <div className="epoch-result-root">
      {/* ヘッダ: 左上に大きく日付 */}
      <header className="epoch-result-header">
        <div className="epoch-result-date-block">
          <EpochDate daysSinceEpoch={currentDate} />
          {epochCount > 1 ? (
            <span className="epoch-result-date-sub">
              {t("epochResult.epochCountSuffix", { count: epochCount })}
            </span>
          ) : null}
        </div>
      </header>

      {/* クエストストリップ: 達成・失敗・達成間近をチップで横並び表示する */}
      {(completedQuests.length > 0 || failedQuests.length > 0 || nearQuests.length > 0) && (
        <div className="epoch-result-quest-strip">
          {completedQuests.map((q) => (
            <div
              key={`done-${q.id}`}
              className={`epoch-result-quest-chip epoch-result-quest-chip--completed epoch-result-quest-chip--${q.tier}`}
              title={q.description}
            >
              <span className="epoch-result-quest-chip-icon">&#10003;</span>
              <span className="epoch-result-quest-chip-name">{q.name}</span>
              <span className="epoch-result-quest-chip-label">{t("epochResult.questCompleted")}</span>
            </div>
          ))}
          {failedQuests.map((q) => (
            <div
              key={`failed-${q.id}`}
              className="epoch-result-quest-chip epoch-result-quest-chip--failed"
              title={q.description}
            >
              <span className="epoch-result-quest-chip-icon">&#10005;</span>
              <span className="epoch-result-quest-chip-name">{q.name}</span>
              <span className="epoch-result-quest-chip-label">{t("epochResult.questFailed")}</span>
            </div>
          ))}
          {nearQuests.map((q) => (
            <div
              key={`near-${q.id}`}
              className="epoch-result-quest-chip epoch-result-quest-chip--near"
              title={q.description}
            >
              <span className="epoch-result-quest-chip-name">{q.name}</span>
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

      {/* メインエリア: 達成目標/統計/スコア(主役) + 建設状況 の2カラム */}
      <div className="epoch-result-main">
        {/* ── 左カラム: 枠を持たないブロックを縦に並べる ── */}
        <div className="epoch-result-col epoch-result-col-stats">
          {/* 達成目標: チェックマークの並びで達成数だけを見せる簡易表示 */}
          <section className="epoch-result-block">
            <div className="epoch-result-section-head">
              <h3 className="epoch-result-section-title">{t("achievements.title")}</h3>
              <span className="epoch-result-section-note">
                {t("epochResult.questProgress", {
                  completed: questSummary.completedCount,
                  total: questSummary.items.length,
                })}
              </span>
            </div>
            <QuestCheckStrip items={questSummary.items} />
          </section>

          {/* 統計タイル */}
          <section className="epoch-result-block">
            <div className="epoch-result-section-head">
              <h3 className="epoch-result-section-title">{t("epochResult.sectionStatistics")}</h3>
              {trendWindow > 0 ? (
                <span className="epoch-result-section-note">
                  {t("epochResult.trendLegend", { count: trendWindow })}
                </span>
              ) : null}
            </div>
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
                  trendTitle={
                    trendWindow > 0
                      ? t("epochResult.trendHint", { count: trendWindow, trend: tile.trendLabel })
                      : tile.trendLabel
                  }
                />
              ))}
            </div>
          </section>

          {/* スコア: 当期スコアとその構成 */}
          <section className="epoch-result-block">
            <div className="epoch-result-section-head">
              <h3 className="epoch-result-section-title">{t("epochResult.sectionScore")}</h3>
            </div>
            <ScoreBlock latest={scoreLatest} />
          </section>
        </div>{/* /.epoch-result-col-stats */}

        {/* ── 右カラム: 建設状況(道路・公共施設の統合リスト) ── */}
        <div className="epoch-result-col epoch-result-col-right">
          <section className="epoch-result-section epoch-result-section-construction">
            <h3 className="epoch-result-section-title">{t("epochResult.sectionConstruction")}</h3>
            <div className="epoch-result-construction-scroll">
              <ConstructionTable items={constructionItems} />
            </div>
          </section>
        </div>{/* /.epoch-result-col-right */}
      </div>{/* /.epoch-result-main */}

      {/* フッタ: 閉じるボタン */}
      <div className="epoch-result-buttons">
        <button className="epoch-result-btn epoch-result-close" onClick={onClose} type="button">
          {t("common.close")}
        </button>
      </div>
    </div>
  );
}

/** 達成目標の簡易表示1件分。state はチェックマークの見た目を決める。 */
type QuestCheckItem = {
  id: number;
  name: string;
  state: "completed" | "active" | "locked" | "failed";
};

/**
 * 達成目標の簡易表示。1目標につきチェックマークを1個並べ、達成済みだけ色を付ける
 * (未達成・未解放・失敗はグレー)。個数以上の情報は出さない代わりに、
 * ホバーで「どれが達成できているのか」の一覧をポップアップする。
 */
function QuestCheckStrip({ items }: { items: QuestCheckItem[] }): ReactNode {
  const { t } = useTranslation();

  if (items.length === 0) {
    return <p className="epoch-result-empty">{t("achievements.noQuests")}</p>;
  }

  return (
    <div className="epoch-result-quest-checks">
      <div className="epoch-result-quest-check-row">
        {items.map((item) => (
          <span
            key={item.id}
            className={`epoch-result-quest-check epoch-result-quest-check--${item.state}`}
            aria-hidden={true}
          >
            &#10003;
          </span>
        ))}
      </div>
      {/* ホバー時のみ表示する内訳。目標数が多いと縦に伸びるためスクロールさせる。 */}
      <div className="epoch-result-quest-popup" role="tooltip">
        {items.map((item) => (
          <div key={item.id} className="epoch-result-quest-popup-row">
            <span className={`epoch-result-quest-check epoch-result-quest-check--${item.state}`}>
              &#10003;
            </span>
            <span className="epoch-result-quest-popup-name">{item.name}</span>
            <span className="epoch-result-quest-popup-state">
              {t(`epochResult.questState.${item.state}`)}
            </span>
          </div>
        ))}
      </div>
    </div>
  );
}

/**
 * スコアブロック。当期スコア(大きい数値)と、その構成を1本の帯で示す。
 *
 * 構成は達成目標ウィンドウのウォーターフォールと同じ内訳
 * (生活水準 + 住環境 - 災害ペナルティ = 最終スコア)で、加点(生活水準・住環境)と
 * 減点(災害ペナルティ)を上下2行の帯に分けて描く。2行の尺度は共通(加点合計=100%)。
 */
function ScoreBlock({ latest }: { latest: ScoreLatest | null }): ReactNode {
  const { t } = useTranslation();

  if (!latest) {
    return <p className="epoch-result-empty">{t("common.loading")}</p>;
  }

  const fmt = (v: number) => v.toLocaleString(undefined, { maximumFractionDigits: 1 });
  const qol = latest.city_score_qol;
  const env = latest.city_score_env;
  const penalty = latest.city_score_collapse_penalty;
  const gross = qol + env;
  const pct = (v: number) => (gross > 0 ? Math.min(100, Math.max(0, (v / gross) * 100)) : 0);

  return (
    <div className="epoch-result-score">
      <div className="epoch-result-score-head">
        <span className="epoch-result-score-label">{t("achievements.currentScore")}</span>
        <span className="epoch-result-score-value">{fmt(latest.city_score)}</span>
        <span className="epoch-result-score-unit">{t("achievements.points")}</span>
        <span className="epoch-result-score-per-household">
          {t("achievements.perHouseholdScore")} {latest.city_score_per_household.toFixed(2)}
        </span>
      </div>

      {/* 加点と減点で行を分ける。2行は同じ尺度(加点合計=100%)なので、
          帯の長さをそのまま比べればペナルティの重さが分かる。 */}
      <div className="epoch-result-score-rows">
        <span className="epoch-result-score-row-label">{t("epochResult.scoreGain")}</span>
        <div className="epoch-result-score-bar">
          <span className="epoch-result-score-seg epoch-result-score-qol" style={{ width: `${pct(qol)}%` }} />
          <span className="epoch-result-score-seg epoch-result-score-env" style={{ width: `${pct(env)}%` }} />
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
          <span className="epoch-result-score-swatch epoch-result-score-env" />
          {t("achievements.envScore")} +{fmt(env)}
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
 * 変動方向を表す矢印。横(横ばい)・斜め上(上昇)・斜め下(下降)の3種を、
 * 同一形状の回転だけで描き分ける。色は CSS クラス(currentColor)で与える。
 * direction="none"(履歴不足)のときは何も描かない。
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

/**
 * 統計タイル1枚。[アイコン][ラベル+値][変動矢印]の横並びで、行の高さを抑えた密なタイルにする。
 * tone は現在値の絶対基準による水準評価で、warn/danger のときタイル全体を着色する。
 * sub を渡すと値の右に補助テキストを添える(人口の増減、為替レートの変動幅など)。
 */
function StatTile({
  icon,
  label,
  value,
  sub,
  tone,
  direction,
  trendTone,
  trendTitle,
}: {
  icon: string;
  label: string;
  value: string;
  sub?: string;
  tone: StatTone;
  direction: TrendDirection;
  trendTone: TrendTone;
  trendTitle: string;
}): ReactNode {
  return (
    <div className={`epoch-result-stat-tile epoch-result-stat-tile-${tone}`} title={trendTitle}>
      <img className="epoch-result-stat-icon" src={icon} alt="" aria-hidden={true} />
      <span className="epoch-result-stat-body">
        <span className="epoch-result-stat-label">{label}</span>
        <span className="epoch-result-stat-value-row">
          <span className="epoch-result-stat-value">{value}</span>
          {sub != null ? <span className="epoch-result-stat-sub">{sub}</span> : null}
        </span>
      </span>
      <TrendArrow direction={direction} tone={trendTone} />
    </div>
  );
}

/**
 * 建設進捗のテーブル表示(道路・公共施設の統合リスト)。
 * 列: 名前(種別バッジ付き) / ステータス(after_status でバッジ表示) / 進捗(バー+テキスト)。
 * リストが空の場合は「対象なし」メッセージを表示する。
 */
function ConstructionTable({
  items,
}: {
  items: ConstructionProgressItem[];
}): ReactNode {
  const { t } = useTranslation();

  if (items.length === 0) {
    return <p className="epoch-result-empty">{t("epochResult.noItems")}</p>;
  }

  return (
    <table className="epoch-result-table">
      <thead>
        <tr>
          <th className="epoch-result-th epoch-result-th-name">{t("epochResult.columnName")}</th>
          <th className="epoch-result-th epoch-result-th-status">{t("epochResult.columnStatus")}</th>
          <th className="epoch-result-th epoch-result-th-progress">{t("epochResult.columnProgress")}</th>
        </tr>
      </thead>
      <tbody>
        {items.map((item) => {
          const displayName = item.name || `#${item.id}`;
          const statusStyle = STATUS_STYLE_MAP[item.after_status] ?? { bg: "#6b7280", text: "#ffffff" };
          const kindLabel =
            item.entity_type === "path" ? t("epochResult.kindPath") : t("epochResult.kindBuilding");
          return (
            <tr key={`${item.entity_type}-${item.id}`} className="epoch-result-tr">
              <td className="epoch-result-td epoch-result-td-name" title={displayName}>
                <span className={`epoch-result-kind-badge epoch-result-kind-badge-${item.entity_type}`}>
                  {kindLabel}
                </span>
                <span className="epoch-result-name-text">{displayName}</span>
              </td>
              <td className="epoch-result-td epoch-result-td-status">
                <span
                  className="epoch-result-status-badge"
                  style={{ backgroundColor: statusStyle.bg, color: statusStyle.text }}
                >
                  {t(`status.${item.after_status}`)}
                </span>
              </td>
              <td className="epoch-result-td epoch-result-td-progress">
                <div className="epoch-result-progress">
                  <div className="epoch-result-progress-bar" aria-hidden={true}>
                    <div
                      className="epoch-result-progress-before"
                      style={{ width: `${Math.min(100, Math.max(0, item.before_pct))}%` }}
                    />
                    <div
                      className="epoch-result-progress-after"
                      style={{ width: `${Math.min(100, Math.max(0, item.after_pct))}%` }}
                    />
                  </div>
                  <span className="epoch-result-progress-text">
                    {t("epochResult.progressFormat", {
                      before: item.before_pct.toFixed(0),
                      after: item.after_pct.toFixed(0),
                    })}
                  </span>
                  {item.completed && (
                    <span className="epoch-result-completed">{t("epochResult.completed")}</span>
                  )}
                </div>
              </td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}
