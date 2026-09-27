import type { TFunction } from "i18next";
import { ApiError, buildApiUrl, fetchApi } from "./api";
import { requestConfirm } from "./confirmDialog";

export type SaveCompatibility = {
  loadable: boolean;
  code?: string;
  balance_changed?: boolean;
  balance_core_version?: string;
  core_version?: string;
};
export type SaveEntry = {
  id: string; city_name: string; population: number; current_date: number;
  epoch: number; simulation_days: number; saved_at: string; is_scenario: boolean; language: string;
  saved_core_version?: string; created_core_version?: string; last_simulated_core_version?: string | null;
  compatibility: SaveCompatibility;
  origin?: "builtin" | "downloaded" | "local" | "save";
  scenario_title?: string; scenario_description?: string; scenario_objective?: string;
  terrain_map_id?: string; terrain_map_name?: string; author?: string;
  category?: "sandbox" | "challenge" | "tutorial"; content_revision?: number;
};

export function saveError(error: unknown, t: TFunction): string {
  if (error instanceof ApiError) return t(`saveCompatibility.${error.code}`, { defaultValue: error.message });
  const message = error instanceof Error ? error.message : String(error);
  const code = message.match(/^"?([A-Z_]+)"?(?::|$)/)?.[1];
  return code ? t(`saveCompatibility.${code}`, { defaultValue: message }) : message;
}

/** 都市やウィンドウを切り替える前に検査・確認を済ませる。コアもロード直前に再検査する。 */
export async function prepareSaveLoad(apiBaseUrl: string, id: string, template: boolean, t: TFunction): Promise<boolean> {
  const result = await fetchApi<{ compatibility: SaveCompatibility }>(
    buildApiUrl(apiBaseUrl, `/api/v1/saves/${encodeURIComponent(id)}/validation?template=${template}`),
    { cache: "no-store" },
  );
  if (!result.compatibility.balance_changed) return true;
  return requestConfirm({ message: t("saveCompatibility.balanceConfirm", {
    from: result.compatibility.balance_core_version, to: result.compatibility.core_version,
  }) });
}
