import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import {
  PieChart,
  Pie,
  Cell,
  RadarChart,
  PolarGrid,
  PolarAngleAxis,
  PolarRadiusAxis,
  Radar,
  BarChart,
  Bar,
  XAxis,
  YAxis,
  Tooltip,
  ResponsiveContainer,
} from "recharts";
import { useEpochCachedStats } from "../useEpochCache";
import { UsageMeter } from "./AccidentsTab";
import "./EducationTab.css";
import { useRemPx } from "../../../hooks/useRemPx";

// --- Backend GET /api/v1/stats/education response types ---

type DistributionPoint = {
  bin: string;
  count: number;
};

type SkillCardData = {
  skill_id: number;
  skill_name: string;
  distribution: DistributionPoint[];
  capacity: number;
  enrolled: number;
};

type EducationLevelDistribution = {
  uneducated: number;
  elementary: number;
  higher: number;
};

type SkillAverages = {
  base: number;
  physical: number;
  office: number;
  retail: number;
  technology: number;
  medical: number;
};

type SchoolItem = {
  id: number;
  name: string;
  zone_id: number;
  zone_name: string;
  total_capacity: number;
  enrolled: number;
  utilization_rate: number;
};

type Scope = {
  zone_id: number | null;
  name: string | null;
  education_level_distribution: EducationLevelDistribution;
  skill_averages: SkillAverages;
  skill_cards: SkillCardData[];
  schools: SchoolItem[];
};

type EducationStats = {
  epoch: number;
  scopes: Scope[];
};

// --- Color definitions ---
const COLOR_LEVEL_HIGHER = "#3b82f6";     // Higher education: blue.
const COLOR_LEVEL_ELEMENTARY = "#10b981"; // Primary education: green.
const COLOR_LEVEL_UNEDUCATED = "#ef4444"; // Not completed: red.

const COLOR_RADAR_FILL = "#06b6d4";      // Radar fill: cyan.
const COLOR_RADAR_STROKE = "#22d3ee";    // Radar outline: bright cyan.

const COLOR_DIST_BAR = "#6366f1";        // Distribution chart bars: indigo.

const getBinColor = (binStr: string) => {
  const parts = binStr.split("-");
  if (parts.length === 2) {
    const start = parseFloat(parts[0]);
    const end = parseFloat(parts[1]);
    const mid = (start + end) / 2;
    if (mid < 0.5) {
      return COLOR_LEVEL_UNEDUCATED;
    } else if (mid >= 1.5) {
      return COLOR_LEVEL_HIGHER;
    } else {
      return COLOR_LEVEL_ELEMENTARY;
    }
  }
  return COLOR_DIST_BAR;
};

const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "1rem",
};

export function EducationTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const remPx = useRemPx();
  const { data, loading, error } = useEpochCachedStats<EducationStats>(
    apiBaseUrl,
    "/api/v1/stats/education"
  );

  const [selectedScopeKey, setSelectedScopeKey] = useState("city");

  const scopes = data?.scopes ?? [];

  // Find the Scope selected in the dropdown.
  const currentScope = useMemo(() => {
    if (selectedScopeKey === "city") {
      return scopes.find((s) => s.zone_id === null);
    }
    return scopes.find((s) => String(s.zone_id) === selectedScopeKey);
  }, [scopes, selectedScopeKey]);

  // Open the building (Agent) details window.
  const openAgentDetail = (agentId: number) => {
    void invoke("open_registered_window", {
      windowType: "agent_detail",
      entityId: String(agentId),
    });
  };

  if (loading) {
    return <div className="stats-tab-loading">{t("statistics.loading", "読み込み中...")}</div>;
  }

  if (error || !currentScope) {
    return (
      <div className="stats-tab-error">
        {t("statistics.error", "データの取得に失敗しました。")}
      </div>
    );
  }

  const schools = currentScope.schools ?? [];

  // --- Prepare education level data ---
  const levelData = [
    {
      name: t("statistics.education.uneducated", "未修了"),
      value: currentScope.education_level_distribution.uneducated,
      color: COLOR_LEVEL_UNEDUCATED,
    },
    {
      name: t("statistics.education.elementary", "初等教育"),
      value: currentScope.education_level_distribution.elementary,
      color: COLOR_LEVEL_ELEMENTARY,
    },
    {
      name: t("statistics.education.higher", "高等教育"),
      value: currentScope.education_level_distribution.higher,
      color: COLOR_LEVEL_HIGHER,
    },
  ].filter((d) => d.value > 0);

  // --- Prepare labor Skill radar chart data ---
  const skillRadarData = [
    {
      subject: t("statistics.skills.base", "基礎"),
      value: currentScope.skill_averages.base,
    },
    {
      subject: t("statistics.skills.physical", "肉体"),
      value: currentScope.skill_averages.physical,
    },
    {
      subject: t("statistics.skills.office", "事務"),
      value: currentScope.skill_averages.office,
    },
    {
      subject: t("statistics.skills.retail", "接客"),
      value: currentScope.skill_averages.retail,
    },
    {
      subject: t("statistics.skills.technology", "技術"),
      value: currentScope.skill_averages.technology,
    },
    {
      subject: t("statistics.skills.medical", "医療"),
      value: currentScope.skill_averages.medical,
    },
  ];

  // Display-name mapping by Skill ID.
  const getSkillDisplayName = (name: string) => {
    switch (name) {
      case "base":
        return t("statistics.skills.baseName", "基礎スキル");
      case "physical":
        return t("statistics.skills.physicalName", "肉体労働スキル");
      case "office":
        return t("statistics.skills.officeName", "事務作業スキル");
      case "retail":
        return t("statistics.skills.retailName", "接客スキル");
      case "technology":
        return t("statistics.skills.technologyName", "工学技術スキル");
      case "medical":
        return t("statistics.skills.medicalName", "医療スキル");
      default:
        return name;
    }
  };

  // Render the school list table.
  const renderSchoolTable = () => (
    <section className="edu-schools-list">
      <h3 className="edu-schools-list__title">
        {t("statistics.education.schoolsTitle", "教育機関一覧")}
        <span className="edu-schools-list__count">({schools.length})</span>
      </h3>
      <div className="edu-schools-list__body">
        {schools.length > 0 ? (
          <table className="edu-schools-table">
            <thead>
              <tr>
                <th>{t("statistics.columnAgent", "施設")}</th>
                <th>{t("statistics.columnDistrict", "所在地区")}</th>
                <th>{t("statistics.education.occupancyRate", "充足率")}</th>
              </tr>
            </thead>
            <tbody>
              {schools.map((school) => (
                <tr key={`school-${school.id}`}>
                  <td className="edu-schools-table__cell edu-schools-table__cell--name">
                    <button
                      type="button"
                      className="entity-link"
                      onClick={() => openAgentDetail(school.id)}
                    >
                      {school.name}
                    </button>
                  </td>
                  <td className="edu-schools-table__cell edu-schools-table__cell--muted">
                    {school.zone_name || `地区#${school.zone_id}`}
                  </td>
                  <td className="edu-schools-table__cell edu-schools-table__cell--meter">
                    <UsageMeter usage_ratio={school.utilization_rate} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : (
          <div className="edu-schools-list__empty">
            {t("statistics.education.noSchools", "教育機関がありません")}
          </div>
        )}
      </div>
    </section>
  );

  return (
    <div className="edu-tab">
      {/* Top header (aggregation scope selection) */}
      <header className="edu-tab__header">
        <div className="scope-selector">
          <label htmlFor="edu-scope-select" className="scope-selector__label">
            {t("statistics.scopeLabel", "集計範囲:")}
          </label>
          <select
            id="edu-scope-select"
            className="scope-selector__select"
            value={selectedScopeKey}
            onChange={(e) => setSelectedScopeKey(e.target.value)}
          >
            <option value="city">{t("statistics.cityWide", "都市全体")}</option>
            {scopes
              .filter((s) => s.zone_id !== null)
              .map((s) => (
                <option key={s.zone_id} value={String(s.zone_id)}>
                  {s.name || `${t("statistics.zone", "地区")} ${s.zone_id}`}
                </option>
              ))}
          </select>
        </div>
      </header>

      {/* Main layout (left and right halves) */}
      <div className="edu-tab__body">
        {/* Left half: side-by-side charts (top) + school list (bottom) */}
        <div className="edu-tab__left">
          {/* Education level pie chart + labor Skill radar chart side by side */}
          <div className="edu-tab__charts-row">
            <div className="edu-card edu-card--half">
              <h3 className="edu-card__title">
                {t("statistics.education.levelTitle", "教育水準分布")}
              </h3>
              <div className="edu-card__chart-container-level">
                <ResponsiveContainer width="100%" height="100%">
                  <PieChart>
                    <Pie
                      data={levelData}
                      cx="50%"
                      cy="45%"
                      innerRadius={35}
                      outerRadius={60}
                      paddingAngle={3}
                      dataKey="value"
                    >
                      {levelData.map((entry, index) => (
                        <Cell key={`cell-${index}`} fill={entry.color} />
                      ))}
                    </Pie>
                    <Tooltip
                      contentStyle={TOOLTIP_STYLE}
                      formatter={(value: any) => [
                        `${value} ${t("statistics.people", "人")}`,
                        t("statistics.count", "人数"),
                      ]}
                    />
                  </PieChart>
                </ResponsiveContainer>
                {/* Custom legend */}
                <div className="edu-legend">
                  {levelData.map((d, index) => (
                    <div key={index} className="edu-legend__item">
                      <span
                        className="edu-legend__bullet"
                        style={{ backgroundColor: d.color }}
                      />
                      <span className="edu-legend__label">{d.name}</span>
                      <span className="edu-legend__value">{d.value}人</span>
                    </div>
                  ))}
                </div>
              </div>
            </div>

            <div className="edu-card edu-card--half">
              <h3 className="edu-card__title">
                {t("statistics.education.skillsRadarTitle", "労働スキルレベル (平均値)")}
              </h3>
              <div className="edu-card__chart-container-radar">
                <ResponsiveContainer width="100%" height="100%">
                  <RadarChart cx="50%" cy="50%" outerRadius="75%" data={skillRadarData}>
                    <PolarGrid stroke="#374151" />
                    <PolarAngleAxis
                      dataKey="subject"
                      tick={{ fill: "#9ca3af", fontSize: "0.93rem" }}
                    />
                    <PolarRadiusAxis
                      angle={30}
                      domain={[0, 2.0]}
                      tick={{ fill: "#6b7280", fontSize: "0.74rem" }}
                    />
                    <Radar
                      name="Average Skill"
                      dataKey="value"
                      stroke={COLOR_RADAR_STROKE}
                      fill={COLOR_RADAR_FILL}
                      fillOpacity={0.25}
                    />
                  </RadarChart>
                </ResponsiveContainer>
              </div>
            </div>
          </div>

          {/* School list */}
          <div className="edu-tab__schools">
            {renderSchoolTable()}
          </div>
        </div>

        {/* Right half: two-column tiled cards for each labor Skill */}
        <div className="edu-tab__right">
          {currentScope.skill_cards.map((card) => {
            const hasCapacity = card.capacity > 0;

            return (
              <div key={card.skill_id} className="edu-skill-card">
                <h4 className="edu-skill-card__title">
                  {getSkillDisplayName(card.skill_name)}
                </h4>
                <div className="edu-skill-card__chart-w">
                  <ResponsiveContainer width="100%" height="100%">
                    <BarChart
                      data={card.distribution}
                      layout="vertical"
                      margin={{ top: 2, right: 10, left: -25, bottom: 2 }}
                      barCategoryGap={0}
                    >
                      <XAxis type="number" hide />
                      <YAxis
                        width={remPx(5.56)}
                        dataKey="bin"
                        type="category"
                        tick={{ fill: "#9ca3af", fontSize: "0.74rem" }}
                        axisLine={false}
                        tickLine={false}
                      />
                      <Tooltip
                        contentStyle={TOOLTIP_STYLE}
                        formatter={(value: any) => [
                          `${value} ${t("statistics.people", "人")}`,
                          t("statistics.count", "人数"),
                        ]}
                      />
                      <Bar dataKey="count" radius={[0, 2, 2, 0]}>
                        {card.distribution.map((entry, index) => (
                          <Cell key={`cell-${index}`} fill={getBinColor(entry.bin)} />
                        ))}
                      </Bar>
                    </BarChart>
                  </ResponsiveContainer>
                </div>

                {/* Educational capacity (fulfillment) is shown only as a meter. */}
                <div className="edu-skill-card__capacity">
                  {hasCapacity ? (
                    <>
                      <UsageMeter usage_ratio={card.enrolled / card.capacity} />
                      <span className="edu-skill-card__capacity-label">
                        {card.enrolled}/{card.capacity} {t("statistics.people", "人")}
                      </span>
                    </>
                  ) : (
                    <span className="edu-no-capacity">
                      {t("statistics.education.noCapacity", "受け入れ定員なし")}
                    </span>
                  )}
                </div>
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}
