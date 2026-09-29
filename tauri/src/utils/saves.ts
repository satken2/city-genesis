import type { TFunction } from "i18next";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
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
  /** 通常セーブの名前(別名セーブで付ける)。無ければ見出しは都市名 */
  save_name?: string;
  has_thumbnail?: boolean;
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
  author?: string;
  content_revision?: number;
  /** 地形の出どころ(取り込んだ標高ファイルの名前、またはチュートリアルの舞台の名前)。表示用の情報で、判定には使わない */
  terrain_source?: string;
  /** マップの大きさ(m)。地形エディタでも変わらない */
  map_width?: number; map_height?: number;
  /** 都市が持つ地表・気候(`GROUND_COVER_*` / `CLIMATE_*`)。地形エディタ・紹介情報で変わる */
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

/** 通常セーブの見出し。名前、無ければ都市名、それも無ければIDの先頭 */
export function saveDisplayName(entry: SaveEntry): string {
  return entry.save_name || entry.city_name || entry.id.slice(0, 8);
}

/** Unityの撮影を待つ上限。応答が無くても保存は止めない */
const THUMBNAIL_CAPTURE_TIMEOUT_MS = 5000;

/**
 * 保存の直前に3Dビューを撮り、次の保存に入る画像としてコアへ預ける(Unityがコアへ直接送る)。
 * Unityが接続していない・応答しないときは撮らずに戻り、保存には前の画像が入る。
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

/** シナリオ画像(PNG)を登録する。紹介情報ウィンドウと、地形を取り込んだ直後の平面図で共用する。 */
export async function uploadScenarioThumbnail(apiBaseUrl: string, scenarioId: string, png: Blob): Promise<void> {
  await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/saves/${encodeURIComponent(scenarioId)}/thumbnail?template=true`), {
    method: "PUT", headers: { "Content-Type": "image/png" }, body: png,
  });
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
