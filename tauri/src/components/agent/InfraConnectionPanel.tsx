/**
 * Infrastructure connection section
 *
 * Connection information in the agent detail panel's "Supply, demand & trading" tab.
 * Shown only for facilities (power plants, substations, etc.) exchanging infrastructure goods
 * (GOODS_INFRASTRUCTURE: electricity, water, etc.). Receives a supply diagram as children;
 * facilities without connection information display only the diagram.
 *
 * Display eligibility:
 * Check the facility's master data definition: materials containing infrastructure goods means
 * a consumer (such as a substation); infrastructure supply means a supplier (such as a power plant).
 * Runtime demand_amount/received_amount may also contain values for non-infrastructure facilities,
 * so do not use them to decide eligibility; this avoids showing the section on ordinary facilities.
 *
 * Content:
 * One green/red bordered badge per infrastructure good exchanged.
 * - Connected: "Connected (Electricity) Received:100.00L" (consumers also show received amounts).
 * - Disconnected: "Disconnected (Electricity)".
 * Connection status uses the ALERT_AGENT_INFRA_DISCONNECTED bit calculated by the backend each epoch.
 * It is set only for active (STATUS_ACTIVE) facilities, so show badges only while active;
 * construction provides a grace period for wiring.
 * Use received_amount (the common amount obtained this epoch) rather than remain_amount,
 * which is reduced by production material consumption and cannot represent receipt totals.
 * received_amount omits zero-value products, so connected facilities receiving nothing
 * (such as during supply shortages) display 0.
 */

import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import {
  GOODS_INFRASTRUCTURE,
  STATUS_ACTIVE,
  ALERT_AGENT_INFRA_DISCONNECTED,
} from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import "./InfraConnectionPanel.css";

type InfraConnectionPanelProps = {
  agent: AgentData;
  children?: ReactNode;
};

export function InfraConnectionPanel({ agent, children }: InfraConnectionPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  if (!masterData) return <>{children}</>;

  const agentMaster = masterData.agents[agent.variant];
  if (!agentMaster) return <>{children}</>;

  /** Whether a product ID represents an infrastructure good. */
  const isInfraProduct = (pid: number) =>
    masterData.products[pid]?.type === GOODS_INFRASTRUCTURE;

  // Infrastructure goods consumed by this facility (in materials, e.g. electricity for a substation).
  const consumedPids = Object.keys(agentMaster.materials ?? {})
    .map(Number)
    .filter(isInfraProduct);
  // Infrastructure goods supplied by this facility (in supply, e.g. electricity from a power plant).
  const suppliedPid =
    agentMaster.supply != null && isInfraProduct(agentMaster.supply)
      ? agentMaster.supply
      : null;

  // Agents that do not exchange infrastructure goods display only the supply diagram.
  if (consumedPids.length === 0 && suppliedPid == null) {
    return <>{children}</>;
  }

  const isActive = agent.status === STATUS_ACTIVE;
  const isDisconnected =
    ((agent.alert ?? 0) & ALERT_AGENT_INFRA_DISCONNECTED) !== 0;
  const received: Record<string, number> = agent.received_amount ?? {};

  // One badge per infrastructure good exchanged. Only consumers (isConsumer) also show received amounts.
  const lines: { pid: number; isConsumer: boolean }[] = [
    ...consumedPids.map((pid) => ({ pid, isConsumer: true })),
    ...(suppliedPid != null ? [{ pid: suppliedPid, isConsumer: false }] : []),
  ];

  return (
    <section className="agent-detail-section">
      <h3 className="agent-detail-section-title">
        {t("agent.infra.title", "インフラ接続")}
      </h3>

      {/*
 * Connection badges (active facilities only, as bits are set only while active).
 * Hide during construction as a grace period for wiring.
 */}
      {isActive && (
        <div className="infra-connection-badges">
          {lines.map(({ pid, isConsumer }) => {
            const product = masterData.products[pid];
            const productName = product?.display_name ?? String(pid);
            const unit = product?.unit ?? "";
            // received_amount omits zero-value products, so fall back to 0 when nothing was received.
            const amount = received[String(pid)] ?? 0;
            const statusText = isDisconnected
              ? t("agent.infra.disconnected", "未接続")
              : t("agent.infra.connected", "接続済み");
            return (
              <div
                key={pid}
                className={[
                  "infra-connection-badge",
                  isDisconnected
                    ? "infra-connection-badge--disconnected"
                    : "infra-connection-badge--connected",
                ].join(" ")}
              >
                {`${statusText}(${productName})`}
                {!isDisconnected && isConsumer && amount != null && (
                  <span className="infra-connection-received">
                    {` ${t("agent.infra.received", "受入れ量")}:${amount.toFixed(2)}${unit}`}
                  </span>
                )}
              </div>
            );
          })}
        </div>
      )}
      {children}
    </section>
  );
}
