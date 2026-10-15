import { PLAY_MODE_SCEARIO } from "../../constants";
import { useEffect, useState, type MouseEvent as ReactMouseEvent } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { usePlayMode } from "../../hooks/usePlayMode";
import { ScenarioInfoSection } from "./ScenarioInfoSection";
import type { Quest, QuestClauseState } from "../../types";
import { ResponsiveContainer, ComposedChart, Bar, Line, CartesianGrid, ReferenceLine, XAxis, YAxis, Tooltip } from "recharts";
import { resolveIntlLocale, resolveLocalizedText } from "../../utils/locale";
import { formatQuestValue, questCompareLabel } from "../../utils/quest";
import { requestConfirm } from "../../utils/confirmDialog";
import { SatisfactionFace, getSatisfactionAppearance } from "../common/SatisfactionFace";
import { Meter } from "../common/Meter";
import "./AchievementsWindow.css";
import { useRemPx } from "../../hooks/useRemPx";

type QuestsResponse = {
  quests: Quest[];
};

/**
 * 1世帯が都市スコアに貢献しうる最大値。
 * qol_score(最大1.0) + 正規化 env_score(最大1.0) の合計であり、スコア式から定まる固定値。
 * 1世帯あたりスコアのグラフ軸の上限に使う（コックピットと同じ値）。
 */
const MAX_SCORE_PER_HOUSEHOLD = 2.0;

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
  city_score_per_household: number;
  city_score_collapse_penalty: number;
  city_score_household_count: number;
  city_score_avg_nutrition: number;
  city_score_avg_healthcare: number;
  city_score_avg_leisure: number;
  city_score_avg_env: number;
  collapse_events: CollapseEvent[];
};

type ScoreHistoryItem = {
  epoch: number;
  city_score: number;
  city_score_per_household: number;
  city_score_qol: number;
  city_score_env: number;
  city_score_collapse_penalty: number;
  city_score_household_count: number;
};

type ScoreResponse = {
  latest: ScoreDetail;
  history: ScoreHistoryItem[];
};

function isCompleted(quest: Quest): boolean {
  return quest.status === "completed";
}

export function AchievementsWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const remPx = useRemPx();
  const locale = resolveIntlLocale(i18n.language);
  const fmt = (value: number, digits = 1) => value.toLocaleString(locale, { maximumFractionDigits: digits });
  const [historyMode, setHistoryMode] = useState<"household" | "total">("household");
  const godMode = usePlayMode(apiBaseUrl) === PLAY_MODE_SCEARIO;
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
            buildApiUrl(apiBaseUrl, "/api/v1/score/"),
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
    if (!godMode) return;
    await invoke("open_registered_window", {
      windowType: "quest_edit",
      params: { extra: { editQuestId: questId } },
    });
  }

  /** カードの削除ボタン押下時、確認の上でクエストを削除する */
  async function handleDeleteQuest(e: ReactMouseEvent, questId: number) {
    e.stopPropagation();
    if (deletingQuestId !== null) return;
    const confirmed = await requestConfirm({
      titleKey: "achievements.deleteQuest",
      message: t("achievements.deleteQuestConfirm"),
      confirmLabel: t("common.delete"),
      tone: "danger",
    });
    if (!confirmed) return;

    setDeletingQuestId(questId);
    try {
      await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/quests/${questId}`), { method: "DELETE" });
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

  const latest = scoreData?.latest;
  const hasHistory = (scoreData?.history.length ?? 0) > 0;
  const hasHouseholds = hasHistory && (latest?.city_score_household_count ?? 0) > 0;
  const satisfaction = latest ? [
    { key: "avgNutrition", hint: "nutritionHint", value: latest.city_score_avg_nutrition },
    { key: "avgHealthcare", hint: "healthcareHint", value: latest.city_score_avg_healthcare },
    { key: "avgLeisure", hint: "leisureHint", value: latest.city_score_avg_leisure },
    { key: "avgEnv", hint: "envHint", value: latest.city_score_avg_env },
  ] : [];
  const overallSatisfaction = getSatisfactionAppearance((latest?.city_score_per_household ?? 0) / MAX_SCORE_PER_HOUSEHOLD);
  const order: Record<string, number> = { active: 0, locked: 1, completed: 2, failed: 3 };
  const orderedQuests = [...quests].sort((a, b) => order[a.status] - order[b.status]);
  // 内訳は減点前の値。減点を負方向に描き、確定済みスコアを線で示す。
  const history = (scoreData?.history ?? []).map((item) => {
    const divisor = historyMode === "household" ? item.city_score_household_count : 1;
    return {
      name: t("achievements.epochLabel", { epoch: item.epoch }),
      qol: divisor > 0 ? item.city_score_qol / divisor : 0,
      env: divisor > 0 ? item.city_score_env / divisor : 0,
      penalty: divisor > 0 ? -item.city_score_collapse_penalty / divisor : 0,
      score: historyMode === "household" ? item.city_score_per_household : item.city_score,
    };
  });

  const historyMinimum = Math.floor(Math.min(0, ...history.map((item) => item.penalty)) * 2) / 2;

  return (
    <div className="achieve-dashboard">
      <div className="achieve-dashboard__goals">
        <h3 className="achieve-dashboard__section-title">
          {t("achievements.title")}
          <span className="achieve-dashboard__quest-count">
            {t("achievements.completedCount", { completed: quests.filter(isCompleted).length, total: quests.length })}
          </span>
          {godMode && (
            <button
              className="achieve-dashboard__add-quest-btn"
              type="button"
              onClick={() => void handleOpenCreateQuest()}
            >
              {t("achievements.addQuest")}
            </button>
          )}
        </h3>

        <p className="achieve-dashboard__hint">{t("achievements.goalsHint")}</p>
        <ScenarioInfoSection apiBaseUrl={apiBaseUrl} godMode={godMode} />
        <div className="achieve-dashboard__quest-grid">
          {quests.length === 0 ? (
            <div className="achieve-dashboard__empty-quests">{t("achievements.noQuests")}</div>
          ) : (
            orderedQuests.map((quest) => {
              const completed = isCompleted(quest);
              const locked = quest.status === "locked";
              const failed = quest.status === "failed";
              // 隠しクエストは達成するまで名前・内容・条件を伏せる（テンプレートモードでは見せる）
              const masked = quest.hidden && !completed && !godMode;
              const name = resolveLocalizedText(quest.name, i18n.language);
              const progress = completed ? 1 : Math.max(0, Math.min(1, quest.progress ?? 0));
              const barColor = completed ? "var(--flat-good)" : failed ? "var(--flat-danger)" : "var(--flat-accent)";
              return (
                <div
                  key={quest.id}
                  className={[
                    "quest-grid-card",
                    completed ? "quest-grid-card--completed" : "",
                    locked ? "quest-grid-card--locked" : "",
                    failed ? "quest-grid-card--failed" : "",
                    godMode ? "quest-grid-card--editable" : "",
                  ].join(" ")}
                  onClick={godMode ? () => void handleOpenEditQuest(quest.id) : undefined}
                  role={godMode ? "button" : undefined}
                  tabIndex={godMode ? 0 : undefined}
                  onKeyDown={godMode ? (event) => {
                    if (event.target === event.currentTarget && (event.key === "Enter" || event.key === " ")) {
                      event.preventDefault();
                      void handleOpenEditQuest(quest.id);
                    }
                  } : undefined}
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
                    <span className="quest-grid-card__name" title={masked ? undefined : name}>
                      {masked ? t("achievements.hiddenQuestName") : name}
                    </span>
                    {/* tier バッジ（bronze はデフォルト扱いで表示しない） */}
                    {quest.tier !== "bronze" && (
                      <span className={`quest-grid-card__tier quest-grid-card__tier--${quest.tier}`}>
                        {t(`achievements.tier.${quest.tier}`)}
                      </span>
                    )}
                    {godMode && (
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

                  <span className="quest-grid-card__status">{t(`achievements.status.${quest.status}`)}</span>
                  <p className="quest-grid-card__desc">
                    {masked ? t("achievements.hiddenQuestDesc") : resolveLocalizedText(quest.description, i18n.language)}
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
                          {t("achievements.rewardMoney", { amount: fmt(quest.reward.amount) })}
                        </span>
                      )}
                    </div>
                  )}

                  {locked ? (
                    <p className="quest-grid-card__locked-note">{t("achievements.lockedNote")}</p>
                  ) : failed ? (
                    <p className="quest-grid-card__failed-note">{t("achievements.failedNote")}</p>
                  ) : !masked ? (
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
                  ) : null}
                </div>
              );
            })
          )}
        </div>
      </div>
      <section className="achieve-dashboard__score" aria-labelledby="resident-score-title">
        <header className="achieve-dashboard__score-heading">
          <h2 id="resident-score-title">{t("achievements.scoreTitle")}</h2>
          {hasHistory && latest && <span>{t("achievements.epochLabel", { epoch: latest.epoch })}</span>}
        </header>
        <p className="achieve-dashboard__hint">{t("achievements.scoreHint")}</p>
        {!hasHistory ? <p className="achieve-dashboard__empty">{t("achievements.noHistory")}</p> : latest && <>
          <div className="resident-score-summary">
            <div className="resident-score-main">
              <span>{t("achievements.perHouseholdScore")}</span>
              {hasHouseholds ? <div className="resident-score-gauge-row">
                <div className="resident-score-gauge" role="meter"
                  aria-label={t("achievements.perHouseholdScore")} aria-valuemin={0} aria-valuemax={2}
                  aria-valuenow={latest.city_score_per_household}
                  aria-valuetext={`${fmt(latest.city_score_per_household, 2)} / 2 ${t("achievements.points")}`}>
                  <svg viewBox="0 0 160 90" aria-hidden="true">
                    <path d="M 12 80 A 68 68 0 0 1 148 80" pathLength={100} className="resident-score-gauge__track" />
                    <path d="M 12 80 A 68 68 0 0 1 148 80" pathLength={100}
                      fill="none" stroke={overallSatisfaction.color} strokeWidth={16}
                      strokeDasharray={`${overallSatisfaction.fraction * 100} 100`} />
                  </svg>
                  <div className="resident-score-gauge__value">
                    <strong style={{ color: overallSatisfaction.color }}>{fmt(overallSatisfaction.fraction * 100, 0)}<small>%</small></strong>
                    <span>{fmt(latest.city_score_per_household, 2)} / {fmt(MAX_SCORE_PER_HOUSEHOLD)} {t("achievements.points")}</span>
                  </div>
                </div>
                <SatisfactionFace value={overallSatisfaction.fraction} size={44} />
              </div> : <div className="resident-score-unavailable">—</div>}
              {!hasHouseholds && <p>{t("achievements.noHouseholds")}</p>}
            </div>
            <div className="resident-score-total">
              <span>{t("achievements.currentScore")}</span>
              <strong>{fmt(latest.city_score)} {t("achievements.points")}</strong>
              <dl>
                <div><dt>{t("achievements.qolScore")}</dt><dd>+{fmt(latest.city_score_qol)}</dd></div>
                <div><dt>{t("achievements.envScore")}</dt><dd>+{fmt(latest.city_score_env)}</dd></div>
                <div className="resident-score-penalty"><dt>{t("achievements.collapsePenalty")}</dt><dd>{latest.city_score_collapse_penalty > 0 ? "−" : ""}{fmt(latest.city_score_collapse_penalty)}</dd></div>
              </dl>
            </div>
          </div>
          <section className="resident-score-history">
            <h3>{t("achievements.scoreHistory")}</h3>
            <div className="resident-score-modes" role="group" aria-label={t("achievements.scoreHistory")}>
              {(["household", "total"] as const).map((mode) => <button key={mode} type="button" aria-pressed={historyMode === mode} onClick={() => setHistoryMode(mode)}>{t(mode === "household" ? "achievements.perHouseholdScore" : "achievements.currentScore")}</button>)}
            </div>
            <div className="resident-score-legend">
              <span style={{ color: "var(--flat-accent)" }}>■ {t("achievements.qolScore")}</span>
              <span style={{ color: "var(--flat-good)" }}>■ {t("achievements.envScore")}</span>
              <span style={{ color: "var(--flat-danger)" }}>■ {t("achievements.collapsePenalty")}</span>
              <span>━ {t("achievements.finalScore")}</span>
            </div>
            <div className="resident-score-chart">
              <ResponsiveContainer width="100%" height="100%">
                <ComposedChart data={history} stackOffset="sign" margin={{ top: 8, right: 12, bottom: 4, left: 0 }} accessibilityLayer>
                  <CartesianGrid vertical={false} stroke="#26374d" />
                  <XAxis dataKey="name" tick={{ fill: "#a4bccc", fontSize: "1rem" }} minTickGap={32} tickLine={false} />
                  <YAxis domain={historyMode === "household" ? [historyMinimum, MAX_SCORE_PER_HOUSEHOLD] : ["auto", "auto"]} ticks={historyMode === "household" ? [-1, -0.5, 0, 0.5, 1, 1.5, 2].filter((value) => value >= historyMinimum) : undefined} tick={{ fill: "#a4bccc", fontSize: "1rem" }} tickFormatter={(value: number) => fmt(value)} width={remPx(4.63)} tickLine={false} />
                  <ReferenceLine y={0} stroke="#a4bccc" />
                  <Tooltip content={({ active, payload, label }) => active && payload?.length ? <div className="resident-score-tooltip"><strong>{label}</strong>{payload.map((entry) => <div key={String(entry.dataKey)}>{entry.name}: {fmt(Number(entry.value), 2)} {t("achievements.points")}</div>)}</div> : null} />
                  <Bar dataKey="qol" name={t("achievements.qolScore")} stackId="score" fill="#4ea1ff" maxBarSize={30} isAnimationActive={false} />
                  <Bar dataKey="env" name={t("achievements.envScore")} stackId="score" fill="#6cd47f" maxBarSize={30} isAnimationActive={false} />
                  <Bar dataKey="penalty" name={t("achievements.collapsePenalty")} stackId="score" fill="#ff9a9a" maxBarSize={30} isAnimationActive={false} />
                  <Line dataKey="score" name={t("achievements.finalScore")} stroke="#e0e0e0" strokeWidth={2} dot={{ r: 2 }} isAnimationActive={false} />
                </ComposedChart>
              </ResponsiveContainer>
            </div>
          </section>
          <section className="resident-satisfaction">
            <h3>{t("achievements.satisfactionTitle")}</h3>
            {hasHouseholds ? satisfaction.map((item) => {
              const appearance = getSatisfactionAppearance(item.value);
              return <div className="resident-satisfaction-row" key={item.key}>
                <div className="resident-satisfaction-row__body">
                  <div className="resident-satisfaction-row__label"><span>{t(`achievements.${item.key}`)}</span><strong>{fmt(appearance.fraction * 100, 0)}%</strong></div>
                  <div role="meter" aria-label={t(`achievements.${item.key}`)} aria-valuemin={0} aria-valuemax={100} aria-valuenow={appearance.fraction * 100}>
                    <Meter value={appearance.fraction} color={appearance.color} />
                  </div>
                  <small>{t(`achievements.${item.hint}`)}</small>
                </div>
                <SatisfactionFace value={appearance.fraction} size={34} />
              </div>;
            }) : <p className="achieve-dashboard__hint">{t("achievements.noHouseholds")}</p>}
          </section>
          <details className="resident-disasters">
            <summary>{t("achievements.disasterList")} ({latest.collapse_events.length})</summary>
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
          </details>
        </>}
      </section>

    </div>
  );
}

/**
 * クエスト条件1節分の表示行。
 * 「現在値 演算子 目標値」に加え、時間派生モード（delta / moving_avg / streak 等）を
 * 小さなラベルで補足する。streak は「n / window エポック連続」を表示する。
 */
function ClauseRow({ clause }: { clause: QuestClauseState }) {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);
  const modeLabel =
    clause.mode !== "value" ? t(`achievements.mode.${clause.mode}`, { window: clause.window }) : null;
  return (
    <div className="quest-grid-card__clause">
      <span className="quest-grid-card__current">{formatQuestValue(clause.current, locale)}</span>
      <span className="quest-grid-card__operator">{questCompareLabel(clause.compare)}</span>
      <span className="quest-grid-card__target">{formatQuestValue(clause.target, locale)}</span>
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
