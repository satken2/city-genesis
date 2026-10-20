/**
 * Economic trend overlay by good.
 *
 * Opens over the statistics window's "Economy" tab when a table row is clicked.
 * Fetches time series by good (average price, production, consumption, exports, imports, and
 * order quantity) from GET /api/v1/stats/market_price, and displays trends for the clicked good
 * in a four-section grid.
 *
 * Layout (two columns × two rows):
 *   - Top left: horizontal split (left = scope dropdowns, right = latest Epoch values).
 *   - Top right: average price history.
 *   - Bottom left: Sankey diagram (goods flow in the latest Epoch).
 *   - Bottom right: supply/demand history (production, consumption, exports, imports, and
 *     order quantity (dotted) combined in one chart).
 *
 * Currently supports only the entire city. A District dropdown is provided for future expansion;
 * selecting a District shows the same city-wide data, with an unimplemented-feature notice.
 * Close by clicking the backdrop outside the box or the close button.
 */

import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  LineChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
  ResponsiveContainer,
  Sankey,
} from "recharts";
import { useEpochCachedStats } from "../useEpochCache";
import "./EconomyTrendOverlay.css";
import { useRemPx } from "../../../hooks/useRemPx";

/** Per-product history data from /api/v1/stats/market_price. */
type MarketPriceProduct = {
  product_id: number;
  product_name: string;
  unit: string;
  type: number;
  average_prices: number[];
  total_production: number[];
  total_order: number[];
  total_consumption: number[];
  total_exports: number[];
  total_imports: number[];
  total_self_supply: number[];
};

/** Complete /api/v1/stats/market_price response. */
type MarketPriceStats = {
  epoch: number;
  epochs: number[];
  products: MarketPriceProduct[];
};

// Chart series colors.
const COLOR_PRICE = "#fbbf24";       // Average price: amber.
const COLOR_PRODUCTION = "#4ea1ff";  // Production: blue.
const COLOR_CONSUMPTION = "#c084fc"; // Consumption (actual): purple.
const COLOR_ORDER = "#94a3b8";       // Order quantity (planned demand): slate gray (dotted).
const COLOR_EXPORT = "#10b981";      // Exports: emerald.
const COLOR_IMPORT = "#ef4444";      // Imports: red.
const COLOR_SELFSUPPLY = "#14b8a6";  // Self-supply: teal.
const COLOR_POOL = "#64748b";        // Supply pool: slate gray.
const COLOR_UNSOLD = "#f59e0b";      // Unsold goods: amber.
const COLOR_SHORTAGE = "#f97316";    // Shortage: orange.

// Shared Recharts style constants (same theme as other tabs).
const CHART_MARGIN = { top: 12, right: 16, left: -10, bottom: 0 };
const Y_AXIS_WIDTH_REM = 4.44; // Y-axis width (rem). Convert to px with useRemPx when rendering.
const AXIS_TICK = { fontSize: "0.93rem", fill: "#8ba3bb" };
const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "1rem",
};

/**
 * Converts arrays of Epoch numbers and values to Recharts data points.
 * Assumes equal-length arrays ordered from oldest to newest Epoch.
 */
function zipToChartData(epochs: number[], values: number[]): Array<{ epoch: number; value: number }> {
  const len = Math.min(epochs.length, values.length);
  const out: Array<{ epoch: number; value: number }> = [];
  for (let i = 0; i < len; i++) {
    out.push({ epoch: epochs[i], value: values[i] });
  }
  return out;
}

/**
 * Card rendering a time series chart.
 * Accepts a title, color, data, unit, and value formatter.
 *
 * If secondaryData is supplied, overlays a solid primary series and a dotted secondary series,
 * with a legend. Used by the consumption history card to show "Actual consumption (solid)"
 * alongside "Order quantity (dotted)". With only a primary series, renders one solid line as before.
 */
function TrendChartCard({
  title,
  data,
  color,
  formatter,
  noDataLabel,
  primaryLabel,
  secondaryData,
  secondaryColor,
  secondaryLabel,
}: {
  title: string;
  data: Array<{ epoch: number; value: number }>;
  color: string;
  formatter: (value: number) => string;
  noDataLabel: string;
  primaryLabel?: string;
  secondaryData?: Array<{ epoch: number; value: number }>;
  secondaryColor?: string;
  secondaryLabel?: string;
}) {
  const remPx = useRemPx();
  const hasSecondary = !!secondaryData;

  // Combine primary and secondary series into {epoch, value, secondary} data.
  // Omit secondary in cards without a secondary series, preserving single-series rendering behavior.
  const chartData = data.map((d, i) => ({
    epoch: d.epoch,
    value: d.value,
    secondary: hasSecondary ? secondaryData![i]?.value ?? 0 : 0,
  }));

  // Treat the chart as having data if either series contains a nonzero value.
  const hasData =
    chartData.length > 0 &&
    chartData.some((d) => d.value !== 0 || (hasSecondary && d.secondary !== 0));

  return (
    <div className="economy-trend-card">
      <h3 className="economy-trend-card__title">{title}</h3>
      <div className="economy-trend-card__chart-container">
        {hasData ? (
          <ResponsiveContainer width="100%" height="100%">
            <LineChart data={chartData} margin={CHART_MARGIN}>
              <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
              <XAxis
                dataKey="epoch"
                tick={AXIS_TICK}
                tickLine={false}
                axisLine={{ stroke: "#253246" }}
              />
              <YAxis
                width={remPx(Y_AXIS_WIDTH_REM)}
                tick={AXIS_TICK}
                tickLine={false}
                axisLine={{ stroke: "#253246" }}
                domain={["auto", "auto"]}
                tickFormatter={(v: number) => formatter(v)}
              />
              <Tooltip
                contentStyle={TOOLTIP_STYLE}
                labelFormatter={(label) => `Epoch ${label}`}
                formatter={(value: any) => formatter(Number(value))}
              />
              {hasSecondary && (
                <Legend wrapperStyle={{ fontSize: "1rem" }} iconType="plainline" />
              )}
              <Line
                type="monotone"
                dataKey="value"
                name={primaryLabel}
                stroke={color}
                strokeWidth={2}
                dot={false}
                activeDot={{ r: 4 }}
                isAnimationActive={false}
              />
              {hasSecondary && (
                <Line
                  type="monotone"
                  dataKey="secondary"
                  name={secondaryLabel}
                  stroke={secondaryColor ?? "#94a3b8"}
                  strokeWidth={2}
                  strokeDasharray="5 4"
                  dot={false}
                  activeDot={{ r: 4 }}
                  isAnimationActive={false}
                />
              )}
            </LineChart>
          </ResponsiveContainer>
        ) : (
          <div className="economy-trend-card__no-data">{noDataLabel}</div>
        )}
      </div>
    </div>
  );
}

/** Sankey diagram node and link data. */
type SankeyData = {
  nodes: Array<{ name: string }>;
  links: Array<{ source: number; target: number; value: number }>;
};

/**
 * Supply/demand history card.
 *
 * Combines production, consumption, exports, and imports as solid lines and order quantity
 * as a dotted line in one chart. Merging the previously separate four cards makes it possible
 * to compare the balance of all series in one view. Order quantity alone is dotted to retain
 * the contrast between consumption (solid) and orders (dotted).
 */
function SupplyDemandTrendCard({
  title,
  epochs,
  production,
  consumption,
  exports,
  imports,
  order,
  formatter,
  noDataLabel,
  labels,
}: {
  title: string;
  epochs: number[];
  production: number[];
  consumption: number[];
  exports: number[];
  imports: number[];
  order: number[];
  formatter: (value: number) => string;
  noDataLabel: string;
  labels: {
    production: string;
    consumption: string;
    export: string;
    import: string;
    order: string;
  };
}) {
  const remPx = useRemPx();
  // Align all series to the same length and convert them into Recharts data points.
  // Advance indices against epochs, assuming oldest-to-newest Epoch order.
  const len = Math.min(
    epochs.length,
    production.length,
    consumption.length,
    exports.length,
    imports.length,
    order.length
  );
  const chartData: Array<{
    epoch: number;
    production: number;
    consumption: number;
    exports: number;
    imports: number;
    order: number;
  }> = [];
  for (let i = 0; i < len; i++) {
    chartData.push({
      epoch: epochs[i],
      production: production[i] ?? 0,
      consumption: consumption[i] ?? 0,
      exports: exports[i] ?? 0,
      imports: imports[i] ?? 0,
      order: order[i] ?? 0,
    });
  }

  // Treat the chart as having data if any series contains a nonzero value.
  const hasData =
    chartData.length > 0 &&
    chartData.some(
      (d) =>
        d.production !== 0 ||
        d.consumption !== 0 ||
        d.exports !== 0 ||
        d.imports !== 0 ||
        d.order !== 0
    );

  return (
    <div className="economy-trend-card">
      <h3 className="economy-trend-card__title">{title}</h3>
      <div className="economy-trend-card__chart-container">
        {hasData ? (
          <ResponsiveContainer width="100%" height="100%">
            <LineChart data={chartData} margin={CHART_MARGIN}>
              <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
              <XAxis
                dataKey="epoch"
                tick={AXIS_TICK}
                tickLine={false}
                axisLine={{ stroke: "#253246" }}
              />
              <YAxis
                width={remPx(Y_AXIS_WIDTH_REM)}
                tick={AXIS_TICK}
                tickLine={false}
                axisLine={{ stroke: "#253246" }}
                domain={["auto", "auto"]}
                tickFormatter={(v: number) => formatter(v)}
              />
              <Tooltip
                contentStyle={TOOLTIP_STYLE}
                labelFormatter={(label) => `Epoch ${label}`}
                formatter={(value: any) => formatter(Number(value))}
              />
              <Legend wrapperStyle={{ fontSize: "1rem" }} iconType="plainline" />
              <Line
                type="monotone"
                dataKey="production"
                name={labels.production}
                stroke={COLOR_PRODUCTION}
                strokeWidth={2}
                dot={false}
                activeDot={{ r: 4 }}
                isAnimationActive={false}
              />
              <Line
                type="monotone"
                dataKey="consumption"
                name={labels.consumption}
                stroke={COLOR_CONSUMPTION}
                strokeWidth={2}
                dot={false}
                activeDot={{ r: 4 }}
                isAnimationActive={false}
              />
              <Line
                type="monotone"
                dataKey="exports"
                name={labels.export}
                stroke={COLOR_EXPORT}
                strokeWidth={2}
                dot={false}
                activeDot={{ r: 4 }}
                isAnimationActive={false}
              />
              <Line
                type="monotone"
                dataKey="imports"
                name={labels.import}
                stroke={COLOR_IMPORT}
                strokeWidth={2}
                dot={false}
                activeDot={{ r: 4 }}
                isAnimationActive={false}
              />
              {/* Order quantity is a reference for planned demand, so draw it dotted to distinguish it from the solid series. */}
              <Line
                type="monotone"
                dataKey="order"
                name={labels.order}
                stroke={COLOR_ORDER}
                strokeWidth={2}
                strokeDasharray="5 4"
                dot={false}
                activeDot={{ r: 4 }}
                isAnimationActive={false}
              />
            </LineChart>
          </ResponsiveContainer>
        ) : (
          <div className="economy-trend-card__no-data">{noDataLabel}</div>
        )}
      </div>
    </div>
  );
}

/**
 * Builds Sankey diagram data from the selected good's latest Epoch values.
 *
 * Topology (two stages):
 *   [Production][Imports][Self-supply] → [Supply pool] → [Consumption][Exports][Unsold/Shortage]
 *
 * "Unsold" appears as an output node when total input exceeds total output (excess supply),
 * while "Shortage" appears as an input node when total output exceeds total input (excess demand).
 * This keeps the Sankey flows balanced.
 *
 * total_production is production in the current Epoch, whereas total_imports / total_exports
 * are recorded trades, so these do not strictly match net production/trading in that Epoch.
 * In particular, sales from carried inventory for industrial materials (GOODS_MATERIAL)
 * appear in the "Shortage" node.
 */
function buildSankeyData(
  production: number,
  imports: number,
  selfSupply: number,
  consumption: number,
  exports: number,
  labels: {
    production: string;
    imports: string;
    selfSupply: string;
    pool: string;
    consumption: string;
    exports: string;
    unsold: string;
    shortage: string;
  }
): SankeyData | null {
  const totalInput = production + imports + selfSupply;
  const totalOutput = consumption + exports;
  const residual = totalInput - totalOutput;

  // No data if every flow is zero.
  if (totalInput === 0 && totalOutput === 0) {
    return null;
  }

  const isShortage = residual < 0;
  const residualAbs = Math.abs(residual);

  // Exclude zero-valued links because Recharts Sankey cannot draw zero-width links correctly.
  if (isShortage) {
    // Shortage (demand > supply): the shortage node appears on the input side.
    // [Production][Imports][Self-supply][Shortage] → [Supply pool] → [Consumption][Exports]
    const nodes = [
      { name: labels.production },
      { name: labels.imports },
      { name: labels.selfSupply },
      { name: labels.shortage },
      { name: labels.pool },
      { name: labels.consumption },
      { name: labels.exports },
    ];
    const links: Array<{ source: number; target: number; value: number }> = [];
    if (production > 0) links.push({ source: 0, target: 4, value: production });
    if (imports > 0) links.push({ source: 1, target: 4, value: imports });
    if (selfSupply > 0) links.push({ source: 2, target: 4, value: selfSupply });
    if (residualAbs > 0) links.push({ source: 3, target: 4, value: residualAbs });
    if (consumption > 0) links.push({ source: 4, target: 5, value: consumption });
    if (exports > 0) links.push({ source: 4, target: 6, value: exports });
    return { nodes, links };
  } else {
    // Unsold goods (supply ≥ demand): the unsold node appears on the output side.
    // [Production][Imports][Self-supply] → [Supply pool] → [Consumption][Exports][Unsold]
    const nodes = [
      { name: labels.production },
      { name: labels.imports },
      { name: labels.selfSupply },
      { name: labels.pool },
      { name: labels.consumption },
      { name: labels.exports },
      { name: labels.unsold },
    ];
    const links: Array<{ source: number; target: number; value: number }> = [];
    if (production > 0) links.push({ source: 0, target: 3, value: production });
    if (imports > 0) links.push({ source: 1, target: 3, value: imports });
    if (selfSupply > 0) links.push({ source: 2, target: 3, value: selfSupply });
    if (consumption > 0) links.push({ source: 3, target: 4, value: consumption });
    if (exports > 0) links.push({ source: 3, target: 5, value: exports });
    if (residualAbs > 0) links.push({ source: 3, target: 6, value: residualAbs });
    return { nodes, links };
  }
}

/**
 * Sankey diagram card.
 *
 * Uses a two-stage Sankey to visualize goods flow from production/imports/self-supply to
 * consumption/exports/unsold goods/shortage for the selected good's latest Epoch. Inserting
 * a central "Supply pool" node enables rendering from totals alone, even when pairwise
 * flow quantities between inputs and outputs are unknown.
 *
 * Node colors distinguish inputs (production = blue / imports = red / self-supply = teal),
 * the supply pool (slate gray), and outputs (consumption = purple / exports = green /
 * unsold goods = amber / shortage = orange). Links use the source node's translucent color.
 */
function SankeyFlowCard({
  title,
  sankeyData,
  isShortage,
  noDataLabel,
}: {
  title: string;
  sankeyData: SankeyData | null;
  isShortage: boolean;
  noDataLabel: string;
}) {
  // Node color map. Indices correspond to node order in buildSankeyData.
  // Node order differs between unsold and shortage cases, so switch with isShortage.
  const nodeColors = isShortage
    ? [
        COLOR_PRODUCTION,  // 0: Production.
        COLOR_IMPORT,      // 1: Imports.
        COLOR_SELFSUPPLY,  // 2: Self-supply.
        COLOR_SHORTAGE,    // 3: Shortage.
        COLOR_POOL,        // 4: Supply pool.
        COLOR_CONSUMPTION, // 5: Consumption.
        COLOR_EXPORT,      // 6: Exports.
      ]
    : [
        COLOR_PRODUCTION,  // 0: Production.
        COLOR_IMPORT,      // 1: Imports.
        COLOR_SELFSUPPLY,  // 2: Self-supply.
        COLOR_POOL,        // 3: Supply pool.
        COLOR_CONSUMPTION, // 4: Consumption.
        COLOR_EXPORT,      // 5: Exports.
        COLOR_UNSOLD,      // 6: Unsold goods.
      ];

  // Exclusive upper index bound for input nodes (indices below this value are inputs).
  // Inputs are 0–2 with unsold goods and 0–3 with a shortage.
  const inputNodeCount = isShortage ? 4 : 3;

  // Supply pool node index.
  // The central pool node is a relay without a real-world entity, so omit its label (title).
  const poolNodeIndex = isShortage ? 4 : 3;

  const hasData = sankeyData !== null && sankeyData.links.length > 0;

  return (
    <div className="economy-trend-card">
      <h3 className="economy-trend-card__title">{title}</h3>
      <div className="economy-trend-card__chart-container">
        {hasData && sankeyData ? (
          <ResponsiveContainer width="100%" height="100%">
            <Sankey
              data={sankeyData}
              nodeWidth={12}
              nodePadding={20}
              linkCurvature={0.5}
              margin={{ top: 8, right: 90, bottom: 8, left: 90 }}
              node={(props: any) => {
                const color = nodeColors[props.index] ?? COLOR_POOL;
                // Place labels to the left of input nodes (left edge), and to the right of all others.
                const isInput = props.index < inputNodeCount;
                // Do not draw a label for the supply pool node (it is a relay).
                const isPool = props.index === poolNodeIndex;
                const labelX = isInput
                  ? props.x - 6
                  : props.x + props.width + 6;
                const textAnchor = isInput ? "end" : "start";
                return (
                  <g>
                    <rect
                      x={props.x}
                      y={props.y}
                      width={props.width}
                      height={props.height}
                      fill={color}
                      fillOpacity={0.85}
                      rx={2}
                    />
                    {!isPool && (
                      <text
                        x={labelX}
                        y={props.y + props.height / 2}
                        dy=".35em"
                        fill="#cbd5e1"
                        fontSize="1rem"
                        textAnchor={textAnchor}
                      >
                        {props.payload?.name}
                      </text>
                    )}
                  </g>
                );
              }}
              link={(linkProps: any) => {
                // Draw links in the source node's translucent color.
                const sourceIdx = linkProps.payload?.source?.index ?? 0;
                const color = nodeColors[sourceIdx] ?? COLOR_POOL;
                return (
                  <path
                    d={`M${linkProps.sourceX},${linkProps.sourceY}C${linkProps.sourceControlX},${linkProps.sourceY} ${linkProps.targetControlX},${linkProps.targetY} ${linkProps.targetX},${linkProps.targetY}`}
                    fill="none"
                    stroke={color}
                    strokeWidth={linkProps.linkWidth}
                    strokeOpacity={0.3}
                  />
                );
              }}
            />
          </ResponsiveContainer>
        ) : (
          <div className="economy-trend-card__no-data">{noDataLabel}</div>
        )}
      </div>
    </div>
  );
}

export function EconomyTrendOverlay({
  apiBaseUrl,
  productId,
  productName,
  onClose,
}: {
  apiBaseUrl: string;
  productId: number;
  productName: string;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const { data, loading, error } = useEpochCachedStats<MarketPriceStats>(
    apiBaseUrl,
    "/api/v1/stats/market_price"
  );

  return (
    <div className="economy-trend-overlay" onMouseDown={onClose}>
      <div
        className="economy-trend-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="economy-trend-title"
        onMouseDown={(e) => e.stopPropagation()}
      >
        <div className="economy-trend-dialog__header">
          <h2 id="economy-trend-title" className="economy-trend-dialog__title">
            {productName}
          </h2>
          <button
            type="button"
            className="economy-trend-dialog__close"
            aria-label={t("common.close")}
            title={t("common.close")}
            onClick={onClose}
          >
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round">
              <path d="M6 6l12 12M18 6L6 18" />
            </svg>
          </button>
        </div>
        {loading ? (
          <div className="economy-trend-dialog__status">{t("common.loading")}</div>
        ) : error || !data ? (
          <div className="economy-trend-dialog__status economy-trend-dialog__status--error">
            {t("common.fetchError")}{error ? `: ${error}` : ""}
          </div>
        ) : (
          <EconomyTrendGrid
            epochs={data.epochs}
            product={data.products.find((p) => p.product_id === productId) ?? null}
          />
        )}
      </div>
    </div>
  );
}

/** Draws the selected good's history in a four-section grid. If the good is absent from the statistics, each card displays "No data". */
function EconomyTrendGrid({
  epochs,
  product: selectedProduct,
}: {
  epochs: number[];
  product: MarketPriceProduct | null;
}) {
  const { t } = useTranslation();
  // Scope selection (entire city = city / District = zone). Only city-wide data is currently implemented.
  const [scopeMode, setScopeMode] = useState<"city" | "zone">("city");

  // For the Sankey diagram: get the selected good's latest Epoch values.
  // The last time series element corresponds to the latest (most recently completed) Epoch.
  const sankeyData = useMemo(() => {
    if (!selectedProduct) return { data: null, isShortage: false };
    const len = selectedProduct.total_production.length;
    if (len === 0) return { data: null, isShortage: false };
    const idx = len - 1;
    const production = selectedProduct.total_production[idx] ?? 0;
    const imports = selectedProduct.total_imports[idx] ?? 0;
    const selfSupply = selectedProduct.total_self_supply[idx] ?? 0;
    const consumption = selectedProduct.total_consumption[idx] ?? 0;
    const exports = selectedProduct.total_exports[idx] ?? 0;
    const totalInput = production + imports + selfSupply;
    const totalOutput = consumption + exports;
    const isShortage = totalInput - totalOutput < 0;
    const built = buildSankeyData(production, imports, selfSupply, consumption, exports, {
      production: t("statistics.economyTrendSankeyProduction"),
      imports: t("statistics.economyTrendSankeyImport"),
      selfSupply: t("statistics.economyTrendSankeySelfSupply"),
      pool: t("statistics.economyTrendSankeyPool"),
      consumption: t("statistics.economyTrendSankeyConsumption"),
      exports: t("statistics.economyTrendSankeyExport"),
      unsold: t("statistics.economyTrendSankeyUnsold"),
      shortage: t("statistics.economyTrendSankeyShortage"),
    });
    return { data: built, isShortage };
  }, [selectedProduct, t]);

  const unit = selectedProduct?.unit ?? "";

  // Currency formatting (for prices).
  const priceFormatter = (v: number) =>
    t("common.currency", { value: v.toFixed(0) });
  // Quantity formatting (for production, consumption, exports/imports). Append a unit if available.
  const amountFormatter = (v: number) => {
    if (Math.abs(v) >= 1000) {
      return `${(v / 1000).toFixed(1)}k${unit ? ` ${unit}` : ""}`;
    }
    return `${v.toFixed(0)}${unit ? ` ${unit}` : ""}`;
  };

  // Latest Epoch values (last elements of the time series), shared by the top-left values and Sankey.
  // Use 0 for empty arrays, and display "-" to indicate absence.
  const lastIdx = selectedProduct
    ? selectedProduct.total_production.length - 1
    : -1;
  const latestPrice = selectedProduct && lastIdx >= 0
    ? selectedProduct.average_prices[lastIdx] ?? 0
    : 0;
  const latestProduction = selectedProduct && lastIdx >= 0
    ? selectedProduct.total_production[lastIdx] ?? 0
    : 0;
  const latestConsumption = selectedProduct && lastIdx >= 0
    ? selectedProduct.total_consumption[lastIdx] ?? 0
    : 0;
  const latestImport = selectedProduct && lastIdx >= 0
    ? selectedProduct.total_imports[lastIdx] ?? 0
    : 0;
  const latestExport = selectedProduct && lastIdx >= 0
    ? selectedProduct.total_exports[lastIdx] ?? 0
    : 0;
  const hasLatest = !!selectedProduct && lastIdx >= 0;

  return (
    <div className="economy-trend-grid">
      {/*
       * Top left: horizontal split.
       * Left = scope dropdowns; right = latest Epoch values (price, production, consumption, imports, exports).
       */}
      <div className="economy-trend-card economy-trend-card--top-left">
        <div className="economy-trend-topleft">
          <div className="economy-trend-topleft__controls">
            <div className="economy-trend-control">
              <label className="economy-trend-control__label">
                {t("statistics.economyTrendScope")}
              </label>
              <select
                className="economy-trend-control__select"
                value={scopeMode}
                onChange={(e) => setScopeMode(e.target.value as "city" | "zone")}
              >
                <option value="city">{t("statistics.economyTrendWholeCity")}</option>
                <option value="zone">{t("statistics.economyTrendByZone")}</option>
              </select>
            </div>
            {scopeMode === "zone" && (
              <div className="economy-trend-control__note">
                {t("statistics.economyTrendZoneNotImplemented")}
              </div>
            )}
          </div>

          <div className="economy-trend-topleft__stats">
            <h3 className="economy-trend-card__title">
              {t("statistics.economyTrendLatest")}
            </h3>
            <dl className="economy-trend-stats">
              <div className="economy-trend-stat">
                <dt>{t("statistics.colPrice")}</dt>
                <dd>{hasLatest ? priceFormatter(latestPrice) : "-"}</dd>
              </div>
              <div className="economy-trend-stat">
                <dt>{t("statistics.colProduction")}</dt>
                <dd>{hasLatest ? amountFormatter(latestProduction) : "-"}</dd>
              </div>
              <div className="economy-trend-stat">
                <dt>{t("statistics.colConsumption")}</dt>
                <dd>{hasLatest ? amountFormatter(latestConsumption) : "-"}</dd>
              </div>
              <div className="economy-trend-stat">
                <dt>{t("statistics.colImport")}</dt>
                <dd>{hasLatest ? amountFormatter(latestImport) : "-"}</dd>
              </div>
              <div className="economy-trend-stat">
                <dt>{t("statistics.colExport")}</dt>
                <dd>{hasLatest ? amountFormatter(latestExport) : "-"}</dd>
              </div>
            </dl>
          </div>
        </div>
      </div>

      {/* Top right: average price history */}
      <TrendChartCard
        title={t("statistics.economyTrendPrice")}
        data={selectedProduct ? zipToChartData(epochs, selectedProduct.average_prices) : []}
        color={COLOR_PRICE}
        formatter={priceFormatter}
        noDataLabel={t("statistics.economyTrendNoData")}
      />

      {/* Bottom left: goods flow Sankey (latest Epoch) */}
      <SankeyFlowCard
        title={t("statistics.economyTrendSankey")}
        sankeyData={sankeyData.data}
        isShortage={sankeyData.isShortage}
        noDataLabel={t("statistics.economyTrendNoData")}
      />

      {/*
       * Bottom right: supply/demand history.
       * Combine production, consumption, exports, and imports as solid lines and order quantity as a dotted line in one chart.
       */}
      <SupplyDemandTrendCard
        title={t("statistics.economyTrendSupplyDemand")}
        epochs={epochs}
        production={selectedProduct?.total_production ?? []}
        consumption={selectedProduct?.total_consumption ?? []}
        exports={selectedProduct?.total_exports ?? []}
        imports={selectedProduct?.total_imports ?? []}
        order={selectedProduct?.total_order ?? []}
        formatter={amountFormatter}
        noDataLabel={t("statistics.economyTrendNoData")}
        labels={{
          production: t("statistics.economyTrendProductionLegend"),
          consumption: t("statistics.economyTrendConsumptionLegend"),
          export: t("statistics.economyTrendExportLegend"),
          import: t("statistics.economyTrendImportLegend"),
          order: t("statistics.economyTrendOrderLegend"),
        }}
      />
    </div>
  );
}
