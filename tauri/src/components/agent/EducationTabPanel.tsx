/**
 * 教育タブコンポーネント
 *
 * 経済主体がTYPE_EDUCATIONの場合に「教育」タブとして表示される詳細パネル。
 * 上から順に以下を表示する:
 * 1. 在籍サマリ(在籍生徒数/総定員のドーナツグラフ+数値)
 * 2. スキル別の「在籍 / 定員」一覧(enrolled_by_skill × education_type)
 * 3. 受け入れ枠の設定フォーム(高等教育=is_higher_educationのバリアントのみ。
 *    スキル別の配分重みを入力し、按分後の定員プレビューを見ながら保存できる)
 * 4. 生徒リスト(CitizenLink。多人数校のDOM肥大を避けるため初期表示は50件)
 *
 * 在籍数(enrolled_by_skill)と総定員(max_students)はGETレスポンスへの手動注入
 * フィールドで、PATCHレスポンスには含まれない。そのため保存成功時は親の
 * refreshKeyをバンプ(onSaved)してGETで再取得する(PATCH応答をsetAgentDataしない)。
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

/** スキル別表示の並び順(統計ウィンドウのBusinessTabと同じ定義順) */
const SKILL_ORDER = [
  SKILL_BASE,
  SKILL_PHYSICAL,
  SKILL_OFFICE,
  SKILL_RETAIL,
  SKILL_TECHNOLOGY,
  SKILL_MEDICAL,
];

/** 高等教育の受け入れ枠として設定可能なスキル(SKILL_BASEは基礎教育専用のため除外) */
const EDITABLE_SKILLS = [
  SKILL_PHYSICAL,
  SKILL_OFFICE,
  SKILL_RETAIL,
  SKILL_TECHNOLOGY,
  SKILL_MEDICAL,
];

/** 生徒リストの初期表示件数(大学は数百人規模になり得るため上限を設ける) */
const INITIAL_STUDENT_DISPLAY = 50;

const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "11px",
};

/**
 * 最大剰余法による按分(バックエンド education_util.calc_education_capacity と
 * 同一規則)。floorした後、小数部が大きい順(同値はスキルID昇順)に余りを1ずつ配る。
 * 保存前の「按分後定員プレビュー」の計算に使う。
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
  /** 受け入れ枠の保存成功時に呼ばれる(親がrefreshKeyをバンプしてGET再取得する) */
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

  // 在籍生徒数。students(生徒リスト)を正とし、未取得時はスキル別集計の合計で代替する
  const totalEnrolled =
    agent.students != null
      ? students.length
      : Object.values(enrolledBySkill).reduce((sum, v) => sum + v, 0);
  const vacantCount = Math.max(0, maxStudents - totalEnrolled);

  // 高等教育(大学)のみ受け入れ枠を編集できる(基礎校はSKILL_BASE単枠で編集不可)
  const isHigherEducation =
    masterData?.agents[agent.variant]?.is_higher_education === true;

  // ---- 受け入れ枠 編集フォームの状態 ----
  // 入力値は文字列のまま保持する(入力途中の空欄を許容するため)。
  // 初期値は現在のeducation_type(=按分後の実定員なので、初期プレビューが現状と一致する)
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
  // 生徒リストの折りたたみ(初期はINITIAL_STUDENT_DISPLAY件のみ表示)
  const [showAllStudents, setShowAllStudents] = useState(false);

  // 別の施設へ遷移した/再取得したときにフォームを現在値へ同期し直す
  useEffect(() => {
    setFormValues(buildFormValues());
    setSaveError(null);
    setShowAllStudents(false);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [agent.agent_id, agent.education_type]);

  // 入力値のパース(空欄=0)。負数・非整数はバリデーションエラーとする
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

  // 按分後定員のライブプレビュー(総定員は延床面積由来で変わらない)
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
      // PATCH応答にはenrolled_by_skill/max_studentsが注入されないため、
      // 応答は使わず親にGET再取得を依頼する
      onSaved();
    } catch (err) {
      console.error("Failed to update education_type:", err);
      setSaveError(t("agent.education.saveFailed", "保存に失敗しました"));
    } finally {
      setSaving(false);
    }
  };

  // ---- 生徒リストの折りたたみ ----
  const visibleStudents = showAllStudents
    ? students
    : students.slice(0, INITIAL_STUDENT_DISPLAY);
  const hiddenStudentCount = students.length - visibleStudents.length;

  // 定員が設定されているスキルのみ、定義順で表示する(BusinessTabと同じ規則)
  const displaySkills = SKILL_ORDER.filter(
    (sk) => (capacityBySkill[String(sk)] ?? 0) > 0
  );

  const peopleUnit = t("education.people", "人");

  // ドーナツグラフ(在籍/空き枠)。雇用情報パネルの雇用状況グラフと同じ意匠
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
        {/* 1. 在籍サマリ */}
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

        {/* 2. スキル別 在籍/定員 */}
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

        {/* 3. 受け入れ枠の設定(高等教育のみ) */}
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

        {/* 4. 生徒リスト */}
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
