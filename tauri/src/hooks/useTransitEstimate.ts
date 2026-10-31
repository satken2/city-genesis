import { describeError } from "../utils/errors";
/**
 * Hook fetching line financial estimates: required vehicles, operating cost, fare revenue at assumed occupancy,
 * and balance. Calculations are centralized in the backend (POST /api/v1/transit/lines/estimate and
 * /lines/{id}/estimate); train counts, trips, and capacity use the same formulas as epoch execution. Do not
 * calculate in the UI. Shared by line creation/route editing (tour points placed in Unity) and the line detail
 * settings tab (edited existing-line values).
 *
 * Fetch after inputs settle for ESTIMATE_DEBOUNCE_MS and abort stale requests to avoid recalculating underlying
 * routes on every added tour point or stepper click. Keep previous results during loading to avoid empty
 * displays and layout jumps. While request is null, skip fetching and clear results.
 */

import { useEffect, useState } from "react";
import { buildApiUrl, fetchApi } from "../utils/api";
import type { TransitLineEstimate, TransitRoutePoint } from "../types";

export type TransitEstimateRequest =
  | {
      kind: "line";
      lineAgentId: number;
      farePrice: number;
      targetHeadway: number;
      carsPerTrain: number;
    }
  | {
      kind: "planned";
      /** Line type (transit_master transit_path_id). */
      pathVariant: number;
      /** Tour points (stops and waypoints in traversal order). */
      points: TransitRoutePoint[];
      /** Line whose route is being edited. Exclude its current trips from track capacity totals (omit for creation). */
      editedLineId?: number;
      farePrice: number;
      targetHeadway: number;
      carsPerTrain: number;
    };

const ESTIMATE_DEBOUNCE_MS = 300;

export function useTransitEstimate(
  apiBaseUrl: string,
  request: TransitEstimateRequest | null,
  refreshKey = 0,
): { estimate: TransitLineEstimate | null; loading: boolean; error: string | null } {
  const [estimate, setEstimate] = useState<TransitLineEstimate | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Refetch only when contents change, not when callers recreate the object on every render.
  const requestKey = request ? JSON.stringify(request) : null;

  useEffect(() => {
    if (requestKey === null) {
      setEstimate(null);
      setLoading(false);
      setError(null);
      return;
    }
    const req = JSON.parse(requestKey) as TransitEstimateRequest;
    const controller = new AbortController();
    setLoading(true);
    const timer = window.setTimeout(() => {
      const settings = {
        fare_price: req.farePrice,
        target_headway: req.targetHeadway,
        cars_per_train: req.carsPerTrain,
      };
      const [path, body] =
        req.kind === "line"
          ? [`/api/v1/transit/lines/${req.lineAgentId}/estimate`, settings]
          : ["/api/v1/transit/lines/estimate", {
              ...settings,
              path_variant: req.pathVariant,
              points: req.points,
              ...(req.editedLineId != null ? { line_agent_id: req.editedLineId } : {}),
            }];
      fetchApi<TransitLineEstimate>(buildApiUrl(apiBaseUrl, path), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        signal: controller.signal,
      })
        .then((data) => {
          if (controller.signal.aborted) return;
          setEstimate(data);
          setError(null);
        })
        .catch((err: unknown) => {
          if (controller.signal.aborted) return;
          setEstimate(null);
          setError(describeError(err));
        })
        .finally(() => {
          if (!controller.signal.aborted) setLoading(false);
        });
    }, ESTIMATE_DEBOUNCE_MS);
    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [apiBaseUrl, requestKey, refreshKey]);

  return { estimate, loading, error };
}
