/**
 * 地形エディタの作業コピー。`src-tauri/src/lib.rs` の地形エディタのコマンドの移植。
 * 作業コピーはウィンドウ間の初期データの受け渡し専用で、シナリオへの反映は save_scenario_terrain(コアの PUT)だけが行う。
 * exe はユーザーフォルダのファイルに、ブラウザ版はページのメモリに置く。
 */
import { apiBase, fetchWithTimeout } from "./net";
import { errorInfo, type ErrorInfo } from "../../src/utils/errors";
import { requireBackend } from "./session";
import { state } from "./state";

interface WorkingCopy {
  meta: Record<string, unknown>;
  near: Uint8Array;
  createdAt: number;
}

/** どのウィンドウにも結び付かない作業コピーを消すまでの時間。作った直後(ウィンドウを開く前)のものは消さない。 */
const STALE_COPY_MS = 3600 * 1000;
const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

const copies = new Map<string, WorkingCopy>();

function workingCopy(editorId: string): WorkingCopy {
  if (!UUID_PATTERN.test(editorId)) throw `地形エディタの作業コピーのIDが不正です: ${editorId}`;
  const copy = copies.get(editorId);
  if (!copy) throw "meta.json の読み込みに失敗しました: 作業コピーがありません";
  return copy;
}

function scenarioTerrainUrl(scenarioId: string): string {
  return `${apiBase()}/api/v1/scenarios/${encodeURIComponent(scenarioId)}/terrain`;
}

/** デスクトップと同じコードとパラメータを iframe へ渡す。 */
async function scenarioApiError(response: Response): Promise<ErrorInfo> {
  return errorInfo(await response.json().catch(() => null));
}

function decodeBase64(text: string): Uint8Array {
  const binary = atob(text);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

function encodeBase64(bytes: Uint8Array): string {
  let binary = "";
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) binary += String.fromCharCode(...bytes.subarray(i, i + chunk));
  return btoa(binary);
}

function terrainEditorWindowIds(): { visible: string[]; all: Set<string> } {
  const visible: string[] = [];
  const all = new Set<string>();
  for (const window of state.openWindows.values()) {
    if (window.windowType !== "terrain_editor" || !window.entity?.id) continue;
    if (!window.hidden) visible.push(window.entity.id);
    all.add(window.entity.id);
  }
  return { visible, all };
}

/** GET の応答から作業コピーを作る。標高のバイト数が格子と合わなければ拒否する。 */
function scenarioTerrainWorkingCopy(scenarioId: string, document: Record<string, unknown>): { meta: Record<string, unknown>; near: Uint8Array } {
  const settings = document.settings as Record<string, number> | undefined;
  const locks = document.locks as Record<string, unknown> | undefined;
  if (!settings || typeof document.near_base64 !== "string" || !locks || typeof locks.heights_locked !== "boolean") {
    throw "シナリオの地形の形式が不正です";
  }
  let near: Uint8Array;
  try {
    near = decodeBase64(document.near_base64);
  } catch (error) {
    throw `標高データのデコードに失敗しました: ${String(error)}`;
  }
  const grid = (count: number) => (Number.isInteger(count) && count >= 2 && count <= 0xffffffff ? count : null);
  const rows = grid(settings.grid_rows);
  const cols = grid(settings.grid_cols);
  if (rows === null || cols === null) throw `標高データの格子数が不正です(${settings.grid_cols}×${settings.grid_rows})`;
  if (rows * cols * 2 !== near.length) throw `標高データのサイズが格子(${cols}×${rows})と一致しません(${near.length} バイト)`;
  const array = (value: unknown) => (Array.isArray(value) ? value : []);
  const props = document.props !== null && typeof document.props === "object" && !Array.isArray(document.props)
    ? document.props
    : { instances: [], scatters: [] };
  const meta = {
    scenario_id: scenarioId,
    terrain_source: document.terrain_source ?? null,
    title: document.title ?? null,
    locks: { heights_locked: locks.heights_locked, reasons: array(locks.reasons) },
    map_width: settings.map_width,
    map_height: settings.map_height,
    near_spacing: settings.near_spacing,
    far_spacing: settings.far_spacing,
    cell_size: settings.cell_size,
    grid_rows: rows,
    grid_cols: cols,
    water_surfaces: array(document.water_surfaces),
    rivers: array(document.rivers),
    resources: array(document.resources),
    props,
    camera: document.camera ?? null,
    ground_cover: document.ground_cover ?? null,
    climate: document.climate ?? null,
  };
  return { meta, near };
}

/**
 * シナリオの地形を作業コピーへ書き出し、そのID(editor_id)を返す。同じシナリオの地形エディタが表示中ならそのIDを返す
 * (開き直しで未保存の編集を捨てず、2枚のウィンドウの保存が互いを上書きしないようにする)。
 */
export async function prepareScenarioTerrainEditor(args: { scenarioId: string }): Promise<string> {
  const { visible, all } = terrainEditorWindowIds();
  const opened = visible.find((id) => UUID_PATTERN.test(id) && copies.get(id)?.meta.scenario_id === args.scenarioId);
  if (opened) return opened;
  const now = Date.now();
  for (const [id, copy] of copies) if (now - copy.createdAt > STALE_COPY_MS && !all.has(id)) copies.delete(id);

  let response: Response;
  try {
    response = await fetchWithTimeout(scenarioTerrainUrl(args.scenarioId), 120000);
  } catch (error) {
    throw `シナリオの地形を取得できません: ${String(error)}`;
  }
  if (!response.ok) throw await scenarioApiError(response);
  let document: Record<string, unknown>;
  try {
    document = (await response.json()) as Record<string, unknown>;
  } catch (error) {
    throw `シナリオの地形の形式が不正です: ${String(error)}`;
  }
  const { meta, near } = scenarioTerrainWorkingCopy(args.scenarioId, document);
  const editorId = crypto.randomUUID();
  copies.set(editorId, { meta, near, createdAt: now });
  console.info(`[prepare_scenario_terrain_editor] scenario_id=${args.scenarioId} editor_id=${editorId}`);
  return editorId;
}

export function loadTerrainEditorData(args: { editorId: string }) {
  const { meta, near } = workingCopy(args.editorId);
  const number = (key: string) => (typeof meta[key] === "number" ? (meta[key] as number) : 0);
  const array = (key: string) => (Array.isArray(meta[key]) ? meta[key] : []);
  const props = meta.props !== null && typeof meta.props === "object" ? meta.props : { instances: [], scatters: [] };
  return {
    scenario_id: typeof meta.scenario_id === "string" ? meta.scenario_id : "",
    terrain_source: meta.terrain_source ?? null,
    title: meta.title ?? null,
    locks: meta.locks ?? null,
    map_width: number("map_width"),
    map_height: number("map_height"),
    near_spacing: number("near_spacing"),
    far_spacing: number("far_spacing"),
    cell_size: number("cell_size"),
    grid_rows: Math.trunc(number("grid_rows")),
    grid_cols: Math.trunc(number("grid_cols")),
    data_base64: encodeBase64(near),
    water_surfaces: array("water_surfaces"),
    rivers: array("rivers"),
    resources: array("resources"),
    props,
    camera: meta.camera ?? null,
    ground_cover: meta.ground_cover ?? null,
    climate: meta.climate ?? null,
  };
}

interface SaveArgs {
  editorId: string;
  nearBase64?: string | null;
  waterSurfaces?: unknown;
  rivers?: unknown;
  resources: unknown;
  props: unknown;
  camera?: unknown;
  groundCover: number;
  climate: unknown;
}

/**
 * 編集内容をシナリオへ保存する(PUT /api/v1/scenarios/{id}/terrain)。保存先のシナリオは作業コピーから引く。
 * 標高・水面・川はロック中なら送らない決まりで、ここでも先に弾く。成功したら作業コピーを保存した内容に合わせる。
 */
export async function saveScenarioTerrain(args: SaveArgs) {
  requireBackend();
  const copy = workingCopy(args.editorId);
  const scenarioId = copy.meta.scenario_id;
  if (typeof scenarioId !== "string" || scenarioId === "") throw "作業コピーにシナリオIDがありません";
  const locks = copy.meta.locks as Record<string, unknown> | null;
  const heightsLocked = typeof locks?.heights_locked === "boolean" ? locks.heights_locked : true;
  const nearBase64 = args.nearBase64 ?? null;
  const waterSurfaces = args.waterSurfaces ?? null;
  const rivers = args.rivers ?? null;
  if (heightsLocked && (nearBase64 !== null || waterSurfaces !== null || rivers !== null)) {
    throw "TERRAIN_LOCKED";
  }
  const body = {
    near_base64: nearBase64,
    water_surfaces: waterSurfaces,
    rivers,
    resources: args.resources,
    props: args.props,
    camera: args.camera ?? null,
    ground_cover: args.groundCover,
    climate: args.climate,
  };
  let response: Response;
  try {
    response = await fetchWithTimeout(scenarioTerrainUrl(scenarioId), 600000, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
  } catch (error) {
    throw `シナリオへ保存できません: ${String(error)}`;
  }
  if (!response.ok) throw await scenarioApiError(response);
  let result: { content_revision: number; locks: unknown; resource_ids: Record<string, string> };
  try {
    const data = (await response.json()) as Record<string, unknown>;
    result = {
      content_revision: data.content_revision as number,
      locks: data.locks,
      resource_ids: (data.resource_ids ?? {}) as Record<string, string>,
    };
  } catch (error) {
    throw `シナリオへの保存の応答の形式が不正です: ${String(error)}`;
  }
  console.info(`[save_scenario_terrain] scenario_id=${scenarioId} editor_id=${args.editorId} content_revision=${result.content_revision}`);
  if (nearBase64 !== null) copy.near = decodeBase64(nearBase64);
  copy.meta = savedTerrainEditorMeta(copy.meta, body, result);
  return result;
}

/**
 * 保存した内容を反映した作業コピー。新しく作った資源はコアの ID に替える(仮IDのまま保存し直すと、コアが資源を作り直すため)。
 * 水面・川は送ったとき(null でないとき)だけ置き換える。コアも null を「変更しない」と扱う。
 */
function savedTerrainEditorMeta(
  meta: Record<string, unknown>,
  body: Record<string, unknown>,
  result: { locks: unknown; resource_ids: Record<string, string> },
): Record<string, unknown> {
  const resources = Array.isArray(body.resources)
    ? body.resources.map((entry: unknown) => {
        if (entry === null || typeof entry !== "object") return entry;
        const id = (entry as Record<string, unknown>).id;
        const saved = typeof id === "string" ? result.resource_ids[id] : undefined;
        return saved !== undefined ? { ...(entry as Record<string, unknown>), id: saved } : entry;
      })
    : body.resources;
  const next: Record<string, unknown> = { ...meta };
  for (const key of ["props", "camera", "ground_cover", "climate"]) next[key] = body[key];
  for (const key of ["water_surfaces", "rivers"]) if (body[key] !== null) next[key] = body[key];
  next.resources = resources;
  next.locks = result.locks ?? null;
  return next;
}
