/**
 * インフラ接続セクション
 *
 * 経済主体詳細パネルの「需給・取引」タブに表示するインフラ財の接続情報。
 * インフラ財(GOODS_INFRASTRUCTURE: 電力・水道など)を授受する施設
 * (発電所・変電所など)にのみ表示される。
 * 供給図を children として受け取り、接続情報がない施設では図だけを表示する。
 *
 * 表示対象の判定:
 * 施設の「マスターデータ定義」を見て、materials にインフラ財を持つ施設(=消費側。
 * 変電所など)か、supply がインフラ財の施設(=供給側。発電所など)にのみ表示する。
 * 実行時の需給データ(demand_amount/received_amount)は非インフラ施設にも
 * 値が入り得るため判定には使わず、インフラを扱わない一般施設にセクションが
 * 出てしまうのを防ぐ。
 *
 * 表示内容:
 * 授受するインフラ財ごとに、緑/赤の枠(バッジ)を1つ表示する。
 * - 接続済み: 「接続済み(電力) 受入れ量:100.00L」(消費側は受領量を併記)
 * - 未接続  : 「未接続(電力)」
 * 接続状態の判定はバックエンドが毎エポック計算するアラートビット
 * (ALERT_AGENT_INFRA_DISCONNECTED)を参照する。ビットは稼働中(STATUS_ACTIVE)の
 * 施設にしか立たないため、バッジも稼働中のみ表示する(建設中は配線猶予期間)。
 * 受領量は remain_amount ではなく received_amount(全財共通の当エポック獲得量)を使う
 * (remain_amount は生産の原材料消費で減算されるため受領実績には使えない)。
 * received_amount はゼロの製品を含まない辞書なので、接続済みでも受領できていない
 * 施設(供給不足など)は 0 として表示する。
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

  /** 製品IDがインフラ財かどうか */
  const isInfraProduct = (pid: number) =>
    masterData.products[pid]?.type === GOODS_INFRASTRUCTURE;

  // この施設が消費するインフラ財(materials にあるインフラ財。例: 変電所の電力)
  const consumedPids = Object.keys(agentMaster.materials ?? {})
    .map(Number)
    .filter(isInfraProduct);
  // この施設が供給するインフラ財(supply がインフラ財。例: 発電所の電力)
  const suppliedPid =
    agentMaster.supply != null && isInfraProduct(agentMaster.supply)
      ? agentMaster.supply
      : null;

  // インフラ財を授受しない経済主体では、供給図だけを表示する。
  if (consumedPids.length === 0 && suppliedPid == null) {
    return <>{children}</>;
  }

  const isActive = agent.status === STATUS_ACTIVE;
  const isDisconnected =
    ((agent.alert ?? 0) & ALERT_AGENT_INFRA_DISCONNECTED) !== 0;
  const received: Record<string, number> = agent.received_amount ?? {};

  // バッジ1つ = 授受するインフラ財1種。消費側(isConsumer)のみ受領量を併記する。
  const lines: { pid: number; isConsumer: boolean }[] = [
    ...consumedPids.map((pid) => ({ pid, isConsumer: true })),
    ...(suppliedPid != null ? [{ pid: suppliedPid, isConsumer: false }] : []),
  ];

  return (
    <section className="agent-detail-section">
      <h3 className="agent-detail-section-title">
        {t("agent.infra.title", "インフラ接続")}
      </h3>

      {/* 接続状態バッジ(稼働中のみ。ビットが稼働中にしか立たないため、
          建設中は配線猶予期間として表示しない) */}
      {isActive && (
        <div className="infra-connection-badges">
          {lines.map(({ pid, isConsumer }) => {
            const product = masterData.products[pid];
            const productName = product?.display_name ?? String(pid);
            const unit = product?.unit ?? "";
            // received_amount はゼロの製品を含まないため、未受領は 0 にフォールバックする
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
