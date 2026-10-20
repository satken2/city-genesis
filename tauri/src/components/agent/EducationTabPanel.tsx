/**
 * Education tab component
 *
 * Detail panel shown as the "Education" tab for TYPE_EDUCATION agents, in this order:
 * 1. Enrollment summary (enrolled students/total capacity donut chart and numbers).
 * 2. Enrollment/capacity by skill (enrolled_by_skill × education_type).
 * 3. Admission capacity settings (higher education variants, is_higher_education, only).
 *    Enter allocation weights by skill, preview allocated capacities, and save.
 * 4. Student list (CitizenLink; initially 50 entries to avoid DOM growth in large schools).
 *
 * Enrollment (enrolled_by_skill) and total capacity (max_students) are manually injected into GET
 * responses and absent from PATCH responses. On save success, bump the parent's refreshKey
 * (onSaved) to refetch via GET; do not pass the PATCH response to setAgentData.
 */

import { Fragment, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import type { AgentData, AgentResponse } from "../../types";
import { useMasterData } from "../../contexts/MasterDataContext";
import { CitizenLink } from "../common/CitizenLink";
import { buildApiUrl, fetchApi } from "../../utils/api";
import {
  SKILL_BASE,
  SKILL_PHYSICAL,
  SKILL_OFFICE,
  SKILL_RETAIL,
  SKILL_TECHNOLOGY,
  SKILL_MEDICAL,
} from "../../constants";
import { PieChart, Pie, Cell, ResponsiveContainer, Tooltip } from "recharts";
import "./EducationTabPanel.css";

/** Skill display order (the same definition order as BusinessTab in the statistics window). */
const SKILL_ORDER = [
  SKILL_BASE,
  SKILL_PHYSICAL,
  SKILL_OFFICE,
  SKILL_RETAIL,
  SKILL_TECHNOLOGY,
  SKILL_MEDICAL,
];

/** Skills available for higher education admission capacity (exclude SKILL_BASE, which is for basic education only). */
const EDITABLE_SKILLS = [
  SKILL_PHYSICAL,
  SKILL_OFFICE,
  SKILL_RETAIL,
  SKILL_TECHNOLOGY,
  SKILL_MEDICAL,
];

/** Initial student list limit (universities may have hundreds of students). */
const INITIAL_STUDENT_DISPLAY = 50;

const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "1rem",
};

/**
 * Allocation by the largest remainder method, following backend education_util.calc_education_capacity.
 * After flooring, distribute the remainder one at a time in descending fractional-part order
 * (ascending skill ID on ties). Used to preview allocated capacity before saving.
 */
function allocateByLargestRemainder(
  weights: Record<number, number>,
  total: number
): Record<number, number> {
  const entries = Object.entries(weights).map(
    ([k, v]) => [Number(k), Math.max(0, Math.floor(v))] as const
  );
  const weightSum = entries.reduce((sum, [, w]) => sum + w, 0);
  if (weightSum <= 0 || total <= 0) {
    return Object.fromEntries(entries.map(([k]) => [k, 0]));
  }
  const quotas = entries.map(
    ([k, w]) => [k, (total * w) / weightSum] as const
  );
  const floored = new Map(quotas.map(([k, q]) => [k, Math.floor(q)]));
  const remainder =
    total - Array.from(floored.values()).reduce((sum, v) => sum + v, 0);
  const order = quotas
    .map(([k, q]) => [k, q - Math.floor(q)] as const)
    .sort((a, b) => b[1] - a[1] || a[0] - b[0])
    .map(([k]) => k);
  for (let i = 0; i < remainder; i += 1) {
    floored.set(order[i], (floored.get(order[i]) ?? 0) + 1);
  }
  return Object.fromEntries(floored);
}

type EducationTabPanelProps = {
  agent: AgentData;
  apiBaseUrl: string;
  pathTemplate: string;
  /** Called after admission capacity is saved successfully (the parent bumps refreshKey and refetches via GET). */
  onSaved: () => void;
};

export function EducationTabPanel({
  agent,
  apiBaseUrl,
  pathTemplate,
  onSaved,
}: EducationTabPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const capacityBySkill: Record<string, number> = agent.education_type ?? {};
  const enrolledBySkill: Record<string, number> = agent.enrolled_by_skill ?? {};
  const students = agent.students ?? [];
  const maxStudents = agent.max_students ?? 0;

  // Enrolled student count. Use students as authoritative; fall back to summed skill counts if not fetched.
  const totalEnrolled =
    agent.students != null
      ? students.length
      : Object.values(enrolledBySkill).reduce((sum, v) => sum + v, 0);
  const vacantCount = Math.max(0, maxStudents - totalEnrolled);

  // Only higher education (universities) allows editing admission capacity; basic schools have one fixed SKILL_BASE allocation.
  const isHigherEducation =
    masterData?.agents[agent.variant]?.is_higher_education === true;

  // ---- Admission capacity edit form state ----
  // Keep input values as strings to allow temporarily blank fields during editing.
  // Initialize from current education_type (actual allocated capacities), so the initial preview matches the current state.
  const buildFormValues = () => {
    const values: Record<number, string> = {};
    for (const sk of EDITABLE_SKILLS) {
      values[sk] = String(capacityBySkill[String(sk)] ?? 0);
    }
    return values;
  };
  const [formValues, setFormValues] = useState<Record<number, string>>(
    buildFormValues
  );
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  // Student list collapse state (initially show only INITIAL_STUDENT_DISPLAY entries).
  const [showAllStudents, setShowAllStudents] = useState(false);

  // Resynchronize the form to current values when navigating to another facility or refetching.
  useEffect(() => {
    setFormValues(buildFormValues());
    setSaveError(null);
    setShowAllStudents(false);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [agent.agent_id, agent.education_type]);

  // Parse inputs (blank=0). Negative and noninteger values are validation errors.
  const parsedWeights = useMemo(() => {
    const weights: Record<number, number> = {};
    let valid = true;
    for (const sk of EDITABLE_SKILLS) {
      const raw = (formValues[sk] ?? "").trim();
      const value = raw === "" ? 0 : Number(raw);
      if (!Number.isInteger(value) || value < 0) {
        valid = false;
        weights[sk] = 0;
      } else {
        weights[sk] = value;
      }
    }
    return { weights, valid };
  }, [formValues]);

  const weightSum = Object.values(parsedWeights.weights).reduce(
    (sum, v) => sum + v,
    0
  );
  const canSave = parsedWeights.valid && weightSum > 0 && !saving;

  // Live preview of allocated capacities (total capacity derives from total floor area and remains fixed).
  const previewAllocation = useMemo(
    () => allocateByLargestRemainder(parsedWeights.weights, maxStudents),
    [parsedWeights, maxStudents]
  );

  const handleSave = async () => {
    if (!canSave) return;
    setSaving(true);
    setSaveError(null);
    const url = buildApiUrl(apiBaseUrl, pathTemplate, String(agent.agent_id));
    try {
      await fetchApi<AgentResponse>(url, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ education_type: parsedWeights.weights }),
      });
      // PATCH responses do not inject enrolled_by_skill/max_students,
      // so discard the response and ask the parent to refetch via GET.
      onSaved();
    } catch (err) {
      console.error("Failed to update education_type:", err);
      setSaveError(t("agent.education.saveFailed", "保存に失敗しました"));
    } finally {
      setSaving(false);
    }
  };

  // ---- Student list collapse ----
  const visibleStudents = showAllStudents
    ? students
    : students.slice(0, INITIAL_STUDENT_DISPLAY);
  const hiddenStudentCount = students.length - visibleStudents.length;

  // Display only skills with capacity set, in definition order (the same rule as BusinessTab).
  const displaySkills = SKILL_ORDER.filter(
    (sk) => (capacityBySkill[String(sk)] ?? 0) > 0
  );

  const peopleUnit = t("education.people", "人");

  // Donut chart (enrolled/vacant slots), matching the employment information panel's employment chart.
  const hasCapacity = maxStudents > 0 || totalEnrolled > 0;
  const chartData = [
    {
      name: t("agent.education.enrolled", "在籍"),
      value: totalEnrolled,
      color: "#10b981",
    },
    {
      name: t("agent.education.vacant", "空き枠"),
      value: vacantCount,
      color: "#374151",
    },
  ];
  const dummyChartData = [
    { name: t("agent.education.noCapacity", "定員なし"), value: 1, color: "#374151" },
  ];

  return (
    <section className="agent-detail-section education-tab-section">
      <div className="education-tab-content">
        {/* 1. Enrollment summary. */}
        <div className="agent-detail-grid">
          <span className="agent-detail-label">
            {t("agent.education.totalStudents", "在籍生徒数")}
          </span>
          <span className="agent-detail-value">
            {totalEnrolled} {peopleUnit}
          </span>
          <span className="agent-detail-label">
            {t("agent.education.totalCapacity", "総定員")}
          </span>
          <span className="agent-detail-value">
            {maxStudents} {peopleUnit}
          </span>
        </div>

        <div className="education-chart-card">
          <div className="education-chart-title">
            {t("agent.education.statusTitle", "在籍状況")}
          </div>
          <div className="education-chart-container">
            <div style={{ width: "80px", height: "80px", flexShrink: 0 }}>
              <ResponsiveContainer width="100%" height="100%">
                <PieChart>
                  <Pie
                    data={hasCapacity ? chartData : dummyChartData}
                    cx="50%"
                    cy="50%"
                    innerRadius={20}
                    outerRadius={38}
                    paddingAngle={hasCapacity ? 3 : 0}
                    dataKey="value"
                  >
                    {(hasCapacity ? chartData : dummyChartData).map(
                      (entry, index) => (
                        <Cell key={`cell-${index}`} fill={entry.color} />
                      )
                    )}
                  </Pie>
                  {hasCapacity && (
                    <Tooltip
                      contentStyle={TOOLTIP_STYLE}
                      formatter={(value: any) => [
                        `${Number(value)} ${peopleUnit}`,
                        t("agent.education.countLabel", "人数"),
                      ]}
                    />
                  )}
                </PieChart>
              </ResponsiveContainer>
            </div>
            <div className="education-legend">
              {(hasCapacity ? chartData : dummyChartData).map((d, index) => (
                <div key={index} className="education-legend-item">
                  <span
                    className="education-legend-bullet"
                    style={{ backgroundColor: d.color }}
                  />
                  <span className="education-legend-label">{d.name}</span>
                </div>
              ))}
            </div>
          </div>
        </div>

        {/* 2. Enrollment/capacity by skill. */}
        {displaySkills.length > 0 && (
          <div className="education-skill-block">
            <span className="education-block-title">
              {t("agent.education.enrollmentBySkill", "スキル別の在籍状況")}
            </span>
            <div className="agent-detail-grid">
              {displaySkills.map((sk) => {
                const skillName =
                  masterData?.skills[sk]?.display_name ?? String(sk);
                const cap = capacityBySkill[String(sk)] ?? 0;
                const enr = enrolledBySkill[String(sk)] ?? 0;
                return (
                  <Fragment key={sk}>
                    <span className="agent-detail-label">{skillName}</span>
                    <span className="agent-detail-value">
                      {enr} / {cap} {peopleUnit}
                    </span>
                  </Fragment>
                );
              })}
            </div>
          </div>
        )}

        {/* 3. Admission capacity settings (higher education only). */}
        {isHigherEducation && (
          <div className="education-settings-block">
            <span className="education-block-title">
              {t("agent.education.settingsTitle", "受け入れ枠の設定")}
            </span>
            <p className="education-settings-hint">
              {t(
                "agent.education.settingsHint",
                "スキルごとの配分重みを設定します。総定員が重みに応じて按分されます。"
              )}
            </p>
            <div className="education-settings-table">
              <div className="education-settings-header">
                <span>{t("agent.education.skillHeader", "スキル")}</span>
                <span>{t("agent.education.weightHeader", "重み")}</span>
                <span>{t("agent.education.previewHeader", "定員")}</span>
              </div>
              {EDITABLE_SKILLS.map((sk) => {
                const skillName =
                  masterData?.skills[sk]?.display_name ?? String(sk);
                return (
                  <div key={sk} className="education-settings-row">
                    <span className="agent-detail-label">{skillName}</span>
                    <input
                      type="number"
                      min={0}
                      step={1}
                      value={formValues[sk] ?? ""}
                      disabled={saving}
                      onChange={(e) =>
                        setFormValues((prev) => ({
                          ...prev,
                          [sk]: e.target.value,
                        }))
                      }
                      className="education-settings-input"
                    />
                    <span className="agent-detail-value">
                      {previewAllocation[sk] ?? 0} {peopleUnit}
                    </span>
                  </div>
                );
              })}
            </div>
            {!saving && parsedWeights.valid && weightSum <= 0 && (
              <p className="education-settings-error">
                {t(
                  "agent.education.sumMustBePositive",
                  "枠の合計は1以上にしてください"
                )}
              </p>
            )}
            {!parsedWeights.valid && (
              <p className="education-settings-error">
                {t(
                  "agent.education.invalidWeight",
                  "重みは0以上の整数で入力してください"
                )}
              </p>
            )}
            {saveError && (
              <p className="education-settings-error">{saveError}</p>
            )}
            <button
              type="button"
              className="education-settings-save"
              disabled={!canSave}
              onClick={() => void handleSave()}
            >
              {saving
                ? t("agent.education.saving", "保存中...")
                : t("agent.education.save", "保存")}
            </button>
          </div>
        )}

        {/* 4. Student list. */}
        <div className="education-students-block">
          <span className="education-block-title">
            {t("agent.education.studentsList", "生徒リスト")} (
            {students.length} {peopleUnit})
          </span>
          <div className="education-students-list">
            {students.length > 0 ? (
              visibleStudents.map((student) => (
                <CitizenLink
                  key={student.id}
                  citizenId={student.id}
                  citizenName={student.name ?? `Citizen #${student.id}`}
                />
              ))
            ) : (
              <span className="agent-detail-muted">
                {t("agent.education.noStudents", "生徒はいません")}
              </span>
            )}
          </div>
          {hiddenStudentCount > 0 && (
            <button
              type="button"
              className="education-students-show-more"
              onClick={() => setShowAllStudents(true)}
            >
              {t("agent.education.showMore", "さらに表示 ({{count}}人)", {
                count: hiddenStudentCount,
              })}
            </button>
          )}
        </div>
      </div>
    </section>
  );
}
