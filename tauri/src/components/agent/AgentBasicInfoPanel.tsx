/**
 * Agent basic information panel
 *
 * Displays basic information shared by all agents in a labeled grid: public facility flag,
 * date the current status began (completion, opening, etc.), reserves, income, expenditure,
 * district, and containing building.
 *
 * Not displayed for import/export hubs (TYPE_TRADE).
 */

import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import {
  REASON_FIRE,
  REASON_USER,
  STATUS_ACTIVE,
  STATUS_COLLAPSED,
  STATUS_CONSTRUCTING,
  STATUS_DEMOLISHING,
  STATUS_EMPTY,
  STATUS_PAUSED,
  STATUS_SCHEDULED_DEMOLITION,
  STATUS_TEMPORARY,
  TYPE_BUILDING,
  TYPE_EDUCATION,
  TYPE_HOUSEHOLD,
  TYPE_STANDARD,
  VARIANT_TOURIST,
} from "../../constants";
import { EpochDate } from "../common/EpochDate";
import { DataList } from "../common/DataList";
import { ZoneLink } from "../common/ZoneLink";
import { AgentLink } from "../common/AgentLink";

/** Heading types for status_from. Must match the i18n keys under `agent.statusSince.*`. */
type StatusSinceLabel =
  | "founded"
  | "householdFormed"
  | "arrived"
  | "opened"
  | "schoolOpened"
  | "completed"
  | "operatingSince"
  | "resumed"
  | "paused"
  | "suspended"
  | "schoolClosed"
  | "constructionStarted"
  | "demolitionScheduled"
  | "demolitionStarted"
  | "demolished"
  | "collapsed"
  | "burnedDown"
  | "default";

/**
 * Select the heading for status_from (the date the current status began) from the status and type.
 *
 * status_from updates on each status change. Active status can follow completion or resumption.
 * The heading must remain valid for every transition the active agent's type allows.
 * - Buildings reactivated by the player (resuming or canceling demolition) use the resumption date.
 * - Rental buildings (TYPE_BUILDING) automatically reactivate when tenant demand returns, so use
 *   the operation start date because this cannot be distinguished from completion.
 * - Private businesses treat resumption after a loss-induced closure as opening, as in their opening/closing history.
 * - Other buildings never suspend automatically, so use the completion date (opening date for schools).
 * Households remain active from formation onward, so this is their formation date (arrival date for tourists).
 */
function resolveStatusSinceLabel(agent: AgentData): StatusSinceLabel {
  const { status, type } = agent;

  if (type === TYPE_HOUSEHOLD) {
    if (status !== STATUS_ACTIVE) return "default";
    return agent.variant === VARIANT_TOURIST ? "arrived" : "householdFormed";
  }

  if (!agent.is_bd) {
    const isSchool = type === TYPE_EDUCATION;
    if (!isSchool && type !== TYPE_STANDARD) return "default";
    switch (status) {
      case STATUS_TEMPORARY:
        return "founded"; // Seeking a building is a temporary state, so its change date equals the founding date.
      case STATUS_ACTIVE:
        return isSchool ? "schoolOpened" : "opened";
      case STATUS_PAUSED:
        return isSchool ? "schoolClosed" : "suspended";
      default:
        return "default";
    }
  }

  const isPrivateBusiness = type === TYPE_STANDARD && !agent.public_facility;
  switch (status) {
    case STATUS_ACTIVE:
      if (agent.status_reason === REASON_USER) return "resumed";
      if (type === TYPE_EDUCATION) return "schoolOpened";
      if (type === TYPE_BUILDING) return "operatingSince";
      if (isPrivateBusiness) return "opened";
      return "completed";
    case STATUS_PAUSED:
      if (type === TYPE_EDUCATION) return "schoolClosed";
      if (isPrivateBusiness) return "suspended";
      return "paused";
    case STATUS_CONSTRUCTING:
      return "constructionStarted";
    case STATUS_SCHEDULED_DEMOLITION:
      return "demolitionScheduled";
    case STATUS_DEMOLISHING:
      return "demolitionStarted";
    case STATUS_EMPTY:
      return "demolished";
    case STATUS_COLLAPSED:
      return agent.status_reason === REASON_FIRE ? "burnedDown" : "collapsed";
    default:
      return "default";
  }
}

type AgentBasicInfoPanelProps = {
  agent: AgentData;
  onNavigate: (entityId: string) => void;
};

export function AgentBasicInfoPanel({
  agent,
  onNavigate,
}: AgentBasicInfoPanelProps) {
  const { t } = useTranslation();

  const formatCurrency = (value: number) =>
    t("common.currency", { value });

  return (
    <section className="agent-detail-section">
      <div className="agent-detail-grid">
        {agent.public_facility ? (
          <>
            <span className="agent-detail-label">
              {t("agent.publicFacility")}
            </span>
            <span className="agent-detail-value">✓</span>
          </>
        ) : null}

        <span className="agent-detail-label">
          {t(`agent.statusSince.${resolveStatusSinceLabel(agent)}`)}
        </span>
        <span className="agent-detail-value">
          <EpochDate daysSinceEpoch={agent.status_from} />
        </span>

        <span className="agent-detail-label">
          {t("agent.reserves")}
        </span>
        <span className="agent-detail-value">
          {formatCurrency(agent.reserves)}
        </span>

        <span className="agent-detail-label">
          {t("agent.revenues")}
        </span>
        <span className="agent-detail-value">
          <DataList
            values={agent.revenues}
            formatter={formatCurrency}
          />
        </span>

        <span className="agent-detail-label">
          {t("agent.expenses")}
        </span>
        <span className="agent-detail-value">
          <DataList
            values={agent.expenses}
            formatter={formatCurrency}
          />
        </span>

        {agent.zone_name && (
          <>
            <span className="agent-detail-label">
              {t("agent.zone")}
            </span>
            <span className="agent-detail-value">
              <ZoneLink
                zoneId={agent.zone_id}
                zoneName={agent.zone_name}
              />
            </span>
          </>
        )}

        {agent.building_agent_name && (
          <>
            <span className="agent-detail-label">
              {t("agent.building")}
            </span>
            <span className="agent-detail-value">
              <AgentLink
                agentId={agent.building_agent_id}
                agentName={agent.building_agent_name}
                onNavigate={onNavigate}
              />
            </span>
          </>
        )}
      </div>
    </section>
  );
}
