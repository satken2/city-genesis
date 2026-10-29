/** Shared component rendering one aggregated series, without retrieval or window management. */
import { Fragment } from "react";
import { useTranslation } from "react-i18next";
import { useMasterData } from "../../contexts/MasterDataContext";
import { resolveAgentIcon } from "../../utils/agentIcon";
import { resolveResourceIcon } from "../../utils/resourceIcon";
import { resolveTransitIcon } from "../../utils/pathIcon";
import { resolveIntlLocale } from "../../utils/locale";
import { TYPE_STANDARD } from "../../constants";
import type { SupplyGraph, SupplyNode } from "./supplyGraph";
import "./SupplyChain.css";

/** Shared icon per good; do not vary it by facility types/counts present in the city. */
const SUPPLIER_ICON_VARIANTS: Record<number, number> = {
  401: 1056,
  402: 1048,
  403: 1079,
  404: 1081,
  405: 1077,
  501: 1060,
  502: 1061,
  503: 1065,
  504: 1064,
  505: 1092,
};

const CONSUMER_ICON_VARIANTS: Record<number, number> = {
  401: 1062,
  402: 1063,
  403: 1073,
  404: 404,
  405: 1078,
  406: 403,
};
const CONSUMER_ROUTE_VARIANTS: Record<number, number> = { 501: 1005, 502: 1001, 503: 1003, 504: 1004, 505: 1009 };

export function SupplyChainGraph({ graph }: { graph: SupplyGraph }) {
  const { t, i18n } = useTranslation();
  const master = useMasterData();
  const format = (amount: number) => new Intl.NumberFormat(resolveIntlLocale(i18n.language),
    { notation: "compact", maximumFractionDigits: 1 }).format(amount);
  const productName = (pid: number) => master?.products[pid]?.display_name ?? `#${pid}`;
  const unit = (pid: number) => master?.products[pid]?.unit ?? "";
  const label = (node: SupplyNode) => {
    if (node.selected) return node.name || t("supplyGraph.selected");
    if (node.kind === "resource") return productName(node.product_id);
    if (node.kind === "consumer") return t(`supplyGraph.consumers.${node.product_id}`);
    return t(`supplyGraph.facilities.${node.product_id}`, {
      defaultValue: t("supplyGraph.providers", { product: productName(node.product_id) }),
    });
  };
  const icon = (node: SupplyNode) => {
    if (node.kind === "resource") return resolveResourceIcon(node.product_id);
    if (node.path_variant != null) return resolveTransitIcon(node.path_variant);
    if (node.kind === "consumer" && CONSUMER_ROUTE_VARIANTS[node.product_id] != null) {
      return resolveTransitIcon(CONSUMER_ROUTE_VARIANTS[node.product_id]);
    }
    const variants = node.kind === "consumer" ? CONSUMER_ICON_VARIANTS : SUPPLIER_ICON_VARIANTS;
    const variant = node.selected ? node.variant ?? -1 : variants[node.product_id] ?? -1;
    const entry = master?.agents[variant];
    return resolveAgentIcon(variant, entry?.type ?? TYPE_STANDARD, entry?.building ?? false, entry?.bd_type ?? 0);
  };

  return <div className="supply-graph-scroll" role="region" aria-label={t("supplyGraph.title")} tabIndex={0}>
    <div className="supply-graph">
      {graph.nodes.map((node, index) => {
        const edge = graph.edges[index - 1];
        const broken = edge && ["disconnected", "missing"].includes(edge.state);
        const warning = edge && !["ok", "unknown"].includes(edge.state) && !broken;
        const flowing = edge && (edge.received ?? 0) > 0 && !["missing", "disconnected", "inactive"].includes(edge.state);
        const amount = node.production == null ? "—" : format(node.production);
        return <Fragment key={node.id}>
          {edge && <svg className={`supply-edge supply-edge--${edge.state}`} viewBox="0 0 78 76"
            role="img" aria-label={t(`supplyGraph.state.${edge.state}`)}>
            <title>{productName(edge.product_id)}: {t(`supplyGraph.state.${edge.state}`)}</title>
            <path className="supply-edge-line" d="M0 38 H78" />
            {flowing && <path className="supply-edge-flow" d="M0 38 H78" />}
            {(broken || warning) && <g>
              <circle className="supply-edge-warning" cx="39" cy="38" r="16" />
              {broken
                ? <path className="supply-edge-symbol" d="M31 30 L47 46 M47 30 L31 46" />
                : <>
                  <path className="supply-edge-symbol" d="M39 28 V39" />
                  <circle className="supply-edge-symbol-dot" cx="39" cy="46" r="2" />
                </>}
            </g>}
          </svg>}
          <div className={`supply-node supply-node--${node.state}${node.selected ? " supply-node--selected" : ""}`}>
            <span className="supply-node-frame"><img src={icon(node)} alt="" /></span>
            <span className="supply-node-name">{label(node)}</span>
            {!node.selected && node.kind !== "resource" && <span className="supply-node-count">
              {t("supplyGraph.activeCount", { active: node.active_count, count: node.count })}
            </span>}
            {node.kind === "resource"
              ? <span className="supply-node-amount" title={t("supplyGraph.resourceRemaining", { amount: format(node.resource_amount ?? 0), unit: unit(node.product_id) })}>
                {format(node.resource_amount ?? 0)} {unit(node.product_id)}
              </span>
              : node.kind !== "consumer" && node.role !== "vehicle_consumer" && <span className="supply-node-amount"
                title={t("supplyGraph.production", { amount, unit: unit(node.product_id) })}>
                {amount} {unit(node.product_id)}
              </span>}
          </div>
        </Fragment>;
      })}
    </div>
  </div>;
}
