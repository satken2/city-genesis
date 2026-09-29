import type { TFunction } from "i18next";
import { ApiError, buildApiUrl, fetchApi } from "./api";
import { requestConfirm } from "./confirmDialog";
import { resolveLocalizedText, type LocalizedText } from "./locale";

export type SaveCompatibility = {
  loadable: boolean;
  code?: string;
  balance_changed?: boolean;
  balance_core_version?: string;
  core_version?: string;
};
/** シナリオの保存元。workshop・dlc は読み取り専用で、編集は「コピーして編集」で自作として保存する。 */
export type ScenarioOrigin = "local" | "workshop" | "dlc";
/** 必要パックの所有状況。unverified は本番でSteam検証もそのキャッシュもまだ無い状態。 */
export type PackStatus = "owned" | "not_owned" | "unverified";
export type SaveEntry = {
  id: string; city_name: string; population: number; current_date: number;
  epoch: number; simulation_days: number; saved_at: string; is_scenario: boolean; language: string;
  saved_core_version?: string; created_core_version?: string; last_simulated_core_version?: string | null;
  compatibility: SaveCompatibility;
  /** 通常セーブの一覧では "save" */
  origin?: ScenarioOrigin | "save";
  read_only?: boolean;
  /** DLC由来の都市はパック鍵で暗号化して保存される */
  encrypted?: boolean;
  /** 出自として都市に刻まれた必要パック(昇順)。通常セーブにも付く */
  required_packs?: string[];
  pack_status?: Record<string, PackStatus>;
  /** 公式署名が有効か */
  official?: boolean;
  /** 紹介文と初期の都市名。作者が書く多言語テキストで、表示は `resolveLocalizedText` で言語を選ぶ */
  scenario_title?: LocalizedText; scenario_description?: LocalizedText; scenario_objective?: LocalizedText;
  scenario_city_name?: LocalizedText;
  /** 作者が固定した開始設定の項目。固定しない項目はゲーム開始時にプレイヤーが選べる */
  scenario_fixed_options?: ScenarioStartOption[];
  terrain_map_id?: string; terrain_map_name?: string; author?: string;
  category?: "sandbox" | "challenge" | "tutorial"; content_revision?: number;
  /** 都市が持つ地表・気候(`GROUND_COVER_*` / `CLIMATE_*`)。地形エディタ・紹介情報で変わるので、オリジナル地形の登録表の値とは異なりうる */
  ground_cover?: number; climate?: number;
  /** 建物・道路アセットの地域(`ASSET_REGION_*`) */
  asset_region?: number;
};

/** 作者が「固定」にできる開始設定の項目(コアの `SCENARIO_START_OPTIONS`)。言語は常にプレイヤーが選べるので含まない */
export type ScenarioStartOption = "simulation_days" | "climate" | "ground_cover" | "asset_region";

/** シナリオの見出し。タイトル(表示言語 → 英語)、無ければ都市名、それも無ければIDの先頭 */
export function scenarioDisplayTitle(entry: SaveEntry, language: string | undefined): string {
  return resolveLocalizedText(entry.scenario_title, language) || entry.city_name || entry.id.slice(0, 8);
}

/** エラーの理由コード。ApiError はその code、Rust コマンドの失敗は「CODE: 本文」の先頭(版照合の `"CODE": 本文` を含む)。無ければ null */
export function errorCode(error: unknown): string | null {
  if (error instanceof ApiError) return error.code;
  const message = error instanceof Error ? error.message : String(error);
  return message.match(/^"?([A-Z_]+)"?(?::|$)/)?.[1] ?? null;
}

export function saveError(error: unknown, t: TFunction): string {
  const message = error instanceof Error ? error.message : String(error);
  const code = errorCode(error);
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
