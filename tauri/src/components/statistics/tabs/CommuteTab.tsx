/**
 * Commute statistics tab component.
 *
 * Fetches and displays work/school commute times, inter-District movement, and car ownership
 * by District from GET /api/v1/stats/commute (the population tab uses the day/night population
 * by District returned by the same API).
 *
 * Layout (2:3 left/right split):
 * - Top left (one-third height): work/school commute time distribution (horizontal stacked bar chart).
 *   Select the scope (entire city / individual District) from the panel heading dropdown.
 *   District means the Citizen's District of residence, and the choice affects only this panel.
 * - Bottom left (two-thirds height): choropleth map of car ownership by District.
 * - Right (full height): inter-District movement (Recharts Sankey diagram). Switch between
 *   "Total", "Work/school", and "Shopping" subtabs, each with its own colors. Values are daily
 *   round trips (one Citizen makes one round trip per day for work/school). Shopping records
 *   cover the latest Epoch and are not saved, so "Total" and "Shopping" have no data before
 *   simulation or immediately after loading.
 */

import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  BarChart,
  Bar,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
  Sankey,
} from "recharts";
import {
  CityMapCanvas,
  getZoneFillRampStops,
  type CityMapLayerVisibility,
  type CityMapZoneFill,
} from "../../map/CityMapCanvas";
import { useEpochCachedStats } from "../useEpochCache";
import "./CommuteTab.css";
import { useRemPx } from "../../../hooks/useRemPx";

// --- Type definitions ---

type Summary = {
  worker_count: number;
  student_count: number;
  total_commuters: number;
};

type TimeDistPoint = {
  bucket: string;
  workers: number;
  students: number;
};

/** Aggregation scope for work/school commute time distribution. Both zone_id and name are null for the entire city. */
type CommuteTimeScope = {
  zone_id: number | null;
  name: string | null;
  distribution: TimeDistPoint[];
};

/** Car ownership by District. Rates are null (missing) where the population contains no Resident Households. */
type CarOwnershipZone = {
  zone_id: number;
  name: string;
  households: number;
  owners: number;
  wanters: number;
  rate: number | null;
  want_rate: number | null;
};

type CarOwnership = {
  households: number;
  owners: number;
  wanters: number;
  rate: number | null;
  want_rate: number | null;
  zones: CarOwnershipZone[];
};

type ODPoint = {
  from_zone_id: number;
  from_name: string;
  to_zone_id: number;
  to_name: string;
  count: number;
};

/** Inter-District movement types. total and shopping are null while shopping records are unavailable. */
type FlowKind = "total" | "commute" | "shopping";

type CommuteStats = {
  epoch: number;
  summary: Summary;
  commute_time_scopes: CommuteTimeScope[];
  od_flows: Record<FlowKind, ODPoint[] | null>;
  car_ownership: CarOwnership;
};

// --- Colors ---
const COLOR_WORKER = "#228be6";      // Work commuters: dark blue.
const COLOR_STUDENT = "#a5d8ff";     // School commuters: light blue (the lighter color requested by the user).
const FLOW_KINDS: FlowKind[] = ["total", "commute", "shopping"];
const FLOW_TAB_KEYS: Record<FlowKind, string> = {
  total: "statistics.flowTabTotal",
  commute: "statistics.flowTabCommute",
  shopping: "statistics.flowTabShopping",
};
/** Sankey colors. Origin nodes (left) are dark, destination nodes (right) are light, and links are translucent. Subtab underlines use the origin color. */
const FLOW_COLORS: Record<FlowKind, { source: string; target: string; link: string }> = {
  total: { source: "#7048e8", target: "#b197fc", link: "rgba(151, 117, 250, 0.4)" },
  commute: { source: "#1c7ed6", target: "#74c0fc", link: "rgba(74, 158, 255, 0.4)" },
  shopping: { source: "#e8590c", target: "#ffa94d", link: "rgba(255, 146, 43, 0.4)" },
};

/**
 * Car ownership map layers. Car ownership depends on Public transport availability, so overlay
 * ownership colors with Public transport Lines for comparison (transitLines is ON, unlike
 * the wellbeing tab map). Keep this as a module constant to avoid triggering the layer-change
 * effect on every render.
 */
const MAP_LAYERS: Partial<CityMapLayerVisibility> = {
  coastline: true,
  trunkRoads: true,
  allRoads: true,
  railways: true,
  transitLines: true,
  parcels: false,
  buildings: false,
  stations: true,
  zoneBorders: true,
  elevation: false,
  contours: false,
  grid: false,
};

/** Formats a ratio (0–1) as a percentage string. Missing values are "—". */
function formatRate(rate: number | null | undefined): string {
  return rate == null ? "—" : `${(rate * 100).toFixed(1)}%`;
}

function scopeKey(scope: CommuteTimeScope): string {
  return scope.zone_id == null ? "city" : String(scope.zone_id);
}

/** Sankey nodes. Separate residence (left) and destination (right) nodes even for the same District, since self-loops for movement within a District cannot be drawn. */
type SankeyNode = { name: string; isSource: boolean };

/**
 * Choropleth map of car ownership by District.
 *
 * Colors stretch the District min–max range across the full scale so even small differences
 * remain visible. This prevents absolute comparisons, so show actual ownership rates in the
 * legend (the same approach as the wellbeing tab map).
 */
function CarOwnershipMap({
  apiBaseUrl,
  ownership,
}: {
  apiBaseUrl: string;
  ownership: CarOwnership;
}) {
  const { t } = useTranslation();

  // Leave Districts without Resident Households in the population uncolored as missing data.
  const zoneValues = useMemo(
    () =>
      ownership.zones
        .filter((z) => typeof z.rate === "number")
        .map((z) => ({ zoneId: z.zone_id, value: z.rate as number })),
    [ownership.zones]
  );

  const zoneRange = useMemo(() => {
    if (!zoneValues.length) return null;
    let min = Infinity;
    let max = -Infinity;
    for (const { value } of zoneValues) {
      if (value < min) min = value;
      if (value > max) max = value;
    }
    return { min, max };
  }, [zoneValues]);

  const zoneFill = useMemo<CityMapZoneFill | undefined>(() => {
    if (!zoneRange) return undefined;
    const span = zoneRange.max - zoneRange.min;
    const values = new Map<number, number>();
    for (const { zoneId, value } of zoneValues) {
      values.set(zoneId, span > 0 ? (value - zoneRange.min) / span : 0.5);
    }
    return { values };
  }, [zoneValues, zoneRange]);

  const rampGradient = useMemo(
    () => `linear-gradient(90deg, ${getZoneFillRampStops().join(", ")})`,
    []
  );

  return (
    <div className="commute-car">
      <div className="commute-panel-title">{t("statistics.carOwnershipTitle")}</div>
      <div className="commute-car__summary">
        <span className="commute-car__metric">
          {t("statistics.carOwnershipRate")}
          <b>{formatRate(ownership.rate)}</b>
        </span>
        {/* Show the desired rate alongside ownership because their difference represents those who want a car but cannot buy one. */}
        <span className="commute-car__metric commute-car__metric--sub">
          {t("statistics.carOwnershipWantRate")}
          <b>{formatRate(ownership.want_rate)}</b>
        </span>
      </div>
      <p className="commute-car__note">{t("statistics.carOwnershipNote")}</p>
      <div className="commute-car__map">
        <CityMapCanvas apiBaseUrl={apiBaseUrl} layers={MAP_LAYERS} zoneFill={zoneFill} />
        {zoneRange ? (
          <div className="commute-car__legend">
            <span>{formatRate(zoneRange.min)}</span>
            <span className="commute-car__legend-bar" style={{ background: rampGradient }} />
            <span>{formatRate(zoneRange.max)}</span>
          </div>
        ) : (
          <div className="commute-car__empty">{t("statistics.carOwnershipEmpty")}</div>
        )}
      </div>
    </div>
  );
}

export function CommuteTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const remPx = useRemPx();
  const { data, loading, error } = useEpochCachedStats<CommuteStats>(
    apiBaseUrl,
    "/api/v1/stats/commute"
  );

  const [selectedKey, setSelectedKey] = useState<string>("city");
  const [flowKind, setFlowKind] = useState<FlowKind>("total");

  const timeScope = useMemo<CommuteTimeScope | null>(() => {
    const scopes = data?.commute_time_scopes ?? [];
    return scopes.find((s) => scopeKey(s) === selectedKey) ?? scopes[0] ?? null;
  }, [data, selectedKey]);

  // --- Build Sankey data ---
  const flows = data?.od_flows[flowKind] ?? null;
  const sankeyData = useMemo(() => {
    if (!flows || flows.length === 0) {
      return { nodes: [], links: [] };
    }

    const nodes: SankeyNode[] = [];
    const nodeMap = new Map<string, number>();
    const links: { source: number; target: number; value: number }[] = [];

    const nodeIndex = (zoneId: number, name: string, isSource: boolean): number => {
      const key = `${isSource ? "src" : "dst"}:${zoneId}`;
      let index = nodeMap.get(key);
      if (index === undefined) {
        index = nodes.length;
        nodeMap.set(key, index);
        nodes.push({ name, isSource });
      }
      return index;
    };

    flows.forEach((item) => {
      links.push({
        source: nodeIndex(item.from_zone_id, item.from_name, true),
        target: nodeIndex(item.to_zone_id, item.to_name, false),
        value: item.count,
      });
    });

    return { nodes, links };
  }, [flows]);

  if (loading) {
    return <div className="commute-tab__loading">{t("common.loading")}</div>;
  }
  if (error || !data) {
    return (
      <div className="commute-tab__error">
        {t("common.fetchError")}
        {error ? `: ${error}` : ""}
      </div>
    );
  }

  const peopleValue = (label: string, n: number) =>
    t("statistics.commutePeopleValue", { label, n });
  const tripsValue = (label: string, n: number) =>
    t("statistics.flowTripsValue", { label, n: n.toLocaleString(undefined, { maximumFractionDigits: 1 }) });
  const flowColors = FLOW_COLORS[flowKind];
  const flowEmptyMessage = flows == null
    ? t("statistics.flowNotSimulated")
    : t(flowKind === "commute" ? "statistics.commuteFlowEmpty" : "statistics.flowEmpty");

  // --- Custom Sankey node rendering ---
  const renderSankeyNode = ({ x, y, width, height, index, payload }: any) => {
    // Switch text position depending on whether this is the residence (left) or destination (right).
    const isSource = (payload as SankeyNode).isSource;

    return (
      <g key={`node-${index}`}>
        <rect
          x={x}
          y={y}
          width={width}
          height={height}
          fill={isSource ? flowColors.source : flowColors.target}
          fillOpacity={0.8}
          rx={2}
        />
        <text
          x={isSource ? x - 6 : x + width + 6}
          y={y + height / 2}
          textAnchor={isSource ? "end" : "start"}
          dominantBaseline="middle"
          fontSize="0.93rem"
          fill="#c1c9d2"
        >
          {payload.name}
        </text>
      </g>
    );
  };

  // --- Custom Sankey tooltip ---
  const CustomSankeyTooltip = ({ active, payload }: any) => {
    if (active && payload && payload.length) {
      // Recharts wraps the target in {name, value, payload}. The inner payload is the link or node itself.
      const { value, payload: item } = payload[0].payload;
      if (item.source && item.target) {
        // Link hover.
        return (
          <div className="commute-tooltip">
            <p className="commute-tooltip__route">{`${item.source.name} ➔ ${item.target.name}`}</p>
            <p className="commute-tooltip__val">{tripsValue(t("statistics.commuteFlowMovers"), value)}</p>
          </div>
        );
      } else if (item.name) {
        // Node hover.
        const role = t((item as SankeyNode).isSource ? "statistics.commuteFlowOrigin" : "statistics.commuteFlowDestination");
        return (
          <div className="commute-tooltip">
            <p className="commute-tooltip__route">{`${item.name} (${role})`}</p>
            <p className="commute-tooltip__val">{tripsValue(t("statistics.commuteFlowTotalMovers"), value)}</p>
          </div>
        );
      }
    }
    return null;
  };

  // --- Custom time distribution tooltip ---
  const CustomBarTooltip = ({ active, payload, label }: any) => {
    if (active && payload && payload.length) {
      const w = payload[0]?.value || 0;
      const s = payload[1]?.value || 0;
      return (
        <div className="commute-tooltip">
          <p className="commute-tooltip__time">{t("statistics.commuteMinutesRange", { range: label })}</p>
          <p className="commute-tooltip__item" style={{ color: COLOR_WORKER }}>
            {peopleValue(t("statistics.commuteWorkers"), w)}
          </p>
          <p className="commute-tooltip__item" style={{ color: COLOR_STUDENT }}>
            {peopleValue(t("statistics.commuteStudents"), s)}
          </p>
          <div className="commute-tooltip__divider" />
          <p className="commute-tooltip__total">
            {peopleValue(t("statistics.commuteTotal"), w + s)}
          </p>
        </div>
      );
    }
    return null;
  };

  return (
    <div className="commute-tab">
      {/* Left pane: 2/5 width */}
      <div className="commute-tab__left">
        {/* Top left: work/school commute time distribution (one-third height) */}
        <div className="commute-distribution">
          <div className="commute-distribution__header">
            <div className="commute-panel-title">{t("statistics.commuteTimeTitle")}</div>
            <select
              className="commute-distribution__scope"
              aria-label={t("statistics.scopeLabel")}
              value={selectedKey}
              onChange={(e) => setSelectedKey(e.target.value)}
            >
              {data.commute_time_scopes.map((s) => (
                <option key={scopeKey(s)} value={scopeKey(s)}>
                  {s.zone_id == null ? t("statistics.cityWide") : s.name}
                </option>
              ))}
            </select>
          </div>
          <div className="commute-distribution__chart">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart
                data={timeScope?.distribution ?? []}
                layout="vertical"
                margin={{ top: 5, right: 15, left: 5, bottom: 5 }}
              >
                <CartesianGrid strokeDasharray="3 3" stroke="#253545" horizontal={true} vertical={false} />
                <XAxis type="number" stroke="#8ba3bb" fontSize="0.83rem" />
                <YAxis dataKey="bucket" type="category" stroke="#8ba3bb" fontSize="0.83rem" width={remPx(3.7)} />
                <Tooltip content={<CustomBarTooltip />} cursor={{ fill: "rgba(255,255,255,0.05)" }} />
                <Bar dataKey="workers" stackId="a" fill={COLOR_WORKER} radius={[0, 0, 0, 0]} name={t("statistics.commuteWorkers")} />
                <Bar dataKey="students" stackId="a" fill={COLOR_STUDENT} radius={[0, 2, 2, 0]} name={t("statistics.commuteStudents")} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </div>

        {/* Bottom left: car ownership by District (two-thirds height) */}
        <CarOwnershipMap apiBaseUrl={apiBaseUrl} ownership={data.car_ownership} />
      </div>

      {/* Right pane: 3/5 width */}
      <div className="commute-tab__right">
        {/* Entire right side: inter-District movement */}
        <div className="commute-flow">
          <div className="commute-panel-title">{t("statistics.commuteFlowTitle")}</div>
          <nav className="commute-flow__tabs">
            {FLOW_KINDS.map((kind) => (
              <button
                key={kind}
                type="button"
                className={`commute-flow__tab${kind === flowKind ? " commute-flow__tab--active" : ""}`}
                style={{ borderBottomColor: kind === flowKind ? FLOW_COLORS[kind].source : "transparent" }}
                onClick={() => setFlowKind(kind)}
              >
                {t(FLOW_TAB_KEYS[kind])}
              </button>
            ))}
          </nav>
          <div className="commute-flow__chart">
            {sankeyData.nodes.length === 0 ? (
              <div className="commute-flow__empty">{flowEmptyMessage}</div>
            ) : (
              <ResponsiveContainer width="100%" height="100%">
                <Sankey
                  key={flowKind}
                  data={sankeyData}
                  node={renderSankeyNode}
                  link={{ stroke: flowColors.link, fill: "none" }}
                  nodePadding={12}
                  margin={{ top: 10, right: 80, bottom: 10, left: 80 }}
                >
                  <Tooltip content={<CustomSankeyTooltip />} />
                </Sankey>
              </ResponsiveContainer>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
