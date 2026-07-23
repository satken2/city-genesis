/**
 * 雇用情報コンポーネント
 *
 * 経済主体がTYPE_STANDARDの場合に表示される詳細情報パネル。
 * 企業の要求スキル(required_skill)と賃金(wage)を表示する。
 * スキル名はマスターデータ(skills)から取得する。
 * 必要労働力(quota), 雇用済み労働力(total_labor)を円グラフで表示し、
 * 従業員(employees)のリストを下部エリアに複数列のグリッド形式で表示する。
 * すべての要素（基本情報の表、円グラフ、従業員リスト）は縦一列に配置され、
 * 円グラフの右側には数値なしの簡易凡例が表示される。
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
  fontSize: "11px",
};

export function EmploymentInfoPanel({ agent }: EmploymentInfoPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const skillName =
    agent.required_skill != null
      ? masterData?.skills[agent.required_skill]?.display_name ??
        String(agent.required_skill)
      : null;

  // 賃金を小数点以下第二位までにフォーマット
  const formatCurrency = (value: number) => t("common.currency", { value: value.toFixed(2) });

  const hiredCount = agent.total_labor ?? 0;
  const quotaCount = agent.quota ?? 0;
  const vacantCount = Math.max(0, quotaCount - hiredCount);

  // 円グラフ用データ
  const chartData = [
    { name: t("employment.hired", "雇用済み"), value: hiredCount, color: "#10b981" },
    { name: t("employment.vacant", "未充足"), value: vacantCount, color: "#374151" },
  ];

  const hasQuota = quotaCount > 0 || hiredCount > 0;
  // 労働力必要なし（quota = 0）の場合のプレースホルダーデータ
  const dummyChartData = [
    { name: t("employment.noQuota", "労働力必要なし"), value: 1, color: "#374151" }
  ];

  return (
    <section className="agent-detail-section employment-info-section">
      <div className="employment-info-content">
        {/* 1. 基本情報（要求スキル、賃金、必要労働力、雇用済み労働力） */}
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

        {/* 2. 雇用状況（円グラフと凡例） */}
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
            
            {/* 数値なしの簡易凡例 */}
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

        {/* 3. 従業員リスト */}
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
