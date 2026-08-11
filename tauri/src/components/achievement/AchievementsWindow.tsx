/**
 * 統合施政スコア＆達成目標ダッシュボード
 *
 * ウィンドウ全体を上下に分割し、都市の成長度合と課題を一目で把握できるようにリニューアル。
 *
 * - 上部エリア (3/5): 施政スコア分析
 *   - 上段・左: 当期スコア、累積スコア、集計世帯数、ペナルティ等の数値表示。
 *   - 上段・右: 世帯満足度5軸（栄養、医療、娯楽、居住、住環境）のレーダーチャート。
 *   - 下段: 過去エポックごとのスコア推移（棒グラフ）。
 * - 下部エリア (2/5): 達成目標（クエスト）カード一覧
 *   - カードグリッド形式で、各目標の進捗率・ステータスを一目で確認可能。
 *   - 進捗はサーバ計算の progress（0..1）を使い、複数条件（clause_states）は行ごとに表示する。
 *   - status: locked（先行クエスト未達成）はグレー+錠前、failed（期限切れ）は赤系で表示。
 *   - hidden の隠しクエストは達成まで名前・内容を「???」で伏せる。
 *   - tier（gold/silver/bronze）バッジと category ラベル、期限（deadline）も表示する。
 */

import { useEffect, useState, type MouseEvent as ReactMouseEvent } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useTemplateMode } from "../../hooks/useTemplateMode";
import type { Quest, QuestClauseState } from "../../types";
import {
  Radar,
  RadarChart,
  PolarGrid,
  PolarAngleAxis,
  PolarRadiusAxis,
  ResponsiveContainer,
  ComposedChart,
  BarChart,
  Bar,
  Cell,
  LabelList,
  Area,
  XAxis,
  YAxis,
  Tooltip,
} from "recharts";
import "./AchievementsWindow.css";

type QuestsResponse = {
  quests: Quest[];
};

type CollapseEvent = {
  agent_id: number;
  variant: number;
  zone_id: number;
  death_count: number;
  is_path: boolean;
  // 倒壊原因("fire"=焼失 / "aging"=老朽倒壊)
  cause?: string;
};

type ScoreDetail = {
  epoch: number;
  city_score: number;
  city_score_qol: number;
  city_score_env: number;
  city_score_cumulative: number;
  city_score_collapse_penalty: number;
  city_score_household_count: number;
  city_score_avg_nutrition: number;
  city_score_avg_healthcare: number;
  city_score_avg_entertainment: number;
  city_score_avg_env: number;
  collapse_events: CollapseEvent[];
};

type ScoreHistoryItem = {
  epoch: number;
  city_score: number;
  city_score_cumulative: number;
};

type ScoreResponse = {
  latest: ScoreDetail;
  history: ScoreHistoryItem[];
};

function compareToLabel(op: string): string {
  switch (op) {
    case ">":
      return ">";
    case "<":
      return "<";
    case ">=":
      return "≥";
    case "<=":
      return "≤";
    case "==":
      return "=";
    case "!=":
      return "≠";
    default:
      return op;
  }
}

function isCompleted(quest: Quest): boolean {
  return quest.status === "completed";
}

function formatMetricValue(v: number | null): string {
  if (v === null || v === undefined) return "—";
  return Number.isInteger(v)
    ? v.toLocaleString()
    : v.toLocaleString(undefined, { maximumFractionDigits: 2 });
}

export function AchievementsWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const templateMode = useTemplateMode(apiBaseUrl);
  const [quests, setQuests] = useState<Quest[]>([]);
  const [scoreData, setScoreData] = useState<ScoreResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [deletingQuestId, setDeletingQuestId] = useState<number | null>(null);

  useEffect(() => {
    const controller = new AbortController();

    async function load() {
      try {
        const [questsRes, scoreRes] = await Promise.all([
          fetchApi<QuestsResponse>(
            buildApiUrl(apiBaseUrl, "/api/v1/quests"),
            { signal: controller.signal }
          ),
          fetchApi<ScoreResponse>(
            buildApiUrl(apiBaseUrl, "/api/v1/score"),
            { signal: controller.signal }
          ),
        ]);

        setQuests(questsRes.quests);
        setScoreData(scoreRes);
      } catch (err) {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : "Unknown error");
        }
      } finally {
        setLoading(false);
      }
    }

    load();
    return () => controller.abort();
  }, [apiBaseUrl]);

  /** 「追加」ボタン押下時、新規作成モードで quest_edit ウィンドウを開く */
  async function handleOpenCreateQuest() {
    await invoke("open_registered_window", {
      windowType: "quest_edit",
      params: { extra: {} },
    });
  }

  /** クエストカードクリック時、編集モードで quest_edit ウィンドウを開く(テンプレートモード時のみ有効) */
  async function handleOpenEditQuest(questId: number) {
    if (!templateMode) return;
    await invoke("open_registered_window", {
      windowType: "quest_edit",
      params: { extra: { editQuestId: questId } },
    });
  }

  /** カードの削除ボタン押下時、確認の上でクエストを削除する */
  async function handleDeleteQuest(e: ReactMouseEvent, questId: number) {
    e.stopPropagation();
    if (deletingQuestId !== null) return;
    const confirmed = window.confirm(t("achievements.deleteQuestConfirm"));
    if (!confirmed) return;

    setDeletingQuestId(questId);
    try {
      const res = await fetch(buildApiUrl(apiBaseUrl, `/api/v1/quests/${questId}`), { method: "DELETE" });
      if (!res.ok) {
        const bodyText = await res.text().catch(() => "");
        throw new Error(`HTTP ${res.status}: ${bodyText}`);
      }
      setQuests((prev) => prev.filter((q) => q.id !== questId));
    } catch (err) {
      console.error("[AchievementsWindow] Failed to delete quest:", err);
    } finally {
      setDeletingQuestId(null);
    }
  }

  if (loading) {
    return <div className="achieve-window__loading">{t("common.loading")}</div>;
  }

  if (error) {
    return <div className="achieve-window__error">{t("common.fetchError")}: {error}</div>;
  }

  // レーダーチャート用データ構築 (0〜100%)。
  // 施政スコアの構成要素は qol_score（栄養・医療・娯楽）と env_score（住環境）の4軸のみ。
  // satis_residential はスコア計算に使われないため表示しない。
  const radarData = scoreData
    ? [
        { subject: t("achievements.avgNutrition"), value: Math.round(scoreData.latest.city_score_avg_nutrition * 100) },
        { subject: t("achievements.avgHealthcare"), value: Math.round(scoreData.latest.city_score_avg_healthcare * 100) },
        { subject: t("achievements.avgEntertainment"), value: Math.round(scoreData.latest.city_score_avg_entertainment * 100) },
        { subject: t("achievements.avgEnv"), value: Math.round(scoreData.latest.city_score_avg_env * 100) },
      ]
    : [];

  // 棒グラフ用データ構築
  const barData = scoreData
    ? scoreData.history.map((h) => ({
        name: t("achievements.epochLabel", { epoch: h.epoch }),
        score: Math.round(h.city_score * 10) / 10,
        cumulative: Math.round(h.city_score_cumulative * 10) / 10,
      }))
    : [];

  const latest = scoreData?.latest;

  return (
    <div className="achieve-dashboard">
      {/* 上部エリア (3/5): 施政スコア分析 */}
      <div className="achieve-dashboard__upper">
        
        {/* 上段: 数値サマリー & スコア推移 */}
        <div className="achieve-dashboard__row-top">
          {/* 左側: 数値サマリー */}
          <div className="achieve-dashboard__summary">
            <h3 className="achieve-dashboard__section-title">{t("achievements.scoreTitle")}</h3>
            {latest && (
              <div className="score-summary-grid">
                <div className="score-summary-card score-summary-card--main">
                  <span className="score-summary-card__label">{t("achievements.currentScore")}</span>
                  <div className="score-summary-card__value-row">
                    <span className="score-summary-card__value">{latest.city_score.toLocaleString(undefined, { maximumFractionDigits: 1 })}</span>
                    <span className="score-summary-card__unit">{t("achievements.points")}</span>
                  </div>
                  <div className="score-summary-card__breakdown">
                    <span>QoL: {latest.city_score_qol.toLocaleString(undefined, { maximumFractionDigits: 1 })}</span>
                    <span>Env: {latest.city_score_env.toLocaleString(undefined, { maximumFractionDigits: 1 })}</span>
                    {latest.city_score_collapse_penalty > 0 && (
                      <span className="score-summary-card__penalty">
                        -{latest.city_score_collapse_penalty.toLocaleString(undefined, { maximumFractionDigits: 1 })}
                      </span>
                    )}
                  </div>
                </div>

                <div className="score-summary-card score-summary-card--sub">
                  <span className="score-summary-card__label">{t("achievements.cumulativeScore")}</span>
                  <div className="score-summary-card__value-row">
                    <span className="score-summary-card__value">{latest.city_score_cumulative.toLocaleString(undefined, { maximumFractionDigits: 1 })}</span>
                    <span className="score-summary-card__unit">{t("achievements.points")}</span>
                  </div>
                </div>

                <div className="score-summary-card score-summary-card--sub">
                  <span className="score-summary-card__label">{t("achievements.householdCount")}</span>
                  <div className="score-summary-card__value-row">
                    <span className="score-summary-card__value">{latest.city_score_household_count.toLocaleString()}</span>
                    <span className="score-summary-card__unit">{t("achievements.households")}</span>
                  </div>
                </div>
              </div>
            )}
          </div>

          {/* 右側: スコア履歴 (棒グラフ) */}
          <div className="achieve-dashboard__history">
            <h3 className="achieve-dashboard__section-title">{t("achievements.scoreHistory")}</h3>
            <div className="achieve-dashboard__chart-container-bar">
              {barData.length > 0 ? (
                <ResponsiveContainer width="100%" height="100%">
                  <ComposedChart data={barData} margin={{ top: 10, right: 10, left: -20, bottom: 5 }}>
                    <defs>
                      <linearGradient id="barGrad" x1="0" y1="0" x2="0" y2="1">
                        <stop offset="0%" stopColor="#4ea1ff" stopOpacity={0.85} />
                        <stop offset="100%" stopColor="#3b82f6" stopOpacity={0.2} />
                      </linearGradient>
                      <linearGradient id="areaGrad" x1="0" y1="0" x2="0" y2="1">
                        <stop offset="0%" stopColor="#f59e0b" stopOpacity={0.3} />
                        <stop offset="100%" stopColor="#f59e0b" stopOpacity={0.03} />
                      </linearGradient>
                    </defs>
                    <XAxis
                      dataKey="name"
                      tick={{ fill: "#7b8fa5", fontSize: 10 }}
                      axisLine={{ stroke: "rgba(255, 255, 255, 0.08)" }}
                      tickLine={false}
                    />
                    <YAxis
                      yAxisId="left"
                      tick={{ fill: "#c4873a", fontSize: 10 }}
                      axisLine={{ stroke: "rgba(255, 255, 255, 0.08)" }}
                      tickLine={false}
                    />
                    <YAxis
                      yAxisId="right"
                      orientation="right"
                      tick={{ fill: "#7b8fa5", fontSize: 10 }}
                      axisLine={{ stroke: "rgba(255, 255, 255, 0.08)" }}
                      tickLine={false}
                    />
                    <Tooltip
                      content={({ active, payload }) => {
                        if (active && payload && payload.length) {
                          const data = payload[0].payload as { name: string; score: number; cumulative: number };
                          return (
                            <div className="bar-chart-tooltip">
                              <p className="bar-chart-tooltip__epoch">{data.name}</p>
                              <p className="bar-chart-tooltip__val">
                                {t("achievements.currentScore")}: <span>{data.score} {t("achievements.points")}</span>
                              </p>
                              <p className="bar-chart-tooltip__val">
                                {t("achievements.cumulativeScore")}: <span>{data.cumulative} {t("achievements.points")}</span>
                              </p>
                            </div>
                          );
                        }
                        return null;
                      }}
                    />
                    <Area
                      yAxisId="left"
                      dataKey="cumulative"
                      stroke="#f59e0b"
                      strokeWidth={1.5}
                      fill="url(#areaGrad)"
                      dot={false}
                      activeDot={{ r: 3, fill: "#f59e0b" }}
                    />
                    <Bar
                      yAxisId="right"
                      dataKey="score"
                      fill="url(#barGrad)"
                      radius={[4, 4, 0, 0]}
                      maxBarSize={45}
                    />
                  </ComposedChart>
                </ResponsiveContainer>
              ) : (
                <div className="achieve-dashboard__no-chart">{t("achievements.noHistory")}</div>
              )}
            </div>
          </div>
        </div>

        {/* 中段: スコア構成 & 世帯満足度 & 災害一覧 */}
        <div className="achieve-dashboard__row-bottom">
          {/* 左: スコア構成ウォーターフォール */}
          {latest && (() => {
            const qol = latest.city_score_qol;
            const env = latest.city_score_env;
            const penalty = latest.city_score_collapse_penalty;
            const hasPenalty = penalty > 0;
            const fmt = (v: number) => v.toLocaleString(undefined, { maximumFractionDigits: 1 });

            const wfData = [
              { name: t("achievements.qolScore"),        base: 0,              value: qol,                color: "#3b82f6", label: `+${fmt(qol)}` },
              { name: t("achievements.envScore"),        base: qol,            value: env,                color: "#10b981", label: `+${fmt(env)}` },
              { name: t("achievements.collapsePenalty"), base: qol + env - penalty, value: Math.max(penalty, 0.1), color: hasPenalty ? "#ef4444" : "rgba(255,255,255,0.04)", label: hasPenalty ? `-${fmt(penalty)}` : "0" },
              { name: t("achievements.finalScore"),      base: 0,              value: latest.city_score,  color: "#4ea1ff", label: fmt(latest.city_score) },
            ];

            return (
              <div className="achieve-dashboard__waterfall">
                <h3 className="achieve-dashboard__section-title">{t("achievements.scoreComposition")}</h3>
                <div className="achieve-dashboard__chart-container">
                  {radarData.length > 0 ? (
                    <ResponsiveContainer width="100%" height="100%">
                      <BarChart
                        data={wfData}
                        margin={{ top: 12, right: 8, left: -20, bottom: 4 }}
                        barCategoryGap="20%"
                      >
                        <XAxis
                          dataKey="name"
                          tick={{ fill: "#9db0c5", fontSize: 9, fontWeight: 600 }}
                          axisLine={{ stroke: "rgba(255, 255, 255, 0.08)" }}
                          tickLine={false}
                          interval={0}
                        />
                        <YAxis hide />
                        <Bar dataKey="base" stackId="wf" fill="transparent" />
                        <Bar dataKey="value" stackId="wf" radius={[3, 3, 0, 0]}>
                          <LabelList dataKey="label" position="top" fill="#e5eef9" fontSize={10} fontWeight={600} />
                          {wfData.map((entry, i) => (
                            <Cell key={i} fill={entry.color} />
                          ))}
                        </Bar>
                      </BarChart>
                    </ResponsiveContainer>
                  ) : (
                    <div className="achieve-dashboard__no-chart">{t("achievements.noHistory")}</div>
                  )}
                </div>
              </div>
            );
          })()}

          {/* 中央: 世帯満足度レーダーチャート */}
          <div className="achieve-dashboard__radar">
            <h3 className="achieve-dashboard__section-title">世帯満足度内訳</h3>
            <div className="achieve-dashboard__chart-container">
              {radarData.length > 0 ? (
                <ResponsiveContainer width="100%" height="100%">
                  <RadarChart cx="50%" cy="50%" outerRadius="68%" data={radarData}>
                    <PolarGrid stroke="rgba(255, 255, 255, 0.08)" />
                    <PolarAngleAxis
                      dataKey="subject"
                      tick={{ fill: "#9db0c5", fontSize: 10, fontWeight: 600 }}
                    />
                    <PolarRadiusAxis
                      angle={30}
                      domain={[0, 100]}
                      tick={{ fill: "#5a6f86", fontSize: 9 }}
                      axisLine={false}
                      tickLine={false}
                    />
                    <Radar
                      name="Satisfaction"
                      dataKey="value"
                      stroke="#4ea1ff"
                      fill="#4ea1ff"
                      fillOpacity={0.25}
                    />
                  </RadarChart>
                </ResponsiveContainer>
              ) : (
                <div className="achieve-dashboard__no-chart">{t("achievements.noHistory")}</div>
              )}
            </div>
          </div>

          {/* 右: 災害一覧 */}
          <div className="achieve-dashboard__disasters">
            <h3 className="achieve-dashboard__section-title">{t("achievements.disasterList")}</h3>
            <div className="disaster-list">
              {latest && latest.collapse_events.length > 0 ? (
                latest.collapse_events.map((evt, idx) => (
                  <div key={idx} className="disaster-card">
                    <div className="disaster-card__header">
                      <span className={`disaster-card__badge ${evt.is_path ? "disaster-card__badge--path" : "disaster-card__badge--building"}`}>
                        {evt.is_path ? t("achievements.disasterTypePath") : t("achievements.disasterTypeBuilding")}
                      </span>
                      <span className="disaster-card__badge">
                        {evt.cause === "fire"
                          ? t("achievements.disasterCauseFire")
                          : t("achievements.disasterCauseAging")}
                      </span>
                      <span className="disaster-card__id">#{evt.agent_id}</span>
                    </div>
                    <div className="disaster-card__details">
                      <span className="disaster-card__zone">{t("achievements.disasterZone")}: {evt.zone_id}</span>
                      {evt.death_count > 0 && (
                        <span className="disaster-card__deaths">{evt.death_count}{t("achievements.disasterDeaths")}</span>
                      )}
                      {evt.death_count === 0 && (
                        <span className="disaster-card__deaths disaster-card__deaths--zero">0{t("achievements.disasterDeaths")}</span>
                      )}
                    </div>
                  </div>
                ))
              ) : (
                <div className="disaster-list__empty">{t("achievements.disasterNoData")}</div>
              )}
            </div>
          </div>
        </div>

      </div>

      {/* 下部エリア (2/5): 達成目標カード一覧 */}
      <div className="achieve-dashboard__lower">
        <h3 className="achieve-dashboard__section-title">
          {t("achievements.title")}
          <span className="achieve-dashboard__quest-count">
            ({quests.filter(isCompleted).length} / {quests.length})
          </span>
          {templateMode && (
            <button
              className="achieve-dashboard__add-quest-btn"
              type="button"
              onClick={() => void handleOpenCreateQuest()}
            >
              {t("achievements.addQuest")}
            </button>
          )}
        </h3>

        <div className="achieve-dashboard__quest-grid">
          {quests.length === 0 ? (
            <div className="achieve-dashboard__empty-quests">{t("achievements.noQuests")}</div>
          ) : (
            quests.map((quest) => {
              const completed = isCompleted(quest);
              const locked = quest.status === "locked";
              const failed = quest.status === "failed";
              // 隠しクエストは達成するまで名前・内容・条件を伏せる（テンプレートモードでは見せる）
              const masked = quest.hidden && !completed && !templateMode;
              const progress = completed ? 1 : quest.progress ?? 0;
              const barColor = completed ? "#10b981" : failed ? "#ef4444" : "#3b82f6";
              return (
                <div
                  key={quest.id}
                  className={[
                    "quest-grid-card",
                    completed ? "quest-grid-card--completed" : "",
                    locked ? "quest-grid-card--locked" : "",
                    failed ? "quest-grid-card--failed" : "",
                    templateMode ? "quest-grid-card--editable" : "",
                  ].join(" ")}
                  onClick={templateMode ? () => void handleOpenEditQuest(quest.id) : undefined}
                  role={templateMode ? "button" : undefined}
                >
                  <div className="quest-grid-card__top">
                    <span className="quest-grid-card__badge">
                      {completed ? (
                        <span className="quest-grid-card__check">&#10003;</span>
                      ) : locked ? (
                        <span className="quest-grid-card__lock">&#128274;</span>
                      ) : failed ? (
                        <span className="quest-grid-card__cross">&#10005;</span>
                      ) : (
                        <span className="quest-grid-card__circle" />
                      )}
                    </span>
                    <span className="quest-grid-card__name" title={masked ? undefined : quest.name}>
                      {masked ? t("achievements.hiddenQuestName") : quest.name}
                    </span>
                    {/* tier バッジ（bronze はデフォルト扱いで表示しない） */}
                    {quest.tier !== "bronze" && (
                      <span className={`quest-grid-card__tier quest-grid-card__tier--${quest.tier}`}>
                        {t(`achievements.tier.${quest.tier}`)}
                      </span>
                    )}
                    {templateMode && (
                      <button
                        className="quest-grid-card__delete-btn"
                        type="button"
                        disabled={deletingQuestId === quest.id}
                        onClick={(e) => void handleDeleteQuest(e, quest.id)}
                        title={t("achievements.deleteQuest")}
                      >
                        &#10005;
                      </button>
                    )}
                  </div>

                  <p className="quest-grid-card__desc">
                    {masked ? t("achievements.hiddenQuestDesc") : quest.description}
                  </p>

                  {/* メタ情報行: カテゴリ・期限・報酬 */}
                  {!masked && (quest.category || quest.deadline_epoch !== null || quest.reward) && (
                    <div className="quest-grid-card__meta">
                      {quest.category && (
                        <span className="quest-grid-card__meta-chip">{quest.category}</span>
                      )}
                      {quest.deadline_epoch !== null && !completed && !failed && (
                        <span className="quest-grid-card__meta-chip quest-grid-card__meta-chip--deadline">
                          {t("achievements.deadlineEpoch", { epoch: quest.deadline_epoch })}
                        </span>
                      )}
                      {quest.reward?.type === "grant_money" && (
                        <span className="quest-grid-card__meta-chip quest-grid-card__meta-chip--reward">
                          {t("achievements.rewardMoney", { amount: quest.reward.amount.toLocaleString() })}
                        </span>
                      )}
                    </div>
                  )}

                  {locked ? (
                    <p className="quest-grid-card__locked-note">{t("achievements.lockedNote")}</p>
                  ) : failed ? (
                    <p className="quest-grid-card__failed-note">{t("achievements.failedNote")}</p>
                  ) : (
                    <div className="quest-grid-card__progress-area">
                      <div className="quest-grid-card__bar-track">
                        <div
                          className="quest-grid-card__bar-fill"
                          style={{
                            width: `${progress * 100}%`,
                            backgroundColor: barColor,
                          }}
                        />
                      </div>

                      {/* 各条件節の現在値・目標値。隠しクエストでは表示しない */}
                      {!masked &&
                        quest.clause_states.map((cs) => (
                          <ClauseRow key={cs.clause_key} clause={cs} />
                        ))}

                      <div className="quest-grid-card__values">
                        <span className="quest-grid-card__pct">
                          {t("achievements.progress")}: {Math.round(progress * 100)}%
                        </span>
                      </div>
                    </div>
                  )}
                </div>
              );
            })
          )}
        </div>
      </div>
    </div>
  );
}

/**
 * クエスト条件1節分の表示行。
 * 「現在値 演算子 目標値」に加え、時間派生モード（delta / moving_avg / streak 等）を
 * 小さなラベルで補足する。streak は「n / window エポック連続」を表示する。
 */
function ClauseRow({ clause }: { clause: QuestClauseState }) {
  const { t } = useTranslation();
  const modeLabel =
    clause.mode !== "value" ? t(`achievements.mode.${clause.mode}`, { window: clause.window }) : null;
  return (
    <div className="quest-grid-card__clause">
      <span className="quest-grid-card__current">{formatMetricValue(clause.current)}</span>
      <span className="quest-grid-card__operator">{compareToLabel(clause.compare)}</span>
      <span className="quest-grid-card__target">{formatMetricValue(clause.target)}</span>
      {clause.mode === "streak" && (
        <span className="quest-grid-card__streak">
          {t("achievements.streakProgress", { current: clause.streak ?? 0, window: clause.window })}
        </span>
      )}
      {modeLabel && clause.mode !== "streak" && (
        <span className="quest-grid-card__mode">{modeLabel}</span>
      )}
      {clause.satisfied === true && <span className="quest-grid-card__clause-ok">&#10003;</span>}
    </div>
  );
}
