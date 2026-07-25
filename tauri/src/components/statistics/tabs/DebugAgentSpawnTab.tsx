/**
 * 経済主体スポーン デバッグタブコンポーネント（開発者専用）
 *
 * 統計ウィンドウの「デバッグ（主体）」タブの中身。バックエンドの
 * GET /api/v1/stats/spawn_debug の agent_spawn 履歴（SpawnPhase が毎エポック
 * 記録する阻害要因カウンタと評価メトリクス）を可視化する。このタブは
 * バックエンドの gd3.debug_execution が true（開発実行時）のときのみ表示される
 * （表示制御は CityStatisticsWindow 側で行う）。
 *
 * レイアウトは左3/4＋右1/4の2カラム構成。
 * - 左上: 阻害要因の内訳（積み上げ棒グラフ）。エポックごとに「何がスポーンを
 *   阻害したか」（需要不足・価格競争力・家賃・原材料・労働力など）のカウントを
 *   積み上げ、候補数・実現数を折れ線で重ねる。
 * - 左下: スポーン評価メトリクスの推移（折れ線グラフ）。選択業種の潜在需要
 *   （国内/輸出）・実効需要・最低稼働量を左軸、競争力係数・粗利益率を右軸に描く。
 *   需要が最低稼働量を上回り、競争力・利益率が上がっていく様子から
 *   「その業種があとどれぐらいで参入しそうか」を観測できる。
 * - 右: 最新エポックの業種別スコア表。行クリックで左のグラフの業種を切り替える。
 *
 * 業種セレクタは左の上下チャートと右表の選択状態を共有する。
 * 「全業種（合算）」選択時、メトリクスは需要系の合算のみ表示する
 * （競争力・利益率は業種固有の値であり、合算に意味がないため）。
 */

import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  Bar,
  ComposedChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
  ResponsiveContainer,
} from "recharts";
import { useEpochCachedStats } from "../useEpochCache";
import { useMasterData } from "../../../contexts/MasterDataContext";
import {
  SpawnDebugStats,
  AgentSpawnMetrics,
  SPAWN_DEBUG_ENDPOINT,
  CHART_MARGIN,
  AXIS_TICK,
  TOOLTIP_STYLE,
  formatCompact,
  formatCoefficient,
} from "./spawnDebugShared";
import "./DebugSpawnTab.css";

/** 経済主体スポーンのバリアント選択値。"all" は全業種の合算を表す。 */
type AgentVariantSelection = number | "all";

/**
 * 阻害要因カウンタの表示定義。
 * キーはバックエンド SpawnPhase._debug_count が使うカウンタ名と一致させる。
 * 積み上げ棒グラフの積み順・色・i18nラベルをここで一元管理する。
 */
const BLOCKER_SERIES: Array<{ key: string; labelKey: string; color: string }> = [
  { key: "no_demand", labelKey: "statistics.debugSpawnCounterNoDemand", color: "#64748b" },
  { key: "competitiveness", labelKey: "statistics.debugSpawnCounterCompetitiveness", color: "#f97316" },
  { key: "rent_negative", labelKey: "statistics.debugSpawnCounterRentNegative", color: "#ef4444" },
  { key: "rent_below_market", labelKey: "statistics.debugSpawnCounterRentBelowMarket", color: "#f59e0b" },
  { key: "materials", labelKey: "statistics.debugSpawnCounterMaterials", color: "#a16207" },
  { key: "materials_capacity", labelKey: "statistics.debugSpawnCounterMaterialsCapacity", color: "#854d0e" },
  { key: "capital", labelKey: "statistics.debugSpawnCounterCapital", color: "#0f766e" },
  { key: "high_skill_labor", labelKey: "statistics.debugSpawnCounterHighSkillLabor", color: "#8b5cf6" },
  { key: "general_labor", labelKey: "statistics.debugSpawnCounterGeneralLabor", color: "#c084fc" },
  { key: "below_min_qty", labelKey: "statistics.debugSpawnCounterBelowMinQty", color: "#0ea5e9" },
  { key: "dedicated_pending", labelKey: "statistics.debugSpawnCounterDedicatedPending", color: "#334155" },
];

/**
 * 評価メトリクスの折れ線の表示定義。
 * キーはバックエンド SpawnPhase._debug_metric_add / _debug_metric_max の
 * 記録キーと一致させる。axis="amount" は左軸（数量）、"coefficient" は右軸（係数）。
 * summable はバリアント合算（"all" 選択時）に意味がある系列のみ true。
 */
const METRIC_SERIES: Array<{
  key: keyof AgentSpawnMetrics;
  labelKey: string;
  color: string;
  axis: "amount" | "coefficient";
  summable: boolean;
  dash?: string;
}> = [
  { key: "demand_domestic", labelKey: "statistics.debugSpawnMetricDemandDomestic", color: "#4ea1ff", axis: "amount", summable: true },
  { key: "demand_export", labelKey: "statistics.debugSpawnMetricDemandExport", color: "#a78bfa", axis: "amount", summable: true },
  { key: "effective_demand", labelKey: "statistics.debugSpawnMetricEffectiveDemand", color: "#10b981", axis: "amount", summable: true },
  { key: "min_qty", labelKey: "statistics.debugSpawnMetricMinQty", color: "#94a3b8", axis: "amount", summable: false, dash: "5 4" },
  { key: "best_domestic_competitiveness", labelKey: "statistics.debugSpawnMetricDomesticCompetitiveness", color: "#f59e0b", axis: "coefficient", summable: false },
  { key: "best_export_competitiveness", labelKey: "statistics.debugSpawnMetricExportCompetitiveness", color: "#f97316", axis: "coefficient", summable: false },
  { key: "best_profit_rate", labelKey: "statistics.debugSpawnMetricProfitRate", color: "#ef4444", axis: "coefficient", summable: false },
];

// 折れ線系列の色
const COLOR_CANDIDATES = "#94a3b8"; // 候補数(点線)
const COLOR_REALIZED = "#10b981";   // 実現数(実線)

/** 最新エポック表の1行分 */
type LatestAgentRow = {
  variantId: number;
  demand: number | null;
  competitiveness: number | null;
  profitRate: number | null;
  spawned: number;
};

export function DebugAgentSpawnTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const { data, loading, error } = useEpochCachedStats<SpawnDebugStats>(
    apiBaseUrl,
    SPAWN_DEBUG_ENDPOINT
  );

  /** バリアントIDから表示名を解決する（マスターに無ければIDのまま表示） */
  const variantName = (variantId: number): string =>
    masterData?.agents?.[variantId]?.display_name ?? `#${variantId}`;

  // ---- 履歴に登場する全業種ID（カウンタ総イベント数の降順） ----
  // 阻害・成功を問わずカウンタ合計が多い業種ほど「動きのある業種」なので上位に並べる。
  // メトリクスのみ記録された業種もセレクタから漏れないように合流させる。
  const agentVariantIds = useMemo(() => {
    if (!data) return [];
    const totals = new Map<number, number>();
    for (const rec of data.agent_spawn) {
      for (const [id, counters] of Object.entries(rec.variants ?? {})) {
        const sum = Object.values(counters).reduce((a, b) => a + b, 0);
        totals.set(Number(id), (totals.get(Number(id)) ?? 0) + sum);
      }
      for (const id of Object.keys(rec.metrics ?? {})) {
        if (!totals.has(Number(id))) totals.set(Number(id), 0);
      }
    }
    return Array.from(totals.keys()).sort(
      (a, b) => (totals.get(b) ?? 0) - (totals.get(a) ?? 0)
    );
  }, [data]);

  // 業種選択。既定は全業種の合算（"all"）。左の上下チャートと右表で共有する。
  const [selectedVariant, setSelectedVariant] =
    useState<AgentVariantSelection>("all");

  // ---- 左上: 阻害要因の積み上げチャート用データ ----
  // "all" のときは全業種を合算し、業種選択時はその業種のカウンタのみを集計する。
  const blockerChartData = useMemo(() => {
    if (!data) return [];
    return data.agent_spawn.map((rec) => {
      const variants = rec.variants ?? {};
      const entries =
        selectedVariant === "all"
          ? Object.values(variants)
          : [variants[String(selectedVariant)] ?? {}];

      const row: Record<string, number> = { date: rec.date, candidates: 0, realized: 0 };
      for (const s of BLOCKER_SERIES) row[s.key] = 0;

      for (const c of entries) {
        row.candidates += c["candidates"] ?? 0;
        row.realized +=
          (c["spawned"] ?? 0) + (c["reused_paused"] ?? 0) + (c["dedicated_registered"] ?? 0);
        for (const s of BLOCKER_SERIES) {
          row[s.key] += c[s.key] ?? 0;
        }
      }
      return row;
    });
  }, [data, selectedVariant]);

  // 選択中の対象にカウンタが1件でもあるか。無ければ「データなし」を表示する。
  const hasBlockerData = useMemo(
    () =>
      blockerChartData.some((row) =>
        Object.entries(row).some(([k, v]) => k !== "date" && v !== 0)
      ),
    [blockerChartData]
  );

  // ---- 左下: 評価メトリクスの折れ線チャート用データ ----
  // 業種選択時はその業種の全メトリクス、"all" のときは合算に意味のある
  // 需要系（summable=true）のみを全業種合算して描く。
  // 値が記録されていないエポックは null にして線を欠落させる（connectNulls で補間）。
  const visibleMetricSeries = useMemo(
    () =>
      selectedVariant === "all"
        ? METRIC_SERIES.filter((s) => s.summable)
        : METRIC_SERIES,
    [selectedVariant]
  );

  const metricChartData = useMemo(() => {
    if (!data) return [];
    return data.agent_spawn.map((rec) => {
      const metrics = rec.metrics ?? {};
      const row: Record<string, number | null> = { date: rec.date };
      if (selectedVariant === "all") {
        for (const s of METRIC_SERIES) {
          if (!s.summable) continue;
          let sum: number | null = null;
          for (const m of Object.values(metrics)) {
            const v = m[s.key];
            if (v !== undefined) sum = (sum ?? 0) + v;
          }
          row[s.key] = sum;
        }
      } else {
        const m = metrics[String(selectedVariant)] ?? {};
        for (const s of METRIC_SERIES) {
          row[s.key] = m[s.key] ?? null;
        }
      }
      return row;
    });
  }, [data, selectedVariant]);

  const hasMetricData = useMemo(
    () =>
      metricChartData.some((row) =>
        Object.entries(row).some(([k, v]) => k !== "date" && v !== null)
      ),
    [metricChartData]
  );

  // ---- 右: 最新エポックの業種別スコア表 ----
  // メトリクス（需要・競争力・利益率）とカウンタ（実現数）を1行にまとめ、
  // 利益率降順（未記録は末尾）に並べる。
  const latestRecord = data?.agent_spawn[data.agent_spawn.length - 1] ?? null;
  const latestRows = useMemo((): LatestAgentRow[] => {
    if (!latestRecord) return [];
    const metrics = latestRecord.metrics ?? {};
    const counters = latestRecord.variants ?? {};
    const ids = new Set<number>([
      ...Object.keys(metrics).map(Number),
      ...Object.keys(counters).map(Number),
    ]);
    const rows = Array.from(ids).map((variantId): LatestAgentRow => {
      const m = metrics[String(variantId)] ?? {};
      const c = counters[String(variantId)] ?? {};
      const domestic = m.demand_domestic;
      const exportDemand = m.demand_export;
      const demand =
        domestic === undefined && exportDemand === undefined
          ? null
          : (domestic ?? 0) + (exportDemand ?? 0);
      const compCandidates = [
        m.best_domestic_competitiveness,
        m.best_export_competitiveness,
      ].filter((v): v is number => v !== undefined);
      return {
        variantId,
        demand,
        competitiveness: compCandidates.length > 0 ? Math.max(...compCandidates) : null,
        profitRate: m.best_profit_rate ?? null,
        spawned:
          (c["spawned"] ?? 0) + (c["reused_paused"] ?? 0) + (c["dedicated_registered"] ?? 0),
      };
    });
    rows.sort((a, b) => (b.profitRate ?? -Infinity) - (a.profitRate ?? -Infinity));
    return rows;
  }, [latestRecord]);

  if (loading) {
    return <div className="debug-spawn-tab__loading">{t("common.loading")}</div>;
  }

  if (error || !data) {
    return (
      <div className="debug-spawn-tab__error">
        {t("common.fetchError")}{error ? `: ${error}` : ""}
      </div>
    );
  }

  return (
    <div className="debug-spawn-tab">
      <h2 className="debug-spawn-tab__title">{t("statistics.debugAgentSpawnTitle")}</h2>

      <div className="debug-spawn-layout">
        <div className="debug-spawn-layout__main">
          {/* 左上: 阻害要因の積み上げ棒グラフ */}
          <section className="debug-spawn-card">
            <div className="debug-spawn-card__header">
              <h3 className="debug-spawn-card__title">
                {t("statistics.debugSpawnAgentSection")}
              </h3>
              <div className="debug-spawn-control">
                <label className="debug-spawn-control__label">
                  {t("statistics.debugSpawnAgentVariantSelect")}
                </label>
                <select
                  className="debug-spawn-control__select"
                  value={selectedVariant === "all" ? "all" : String(selectedVariant)}
                  onChange={(e) =>
                    setSelectedVariant(
                      e.target.value === "all" ? "all" : Number(e.target.value)
                    )
                  }
                >
                  <option value="all">{t("statistics.debugSpawnAllVariants")}</option>
                  {agentVariantIds.map((id) => (
                    <option key={id} value={id}>
                      {variantName(id)}
                    </option>
                  ))}
                </select>
              </div>
            </div>
            {blockerChartData.length > 0 && hasBlockerData ? (
              <div className="debug-spawn-card__chart">
                <ResponsiveContainer width="100%" height="100%">
                  <ComposedChart data={blockerChartData} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
                    <XAxis dataKey="date" tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} />
                    <YAxis tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} width={40} allowDecimals={false} />
                    <Tooltip contentStyle={TOOLTIP_STYLE} />
                    <Legend wrapperStyle={{ fontSize: "11px" }} />
                    {BLOCKER_SERIES.map((s) => (
                      <Bar
                        key={s.key}
                        dataKey={s.key}
                        name={t(s.labelKey)}
                        stackId="blockers"
                        fill={s.color}
                        isAnimationActive={false}
                      />
                    ))}
                    <Line
                      type="monotone"
                      dataKey="candidates"
                      name={t("statistics.debugSpawnCandidates")}
                      stroke={COLOR_CANDIDATES}
                      strokeWidth={2}
                      strokeDasharray="5 4"
                      dot={false}
                      isAnimationActive={false}
                    />
                    <Line
                      type="monotone"
                      dataKey="realized"
                      name={t("statistics.debugSpawnRealized")}
                      stroke={COLOR_REALIZED}
                      strokeWidth={2}
                      dot={false}
                      isAnimationActive={false}
                    />
                  </ComposedChart>
                </ResponsiveContainer>
              </div>
            ) : (
              <div className="debug-spawn-card__no-data">{t("statistics.debugSpawnNoData")}</div>
            )}
          </section>

          {/* 左下: 評価メトリクスの折れ線グラフ */}
          <section className="debug-spawn-card">
            <h3 className="debug-spawn-card__title">
              {t("statistics.debugSpawnMetricsSection")}
            </h3>
            {hasMetricData ? (
              <div className="debug-spawn-card__chart">
                <ResponsiveContainer width="100%" height="100%">
                  <ComposedChart data={metricChartData} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
                    <XAxis dataKey="date" tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} />
                    {/* 左軸: 需要量（潜在/実効需要・最低稼働量） */}
                    <YAxis
                      yAxisId="amount"
                      tick={AXIS_TICK}
                      tickLine={false}
                      axisLine={{ stroke: "#253246" }}
                      width={52}
                      tickFormatter={formatCompact}
                      domain={["auto", "auto"]}
                    />
                    {/* 右軸: 係数（競争力0〜1・粗利益率） */}
                    {selectedVariant !== "all" && (
                      <YAxis
                        yAxisId="coefficient"
                        orientation="right"
                        tick={AXIS_TICK}
                        tickLine={false}
                        axisLine={{ stroke: "#253246" }}
                        width={40}
                      />
                    )}
                    <Tooltip
                      contentStyle={TOOLTIP_STYLE}
                      formatter={(value: any, _name: any, item: any) => {
                        const series = METRIC_SERIES.find((s) => s.key === item?.dataKey);
                        return series?.axis === "coefficient"
                          ? formatCoefficient(Number(value))
                          : formatCompact(Number(value));
                      }}
                    />
                    <Legend wrapperStyle={{ fontSize: "11px" }} />
                    {visibleMetricSeries.map((s) => (
                      <Line
                        key={s.key}
                        yAxisId={s.axis}
                        type="monotone"
                        dataKey={s.key}
                        name={t(s.labelKey)}
                        stroke={s.color}
                        strokeWidth={2}
                        strokeDasharray={s.dash}
                        dot={false}
                        connectNulls
                        isAnimationActive={false}
                      />
                    ))}
                  </ComposedChart>
                </ResponsiveContainer>
              </div>
            ) : (
              <div className="debug-spawn-card__no-data">{t("statistics.debugSpawnNoData")}</div>
            )}
          </section>
        </div>

        {/* 右: 最新エポックの業種別スコア表（行クリックで業種選択） */}
        <div className="debug-spawn-layout__side">
          <section className="debug-spawn-card debug-spawn-card--fill">
            <h3 className="debug-spawn-card__title">
              {t("statistics.debugSpawnAgentLatestTable")}
            </h3>
            {latestRows.length > 0 ? (
              <div className="debug-spawn-table-scroll debug-spawn-table-scroll--fill">
                <table className="debug-spawn-table debug-spawn-table--clickable">
                  <thead>
                    <tr>
                      <th>{t("statistics.debugSpawnColVariant")}</th>
                      <th>{t("statistics.debugSpawnColDemand")}</th>
                      <th>{t("statistics.debugSpawnColCompetitiveness")}</th>
                      <th>{t("statistics.debugSpawnColProfitRate")}</th>
                      <th>{t("statistics.debugSpawnColSpawned")}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {latestRows.map((row) => (
                      <tr
                        key={row.variantId}
                        className={[
                          row.spawned > 0 && "debug-spawn-table__row--positive",
                          row.variantId === selectedVariant &&
                            "debug-spawn-table__row--selected",
                        ]
                          .filter(Boolean)
                          .join(" ")}
                        onClick={() => setSelectedVariant(row.variantId)}
                      >
                        <td>{variantName(row.variantId)}</td>
                        <td className="debug-spawn-table__num">
                          {row.demand !== null ? formatCompact(row.demand) : "-"}
                        </td>
                        <td className="debug-spawn-table__num">
                          {row.competitiveness !== null
                            ? formatCoefficient(row.competitiveness)
                            : "-"}
                        </td>
                        <td className="debug-spawn-table__num">
                          {row.profitRate !== null
                            ? formatCoefficient(row.profitRate)
                            : "-"}
                        </td>
                        <td className="debug-spawn-table__num">{row.spawned}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : (
              <div className="debug-spawn-card__no-data">{t("statistics.debugSpawnNoData")}</div>
            )}
          </section>
        </div>
      </div>
    </div>
  );
}
