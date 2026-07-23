/**
 * 建物スポーン デバッグタブコンポーネント（開発者専用）
 *
 * 統計ウィンドウの「デバッグ（建物）」タブの中身。バックエンドの
 * GET /api/v1/stats/spawn_debug の construction 履歴（ConstructionPhase が
 * 毎エポック記録するバリアント別の最高最終スコアとその内訳）を可視化する。
 * このタブはバックエンドの gd3.debug_execution が true（開発実行時）のときのみ
 * 表示される（表示制御は CityStatisticsWindow 側で行う）。
 *
 * レイアウトは左3/4＋右1/4の2カラム構成。
 * - 左: 選択バリアントの最高最終スコア（NPV − 建設コスト − 買収・解体 − 土地代）の
 *   推移を折れ線で表示する。スコアが負から 0 に近づいていく様子から
 *   「あと少しで建つ」状態が観測できる。あわせて最良パターンの NPV（収益側）と
 *   コスト計（建設+土地+買収解体）も重ねて描く。NPV とコストの交差から
 *   「家賃があとどれだけ上がれば/コストが下がれば建つか」が読み取れ、
 *   cost_ratio や用途別家賃倍率などのマスターパラメータ調整の材料になる。
 * - 右: 最新エポックのバリアント別スコア表。行クリックで左のグラフの
 *   バリアントを切り替える。
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
  ReferenceLine,
  ResponsiveContainer,
} from "recharts";
import { useEpochCachedStats } from "../useEpochCache";
import { useMasterData } from "../../../contexts/MasterDataContext";
import {
  SpawnDebugStats,
  ConstructionVariantEntry,
  SPAWN_DEBUG_ENDPOINT,
  CHART_MARGIN,
  AXIS_TICK,
  TOOLTIP_STYLE,
  formatCompact,
} from "./spawnDebugShared";
import "./DebugSpawnTab.css";

// 折れ線系列の色
const COLOR_BEST_SCORE = "#4ea1ff"; // 最高最終スコア
const COLOR_NPV = "#10b981";        // 最良パターンのNPV（収益側）
const COLOR_COST = "#ef4444";       // 最良パターンのコスト計

/**
 * 最良パターンのコスト計（建設+土地+買収解体）を求める。
 * 内訳が未記録（記録拡張前の古いレコード）の場合は null を返し、
 * グラフ上は線を欠落させる。
 */
function totalCost(entry: ConstructionVariantEntry): number | null {
  if (entry.best_construction_cost === undefined) return null;
  return (
    entry.best_construction_cost +
    (entry.best_land_cost ?? 0) +
    (entry.best_extra_cost ?? 0)
  );
}

export function DebugBuildingSpawnTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const { data, loading, error } = useEpochCachedStats<SpawnDebugStats>(
    apiBaseUrl,
    SPAWN_DEBUG_ENDPOINT
  );

  /** バリアントIDから表示名を解決する（マスターに無ければIDのまま表示） */
  const variantName = (variantId: number): string =>
    masterData?.agents?.[variantId]?.display_name ?? `#${variantId}`;

  // ---- 履歴に登場する全バリアントID（最新エポックのスコア降順） ----
  const variantIds = useMemo(() => {
    if (!data || data.construction.length === 0) return [];
    const ids = new Set<number>();
    for (const rec of data.construction) {
      for (const key of Object.keys(rec.variants ?? {})) {
        ids.add(Number(key));
      }
    }
    const latest = data.construction[data.construction.length - 1]?.variants ?? {};
    return Array.from(ids).sort(
      (a, b) => (latest[b]?.best_score ?? -Infinity) - (latest[a]?.best_score ?? -Infinity)
    );
  }, [data]);

  const [selectedVariantId, setSelectedVariantId] = useState<number | null>(null);
  // 未選択時は最新エポックの最高スコアのバリアントをフォールバック表示する。
  const effectiveVariantId = selectedVariantId ?? variantIds[0] ?? null;

  // ---- 選択バリアントのスコア・内訳推移チャート用データ ----
  const chartData = useMemo(() => {
    if (!data || effectiveVariantId === null) return [];
    return data.construction.map((rec) => {
      const entry = rec.variants?.[effectiveVariantId] ?? null;
      return {
        date: rec.date,
        best_score: entry ? entry.best_score : null,
        best_npv: entry ? entry.best_npv ?? null : null,
        best_cost: entry ? totalCost(entry) : null,
        spawned: entry ? entry.spawned : 0,
      };
    });
  }, [data, effectiveVariantId]);

  // 選択バリアントに実データ（1エポックでもスコア記録あり）があるか。
  // 候補パーセルが無い等で当該バリアントが一度も評価されていない場合、
  // best_score は全エポックで null になる。この判定を使い、グラフの代わりに
  // 「データなし」を表示して、前に選択していたバリアントのグラフが残らないようにする。
  const hasChartData = useMemo(
    () => chartData.some((d) => d.best_score !== null),
    [chartData]
  );

  // ---- 最新エポックのバリアント別一覧 ----
  const latestConstruction = data?.construction[data.construction.length - 1] ?? null;
  const latestRows = useMemo(() => {
    if (!latestConstruction) return [];
    return Object.entries(latestConstruction.variants ?? {})
      .map(([id, entry]) => ({
        variantId: Number(id),
        bestScore: entry.best_score,
        npv: entry.best_npv ?? null,
        cost: totalCost(entry),
        spawned: entry.spawned,
      }))
      .sort((a, b) => b.bestScore - a.bestScore);
  }, [latestConstruction]);

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
    <div className="debug-spawn-tab debug-spawn-tab--fixed">
      <h2 className="debug-spawn-tab__title">{t("statistics.debugBuildingSpawnTitle")}</h2>

      <div className="debug-spawn-layout">
        {/* 左: スコア・内訳の推移チャート */}
        <div className="debug-spawn-layout__main">
          <section className="debug-spawn-card debug-spawn-card--fill">
            <div className="debug-spawn-card__header">
              <h3 className="debug-spawn-card__title">
                {t("statistics.debugSpawnConstructionSection")}
              </h3>
              <div className="debug-spawn-control">
                <label className="debug-spawn-control__label">
                  {t("statistics.debugSpawnVariantSelect")}
                </label>
                <select
                  className="debug-spawn-control__select"
                  value={effectiveVariantId ?? ""}
                  onChange={(e) => setSelectedVariantId(Number(e.target.value))}
                >
                  {variantIds.map((id) => (
                    <option key={id} value={id}>
                      {variantName(id)}
                    </option>
                  ))}
                </select>
              </div>
            </div>

            {hasChartData && effectiveVariantId !== null ? (
              <div className="debug-spawn-card__chart debug-spawn-card__chart--tall">
                <ResponsiveContainer width="100%" height="100%">
                  <ComposedChart data={chartData} margin={CHART_MARGIN}>
                    <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
                    <XAxis dataKey="date" tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} />
                    <YAxis
                      yAxisId="score"
                      tick={AXIS_TICK}
                      tickLine={false}
                      axisLine={{ stroke: "#253246" }}
                      width={52}
                      tickFormatter={formatCompact}
                      domain={["auto", "auto"]}
                    />
                    <YAxis
                      yAxisId="count"
                      orientation="right"
                      tick={AXIS_TICK}
                      tickLine={false}
                      axisLine={{ stroke: "#253246" }}
                      width={30}
                      allowDecimals={false}
                    />
                    <Tooltip
                      contentStyle={TOOLTIP_STYLE}
                      formatter={(value: any, _name: any, item: any) =>
                        item?.dataKey === "spawned"
                          ? value
                          : formatCompact(Number(value))
                      }
                    />
                    <Legend wrapperStyle={{ fontSize: "11px" }} />
                    {/* スコア0 = 建設が成立するライン */}
                    <ReferenceLine
                      yAxisId="score"
                      y={0}
                      stroke="#f59e0b"
                      strokeDasharray="6 3"
                      label={{
                        value: t("statistics.debugSpawnZeroLine"),
                        position: "insideTopRight",
                        fill: "#f59e0b",
                        fontSize: 11,
                      }}
                    />
                    <Bar
                      yAxisId="count"
                      dataKey="spawned"
                      name={t("statistics.debugSpawnSpawnedCount")}
                      fill="#10b981"
                      fillOpacity={0.5}
                      isAnimationActive={false}
                    />
                    <Line
                      yAxisId="score"
                      type="monotone"
                      dataKey="best_score"
                      name={t("statistics.debugSpawnBestScore")}
                      stroke={COLOR_BEST_SCORE}
                      strokeWidth={2}
                      dot={false}
                      connectNulls
                      isAnimationActive={false}
                    />
                    <Line
                      yAxisId="score"
                      type="monotone"
                      dataKey="best_npv"
                      name={t("statistics.debugSpawnBestNpv")}
                      stroke={COLOR_NPV}
                      strokeWidth={1.5}
                      strokeDasharray="5 4"
                      dot={false}
                      connectNulls
                      isAnimationActive={false}
                    />
                    <Line
                      yAxisId="score"
                      type="monotone"
                      dataKey="best_cost"
                      name={t("statistics.debugSpawnBestCost")}
                      stroke={COLOR_COST}
                      strokeWidth={1.5}
                      strokeDasharray="5 4"
                      dot={false}
                      connectNulls
                      isAnimationActive={false}
                    />
                  </ComposedChart>
                </ResponsiveContainer>
              </div>
            ) : (
              <div className="debug-spawn-card__no-data">{t("statistics.debugSpawnNoData")}</div>
            )}
          </section>
        </div>

        {/* 右: 最新エポックのバリアント別スコア一覧（行クリックでバリアント選択） */}
        <div className="debug-spawn-layout__side">
          <section className="debug-spawn-card debug-spawn-card--fill">
            <h3 className="debug-spawn-card__title">
              {t("statistics.debugSpawnLatestTable")}
              {latestConstruction && (
                <span className="debug-spawn-card__meta">
                  {t("statistics.debugSpawnPositivePatterns")}: {latestConstruction.positive_patterns}
                </span>
              )}
            </h3>
            {latestRows.length > 0 ? (
              <div className="debug-spawn-table-scroll debug-spawn-table-scroll--fill">
                <table className="debug-spawn-table debug-spawn-table--clickable">
                  <thead>
                    <tr>
                      <th>{t("statistics.debugSpawnColVariant")}</th>
                      <th>{t("statistics.debugSpawnColBestScore")}</th>
                      <th>{t("statistics.debugSpawnColNpv")}</th>
                      <th>{t("statistics.debugSpawnColCost")}</th>
                      <th>{t("statistics.debugSpawnColSpawned")}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {latestRows.map((row) => (
                      <tr
                        key={row.variantId}
                        className={[
                          row.bestScore > 0 && "debug-spawn-table__row--positive",
                          row.variantId === effectiveVariantId &&
                            "debug-spawn-table__row--selected",
                        ]
                          .filter(Boolean)
                          .join(" ")}
                        onClick={() => setSelectedVariantId(row.variantId)}
                      >
                        <td>{variantName(row.variantId)}</td>
                        <td className="debug-spawn-table__num">{formatCompact(row.bestScore)}</td>
                        <td className="debug-spawn-table__num">
                          {row.npv !== null ? formatCompact(row.npv) : "-"}
                        </td>
                        <td className="debug-spawn-table__num">
                          {row.cost !== null ? formatCompact(row.cost) : "-"}
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
