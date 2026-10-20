/**
 * Road membership selection components.
 *
 * Choose which named road a section (street/highway path) belongs to.
 * - RoadMembershipRow: path-detail membership row, saved immediately. Create new roads from this one section,
 *   then open road details with a proposal to extend straight ahead.
 * - RoadSelect: dropdown (none / existing road / new road). Construction uses deferred mode, retaining only a name
 *   for new roads and creating them after placement confirmation.
 * - useRoadList: fetch roads and refetch on ROADS_CHANGED_EVENT.
 *
 * Match tag-row styling (common/TagRow and TagSelect) by reusing their CSS classes.
 * Section road_id is the membership source of truth; VALUE_NONE (-1) denotes unnamed, unassigned paths.
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";

import { ROAD_NAME_MAX_LENGTH } from "../../constants";
import { resolveIntlLocale } from "../../utils/locale";
import {
  ROADS_CHANGED_EVENT,
  createRoad,
  fetchRoadList,
  hasRoad,
  notifyRoadsChanged,
  roadDisplayName,
  setPathRoad,
} from "./roadApi";
import type { RoadSummary } from "./roadTypes";
import "../common/TagSelect.css";
import "./RoadMembershipRow.css";

/** Road membership in path detail/list responses. Extend the type without changing handwritten PathData. */
export type PathRoadFields = { road_id?: number | null };

/** Membership choice. new means a new road (name may be empty; display code supplies "Road #id"). */
export type RoadChoice = { kind: "none" } | { kind: "road"; roadId: number } | { kind: "new"; name: string };

/** Derive selection from road_id. Unassigned/invalid values become none. */
export function roadChoiceOf(roadId: number | null | undefined): RoadChoice {
  return hasRoad(roadId) ? { kind: "road", roadId } : { kind: "none" };
}

/** Dropdown sentinel for a new road. Existing road values are positive integer strings, so cannot collide. */
const NEW_ROAD_OPTION = "new";

/** Fetch roads and track changes from other windows. Do not fetch while enabled=false. */
export function useRoadList(apiBaseUrl: string | undefined, enabled = true) {
  const [roads, setRoads] = useState<RoadSummary[]>([]);

  const reload = useCallback(
    async (signal?: AbortSignal) => {
      if (!apiBaseUrl || !enabled) return;
      const data = await fetchRoadList(apiBaseUrl, signal);
      if (!signal?.aborted) setRoads(data.roads ?? []);
    },
    [apiBaseUrl, enabled],
  );

  useEffect(() => {
    const controller = new AbortController();
    reload(controller.signal).catch((err) => {
      if (!controller.signal.aborted) console.error("failed to load roads", err);
    });
    return () => controller.abort();
  }, [reload]);

  useEffect(() => {
    if (!enabled) return;
    const unlistenPromise = listen(ROADS_CHANGED_EVENT, () => {
      reload().catch((err) => console.error("failed to reload roads", err));
    });
    return () => {
      void unlistenPromise.then((unlisten) => unlisten());
    };
  }, [reload, enabled]);

  return { roads, reload };
}

type RoadSelectProps = {
  roads: RoadSummary[];
  value: RoadChoice;
  onChange: (choice: RoadChoice) => void;
  disabled?: boolean;
  /**
 * true: retain only a new road's name (show its input while value is new, calling onChange on each edit).
 * false: call onCreate when the user enters a name and presses Create (the caller performs creation).
 */
  deferred?: boolean;
  onCreate?: (name: string) => void;
};

export function RoadSelect({ roads, value, onChange, disabled, deferred = false, onCreate }: RoadSelectProps) {
  const { t, i18n } = useTranslation();
  const [creatingLocal, setCreatingLocal] = useState(false);
  const [input, setInput] = useState("");
  const creating = deferred ? value.kind === "new" : creatingLocal;
  const valueKey = value.kind === "road" ? `road:${value.roadId}` : value.kind;

  // Close the input after successful saving changes selection. Keep it open on failure to allow retries.
  useEffect(() => {
    setCreatingLocal(false);
  }, [valueKey]);

  const options = useMemo(() => {
    const collator = new Intl.Collator(resolveIntlLocale(i18n.language));
    const list = roads.map((road) => ({
      id: road.road_id,
      label: roadDisplayName(road.name, road.road_id, t),
      color: road.color,
    }));
    // Retain the current membership option even before road-list refetching catches up.
    if (value.kind === "road" && !list.some((option) => option.id === value.roadId)) {
      list.push({ id: value.roadId, label: roadDisplayName("", value.roadId, t), color: "" });
    }
    return list.sort((a, b) => collator.compare(a.label, b.label) || a.id - b.id);
  }, [roads, value, t, i18n.language]);

  const selectedColor = value.kind === "road" ? options.find((option) => option.id === value.roadId)?.color : undefined;

  if (creating) {
    const name = deferred && value.kind === "new" ? value.name : input;
    const setName = (next: string) => (deferred ? onChange({ kind: "new", name: next }) : setInput(next));
    const cancel = () => (deferred ? onChange({ kind: "none" }) : setCreatingLocal(false));
    return (
      <div className="tag-select">
        <input
          className="tag-select__input"
          type="text"
          value={name}
          autoFocus
          maxLength={ROAD_NAME_MAX_LENGTH}
          placeholder={t("path.road.newPlaceholder")}
          disabled={disabled}
          onChange={(e) => setName(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !deferred) onCreate?.(name.trim());
            if (e.key === "Escape") cancel();
          }}
        />
        {!deferred && (
          <button className="tag-select__btn" type="button" disabled={disabled} onClick={() => onCreate?.(name.trim())}>
            {t("path.road.create")}
          </button>
        )}
        <button className="tag-select__btn" type="button" disabled={disabled} onClick={cancel}>
          {t("path.road.cancel")}
        </button>
      </div>
    );
  }

  return (
    <div className="tag-select">
      {selectedColor && <span className="road-select__swatch" style={{ background: selectedColor }} aria-hidden="true" />}
      <select
        className="tag-select__select"
        value={value.kind === "road" ? String(value.roadId) : ""}
        disabled={disabled}
        onChange={(e) => {
          const next = e.target.value;
          if (next === NEW_ROAD_OPTION) {
            setInput("");
            if (deferred) onChange({ kind: "new", name: "" });
            else setCreatingLocal(true);
            return;
          }
          onChange(next === "" ? { kind: "none" } : { kind: "road", roadId: Number(next) });
        }}
      >
        <option value="">{t("path.road.none")}</option>
        {options.map((option) => (
          <option key={option.id} value={String(option.id)}>
            {option.label}
          </option>
        ))}
        <option value={NEW_ROAD_OPTION}>{t("path.road.createNew")}</option>
      </select>
    </div>
  );
}

type RoadMembershipRowProps = {
  apiBaseUrl: string;
  pathId: number;
  roadId: number | null | undefined;
  roads: RoadSummary[];
  /** Membership changed (roadId=null restores an unnamed path). The caller refetches display data. */
  onChanged: (roadId: number | null) => void;
};

/** Road membership row in path details. Show only for street/highway paths; the caller checks eligibility. */
export function RoadMembershipRow({ apiBaseUrl, pathId, roadId, roads, onChanged }: RoadMembershipRowProps) {
  const { t } = useTranslation();
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const openRoadDetail = useCallback((id: number, extra?: Record<string, unknown>) => {
    void invoke("open_registered_window", {
      windowType: "road_detail",
      entityId: String(id),
      ...(extra ? { params: { extra } } : {}),
    }).catch(console.error);
  }, []);

  const handleChange = useCallback(
    async (choice: RoadChoice) => {
      if (choice.kind === "new") return;
      const next = choice.kind === "road" ? choice.roadId : null;
      if (next === (hasRoad(roadId) ? roadId : null)) return;
      setSaving(true);
      setError(null);
      try {
        await setPathRoad(apiBaseUrl, pathId, next);
        // Reassignment changes both old and new roads, so notify without limiting to one road.
        await notifyRoadsChanged(undefined, "path_detail");
        onChanged(next);
      } catch (err) {
        console.error("failed to change road membership", err);
        setError(t("path.road.saveFailed"));
      } finally {
        setSaving(false);
      }
    },
    [apiBaseUrl, pathId, roadId, onChanged, t],
  );

  const handleCreate = useCallback(
    async (name: string) => {
      setSaving(true);
      setError(null);
      try {
        const { road } = await createRoad(apiBaseUrl, { name, path_ids: [pathId] });
        await notifyRoadsChanged(undefined, "path_detail");
        onChanged(road.road_id);
        // A one-section road is incomplete; propose straight-ahead extension in road details.
        openRoadDetail(road.road_id, { offerTrace: true, seedPathId: pathId });
      } catch (err) {
        console.error("failed to create road", err);
        setError(t("path.road.createFailed"));
      } finally {
        setSaving(false);
      }
    },
    [apiBaseUrl, pathId, onChanged, openRoadDetail, t],
  );

  return (
    <div className="tag-row road-membership-row">
      <span className="tag-row__label">{t("path.road.label")}</span>
      <RoadSelect
        roads={roads}
        value={roadChoiceOf(roadId)}
        onChange={(choice) => void handleChange(choice)}
        onCreate={(name) => void handleCreate(name)}
        disabled={saving}
      />
      {hasRoad(roadId) && (
        <button type="button" className="tag-select__btn" onClick={() => openRoadDetail(roadId)}>
          {t("path.road.openDetail")}
        </button>
      )}
      {error && <span className="tag-select__error road-membership-row__error">{error}</span>}
    </div>
  );
}
