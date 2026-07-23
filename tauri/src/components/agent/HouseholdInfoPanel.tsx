/**
 * 世帯情報コンポーネント
 *
 * 経済主体のtypeがTYPE_HOUSEHOLDの場合に表示される詳細情報パネル。
 * 世帯タイプ・自動車保有・可処分所得・各種充足度(メーター)・世帯構成員を表示する。
 *
 * 充足度(satis_*)は0.0〜1.0の割合値としてMeterコンポーネントで可視化する。
 * 世帯構成員はCitizenLinkとして表示し、クリックで市民詳細パネルへの遷移を想定している。
 */

import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import {
  FAMILY_SINGLE,
  FAMILY_COUPLE,
  FAMILY_FAMILY,
  FAMILY_SENIOR,
} from "../../constants";
import { Meter } from "../common/Meter";
import { CitizenLink } from "../common/CitizenLink";
import trafficPedestrian from "../../assets/images/traffics/traffic_pedestrian.png";
import trafficRoad from "../../assets/images/traffics/traffic_road.png";
import "./HouseholdInfoPanel.css";

type HouseholdInfoPanelProps = {
  agent: AgentData;
};

/**
 * 世帯タイプの数値に対応する表示名を返す
 */
function getFamilyTypeName(familyType: number, t: (key: string) => string): string {
  switch (familyType) {
    case FAMILY_SINGLE:
      return t("household.familySingle");
    case FAMILY_COUPLE:
      return t("household.familyCouple");
    case FAMILY_FAMILY:
      return t("household.familyFamily");
    case FAMILY_SENIOR:
      return t("household.familySenior");
    default:
      return String(familyType);
  }
}

export function HouseholdInfoPanel({ agent }: HouseholdInfoPanelProps) {
  const { t } = useTranslation();

  const automobileIconSrc = agent.has_automobile ? trafficRoad : trafficPedestrian;
  const automobileAlt = agent.has_automobile
    ? t("household.hasAutomobile")
    : t("household.noAutomobile");

  return (
    <section className="agent-detail-section household-info-section">
      <div className="household-info-content">
        <div className="agent-detail-grid">
          <span className="agent-detail-label">
            {t("household.familyType")}
          </span>
          <span className="agent-detail-value household-family-type">
            {agent.family_type != null
              ? getFamilyTypeName(agent.family_type, t)
              : "-"}
            <img
              className="household-automobile-icon"
              src={automobileIconSrc}
              alt={automobileAlt}
              title={automobileAlt}
            />
          </span>

          <span className="agent-detail-label">
            {t("household.discretionaryIncome")}
          </span>
          <span className="agent-detail-value">
            {agent.discretionary_income != null
              ? t("common.currency", { value: agent.discretionary_income })
              : "-"}
          </span>
        </div>

        <div className="household-info-meters">
          {agent.satis_nutrition != null && (
            <Meter
              label={t("household.satisNutrition")}
              value={agent.satis_nutrition}
            />
          )}
          {agent.satis_healthcare != null && (
            <Meter
              label={t("household.satisHealthcare")}
              value={agent.satis_healthcare}
            />
          )}
          {agent.satis_entertainment != null && (
            <Meter
              label={t("household.satisEntertainment")}
              value={agent.satis_entertainment}
            />
          )}
        </div>

        {agent.members && agent.members.length > 0 && (
          <div className="household-members-section">
            <span className="household-members-label">
              {t("household.members")}
            </span>
            <div className="household-members-list">
              {agent.members.map((member) => (
                <CitizenLink
                  key={member.id}
                  citizenId={member.id}
                  citizenName={member.name}
                />
              ))}
            </div>
          </div>
        )}
      </div>
    </section>
  );
}
