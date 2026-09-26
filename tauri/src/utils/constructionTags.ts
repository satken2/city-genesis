/**
 * 建設プロジェクトのタグ(建物・道路に付ける単一の分類ラベル)の共通処理
 *
 * タグの実体はバックエンド(建物・道路のプロパティ `tag`)にあり、セーブに含まれる。
 * 一方で「タグ単位／オブジェクト単位の表示・非表示」はバックエンドに一切保存されず、
 * Unityがセッション内の状態として持つ(`sendSetObjectFilter` でIDの全集合を送る)。
 *
 * 新たにタグを付けられるのは着工前・建設中の公共施設と道路だけ(`isTaggable`)。
 * 付いたタグは竣工・倒壊しても外れない(プロジェクトの表で竣工済みの件数を見せ続けるため)。
 *
 * タグ選択UI(`common/TagPicker`・`common/TagSelect`)・建設プロジェクトウィンドウ・
 * 建物/道路の詳細パネル・建設パネルが共用する。
 */

import { STATUS_CONSTRUCTING, STATUS_PENDING, STATUS_SCHEDULED_CONSTRUCTION } from "../constants";
import { buildApiUrl, fetchApi } from "./api";

/**
 * タグを付けられるステータス(着工保留中・計画中・建設中)。
 * バックエンド `crud_tags.TAGGABLE_STATUSES` の写しで、一致させる契約。
 */
export const TAGGABLE_STATUSES: readonly number[] = [
  STATUS_PENDING,
  STATUS_SCHEDULED_CONSTRUCTION,
  STATUS_CONSTRUCTING,
];

/**
 * 建物・道路に新たにタグを付けられる状態か(バックエンド `crud_tags.is_taggable` と同じ規則)。
 * 建物は市政府の施設に限る。道路は常に市の資産なので `publicFacility` を見ない。
 */
export function isTaggable(kind: "agent" | "path", status: number, publicFacility?: boolean): boolean {
  if (!TAGGABLE_STATUSES.includes(status)) return false;
  return kind === "path" || !!publicFacility;
}

/** タグ候補1件。件数0のタグも候補として残る(付け外ししても選択肢が消えないように)。 */
export type TagSummary = {
  name: string;
  agent_count: number;
  path_count: number;
};

/** 建設プロジェクトの構成アイテム1件(タグの付いた建物または道路)。 */
export type TagProjectItem = {
  kind: "agent" | "path";
  id: number;
  name: string;
  variant: number;
  status: number;
  /** 残りの建設費見込み(竣工済みは0) */
  remaining_cost: number;
  /** 1エポックあたりの建設費見込み */
  cost_per_epoch: number;
  /** 残りエポック数。null=このままでは終わらない(建設スピード0など) */
  remaining_epochs: number | null;
  completed: boolean;
  collapsed: boolean;
};

/** タグ1件ぶんの建設プロジェクト(構成アイテム+集計)。 */
export type TagProject = {
  tag: string;
  items: TagProjectItem[];
  /** 建設費の見込み額合計(未完了アイテムの残り事業費の合計) */
  total_remaining_cost: number;
  total_cost_per_epoch: number;
  /** 工期(未完了アイテムの残りエポック数の最大値) */
  max_remaining_epochs: number;
  in_progress_count: number;
  completed_count: number;
  /** 残りエポックが算出できない(=このままでは終わらない)アイテムの件数 */
  stalled_count: number;
};

/** タグ候補の一覧を取得する。 */
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

/** タグ別の建設プロジェクト(アイテム一覧+集計)を取得する。 */
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
 * 建物・道路1件に付いているタグを取得する。
 * `tag` は自動生成スキーマ(endpoints.yaml)に載せていないため、
 * 詳細取得(GET /agents/{id} 等)のレスポンスには含まれない。
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

/** 建物・道路1件のタグを更新する(空文字でタグを外す)。 */
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

/** タグを候補から削除し、付いている建物・道路からも外す。 */
export async function deleteTag(apiBaseUrl: string, tag: string): Promise<void> {
  await fetchApi(
    buildApiUrl(apiBaseUrl, `/api/v1/tags/${encodeURIComponent(tag)}`),
    { method: "DELETE" },
  );
}

/** タグの変更を他ウィンドウへ知らせるグローバルイベント名(建設プロジェクトの表が追従する)。 */
export const TAGS_CHANGED_EVENT = "tags:changed";
