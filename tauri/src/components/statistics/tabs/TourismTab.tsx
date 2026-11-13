/**
 * Tourism statistics tab component.
 *
 * Shows GET /api/v1/stats/tourism.
 * - Left column: history charts of staying tourists vs visitor capacity, tourism revenue, and the average
 *   satisfaction of staying tourists.
 * - Right column: lists of gateways and tourist attractions. Each facility shows how many tourists it adds to
 *   the city's visitor capacity, the same value as its detail panel.
 */

import { invoke } from "@tauri-apps/api/core";
import { resolveIntlLocale } from "../../../utils/locale";
import { formatExactAmount } from "../../../utils/money";
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
} from "recharts";
import type { YAxisProps } from "recharts";
import { AgentLink } from "../../common/AgentLink";
import { useEpochCachedStats } from "../useEpochCache";
import "./TourismTab.css";
import { useRemPx } from "../../../hooks/useRemPx";

// --- Backend GET /api/v1/stats/tourism response types ---

type HistoryPoint = {
  epoch: number;
  tourist_count: number;
  tourist_capacity: number;
  tourism_revenue: number;
  satisfaction: number | null;
};

type Facility = {
  agent_id: number;
  name: string;
  tourist_capacity: number | null;
};

type TourismStats = {
  epoch: number;
  history: HistoryPoint[];
  gateways: Facility[];
  attractions: Facility[];
};

const COLOR_COUNT = "#ec4899";
const COLOR_CAPACITY = "#06b6d4";
const COLOR_REVENUE = "#a78bfa";
const COLOR_SATISFACTION = "#60a5fa";

const CHART_MARGIN = { top: 12, right: 16, left: -10, bottom: 0 };
const Y_AXIS_WIDTH_REM = 4.44; // Y-axis width (rem). Convert to px with useRemPx when rendering.
const AXIS_TICK = { fontSize: "0.93rem", fill: "#8ba3bb" };
const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "1rem",
};

function fmt(value: number, digits = 0): string {
  return value.toLocaleString(undefined, { maximumFractionDigits: digits });
}

function pct(ratio: number): string {
  return `${Math.round(ratio * 100)}%`;
}

function openAgent(entityId: string) {
  void invoke("open_registered_window", { windowType: "agent_detail", entityId }).catch(console.error);
}

export function TourismTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);
  const remPx = useRemPx();
  const { data, loading, error } = useEpochCachedStats<TourismStats>(
    apiBaseUrl,
    "/api/v1/stats/tourism"
  );

  if (loading) {
    return <div className="tourism-tab__loading">{t("common.loading")}</div>;
  }
  if (error || !data) {
    return (
      <div className="tourism-tab__error">
        {t("common.fetchError")}{error ? `: ${error}` : ""}
      </div>
    );
  }

  const history = data.history;
  const hasHistory = history.length > 0;

  const renderHistoryChart = (
    children: React.ReactNode,
    valueFormatter?: (value: number) => string,
    yAxis?: YAxisProps,
  ) => (
    <div className="tourism-card__chart-container">
      {hasHistory ? (
        <ResponsiveContainer width="100%" height="100%">
          <LineChart data={history} margin={CHART_MARGIN}>
            <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
            <XAxis dataKey="epoch" tick={AXIS_TICK} tickLine={false} axisLine={{ stroke: "#253246" }} />
            <YAxis
              width={remPx(Y_AXIS_WIDTH_REM)}
              tick={AXIS_TICK}
              tickLine={false}
              axisLine={{ stroke: "#253246" }}
              allowDecimals={false}
              tickFormatter={valueFormatter}
              {...yAxis}
            />
            <Tooltip
              contentStyle={TOOLTIP_STYLE}
              labelFormatter={(label) => `Epoch ${label}`}
              formatter={valueFormatter ? (value) => valueFormatter(Number(value)) : undefined}
            />
            <Legend wrapperStyle={{ fontSize: "0.93rem" }} iconSize={8} />
            {children}
          </LineChart>
        </ResponsiveContainer>
      ) : (
        <div className="tourism-card__no-data">{t("statistics.tourism.noData")}</div>
      )}
    </div>
  );

  const renderFacilityList = (title: string, facilities: Facility[]) => (
    <div className="tourism-card">
      <h3 className="tourism-card__title">{title}</h3>
      <ul className="tourism-facility-list">
        {facilities.map((f) => (
          <li key={f.agent_id} className="tourism-facility-list__item">
            <AgentLink agentId={f.agent_id} agentName={f.name || `#${f.agent_id}`} onNavigate={openAgent} />
            <span className="tourism-facility-list__value">
              {f.tourist_capacity == null ? "-" : `${fmt(f.tourist_capacity)} ${t("statistics.people")}`}
            </span>
          </li>
        ))}
      </ul>
    </div>
  );

  return (
    <div className="tourism-tab">
      <h2 className="tourism-tab__title">{t("statistics.tourism.title")}</h2>

      <div className="tourism-tab__grid">
        <div className="tourism-tab__col">
          <div className="tourism-card">
            <h3 className="tourism-card__title">{t("statistics.tourism.chartCountHistory")}</h3>
            {renderHistoryChart(
              <>
                <Line type="monotone" dataKey="tourist_count" name={t("statistics.tourism.seriesCount")} stroke={COLOR_COUNT} strokeWidth={2} dot={{ r: 2 }} />
                <Line type="monotone" dataKey="tourist_capacity" name={t("statistics.tourism.seriesCapacity")} stroke={COLOR_CAPACITY} strokeWidth={2} strokeDasharray="5 3" dot={false} />
              </>
            )}
          </div>

          <div className="tourism-card">
            <h3 className="tourism-card__title">{t("statistics.tourism.chartRevenueHistory")}</h3>
            {renderHistoryChart(
              <Line type="monotone" dataKey="tourism_revenue" name={t("statistics.tourism.seriesRevenue")} stroke={COLOR_REVENUE} strokeWidth={2} dot={{ r: 2 }} />,
              (value) => formatExactAmount(value, locale),
            )}
          </div>

          <div className="tourism-card">
            <h3 className="tourism-card__title">{t("statistics.tourism.chartSatisfactionHistory")}</h3>
            {renderHistoryChart(
              <Line type="monotone" dataKey="satisfaction" name={t("statistics.tourism.seriesSatisfaction")} stroke={COLOR_SATISFACTION} strokeWidth={2} dot={{ r: 2 }} />,
              pct,
              { domain: [0, 1], allowDecimals: true },
            )}
          </div>
        </div>

        <div className="tourism-tab__col">
          {renderFacilityList(t("statistics.tourism.gatewaysTitle"), data.gateways)}
          {renderFacilityList(t("statistics.tourism.attractionsTitle"), data.attractions)}
        </div>
      </div>
    </div>
  );
}
