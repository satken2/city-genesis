/**
 * Web シェルの共有状態。`src-tauri/src/state.rs` の AppState の移植で、項目の意味は Rust 側の説明に従う。
 * ブラウザは1本のスレッドで動くので、Rust のロックは要らない。
 */
import registryJson from "../../src-tauri/config/window-registry.json";
import mutexGroupsJson from "../../src-tauri/config/window-mutex-groups.json";
import { CORE_API_URL } from "./config";

export type WindowLayout =
  | "default"
  | "left_panel"
  | "left_panel_narrow"
  | "left_top"
  | "right_top"
  | "right_panel"
  | "bottom_center";

/** `messages.rs` の WindowDefinition。省略された項目は serde の既定値で埋める(UI は全項目がある前提で読む)。 */
export interface WindowDefinition {
  title: string;
  width: number;
  height: number;
  resizable: boolean;
  policy: string;
  entity_type: string | null;
  fetch: { method: string; path_template: string } | null;
  actions: { id: string; label: string }[];
  html_path: string | null;
  empty_shell: boolean;
  chromeless: boolean;
  layout: WindowLayout;
  width_ratio: number | null;
  height_ratio: number | null;
  maximized: boolean;
}

export interface EntityRef {
  type: string | null;
  id: string | null;
}

export interface PositionHint {
  mode: string | null;
  x: number | null;
  y: number | null;
}

export interface OpenWindowParams {
  preferred_position: PositionHint | null;
  extra: unknown;
}

export interface OpenWindowPayload {
  window_type: string;
  entity: EntityRef | null;
  params: OpenWindowParams | null;
}

export interface OpenWindowState {
  label: string;
  instanceKey: string;
  windowType: string;
  title: string;
  entity: EntityRef | null;
  params: OpenWindowParams | null;
  hidden: boolean;
  openSeq: number;
}

/** 記憶したポップアップの位置とサイズ(CSS ピクセル)。 */
export interface WindowGeometry {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface NotificationPayload {
  message: string;
  icon_path: string;
  duration_ms: number | null;
}

function normalizeDefinition(raw: Record<string, unknown>): WindowDefinition {
  return {
    title: String(raw.title ?? ""),
    width: Number(raw.width ?? 0),
    height: Number(raw.height ?? 0),
    resizable: raw.resizable === true,
    policy: String(raw.policy ?? ""),
    entity_type: (raw.entity_type as string | undefined) ?? null,
    fetch: (raw.fetch as WindowDefinition["fetch"] | undefined) ?? null,
    actions: (raw.actions as WindowDefinition["actions"] | undefined) ?? [],
    html_path: (raw.html_path as string | undefined) ?? null,
    empty_shell: raw.empty_shell === true,
    chromeless: raw.chromeless === true,
    layout: (raw.layout as WindowLayout | undefined) ?? "default",
    width_ratio: (raw.width_ratio as number | undefined) ?? null,
    height_ratio: (raw.height_ratio as number | undefined) ?? null,
    maximized: raw.maximized === true,
  };
}

const registry: Record<string, WindowDefinition> = Object.fromEntries(
  Object.entries(registryJson as Record<string, Record<string, unknown>>).map(([type, raw]) => [type, normalizeDefinition(raw)]),
);

export const state = {
  registry,
  mutexGroups: mutexGroupsJson as Record<string, string[]>,
  openWindows: new Map<string, OpenWindowState>(),
  savedGeometries: new Map<string, WindowGeometry>(),
  apiBaseUrl: CORE_API_URL,
  currentMonitorIndex: 0,
  multiDisplay: false,
  popupMonitorIndex: 0,
  gameTime: null as unknown,
  shutdown: false,
  exiting: false,
  sessionState: null as unknown,
  backendHealth: { connected: false, failures: 0 },
  lastSessionId: null as string | null,
  isLoading: false,
  loadingId: 0,
  backendLoadComplete: false,
  graphicLoadReady: false,
  needsParameterSetup: false,
  awaitingParameterSetup: false,
  loadingStarted: false,
  tutorialId: null as string | null,
  introDialog: null as unknown,
  parameterSetupEditMode: false,
  notificationQueue: [] as NotificationPayload[],
  notificationShowing: false,
  notificationGeneration: 0,
  hiddenLru: [] as string[],
  graphicReadySeen: false,
  graphicConnected: false,
  graphicContractError: null as string | null,
  graphicVersion: null as string | null,
  epochPresentationId: null as string | null,
};

/** `get_window_context` の戻り値(WindowContextDto)。 */
export function toContext(label: string) {
  const window = state.openWindows.get(label);
  if (!window) return null;
  const definition = state.registry[window.windowType];
  if (!definition) return null;
  return {
    label: window.label,
    instance_key: window.instanceKey,
    window_type: window.windowType,
    title: window.title,
    definition,
    entity: window.entity,
    params: window.params,
    api_base_url: state.apiBaseUrl,
    open_seq: window.openSeq,
  };
}
