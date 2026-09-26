/**
 * 道路(名前付き道路)のAPI呼び出しと、ウィンドウ横断の変更通知。
 *
 * 道路の作成・改名・所属変更・削除に成功したら必ず `notifyRoadsChanged` を呼ぶ。
 * 開いている他のウィンドウ(道路詳細・パス詳細・都市マップ)へ Tauri のグローバルイベントで
 * 取り直しを促し、Unity にも道路一覧の再取得(`world:refresh_roads`)を送る。
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

/** 道路が変わったことを知らせる Tauri のグローバルイベント名(ペイロードは { road_id?: number }) */
export const ROADS_CHANGED_EVENT = "roads:changed";

const JSON_HEADERS = { "Content-Type": "application/json" };

/** 区間の road_id が道路を指しているか。null・VALUE_NONE(-1)・0 は名もなき道 */
export function hasRoad(roadId: number | null | undefined): roadId is number {
  return roadId != null && roadId !== VALUE_NONE && roadId > 0;
}

/** 道路に所属できるパス種別(街路・高速道路)か */
export function isNamedRoadPathType(pathType: number | null | undefined): boolean {
  return pathType != null && PATH_TYPES_NAMED_ROAD.includes(pathType);
}

/** 道路の表示名。未命名なら「道路 #id」 */
export function roadDisplayName(name: string | null | undefined, roadId: number, t: TFunction): string {
  return name && name !== "" ? name : t("road.unnamed", { id: roadId });
}

export function fetchRoadList(apiBaseUrl: string, signal?: AbortSignal): Promise<RoadListResponse> {
  return fetchApi<RoadListResponse>(buildApiUrl(apiBaseUrl, "/api/v1/roads/"), { signal });
}

export function fetchRoadDetail(apiBaseUrl: string, roadId: number, signal?: AbortSignal): Promise<RoadDetail> {
  return fetchApi<RoadDetail>(buildApiUrl(apiBaseUrl, "/api/v1/roads/{id}", String(roadId)), { signal });
}

/** 道路を作成する。path_ids は1本以上必要(区間の無い道路は存在できない) */
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

/** 名前・色の変更と、起点と終点の入れ替え(reverse: true) */
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

/** 道路を削除する。区間は残り、名もなき道に戻る */
export function deleteRoad(apiBaseUrl: string, roadId: number): Promise<RoadDeleteResponse> {
  return fetchApi<RoadDeleteResponse>(buildApiUrl(apiBaseUrl, "/api/v1/roads/{id}", String(roadId)), {
    method: "DELETE",
  });
}

/**
 * 所属区間の一括追加・削除。別の道路に属していた区間を add すると付け替えになる。
 * 区間が0本になった道路は削除され、`{road: null, deleted: true}` が返る。
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
 * 直進方向の自動延長の候補(追加する区間のID)を取得する。
 * pathId を渡すとその区間から両方向へ、roadId だけを渡すと道路の端から外側へたどる。
 * 両方渡した場合は pathId から始め、roadId の既存の区間は候補に含めない。
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
 * 新しく敷く道路の端点が、既存の道路の端に接しているかを問い合わせる。
 * 端点の位置に端を持つ区間(腕)のうち、同じ道路に属するものがちょうど1本なら、その道路の延長として提案される。
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

/** 1区間の所属を変更する。roadId=null で道路から外す(名もなき道に戻す) */
export function setPathRoad(apiBaseUrl: string, pathId: number, roadId: number | null): Promise<unknown> {
  return fetchApi<unknown>(buildApiUrl(apiBaseUrl, "/api/v1/paths/{id}", String(pathId)), {
    method: "PATCH",
    headers: JSON_HEADERS,
    body: JSON.stringify({ road_id: roadId }),
  });
}

/** 道路の変更を他ウィンドウと Unity へ知らせる */
export async function notifyRoadsChanged(roadId?: number, windowType?: string): Promise<void> {
  await Promise.allSettled([
    emit(ROADS_CHANGED_EVENT, roadId != null ? { road_id: roadId } : {}),
    sendRefreshRoads(windowType),
  ]);
}
