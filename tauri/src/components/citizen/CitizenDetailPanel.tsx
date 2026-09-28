/**
 * 市民詳細パネル
 *
 * window_type="citizen_detail" のポップアップウィンドウで表示する専用パネル。
 * 世帯詳細パネル(AgentDetailPanel の HouseholdInfoPanel)で世帯構成員(CitizenLink)を
 * クリックしたときに、この市民詳細ウィンドウが開く。
 *
 * 表示内容は API /api/v1/citizens/{id} (endpoints.yaml の single_citizen スキーマ)が
 * 返すフィールドに限られる。以下の4セクションで構成する。
 *   - 基本情報: 年齢・性別・配偶関係・所属世帯
 *   - 就労・教育: 勤務先・通学先・専攻スキル・高等教育修了状況
 *   - スキル: 6種のスキル値をレーダーチャートで可視化
 *   - 治安: 犯罪性向
 *
 * 所属世帯・勤務先・通学先のリンクは、現在のパネル内ナビゲーションではなく、
 * 別の経済主体詳細ウィンドウを新規に開く(SearchWindow 等と同じ open_registered_window)。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import {
  Radar,
  RadarChart,
  PolarGrid,
  PolarAngleAxis,
  PolarRadiusAxis,
  ResponsiveContainer,
} from "recharts";
import type { CitizenData, CitizenResponse } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { MEMBER_MARRIED } from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import { InfoHeader } from "../common/InfoHeader";
import citizenIcon from "../../assets/images/citizen.png";
import "./CitizenDetailPanel.css";

/**
 * 高スキル境界。バックエンドの HIGH_SKILL_BOUNDARY と同じ値。
 * いずれかのスキルがこの値以上の市民は高等教育修了相当（高度人材）とみなす。
 */
const HIGH_SKILL_BOUNDARY = 1.5;

/**
 * スキル値の表示上限。
 * バックエンドのスキル値は基礎教育で MAX_BASE_SKILL(1.0)、
 * 高等教育で HIGH_SKILL_BOUNDARY(1.5) を上限として成長するため、
 * レーダーチャートの半径軸は最大値を 1.5 に固定して常に同じ尺度で比較できるようにする。
 */
const SKILL_DISPLAY_MAX = HIGH_SKILL_BOUNDARY;

type CitizenDetailPanelProps = {
  apiBaseUrl: string;
  pathTemplate: string;
  initialEntityId: string;
};

/**
 * 経済主体名のクリッカブルリンク。
 * クリックすると対象の経済主体詳細ウィンドウを新規に開く。
 * 名前が未設定(無職・未就学などで参照先が存在しない)場合はプレースホルダを表示する。
 */
function AgentWindowLink({
  agentName,
  agentId,
  placeholder,
}: {
  agentName: string | null | undefined;
  agentId: number;
  placeholder: string;
}) {
  if (!agentName) {
    return <span className="agent-detail-muted">{placeholder}</span>;
  }

  function handleClick() {
    void invoke("open_registered_window", {
      windowType: "agent_detail",
      entityId: String(agentId),
    });
  }

  return (
    <button className="entity-link" onClick={handleClick} type="button">
      {agentName}
    </button>
  );
}

export function CitizenDetailPanel({
  apiBaseUrl,
  pathTemplate,
  initialEntityId,
}: CitizenDetailPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const [citizenData, setCitizenData] = useState<CitizenData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const url = buildApiUrl(apiBaseUrl, pathTemplate, initialEntityId);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    fetchApi<CitizenResponse>(url, { signal: controller.signal })
      .then((response) => {
        if (!controller.signal.aborted) {
          setCitizenData(response.citizen);
          setLoading(false);
        }
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          const message = err instanceof Error ? err.message : "Unknown error";
          setError(message);
          setLoading(false);
        }
      });

    return () => {
      controller.abort();
    };
  }, [url]);

  if (loading || !masterData) {
    return <p className="agent-detail-muted">{t("common.loading")}</p>;
  }

  if (error || !citizenData) {
    return (
      <div className="agent-detail-error">
        <strong>{t("common.fetchError")}</strong>
        <p>{error}</p>
      </div>
    );
  }

  // 性別・配偶関係の表示文字列。バックエンドの gender は 0=男性/1=女性、
  // citizen_type は MEMBER_SINGLE(独身)/MEMBER_MARRIED(既婚)。
  const genderLabel =
    citizenData.gender === 0 ? t("citizen.male") : t("citizen.female");
  const maritalLabel =
    citizenData.citizen_type === MEMBER_MARRIED
      ? t("citizen.married")
      : t("citizen.single");

  // 高等教育の専攻スキル。通学中かつ有効なスキルIDを持つ場合のみ表示する
  // (未就学・基礎教育中は education_skill が VALUE_NONE となり、マスターに該当しない)。
  const majorSkill = masterData.skills[citizenData.education_skill];

  // 高等教育修了はスキル基準で判定する（いずれかのスキルが高スキル境界以上）。
  const isHigherEducated = Object.values(citizenData.skills ?? {}).some(
    (value) => value >= HIGH_SKILL_BOUNDARY,
  );

  // スキルのレーダーチャート用データ。スキルIDの昇順で並べ、
  // 表示名はマスターデータから解決する(未定義IDはIDをそのまま表示)。
  const skillChartData = Object.entries(citizenData.skills ?? {})
    .map(([skillId, value]) => ({
      id: Number(skillId),
      subject: masterData.skills[Number(skillId)]?.display_name ?? skillId,
      value,
    }))
    .sort((a, b) => a.id - b.id);

  return (
    <div className="agent-detail">
      <InfoHeader
        iconSrc={citizenIcon}
        showStatus={false}
        variantName={maritalLabel}
        name={citizenData.name}
      />

      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">
          {t("citizen.basicInfo")}
        </h3>
        <div className="agent-detail-grid">
          <span className="agent-detail-label">{t("citizen.age")}</span>
          <span className="agent-detail-value">
            {t("citizen.ageValue", { age: citizenData.age })}
          </span>

          <span className="agent-detail-label">{t("citizen.gender")}</span>
          <span className="agent-detail-value">{genderLabel}</span>

          <span className="agent-detail-label">
            {t("citizen.maritalStatus")}
          </span>
          <span className="agent-detail-value">{maritalLabel}</span>

          <span className="agent-detail-label">{t("citizen.household")}</span>
          <span className="agent-detail-value">
            <AgentWindowLink
              agentName={citizenData.household_agent_name}
              agentId={citizenData.household_agent_id}
              placeholder="-"
            />
          </span>
        </div>
      </section>

      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">
          {t("citizen.workEducation")}
        </h3>
        <div className="agent-detail-grid">
          <span className="agent-detail-label">{t("citizen.employer")}</span>
          <span className="agent-detail-value">
            <AgentWindowLink
              agentName={citizenData.employer_agent_name}
              agentId={citizenData.employer_agent_id}
              placeholder={t("citizen.unemployed")}
            />
          </span>

          {citizenData.employer_agent_id > 0 && (
            <>
              <span className="agent-detail-label">{t("citizen.commuteTime")}</span>
              <span className="agent-detail-value citizen-commute" style={{ display: "flex", alignItems: "center", gap: "8px" }}>
                <div 
                  className="citizen-commute-meter-container" 
                  style={{
                    width: "120px",
                    height: "12px",
                    backgroundColor: "rgba(255, 255, 255, 0.1)",
                    borderRadius: "6px",
                    overflow: "hidden",
                    border: "1px solid rgba(255, 255, 255, 0.2)",
                    position: "relative"
                  }}
                  title={`${citizenData.commute_time.toFixed(1)} mins`}
                >
                  <div 
                    className="citizen-commute-meter-fill"
                    style={{
                      width: `${Math.min(100, (citizenData.commute_time / 120) * 100)}%`,
                      height: "100%",
                      backgroundColor: `hsl(${Math.max(0, 120 - (citizenData.commute_time / 120) * 120)}, 80%, 50%)`,
                      borderRadius: "inherit",
                      transition: "width 0.3s ease, background-color 0.3s ease"
                    }}
                  />
                </div>
                <span style={{ fontSize: "1.11rem", color: "#c5d4e3" }}>
                  {`${citizenData.commute_time.toFixed(1)} ${t("common.minutes")}`}
                </span>
              </span>
            </>
          )}

          <span className="agent-detail-label">{t("citizen.school")}</span>
          <span className="agent-detail-value">
            <AgentWindowLink
              agentName={citizenData.school_agent_name}
              agentId={citizenData.school_agent_id}
              placeholder={t("citizen.notEnrolled")}
            />
          </span>

          {majorSkill && (
            <>
              <span className="agent-detail-label">{t("citizen.major")}</span>
              <span className="agent-detail-value">
                {majorSkill.display_name}
              </span>
            </>
          )}

          <span className="agent-detail-label">
            {t("citizen.higherEducation")}
          </span>
          <span className="agent-detail-value">
            {isHigherEducated
              ? t("citizen.graduated")
              : t("citizen.notGraduated")}
          </span>
        </div>
      </section>

      {skillChartData.length > 0 && (
        <section className="agent-detail-section">
          <h3 className="agent-detail-section-title">{t("citizen.skills")}</h3>
          <div className="citizen-skill-chart">
            <ResponsiveContainer width="100%" height={260}>
              <RadarChart
                cx="50%"
                cy="50%"
                outerRadius="75%"
                data={skillChartData}
              >
                <PolarGrid />
                <PolarAngleAxis
                  dataKey="subject"
                  tick={{ fontSize: "1.02rem", fill: "#c5d4e3" }}
                />
                <PolarRadiusAxis
                  angle={30}
                  domain={[0, SKILL_DISPLAY_MAX]}
                  tick={false}
                  axisLine={false}
                />
                <Radar
                  name={t("citizen.skills")}
                  dataKey="value"
                  stroke="#4ea1ff"
                  fill="#4ea1ff"
                  fillOpacity={0.6}
                />
              </RadarChart>
            </ResponsiveContainer>
          </div>
        </section>
      )}

      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">
          {t("citizen.publicSafety")}
        </h3>
        <div className="agent-detail-grid">
          <span className="agent-detail-label">{t("citizen.criminality")}</span>
          <span className="agent-detail-value citizen-criminality">
            <meter
              className="citizen-criminality-meter"
              value={citizenData.potential_criminality}
              min={0}
              max={1}
            />
            {`${(citizenData.potential_criminality * 100).toFixed(1)}%`}
          </span>
        </div>
      </section>
    </div>
  );
}
