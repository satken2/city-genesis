import { fetchApi } from "../utils/api";

/** Pass only learning targets to list-based checks in existing lessons. Do not include shared living facilities in completion conditions. */
export async function fetchLessonApi<T>(url: string, options?: Parameters<typeof fetchApi>[1]): Promise<T> {
  const result = await fetchApi<T>(url, options);
  const match = url.match(/^(.*\/api\/v1)\/(paths|parcels|buildings|agents|zones|resources)\/?$/);
  if (!match || (options?.method && options.method !== "GET")) return result;
  const { tutorial } = await fetchApi<{ tutorial: { excluded: Record<string, number[]> } }>(`${match[1]}/tutorials`);
  const data = result as Record<string, unknown>;
  const key = match[2], rows = data[key];
  if (!Array.isArray(rows)) return result;
  const idField = { paths: "path_id", parcels: "parcel_id", buildings: "agent_id", agents: "agent_id", zones: "zone_id", resources: "resource_id" }[key]!;
  const excluded = new Set(tutorial.excluded[key] ?? []);
  return { ...data, [key]: rows.filter(row => !excluded.has(row[idField])) } as T;
}
