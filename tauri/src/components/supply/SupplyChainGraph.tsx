/** 集約済みの一系列を描く共通部品。取得とウィンドウ管理を持たない。 */
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

/** 財ごとの共通アイコン。都市にある施設の種類・件数で切り替えない。 */
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
};

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
    return t(`supplyGraph.facilities.${node.product_id}`, {
      defaultValue: t("supplyGraph.providers", { product: productName(node.product_id) }),
    });
  };
  const icon = (node: SupplyNode) => {
    if (node.kind === "resource") return resolveResourceIcon(node.product_id);
    if (node.path_variant != null) return resolveTransitIcon(node.path_variant);
    const variant = node.selected ? node.variant ?? -1 : SUPPLIER_ICON_VARIANTS[node.product_id] ?? -1;
    const entry = master?.agents[variant];
    return resolveAgentIcon(variant, entry?.type ?? TYPE_STANDARD, entry?.building ?? false, entry?.bd_type ?? 0);
  };

  return <div className="supply-graph-scroll" role="region" aria-label={t("supplyGraph.title")} tabIndex={0}>
    <div className="supply-graph">
      {graph.nodes.map((node, index) => {
        const edge = graph.edges[index - 1];
        const broken = edge && ["disconnected", "missing"].includes(edge.state);
        const warning = edge && !["ok", "unknown"].includes(edge.state) && !broken;
        const amount = node.production == null ? "—" : format(node.production);
        return <Fragment key={node.id}>
          {edge && <svg className={`supply-edge supply-edge--${edge.state}`} viewBox="0 0 40 40"
            role="img" aria-label={t(`supplyGraph.state.${edge.state}`)}>
            <title>{productName(edge.product_id)}: {t(`supplyGraph.state.${edge.state}`)}</title>
            <path d="M0 20 H34 M27 13 L34 20 L27 27" />
            {(broken || warning) && <g>
              <circle cx="17" cy="20" r="8" />
              <text x="17" y="20" textAnchor="middle" dominantBaseline="central">{broken ? "×" : "!"}</text>
            </g>}
          </svg>}
          <div className={`supply-node supply-node--${node.state}${node.selected ? " supply-node--selected" : ""}`}>
            <span className="supply-node-role">{t(`supplyGraph.role.${node.role}`)}</span>
            <span className="supply-node-frame"><img src={icon(node)} alt="" /></span>
            <span className="supply-node-name">{label(node)}</span>
            <span className="supply-node-state">{t(`supplyGraph.state.${node.state}`)}</span>
            {!node.selected && node.kind === "facility" && <span className="supply-node-count">
              {t("supplyGraph.activeCount", { active: node.active_count, count: node.count })}
            </span>}
            {node.kind === "resource"
              ? <span className="supply-node-amount" title={t("supplyGraph.resourceRemaining", { amount: format(node.resource_amount ?? 0), unit: unit(node.product_id) })}>
                {format(node.resource_amount ?? 0)} {unit(node.product_id)}
              </span>
              : node.role !== "vehicle_consumer" && <span className="supply-node-amount"
                title={t("supplyGraph.production", { amount, unit: unit(node.product_id) })}>
                {amount} {unit(node.product_id)}
              </span>}
          </div>
        </Fragment>;
      })}
    </div>
  </div>;
}
