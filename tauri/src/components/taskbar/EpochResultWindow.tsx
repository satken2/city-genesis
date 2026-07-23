/**
 * エポック実行結果ウィンドウ
 *
 * エポック実行完了後に別ウィンドウとして開かれ、バックエンドの
 * POST /api/v1/epochs レスポンスの epoch_report フィールドを受け取って表示する。
 *
 * レイアウトはヘッダ + クエストストリップ + 横3分割のメインエリア + フッタ(閉じるボタン):
 * - ヘッダ: 左上に大きく現在のゲーム内日付(実行後)、右に為替レート(前後)
 * - クエストストリップ: このエポックで達成したクエスト(お祝いウィンドウの予告)と、
 *   達成間近(進捗70%以上)のアクティブなクエストの進捗バーを横並びで表示する。
 *   達成間近は GET /api/v1/quests のサーバ計算 progress を使う。
 * - 左カラム: 市民動態(前後人口と出生/死亡/転入/転出のブレイクダウン縦棒グラフ)と
 *             予算執行状況(前エポック計画・実績の縦棒グラフ + 市政府現預金)を縦に並べる
 * - 中央カラム: 統計タイル(3×3)。物価/所得/失業率/医療・栄養・娯楽充足度/火災/交通事故/犯罪
 * - 右カラム: 建設状況(建設中の道路・建設中の公共施設)を縦に並べる
 *
 * 閉じるボタンで親から渡された onClose を呼ぶ。notify="modal" の達成クエストがあれば
 * 親側で引き続き QuestCompleteWindow を表示する(結果→お祝いの連続演出)。
 */
import { ReactNode, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { emit } from "@tauri-apps/api/event";
import { EpochDate } from "../common/EpochDate";
import { STATUS_STYLE_MAP } from "../../local-constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import { buildApiUrl, fetchApi } from "../../utils/api";
import type { BudgetCategoryEntry, Quest, QuestNotification } from "../../types";
import {
  BarChart,
  Bar,
  XAxis,
  YAxis,
  Tooltip,
  ResponsiveContainer,
  CartesianGrid,
  Cell,
  Legend
} from "recharts";
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
};

export type ExchangeRateReport = {
  before: number;
  after: number;
};

/** エポック実行完了時点の都市統計。中央の統計タイル(3×3)に表示する。 */
export type StatisticsReport = {
  price: number;                   // 小売財(GOODS_RETAIL)の平均価格
  income: number;                  // 稼働中世帯の平均収入
  unemployment_rate: number;       // 失業率(0..1比率)
  avg_healthcare: number;          // 世帯平均の医療充足度(0..1)
  avg_nutrition: number;           // 世帯平均の栄養充足度(0..1)
  avg_entertainment: number;       // 世帯平均の娯楽充足度(0..1)
  fire_count: number;              // 実行エポック合計の火災件数
  traffic_accident_count: number;  // 実行エポック合計の交通事故件数
  crime_count: number;             // 実行エポック合計の犯罪件数
};

export type EpochReport = {
  construction_progress: ConstructionProgressItem[];
  population: PopulationReport;
  exchange_rate: ExchangeRateReport;
  statistics: StatisticsReport;
};

export type BudgetHistoryEntry = {
  epoch: number;
  revenues: Record<string, number>;
};

export type BudgetData = {
  budgets: Record<string, number>;
  prev_budgets: Record<string, number>;
  total_expense: Record<string, number>;
  total_revenue: Record<string, number>;
  budget_tiers?: Record<string, number>;
  history?: BudgetHistoryEntry[];
};

type CityAgentData = {
  economic_agent: {
    reserves: number;
  };
};

type CityData = {
  city: {
    city_agent_id: number;
  };
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

export function EpochResultWindow({
  report,
  epochCount,
  currentDate,
  onClose,
  apiBaseUrl,
  completedQuests = [],
  failedQuests = [],
}: EpochResultWindowProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const [budgetData, setBudgetData] = useState<BudgetData | null>(null);
  const [cityReserves, setCityReserves] = useState<number | null>(null);
  const [nearQuests, setNearQuests] = useState<Quest[]>([]);

  // エポック結果画面の表示中は専用BGM (bgm-result.mp3) に切り替える
  useEffect(() => {
    void emit("bgm:request", { id: "epoch_result", track: "result", priority: 10 });
    return () => {
      void emit("bgm:release", { id: "epoch_result" });
    };
  }, []);

  useEffect(() => {
    const baseUrl = apiBaseUrl.replace(/\/+$/, "");
    fetch(`${baseUrl}/api/v1/city/budgets`)
      .then((res) => res.json())
      .then((data) => setBudgetData(data))
      .catch((err) => console.error("failed to fetch budget data", err));
  }, [apiBaseUrl]);

  // 市政府の現預金(reserves)を取得する。
  // /api/v1/city から city_agent_id を取得し、
  // /api/v1/agents/{id} の economic_agent.reserves を読み取る。
  useEffect(() => {
    const controller = new AbortController();
    (async () => {
      try {
        const cityRes = await fetchApi<CityData>(
          buildApiUrl(apiBaseUrl, "/api/v1/city"),
          { signal: controller.signal },
        );
        const agentData = await fetchApi<CityAgentData>(
          buildApiUrl(apiBaseUrl, `/api/v1/agents/${cityRes.city.city_agent_id}`),
          { signal: controller.signal },
        );
        setCityReserves(agentData.economic_agent.reserves);
      } catch (err) {
        if (!controller.signal.aborted) {
          console.error("failed to fetch city reserves", err);
        }
      }
    })();
    return () => controller.abort();
  }, [apiBaseUrl]);

  // 達成間近(進捗70%以上・アクティブ・隠しでない)のクエストを取得する。
  // 「あと少しで達成」を見せて次のエポックの動機付けにする。
  useEffect(() => {
    const controller = new AbortController();
    (async () => {
      try {
        const data = await fetchApi<{ quests: Quest[] }>(
          buildApiUrl(apiBaseUrl, "/api/v1/quests"),
          { signal: controller.signal },
        );
        const completedIds = new Set(completedQuests.map((q) => q.id));
        setNearQuests(
          data.quests
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
            .slice(0, 4),
        );
      } catch (err) {
        if (!controller.signal.aborted) {
          console.error("failed to fetch quests for near-completion strip", err);
        }
      }
    })();
    return () => controller.abort();
    // completedQuests は epoch_confirm から一度だけ渡される静的な配列のため依存に含めない
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [apiBaseUrl]);

  const categories = useMemo(() => {
    if (!masterData?.budget_categories) return [];
    return Object.entries(masterData.budget_categories).map(([id, entry]) => ({
      id: Number(id),
      display_name: (entry as BudgetCategoryEntry).display_name,
    }));
  }, [masterData?.budget_categories]);

  // entity_type→id 昇順でソートされたリストを道路/建物で分ける
  const paths = report.construction_progress.filter((x) => x.entity_type === "path");
  const buildings = report.construction_progress.filter((x) => x.entity_type === "agent");

  // 人口増減(後-前)。表示の+/-色分けに使用する
  const populationDelta = report.population.after - report.population.before;

  // 為替レート増減(後-前)。通貨安(レート上昇)で正、通貨高(レート下落)で負。
  const fxDelta = report.exchange_rate.after - report.exchange_rate.before;

  // 人口動態のブレイクダウンデータ (出生, 死亡, 転入, 転出, 増減値)
  const popChartData = useMemo(() => {
    return [
      {
        name: t("epochResult.births"),
        value: report.population.births,
        fill: "#6cd47f",
      },
      {
        name: t("epochResult.deaths"),
        value: -report.population.deaths,
        fill: "#e87878",
      },
      {
        name: t("epochResult.immigrations"),
        value: report.population.immigrations,
        fill: "#6cd47f",
      },
      {
        name: t("epochResult.emigrations"),
        value: -report.population.emigrations,
        fill: "#e87878",
      },
      {
        name: "実質増減",
        value: populationDelta,
        fill: populationDelta >= 0 ? "#4a9eff" : "#e87878",
      },
    ];
  }, [report.population, populationDelta, t]);

  // 予算執行状況データ (計画と実績)
  const budgetChartData = useMemo(() => {
    if (!budgetData || categories.length === 0) return [];
    return categories
      .filter((cat) => cat.id !== 10) // 貯蓄（ID: 10）は支出カテゴリではないため除外
      .sort((a, b) => a.id - b.id)
      .map((cat) => {
        const key = String(cat.id);
        const plan = budgetData.prev_budgets[key] ?? 0;
        const actual = budgetData.total_expense[key] ?? 0;
        return {
          name: t(`budget.category.${cat.id}`, { defaultValue: cat.display_name }),
          plan: Math.round(plan),
          actual: Math.round(actual),
        };
      });
  }, [budgetData, categories, t]);

  // 統計タイル(3×3)の表示データ。行順に: 物価/所得/失業率, 医療/栄養/娯楽, 火災/交通事故/犯罪。
  // 事故・災害系(火災/交通事故/犯罪)は件数が非0のとき警告色(赤)で強調する。
  const stats = report.statistics;
  const statTiles = useMemo(() => {
    const pct = (v: number) => `${Math.round(v * 100)}%`;
    return [
      {
        label: t("epochResult.statPrice"),
        value: stats.price.toLocaleString(undefined, { maximumFractionDigits: 1 }),
      },
      {
        label: t("epochResult.statIncome"),
        value: t("common.currency", { value: Math.round(stats.income) }),
      },
      {
        label: t("epochResult.statUnemployment"),
        value: `${(stats.unemployment_rate * 100).toFixed(1)}%`,
      },
      { label: t("epochResult.statHealthcare"), value: pct(stats.avg_healthcare) },
      { label: t("epochResult.statNutrition"), value: pct(stats.avg_nutrition) },
      { label: t("epochResult.statEntertainment"), value: pct(stats.avg_entertainment) },
      {
        label: t("epochResult.statFire"),
        value: stats.fire_count.toLocaleString(),
        tone: stats.fire_count > 0 ? "warn" : undefined,
      },
      {
        label: t("epochResult.statTraffic"),
        value: stats.traffic_accident_count.toLocaleString(),
        tone: stats.traffic_accident_count > 0 ? "warn" : undefined,
      },
      {
        label: t("epochResult.statCrime"),
        value: stats.crime_count.toLocaleString(),
        tone: stats.crime_count > 0 ? "warn" : undefined,
      },
      {
        // 為替レート。値の下に前エポックからの変動を色付きで併記する。
        // 通貨安(レート上昇)=赤、通貨高(レート下落)=緑(旧ヘッダ表示の配色を踏襲)。
        label: t("epochResult.exchangeRate"),
        value: report.exchange_rate.after.toFixed(4),
        sub: `${fxDelta >= 0 ? "+" : ""}${fxDelta.toFixed(4)}`,
        subTone: fxDelta > 0 ? "up" : fxDelta < 0 ? "down" : undefined,
      },
    ] as {
      label: string;
      value: string;
      tone?: "warn";
      sub?: string;
      subTone?: "up" | "down";
    }[];
  }, [stats, report.exchange_rate.after, fxDelta, t]);

  return (
    <div className="epoch-result-root">
      {/* ヘッダ: 左上に大きく日付(為替レートは統計タイルへ移動) */}
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

      {/* クエストストリップ: 達成(お祝いの予告)・失敗・達成間近をチップで横並び表示する */}
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

      {/* メインエリア: 左(人口/予算) + 中央(統計) + 右(建設) の3カラム */}
      <div className="epoch-result-main">
        {/* ── 左カラム: 市民動態 + 予算執行状況 ── */}
        <div className="epoch-result-col epoch-result-col-left">
          {/* 市民動態 */}
          <section className="epoch-result-section epoch-result-section-pop">
            <h3 className="epoch-result-section-title">{t("epochResult.sectionPopulation")}</h3>
            <div className="epoch-result-pop-summary">
              <span className="epoch-result-pop-summary-label">人口推移:</span>
              <span className="epoch-result-pop-summary-values">
                {report.population.before.toLocaleString()} ➔ {report.population.after.toLocaleString()}
                <span
                  className={
                    populationDelta >= 0
                      ? "epoch-result-pop-delta-plus"
                      : "epoch-result-pop-delta-minus"
                  }
                >
                  ({populationDelta >= 0 ? "+" : ""}{populationDelta.toLocaleString()})
                </span>
              </span>
            </div>
            {/* 移民の応募数（世帯）と許可数（人数）。審査前の応募と実際にスポーンした人数を比較できる。
                移民が発生しなかったエポックでは両方0になるため、非表示にする。 */}
            {(report.population.immigration_applicants ?? 0) > 0 || report.population.immigrations > 0 ? (
              <div className="epoch-result-pop-summary">
                <span className="epoch-result-pop-summary-label">{t("epochResult.immigrationStatus")}:</span>
                <span className="epoch-result-pop-summary-values">
                  {t("epochResult.immigrationApplicants", { households: (report.population.immigration_applicants ?? 0).toLocaleString() })}
                  {" / "}
                  {t("epochResult.immigrationApproved", { people: report.population.immigrations.toLocaleString() })}
                </span>
              </div>
            ) : null}
            <div className="epoch-result-chart-container">
              <ResponsiveContainer width="100%" height="100%">
                <BarChart data={popChartData} margin={{ top: 5, right: 5, left: -20, bottom: 0 }}>
                  <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.05)" />
                  <XAxis dataKey="name" stroke="#8ba3bb" fontSize={10} tickLine={false} />
                  <YAxis stroke="#8ba3bb" fontSize={10} tickLine={false} />
                  <Tooltip
                    contentStyle={{
                      background: "#1a2230",
                      border: "1px solid rgba(255,255,255,0.1)",
                      borderRadius: "4px",
                    }}
                    labelStyle={{ color: "#fff" }}
                  />
                  <Bar dataKey="value" radius={[3, 3, 0, 0]}>
                    {popChartData.map((entry, index) => (
                      <Cell key={`cell-${index}`} fill={entry.fill} />
                    ))}
                  </Bar>
                </BarChart>
              </ResponsiveContainer>
            </div>
          </section>

          {/* 予算執行状況 */}
          <section className="epoch-result-section epoch-result-section-budget">
            <h3 className="epoch-result-section-title">予算執行状況 (前回計画 vs 実績)</h3>
            {budgetData ? (
              <div className="epoch-result-chart-container">
                <ResponsiveContainer width="100%" height="100%">
                  <BarChart data={budgetChartData} margin={{ top: 5, right: 5, left: -10, bottom: 0 }}>
                    <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.05)" />
                    <XAxis dataKey="name" stroke="#8ba3bb" fontSize={9} tickLine={false} />
                    <YAxis stroke="#8ba3bb" fontSize={9} tickLine={false} />
                    <Tooltip
                      contentStyle={{
                        background: "#1a2230",
                        border: "1px solid rgba(255,255,255,0.1)",
                        borderRadius: "4px",
                      }}
                      labelStyle={{ color: "#fff" }}
                      formatter={(value: any) => [Math.round(value).toLocaleString(), ""]}
                    />
                    <Legend
                      verticalAlign="top"
                      height={20}
                      iconSize={8}
                      wrapperStyle={{ fontSize: 9, marginTop: -5 }}
                    />
                    <Bar dataKey="plan" name="予算計画" fill="rgba(74, 158, 255, 0.4)" radius={[2, 2, 0, 0]} />
                    <Bar dataKey="actual" name="支出実績" fill="#4a9eff" radius={[2, 2, 0, 0]} />
                  </BarChart>
                </ResponsiveContainer>
              </div>
            ) : (
              <p className="epoch-result-empty">{t("common.loading")}</p>
            )}
            {/* 市政府の現預金(残金)。予算執行状況グラフの下に表示する */}
            <div className="epoch-result-reserves">
              <span className="epoch-result-reserves-label">{t("epochResult.cityReserves")}</span>
              <span className="epoch-result-reserves-value">
                {cityReserves != null
                  ? t("common.currency", { value: Math.round(cityReserves) })
                  : t("common.loading")}
              </span>
            </div>
          </section>
        </div>{/* /.epoch-result-col-left */}

        {/* ── 中央カラム: 統計タイル(3×3) ── */}
        <div className="epoch-result-col epoch-result-col-center">
          <section className="epoch-result-section epoch-result-section-stats">
            <h3 className="epoch-result-section-title">{t("epochResult.sectionStatistics")}</h3>
            <div className="epoch-result-stat-grid">
              {statTiles.map((tile) => (
                <StatTile
                  key={tile.label}
                  label={tile.label}
                  value={tile.value}
                  tone={tile.tone}
                  sub={tile.sub}
                  subTone={tile.subTone}
                />
              ))}
            </div>
          </section>
        </div>{/* /.epoch-result-col-center */}

        {/* ── 右カラム: 建設状況(道路 + 公共施設) ── */}
        <div className="epoch-result-col epoch-result-col-right">
          <section className="epoch-result-section epoch-result-section-paths">
            <h3 className="epoch-result-section-title">{t("epochResult.sectionPaths")}</h3>
            <div className="epoch-result-construction-scroll">
              <ConstructionTable items={paths} />
            </div>
          </section>

          <section className="epoch-result-section epoch-result-section-buildings">
            <h3 className="epoch-result-section-title">{t("epochResult.sectionBuildings")}</h3>
            <div className="epoch-result-construction-scroll">
              <ConstructionTable items={buildings} />
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

/**
 * 統計タイル1枚。ラベル(小)+値(大)を中央寄せで表示する。
 * tone="warn" のとき値を警告色(赤)で強調する(火災・交通事故・犯罪が非0のとき)。
 * sub を渡すと値の下に補助テキストを表示する(為替レートの変動幅など)。
 * subTone="up"(赤)/"down"(緑)で補助テキストに色を付ける。
 */
function StatTile({
  label,
  value,
  tone,
  sub,
  subTone,
}: {
  label: string;
  value: string;
  tone?: "warn";
  sub?: string;
  subTone?: "up" | "down";
}): ReactNode {
  const subClass =
    subTone === "up"
      ? "epoch-result-stat-sub epoch-result-stat-sub-up"
      : subTone === "down"
        ? "epoch-result-stat-sub epoch-result-stat-sub-down"
        : "epoch-result-stat-sub";
  return (
    <div className={tone === "warn" ? "epoch-result-stat-tile epoch-result-stat-tile-warn" : "epoch-result-stat-tile"}>
      <span className="epoch-result-stat-label">{label}</span>
      <span className="epoch-result-stat-value">{value}</span>
      {sub != null ? <span className={subClass}>{sub}</span> : null}
    </div>
  );
}

/**
 * 建設進捗のテーブル表示。
 * 列: 名前 / ステータス(after_status でバッジ表示) / 進捗(バー+テキスト)。
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
          return (
            <tr key={`${item.entity_type}-${item.id}`} className="epoch-result-tr">
              <td className="epoch-result-td epoch-result-td-name" title={displayName}>
                {displayName}
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
