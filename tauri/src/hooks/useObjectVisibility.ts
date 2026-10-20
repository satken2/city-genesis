/**
 * Shared hook for per-object building/road visibility. Unity (ObjectVisibilityFilter) owns the state; the
 * backend never stores it, including in saves (reopening a game restores all objects). The UI sends the complete
 * hidden ID set via `view:set_object_filter` and receives current state via `view:object_filter_state`, like the
 * layer display options panel querying Unity with `view:query_state`.
 *
 * Shared by construction project windows (tag/row toggles) and building/road detail panels (individual toggles).
 * Unity broadcasts current state to all windows after changes from any window, keeping displays synchronized.
 */

import { useCallback, useEffect, useState } from "react";
import { listen } from "@tauri-apps/api/event";

import {
  IPC_EVENT_VIEW_OBJECT_FILTER_STATE,
  sendQueryObjectFilter,
  sendSetObjectFilter,
} from "../utils/ipc";

export type ObjectKind = "agent" | "path";

type ObjectFilterStatePayload = {
  hidden_building_ids?: number[];
  hidden_path_ids?: number[];
};

export function useObjectVisibility(windowType: string) {
  const [hiddenBuildings, setHiddenBuildings] = useState<Set<number>>(new Set());
  const [hiddenPaths, setHiddenPaths] = useState<Set<number>>(new Set());

  // Wait for listener registration before querying to avoid missing the response.
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    let cancelled = false;

    const setup = async () => {
      unlisten = await listen<ObjectFilterStatePayload>(
        IPC_EVENT_VIEW_OBJECT_FILTER_STATE,
        (event) => {
          setHiddenBuildings(new Set(event.payload?.hidden_building_ids ?? []));
          setHiddenPaths(new Set(event.payload?.hidden_path_ids ?? []));
        },
      );
      if (cancelled) {
        unlisten();
        unlisten = undefined;
        return;
      }
      await sendQueryObjectFilter(windowType);
    };

    setup().catch((err) => {
      console.warn("[useObjectVisibility] setup failed:", err);
    });

    return () => {
      cancelled = true;
      unlisten?.();
    };
  }, [windowType]);

  /**
   * Send the hidden set and optimistically update local state. Unity's `view:object_filter_state` remains
   * authoritative; the next response reconciles state even after failure.
   */
  const applyHidden = useCallback(
    (buildings: Set<number>, paths: Set<number>) => {
      setHiddenBuildings(buildings);
      setHiddenPaths(paths);
      sendSetObjectFilter(Array.from(buildings), Array.from(paths), windowType).catch((err) => {
        console.error("[useObjectVisibility] sendSetObjectFilter failed:", err);
      });
    },
    [windowType],
  );

  /** Whether the specified object is hidden. */
  const isHidden = useCallback(
    (kind: ObjectKind, id: number) =>
      kind === "path" ? hiddenPaths.has(id) : hiddenBuildings.has(id),
    [hiddenBuildings, hiddenPaths],
  );

  /** Toggle the specified object's visibility. */
  const toggleObject = useCallback(
    (kind: ObjectKind, id: number) => {
      const buildings = new Set(hiddenBuildings);
      const paths = new Set(hiddenPaths);
      const target = kind === "path" ? paths : buildings;
      if (target.has(id)) target.delete(id); else target.add(id);
      applyHidden(buildings, paths);
    },
    [applyHidden, hiddenBuildings, hiddenPaths],
  );

  /** Show/hide multiple objects together (used for tag-based toggles). */
  const setObjectsHidden = useCallback(
    (targets: { kind: ObjectKind; id: number }[], hidden: boolean) => {
      const buildings = new Set(hiddenBuildings);
      const paths = new Set(hiddenPaths);
      for (const target of targets) {
        const set = target.kind === "path" ? paths : buildings;
        if (hidden) set.add(target.id); else set.delete(target.id);
      }
      applyHidden(buildings, paths);
    },
    [applyHidden, hiddenBuildings, hiddenPaths],
  );

  /** Restore visibility for all objects. */
  const showAll = useCallback(() => {
    applyHidden(new Set(), new Set());
  }, [applyHidden]);

  return {
    hiddenBuildings,
    hiddenPaths,
    hiddenCount: hiddenBuildings.size + hiddenPaths.size,
    isHidden,
    toggleObject,
    setObjectsHidden,
    showAll,
  };
}
