/**
 * Shared construction project tags (one classification label per building/road). Tags live in backend
 * building/road `tag` properties and are saved. Tag/object visibility is never stored in the backend; Unity
 * holds session state (send the complete ID set through `sendSetObjectFilter`).
 *
 * Only public facilities and roads before/during construction accept new tags (`isTaggable`). Existing tags
 * survive completion/collapse so project tables keep showing completed counts. Shared by common/TagPicker,
 * common/TagSelect, project windows, building/road detail panels, and construction panels.
 */

import { STATUS_CONSTRUCTING, STATUS_PENDING, STATUS_SCHEDULED_CONSTRUCTION } from "../constants";
import { buildApiUrl, fetchApi } from "./api";

/**
 * Taggable statuses: construction on hold, planned, and under construction. Must match backend
 * `crud_tags.TAGGABLE_STATUSES`.
 */
export const TAGGABLE_STATUSES: readonly number[] = [
  STATUS_PENDING,
  STATUS_SCHEDULED_CONSTRUCTION,
  STATUS_CONSTRUCTING,
];

/**
 * Whether a building/road can receive a new tag (same rule as backend `crud_tags.is_taggable`). Buildings must
 * be city government facilities. Roads are always city assets; do not check publicFacility for them.
 */
export function isTaggable(kind: "agent" | "path", status: number, publicFacility?: boolean): boolean {
  if (!TAGGABLE_STATUSES.includes(status)) return false;
  return kind === "path" || !!publicFacility;
}

/** One tag candidate. Retain zero-count tags so attaching/detaching does not remove choices. */
export type TagSummary = {
  name: string;
  agent_count: number;
  path_count: number;
};

/** One construction project item (tagged building or road). */
export type TagProjectItem = {
  kind: "agent" | "path";
  id: number;
  name: string;
  variant: number;
  status: number;
  /** Estimated remaining construction cost (zero when completed). */
  remaining_cost: number;
  /** Estimated construction cost per epoch. */
  cost_per_epoch: number;
  /** Remaining epochs. null means it cannot finish as-is (such as zero construction speed). */
  remaining_epochs: number | null;
  completed: boolean;
  collapsed: boolean;
};

/** Construction project for one tag (items and aggregates). */
export type TagProject = {
  tag: string;
  items: TagProjectItem[];
  /** Total estimated construction cost (sum of remaining project costs for unfinished items). */
  total_remaining_cost: number;
  total_cost_per_epoch: number;
  /** Construction time (maximum remaining epochs among unfinished items). */
  max_remaining_epochs: number;
  in_progress_count: number;
  completed_count: number;
  /** Number of items with incalculable remaining epochs (cannot finish as-is). */
  stalled_count: number;
};

/** Fetch tag candidates. */
export async function fetchTags(
  apiBaseUrl: string,
  signal?: AbortSignal,
): Promise<TagSummary[]> {
  const res = await fetchApi<{ tags: TagSummary[] }>(
    buildApiUrl(apiBaseUrl, "/api/v1/tags/"),
    { signal },
  );
  return res.tags ?? [];
}

/** Fetch construction projects by tag (item lists and aggregates). */
export async function fetchTagProjects(
  apiBaseUrl: string,
  signal?: AbortSignal,
): Promise<TagProject[]> {
  const res = await fetchApi<{ projects: TagProject[] }>(
    buildApiUrl(apiBaseUrl, "/api/v1/tags/projects"),
    { signal },
  );
  return res.projects ?? [];
}

/**
 * Fetch one building/road's tag. `tag` is absent from generated endpoints.yaml schemas, so detail responses such
 * as GET /agents/{id} omit it.
 */
export async function fetchEntityTag(
  apiBaseUrl: string,
  kind: "agent" | "path",
  entityId: number,
  signal?: AbortSignal,
): Promise<string> {
  const res = await fetchApi<{ tag: string }>(
    buildApiUrl(apiBaseUrl, `/api/v1/tags/object?kind=${kind}&id=${entityId}`),
    { signal },
  );
  return res.tag ?? "";
}

/** Update one building/road's tag (empty string removes it). */
export async function saveEntityTag(
  apiBaseUrl: string,
  kind: "agent" | "path",
  entityId: number,
  tag: string,
): Promise<void> {
  const path = kind === "path" ? `/api/v1/paths/${entityId}` : `/api/v1/agents/${entityId}`;
  await fetchApi(buildApiUrl(apiBaseUrl, path), {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ tag }),
  });
}

/** Delete a tag candidate and remove it from all tagged buildings/roads. */
export async function deleteTag(apiBaseUrl: string, tag: string): Promise<void> {
  await fetchApi(
    buildApiUrl(apiBaseUrl, `/api/v1/tags/${encodeURIComponent(tag)}`),
    { method: "DELETE" },
  );
}

/** Global event for tag changes across windows (construction project tables follow it). */
export const TAGS_CHANGED_EVENT = "tags:changed";
