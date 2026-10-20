/**
 * API calls and cross-window change notifications for named roads.
 *
 * Always call notifyRoadsChanged after successful road creation, renaming, membership changes, or deletion.
 * Use a global Tauri event to prompt other open windows (road/path details and city map) to refetch,
 * and send world:refresh_roads to Unity to refresh its road list.
 */

import { emit } from "@tauri-apps/api/event";
import type { TFunction } from "i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { sendRefreshRoads } from "../../utils/ipc";
import { PATH_TYPES_NAMED_ROAD, VALUE_NONE } from "../../constants";
import type {
  RoadDeleteResponse,
  RoadDetail,
  RoadExtensionCandidatesResponse,
  RoadListResponse,
  RoadMembersResponse,
  RoadMutationResponse,
  RoadTraceResponse,
} from "./roadTypes";

/** Global Tauri event name for road changes (payload: { road_id?: number }). */
export const ROADS_CHANGED_EVENT = "roads:changed";

const JSON_HEADERS = { "Content-Type": "application/json" };

/** Whether a section's road_id refers to a road. null, VALUE_NONE (-1), and 0 mean an unnamed path. */
export function hasRoad(roadId: number | null | undefined): roadId is number {
  return roadId != null && roadId !== VALUE_NONE && roadId > 0;
}

/** Whether the path type can belong to a road (street/highway). */
export function isNamedRoadPathType(pathType: number | null | undefined): boolean {
  return pathType != null && PATH_TYPES_NAMED_ROAD.includes(pathType);
}

/** Road display name. Use "Road #id" if unnamed. */
export function roadDisplayName(name: string | null | undefined, roadId: number, t: TFunction): string {
  return name && name !== "" ? name : t("road.unnamed", { id: roadId });
}

export function fetchRoadList(apiBaseUrl: string, signal?: AbortSignal): Promise<RoadListResponse> {
  return fetchApi<RoadListResponse>(buildApiUrl(apiBaseUrl, "/api/v1/roads/"), { signal });
}

export function fetchRoadDetail(apiBaseUrl: string, roadId: number, signal?: AbortSignal): Promise<RoadDetail> {
  return fetchApi<RoadDetail>(buildApiUrl(apiBaseUrl, "/api/v1/roads/{id}", String(roadId)), { signal });
}

/** Create a road. Requires at least one path_id because a road cannot exist without sections. */
export function createRoad(
  apiBaseUrl: string,
  body: { name: string; color?: string; path_ids: number[] },
): Promise<RoadMutationResponse> {
  return fetchApi<RoadMutationResponse>(buildApiUrl(apiBaseUrl, "/api/v1/roads/"), {
    method: "POST",
    headers: JSON_HEADERS,
    body: JSON.stringify(body),
  });
}

/** Change name/color or swap origin and endpoint (reverse: true). */
export function updateRoad(
  apiBaseUrl: string,
  roadId: number,
  body: { name?: string; color?: string; reverse?: boolean },
): Promise<RoadMutationResponse> {
  return fetchApi<RoadMutationResponse>(buildApiUrl(apiBaseUrl, "/api/v1/roads/{id}", String(roadId)), {
    method: "PATCH",
    headers: JSON_HEADERS,
    body: JSON.stringify(body),
  });
}

/** Delete a road, keeping its sections as unnamed paths. */
export function deleteRoad(apiBaseUrl: string, roadId: number): Promise<RoadDeleteResponse> {
  return fetchApi<RoadDeleteResponse>(buildApiUrl(apiBaseUrl, "/api/v1/roads/{id}", String(roadId)), {
    method: "DELETE",
  });
}

/**
 * Bulk add/remove member sections. Adding a section from another road reassigns it.
 * A road with no remaining sections is deleted, returning {road: null, deleted: true}.
 */
export function updateRoadMembers(
  apiBaseUrl: string,
  roadId: number,
  body: { add?: number[]; remove?: number[] },
): Promise<RoadMembersResponse> {
  return fetchApi<RoadMembersResponse>(buildApiUrl(apiBaseUrl, `/api/v1/roads/${roadId}/members`), {
    method: "POST",
    headers: JSON_HEADERS,
    body: JSON.stringify(body),
  });
}

/**
 * Get candidate section IDs for automatic straight-ahead extension.
 * With pathId, trace in both directions from that section; with roadId alone, trace outward from the road's ends.
 * With both, start from pathId and exclude existing roadId sections from candidates.
 */
export function traceRoad(
  apiBaseUrl: string,
  params: { pathId?: number; roadId?: number },
): Promise<RoadTraceResponse> {
  const query = new URLSearchParams();
  if (params.pathId != null) query.set("path_id", String(params.pathId));
  if (params.roadId != null) query.set("road_id", String(params.roadId));
  return fetchApi<RoadTraceResponse>(buildApiUrl(apiBaseUrl, `/api/v1/roads/trace?${query.toString()}`));
}

/**
 * Candidate bounds [minx, minz, maxx, maxz] for pathIds from trace. The overview loads sections by bounds,
 * so use this to include candidates in its display. Prefer response bbox; otherwise fetch candidates individually
 * and approximate from endpoint bounds (overview padding absorbs curve bulges).
 */
export async function resolveTraceBbox(
  apiBaseUrl: string,
  res: RoadTraceResponse,
  pathIds: number[],
): Promise<[number, number, number, number] | null> {
  if (res.bbox !== undefined) return res.bbox;
  if (pathIds.length === 0) return null;
  const paths = await Promise.all(
    pathIds.map((id) =>
      fetchApi<{ start_point?: number[]; end_point?: number[] }>(buildApiUrl(apiBaseUrl, "/api/v1/paths/{id}", String(id)))
        .catch(() => null),
    ),
  );
  let box: [number, number, number, number] | null = null;
  for (const p of paths) {
    for (const pt of [p?.start_point, p?.end_point]) {
      if (!pt || pt.length < 2) continue;
      // Endpoints are [x, y, z] or [x, z].
      const x = pt[0];
      const z = pt.length >= 3 ? pt[2] : pt[1];
      box = box
        ? [Math.min(box[0], x), Math.min(box[1], z), Math.max(box[2], x), Math.max(box[3], z)]
        : [x, z, x, z];
    }
  }
  return box;
}

/**
 * Query whether a newly placed road's endpoints touch an existing road's ends.
 * If exactly one arm ending there belongs to a given road, propose extending that road.
 */
export function fetchRoadExtensionCandidates(
  apiBaseUrl: string,
  points: number[][],
  signal?: AbortSignal,
): Promise<RoadExtensionCandidatesResponse> {
  return fetchApi<RoadExtensionCandidatesResponse>(buildApiUrl(apiBaseUrl, "/api/v1/roads/extension-candidates"), {
    method: "POST",
    headers: JSON_HEADERS,
    body: JSON.stringify({ points }),
    signal,
  });
}

/** Change one section's membership. roadId=null removes it from its road, restoring an unnamed path. */
export function setPathRoad(apiBaseUrl: string, pathId: number, roadId: number | null): Promise<unknown> {
  return fetchApi<unknown>(buildApiUrl(apiBaseUrl, "/api/v1/paths/{id}", String(pathId)), {
    method: "PATCH",
    headers: JSON_HEADERS,
    body: JSON.stringify({ road_id: roadId }),
  });
}

/** Notify other windows and Unity of road changes. */
export async function notifyRoadsChanged(roadId?: number, windowType?: string): Promise<void> {
  await Promise.allSettled([
    emit(ROADS_CHANGED_EVENT, roadId != null ? { road_id: roadId } : {}),
    sendRefreshRoads(windowType),
  ]);
}
