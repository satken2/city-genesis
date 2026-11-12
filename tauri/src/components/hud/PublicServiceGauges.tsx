/**
 * Public service adequacy meter (persistent in the cockpit).
 * One bar each for electricity and water, capped at the smaller of service and high-voltage goods capacity.
 * Convert high-voltage goods to service units, and fill service demand upward in green.
 * Do not use actual production or demand for high-voltage goods. Demand above capacity appears as a full bar,
 * with the ratio in the tooltip.
 * The trade bar is the busier direction's handling over the total capacity of active trade hubs, matching each
 * hub's utilization since imports and exports can each use the full capacity. It uses actual handling because
 * export demand is the whole domestic supply and always fills capacity. The support hub (VARIANT_IMPORT) that every
 * city starts with inside City Hall is excluded, so the cross remains until the city has a hub of its own.
 * Use equal heights since absolute quantities with different units cannot be compared.
 * The whole widget is a button opening public facilities, whose logistics tab lists the trade hubs.
 * Poll at a longer interval, pause during simulation, and refetch on completion.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { buildApiUrl, fetchApi } from "../../utils/api";
import { COCKPIT_PUBLIC_SERVICE_PRODUCTS } from "../../local-constants";
import {
  PRODUCT_ELECTRICITY,
  PRODUCT_WATER_SUPPLY,
  STATUS_ACTIVE,
  VARIANT_IMPORT,
} from "../../constants";
import type { PublicServiceSupply, PublicServiceSupplyData } from "../../types";
import type { LogisticsData, LogisticsHub } from "../budget/LogisticsPane";
import { getPublicServiceCapacity } from "../budget/publicServiceInfrastructure";
import "./PublicServiceGauges.css";

/** Backend polling interval in milliseconds. A longer interval suffices since supply and demand change only per epoch. */
const POLL_INTERVAL_MS = 30_000;

/** Lightning, droplet, and cargo ship paths, within a 16×16 view box */
const ICON_PATHS = {
  bolt: "M9.5 1 3.5 9.5h3.2L6 15l6.2-8.8H8.9L9.5 1Z",
  drop: "M8 1.5c3 3.6 4.6 6.1 4.6 8.1a4.6 4.6 0 1 1-9.2 0c0-2 1.6-4.5 4.6-8.1Z",
  ship: "M6.2 2.5h3.6v3H6.2v-3ZM4 6h3.5v3H4V6Zm4.5 0H12v3H8.5V6ZM1.5 9.5h13l-2.2 4H3.7l-2.2-4Z",
} as const;

type GaugeIcon = keyof typeof ICON_PATHS;

/** Icon geometry by product ID. */
const SERVICE_ICON_SHAPE: Record<number, GaugeIcon> = {
  [PRODUCT_ELECTRICITY]: "bolt",
  [PRODUCT_WATER_SUPPLY]: "drop",
};

type TradeTotals = { capacity: number; handling: number };

function sumTrade(hubs: LogisticsHub[]): TradeTotals {
  const counted = hubs.filter((hub) => hub.status === STATUS_ACTIVE && hub.variant !== VARIANT_IMPORT);
  const sum = (key: "capacity" | "import_handled" | "export_handled") =>
    counted.reduce((total, hub) => total + hub[key], 0);
  return { capacity: sum("capacity"), handling: Math.max(sum("import_handled"), sum("export_handled")) };
}

/**
 * One bar filled by load / capacity.
 * Zero capacity means the city has no facility able to supply it, so draw a cross instead of the bar to show that
 * an essential facility is still missing. Load is null before retrieval, leaving an unfilled bar outline.
 */
function GaugeColumn({
  label,
  load,
  capacity,
  icon,
}: {
  label: string;
  load: number | null;
  capacity: number;
  icon: GaugeIcon;
}) {
  const missing = load !== null && capacity <= 0;
  const ratio = load !== null && capacity > 0 ? load / capacity : 0;
  return (
    <span className="psg-col" title={load !== null && !missing ? `${label} ${Math.round(ratio * 100)}%` : label}>
      <span className="psg-track">
        {missing ? (
          <svg className="psg-missing" viewBox="0 0 16 16" aria-hidden="true" focusable="false">
            <path d="M3.5 3.5 12.5 12.5M12.5 3.5 3.5 12.5" />
          </svg>
        ) : (
          <span className="psg-fill" style={{ height: `${Math.min(ratio, 1) * 100}%` }} />
        )}
      </span>
      <svg className="psg-icon" viewBox="0 0 16 16" aria-hidden="true" focusable="false">
        <path d={ICON_PATHS[icon]} />
      </svg>
    </span>
  );
}

export function PublicServiceGauges({
  apiBaseUrl,
  epochExecuting,
  onOpen,
}: {
  apiBaseUrl: string;
  epochExecuting: boolean;
  onOpen: () => void;
}) {
  const { t } = useTranslation();
  // Supply and demand keyed by product ID, and trade hub totals. Empty/null before retrieval.
  const [supplies, setSupplies] = useState<Record<number, PublicServiceSupply>>({});
  const [trade, setTrade] = useState<TradeTotals | null>(null);

  useEffect(() => {
    if (!apiBaseUrl || epochExecuting) return;
    const controller = new AbortController();
    let cancelled = false;

    async function refresh() {
      try {
        const [supplyRes, logisticsRes] = await Promise.all([
          fetchApi<PublicServiceSupplyData>(
            buildApiUrl(apiBaseUrl, "/api/v1/city/public-service-supply"),
            { signal: controller.signal },
          ),
          fetchApi<LogisticsData>(buildApiUrl(apiBaseUrl, "/api/v1/stats/logistics"), { signal: controller.signal }),
        ]);
        if (cancelled) return;
        const map: Record<number, PublicServiceSupply> = {};
        for (const row of supplyRes.services) {
          map[row.product_id] = row;
        }
        setSupplies(map);
        setTrade(sumTrade(logisticsRes.hubs));
      } catch {
        // Keep the previous value on retrieval failure (including aborts) to avoid flicker, as in ScoreMiniMeter.
      }
    }

    void refresh();
    const timer = setInterval(() => void refresh(), POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      controller.abort();
      clearInterval(timer);
    };
  }, [apiBaseUrl, epochExecuting]);

  return (
    <button
      type="button"
      className="psg"
      data-guide="hud.publicServices"
      title={t("publicServiceGauge.tooltip")}
      onClick={onOpen}
    >
      {COCKPIT_PUBLIC_SERVICE_PRODUCTS.map((productId) => (
        <GaugeColumn
          key={productId}
          label={t(`publicServiceGauge.services.${productId}`)}
          load={supplies[productId]?.demand ?? null}
          capacity={getPublicServiceCapacity(productId, supplies)}
          icon={SERVICE_ICON_SHAPE[productId] ?? "bolt"}
        />
      ))}
      <GaugeColumn
        label={t("publicServiceGauge.trade")}
        load={trade?.handling ?? null}
        capacity={trade?.capacity ?? 0}
        icon="ship"
      />
    </button>
  );
}
