/**
 * Employment information component
 *
 * Detail panel for TYPE_STANDARD agents. Displays required_skill and wage;
 * skill names come from master data (skills).
 * Shows required labor (quota) and employed labor (total_labor) in a pie chart,
 * and employees in a multicolumn grid below.
 * All elements (basic information table, pie chart, employee list) form one vertical column;
 * a compact legend without numbers sits to the right of the pie chart.
 */

import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import { useMasterData } from "../../contexts/MasterDataContext";
import { CitizenLink } from "../common/CitizenLink";
import { PieChart, Pie, Cell, ResponsiveContainer, Tooltip } from "recharts";
import "./EmploymentInfoPanel.css";

type EmploymentInfoPanelProps = {
  agent: AgentData;
};

const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "1rem",
};

export function EmploymentInfoPanel({ agent }: EmploymentInfoPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const skillName =
    agent.required_skill != null
      ? masterData?.skills[agent.required_skill]?.display_name ??
        String(agent.required_skill)
      : null;

  // Format wages to two decimal places.
  const formatCurrency = (value: number) => t("common.currency", { value: value.toFixed(2) });

  const hiredCount = agent.total_labor ?? 0;
  const quotaCount = agent.quota ?? 0;
  const vacantCount = Math.max(0, quotaCount - hiredCount);

  // Pie chart data.
  const chartData = [
    { name: t("employment.hired", "雇用済み"), value: hiredCount, color: "#10b981" },
    { name: t("employment.vacant", "未充足"), value: vacantCount, color: "#374151" },
  ];

  const hasQuota = quotaCount > 0 || hiredCount > 0;
  // Placeholder data when no labor is required (quota = 0).
  const dummyChartData = [
    { name: t("employment.noQuota", "労働力必要なし"), value: 1, color: "#374151" }
  ];

  return (
    <section className="agent-detail-section employment-info-section">
      <div className="employment-info-content">
        {/* 1. Basic information (required skill, wage, required labor, employed labor). */}
        <div className="agent-detail-grid">
          {skillName != null && (
            <>
              <span className="agent-detail-label">
                {t("employment.requiredSkill")}
              </span>
              <span className="agent-detail-value">{skillName}</span>
            </>
          )}

          {agent.wage != null && (
            <>
              <span className="agent-detail-label">
                {t("employment.wage")}
              </span>
              <span className="agent-detail-value">
                {formatCurrency(agent.wage)}
              </span>
            </>
          )}

          <span className="agent-detail-label">
            {t("employment.quota", "必要労働力")}
          </span>
          <span className="agent-detail-value">
            {quotaCount.toFixed(2)} {t("education.people", "人")}
          </span>

          <span className="agent-detail-label">
            {t("employment.totalLabor", "雇用済み労働力")}
          </span>
          <span className="agent-detail-value">
            {hiredCount.toFixed(2)} {t("education.people", "人")}
          </span>
        </div>

        {/* 2. Employment status (pie chart and legend). */}
        <div className="employment-chart-card">
          <div className="employment-chart-title">
            {t("employment.statusTitle", "雇用状況")}
          </div>
          <div className="employment-chart-container">
            <div style={{ width: "80px", height: "80px", flexShrink: 0 }}>
              <ResponsiveContainer width="100%" height="100%">
                <PieChart>
                  <Pie
                    data={hasQuota ? chartData : dummyChartData}
                    cx="50%"
                    cy="50%"
                    innerRadius={20}
                    outerRadius={38}
                    paddingAngle={hasQuota ? 3 : 0}
                    dataKey="value"
                  >
                    {(hasQuota ? chartData : dummyChartData).map((entry, index) => (
                      <Cell key={`cell-${index}`} fill={entry.color} />
                    ))}
                  </Pie>
                  {hasQuota && (
                    <Tooltip
                      contentStyle={TOOLTIP_STYLE}
                      formatter={(value: any) => [
                        `${Number(value).toFixed(2)} ${t("education.people", "人")}`,
                        t("statistics.count", "人数"),
                      ]}
                    />
                  )}
                </PieChart>
              </ResponsiveContainer>
            </div>
            
            {/* Compact legend without numbers. */}
            <div className="employment-legend">
              {hasQuota ? (
                chartData.map((d, index) => (
                  <div key={index} className="employment-legend-item">
                    <span
                      className="employment-legend-bullet"
                      style={{ backgroundColor: d.color }}
                    />
                    <span className="employment-legend-label">{d.name}</span>
                  </div>
                ))
              ) : (
                <div className="employment-legend-item">
                  <span
                    className="employment-legend-bullet"
                    style={{ backgroundColor: "#374151" }}
                  />
                  <span className="employment-legend-label">
                    {t("employment.noQuota", "労働力必要なし")}
                  </span>
                </div>
              )}
            </div>
          </div>
        </div>

        {/* 3. Employee list. */}
        <div className="employment-info-bottom">
          <span className="employment-info-bottom-title">
            {t("employment.employees", "従業員リスト")}
          </span>
          <div className="employment-employees-list">
            {agent.employees && agent.employees.length > 0 ? (
              agent.employees.map((employee) => (
                <CitizenLink
                  key={employee.id}
                  citizenId={employee.id}
                  citizenName={employee.name ?? `Citizen #${employee.id}`}
                />
              ))
            ) : (
              <span className="agent-detail-muted">
                {t("employment.noEmployees", "従業員はいません")}
              </span>
            )}
          </div>
        </div>
      </div>
    </section>
  );
}
