import { fetchApi } from "../../utils/api";

/** 造り替えの受理後、開いているパス詳細へ再取得を知らせる。 */
export const PATH_REBUILD_CHANGED_EVENT = "path:rebuild_changed";

export type PathRebuildEstimate = {
  path_id: number;
  current_variant: number;
  rebuild_variant: number;
  estimated_construction_cost: number;
  estimated_duration_days: number;
  cost_per_simulation_days: number;
  affordable: boolean;
  required_total: number;
  available_funds: number | null;
  removable_parcel_ids: number[];
  shavable_parcel_ids: number[];
};

export function requestPathRebuild(apiBaseUrl: string, pathId: number, variant: number, dryRun: boolean, signal?: AbortSignal) {
  return fetchApi<PathRebuildEstimate>(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/${pathId}/rebuild`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ path_variant: variant, dry_run: dryRun }),
    signal,
  });
}
