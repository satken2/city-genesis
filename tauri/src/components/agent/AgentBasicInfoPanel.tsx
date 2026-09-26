/**
 * 経済主体の基本情報パネル
 *
 * 全経済主体に共通する基本情報を表示するセクション。
 * 公共施設フラグ・現在のステータスになった日(竣工日・開業日など)・準備金・収入・支出・
 * ゾーン・所属建物をラベル付きグリッドで一覧表示する。
 *
 * 輸出入拠点(TYPE_TRADE)の経済主体には表示しない。
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

/** status_from の見出しの種類。i18n `agent.statusSince.*` のキーと一致させる */
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
 * status_from(現在のステータスになった日)の見出しを、ステータスと種別から選ぶ。
 *
 * status_from はステータスが変わるたびに更新され、稼働中へは竣工のほか休止からの再開でも戻る。
 * そのため稼働中の見出しは、その種別が稼働中になりうるすべての経路で正しい語にする。
 * - プレイヤーの操作で稼働中に戻した建物(再開・取り壊しの取り消し)は再開日。
 * - 貸しビル(TYPE_BUILDING)は入居需要が戻ると自動で再稼働し、竣工と区別できないため稼働開始日。
 * - 民間の事業所は赤字休業からの再稼働も開業とみなす(開店・閉店履歴と同じ扱い)。
 * - それ以外の建物は自動では休止しないので竣工日(学校は開校日)。
 * 世帯は結成時から稼働中のまま変わらないため、世帯の形成日(観光客は到着日)を指す。
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
        return "founded"; // 入居先の建物を探している間は仮の状態で、変更日=設立日になる
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
