/**
 * Agent alert card list
 *
 * A shared component that reads agent alerts and spatial warnings on the building and its parcel,
 * and vertically lists an icon, title, and description card for each set bit.
 * Placed at the top of the agent detail panel (AgentDetailPanel).
 *
 * - Agent cards are defined in ALERT_CARDS, and spatial warnings in WARNING_CARDS.
 *   Text uses the i18n keys agent.alerts.<key>.
 * - This is independent of Unity billboard icons (ALERT_AGENT_ICON_BITS) and displays all bits,
 *   including production bottlenecks exclusive to the detail panel.
 * - Spatial warnings use the same API evaluation results as Unity, in a separate bit field from alerts.
 * - Renders nothing when no bits are set.
 */

import { useTranslation } from "react-i18next";
import {
  ALERT_AGENT_CONSTRUCTION_SHORTAGE,
  ALERT_AGENT_ON_FIRE,
  ALERT_AGENT_ACCIDENTAL_FIRE,
  ALERT_AGENT_CRIME,
  ALERT_AGENT_CRIME_SIGN,
  ALERT_AGENT_INFRA_DISCONNECTED,
  ALERT_AGENT_LABOR_SHORTAGE,
  ALERT_AGENT_MATERIAL_SHORTAGE,
  ALERT_AGENT_HIGH_SKILL_SHORTAGE,
  ALERT_AGENT_BUDGET_CAPPED,
  ALERT_AGENT_CAPACITY_TIGHT,
  ALERT_DISPLAY_FACILITY_WARNING,
  ALERT_DISPLAY_SANITATION_WARNING,
  ALERT_DISPLAY_INFRA_WARNING,
} from "../../constants";
import shortageIcon from "../../assets/images/ui/agent_panel/bribery.png";
import fireIcon from "../../assets/images/ui/agent_panel/fire.png";
import facilityWarningIcon from "../../assets/images/alerts/bribery.png";
import sanitationWarningIcon from "../../assets/images/alerts/sanitation.png";
import infraWarningIcon from "../../assets/images/alerts/electricity.png";
import capacityTightIcon from "../../assets/images/alerts/capacity-tight.svg";
import "./AgentDetailPanel.css";

type AlertCardDef = {
  bit: number;
  icon: string;
  /** Suffix of the i18n key. References agent.alerts.<key>.title / .description. */
  key: string;
};

// Mapping from bits to card definitions. Display order follows this array.
// Bits without dedicated icons temporarily reuse the shortage icon (bribery).
const ALERT_CARDS: AlertCardDef[] = [
  { bit: ALERT_AGENT_CONSTRUCTION_SHORTAGE, icon: shortageIcon, key: "constructionShortage" },
  { bit: ALERT_AGENT_ON_FIRE, icon: fireIcon, key: "onFire" },
  { bit: ALERT_AGENT_ACCIDENTAL_FIRE, icon: fireIcon, key: "accidentalFire" },
  { bit: ALERT_AGENT_CRIME, icon: shortageIcon, key: "crime" },
  { bit: ALERT_AGENT_CRIME_SIGN, icon: shortageIcon, key: "crimeSign" },
  { bit: ALERT_AGENT_INFRA_DISCONNECTED, icon: shortageIcon, key: "infraDisconnected" },
  { bit: ALERT_AGENT_CAPACITY_TIGHT, icon: capacityTightIcon, key: "capacityTight" },
  { bit: ALERT_AGENT_LABOR_SHORTAGE, icon: shortageIcon, key: "laborShortage" },
  { bit: ALERT_AGENT_MATERIAL_SHORTAGE, icon: shortageIcon, key: "materialShortage" },
  { bit: ALERT_AGENT_HIGH_SKILL_SHORTAGE, icon: shortageIcon, key: "highSkillShortage" },
  { bit: ALERT_AGENT_BUDGET_CAPPED, icon: shortageIcon, key: "budgetCapped" },
];

const WARNING_CARDS: AlertCardDef[] = [
  { bit: ALERT_DISPLAY_FACILITY_WARNING, icon: facilityWarningIcon, key: "facilityWarning" },
  { bit: ALERT_DISPLAY_SANITATION_WARNING, icon: sanitationWarningIcon, key: "sanitationWarning" },
  { bit: ALERT_DISPLAY_INFRA_WARNING, icon: infraWarningIcon, key: "infraWarning" },
];

type AgentAlertsPanelProps = {
  /** Combined agent alert bits. Treat unset values (undefined/null) as 0. */
  alert: number | undefined | null;
  /** Spatial warnings on the building and its parcel. Independent of display filters in the 3D view. */
  warning?: number | null;
};

export function AgentAlertsPanel({ alert, warning }: AgentAlertsPanelProps) {
  const { t } = useTranslation();
  const bits = alert ?? 0;
  const activeCards = [
    ...ALERT_CARDS.filter((card) => (bits & card.bit) !== 0),
    ...WARNING_CARDS.filter((card) => ((warning ?? 0) & card.bit) !== 0),
  ];
  if (activeCards.length === 0) return null;

  return (
    <div className="agent-detail-alerts">
      {activeCards.map((card) => {
        const body = (
          <>
            <div className="agent-detail-alert-title">
              <img src={card.icon} alt="" className="agent-detail-alert-icon" />
              {t(`agent.alerts.${card.key}.title`)}
            </div>
            <div className="agent-detail-alert-desc">
              {t(`agent.alerts.${card.key}.description`)}
            </div>
          </>
        );
        return (
          <div className="agent-detail-alert-card" key={card.key}>
            {body}
          </div>
        );
      })}
    </div>
  );
}
