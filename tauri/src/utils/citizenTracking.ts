/**
 * Shared citizen tracking logic: destinations/routes, tracked list, and following state. Itinerary/list
 * responses are handwritten JSON outside endpoints.yaml, so define types here. Unity performs following and
 * broadcasts start/end through `citizen:follow_state` to all windows.
 */

import { useEffect, useState } from "react";
import { listen } from "@tauri-apps/api/event";

import { buildApiUrl, fetchApi } from "./api";
import { IPC_EVENT_CITIZEN_FOLLOW_STATE } from "./ipc";

export type CitizenVisitKind = "home" | "work" | "school" | "shop";

export type CitizenVisit = {
  kind: CitizenVisitKind;
  agent_id: number;
  name: string;
  parcel_id: number;
  /** Daily round trips from home (commute/school: 1; shopping: 1/epoch days per item). */
  per_day: number;
  product_ids: number[];
  x: number | null;
  z: number | null;
};

export type CitizenTripLeg = {
  kind: "walk" | "drive" | "transit" | "conveyor" | "access" | "turn";
  /** Road/public transport geometry in the itinerary. World [x, z] coordinates. */
  points: number[][];
  minutes: number;
  distance_m: number;
  name: string;
  color: string | null;
  line_id: number | null;
};

export type CitizenTrip = {
  status: string;
  from_visit: number;
  to_visit: number;
  minutes?: number;
  distance_m?: number;
  fare?: number;
  legs?: CitizenTripLeg[];
};

/** status: ok / busy (epoch executing) / not_ready (routes not calculated) / missing (citizen absent) / no_home (awaiting housing). */
export type CitizenItinerary = {
  status: string;
  mode?: "automobile" | "pedestrian";
  visits?: CitizenVisit[];
  trips?: CitizenTrip[];
};

export type TrackedCitizen = {
  id: number;
  name: string;
  age: number;
  gender: number;
  household_agent_id: number;
  zone_name: string | null;
  employer_agent_id: number | null;
  employer_agent_name: string | null;
  school_agent_id: number | null;
  school_agent_name: string | null;
};

/** Global event for tracked-list changes across windows (tracking list and citizen panels follow it). */
export const TRACKED_CITIZENS_CHANGED_EVENT = "tracking:changed";

export async function fetchItinerary(apiBaseUrl: string, citizenId: number, signal?: AbortSignal): Promise<CitizenItinerary> {
  return fetchApi<CitizenItinerary>(buildApiUrl(apiBaseUrl, `/api/v1/citizens/${citizenId}/itinerary`), { signal });
}

export async function fetchTrackedCitizens(apiBaseUrl: string, signal?: AbortSignal): Promise<TrackedCitizen[]> {
  const response = await fetchApi<{ citizens: TrackedCitizen[] }>(buildApiUrl(apiBaseUrl, "/api/v1/citizens/tracked"), { signal });
  return response.citizens;
}

export async function trackCitizen(apiBaseUrl: string, citizenId: number): Promise<void> {
  await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/citizens/tracked/${citizenId}`), { method: "PUT" });
}

export async function trackRandomCitizen(apiBaseUrl: string): Promise<{ citizen_id: number | null; citizens: TrackedCitizen[] }> {
  return fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/citizens/tracked/random"), { method: "POST" });
}

export async function untrackCitizen(apiBaseUrl: string, citizenId: number): Promise<void> {
  await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/citizens/tracked/${citizenId}`), { method: "DELETE" });
}

/**
 * Storage shared across windows for the followed citizen ID. Popups must know current state even when opened
 * later: windows receiving `citizen:follow_state` write it, and newly opened windows read it (same-origin
 * WebViews share localStorage). The persistent cockpit also writes so endings are retained while both citizen
 * panel and tracking list are closed. The cockpit clears it on app startup because Unity also restarts without
 * following anyone.
 */
const FOLLOW_STORAGE_KEY = "citizenTracking.followed";

type FollowStatePayload = { citizen_id: number; following: boolean };

function readFollowed(): number | null {
  const value = Number(localStorage.getItem(FOLLOW_STORAGE_KEY));
  return Number.isFinite(value) && value > 0 ? value : null;
}

export function clearFollowedCitizen() {
  localStorage.removeItem(FOLLOW_STORAGE_KEY);
}

/** Apply Unity following start/end notifications to shared storage. */
export function recordFollowState({ citizen_id: id, following }: FollowStatePayload) {
  if (following) localStorage.setItem(FOLLOW_STORAGE_KEY, String(id));
  else if (readFollowed() === id) localStorage.removeItem(FOLLOW_STORAGE_KEY);
}

/** Keep shared storage current by subscribing in the persistent cockpit window. Return the unsubscribe function. */
export function listenFollowState(): () => void {
  const unlisten = listen<FollowStatePayload>(IPC_EVENT_CITIZEN_FOLLOW_STATE, (event) => recordFollowState(event.payload));
  return () => {
    void unlisten.then((fn) => fn());
  };
}

/** Followed citizen ID (null if none). Follow Unity notifications and shared storage changes written by other windows. */
export function useFollowedCitizen(): number | null {
  const [followed, setFollowed] = useState<number | null>(readFollowed);
  useEffect(() => {
    const unlisten = listen<FollowStatePayload>(IPC_EVENT_CITIZEN_FOLLOW_STATE, (event) => {
      recordFollowState(event.payload);
      setFollowed(readFollowed());
    });
    const onStorage = (event: StorageEvent) => {
      if (event.key === FOLLOW_STORAGE_KEY || event.key === null) setFollowed(readFollowed());
    };
    window.addEventListener("storage", onStorage);
    return () => {
      void unlisten.then((fn) => fn());
      window.removeEventListener("storage", onStorage);
    };
  }, []);
  return followed;
}
