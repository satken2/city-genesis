/**
 * Achievement quest editor
 *
 * Dedicated panel for the window_type="quest_edit" popup.
 * Create, edit, and delete achievement quests only in scenario authoring mode (play_mode=PLAY_MODE_SCENARIO).
 *
 * - Without editQuestId: creation mode. Confirm calls POST /api/v1/quests.
 * - With editQuestId: edit mode. On mount, GET /api/v1/quests/{id} retrieves the current quest into the form;
 *   Confirm calls PATCH /api/v1/quests/{id}.
 *   This mode also shows a Delete button that calls DELETE /api/v1/quests/{id}.
 *
 * ## Two condition editor modes
 *
 * 1. Form mode: edit flat conditions combining a list of clauses with all / any.
 *    Each clause has a metric (path/index/agg), temporal derivation (mode/window), and comparison
 *    (compare/target or target_metric).
 *    metric.path is a free-text field for a property path in the city object. The backend does not check whether
 *    it exists, since this is an advanced feature for scenario authors.
 * 2. JSON mode: edit the condition tree directly as JSON. Use this for the not operator and nested combinations
 *    that the form cannot represent. Existing nested conditions open in JSON mode automatically.
 *
 * Also edits lifecycle settings (latch/notify/deadline/prerequisite quests), rewards (grants), and display
 * metadata (category/hidden).
 * For achievement quest concepts, see city-genesis/docs/ja/economics/16_スコアと達成目標.md.
 *
 * Names and descriptions use multilingual text (LocalizedTextFields), with English required for both.
 * English is the fallback when the display language has no text, and the core also rejects text without English.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import type {
  QuestClause,
  QuestClauseMode,
  QuestCompareOp,
  QuestCondition,
  QuestNotify,
} from "../../types";
import { requestConfirm } from "../../utils/confirmDialog";
import { compactLocalizedText, missingEnglish, type LocalizedText } from "../../utils/locale";
import { LocalizedTextFields } from "../common/LocalizedTextFields";
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

/** Form values for one condition clause (kept as strings and converted to numbers on submission) */
type ClauseForm = {
  path: string;
  index: string;
  agg: string;
  mode: QuestClauseMode;
  window: string;
  compare: QuestCompareOp;
  target: string;
  /** Whether the right-hand side uses another metric (e.g. education budget >= fire protection budget) */
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

/** Form values for the entire quest */
type QuestFormState = {
  name: LocalizedText;
  description: LocalizedText;
  conditionOp: "all" | "any";
  clauses: ClauseForm[];
  /** JSON string for condition in JSON mode */
  conditionJson: string;
  jsonMode: boolean;
  latch: boolean;
  notify: QuestNotify;
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
  name: {},
  description: {},
  conditionOp: "all",
  clauses: [{ ...EMPTY_CLAUSE }],
  conditionJson: "",
  jsonMode: false,
  latch: true,
  notify: "modal",
  category: "",
  hidden: false,
  deadlineEpoch: "",
  deadlineWithin: "",
  prerequisites: "",
  rewardEnabled: false,
  rewardAmount: "",
  is_force_completable: false,
};

/** Whether the condition tree contains nested combination nodes (which the form cannot edit) */
function hasNestedCondition(condition: QuestCondition): boolean {
  if (condition.op === "not") return true;
  return condition.clauses.some((c) => "clauses" in c);
}

/** Convert a backend clause to form values. */
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

/** Convert form values to a backend clause. */
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

/** Whether the form for one condition clause can be submitted */
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

  /** In edit mode, retrieve the existing quest on mount and populate the form. */
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
        // Open in JSON mode because the form cannot represent not or nested combinations.
        const nested = hasNestedCondition(condition);
        setForm({
          name: data.name ?? {},
          description: data.description ?? {},
          conditionOp: condition.op === "any" ? "any" : "all",
          clauses: nested
            ? [{ ...EMPTY_CLAUSE }]
            : (condition.clauses as QuestClause[]).map(clauseToForm),
          conditionJson: JSON.stringify(condition, null, 2),
          jsonMode: nested,
          latch: Boolean(data.latch ?? true),
          notify: (data.notify as QuestNotify) ?? "modal",
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

  /** Switch between form and JSON modes. Form to JSON serializes the current form values. */
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
      // JSON to form carries over values only when there is no nesting.
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
        // If parsing fails or nesting exists, switch while retaining the form values.
      }
      setForm((prev) => ({ ...prev, jsonMode: false }));
    }
  }

  /** Build condition from the parsed JSON in JSON mode, or from the clause list in form mode. */
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

  const textValid = !missingEnglish(form.name, true) && !missingEnglish(form.description, true);
  const isFormValid =
    textValid &&
    conditionValid &&
    prerequisitesValid &&
    (!form.rewardEnabled || (form.rewardAmount.trim() !== "" && Number(form.rewardAmount) > 0)) &&
    (form.deadlineEpoch.trim() === "" || Number(form.deadlineEpoch) >= 1) &&
    (form.deadlineWithin.trim() === "" || Number(form.deadlineWithin) >= 1);

  /** Confirm: call POST in creation mode or PATCH in edit mode. */
  async function handleConfirm() {
    if (!isFormValid || saving) return;
    setSaving(true);
    setError(null);

    const body: Record<string, unknown> = {
      name: compactLocalizedText(form.name),
      description: compactLocalizedText(form.description),
      latch: form.latch,
      notify: form.notify,
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

  /** Delete (edit mode only): call DELETE after a confirmation dialog. */
  async function handleDelete() {
    if (!isEditMode || deleting) return;
    const confirmed = await requestConfirm({
      titleKey: "achievements.deleteQuest",
      message: t("achievementQuests.deleteConfirm"),
      confirmLabel: t("common.delete"),
      tone: "danger",
    });
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
            {/* ── Basic information ── */}
            <LocalizedTextFields
              value={{ name: form.name, description: form.description }}
              onChange={(value) => setForm((prev) => ({ ...prev, name: value.name ?? {}, description: value.description ?? {} }))}
              fields={[
                { key: "name", label: t("achievementQuests.fieldName"), maxLength: 200 },
                { key: "description", label: t("achievementQuests.fieldDescription"), maxLength: 2000, multiline: true },
              ]}
            />
            {!textValid && <span className="quest-edit-field-hint">{t("achievementQuests.englishRequired")}</span>}

            <label className="quest-edit-checkbox-label">
              <input
                type="checkbox"
                checked={form.is_force_completable}
                onChange={(e) => updateField("is_force_completable", e.target.checked)}
              />
              <span>{t("achievementQuests.fieldForceCompletable")}</span>
            </label>

            {/* ── Completion conditions (hidden for forced completion) ── */}
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

            {/* ── Lifecycle ── */}
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

            {/* ── Rewards ── */}
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

            {/* ── Display metadata ── */}
            <fieldset className="quest-edit-group">
              <legend className="quest-edit-group-legend">{t("achievementQuests.groupDisplay")}</legend>
              <label className="quest-edit-field">
                <span className="quest-edit-field-label">{t("achievementQuests.fieldCategory")}</span>
                <input
                  className="quest-edit-input"
                  type="text"
                  value={form.category}
                  onChange={(e) => updateField("category", e.target.value)}
                />
              </label>
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
