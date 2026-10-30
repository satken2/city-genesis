/**
 * Breakdown of the latest Epoch's nominal GDP, shown in the right column of the finance statistics tab.
 *
 * The core builds the breakdown in its GDP phase from the same amounts it adds into GDP, so the composition,
 * the flow, the industries, and the Districts each add up to the same total. Every amount is per Epoch.
 *
 * - Headline: nominal GDP, how many Agents produced it, and per-capita / per-worker figures.
 * - Composition: private firms' value added + buildings' rent income + public facilities' wages + indirect taxes.
 * - Flow (Sankey): private firms' output splits into materials, rent, wages, and operating surplus. Rent paid by
 *   firms joins the other rent in the buildings, so the right side reads intermediate inputs plus GDP by income.
 * - Industries (agent_master industry), expandable to facility types; Districts; firms with the largest value added.
 */
import { useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { useTranslation } from "react-i18next";
import { ResponsiveContainer, Sankey, Tooltip } from "recharts";
import { resolveIntlLocale } from "../../../utils/locale";
import { formatCompactAmount, formatExactAmount } from "../../../utils/money";
import { useRemPx } from "../../../hooks/useRemPx";
import "./GdpBreakdown.css";

type VariantRow = {
  variant: number;
  name: string;
  value_added: number;
  agents: number;
  workers: number;
};

type IndustryRow = {
  industry: number; // INDUSTRY_*
  value_added: number;
  workers: number;
  variants: VariantRow[];
};

type ZoneRow = {
  zone_id: number;
  name: string;
  value_added: number;
};

type TopFirm = {
  agent_id: number;
  name: string;
  variant_name: string;
  zone_name: string;
  value_added: number;
  workers: number;
};

/** The breakdown field of GET /api/v1/stats/finance. */
export type GdpBreakdown = {
  gdp: number;
  private: number;
  building: number;
  public: number;
  consumption_tax: number;
  import_tax: number;
  private_output: number;
  private_raw_cost: number;
  business_rent: number;
  wages: number; // Includes the public facilities' wages (= public).
  population: number;
  workers: number;
  firms: number;
  buildings: number;
  facilities: number;
  exports: number;
  imports: number;
  industries: IndustryRow[];
  zones: ZoneRow[];
  top_firms: TopFirm[];
};

// Fixed categorical order (validated for adjacent pairs on the dark surface); the flow reuses the same colors.
const COLOR_PRIVATE = "#3987e5";
const COLOR_BUILDING = "#d95926";
const COLOR_PUBLIC = "#199e70";
const COLOR_TAX = "#c98500";
const COLOR_DEFICIT = "#e66767";
const COLOR_INCOME = "#7b8ea6"; // Flow sinks that make up GDP (wages, surplus, taxes).
const COLOR_INTERMEDIATE = "#4b5a6e"; // Flow sink outside GDP (materials).
const COLOR_MAGNITUDE = "#5b7fa8"; // Single-hue bars for industries and Districts.

type Formatters = {
  money: (value: number) => string;
  exact: (value: number) => string;
  share: (value: number) => string;
  count: (value: number) => string;
};

export function GdpBreakdownPanel({ breakdown }: { breakdown: GdpBreakdown }) {
  const { i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);
  const shareFormat = new Intl.NumberFormat(locale, { style: "percent", maximumFractionDigits: 1 });
  const countFormat = new Intl.NumberFormat(locale);
  const fmt: Formatters = {
    money: (v) => formatCompactAmount(v, locale, true),
    exact: (v) => formatExactAmount(Math.round(v), locale, true),
    share: (v) => (Number.isFinite(v) ? shareFormat.format(v) : "-"),
    count: (v) => countFormat.format(v),
  };
  return (
    <>
      <HeadlineCard b={breakdown} fmt={fmt} />
      <CompositionCard b={breakdown} fmt={fmt} />
      <FlowCard b={breakdown} fmt={fmt} />
      <IndustryCard b={breakdown} fmt={fmt} />
      <ZoneCard b={breakdown} fmt={fmt} />
      <TopFirmsCard b={breakdown} fmt={fmt} />
    </>
  );
}

function indirectTaxes(b: GdpBreakdown): number {
  return b.consumption_tax + b.import_tax;
}

function HeadlineCard({ b, fmt }: { b: GdpBreakdown; fmt: Formatters }) {
  const { t } = useTranslation();
  const valueAdded = b.gdp - indirectTaxes(b);
  const sources = [
    { label: t("financialPolicy.sourceFirms"), value: b.firms },
    { label: t("financialPolicy.sourceBuildings"), value: b.buildings },
    { label: t("financialPolicy.sourceFacilities"), value: b.facilities },
    { label: t("financialPolicy.sourceWorkers"), value: b.workers },
  ];
  const tiles = [
    { label: t("financialPolicy.gdpPerCapita"), value: b.population > 0 ? fmt.money(b.gdp / b.population) : "-" },
    { label: t("financialPolicy.gdpPerWorker"), value: b.workers > 0 ? fmt.money(b.gdp / b.workers) : "-" },
    { label: t("financialPolicy.laborShare"), value: valueAdded > 0 ? fmt.share(b.wages / valueAdded) : "-" },
    { label: t("financialPolicy.exports"), value: fmt.money(b.exports) },
    { label: t("financialPolicy.imports"), value: fmt.money(b.imports) },
  ];
  return (
    <section className="gdp-card gdp-headline">
      <div className="gdp-headline__label">{t("financialPolicy.latestGdp")}</div>
      <div className="gdp-headline__value" title={fmt.exact(b.gdp)}>{fmt.money(b.gdp)}</div>
      <ul className="gdp-headline__sources">
        {sources.map((s) => (
          <li key={s.label}>
            <span>{s.label}</span>
            <strong>{fmt.count(s.value)}</strong>
          </li>
        ))}
      </ul>
      <dl className="gdp-headline__tiles">
        {tiles.map((tile) => (
          <div key={tile.label} className="gdp-tile">
            <dt>{tile.label}</dt>
            <dd>{tile.value}</dd>
          </div>
        ))}
      </dl>
    </section>
  );
}

function CompositionCard({ b, fmt }: { b: GdpBreakdown; fmt: Formatters }) {
  const { t } = useTranslation();
  const parts = [
    { key: "private", label: t("financialPolicy.partPrivate"), value: b.private, color: COLOR_PRIVATE, note: "" },
    { key: "building", label: t("financialPolicy.partBuilding"), value: b.building, color: COLOR_BUILDING, note: "" },
    { key: "public", label: t("financialPolicy.partPublic"), value: b.public, color: COLOR_PUBLIC, note: "" },
    {
      key: "tax",
      label: t("financialPolicy.partTax"),
      value: indirectTaxes(b),
      color: COLOR_TAX,
      note: `${t("financialPolicy.consumptionTax")} ${fmt.money(b.consumption_tax)} · ${t("financialPolicy.importTax")} ${fmt.money(b.import_tax)}`,
    },
  ];
  // A negative part (firms losing money as a whole) has no width in the bar; its row still shows the value.
  const barTotal = parts.reduce((sum, p) => sum + Math.max(0, p.value), 0);
  return (
    <section className="gdp-card">
      <h3 className="gdp-card__title">{t("financialPolicy.compositionTitle")}</h3>
      {barTotal > 0 && (
        <div className="gdp-stack">
          {parts.filter((p) => p.value > 0).map((p) => (
            <div
              key={p.key}
              className="gdp-stack__segment"
              style={{ width: `${(p.value / barTotal) * 100}%`, background: p.color }}
              title={`${p.label}: ${fmt.exact(p.value)}`}
            />
          ))}
        </div>
      )}
      <ul className="gdp-legend">
        {parts.map((p) => (
          <li key={p.key} className="gdp-legend__row">
            <span className="gdp-legend__swatch" style={{ background: p.color }} />
            <span className="gdp-legend__label">
              {p.label}
              {p.note && <span className="gdp-legend__note">{p.note}</span>}
            </span>
            <span className="gdp-legend__value" title={fmt.exact(p.value)}>{fmt.money(p.value)}</span>
            <span className="gdp-legend__share">{b.gdp > 0 ? fmt.share(p.value / b.gdp) : "-"}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}

type FlowNodeKey =
  | "output" | "deficit" | "otherRent" | "public" | "consumptionTax" | "importTax"
  | "building" | "materials" | "compensation" | "surplus" | "taxes";

// Node order is the drawing order (sort is off), so sources stay in the composition's color order.
const FLOW_NODES: { key: FlowNodeKey; labelKey: string; color: string; source: boolean }[] = [
  { key: "output", labelKey: "financialPolicy.flowOutput", color: COLOR_PRIVATE, source: true },
  { key: "deficit", labelKey: "financialPolicy.flowDeficit", color: COLOR_DEFICIT, source: true },
  { key: "otherRent", labelKey: "financialPolicy.flowOtherRent", color: COLOR_BUILDING, source: true },
  { key: "public", labelKey: "financialPolicy.flowPublic", color: COLOR_PUBLIC, source: true },
  { key: "consumptionTax", labelKey: "financialPolicy.consumptionTax", color: COLOR_TAX, source: true },
  { key: "importTax", labelKey: "financialPolicy.importTax", color: COLOR_TAX, source: true },
  { key: "building", labelKey: "financialPolicy.flowBuilding", color: COLOR_BUILDING, source: false },
  { key: "materials", labelKey: "financialPolicy.flowMaterials", color: COLOR_INTERMEDIATE, source: false },
  { key: "compensation", labelKey: "financialPolicy.flowCompensation", color: COLOR_INCOME, source: false },
  { key: "surplus", labelKey: "financialPolicy.flowSurplus", color: COLOR_INCOME, source: false },
  { key: "taxes", labelKey: "financialPolicy.flowTaxes", color: COLOR_INCOME, source: false },
];

type Flow = [from: FlowNodeKey, to: FlowNodeKey, value: number];

// Flows below one currency unit are rounding noise and would draw hairline links.
const MIN_FLOW = 1;

/**
 * Pairs sources with uses in order, so that each use is filled by the sources in turn. The total of both sides
 * must match; the flows only say which source covers which use, not an actual payment between them.
 */
function allocateFlows(sources: [FlowNodeKey, number][], uses: [FlowNodeKey, number][]): Flow[] {
  const flows: Flow[] = [];
  const remaining = sources.map(([key, value]) => [key, Math.max(0, value)] as [FlowNodeKey, number]);
  let si = 0;
  for (const [useKey, useValue] of uses) {
    let need = Math.max(0, useValue);
    while (need > 0 && si < remaining.length) {
      const amount = Math.min(need, remaining[si][1]);
      if (amount > 0) flows.push([remaining[si][0], useKey, amount]);
      need -= amount;
      remaining[si][1] -= amount;
      if (remaining[si][1] <= 0) si += 1;
    }
  }
  return flows;
}

/**
 * Builds the flow from the breakdown totals. Private firms' output = materials + rent + wages + operating surplus;
 * when the operating surplus is negative, the loss enters as a source that covers part of the uses (Sankey links
 * cannot be negative). Private wages are the total wages minus the public facilities' wages.
 */
function buildFlow(b: GdpBreakdown, label: (key: string) => string) {
  const privateWages = Math.max(0, b.wages - b.public);
  const privateSurplus = b.private - privateWages;
  const flows: Flow[] = [
    ...allocateFlows(
      [["output", b.private_output], ["deficit", -privateSurplus]],
      [["materials", b.private_raw_cost], ["building", b.business_rent], ["compensation", privateWages], ["surplus", privateSurplus]]
    ),
    ["otherRent", "building", b.building - b.business_rent],
    ["building", "surplus", b.building],
    ["public", "compensation", b.public],
    ["consumptionTax", "taxes", b.consumption_tax],
    ["importTax", "taxes", b.import_tax],
  ];
  const used = flows.filter(([, , value]) => value >= MIN_FLOW);
  const nodes = FLOW_NODES.filter((n) => used.some(([from, to]) => from === n.key || to === n.key));
  const index = new Map(nodes.map((n, i) => [n.key, i]));
  return {
    nodes: nodes.map((n) => ({ name: label(n.labelKey), color: n.color, source: n.source })),
    links: used.map(([from, to, value]) => ({ source: index.get(from)!, target: index.get(to)!, value })),
  };
}

function FlowCard({ b, fmt }: { b: GdpBreakdown; fmt: Formatters }) {
  const { t } = useTranslation();
  const remPx = useRemPx();
  const data = buildFlow(b, (key) => t(key));
  return (
    <section className="gdp-card">
      <h3 className="gdp-card__title">{t("financialPolicy.flowTitle")}</h3>
      <div className="gdp-flow">
        {data.links.length > 0 && (
          <ResponsiveContainer width="100%" height="100%">
            <Sankey
              data={data}
              sort={false}
              nodeWidth={10}
              nodePadding={remPx(2.2)}
              linkCurvature={0.5}
              margin={{ top: remPx(1), right: remPx(12.5), bottom: remPx(1.4), left: remPx(12.5) }}
              node={(props: any) => {
                const node = props.payload;
                const labelX = node.source ? props.x - 6 : props.x + props.width + 6;
                const anchor = node.source ? "end" : "start";
                const midY = props.y + props.height / 2;
                return (
                  <g>
                    <rect x={props.x} y={props.y} width={props.width} height={Math.max(props.height, 1)} fill={node.color} rx={2} />
                    <text x={labelX} y={midY} textAnchor={anchor} fontSize="0.93rem">
                      <tspan x={labelX} dy="-0.15em" fill="#cbd5e1">{node.name}</tspan>
                      <tspan x={labelX} dy="1.15em" fill="#8ba3bb">{fmt.money(node.value)}</tspan>
                    </text>
                  </g>
                );
              }}
              link={(props: any) => (
                <path
                  d={`M${props.sourceX},${props.sourceY}C${props.sourceControlX},${props.sourceY} ${props.targetControlX},${props.targetY} ${props.targetX},${props.targetY}`}
                  fill="none"
                  stroke={props.payload?.source?.color ?? COLOR_INCOME}
                  strokeWidth={Math.max(props.linkWidth, 1)}
                  strokeOpacity={0.35}
                />
              )}
            >
              <Tooltip
                content={({ active, payload }: any) => {
                  const item = active ? payload?.[0]?.payload?.payload : null;
                  if (!item) return null;
                  const name = item.source && item.target ? `${item.source.name} → ${item.target.name}` : item.name;
                  return (
                    <div className="gdp-tooltip">
                      <div>{name}</div>
                      <strong>{fmt.exact(item.value)}</strong>
                    </div>
                  );
                }}
              />
            </Sankey>
          </ResponsiveContainer>
        )}
      </div>
    </section>
  );
}

function MagnitudeBar({ value, max }: { value: number; max: number }) {
  const width = max > 0 ? (Math.max(0, value) / max) * 100 : 0;
  return (
    <span className="gdp-bar">
      <span className="gdp-bar__fill" style={{ width: `${width}%`, background: COLOR_MAGNITUDE }} />
    </span>
  );
}

function IndustryCard({ b, fmt }: { b: GdpBreakdown; fmt: Formatters }) {
  const { t } = useTranslation();
  const [expanded, setExpanded] = useState<number | null>(null);
  const total = b.gdp - indirectTaxes(b);
  const max = Math.max(0, ...b.industries.map((row) => row.value_added));
  return (
    <section className="gdp-card">
      <h3 className="gdp-card__title">{t("financialPolicy.industryTitle")}</h3>
      <ul className="gdp-rows">
        {b.industries.map((row) => {
          const open = expanded === row.industry;
          return (
            <li key={row.industry}>
              <button
                type="button"
                className="gdp-row gdp-row--button"
                aria-expanded={open}
                onClick={() => setExpanded(open ? null : row.industry)}
              >
                <span className={`gdp-row__chevron${open ? " gdp-row__chevron--open" : ""}`} aria-hidden="true">›</span>
                <span className="gdp-row__name">{t(`financialPolicy.industry.${row.industry}`)}</span>
                <MagnitudeBar value={row.value_added} max={max} />
                <span className="gdp-row__value" title={fmt.exact(row.value_added)}>{fmt.money(row.value_added)}</span>
                <span className="gdp-row__share">{total > 0 ? fmt.share(row.value_added / total) : "-"}</span>
              </button>
              {open && (
                <ul className="gdp-subrows">
                  {row.variants.map((v) => (
                    <li key={v.variant} className="gdp-row gdp-row--sub">
                      <span className="gdp-row__name">
                        {v.name}
                        <span className="gdp-row__meta">
                          {t("financialPolicy.facilityCount")} {fmt.count(v.agents)}
                          {v.workers > 0 &&
                            ` · ${t("financialPolicy.sourceWorkers")} ${fmt.count(v.workers)} · ${t("financialPolicy.perWorker")} ${fmt.money(v.value_added / v.workers)}`}
                        </span>
                      </span>
                      <MagnitudeBar value={v.value_added} max={max} />
                      <span className="gdp-row__value" title={fmt.exact(v.value_added)}>{fmt.money(v.value_added)}</span>
                      <span className="gdp-row__share">{total > 0 ? fmt.share(v.value_added / total) : "-"}</span>
                    </li>
                  ))}
                </ul>
              )}
            </li>
          );
        })}
      </ul>
    </section>
  );
}

function ZoneCard({ b, fmt }: { b: GdpBreakdown; fmt: Formatters }) {
  const { t } = useTranslation();
  const total = b.gdp - indirectTaxes(b);
  const max = Math.max(0, ...b.zones.map((row) => row.value_added));
  return (
    <section className="gdp-card">
      <h3 className="gdp-card__title">{t("financialPolicy.zoneTitle")}</h3>
      <ul className="gdp-rows gdp-rows--scroll">
        {b.zones.map((row) => (
          <li key={row.zone_id} className="gdp-row">
            <span className="gdp-row__name">{row.name || t("financialPolicy.noZone")}</span>
            <MagnitudeBar value={row.value_added} max={max} />
            <span className="gdp-row__value" title={fmt.exact(row.value_added)}>{fmt.money(row.value_added)}</span>
            <span className="gdp-row__share">{total > 0 ? fmt.share(row.value_added / total) : "-"}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}

function TopFirmsCard({ b, fmt }: { b: GdpBreakdown; fmt: Formatters }) {
  const { t } = useTranslation();
  if (b.top_firms.length === 0) return null;
  const openAgentDetail = (agentId: number) => {
    void invoke("open_registered_window", { windowType: "agent_detail", entityId: String(agentId) });
  };
  return (
    <section className="gdp-card">
      <h3 className="gdp-card__title">{t("financialPolicy.topFirmsTitle")}</h3>
      <table className="gdp-table">
        <thead>
          <tr>
            <th>{t("financialPolicy.colFirm")}</th>
            <th>{t("financialPolicy.colType")}</th>
            <th>{t("financialPolicy.colZone")}</th>
            <th className="gdp-table__num">{t("financialPolicy.colWorkers")}</th>
            <th className="gdp-table__num">{t("financialPolicy.colPerWorker")}</th>
            <th className="gdp-table__num">{t("financialPolicy.colValueAdded")}</th>
          </tr>
        </thead>
        <tbody>
          {b.top_firms.map((firm) => (
            <tr key={firm.agent_id}>
              <td>
                <button type="button" className="gdp-table__link" onClick={() => openAgentDetail(firm.agent_id)}>
                  {firm.name}
                </button>
              </td>
              <td>{firm.variant_name}</td>
              <td>{firm.zone_name || t("financialPolicy.noZone")}</td>
              <td className="gdp-table__num">{fmt.count(firm.workers)}</td>
              <td className="gdp-table__num">{firm.workers > 0 ? fmt.money(firm.value_added / firm.workers) : "-"}</td>
              <td className="gdp-table__num" title={fmt.exact(firm.value_added)}>{fmt.money(firm.value_added)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
