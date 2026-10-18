/**
 * 市民の追跡(訪問先と経路・追跡中の一覧・追従の状態)の共通処理
 *
 * 行程と追跡リストの応答は手書きのJSONで返され、endpoints.yaml に載らないため、型はここで定義する。
 * 追従そのものはUnityが行い、開始・終了は `citizen:follow_state` で全ウィンドウに届く。
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
  /** 自宅との往復の1日あたりの回数(通勤・通学は1、買物は品目ごとに1/エポック日数) */
  per_day: number;
  product_ids: number[];
  x: number | null;
  z: number | null;
};

export type CitizenTripLeg = {
  kind: "walk" | "drive" | "transit" | "conveyor" | "access" | "turn";
  /** 行程に含まれる道路・公共交通上の形状。ワールド座標の [x, z]。 */
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

/** status は ok / busy(エポック実行中) / not_ready(経路の計算前) / missing(市民がいない) / no_home(住宅待ち) */
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

/** 追跡中の一覧が変わったことを他ウィンドウへ知らせるグローバルイベント名(追跡リストと市民パネルが追従する)。 */
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
 * 追従中の市民IDを全ウィンドウで共有する保存先。ポップアップは後から開いても今の状態を知る必要があるので、
 * `citizen:follow_state` を受けたウィンドウが書き、開いたウィンドウが読む(同じオリジンのWebViewは localStorage を共有する)。
 * 市民パネルも追跡リストも閉じている間に追従が終わっても取りこぼさないよう、常駐するコックピットも書く。
 * アプリの起動時にはコックピットが消す(Unityも起動し直していて、追従していないため)。
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

/** Unity の追従の開始・終了の通知を保存先へ反映する。 */
export function recordFollowState({ citizen_id: id, following }: FollowStatePayload) {
  if (following) localStorage.setItem(FOLLOW_STORAGE_KEY, String(id));
  else if (readFollowed() === id) localStorage.removeItem(FOLLOW_STORAGE_KEY);
}

/** 常駐ウィンドウ(コックピット)で追従の通知を受け続け、保存先を最新に保つ。戻り値は購読の解除。 */
export function listenFollowState(): () => void {
  const unlisten = listen<FollowStatePayload>(IPC_EVENT_CITIZEN_FOLLOW_STATE, (event) => recordFollowState(event.payload));
  return () => {
    void unlisten.then((fn) => fn());
  };
}

/** 追従中の市民ID(追従していなければ null)。Unity の通知と、他のウィンドウが書いた保存先の変化に追従する。 */
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
