import { useTranslation } from "react-i18next";
import { useSupplyGraph } from "../../hooks/useSupplyGraph";
import { AgentAlertsPanel } from "../agent/AgentAlertsPanel";
import "../agent/AgentDetailPanel.css";
import { SupplyChainGraph } from "./SupplyChainGraph";
import "./SupplyChain.css";

type Props = { apiBaseUrl: string; agentId: string; enabled?: boolean; refreshToken?: unknown;
  fallbackAlert?: number | null; fallbackNotice?: string | null; onNavigate?: (entityId: string) => void };

export function SupplyChainSection({ apiBaseUrl, agentId, enabled = true, refreshToken, fallbackAlert, fallbackNotice, onNavigate }: Props) {
  const { t } = useTranslation();
  const { data, error, loading, executing, pending, refresh } = useSupplyGraph(apiBaseUrl, agentId, enabled, refreshToken);
  const lineWarnings = data?.roots.filter((root) => root.type.startsWith("line_")) ?? [];
  if (!enabled) return <AgentAlertsPanel alert={fallbackAlert} />;
  return <section className="supply-section" aria-label={t("supplyGraph.title")} aria-busy={loading || pending}>
    <div className="supply-section-heading"><strong>{t("supplyGraph.title")}</strong>
      <button type="button" className="supply-refresh" onClick={refresh} disabled={loading && !pending || executing} title={t("supplyGraph.refresh")} aria-label={t("supplyGraph.refresh")}>↻</button>
    </div>
    {pending ? <p className="supply-note">{t("supplyGraph.executing")}</p>
      : error ? <p className="supply-note" role="status">{t("supplyGraph.fetchError")}</p>
      : loading ? <p className="supply-note">{t("common.loading")}</p>
      : data?.graph && <>
        <SupplyChainGraph graph={data.graph} onNavigate={onNavigate} />
        <p className="supply-note">{data.graph.has_results ? t("supplyGraph.epochNote", { epoch: data.epoch }) : t("supplyGraph.noResults")}</p>
        <p className="supply-note">{t("supplyGraph.inputsNote")}</p>
      </>}
    {!pending && <AgentAlertsPanel alert={data?.alert ?? fallbackAlert} />}
    {!pending && lineWarnings.map((root, index) =>
      <div className="agent-detail-alert-card" key={index}>{t(`diagnosis.nodes.${root.type}.title`, { ...root.params, defaultValue: t("transit.vehicleShortage") })}</div>)}
    {!pending && !lineWarnings.length && fallbackNotice && <div className="agent-detail-alert-card" role="status">{fallbackNotice}</div>}
  </section>;
}
