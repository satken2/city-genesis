/** 集約済みの供給関係を描く共通部品。取得とウィンドウ管理を持たない。 */
import { useId, useLayoutEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMasterData } from "../../contexts/MasterDataContext";
import { resolveAgentIcon } from "../../utils/agentIcon";
import { resolveResourceIcon } from "../../utils/resourceIcon";
import { resolveTransitIcon } from "../../utils/pathIcon";
import { resolveIntlLocale } from "../../utils/locale";
import { TYPE_STANDARD } from "../../constants";
import { AgentLink } from "../common/AgentLink";
import { supplyColumns, type SupplyEdge, type SupplyGraph, type SupplyNode } from "./supplyGraph";
import "./SupplyChain.css";

type EdgeShape = { edge: SupplyEdge; path: string; x: number; y: number };

export function SupplyChainGraph({ graph, onNavigate }: { graph: SupplyGraph; onNavigate?: (entityId: string) => void }) {
  const { t, i18n } = useTranslation();
  const master = useMasterData();
  const canvas = useRef<HTMLDivElement>(null);
  const frames = useRef(new Map<string, HTMLSpanElement>());
  const [shapes, setShapes] = useState<EdgeShape[]>([]);
  const [expanded, setExpanded] = useState<string | null>(null);
  const marker = `supply-arrow-${useId().replace(/:/g, "")}`;
  const columns = supplyColumns(graph);
  const format = (amount: number, compact = false) => new Intl.NumberFormat(resolveIntlLocale(i18n.language),
    { notation: compact ? "compact" : "standard", maximumFractionDigits: 1 }).format(amount);
  const productName = (pid: number) => master?.products[pid]?.display_name ?? `#${pid}`;
  const unit = (pid: number) => master?.products[pid]?.unit ?? "";
  const label = (node: SupplyNode) => {
    if (node.selected) return node.name || t("supplyGraph.selected");
    if (node.kind === "resource") return productName(node.product_id);
    if (node.variants.length === 1 && !node.importer_count) {
      return master?.agents[node.variants[0].variant]?.display_name ?? productName(node.product_id);
    }
    return t("supplyGraph.providers", { product: productName(node.product_id) });
  };
  const icon = (node: SupplyNode) => {
    if (node.kind === "resource") return resolveResourceIcon(node.product_id);
    if (node.path_variant != null) return resolveTransitIcon(node.path_variant);
    const variant = node.variant ?? (node.variants.length === 1 ? node.variants[0].variant : -1);
    const entry = master?.agents[variant];
    return resolveAgentIcon(variant, entry?.type ?? TYPE_STANDARD, entry?.building ?? false, entry?.bd_type ?? 0);
  };
  const edgeLabel = (edge: SupplyEdge) => [productName(edge.product_id), t(`supplyGraph.state.${edge.state}`),
    t(`supplyGraph.transport.${edge.transport}`),
    t("supplyGraph.recipients", { count: edge.recipient_count }),
    edge.received == null ? t("supplyGraph.noResults") : t("supplyGraph.received", { amount: format(edge.received), unit: unit(edge.product_id) }),
    t("supplyGraph.nextDemand", { amount: format(edge.demand), unit: unit(edge.product_id) }),
    edge.cycle ? t("supplyGraph.cycle") : ""].filter(Boolean).join("\n");

  useLayoutEffect(() => {
    const element = canvas.current;
    if (!element) return;
    const measure = () => {
      const origin = element.getBoundingClientRect();
      setShapes(graph.edges.flatMap((edge) => {
        const from = frames.current.get(edge.source)?.getBoundingClientRect();
        const to = frames.current.get(edge.target)?.getBoundingClientRect();
        if (!from || !to) return [];
        const x1 = from.right - origin.left, y1 = from.top + from.height / 2 - origin.top;
        const x2 = to.left - origin.left, y2 = to.top + to.height / 2 - origin.top;
        const bend = edge.cycle ? Math.max(from.bottom, to.bottom) - origin.top + 16 : (y1 + y2) / 2;
        const mid = (x1 + x2) / 2;
        const path = edge.cycle ? `M${x1},${y1} C${x1 + 20},${bend + 25} ${x2 - 20},${bend + 25} ${x2},${y2}`
          : `M${x1},${y1} C${mid},${y1} ${mid},${y2} ${x2},${y2}`;
        return [{ edge, path, x: mid, y: bend }];
      }));
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    frames.current.forEach((frame) => observer.observe(frame));
    return () => observer.disconnect();
  }, [graph, i18n.language]);

  const detail = graph.nodes.find((node) => node.id === expanded);
  return (
    <>
      <div className="supply-graph-scroll" role="region" aria-label={t("supplyGraph.title")} tabIndex={0}>
        <div className="supply-graph" ref={canvas}>
          <svg className="supply-graph-lines" aria-hidden="true">
            <defs><marker id={marker} viewBox="0 0 10 10" refX="9" refY="5" markerWidth="5" markerHeight="5" orient="auto-start-reverse">
              <path d="M0 0 L10 5 L0 10 z" fill="context-stroke" />
            </marker></defs>
            {shapes.map(({ edge, path, x, y }, index) => {
              const broken = ["disconnected", "missing", "unavailable"].includes(edge.state);
              const warning = !["ok", "unknown"].includes(edge.state) && !broken;
              return <g key={`${edge.source}:${edge.target}:${index}`} className={`supply-edge supply-edge--${edge.state}`}>
                <path d={path} markerEnd={`url(#${marker})`} />
                {(broken || warning || edge.cycle) && <g transform={`translate(${x},${y})`}>
                  <circle r="9" /><text textAnchor="middle" dominantBaseline="central">{edge.cycle ? "↻" : broken ? "×" : "!"}</text>
                </g>}
              </g>;
            })}
          </svg>
          {columns.map((nodes, index) => <div className="supply-column" key={index}>
            {nodes.map((node) => <button key={node.id} type="button"
              className={`supply-node supply-node--${node.state}${node.selected ? " supply-node--selected" : ""}`}
              aria-expanded={expanded === node.id} onClick={() => setExpanded(expanded === node.id ? null : node.id)}
              title={[label(node), t(`supplyGraph.state.${node.state}`), ...graph.edges.filter((edge) => edge.target === node.id).map(edgeLabel)].join("\n")}>
              <span className="supply-node-role">{t(`supplyGraph.role.${node.role}`)}</span>
              <span className="supply-node-frame" ref={(element) => { if (element) frames.current.set(node.id, element); else frames.current.delete(node.id); }}>
                <img src={icon(node)} alt="" />
                <span className="supply-node-mark" aria-hidden="true">{node.state === "missing" ? "−" : node.state === "inactive" ? "Ⅱ" : ["limited", "partial"].includes(node.state) ? "!" : ""}</span>
              </span>
              <span className="supply-node-name">{label(node)}</span>
              <span className="supply-node-state">{t(`supplyGraph.state.${node.state}`)}</span>
              {!node.selected && node.kind === "facility" && <span className="supply-node-count">{t("supplyGraph.activeCount", { active: node.active_count, count: node.count })}</span>}
              {node.kind === "resource" ? <span className="supply-node-amount">{format(node.resource_amount ?? 0, true)} {unit(node.product_id)}</span>
                : node.role !== "vehicle_consumer" && <span className="supply-node-amount">{node.production == null ? "—" : `${format(node.production, true)} ${unit(node.product_id)}`}</span>}
            </button>)}
          </div>)}
        </div>
      </div>
      {detail && <div className="supply-details">
        <strong>{label(detail)}</strong>
        {detail.kind === "resource" ? <p>{t("supplyGraph.resourceRemaining", { amount: format(detail.resource_amount ?? 0), unit: unit(detail.product_id) })}</p>
          : detail.role !== "vehicle_consumer" && <p>{t("supplyGraph.production", { amount: detail.production == null ? "—" : format(detail.production), unit: unit(detail.product_id) })}</p>}
        {detail.variants.map((variant) => <div className="supply-variant" key={variant.variant}>
          <span>{master?.agents[variant.variant]?.display_name ?? `#${variant.variant}`}</span>
          <span>{t("supplyGraph.activeCount", { active: variant.active_count, count: variant.count })}</span>
          {variant.production != null && <span>{format(variant.production)} {unit(detail.product_id)}</span>}
        </div>)}
        {!!detail.importer_count && <p>{t("supplyGraph.importers", { count: detail.importer_count })}</p>}
        {!detail.selected && onNavigate && detail.facilities?.map((facility) => <AgentLink
          key={facility.id} agentId={facility.id} agentName={facility.name || `#${facility.id}`} onNavigate={onNavigate} />)}
        {graph.edges.filter((edge) => edge.target === detail.id).map((edge, index) => <div className="supply-edge-detail" key={index}>
          <strong>{productName(edge.product_id)} · {t(`supplyGraph.state.${edge.state}`)}</strong>
          <span>{t(`supplyGraph.transport.${edge.transport}`)} · {t("supplyGraph.recipients", { count: edge.recipient_count })}</span>
          <span>{edge.received == null ? t("supplyGraph.noResults") : t("supplyGraph.received", { amount: format(edge.received), unit: unit(edge.product_id) })}</span>
          <span>{t("supplyGraph.nextDemand", { amount: format(edge.demand), unit: unit(edge.product_id) })}</span>
          {!!edge.path_ids.length && <span>{t("supplyGraph.capacityPaths", { ids: edge.path_ids.join(", ") })}</span>}
          {edge.cycle && <span>{t("supplyGraph.cycle")}</span>}
        </div>)}
      </div>}
      {graph.truncated && <p className="supply-note">{t("supplyGraph.truncated")}</p>}
    </>
  );
}
