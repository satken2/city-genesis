/**
 * エポック実行結果ウィンドウ
 *
 * エポック実行完了後に別ウィンドウとして開かれ、バックエンドの
 * POST /api/v1/epochs レスポンスの epoch_report フィールドを受け取って表示する。
 *
 * タイトルバーを持たないウィンドウ(レジストリの chromeless)で、閉じるのはフッタの「閉じる」と Esc。
 * レイアウトはヒーロー + クエストストリップ + 横2分割のメインエリア + フッタ:
 * - 背景: 実行後の月の画像(`utils/season` の seasonImageUrl。無ければ既定の画像、それも無ければ画像なし)を、
 *   ウィンドウの上半分に左右いっぱいで薄く敷き、縦の中央で消える。
 * - ヒーロー: 左に実行後のゲーム内日付、右にスコアを大きく出す。
 *   スコアは当期スコアとその構成(生活水準+住環境-災害ペナルティ)。GET /api/v1/score の
 *   latest を、達成目標ウィンドウと同じ内訳で軽量に描く(グラフライブラリは使わない)。
 * - クエストストリップ: このエポックで達成したクエスト(お祝いウィンドウの予告)と、
 *   達成間近(進捗70%以上)のアクティブなクエストの進捗バーを横並びで表示する。
 *   達成間近は GET /api/v1/quests のサーバ計算 progress を使う。
 * - 左(主役): 統計タイル / 達成目標 を縦に並べる。左カラムの各ブロックは
 *   カード(枠)を持たない。中身のタイルがカードなので、入れ子にすると二重枠になるため。
 *   - 統計: 人口/GDP/物価/所得/失業率/医療・栄養・娯楽充足度/火災/交通事故/犯罪。
 *   - 達成目標: 進行中の目標の説明・条件ごとの現在値と目標値・進捗と、達成・失敗した目標の一覧
 *     (GET /api/v1/quests)。左カラムの残りの高さを使い、溢れたらスクロールする。
 * - 右カラム: 建設状況(道路と公共施設を種別バッジ付きの単一リストにまとめたもの)。
 *   タグ(建設プロジェクト)の付いた工事は個別の行にせず、タグごとの1行にまとめて先頭に並べる。
 *   プロジェクトの行は、体積で按分した進捗バーの下に、メンバーの道路・建物を小さなアイコンで
 *   並べる(未完了=灰 / 完了=緑)。道路の種別バッジはバリアントのカテゴリ名(街路・鉄道・電気 など)。
 *
 * 統計タイルは2種類の視覚効果を持つ。この2つは独立した軸である。
 * - 変動方向(矢印): バックエンドが返す指標ごとの履歴(`statistics.history`、直近
 *   `trend_window` エポック分)の回帰傾きで、上昇(斜め上)/横ばい(横)/下降(斜め下)を示す。
 *   1エポックの差分は振れ幅が大きく毎回向きが反転するため、窓の傾きで判定する。
 *   矢印の色は指標の極性(値が増えて嬉しいか)で決まり、緑=good / 赤=bad / 灰=中立。
 * - 水準の警戒色(タイル自体の色): 現在値の絶対基準による評価。相対的な傾向がどうであれ
 *   「失業率15%」のような水準そのものが危険な状態を赤で示す。しきい値はこのファイルの
 *   UI定数(STAT_*_WARN / STAT_*_DANGER)。
 * タイルの背景には、矢印の判定に使うのと同じ履歴(直近 trend_window エポック)の推移を矢印と同じ色で薄く描く。
 *
 * 閉じる操作で親から渡された onClose を1回だけ呼ぶ。notify="modal" の達成クエストがあれば
 * 親側で引き続き QuestCompleteWindow を表示する(結果→お祝いの連続演出)。
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
import entertainmentIcon from "../../assets/images/stats/entertainment.png";
import fireIcon from "../../assets/images/stats/fire.png";
import trafficIcon from "../../assets/images/stats/traffic_accident.png";
import crimeIcon from "../../assets/images/stats/crime.png";
import placeholderIcon from "../../assets/images/ui/help.png";
import "./EpochResultWindow.css";

export type ConstructionProgressItem = {
  entity_type: "agent" | "path";
  id: number;
  name: string;
  /** 現在のバリアント。道路の種別バッジをマスターのカテゴリから引くのに使う。 */
  variant: number;
  before_pct: number;
  before_status: number;
  after_pct: number;
  after_status: number;
  completed: boolean;
};

/** タグ付き建設プロジェクト1件の進捗。進捗率はメンバーの建設体積で按分した平均。 */
export type ConstructionProjectReport = {
  tag: string;
  before_pct: number;
  after_pct: number;
  /** 全メンバーが完了 */
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
  /** 観光客の到着人数(宿泊フロアへのチェックイン)・帰国人数・エポック末の滞在人数 */
  tourist_arrivals?: number;
  tourist_departures?: number;
  tourist_count?: number;
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
  construction_projects: ConstructionProjectReport[];
  population: PopulationReport;
  statistics: StatisticsReport;
};

type EpochResultWindowProps = {
  report: EpochReport;
  /** 実行したエポック数。タイトルに補助表示する。 */
  epochCount: number;
  /** 実行後のゲーム内日付(1800年1月1日からの経過日数)。左上に大きく表示する。 */
  currentDate: number;
  onClose: () => void | Promise<void>;
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
 * タイルのアイコン(`assets/images/stats/`)。専用の画像が用意されていない指標(栄養)は、
 * 未整備であることが分かるようプレースホルダ(?アイコン)を出しておく。
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
  avg_entertainment: entertainmentIcon,
  fire_count: fireIcon,
  traffic_accident_count: trafficIcon,
  crime_count: crimeIcon,
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

  // 閉じる処理(お祝いウィンドウを開いてから閉じる)は、ボタンとEscが重なっても1回だけ走らせる
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

  // エポック結果画面の表示中は専用BGM (bgm-result.opus) に切り替える
  useEffect(() => {
    void emit("bgm:request", { id: "epoch_result", track: "result", priority: 10 });
    return () => {
      void emit("bgm:release", { id: "epoch_result" });
    };
  }, []);

  // 達成目標の一覧(左下の一覧と達成間近ストリップの元データ)を取得する。
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
  const constructionProjects = report.construction_projects;

  // 人口増減(後-前)。表示の+/-色分けに使用する
  const populationDelta = report.population.after - report.population.before;

  const stats = report.statistics;
  const trendWindow = stats.trend_window ?? 0;

  // 達成目標の一覧に出す目標。進行中(進捗の高い順)→達成→失敗の順に並べる。
  // 未解放の目標と、達成するまで中身を伏せる隠し目標は出さない(達成目標ウィンドウでも条件を見せないため)。
  const listedQuests = useMemo(() => {
    const order: Partial<Record<Quest["status"], number>> = { active: 0, completed: 1, failed: 2 };
    return quests
      .filter((q) => order[q.status] !== undefined && !(q.hidden && q.status !== "completed"))
      .sort((a, b) => (order[a.status] ?? 0) - (order[b.status] ?? 0) || b.progress - a.progress);
  }, [quests]);

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
      {/* 実行後の月の画像。左右いっぱい・高さ=ウィンドウの半分に切り抜いて薄く敷き、下へ向かって地に溶かす */}
      {seasonImage && (
        <img className="epoch-result-backdrop" src={seasonImage} alt="" aria-hidden={true} draggable={false} />
      )}

      {/* ヒーロー: 左に日付、右にスコアを大きく出す */}
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
        {/* クエストストリップ: 達成・失敗・達成間近をチップで横並び表示する */}
        {(completedQuests.length > 0 || failedQuests.length > 0 || nearQuests.length > 0) && (
          <div className="epoch-result-quest-strip">
            {completedQuests.map((q) => (
              <div
                key={`done-${q.id}`}
                className={`epoch-result-quest-chip epoch-result-quest-chip--completed epoch-result-quest-chip--${q.tier}`}
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

        {/* メインエリア: 統計・達成目標(主役) + 建設状況 の2カラム */}
        <div className="epoch-result-main">
          {/* ── 左カラム: 枠を持たないブロックを縦に並べる ── */}
          <div className="epoch-result-col epoch-result-col-stats">
            {/* 統計タイル */}
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

            {/* 達成目標: 進行中の目標の条件と現在値。左カラムの残りの高さを使い、溢れたらスクロールする */}
            <section className="epoch-result-block epoch-result-block--fill">
              <h3 className="epoch-result-section-title">{t("achievements.title")}</h3>
              <div className="epoch-result-scroll">
                <QuestList quests={listedQuests} />
              </div>
            </section>
          </div>{/* /.epoch-result-col-stats */}

          {/* ── 右カラム: 建設状況(道路・公共施設の統合リスト)。見出しはカードの外に置く ── */}
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
 * 達成目標の一覧。進行中の目標は、名前・説明・期限・全体の進捗率と、条件ごとの進捗のバー・現在値・目標値を出す。
 * 達成・失敗した目標は、名前と状態だけの1行にまとめて控えめに出す。
 * 条件の指標名は内部のパスなので出さず、何を目指すかは作者が書いた名前と説明で伝える。
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
          <li key={q.id} className={`epoch-result-quest epoch-result-quest--active epoch-result-quest--${q.tier}`}>
            <div className="epoch-result-quest-head">
              <span className="epoch-result-quest-name">{name}</span>
              {description && <span className="epoch-result-quest-desc" title={description}>{description}</span>}
              {q.deadline_epoch !== null && (
                <span className="epoch-result-quest-deadline">
                  {t("achievements.deadlineEpoch", { epoch: q.deadline_epoch })}
                </span>
              )}
              {/* 切り捨てて、未達成の目標が100%と表示されないようにする */}
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
 * 条件1つ分の行。進捗のバーと「現在値 / 比較 目標値」を並べ、時間派生の条件(前エポック比・連続など)は
 * 小さなラベルで補う。満たしている条件はバーを達成の色にする。
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
 * スコアブロック。当期スコア(大きい数値)と、その構成を1本の帯で示す。
 *
 * 構成は達成目標ウィンドウのウォーターフォールと同じ内訳
 * (生活水準 + 住環境 - 災害ペナルティ = 最終スコア)で、加点(生活水準・住環境)と
 * 減点(災害ペナルティ)を上下2行の帯に分けて描く。2行の尺度は共通(加点合計=100%)。
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
  const env = latest.city_score_env;
  const penalty = latest.city_score_collapse_penalty;
  const gross = qol + env;
  const pct = (v: number) => (gross > 0 ? Math.min(100, Math.max(0, (v / gross) * 100)) : 0);

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

/** 推移の線を描く縦の範囲(viewBox 0〜100)。タイルの下半分に収め、上段の文字にかからないようにする */
const SPARK_TOP = 48;
const SPARK_BOTTOM = 90;

/**
 * 統計タイルの背景に薄く描く推移(`statistics.history` の直近 trend_window エポック)。
 * 値の最小〜最大で上下いっぱいに伸ばして形だけを見せる(横ばいなら中ほどの水平線)。
 * 都市の歴史より古い(null)点は除き、2点未満なら描かない。色は変動方向の矢印と同じ。
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
 * 統計タイル1枚。上段に[アイコン][ラベル][変動矢印]、下段に値を大きく出し、背景に推移を薄く描く。
 * tone は現在値の絶対基準による水準評価で、warn/danger のときタイル全体を着色する。
 * sub を渡すと値の右に補助テキストを添える(人口の増減など)。
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
 * 建設進捗の一覧。タグ付きのプロジェクト(先頭)と、タグの付いていない道路・公共施設の統合リスト。
 * 1件を2段で表示する(プロジェクトはさらに下段へメンバーのアイコン列を足す)。
 * 上段: 種別バッジ・名前・ステータス(after_status のバッジ。プロジェクトは持たない)、
 * 下段: 進捗バー・前後の進捗率・完成の札。
 * リストが空の場合は「対象なし」メッセージを表示する。
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
        // 道路はバリアントのカテゴリ名。マスター未取得などで引けないときだけ総称の「道路」にする
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
 * 進捗バー・前後の進捗率・完成の札の1行。
 * バーは実行後までを淡く、実行前までをさらに淡く重ね、このエポックで進んだ区間を明るく見せる。
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
 * プロジェクトのメンバー(道路・建物)1件を表す小さなアイコン。色は CSS クラス(currentColor)で、
 * 未完了=灰・完了=緑。
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
