import type { TFunction } from "i18next";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { buildApiUrl, fetchApi } from "./api";
import { describeError, errorInfo } from "./errors";
import { requestConfirm } from "./confirmDialog";
import { resolveLocalizedText, type LocalizedText } from "./locale";

export type SaveCompatibility = {
  loadable: boolean;
  code?: string;
  balance_changed?: boolean;
  balance_core_version?: string;
  core_version?: string;
};
/** Scenario storage source. Only local is writable; others are playable read-only. bundled is shipped with the game and listed first. debug denotes development cities returned only when the core starts in debug mode. */
export type ScenarioOrigin = "local" | "bundled" | "workshop" | "dlc" | "debug";
/** Tag given only by the bundled catalog. terrain marks a bare terrain listed under Select Terrain in New Game. */
export type ScenarioTag = "standard" | "terrain";
/** Required pack ownership state. unverified means production has neither Steam verification nor its cache yet. */
export type PackStatus = "owned" | "not_owned" | "unverified";
/** Common list information across save types. The API derives compatibility/ownership from the runtime environment. */
export type CommonSaveEntry = {
  id: string; simulation_days: number; saved_at: string; language: string;
  /** Also show the source scenario title in normal save lists. */
  scenario_title?: LocalizedText;
  has_thumbnail?: boolean;
  saved_core_version?: string; created_core_version?: string; last_simulated_core_version?: string | null;
  compatibility: SaveCompatibility;
  /** DLC-derived cities are encrypted with the pack key when saved. */
  encrypted?: boolean;
  /** Required packs recorded as city provenance (ascending). Also present on normal saves. */
  required_packs?: string[];
  pack_status?: Record<string, PackStatus>;
};

/** Normal save list information. The in-game city name is a single string. */
export type SaveEntry = CommonSaveEntry & {
  is_scenario: false;
  city_name: string; population: number; current_date: number; epoch: number;
  /** List heading assigned through Save As. Fall back to city name if absent. */
  save_name?: string;
  autosave?: boolean;
};

/** Scenario list information. City name contains only multilingual initial values used at startup. */
export type ScenarioEntry = CommonSaveEntry & {
  is_scenario: true;
  origin?: ScenarioOrigin;
  tag?: ScenarioTag | null;
  read_only?: boolean;
  /** Whether the official signature is valid. */
  official?: boolean;
  /** Description and initial city name: author-written multilingual text; resolveLocalizedText selects the display language. */
  scenario_description?: LocalizedText; scenario_objective?: LocalizedText;
  scenario_city_name?: LocalizedText;
  /** Start settings locked by the author. Players choose unlocked settings when starting a game. */
  scenario_fixed_options?: ScenarioStartOption[];
  author?: string;
  /** Attribution for terrain and geographic data. Show verbatim: providers prescribe the wording, so it is never translated. */
  scenario_credits?: string;
  content_revision?: number;
  /** Map dimensions in m. Unchanged even by the terrain editor. */
  map_width?: number; map_height?: number;
  /** City ground cover and monthly climate. Display the climate name text saved by the author. */
  ground_cover?: number; climate?: import("./climate").ClimateSettings;
  /** Default asset pack ID (standard is ASSET_PACK_STANDARD). Authors cannot lock it; players choose at startup. */
  asset_pack?: string;
};

/** Start settings the author may lock (core SCENARIO_START_OPTIONS). Excludes language/asset packs, which players always choose. */
export type ScenarioStartOption = "simulation_days" | "climate" | "ground_cover";

/** Available asset packs (owned and installed), excluding standard. */
export type AssetPackEntry = { pack_id: string; display_name: LocalizedText };

export async function fetchAssetPacks(apiBaseUrl: string): Promise<AssetPackEntry[]> {
  const { asset_packs } = await fetchApi<{ asset_packs: AssetPackEntry[] }>(buildApiUrl(apiBaseUrl, "/api/v1/system/asset_packs"), { cache: "reload" });
  return asset_packs ?? [];
}

/** Scenario heading: title, then initial city name, then ID prefix. */
export function scenarioDisplayTitle(entry: ScenarioEntry, language: string | undefined): string {
  return resolveLocalizedText(entry.scenario_title, language) || resolveLocalizedText(entry.scenario_city_name, language) || entry.id.slice(0, 8);
}

/** Normal save heading: name, then city name, then ID prefix. */
export function saveDisplayName(entry: SaveEntry): string {
  return entry.save_name || entry.city_name || entry.id.slice(0, 8);
}

/** Maximum wait for Unity capture. Missing responses must not block saving. */
const THUMBNAIL_CAPTURE_TIMEOUT_MS = 5000;

/**
 * Capture 3D immediately before saving and store the image in the core for the next save (Unity uploads
 * directly). If Unity is disconnected/unresponsive, return without capture; saving retains the previous image.
 */
export async function captureSaveThumbnail(): Promise<void> {
  const id = crypto.randomUUID();
  let finish!: () => void;
  const captured = new Promise<void>(resolve => { finish = resolve; });
  const unlisten = await listen<{ id: string }>("camera:thumbnail_captured", event => {
    if (event.payload.id === id) finish();
  });
  const timer = setTimeout(() => finish(), THUMBNAIL_CAPTURE_TIMEOUT_MS);
  try {
    if (await invoke<boolean>("request_thumbnail_capture", { id })) await captured;
  } catch (error) {
    console.error("failed to capture save thumbnail", error);
  } finally {
    clearTimeout(timer);
    unlisten();
  }
}

/** HTTP と Rust の構造化エラーから理由コードを取得する。 */
export function errorCode(error: unknown): string | null {
  const { code } = errorInfo(error);
  return code === "UNKNOWN_ERROR" ? null : code;
}

export function saveError(error: unknown, t: TFunction): string {
  return describeError(error, t);
}

/** Register a scenario image (PNG). Shared by metadata windows and the plan view immediately after terrain import. */
export async function uploadScenarioThumbnail(apiBaseUrl: string, scenarioId: string, png: Blob): Promise<void> {
  await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/saves/${encodeURIComponent(scenarioId)}/thumbnail?scenario=true`), {
    method: "PUT", headers: { "Content-Type": "image/png" }, body: png,
  });
}

/** Complete validation/confirmation before switching cities/windows. The core rechecks immediately before loading too. */
export async function prepareSaveLoad(apiBaseUrl: string, id: string, scenario: boolean, t: TFunction): Promise<boolean> {
  const result = await fetchApi<{ compatibility: SaveCompatibility }>(
    buildApiUrl(apiBaseUrl, `/api/v1/saves/${encodeURIComponent(id)}/validation?scenario=${scenario}`),
    { cache: "no-store" },
  );
  if (!result.compatibility.balance_changed) return true;
  return requestConfirm({ message: t("saveCompatibility.balanceConfirm", {
    from: result.compatibility.balance_core_version, to: result.compatibility.core_version,
  }) });
}
