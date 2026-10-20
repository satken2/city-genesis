/**
 * Public service adequacy meter (persistent in the cockpit).
 * One bar each for electricity and water, capped at the smaller of service and high-voltage goods capacity.
 * Convert high-voltage goods to service units, and fill service demand upward in green.
 * Do not use actual production or demand for high-voltage goods. Demand above capacity appears as a full bar,
 * with the ratio in the tooltip.
 * Use equal heights since absolute quantities of services with different units cannot be compared.
 * The whole widget is a button opening public facilities.
 * Poll at a longer interval, pause during simulation, and refetch on completion.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { buildApiUrl, fetchApi } from "../../utils/api";
import { COCKPIT_PUBLIC_SERVICE_PRODUCTS } from "../../local-constants";
import {
  PRODUCT_ELECTRICITY,
  PRODUCT_WATER_SUPPLY,
} from "../../constants";
import type { PublicServiceSupply, PublicServiceSupplyData } from "../../types";
import { getPublicServiceCapacity } from "../budget/publicServiceInfrastructure";
import "./PublicServiceGauges.css";

/** Backend polling interval in milliseconds. A longer interval suffices since supply and demand change only per epoch. */
const POLL_INTERVAL_MS = 30_000;

/** Icon geometry by product ID. */
const SERVICE_ICON_SHAPE: Record<number, "bolt" | "drop"> = {
  [PRODUCT_ELECTRICITY]: "bolt",
  [PRODUCT_WATER_SUPPLY]: "drop",
};

/** Lightning and droplet paths, within a 16×16 view box */
const ICON_PATHS: Record<"bolt" | "drop", string> = {
  bolt: "M9.5 1 3.5 9.5h3.2L6 15l6.2-8.8H8.9L9.5 1Z",
  drop: "M8 1.5c3 3.6 4.6 6.1 4.6 8.1a4.6 4.6 0 1 1-9.2 0c0-2 1.6-4.5 4.6-8.1Z",
};

function ServiceIcon({ productId }: { productId: number }) {
  const shape = SERVICE_ICON_SHAPE[productId] ?? "bolt";
  const path = ICON_PATHS[shape];

  return (
    <svg className="psg-icon" viewBox="0 0 16 16" aria-hidden="true" focusable="false">
      <path d={path} />
    </svg>
  );
}

/**
 * Calculate the demand/capacity ratio.
 * Demand with no capacity means none is covered, so treat the bar as full (1.0).
 */
function demandRatio(supply: PublicServiceSupply | undefined, capacity: number): number {
  if (!supply) return 0;
  if (capacity > 0) return supply.demand / capacity;
  return supply.demand > 0 ? 1 : 0;
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
  // Supply and demand keyed by product ID. Empty before retrieval, leaving unfilled bar outlines.
  const [supplies, setSupplies] = useState<Record<number, PublicServiceSupply>>({});

  useEffect(() => {
    if (!apiBaseUrl || epochExecuting) return;
    const controller = new AbortController();
    let cancelled = false;

    async function refresh() {
      try {
        const res = await fetchApi<PublicServiceSupplyData>(
          buildApiUrl(apiBaseUrl, "/api/v1/city/public-service-supply"),
          { signal: controller.signal },
        );
        if (cancelled) return;
        const map: Record<number, PublicServiceSupply> = {};
        for (const row of res.services) {
          map[row.product_id] = row;
        }
        setSupplies(map);
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
      {COCKPIT_PUBLIC_SERVICE_PRODUCTS.map((productId) => {
        const supply = supplies[productId];
        const ratio = demandRatio(supply, getPublicServiceCapacity(productId, supplies));
        const name = t(`publicServiceGauge.services.${productId}`);
        return (
          <span
            key={productId}
            className="psg-col"
            title={
              supply
                ? `${name} ${Math.round(ratio * 100)}%`
                : name
            }
          >
            <span className="psg-track">
              <span
                className="psg-fill"
                style={{
                  height: `${Math.min(ratio, 1) * 100}%`,
                }}
              />
            </span>
            <ServiceIcon productId={productId} />
          </span>
        );
      })}
    </button>
  );
}
