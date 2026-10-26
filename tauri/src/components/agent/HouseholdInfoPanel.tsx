/**
 * Household information component
 *
 * Household section for TYPE_HOUSEHOLD agents, shown below the basic information in the Basic tab.
 * Shows household type, car ownership, disposable income, standard of living, satisfaction meters, and members.
 * Satisfaction values (satis_*) range from 0.0 to 1.0, visualized by Meter.
 * Members appear as CitizenLink entries intended to navigate to citizen details on click.
 *
 * Standard of living is the household's demand_tier (the highest product tier it ordered: 0 nothing,
 * 1 necessities only, 2 general goods, 3 luxury goods), shown as a word such as "Destitute" or "Affluent".
 *
 * Tourist households (VARIANT_TOURIST) have no citizens; trip satisfaction depends on
 * entertainment/leisure and tourism experiences. Show "Tourist" instead of household type,
 * and show only the entertainment/leisure and tourism satisfaction meters. Their demand_tier follows the separate
 * tourist demand, so standard of living is hidden.
 */

import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import {
  FAMILY_SINGLE,
  FAMILY_COUPLE,
  FAMILY_FAMILY,
  FAMILY_SENIOR,
  VARIANT_TOURIST,
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
 * Return the display name corresponding to a numeric household type.
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
  const isTourist = agent.variant === VARIANT_TOURIST;

  const automobileIconSrc = agent.has_automobile ? trafficRoad : trafficPedestrian;
  const automobileAlt = agent.has_automobile
    ? t("household.hasAutomobile")
    : t("household.noAutomobile");

  return (
    <section className="agent-detail-section household-info-section">
      <h3 className="agent-detail-section-title">{t("household.title")}</h3>
      <div className="household-info-content">
        <div className="agent-detail-grid">
          <span className="agent-detail-label">
            {t("household.familyType")}
          </span>
          <span className="agent-detail-value household-family-type">
            {isTourist
              ? t("household.tourist")
              : agent.family_type != null
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

          {!isTourist && agent.demand_tier != null && (
            <>
              <span className="agent-detail-label">
                {t("household.livingStandard")}
              </span>
              <span className="agent-detail-value">
                {t(`household.livingStandardLevel.${agent.demand_tier}`)}
              </span>
            </>
          )}
        </div>

        <div className="household-info-meters">
          {!isTourist && agent.satis_nutrition != null && (
            <Meter
              label={t("household.satisNutrition")}
              value={agent.satis_nutrition}
            />
          )}
          {!isTourist && agent.satis_healthcare != null && (
            <Meter
              label={t("household.satisHealthcare")}
              value={agent.satis_healthcare}
            />
          )}
          {agent.satis_leisure != null && (
            <Meter
              label={t("household.satisLeisure")}
              value={agent.satis_leisure}
            />
          )}
          {isTourist && agent.satis_tourism != null && (
            <Meter
              label={t("household.satisTourism")}
              value={agent.satis_tourism}
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
