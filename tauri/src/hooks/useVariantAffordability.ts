import { requireApiSuccess } from "../utils/api";
/**
 * Hook fetching per-variant affordability estimates for construction panels. Call GET
 * /api/v1/paths/variant-affordability or GET /api/v1/buildings/variant-affordability for all variants' reference
 * costs (roads: flat terrain/reference length; buildings: minimum size) and affordability with current funds.
 * Panels prevent selecting clearly unaffordable standards/facilities as advance guidance; the backend checks
 * actual total shape cost when ordering.
 *
 * Fetch on mount, explicit refresh() after successful confirmation, and epoch execution completion
 * (executing=false in "epoch:executing" from epoch_confirm). Scenario authoring mode returns unlimited=true,
 * enabling all variants.
 */

import { useCallback, useEffect, useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";

export type VariantAffordabilityEntry = {
  /** Reference cost (flat road at reference length / minimum-size building). */
  referenceCost: number;
  /** Whether available funds cover the reference cost. */
  affordable: boolean;
  /** Shortfall (nonnegative). */
  fundsShortfall: number;
};

export type VariantAffordability = {
  /** City funds available for orders. null when unlimited. */
  availableFunds: number | null;
  /** True when funds are not checked, such as scenario authoring mode. */
  unlimited: boolean;
  /** Variant ID → affordability. */
  variants: Record<number, VariantAffordabilityEntry>;
};

type ApiResponse = {
  available_funds: number | null;
  unlimited: boolean;
  reference_length: number;
  variants: Record<string, { reference_cost: number; affordable: boolean; funds_shortfall: number }>;
};

export function useVariantAffordability(
  kind: "paths" | "buildings",
  apiBaseUrl: string | undefined,
): { affordability: VariantAffordability | null; refresh: () => void } {
  const [affordability, setAffordability] = useState<VariantAffordability | null>(null);
  const [tick, setTick] = useState(0);

  const refresh = useCallback(() => setTick((n) => n + 1), []);

  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();
    const base = apiBaseUrl.replace(/\/+$/, "");
    fetch(`${base}/api/v1/${kind}/variant-affordability`, { signal: controller.signal })
      .then(async (res) => {
        await requireApiSuccess(res);
        return res.json() as Promise<ApiResponse>;
      })
      .then((data) => {
        if (controller.signal.aborted) return;
        const variants: Record<number, VariantAffordabilityEntry> = {};
        for (const [id, v] of Object.entries(data.variants ?? {})) {
          variants[parseInt(id)] = {
            referenceCost: v.reference_cost,
            affordable: v.affordable,
            fundsShortfall: v.funds_shortfall,
          };
        }
        setAffordability({
          availableFunds: data.available_funds ?? null,
          unlimited: data.unlimited === true,
          variants,
        });
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          // Omit estimates on fetch failure (safe because the backend checks funds when ordering).
          console.error("[useVariantAffordability] fetch failed:", err);
          setAffordability(null);
        }
      });
    return () => controller.abort();
  }, [apiBaseUrl, kind, tick]);

  // Refetch after epoch execution changes the city's cash.
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    getCurrentWindow()
      .listen<{ executing: boolean }>("epoch:executing", (event) => {
        if (!event.payload.executing) refresh();
      })
      .then((fn) => { unlisten = fn; })
      .catch(() => {});
    return () => { unlisten?.(); };
  }, [refresh]);

  return { affordability, refresh };
}
