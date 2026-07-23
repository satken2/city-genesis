/**
 * 達成クエスト編集パネル
 *
 * window_type="quest_edit" のポップアップウィンドウで表示する専用パネル。
 * テンプレート作成モード(gd3.template_mode)でのみ、達成クエストの新規作成・編集・削除を行う。
 *
 * - editQuestId が未指定の場合: 新規作成モード。確定ボタンで POST /api/v1/quests を呼ぶ。
 * - editQuestId が指定されている場合: 編集モード。マウント時に GET /api/v1/quests/{id} で
 *   対象クエストの現在値を取得してフォームに反映し、確定ボタンで PATCH /api/v1/quests/{id} を呼ぶ。
 *   このモードでは削除ボタンも表示され、DELETE /api/v1/quests/{id} を呼ぶ。
 *
 * ## 条件エディタの2モード
 *
 * 1. フォームモード: 条件節(clause)のリストを all / any で合成する平坦な条件を編集する。
 *    各節は metric(path/index/agg)・時間派生(mode/window)・比較(compare/target または
 *    target_metric)を持つ。metric.path は city オブジェクトのプロパティ名を直接入力する
 *    自由入力欄であり、バックエンド側でも存在チェックは行われない(シナリオ作成者向けの
 *    上級者機能のため)。
 * 2. JSONモード: condition の木をそのままJSONで編集する。not 演算子や入れ子の合成条件など
 *    フォームで表現できない条件はこちらで編集する。既存条件に入れ子が含まれる場合は
 *    自動的にJSONモードで開く。
 *
 * 条件以外にも、ライフサイクル(latch/notify/期限/先行クエスト)・報酬(交付金)・
 * 表示メタ(tier/category/hidden)を編集できる。仕様の正は
 * genesis-docs/docs/economics/24_達成クエスト.md。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import type {
  QuestClause,
  QuestClauseMode,
  QuestCompareOp,
  QuestCondition,
  QuestNotify,
  QuestTier,
} from "../../types";
import "./QuestEditPanel.css";

type QuestEditPanelProps = {
  apiBaseUrl: string;
  onClose: () => void;
  editQuestId?: number;
};

const COMPARE_OPERATORS: QuestCompareOp[] = [">", "<", ">=", "<=", "==", "!="];
const MODES: QuestClauseMode[] = ["value", "delta", "delta_pct", "moving_avg", "streak"];
const AGGS = ["", "sum", "mean", "max", "min", "count"] as const;
const NOTIFY_OPTIONS: QuestNotify[] = ["modal", "toast", "off"];
const TIER_OPTIONS: QuestTier[] = ["bronze", "silver", "gold"];

/** 条件節1件分のフォーム入力値(文字列のまま保持し、送信時に数値へ変換する) */
type ClauseForm = {
  path: string;
  index: string;
  agg: string;
  mode: QuestClauseMode;
  window: string;
  compare: QuestCompareOp;
  target: string;
  /** 比較右辺を別メトリクスにするか(「教育予算 >= 消防予算」等) */
  useTargetMetric: boolean;
  targetMetricPath: string;
};

const EMPTY_CLAUSE: ClauseForm = {
  path: "",
  index: "",
  agg: "",
  mode: "value",
  window: "3",
  compare: ">=",
  target: "",
  useTargetMetric: false,
  targetMetricPath: "",
};

/** クエスト全体のフォーム入力値 */
type QuestFormState = {
  name: string;
  description: string;
  conditionOp: "all" | "any";
  clauses: ClauseForm[];
  /** JSONモード時の condition のJSON文字列 */
  conditionJson: string;
  jsonMode: boolean;
  latch: boolean;
  notify: QuestNotify;
  tier: QuestTier;
  category: string;
  hidden: boolean;
  deadlineEpoch: string;
  deadlineWithin: string;
  prerequisites: string;
  rewardEnabled: boolean;
  rewardAmount: string;
  is_force_completable: boolean;
};

const EMPTY_FORM: QuestFormState = {
  name: "",
  description: "",
  conditionOp: "all",
  clauses: [{ ...EMPTY_CLAUSE }],
  conditionJson: "",
  jsonMode: false,
  latch: true,
  notify: "modal",
  tier: "bronze",
  category: "",
  hidden: false,
  deadlineEpoch: "",
  deadlineWithin: "",
  prerequisites: "",
  rewardEnabled: false,
  rewardAmount: "",
  is_force_completable: false,
};

/** condition の木に入れ子の合成ノードが含まれるか(含まれる場合フォームでは編集できない) */
function hasNestedCondition(condition: QuestCondition): boolean {
  if (condition.op === "not") return true;
  return condition.clauses.some((c) => "clauses" in c);
}

/** バックエンドの clause をフォーム入力値へ変換する */
function clauseToForm(clause: QuestClause): ClauseForm {
  return {
    path: clause.metric?.path ?? "",
    index: clause.metric?.index != null ? String(clause.metric.index) : "",
    agg: clause.metric?.agg ?? "",
    mode: clause.mode ?? "value",
    window: String(clause.window ?? 3),
    compare: clause.compare ?? ">=",
    target: clause.target != null ? String(clause.target) : "",
    useTargetMetric: clause.target_metric != null,
    targetMetricPath: clause.target_metric?.path ?? "",
  };
}

/** フォーム入力値をバックエンドの clause へ変換する */
function formToClause(form: ClauseForm): QuestClause {
  const clause: QuestClause = {
    metric: {
      path: form.path.trim(),
      index: form.index.trim() !== "" ? form.index.trim() : null,
      agg: form.agg !== "" ? (form.agg as QuestClause["metric"]["agg"]) : null,
    },
    mode: form.mode,
    compare: form.compare,
  };
  if (form.mode === "moving_avg" || form.mode === "streak") {
    clause.window = Number(form.window);
  }
  if (form.useTargetMetric) {
    clause.target = null;
    clause.target_metric = { path: form.targetMetricPath.trim() };
  } else {
    clause.target = Number(form.target);
    clause.target_metric = null;
  }
  return clause;
}

/** 条件節1件分のフォームが送信可能か */
function isClauseValid(form: ClauseForm): boolean {
  if (form.path.trim() === "") return false;
  if (form.useTargetMetric) {
    if (form.targetMetricPath.trim() === "") return false;
  } else {
    if (form.target.trim() === "" || Number.isNaN(Number(form.target))) return false;
  }
  if (form.mode === "moving_avg" || form.mode === "streak") {
    const w = Number(form.window);
    if (!Number.isInteger(w) || w < 1) return false;
  }
  return true;
}

export function QuestEditPanel({ apiBaseUrl, onClose, editQuestId }: QuestEditPanelProps) {
  const { t } = useTranslation();
  const isEditMode = editQuestId != null;
  const base = apiBaseUrl.replace(/\/+$/, "");

  const [form, setForm] = useState<QuestFormState>(EMPTY_FORM);
  const [loading, setLoading] = useState(isEditMode);
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  /** 編集モードの場合、マウント時に既存のクエストデータを取得してフォームに反映する */
  useEffect(() => {
    if (!isEditMode) return;
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    fetch(`${base}/api/v1/quests/${editQuestId}`, { signal: controller.signal })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json();
      })
      .then((data) => {
        if (controller.signal.aborted) return;
        const condition: QuestCondition = data.condition ?? { op: "all", clauses: [] };
        // not 演算子や入れ子の合成条件はフォームで表現できないため、JSONモードで開く
        const nested = hasNestedCondition(condition);
        setForm({
          name: data.name ?? "",
          description: data.description ?? "",
          conditionOp: condition.op === "any" ? "any" : "all",
          clauses: nested
            ? [{ ...EMPTY_CLAUSE }]
            : (condition.clauses as QuestClause[]).map(clauseToForm),
          conditionJson: JSON.stringify(condition, null, 2),
          jsonMode: nested,
          latch: Boolean(data.latch ?? true),
          notify: (data.notify as QuestNotify) ?? "modal",
          tier: (data.tier as QuestTier) ?? "bronze",
          category: data.category ?? "",
          hidden: Boolean(data.hidden),
          deadlineEpoch: data.deadline_epoch != null ? String(data.deadline_epoch) : "",
          deadlineWithin: data.deadline_within != null ? String(data.deadline_within) : "",
          prerequisites: (data.prerequisites ?? []).join(", "),
          rewardEnabled: data.reward?.type === "grant_money",
          rewardAmount: data.reward?.amount != null ? String(data.reward.amount) : "",
          is_force_completable: Boolean(data.is_force_completable),
        });
        setLoading(false);
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        setError(err instanceof Error ? err.message : "Unknown error");
        setLoading(false);
      });

    return () => controller.abort();
  }, [base, editQuestId, isEditMode]);

  function updateField<K extends keyof QuestFormState>(key: K, value: QuestFormState[K]) {
    setForm((prev) => ({ ...prev, [key]: value }));
  }

  function updateClause(index: number, patch: Partial<ClauseForm>) {
    setForm((prev) => ({
      ...prev,
      clauses: prev.clauses.map((c, i) => (i === index ? { ...c, ...patch } : c)),
    }));
  }

  function addClause() {
    setForm((prev) => ({ ...prev, clauses: [...prev.clauses, { ...EMPTY_CLAUSE }] }));
  }

  function removeClause(index: number) {
    setForm((prev) => ({
      ...prev,
      clauses: prev.clauses.filter((_, i) => i !== index),
    }));
  }

  /** フォームモード ⇔ JSONモードの切り替え。フォーム→JSONは現在の内容をJSON化して引き継ぐ */
  function toggleJsonMode(enabled: boolean) {
    if (enabled) {
      const condition: QuestCondition = {
        op: form.conditionOp,
        clauses: form.clauses.filter(isClauseValid).map(formToClause),
      };
      setForm((prev) => ({
        ...prev,
        jsonMode: true,
        conditionJson: JSON.stringify(condition, null, 2),
      }));
    } else {
      // JSON→フォームは入れ子が無い場合のみ内容を引き継ぐ
      try {
        const condition = JSON.parse(form.conditionJson) as QuestCondition;
        if (!hasNestedCondition(condition)) {
          setForm((prev) => ({
            ...prev,
            jsonMode: false,
            conditionOp: condition.op === "any" ? "any" : "all",
            clauses: (condition.clauses as QuestClause[]).map(clauseToForm),
          }));
          return;
        }
      } catch {
        // パース不能・入れ子ありの場合はフォーム内容を維持したまま切り替える
      }
      setForm((prev) => ({ ...prev, jsonMode: false }));
    }
  }

  /** condition を組み立てる。JSONモードではパース結果、フォームモードでは節リストから */
  function buildCondition(): QuestCondition | null {
    if (form.jsonMode) {
      try {
        return JSON.parse(form.conditionJson) as QuestCondition;
      } catch {
        return null;
      }
    }
    return {
      op: form.conditionOp,
      clauses: form.clauses.map(formToClause),
    };
  }

  const conditionValid = form.is_force_completable
    ? true
    : form.jsonMode
      ? (() => {
          try {
            JSON.parse(form.conditionJson);
            return true;
          } catch {
            return false;
          }
        })()
      : form.clauses.length > 0 && form.clauses.every(isClauseValid);

  const prerequisitesValid =
    form.prerequisites.trim() === "" ||
    form.prerequisites.split(",").every((s) => /^\s*\d+\s*$/.test(s));

  const isFormValid =
    form.name.trim() !== "" &&
    form.description.trim() !== "" &&
    conditionValid &&
    prerequisitesValid &&
    (!form.rewardEnabled || (form.rewardAmount.trim() !== "" && Number(form.rewardAmount) > 0)) &&
    (form.deadlineEpoch.trim() === "" || Number(form.deadlineEpoch) >= 1) &&
    (form.deadlineWithin.trim() === "" || Number(form.deadlineWithin) >= 1);

  /** 確定処理: 新規作成モードは POST、編集モードは PATCH を呼ぶ */
  async function handleConfirm() {
    if (!isFormValid || saving) return;
    setSaving(true);
    setError(null);

    const body: Record<string, unknown> = {
      name: form.name,
      description: form.description,
      latch: form.latch,
      notify: form.notify,
      tier: form.tier,
      category: form.category,
      hidden: form.hidden,
      deadline_epoch: form.deadlineEpoch.trim() !== "" ? Number(form.deadlineEpoch) : null,
      deadline_within: form.deadlineWithin.trim() !== "" ? Number(form.deadlineWithin) : null,
      prerequisites:
        form.prerequisites.trim() !== ""
          ? form.prerequisites.split(",").map((s) => Number(s.trim()))
          : [],
      reward: form.rewardEnabled
        ? { type: "grant_money", amount: Number(form.rewardAmount) }
        : null,
      is_force_completable: form.is_force_completable,
    };
    if (!form.is_force_completable) {
      body.condition = buildCondition();
    }

    const url = isEditMode ? `${base}/api/v1/quests/${editQuestId}` : `${base}/api/v1/quests`;
    const method = isEditMode ? "PATCH" : "POST";

    try {
      const res = await fetch(url, {
        method,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      if (!res.ok) {
        const bodyText = await res.text().catch(() => "");
        throw new Error(`HTTP ${res.status}: ${bodyText}`);
      }
      onClose();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : "Unknown error");
      setSaving(false);
    }
  }

  /** 削除処理(編集モードのみ)。確認ダイアログの後に DELETE を呼ぶ */
  async function handleDelete() {
    if (!isEditMode || deleting) return;
    const confirmed = window.confirm(t("achievementQuests.deleteConfirm"));
    if (!confirmed) return;

    setDeleting(true);
    setError(null);
    try {
      const res = await fetch(`${base}/api/v1/quests/${editQuestId}`, { method: "DELETE" });
      if (!res.ok) {
        const bodyText = await res.text().catch(() => "");
        throw new Error(`HTTP ${res.status}: ${bodyText}`);
      }
      onClose();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : "Unknown error");
      setDeleting(false);
    }
  }

  return (
    <div className="quest-edit">
      <h2 className="quest-edit-title">
        {isEditMode ? t("achievementQuests.editTitle") : t("achievementQuests.createTitle")}
      </h2>

      {error && <div className="quest-edit-error">{error}</div>}

      {loading ? (
        <p className="quest-edit-loading">{t("common.loading")}</p>
      ) : (
        <>
          <div className="quest-edit-fields">
            {/* ── 基本情報 ── */}
            <label className="quest-edit-field">
              <span className="quest-edit-field-label">{t("achievementQuests.fieldName")}</span>
              <input
                className="quest-edit-input"
                type="text"
                value={form.name}
                onChange={(e) => updateField("name", e.target.value)}
              />
            </label>

            <label className="quest-edit-field">
              <span className="quest-edit-field-label">{t("achievementQuests.fieldDescription")}</span>
              <textarea
                className="quest-edit-textarea"
                rows={2}
                value={form.description}
                onChange={(e) => updateField("description", e.target.value)}
              />
            </label>

            <label className="quest-edit-checkbox-label">
              <input
                type="checkbox"
                checked={form.is_force_completable}
                onChange={(e) => updateField("is_force_completable", e.target.checked)}
              />
              <span>{t("achievementQuests.fieldForceCompletable")}</span>
            </label>

            {/* ── 判定条件(強制完了型では非表示) ── */}
            {!form.is_force_completable && (
              <fieldset className="quest-edit-group">
                <legend className="quest-edit-group-legend">
                  {t("achievementQuests.groupCondition")}
                  <label className="quest-edit-json-toggle">
                    <input
                      type="checkbox"
                      checked={form.jsonMode}
                      onChange={(e) => toggleJsonMode(e.target.checked)}
                    />
                    <span>{t("achievementQuests.jsonMode")}</span>
                  </label>
                </legend>

                {form.jsonMode ? (
                  <>
                    <textarea
                      className="quest-edit-textarea quest-edit-json"
                      rows={12}
                      value={form.conditionJson}
                      onChange={(e) => updateField("conditionJson", e.target.value)}
                      spellCheck={false}
                    />
                    <span className="quest-edit-field-hint">{t("achievementQuests.jsonHint")}</span>
                  </>
                ) : (
                  <>
                    {form.clauses.length > 1 && (
                      <label className="quest-edit-field quest-edit-field-op">
                        <span className="quest-edit-field-label">{t("achievementQuests.fieldConditionOp")}</span>
                        <select
                          className="quest-edit-select"
                          value={form.conditionOp}
                          onChange={(e) => updateField("conditionOp", e.target.value as "all" | "any")}
                        >
                          <option value="all">{t("achievementQuests.opAll")}</option>
                          <option value="any">{t("achievementQuests.opAny")}</option>
                        </select>
                      </label>
                    )}

                    {form.clauses.map((clause, i) => (
                      <div key={i} className="quest-edit-clause">
                        <div className="quest-edit-clause-header">
                          <span className="quest-edit-clause-no">#{i + 1}</span>
                          {form.clauses.length > 1 && (
                            <button
                              className="quest-edit-clause-remove"
                              type="button"
                              onClick={() => removeClause(i)}
                              title={t("achievementQuests.removeClause")}
                            >
                              &#10005;
                            </button>
                          )}
                        </div>

                        <label className="quest-edit-field">
                          <span className="quest-edit-field-label">{t("achievementQuests.fieldKey")}</span>
                          <input
                            className="quest-edit-input"
                            type="text"
                            placeholder={t("achievementQuests.fieldKeyPlaceholder")}
                            value={clause.path}
                            onChange={(e) => updateClause(i, { path: e.target.value })}
                          />
                          <span className="quest-edit-field-hint">{t("achievementQuests.fieldKeyHint")}</span>
                        </label>

                        <div className="quest-edit-field-row">
                          <label className="quest-edit-field">
                            <span className="quest-edit-field-label">{t("achievementQuests.fieldIndex")}</span>
                            <input
                              className="quest-edit-input"
                              type="text"
                              placeholder={t("achievementQuests.fieldIndexPlaceholder")}
                              value={clause.index}
                              onChange={(e) => updateClause(i, { index: e.target.value })}
                            />
                          </label>
                          <label className="quest-edit-field">
                            <span className="quest-edit-field-label">{t("achievementQuests.fieldAgg")}</span>
                            <select
                              className="quest-edit-select"
                              value={clause.agg}
                              onChange={(e) => updateClause(i, { agg: e.target.value })}
                            >
                              {AGGS.map((agg) => (
                                <option key={agg} value={agg}>
                                  {agg === "" ? t("achievementQuests.aggNone") : agg}
                                </option>
                              ))}
                            </select>
                          </label>
                        </div>

                        <div className="quest-edit-field-row">
                          <label className="quest-edit-field">
                            <span className="quest-edit-field-label">{t("achievementQuests.fieldMode")}</span>
                            <select
                              className="quest-edit-select"
                              value={clause.mode}
                              onChange={(e) => updateClause(i, { mode: e.target.value as QuestClauseMode })}
                            >
                              {MODES.map((mode) => (
                                <option key={mode} value={mode}>
                                  {t(`achievementQuests.mode.${mode}`)}
                                </option>
                              ))}
                            </select>
                          </label>
                          {(clause.mode === "moving_avg" || clause.mode === "streak") && (
                            <label className="quest-edit-field">
                              <span className="quest-edit-field-label">{t("achievementQuests.fieldWindow")}</span>
                              <input
                                className="quest-edit-input"
                                type="number"
                                min={1}
                                value={clause.window}
                                onChange={(e) => updateClause(i, { window: e.target.value })}
                              />
                            </label>
                          )}
                        </div>

                        <div className="quest-edit-field-row">
                          <label className="quest-edit-field quest-edit-field-compare">
                            <span className="quest-edit-field-label">{t("achievementQuests.fieldCompare")}</span>
                            <select
                              className="quest-edit-select"
                              value={clause.compare}
                              onChange={(e) => updateClause(i, { compare: e.target.value as QuestCompareOp })}
                            >
                              {COMPARE_OPERATORS.map((op) => (
                                <option key={op} value={op}>
                                  {op}
                                </option>
                              ))}
                            </select>
                          </label>

                          {clause.useTargetMetric ? (
                            <label className="quest-edit-field quest-edit-field-target">
                              <span className="quest-edit-field-label">{t("achievementQuests.fieldTargetMetric")}</span>
                              <input
                                className="quest-edit-input"
                                type="text"
                                placeholder={t("achievementQuests.fieldKeyPlaceholder")}
                                value={clause.targetMetricPath}
                                onChange={(e) => updateClause(i, { targetMetricPath: e.target.value })}
                              />
                            </label>
                          ) : (
                            <label className="quest-edit-field quest-edit-field-target">
                              <span className="quest-edit-field-label">{t("achievementQuests.fieldTarget")}</span>
                              <input
                                className="quest-edit-input"
                                type="number"
                                value={clause.target}
                                onChange={(e) => updateClause(i, { target: e.target.value })}
                              />
                            </label>
                          )}
                        </div>

                        <label className="quest-edit-checkbox-label">
                          <input
                            type="checkbox"
                            checked={clause.useTargetMetric}
                            onChange={(e) => updateClause(i, { useTargetMetric: e.target.checked })}
                          />
                          <span>{t("achievementQuests.fieldUseTargetMetric")}</span>
                        </label>
                      </div>
                    ))}

                    <button className="quest-edit-add-clause" type="button" onClick={addClause}>
                      {t("achievementQuests.addClause")}
                    </button>
                  </>
                )}
              </fieldset>
            )}

            {/* ── ライフサイクル ── */}
            <fieldset className="quest-edit-group">
              <legend className="quest-edit-group-legend">{t("achievementQuests.groupLifecycle")}</legend>

              <label className="quest-edit-checkbox-label">
                <input
                  type="checkbox"
                  checked={form.latch}
                  onChange={(e) => updateField("latch", e.target.checked)}
                />
                <span>{t("achievementQuests.fieldOnce")}</span>
              </label>

              <label className="quest-edit-field">
                <span className="quest-edit-field-label">{t("achievementQuests.fieldNotify")}</span>
                <select
                  className="quest-edit-select"
                  value={form.notify}
                  onChange={(e) => updateField("notify", e.target.value as QuestNotify)}
                >
                  {NOTIFY_OPTIONS.map((n) => (
                    <option key={n} value={n}>
                      {t(`achievementQuests.notify.${n}`)}
                    </option>
                  ))}
                </select>
              </label>

              <div className="quest-edit-field-row">
                <label className="quest-edit-field">
                  <span className="quest-edit-field-label">{t("achievementQuests.fieldDeadlineEpoch")}</span>
                  <input
                    className="quest-edit-input"
                    type="number"
                    min={1}
                    placeholder={t("achievementQuests.noDeadline")}
                    value={form.deadlineEpoch}
                    onChange={(e) => updateField("deadlineEpoch", e.target.value)}
                  />
                </label>
                <label className="quest-edit-field">
                  <span className="quest-edit-field-label">{t("achievementQuests.fieldDeadlineWithin")}</span>
                  <input
                    className="quest-edit-input"
                    type="number"
                    min={1}
                    placeholder={t("achievementQuests.noDeadline")}
                    value={form.deadlineWithin}
                    onChange={(e) => updateField("deadlineWithin", e.target.value)}
                  />
                </label>
              </div>

              <label className="quest-edit-field">
                <span className="quest-edit-field-label">{t("achievementQuests.fieldPrerequisites")}</span>
                <input
                  className="quest-edit-input"
                  type="text"
                  placeholder={t("achievementQuests.fieldPrerequisitesPlaceholder")}
                  value={form.prerequisites}
                  onChange={(e) => updateField("prerequisites", e.target.value)}
                />
                <span className="quest-edit-field-hint">{t("achievementQuests.fieldPrerequisitesHint")}</span>
              </label>
            </fieldset>

            {/* ── 報酬 ── */}
            <fieldset className="quest-edit-group">
              <legend className="quest-edit-group-legend">{t("achievementQuests.groupReward")}</legend>
              <label className="quest-edit-checkbox-label">
                <input
                  type="checkbox"
                  checked={form.rewardEnabled}
                  onChange={(e) => updateField("rewardEnabled", e.target.checked)}
                />
                <span>{t("achievementQuests.fieldRewardEnabled")}</span>
              </label>
              {form.rewardEnabled && (
                <label className="quest-edit-field">
                  <span className="quest-edit-field-label">{t("achievementQuests.fieldRewardAmount")}</span>
                  <input
                    className="quest-edit-input"
                    type="number"
                    min={1}
                    value={form.rewardAmount}
                    onChange={(e) => updateField("rewardAmount", e.target.value)}
                  />
                </label>
              )}
            </fieldset>

            {/* ── 表示メタ ── */}
            <fieldset className="quest-edit-group">
              <legend className="quest-edit-group-legend">{t("achievementQuests.groupDisplay")}</legend>
              <div className="quest-edit-field-row">
                <label className="quest-edit-field">
                  <span className="quest-edit-field-label">{t("achievementQuests.fieldTier")}</span>
                  <select
                    className="quest-edit-select"
                    value={form.tier}
                    onChange={(e) => updateField("tier", e.target.value as QuestTier)}
                  >
                    {TIER_OPTIONS.map((tier) => (
                      <option key={tier} value={tier}>
                        {t(`achievements.tier.${tier}`)}
                      </option>
                    ))}
                  </select>
                </label>
                <label className="quest-edit-field">
                  <span className="quest-edit-field-label">{t("achievementQuests.fieldCategory")}</span>
                  <input
                    className="quest-edit-input"
                    type="text"
                    value={form.category}
                    onChange={(e) => updateField("category", e.target.value)}
                  />
                </label>
              </div>
              <label className="quest-edit-checkbox-label">
                <input
                  type="checkbox"
                  checked={form.hidden}
                  onChange={(e) => updateField("hidden", e.target.checked)}
                />
                <span>{t("achievementQuests.fieldHidden")}</span>
              </label>
            </fieldset>
          </div>

          <div className="quest-edit-actions">
            {isEditMode && (
              <button
                className="quest-edit-btn quest-edit-btn--delete"
                type="button"
                disabled={deleting || saving}
                onClick={handleDelete}
              >
                {deleting ? t("achievementQuests.deleting") : t("achievementQuests.delete")}
              </button>
            )}
            <button
              className="quest-edit-btn quest-edit-btn--confirm"
              type="button"
              disabled={!isFormValid || saving || deleting}
              onClick={handleConfirm}
            >
              {saving ? t("achievementQuests.saving") : t("achievementQuests.confirm")}
            </button>
          </div>
        </>
      )}
    </div>
  );
}
