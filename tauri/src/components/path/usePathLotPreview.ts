/**
 * Roadside parcel preview while placing streets.
 *
 * Asks the core which parcels the planned paths would receive if they were completed now (POST /paths/lot-preview,
 * the same layout that runs on completion) and lets Unity draw the outlines for the editing generation they belong to.
 * Requests start once the shape is finalized (editing phase), like the construction validation.
 *
 * The layout takes from a few tenths of a second to a few seconds and the core cannot cancel it, so at most one
 * request is in flight; when it returns, only the latest settings are requested again. A result is shown only while
 * it still matches the current request. Unity drops the outlines on every shape change; when only the generation
 * changes, the last result for the same request is resent without recomputing.
 */

import { useEffect, useRef } from "react";
import { fetchApi } from "../../utils/api";
import { sendEditPathLots } from "../../utils/ipc";
import type { LotAreas } from "./PathLotSection";

const DEBOUNCE_MS = 150;

type PlannedBody = { paths: Record<string, unknown>[] } | null;

export function usePathLotPreview(
  apiBaseUrl: string | undefined,
  constructionBody: PlannedBody,
  shapePhase: string | undefined,
  shapeRevision: string,
  lotAreas: LotAreas | null,
  editPathId: number | null,
): void {
  const request = apiBaseUrl && constructionBody && shapePhase === "editing" && shapeRevision
    && lotAreas && (lotAreas.left > 0 || lotAreas.right > 0)
    ? JSON.stringify({
      paths: constructionBody.paths.map((p) => ({ ...p, left_lot_area: lotAreas.left, right_lot_area: lotAreas.right })),
      exclude_path_id: editPathId,
    })
    : "";

  const latest = useRef({ request: "", revision: "" });
  const result = useRef<{ request: string; lots: number[][][] } | null>(null);
  const shown = useRef(false);
  const inFlight = useRef(false);
  const url = apiBaseUrl ? `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/lot-preview` : "";

  const show = (revision: string, lots: number[][][]) => {
    shown.current = lots.length > 0;
    void sendEditPathLots(revision, lots).catch(console.error);
  };

  const fetchLatest = async (): Promise<void> => {
    if (inFlight.current || !latest.current.request) return;
    const { request: body } = latest.current;
    inFlight.current = true;
    try {
      const response = await fetchApi<{ lots: number[][][] }>(url, {
        method: "POST", headers: { "Content-Type": "application/json" }, body,
      });
      result.current = { request: body, lots: response.lots };
      if (latest.current.request === body) show(latest.current.revision, response.lots);
    } catch (error) {
      console.error("[usePathLotPreview] lot preview failed:", error);
    } finally {
      inFlight.current = false;
    }
    if (latest.current.request && latest.current.request !== body) await fetchLatest();
  };

  useEffect(() => {
    latest.current = { request, revision: shapeRevision };
    if (!request) {
      if (shown.current && shapeRevision) show(shapeRevision, []);
      return;
    }
    if (result.current?.request === request) {
      show(shapeRevision, result.current.lots);
      return;
    }
    if (shown.current) show(shapeRevision, []);
    const timer = setTimeout(() => void fetchLatest(), DEBOUNCE_MS);
    return () => clearTimeout(timer);
    // fetchLatest/show read refs only; the effect reacts to the request and the editing generation.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [request, shapeRevision]);
}
