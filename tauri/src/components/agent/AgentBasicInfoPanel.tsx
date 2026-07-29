/**
 * 経済主体の基本情報パネル
 *
 * 全経済主体に共通する基本情報を表示するセクション。
 * 公共施設フラグ・活動開始日・準備金・収入・支出・ゾーン・所属建物を
 * ラベル付きグリッドで一覧表示する。
 *
 * 輸出入拠点(TYPE_TRADE)の経済主体には表示しない。
 */

import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import { EpochDate } from "../common/EpochDate";
import { DataList } from "../common/DataList";
import { ZoneLink } from "../common/ZoneLink";
import { AgentLink } from "../common/AgentLink";

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
          {t("agent.activeFrom")}
        </span>
        <span className="agent-detail-value">
          <EpochDate daysSinceEpoch={agent.active_from} />
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
