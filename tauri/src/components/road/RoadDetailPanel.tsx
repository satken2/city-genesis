/**
 * Road detail panel.
 *
 * Right panel with window_type="road_detail", managing a road formed by grouping multiple paths (sections) divided
 * at intersections. Match line details (transit/TransitDetailPanel) and path details with agent-detail-* sections,
 * underlined tabs, and small buttons, plus a road-colored band directly below the header.
 *
 * Tabs:
 *   - Basic information: length, average traffic, maximum congestion, and delay tiles; section count, districts,
 *     travel times, and other table values.
 *   - Overview: highlight sections as road-colored bands on the 2D city map (RoadOverviewMap).
 *   - Strip map: vertical RoadStripMap.
 *   - Traffic: metric graphs against chainage (RoadTrafficProfile).
 *   - Settings: name, color, reverse endpoints, edit sections, straight-ahead extension, delete road.
 *
 * While open, show a road highlight in 3D (world:set_road_focus), resending when sections/colors change.
 * Rust handles clearing and ending section editing on close (window.rs close_window_by_label), because hidden
 * windows may be reused without unmounting.
 *
 * In section editing, edit_road:toggle_path from Unity toggles membership; return the complete result through
 * edit_road:set_members. Unity can end its mode for reasons unknown to the UI (Esc, another mode, etc.),
 * so clear editing display on edit_road:ended. Notify other windows and Unity through notifyRoadsChanged after
 * road/membership changes. The backend deletes roads with no sections; switch to the deleted state on fetch 404.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";
import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { ApiError } from "../../utils/api";
import {
  IPC_EVENT_ROAD_EDIT_ENDED,
  IPC_EVENT_ROAD_TOGGLE_PATH,
  sendClearRoadFocus,
  sendEndRoadMemberEdit,
  sendRoadEditSetMembers,
  sendRoadFocus,
  sendStartRoadMemberEdit,
  type RoadEditEndedPayload,
  type RoadTogglePathPayload,
} from "../../utils/ipc";
import { clearHintbarTextIf, setHintbarText } from "../../utils/hintbar";
import { resolveIntlLocale } from "../../utils/locale";
import { resolvePathIcon } from "../../utils/pathIcon";
import { ROAD_COLOR_PALETTE, ROAD_NAME_MAX_LENGTH } from "../../constants";
import { InfoHeader } from "../common/InfoHeader";
import { EditableLabel } from "../common/EditableLabel";
import { ColorSwatchPicker } from "../common/ColorSwatchPicker";
import { ZoneLink } from "../common/ZoneLink";
import { Meter } from "../common/Meter";
import {
  ROADS_CHANGED_EVENT,
  deleteRoad,
  fetchRoadDetail,
  fetchRoadList,
  isNamedRoadPathType,
  notifyRoadsChanged,
  resolveTraceBbox,
  roadDisplayName,
  traceRoad,
  updateRoad,
  updateRoadMembers,
} from "./roadApi";
import type { RoadDetail, RoadSummary, RoadTraceResponse } from "./roadTypes";
import {
  ROAD_VC_CONGESTED,
  dominantPathType,
  formatInt,
  formatKm,
  formatPercentValue,
  lanesSummary,
  sortedLanes,
} from "./roadFormat";
import { RoadOverviewMap } from "./RoadOverviewMap";
import { RoadStripMap } from "./RoadStripMap";
import { RoadTrafficProfile } from "./RoadTrafficProfile";
// Share right-panel sections/grids/tabs with agent details, and bands/tiles/small buttons/deletion blocks with line details.
import "../agent/AgentDetailPanel.css";
import "../transit/TransitDetailPanel.css";
import "./RoadDetailPanel.css";

type TabId = "basic" | "map" | "diagram" | "traffic" | "settings";

type RoadDetailPanelProps = {
  apiBaseUrl: string;
  initialEntityId: string;
  /** Opened just after creating a road in path details. Propose straight-ahead extension in Settings. */
  offerTrace?: boolean;
  /** Section from which to propose extension (the path used to create the road). */
  seedPathId?: number;
};

type Notice = { text: string; tone: "info" | "error" };

/**
 * Addition candidates for automatic straight-ahead extension. offer=true means a proposal immediately after creation.
 * bbox covers candidates and is included in overview bounds, since out-of-view sections cannot be loaded or drawn.
 */
type TracePreview = { ids: number[]; offer: boolean; bbox: [number, number, number, number] | null };

/**
 * Global Tauri event sent by a road detail panel starting section editing to other road detail panels (payload: { label }).
 * Unity supports only one section-editing mode, and edit_road:toggle_path reaches all windows, so all panels except
 * the latest must discard editing state (otherwise sections could be reassigned to another road).
 */
const ROAD_MEMBER_EDIT_STARTED_EVENT = "road:member_edit_started";

/** Event Rust (window.rs) sends only to the relevant panel when closing road details. */
const ROAD_PANEL_CLOSING_EVENT = "road:panel_closing";

/**
 * Closed panels remain hidden with live listeners. Manipulating 3D highlights or Unity state from a hidden panel
 * would disrupt another visible panel's highlights/editing, so check visibility before sending.
 */
function isWindowVisible(): Promise<boolean> {
  return getCurrentWindow().isVisible().catch(() => true);
}

function errorText(err: unknown, t: TFunction): string {
  if (err instanceof ApiError) {
    return t(`errors.${err.code}`, { defaultValue: err.message });
  }
  return err instanceof Error ? err.message : t("errors.UNKNOWN_ERROR");
}

export function RoadDetailPanel({ apiBaseUrl, initialEntityId, offerTrace = false, seedPathId }: RoadDetailPanelProps) {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);
  const roadId = Number(initialEntityId);

  const [road, setRoad] = useState<RoadDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [deleted, setDeleted] = useState(false);
  const [refreshKey, setRefreshKey] = useState(0);
  const [activeTab, setActiveTab] = useState<TabId>(offerTrace ? "settings" : "basic");
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [memberEditing, setMemberEditing] = useState(false);
  const [tracePreview, setTracePreview] = useState<TracePreview | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);

  // Mirrors for reading current values from async event handlers.
  const membersRef = useRef<number[]>([]);
  const previewRef = useRef<number[]>([]);
  const editingRef = useRef(false);
  const roadListRef = useRef<RoadSummary[]>([]);
  const lastMembersSigRef = useRef<string>("");
  const toggleChainRef = useRef<Promise<void>>(Promise.resolve());
  const offeredRef = useRef(false);
  /** Sequence number for each section-editing session. Match Unity edit_road:ended to prevent delayed previous-session ends from closing new edits. */
  const sessionRef = useRef(0);
  /** Guidance this panel placed in the hint bar (match during cleanup to avoid clearing another panel's hints). */
  const hintTextRef = useRef("");

  const refresh = useCallback(() => setRefreshKey((k) => k + 1), []);

  /* ---- Fetching ---- */
  useEffect(() => {
    if (!Number.isFinite(roadId) || roadId <= 0) {
      setError(`Invalid road id: ${initialEntityId}`);
      setLoading(false);
      return;
    }
    const controller = new AbortController();
    fetchRoadDetail(apiBaseUrl, roadId, controller.signal)
      .then((data) => {
        if (controller.signal.aborted) return;
        setRoad(data);
        setError(null);
        setDeleted(false);
        setLoading(false);
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        if (err instanceof ApiError && err.status === 404) {
          setDeleted(true);
          setRoad(null);
        } else {
          setError(err instanceof Error ? err.message : "Unknown error");
        }
        setLoading(false);
      });
    return () => controller.abort();
  }, [apiBaseUrl, roadId, initialEntityId, refreshKey]);

  // Refetch after road changes in other windows or completed epochs (which change aggregate traffic values, etc.).
  useEffect(() => {
    const unlisteners: (() => void)[] = [];
    let disposed = false;
    const add = (p: Promise<() => void>) =>
      p.then((fn) => (disposed ? fn() : unlisteners.push(fn))).catch(console.error);
    add(listen(ROADS_CHANGED_EVENT, () => refresh()));
    add(
      listen<{ executing: boolean }>("epoch:executing", (event) => {
        if (!event.payload.executing) refresh();
      }),
    );
    return () => {
      disposed = true;
      unlisteners.forEach((fn) => fn());
    };
  }, [refresh]);

  /* ---- 3D highlight bands ---- */
  // Candidates during section editing use edit_road:set_members, so include candidates in highlight bands only outside that mode.
  const focusIds = useMemo(() => {
    if (!road) return null;
    const preview = !memberEditing && tracePreview ? tracePreview.ids : [];
    return [...road.path_ids, ...preview.filter((id) => !road.path_ids.includes(id))];
  }, [road, memberEditing, tracePreview]);
  const focusSig = road && focusIds ? `${road.road_id}|${road.color}|${focusIds.join(",")}` : "";

  useEffect(() => {
    if (!road || !focusIds || focusSig === "") return;
    const { road_id, color } = road;
    void isWindowVisible().then((visible) => {
      if (visible) void sendRoadFocus(road_id, color, focusIds);
    });
    // Compare content signatures to avoid resending identical data on every refetch.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [focusSig]);

  // Only one 3D highlight exists; when multiple road details are open, restore the focused panel's road.
  const focusRef = useRef<{ roadId: number; color: string; ids: number[] } | null>(null);
  focusRef.current = road && focusIds ? { roadId: road.road_id, color: road.color, ids: focusIds } : null;
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    let disposed = false;
    getCurrentWindow()
      .onFocusChanged(({ payload: focused }) => {
        const f = focusRef.current;
        if (focused && f) void sendRoadFocus(f.roadId, f.color, f.ids);
      })
      .then((fn) => (disposed ? fn() : (unlisten = fn)))
      .catch(console.error);
    return () => {
      disposed = true;
      unlisten?.();
    };
  }, []);

  useEffect(() => {
    if (road) membersRef.current = road.path_ids;
  }, [road]);

  useEffect(() => {
    previewRef.current = tracePreview?.ids ?? [];
  }, [tracePreview]);

  // Send nothing on unmount. Rust handles close cleanup; when a hidden window unmounts for reuse as another type,
  // cleanup here would erase another visible road detail panel's highlights or hints.
  useEffect(() => {
    if (!deleted) return;
    void isWindowVisible().then((visible) => {
      if (visible) void sendClearRoadFocus(roadId);
    });
  }, [deleted, roadId]);

  /** Send members/candidates to Unity during section editing, without resending identical data. */
  const syncEditMembers = useCallback(
    (pathIds: number[], previewIds: number[]) => {
      if (!editingRef.current) return;
      const preview = previewIds.filter((id) => !pathIds.includes(id));
      const sig = `${pathIds.join(",")}|${preview.join(",")}`;
      if (sig === lastMembersSigRef.current) return;
      lastMembersSigRef.current = sig;
      void sendRoadEditSetMembers(roadId, pathIds, preview);
    },
    [roadId],
  );

  useEffect(() => {
    if (memberEditing && road) syncEditMembers(road.path_ids, tracePreview?.ids ?? []);
  }, [memberEditing, road, tracePreview, syncEditMembers]);

  const loadRoadList = useCallback(() => {
    fetchRoadList(apiBaseUrl)
      .then((res) => {
        roadListRef.current = res.roads;
      })
      .catch(console.error);
  }, [apiBaseUrl]);

  /* ---- Section editing mode ---- */
  /**
 * Clear editing display without sending to Unity. With clearHint=true, remove this panel's guidance from the hint bar.
 * When another road detail starts editing, its guidance has already replaced ours, so do not clear it.
 */
  const dropMemberEdit = useCallback((clearHint: boolean) => {
    editingRef.current = false;
    lastMembersSigRef.current = "";
    setMemberEditing(false);
    if (clearHint && hintTextRef.current !== "") void clearHintbarTextIf(hintTextRef.current);
    hintTextRef.current = "";
  }, []);

  // Unity ends editing only if it is editing this road's sections. It does not terminate unrelated modes entered
  // without the UI's knowledge, so this is safe from hidden panels and deletion/404 cleanup.
  const endMemberEdit = useCallback(() => {
    if (!editingRef.current) return;
    dropMemberEdit(true);
    void sendEndRoadMemberEdit(roadId);
  }, [dropMemberEdit, roadId]);

  // If another road detail panel starts editing, Unity has invalidated this panel's editing state.
  useEffect(() => {
    const ownLabel = getCurrentWindow().label;
    let unlisten: (() => void) | undefined;
    let disposed = false;
    listen<{ label: string }>(ROAD_MEMBER_EDIT_STARTED_EVENT, (event) => {
      if (event.payload.label === ownLabel || !editingRef.current) return;
      dropMemberEdit(false);
    })
      .then((fn) => (disposed ? fn() : (unlisten = fn)))
      .catch(console.error);
    return () => {
      disposed = true;
      unlisten?.();
    };
  }, [dropMemberEdit]);

  // Unity section editing ended for any reason. Clear display if this matches our road and current session.
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    let disposed = false;
    listen<RoadEditEndedPayload>(IPC_EVENT_ROAD_EDIT_ENDED, (event) => {
      const { road_id: endedRoadId, session_id: sessionId } = event.payload;
      if (!editingRef.current || endedRoadId !== roadId) return;
      // Session numbers are positive integers. Missing/nonpositive numbers (Unity's fallback if not received) cannot match, so check road ID only.
      const endedSession = Number(sessionId);
      if (Number.isFinite(endedSession) && endedSession > 0 && endedSession !== sessionRef.current) return;
      dropMemberEdit(true);
    })
      .then((fn) => (disposed ? fn() : (unlisten = fn)))
      .catch(console.error);
    return () => {
      disposed = true;
      unlisten?.();
    };
  }, [roadId, dropMemberEdit]);

  // Panel closed. Rust ends Unity mode with edit_road:cancel, so clear only display and guidance.
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    let disposed = false;
    getCurrentWindow()
      .listen(ROAD_PANEL_CLOSING_EVENT, () => {
        if (editingRef.current) dropMemberEdit(true);
        setTracePreview(null);
      })
      .then((fn) => (disposed ? fn() : (unlisten = fn)))
      .catch(console.error);
    return () => {
      disposed = true;
      unlisten?.();
    };
  }, [dropMemberEdit]);

  const startMemberEdit = useCallback(() => {
    if (!road) return;
    editingRef.current = true;
    setMemberEditing(true);
    lastMembersSigRef.current = `${road.path_ids.join(",")}|`;
    // Use positive values fitting Unity int. Derive from time to avoid collisions with reopened panels for the same road that forgot earlier numbers.
    const nextSession = (Date.now() % 1_000_000_000) || 1;
    sessionRef.current = nextSession !== sessionRef.current ? nextSession : (nextSession % 999_999_999) + 1;
    void emit(ROAD_MEMBER_EDIT_STARTED_EVENT, { label: getCurrentWindow().label });
    void sendStartRoadMemberEdit(road.road_id, road.color, road.path_ids, sessionRef.current);
    // Include the road name to distinguish guidance from other road detail panels for cleanup matching.
    const hint = t("road.memberEditHintFor", {
      road: roadDisplayName(road.name, road.road_id, t),
      hint: t("road.memberEditHint"),
    });
    hintTextRef.current = hint;
    void setHintbarText(hint);
    loadRoadList();
    setNotice(null);
  }, [road, t, loadRoadList]);

  // Also end section editing if the road disappears through another window (fetch 404).
  useEffect(() => {
    if (deleted) endMemberEdit();
  }, [deleted, endMemberEdit]);

  /** Cleanup when the road is deleted after losing its last section. */
  const handleRoadVanished = useCallback(() => {
    endMemberEdit();
    setTracePreview(null);
    setDeleted(true);
    setRoad(null);
    void notifyRoadsChanged(roadId);
  }, [endMemberEdit, roadId]);

  const handleToggle = useCallback(
    async (payload: RoadTogglePathPayload) => {
      if (!editingRef.current) return;
      if (!isNamedRoadPathType(payload.path_type)) {
        setNotice({ text: t("road.notNamedRoadType"), tone: "error" });
        return;
      }
      const members = new Set(membersRef.current);
      let body: { add?: number[]; remove?: number[] };
      if (payload.shift) {
        const res = await traceRoad(apiBaseUrl, { pathId: payload.path_id, roadId });
        const add = [...new Set([payload.path_id, ...res.path_ids])].filter((id) => !members.has(id));
        if (add.length === 0) {
          setNotice({ text: t("road.noTraceCandidates"), tone: "info" });
          return;
        }
        body = { add };
      } else if (members.has(payload.path_id)) {
        body = { remove: [payload.path_id] };
      } else {
        body = { add: [payload.path_id] };
      }

      // Record the original road name because sections assigned elsewhere will be reassigned.
      const sources = new Set<string>();
      for (const id of body.add ?? []) {
        const owner = roadListRef.current.find((r) => r.road_id !== roadId && r.path_ids.includes(id));
        if (owner) sources.add(roadDisplayName(owner.name, owner.road_id, t));
      }

      const res = await updateRoadMembers(apiBaseUrl, roadId, body);
      if (res.deleted || !res.road) {
        handleRoadVanished();
        return;
      }
      membersRef.current = res.road.path_ids;
      syncEditMembers(res.road.path_ids, previewRef.current);
      if (sources.size > 0) {
        setNotice({ text: t("road.reassigned", { roads: [...sources].join(t("road.listSeparator")) }), tone: "info" });
      } else if (body.remove) {
        setNotice({ text: t("road.segmentRemoved", { id: payload.path_id }), tone: "info" });
      } else {
        setNotice({ text: t("road.segmentsAdded", { n: body.add?.length ?? 0 }), tone: "info" });
      }
      await notifyRoadsChanged(roadId);
      loadRoadList();
      refresh();
    },
    [apiBaseUrl, roadId, t, handleRoadVanished, syncEditMembers, loadRoadList, refresh],
  );

  useEffect(() => {
    if (!memberEditing) return;
    let unlisten: (() => void) | undefined;
    let disposed = false;
    listen<RoadTogglePathPayload>(IPC_EVENT_ROAD_TOGGLE_PATH, (event) => {
      // Process sequentially so rapid clicks do not reorder membership updates.
      toggleChainRef.current = toggleChainRef.current
        .then(() => handleToggle(event.payload))
        .catch((err: unknown) => {
          setNotice({ text: errorText(err, t), tone: "error" });
          // If another operation removed the section (split/merge/delete), refetch to prepare for the next click.
          refresh();
        });
    })
      .then((fn) => (disposed ? fn() : (unlisten = fn)))
      .catch(console.error);
    return () => {
      disposed = true;
      unlisten?.();
    };
  }, [memberEditing, handleToggle, t, refresh]);

  /* ---- Automatic straight-ahead extension ---- */
  const showTracePreview = useCallback(
    async (res: RoadTraceResponse, offer: boolean) => {
      const fresh = res.path_ids.filter((id) => !membersRef.current.includes(id));
      if (fresh.length === 0) {
        if (!offer) setNotice({ text: t("road.noTraceCandidates"), tone: "info" });
        setTracePreview(null);
        return;
      }
      const bbox = await resolveTraceBbox(apiBaseUrl, res, fresh).catch(() => null);
      setTracePreview({ ids: fresh, offer, bbox });
      setNotice(null);
    },
    [apiBaseUrl, t],
  );

  const handleTraceFromEnds = useCallback(async () => {
    setBusy(true);
    try {
      const res = await traceRoad(apiBaseUrl, { roadId });
      await showTracePreview(res, false);
    } catch (err) {
      setNotice({ text: errorText(err, t), tone: "error" });
    } finally {
      setBusy(false);
    }
  }, [apiBaseUrl, roadId, showTracePreview, t]);

  // Immediately after creating a road, propose candidates traced straight ahead from the originating section once.
  useEffect(() => {
    if (!road || !offerTrace || seedPathId == null || offeredRef.current) return;
    offeredRef.current = true;
    traceRoad(apiBaseUrl, { pathId: seedPathId, roadId })
      .then((res) => showTracePreview(res, true))
      .catch(console.error);
  }, [road, offerTrace, seedPathId, apiBaseUrl, roadId, showTracePreview]);

  const confirmTrace = useCallback(async () => {
    if (!tracePreview) return;
    setBusy(true);
    try {
      const res = await updateRoadMembers(apiBaseUrl, roadId, { add: tracePreview.ids });
      setTracePreview(null);
      if (res.deleted || !res.road) {
        handleRoadVanished();
        return;
      }
      membersRef.current = res.road.path_ids;
      syncEditMembers(res.road.path_ids, []);
      setNotice({ text: t("road.segmentsAdded", { n: tracePreview.ids.length }), tone: "info" });
      await notifyRoadsChanged(roadId);
      refresh();
    } catch (err) {
      setNotice({ text: errorText(err, t), tone: "error" });
    } finally {
      setBusy(false);
    }
  }, [apiBaseUrl, roadId, tracePreview, handleRoadVanished, syncEditMembers, refresh, t]);

  /* ---- Name, color, direction, deletion ---- */
  const saveRoad = useCallback(
    async (body: { name?: string; color?: string; reverse?: boolean }) => {
      await updateRoad(apiBaseUrl, roadId, body);
      await notifyRoadsChanged(roadId);
      refresh();
    },
    [apiBaseUrl, roadId, refresh],
  );

  /** Throwing keeps EditableLabel in edit mode with its generic rename-failed message; the thrown text itself is not shown. */
  const handleNameSave = useCallback(
    async (next: string) => {
      const name = next.trim().replace(/\s+/g, " ");
      if (name.length > ROAD_NAME_MAX_LENGTH) {
        throw new Error(t("road.nameTooLong", { max: ROAD_NAME_MAX_LENGTH }));
      }
      try {
        await saveRoad({ name });
      } catch (err) {
        throw new Error(errorText(err, t));
      }
    },
    [saveRoad, t],
  );

  const runAction = useCallback(
    async (action: () => Promise<void>) => {
      setBusy(true);
      try {
        await action();
      } catch (err) {
        setNotice({ text: errorText(err, t), tone: "error" });
      } finally {
        setBusy(false);
      }
    },
    [t],
  );

  const handleDelete = useCallback(async () => {
    setBusy(true);
    try {
      endMemberEdit();
      await deleteRoad(apiBaseUrl, roadId);
      await notifyRoadsChanged(roadId);
      await invoke("close_current_window");
    } catch (err) {
      setNotice({ text: errorText(err, t), tone: "error" });
      setConfirmDelete(false);
      setBusy(false);
    }
  }, [apiBaseUrl, roadId, endMemberEdit, t]);

  /* ---- Drawing ---- */
  if (loading) {
    return <p className="agent-detail-muted">{t("common.loading")}</p>;
  }
  if (deleted) {
    return (
      <div className="agent-detail">
        <div className="road-detail__deleted" role="status">
          <strong>{t("road.deletedTitle")}</strong>
          <p>{t("road.deletedDescription")}</p>
        </div>
      </div>
    );
  }
  if (error || !road) {
    return (
      <div className="agent-detail-error">
        <strong>{t("common.fetchError")}</strong>
        <p>{error}</p>
      </div>
    );
  }

  const stats = road.stats;
  const displayName = roadDisplayName(road.name, road.road_id, t);
  const km = (m: number) => `${formatKm(m, locale)} km`;
  const dash = "—";
  const openWindow = (windowType: string, id: number) => {
    void invoke("open_registered_window", { windowType, entityId: String(id) });
  };

  const tabs: { id: TabId; label: string }[] = [
    { id: "basic", label: t("road.tabs.basic") },
    { id: "map", label: t("road.tabs.map") },
    { id: "diagram", label: t("road.tabs.diagram") },
    { id: "traffic", label: t("road.tabs.traffic") },
    { id: "settings", label: t("road.tabs.settings") },
  ];

  const speedLimitText =
    stats.speed_limit_min == null || stats.speed_limit_max == null
      ? dash
      : Math.round(stats.speed_limit_min) === Math.round(stats.speed_limit_max)
        ? `${Math.round(stats.speed_limit_min)} km/h`
        : `${Math.round(stats.speed_limit_min)}–${Math.round(stats.speed_limit_max)} km/h`;

  const minutes = (v: number | null) => (v == null ? dash : t("road.minutesValue", { v: v.toLocaleString(locale, { maximumFractionDigits: 1 }) }));
  const kmh = (v: number | null) => (v == null ? dash : `${v.toLocaleString(locale, { maximumFractionDigits: 1 })} km/h`);

  return (
    <div className="agent-detail">
      <InfoHeader
        iconSrc={resolvePathIcon(dominantPathType(road.segments))}
        showStatus={false}
        variantName={lanesSummary(stats, t)}
        name={displayName}
        onNameSave={handleNameSave}
        nameValue={road.name}
      />
      <div className="transit-detail__stripe" style={{ background: road.color }} aria-hidden="true" />

      {memberEditing && (
        <div className="road-detail__editing" role="status">
          <span className="road-detail__editing-text">
            {t("road.memberEditing", { n: road.segment_count })}
          </span>
          <button type="button" className="transit-detail__btn transit-detail__btn--primary" onClick={endMemberEdit}>
            {t("road.memberEditDone")}
          </button>
        </div>
      )}

      {tracePreview && (
        <div className="road-detail__preview" role="status">
          <span className="road-detail__preview-text">
            {t(tracePreview.offer ? "road.traceOffer" : "road.tracePreview", { n: tracePreview.ids.length })}
          </span>
          <div className="transit-detail__actions">
            <button
              type="button"
              className="transit-detail__btn transit-detail__btn--ghost"
              disabled={busy}
              onClick={() => setTracePreview(null)}
            >
              {t("road.traceDiscard")}
            </button>
            <button
              type="button"
              className="transit-detail__btn transit-detail__btn--primary"
              disabled={busy}
              onClick={() => void confirmTrace()}
            >
              {t("road.traceConfirm")}
            </button>
          </div>
        </div>
      )}

      {notice && (
        <p className={`road-detail__notice road-detail__notice--${notice.tone}`}>
          {notice.text}
          <button type="button" className="road-detail__notice-close" aria-label={t("road.dismiss")} onClick={() => setNotice(null)}>
            ×
          </button>
        </p>
      )}

      <div className="agent-detail-tabs">
        {tabs.map((tab) => (
          <button
            key={tab.id}
            type="button"
            className={`agent-detail-tab${tab.id === activeTab ? " agent-detail-tab--active" : ""}`}
            onClick={() => setActiveTab(tab.id)}
          >
            {tab.label}
          </button>
        ))}
      </div>

      <div className="agent-detail-tab-content">
        {activeTab === "basic" && (
          <>
            <section className="agent-detail-section">
              <div className="transit-detail__tiles">
                <StatTile label={t("road.stats.length")} value={formatKm(stats.length_m, locale)} unit="km" />
                <StatTile
                  label={t("road.stats.avgTraffic")}
                  value={stats.avg_daily_traffic == null ? dash : formatInt(stats.avg_daily_traffic, locale)}
                  unit={stats.avg_daily_traffic == null ? undefined : t("road.unit.vehiclesPerDay")}
                />
                <StatTile
                  label={t("road.stats.maxVc")}
                  value={stats.max_vc == null ? dash : formatPercentValue(stats.max_vc.value, locale)}
                  tone={stats.max_vc != null && stats.max_vc.value >= ROAD_VC_CONGESTED ? "negative" : undefined}
                  hint={stats.max_vc == null
                    ? undefined
                    : `${formatKm(stats.max_vc.from_km * 1000, locale)}–${formatKm(stats.max_vc.to_km * 1000, locale)} km`}
                  onClick={stats.max_vc == null ? undefined : () => openWindow("path_detail", stats.max_vc!.path_id)}
                  title={stats.max_vc == null ? undefined : t("road.openMaxVcSegment")}
                />
                <StatTile
                  label={t("road.stats.delay")}
                  value={stats.delay_vehicle_hours.toLocaleString(locale, { maximumFractionDigits: 1 })}
                  unit={t("road.unit.vehicleHoursPerDay")}
                />
              </div>
            </section>

            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">{t("road.tabs.basic")}</h3>
              <div className="agent-detail-grid road-detail__grid">
                <span className="agent-detail-label">{t("road.stats.segments")}</span>
                <span className="agent-detail-value">{formatInt(stats.segment_count, locale)}</span>

                <span className="agent-detail-label">{t("road.stats.zones")}</span>
                <span className="agent-detail-value road-detail__wrap">
                  {road.zones.length === 0
                    ? dash
                    : road.zones.map((z, i) => (
                        <span key={z.zone_id} className="road-detail__zone">
                          {i > 0 && <span className="road-detail__sep">{t("road.listSeparator")}</span>}
                          <ZoneLink zoneId={z.zone_id} zoneName={z.name} />
                        </span>
                      ))}
                </span>

                <span className="agent-detail-label">{t("road.stats.lanes")}</span>
                <span className="agent-detail-value road-detail__wrap">
                  {stats.lanes_composition.length === 0
                    ? dash
                    : sortedLanes(stats)
                        .map((l) => t("road.lanesLength", { n: l.lanes, length: km(l.length_m) }))
                        .join(t("road.listSeparator"))}
                </span>

                <span className="agent-detail-label">{t("road.stats.speedLimit")}</span>
                <span className="agent-detail-value">{speedLimitText}</span>

                <span className="agent-detail-label">{t("road.stats.travelTime")}</span>
                <span className="agent-detail-value road-detail__wrap">
                  {/* Explain unavailable calculations as planned/under-construction sections only when present; otherwise cite inactive/impassable sections. */}
                  {stats.travel_time_min == null && stats.travel_time_free_min == null
                    ? (
                        <span className="road-detail__muted">
                          {t(stats.planned_length_m > 0
                            ? "road.stats.travelTimeUnavailable"
                            : "road.stats.travelTimeUnavailableImpassable")}
                        </span>
                      )
                    : t("road.stats.travelTimeValue", {
                        congested: minutes(stats.travel_time_min),
                        free: minutes(stats.travel_time_free_min),
                      })}
                </span>

                <span className="agent-detail-label">{t("road.stats.travelSpeed")}</span>
                <span className="agent-detail-value road-detail__wrap">
                  {stats.travel_speed_kmh == null && stats.travel_speed_free_kmh == null
                    ? dash
                    : t("road.stats.travelSpeedValue", {
                        congested: kmh(stats.travel_speed_kmh),
                        free: kmh(stats.travel_speed_free_kmh),
                      })}
                </span>

                <span className="agent-detail-label">{t("road.stats.vehicleKm")}</span>
                <span className="agent-detail-value">
                  {formatInt(stats.vehicle_km, locale)}
                  <span className="transit-detail__unit">{t("road.unit.vehicleKmPerDay")}</span>
                </span>

                <span className="agent-detail-label">{t("road.stats.congestedLength")}</span>
                <span className="agent-detail-value">{km(stats.congested_length_m)}</span>

                <span className="agent-detail-label">{t("road.stats.durability")}</span>
                <span className="agent-detail-value transit-detail__meter">
                  {stats.avg_durability == null ? (
                    dash
                  ) : (
                    <>
                      <Meter value={stats.avg_durability} />
                      <span className="transit-detail__meter-value">{formatPercentValue(stats.avg_durability, locale)}</span>
                      {stats.low_durability_count > 0 && (
                        <span className="road-detail__warn-tag">{t("road.stats.lowDurability", { n: stats.low_durability_count })}</span>
                      )}
                    </>
                  )}
                </span>

                <span className="agent-detail-label">{t("road.stats.accidents")}</span>
                <span className="agent-detail-value">
                  {t("road.segmentsCount", { n: stats.accident_segment_count })}
                </span>

                <span className="agent-detail-label">{t("road.stats.planned")}</span>
                <span className="agent-detail-value">{km(stats.planned_length_m)}</span>

                <span className="agent-detail-label">{t("road.stats.transitLines")}</span>
                <span className="agent-detail-value road-detail__wrap">
                  {road.transit_lines.length === 0
                    ? dash
                    : road.transit_lines.map((line) => (
                        <button
                          key={line.line_agent_id}
                          type="button"
                          className="road-detail__line-chip"
                          onClick={() => openWindow("transit_detail", line.line_agent_id)}
                        >
                          <i style={{ background: line.color }} />
                          {line.name !== "" ? line.name : `#${line.line_agent_id}`}
                        </button>
                      ))}
                </span>
              </div>
            </section>
          </>
        )}

        {activeTab === "map" && (
          <section className="agent-detail-section">
            <RoadOverviewMap
              apiBaseUrl={apiBaseUrl}
              bbox={road.bbox}
              pathIds={road.path_ids}
              color={road.color}
              previewPathIds={tracePreview?.ids ?? null}
              previewBbox={tracePreview?.bbox ?? null}
            />
          </section>
        )}

        {activeTab === "diagram" && (
          <section className="agent-detail-section">
            {road.segments.length > 0 ? <RoadStripMap road={road} /> : <p className="agent-detail-muted">{t("road.noSegments")}</p>}
          </section>
        )}

        {activeTab === "traffic" && (
          <section className="agent-detail-section">
            <RoadTrafficProfile road={road} />
          </section>
        )}

        {activeTab === "settings" && (
          <>
            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">{t("road.tabs.settings")}</h3>
              <div className="agent-detail-grid transit-detail__settings-grid">
                <span className="agent-detail-label">{t("road.settings.name")}</span>
                <span className="agent-detail-value">
                  <EditableLabel value={road.name} displayText={displayName} allowEmpty={true} onSave={handleNameSave} />
                </span>
                <span className="agent-detail-label">{t("road.settings.color")}</span>
                <span className="agent-detail-value transit-detail__color-value">
                  <ColorSwatchPicker
                    value={road.color.toLowerCase()}
                    presets={ROAD_COLOR_PALETTE.map((c) => c.toLowerCase())}
                    disabled={busy}
                    onChange={(hex) => {
                      if (hex.toLowerCase() === road.color.toLowerCase()) return;
                      void runAction(() => saveRoad({ color: hex }));
                    }}
                  />
                </span>
              </div>
            </section>

            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">{t("road.settings.directionTitle")}</h3>
              <div className="road-detail__action-row">
                <span className="road-detail__action-desc">{t("road.settings.reverseDescription")}</span>
                <button
                  type="button"
                  className="transit-detail__btn transit-detail__btn--ghost"
                  disabled={busy}
                  onClick={() => void runAction(() => saveRoad({ reverse: true }))}
                >
                  {t("road.settings.reverse")}
                </button>
              </div>
            </section>

            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">{t("road.settings.segmentsTitle")}</h3>
              <div className="road-detail__action-row">
                <span className="road-detail__action-desc">
                  {t("road.settings.segmentsSummary", { n: road.segment_count, length: km(road.length_m) })}
                </span>
                {memberEditing ? (
                  <button type="button" className="transit-detail__btn transit-detail__btn--primary" onClick={endMemberEdit}>
                    {t("road.memberEditDone")}
                  </button>
                ) : (
                  <button type="button" className="transit-detail__btn transit-detail__btn--ghost" disabled={busy} onClick={startMemberEdit}>
                    {t("road.settings.editSegments")}
                  </button>
                )}
              </div>
              <div className="road-detail__action-row">
                <span className="road-detail__action-desc">{t("road.settings.traceDescription")}</span>
                <button
                  type="button"
                  className="transit-detail__btn transit-detail__btn--ghost"
                  disabled={busy}
                  onClick={() => void handleTraceFromEnds()}
                >
                  {t("road.settings.trace")}
                </button>
              </div>
            </section>

            <section className="agent-detail-section transit-detail__danger">
              <div className="transit-detail__danger-row">
                <div className="transit-detail__danger-text">
                  <span className="transit-detail__danger-title">{t("road.settings.deleteTitle")}</span>
                  <span className="transit-detail__danger-desc">{t("road.settings.deleteDescription")}</span>
                </div>
                {confirmDelete ? (
                  <div className="transit-detail__actions">
                    <button
                      type="button"
                      className="transit-detail__btn transit-detail__btn--ghost"
                      disabled={busy}
                      onClick={() => setConfirmDelete(false)}
                    >
                      {t("road.settings.cancel")}
                    </button>
                    <button
                      type="button"
                      className="transit-detail__btn transit-detail__btn--danger-solid"
                      disabled={busy}
                      onClick={() => void handleDelete()}
                    >
                      {busy ? t("common.processing") : t("road.settings.deleteConfirm")}
                    </button>
                  </div>
                ) : (
                  <button
                    type="button"
                    className="transit-detail__btn transit-detail__btn--danger"
                    onClick={() => setConfirmDelete(true)}
                  >
                    {t("road.settings.delete")}
                  </button>
                )}
              </div>
            </section>
          </>
        )}
      </div>
    </div>
  );
}

type StatTileProps = {
  label: string;
  value: string;
  unit?: string;
  tone?: "positive" | "negative";
  hint?: string;
  title?: string;
  onClick?: () => void;
};

/** Basic information tiles (label above, value below), styled like line detail performance tiles. */
function StatTile({ label, value, unit, tone, hint, title, onClick }: StatTileProps) {
  const className = [
    "transit-detail__tile",
    tone && `transit-detail__tile--${tone}`,
    onClick && "road-detail__tile--link",
  ]
    .filter(Boolean)
    .join(" ");
  const body = (
    <>
      <span className="transit-detail__tile-label">{label}</span>
      <span className="transit-detail__tile-value">
        {value}
        {unit && unit !== "" && <span className="transit-detail__unit">{unit}</span>}
      </span>
      {hint && <span className="road-detail__tile-hint">{hint}</span>}
    </>
  );
  return onClick ? (
    <button type="button" className={className} title={title} onClick={onClick}>
      {body}
    </button>
  ) : (
    <div className={className}>{body}</div>
  );
}
