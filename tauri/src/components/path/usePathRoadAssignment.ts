/**
 * Road membership state for the road construction panel.
 *
 * Manage whether new streets/highways join an existing named road, a new road, or none (unnamed paths).
 * Include existing road_id on each confirmed path. For new roads, create the road using the returned path IDs
 * after placement, since roads cannot exist without sections.
 *
 * Extension proposals: query POST /api/v1/roads/extension-candidates with the shape's start/end points.
 * A road is proposed if exactly one section of it ends there (a road endpoint). Automatically select the first candidate
 * unless the user has changed the selection. Query only when endpoints change, not intermediate geometry.
 */

import { useCallback, useEffect, useRef, useState } from "react";
import { createRoad, fetchRoadExtensionCandidates, notifyRoadsChanged } from "../road/roadApi";
import type { RoadExtensionCandidate } from "../road/roadTypes";
import { useRoadList, type RoadChoice } from "../road/RoadMembershipRow";

/** Do not refetch if endpoints remain in the same grid cell of this width (m), matching backend endpoint tolerance (1 m). */
const ENDPOINT_KEY_GRID_M = 1;
/** Delay after geometry updates settle before querying (ms). */
const CANDIDATE_DEBOUNCE_MS = 250;

export function usePathRoadAssignment(apiBaseUrl: string | undefined, enabled: boolean, rawPoints: number[][]) {
  const { roads } = useRoadList(apiBaseUrl, enabled);
  const [choice, setChoiceState] = useState<RoadChoice>({ kind: "none" });
  const [candidates, setCandidates] = useState<RoadExtensionCandidate[]>([]);
  // Whether the user selected manually. Once changed, do not overwrite with candidates.
  const touchedRef = useRef(false);

  const setChoice = useCallback((next: RoadChoice) => {
    touchedRef.current = true;
    setChoiceState(next);
  }, []);

  const first = rawPoints[0];
  const last = rawPoints[rawPoints.length - 1];
  const keyOf = (p: number[] | undefined) =>
    p ? `${Math.round(p[0] / ENDPOINT_KEY_GRID_M)},${Math.round(p[p.length - 1] / ENDPOINT_KEY_GRID_M)}` : "";
  const endpointKey = enabled && rawPoints.length >= 2 ? `${keyOf(first)}|${keyOf(last)}` : "";

  useEffect(() => {
    if (!apiBaseUrl || !endpointKey || !first || !last) {
      setCandidates([]);
      if (!touchedRef.current) setChoiceState({ kind: "none" });
      return;
    }
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      fetchRoadExtensionCandidates(apiBaseUrl, [first, last], controller.signal)
        .then((data) => {
          if (controller.signal.aborted) return;
          const found = data.candidates ?? [];
          setCandidates(found);
          if (!touchedRef.current) {
            setChoiceState(found.length > 0 ? { kind: "road", roadId: found[0].road_id } : { kind: "none" });
          }
        })
        .catch((err) => {
          if (!controller.signal.aborted) console.error("failed to fetch road extension candidates", err);
        });
    }, CANDIDATE_DEBOUNCE_MS);
    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
    // Endpoints are folded into endpointKey; array identity changes must not trigger new queries.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [apiBaseUrl, endpointKey]);

  /** The selected road's extension candidate, if present (for the "Extend ..." notice). */
  const extending = choice.kind === "road" ? candidates.find((c) => c.road_id === choice.roadId) ?? null : null;

  /** Fields added to each path on confirmation. */
  const postFields: Record<string, unknown> | undefined =
    enabled && choice.kind === "road" ? { road_id: choice.roadId } : undefined;

  /**
 * Call after successful confirmation. For a new road, create it from the created paths and notify road changes
 * (existing-road and no-road notifications are already handled by usePathConstruction.confirmPath).
 * Restart proposals for the next placement, which can propose extending the road just created.
 */
  const finalize = useCallback(
    async (createdPathIds: number[]): Promise<void> => {
      if (!enabled || !apiBaseUrl) return;
      const current = choice;
      touchedRef.current = false;
      setChoiceState({ kind: "none" });
      setCandidates([]);
      if (current.kind === "new" && createdPathIds.length > 0) {
        await createRoad(apiBaseUrl, { name: current.name.trim(), path_ids: createdPathIds });
        await notifyRoadsChanged(undefined, "path_build");
      }
    },
    [enabled, apiBaseUrl, choice],
  );

  return { roads, choice, setChoice, candidates, extending, postFields, finalize };
}

export type PathRoadAssignment = ReturnType<typeof usePathRoadAssignment>;
