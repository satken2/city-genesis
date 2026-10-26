/**
 * Citizen list window
 *
 * Opened from the cockpit citizen list icon, with two tabs:
 * - Search: find citizens across the city by part of the name and an age range. The core returns only the first
 *   matches in citizen ID order, so say how many matched when the list is cut off.
 * - Tracking list: citizens added through Track in the citizen panel, from search results, or by random
 *   tracking, in insertion order and persisted in save data. The core omits deceased citizens and similar cases.
 *
 * Both tabs share one row: the name opens the citizen panel, and the buttons follow with the camera and add to or
 * remove from the tracking list.
 * Refetch on additions/removals in other windows (`tracking:changed`) and epoch execution, which changes ages
 * and workplaces. Search results are refetched with the last submitted conditions.
 */

import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";

import { CitizenLink } from "../common/CitizenLink";
import { sendCitizenFollow, sendCitizenUnfollow, sendFocusGraphic } from "../../utils/ipc";
import {
  TRACKED_CITIZENS_CHANGED_EVENT,
  fetchTrackedCitizens,
  searchCitizens,
  trackCitizen,
  trackRandomCitizen,
  untrackCitizen,
  useFollowedCitizen,
  type CitizenListRow,
  type CitizenSearchQuery,
  type CitizenSearchResult,
} from "../../utils/citizenTracking";
import { CameraIcon, RemoveIcon, TrackIcon } from "./CitizenTrackingIcons";
import "../agent/AgentDetailPanel.css";
import "../agent/AgentOperationPanel.css";
import "./CitizenListWindow.css";

/** Window type, also used as the sender label for IPC to Unity. */
const WINDOW_TYPE = "citizen_list";

type Tab = "search" | "tracking";

type CitizenListWindowProps = {
  apiBaseUrl: string;
};

/** Blank fields do not filter. */
function parseAge(value: string): number | null {
  return value.trim() === "" ? null : Math.max(0, Math.floor(Number(value)));
}

export function CitizenListWindow({ apiBaseUrl }: CitizenListWindowProps) {
  const { t } = useTranslation();
  const [tab, setTab] = useState<Tab>("search");
  const followed = useFollowedCitizen();

  const [tracked, setTracked] = useState<CitizenListRow[] | null>(null);
  const [trackedFailed, setTrackedFailed] = useState(false);
  const [adding, setAdding] = useState(false);
  const [addMessage, setAddMessage] = useState<string | null>(null);
  const trackedIds = useMemo(() => new Set(tracked?.map((citizen) => citizen.id)), [tracked]);

  const [name, setName] = useState("");
  const [ageMin, setAgeMin] = useState("");
  const [ageMax, setAgeMax] = useState("");
  const [searching, setSearching] = useState(false);
  const [result, setResult] = useState<CitizenSearchResult | null>(null);
  const [searchFailed, setSearchFailed] = useState(false);
  const lastQuery = useRef<CitizenSearchQuery | null>(null);

  const reloadTracked = useCallback(async (signal?: AbortSignal) => {
    try {
      const list = await fetchTrackedCitizens(apiBaseUrl, signal);
      if (signal?.aborted) return;
      setTracked(list);
      setTrackedFailed(false);
    } catch (err) {
      if (!signal?.aborted) {
        console.error("failed to load tracked citizens", err);
        setTrackedFailed(true);
      }
    }
  }, [apiBaseUrl]);

  const runSearch = useCallback(async (query: CitizenSearchQuery) => {
    lastQuery.current = query;
    setSearching(true);
    try {
      const found = await searchCitizens(apiBaseUrl, query);
      if (lastQuery.current !== query) return;
      setResult(found);
      setSearchFailed(false);
    } catch (err) {
      console.error("failed to search citizens", err);
      if (lastQuery.current === query) setSearchFailed(true);
    } finally {
      if (lastQuery.current === query) setSearching(false);
    }
  }, [apiBaseUrl]);

  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();
    void reloadTracked(controller.signal);
    return () => controller.abort();
  }, [apiBaseUrl, reloadTracked]);

  useEffect(() => {
    const unlistenTracked = listen(TRACKED_CITIZENS_CHANGED_EVENT, () => void reloadTracked());
    const unlistenEpoch = listen<{ executing: boolean }>("epoch:executing", (event) => {
      if (event.payload.executing) return;
      void reloadTracked();
      if (lastQuery.current) void runSearch(lastQuery.current);
    });
    return () => {
      void unlistenTracked.then((fn) => fn());
      void unlistenEpoch.then((fn) => fn());
    };
  }, [reloadTracked, runSearch]);

  function submitSearch(event: FormEvent) {
    event.preventDefault();
    void runSearch({ name: name.trim(), age_min: parseAge(ageMin), age_max: parseAge(ageMax) });
  }

  async function toggleFollow(citizenId: number) {
    try {
      if (followed === citizenId) {
        await sendCitizenUnfollow(WINDOW_TYPE, citizenId);
      } else {
        await sendCitizenFollow(WINDOW_TYPE, citizenId);
        await sendFocusGraphic();
      }
    } catch (err) {
      console.error("failed to toggle citizen follow", err);
    }
  }

  async function toggleTracked(citizenId: number) {
    try {
      if (trackedIds.has(citizenId)) {
        await untrackCitizen(apiBaseUrl, citizenId);
      } else {
        await trackCitizen(apiBaseUrl, citizenId);
      }
      await emit(TRACKED_CITIZENS_CHANGED_EVENT, { citizen_id: citizenId });
      await reloadTracked();
    } catch (err) {
      console.error("failed to toggle citizen tracking", err);
    }
  }

  async function addRandom() {
    setAdding(true);
    setAddMessage(null);
    try {
      const added = await trackRandomCitizen(apiBaseUrl);
      setTracked(added.citizens);
      setTrackedFailed(false);
      if (added.citizen_id === null) {
        setAddMessage("citizenTracking.noUntrackedCitizens");
      } else {
        await emit(TRACKED_CITIZENS_CHANGED_EVENT, { citizen_id: added.citizen_id });
      }
    } catch (err) {
      console.error("failed to track random citizen", err);
      setAddMessage("citizenTracking.trackRandomError");
    } finally {
      setAdding(false);
    }
  }

  function renderRow(citizen: CitizenListRow, trackButton: ReactNode) {
    const workplaceId = citizen.employer_agent_id ?? citizen.school_agent_id;
    const workplaceName = citizen.employer_agent_name ?? citizen.school_agent_name;
    const following = followed === citizen.id;
    return (
      <li key={citizen.id} className={`citizen-list-row${following ? " citizen-list-row--following" : ""}`}>
        <span className="citizen-list-cell">
          <CitizenLink citizenId={citizen.id} citizenName={citizen.name} />
        </span>
        <span className="citizen-list-cell citizen-list-meta">{t("citizen.ageValue", { age: citizen.age })}</span>
        <span className="citizen-list-cell citizen-list-meta">{citizen.zone_name ?? ""}</span>
        <span className="citizen-list-cell citizen-list-meta">
          {workplaceId && workplaceName ? (
            <button
              className="entity-link"
              type="button"
              onClick={() => void invoke("open_registered_window", { windowType: "agent_detail", entityId: String(workplaceId) })}
            >
              {workplaceName}
            </button>
          ) : (
            t("citizen.unemployed")
          )}
        </span>
        <div className="operation-panel">
          <button
            className={`operation-btn${following ? " operation-btn--active" : ""}`}
            type="button"
            title={t(following ? "citizenTracking.unfollow" : "citizenTracking.follow")}
            aria-pressed={following}
            onClick={() => void toggleFollow(citizen.id)}
          >
            <CameraIcon />
          </button>
          {trackButton}
        </div>
      </li>
    );
  }

  function renderSearch() {
    return (
      <>
        <form className="citizen-list-search" onSubmit={submitSearch}>
          <label className="citizen-list-field">
            <span>{t("citizenList.name")}</span>
            <input
              className="citizen-list-name-input"
              type="text"
              value={name}
              maxLength={100}
              onChange={(event) => setName(event.target.value)}
            />
          </label>
          <label className="citizen-list-field">
            <span>{t("citizenList.age")}</span>
            <input
              className="citizen-list-age-input"
              type="number"
              min={0}
              value={ageMin}
              onChange={(event) => setAgeMin(event.target.value)}
            />
            <span>{t("citizenList.ageSeparator")}</span>
            <input
              className="citizen-list-age-input"
              type="number"
              min={0}
              value={ageMax}
              onChange={(event) => setAgeMax(event.target.value)}
            />
          </label>
          <button type="submit" disabled={searching || !apiBaseUrl}>{t("citizenList.search")}</button>
        </form>
        {searchFailed ? (
          <p className="agent-detail-muted">{t("common.fetchError")}</p>
        ) : result && result.citizens.length === 0 ? (
          <p className="agent-detail-muted">{t("citizenList.noResults")}</p>
        ) : result && (
          <>
            {result.total > result.citizens.length && (
              <p className="agent-detail-muted">
                {t("citizenList.truncated", { total: result.total, shown: result.citizens.length })}
              </p>
            )}
            <ul className="citizen-list">
              {result.citizens.map((citizen) => {
                const isTracked = trackedIds.has(citizen.id);
                return renderRow(citizen, (
                  <button
                    className={`operation-btn${isTracked ? " operation-btn--active" : ""}`}
                    type="button"
                    title={t(isTracked ? "citizenTracking.untrack" : "citizenTracking.track")}
                    aria-pressed={isTracked}
                    onClick={() => void toggleTracked(citizen.id)}
                  >
                    <TrackIcon />
                  </button>
                ));
              })}
            </ul>
          </>
        )}
      </>
    );
  }

  function renderTracking() {
    return (
      <>
        <div className="citizen-list-toolbar">
          <button
            type="button"
            data-guide="track-random-citizen"
            disabled={adding || !apiBaseUrl}
            onClick={() => void addRandom()}
          >
            {t("citizenTracking.trackRandom")}
          </button>
        </div>
        {addMessage && <p className="agent-detail-muted" role="status">{t(addMessage)}</p>}
        {trackedFailed ? (
          <p className="agent-detail-muted">{t("common.fetchError")}</p>
        ) : !tracked ? (
          <p className="agent-detail-muted">{t("common.loading")}</p>
        ) : tracked.length === 0 ? (
          <p className="agent-detail-muted">{t("citizenTracking.empty")}</p>
        ) : (
          <ul className="citizen-list">
            {tracked.map((citizen) => renderRow(citizen, (
              <button
                className="operation-btn"
                type="button"
                title={t("citizenTracking.untrack")}
                onClick={() => void toggleTracked(citizen.id)}
              >
                <RemoveIcon />
              </button>
            )))}
          </ul>
        )}
      </>
    );
  }

  return (
    <div className="citizen-list-window">
      <div className="agent-detail-tabs">
        {(["search", "tracking"] as const).map((id) => (
          <button
            key={id}
            className={`agent-detail-tab${id === tab ? " agent-detail-tab--active" : ""}`}
            type="button"
            onClick={() => setTab(id)}
          >
            {t(id === "search" ? "citizenList.tabSearch" : "citizenList.tabTracking")}
          </button>
        ))}
      </div>
      {tab === "search" ? renderSearch() : renderTracking()}
    </div>
  );
}
