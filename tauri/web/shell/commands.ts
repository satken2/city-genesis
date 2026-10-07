/**
 * Tauri のコマンドの実装。`src-tauri/src/lib.rs` の invoke_handler に並ぶコマンドを、同じ名前・引数・戻り値で受ける。
 * 引数の名前は Tauri と同じく JS 側の camelCase。失敗は Rust の Err(String) と同じく文字列で返す。
 */
import { emit } from "./events";
import { sendUiAction } from "./ipc";
import { requestAppExit } from "./process";
import {
  closeTipsAndReveal,
  createSession,
  finishParameterSetup,
  requireBackend,
  startLoadGame,
} from "./session";
import { loadSettings, saveSettings } from "./settings";
import { state, toContext } from "./state";
import { loadTerrainEditorData, prepareScenarioTerrainEditor, saveScenarioTerrain } from "./terrainEditor";
import { coreInfo, getFrontendStatus } from "./version";
import {
  closeParameterSetupWindow,
  closeTipsWindow,
  closeWindowByLabel,
  closeWindowsByType,
  dismissNotification,
  enqueueNotification,
  finishEpochPresentation,
  getConnectionStatus,
  normalizeEntity,
  normalizeOpenParams,
  openParameterSetupWindow,
  openTipsWindow,
  openWindow,
  resetWindowGeometries,
  resizeIconPanel,
  resizeNotificationWindow,
  resizeSystemTray,
} from "./windows";

type Args = Record<string, unknown>;
type Command = (caller: string, args: Args) => unknown;

function stringArg(args: Args, key: string, command: string): string {
  const value = args[key];
  if (typeof value !== "string") throw `invalid args \`${key}\` for command \`${command}\``;
  return value;
}

function optionalString(args: Args, key: string, command: string): string | null {
  const value = args[key];
  if (value === null || value === undefined) return null;
  if (typeof value !== "string") throw `invalid args \`${key}\` for command \`${command}\``;
  return value;
}

const commands: Record<string, Command> = {
  get_window_context: (caller) => {
    const context = toContext(caller);
    if (!context) throw "window context not found";
    return context;
  },
  close_current_window: (caller) => closeWindowByLabel(caller),
  send_window_action: (_caller, args) => {
    requireBackend();
    sendUiAction(stringArg(args, "windowType", "send_window_action"), normalizeEntity(args.entity), stringArg(args, "action", "send_window_action"), args.params ?? null);
  },
  begin_epoch_presentation: (_caller, args) => {
    requireBackend();
    const id = stringArg(args, "id", "begin_epoch_presentation");
    if (state.isLoading || state.epochPresentationId !== null) throw "エポック演出または初期ロードが進行中です";
    state.epochPresentationId = id;
    const connected = state.graphicConnected;
    if (connected) {
      try {
        sendUiAction("", null, "epoch:prepare", { id });
      } catch (error) {
        finishEpochPresentation(id);
        throw error;
      }
    }
    emit("epoch:executing", { executing: true });
    return connected;
  },
  set_epoch_tips_visible: (_caller, args) => {
    if (state.epochPresentationId !== stringArg(args, "id", "set_epoch_tips_visible")) return;
    if (args.visible === true) openTipsWindow(true);
    else closeTipsWindow();
  },
  finish_epoch_presentation: (_caller, args) => finishEpochPresentation(stringArg(args, "id", "finish_epoch_presentation")),
  is_epoch_presentation_current: (_caller, args) => state.epochPresentationId === stringArg(args, "id", "is_epoch_presentation_current"),
  request_thumbnail_capture: (_caller, args) => {
    if (!state.graphicConnected) return false;
    sendUiAction("", null, "camera:capture_thumbnail", { id: stringArg(args, "id", "request_thumbnail_capture") });
    return true;
  },
  get_connection_status: () => getConnectionStatus(),
  resize_icon_panel: (_caller, args) => resizeIconPanel(Number(args.height)),
  resize_system_tray: (_caller, args) => resizeSystemTray(Number(args.width)),
  get_topmenu_state: () => ({ apiBaseUrl: state.apiBaseUrl }),
  switch_monitor: () => ({ monitorIndex: 0 }), // モニターは表示領域1枚なので、モニター1台の exe と同じ結果になる
  set_multi_display: (_caller, args) => {
    state.multiDisplay = args.enabled === true;
    saveSettings({ ...loadSettings(), multi_display: state.multiDisplay });
  },
  switch_popup_monitor: () => {
    state.popupMonitorIndex = 0;
    saveSettings({ ...loadSettings(), popup_monitor_index: 0 });
  },
  load_app_settings: () => loadSettings(),
  save_app_settings: (_caller, args) => saveSettings(args.settings),
  open_registered_window: async (_caller, args) => {
    const entityId = optionalString(args, "entityId", "open_registered_window");
    await openWindow({
      window_type: stringArg(args, "windowType", "open_registered_window"),
      entity: entityId === null ? null : { type: null, id: entityId },
      params: normalizeOpenParams(args.params),
    });
  },
  close_registered_window: (_caller, args) => closeWindowsByType(stringArg(args, "windowType", "close_registered_window")),
  start_load_game: (_caller, args) =>
    startLoadGame({ saveId: stringArg(args, "saveId", "start_load_game"), acceptBalanceChange: args.acceptBalanceChange as boolean | null }),
  get_frontend_status: () => getFrontendStatus(),
  get_core_version: () => coreInfo(),
  create_session: (_caller, args) => createSession(args),
  exit_app: () => {
    setTimeout(requestAppExit, 0);
  },
  show_notification: (_caller, args) =>
    enqueueNotification({
      message: stringArg(args, "message", "show_notification"),
      icon_path: optionalString(args, "iconPath", "show_notification") ?? "",
      duration_ms: typeof args.durationMs === "number" ? args.durationMs : null,
    }),
  dismiss_notification: () => dismissNotification(),
  resize_notification_window: (_caller, args) => resizeNotificationWindow(Number(args.height)),
  finish_parameter_setup: () => finishParameterSetup(),
  open_parameter_edit_window: () => openParameterSetupWindow(true),
  get_parameter_setup_mode: () => state.parameterSetupEditMode,
  close_parameter_setup_window: () => closeParameterSetupWindow(),
  close_tips_window: () => closeTipsAndReveal(),
  get_initial_loading_ready: () => state.graphicLoadReady,
  reset_window_geometries: () => resetWindowGeometries(),
  prepare_scenario_terrain_editor: (_caller, args) =>
    prepareScenarioTerrainEditor({ scenarioId: stringArg(args, "scenarioId", "prepare_scenario_terrain_editor") }),
  load_terrain_editor_data: (_caller, args) => loadTerrainEditorData({ editorId: stringArg(args, "editorId", "load_terrain_editor_data") }),
  save_scenario_terrain: (_caller, args) =>
    saveScenarioTerrain({
      editorId: stringArg(args, "editorId", "save_scenario_terrain"),
      nearBase64: optionalString(args, "nearBase64", "save_scenario_terrain"),
      waterSurfaces: args.waterSurfaces,
      rivers: args.rivers,
      resources: args.resources,
      props: args.props,
      camera: args.camera,
      groundCover: Number(args.groundCover),
      climate: args.climate,
    }),
  open_save_directory: () => {
    throw "ブラウザ版ではセーブフォルダを開けません"; // セーブはコアの PC にあり、ブラウザからはそのフォルダを開けない
  },
  log_frontend_error: (_caller, args) => console.error(`[frontend] ${String(args.message)}`),
  log_frontend_info: (_caller, args) => console.info(`[frontend] ${String(args.message)}`),
  get_client_system_stats: () => ({ // ブラウザでは測れないので、Rust が非対応の環境で返す形にする
    memory: { system_total_bytes: null, used_bytes: null, graphic: null },
    vram: null,
    environment: { cpu_name: null, gpu_name: null, gpu_memory_bytes: null },
  }),
};

export async function runCommand(caller: string, command: string, args: Args): Promise<unknown> {
  const handler = commands[command];
  if (!handler) throw `command ${command} not found`;
  return await handler(caller, args);
}
