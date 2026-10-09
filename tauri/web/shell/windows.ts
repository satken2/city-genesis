/**
 * ウィンドウの管理。`src-tauri/src/window.rs` と `lib.rs` のウィンドウ生成・配置の移植で、
 * 寸法・配置の規則・再利用・排他・閉じたときの後始末は exe と同じにする。どちらかを変えたら両方を揃える。
 * モニターはブラウザの表示領域1枚なので、表示先の切り替えに関わる計算は1台のときの結果になる。
 */
import { APP_PAGE } from "./config";
import { emit, emitTo } from "./events";
import * as host from "./host";
import type { Placement } from "./host";
import { sendUiAction } from "./ipc";
import { loadSettings } from "./settings";
import {
  state,
  toContext,
  type EntityRef,
  type NotificationPayload,
  type OpenWindowParams,
  type OpenWindowPayload,
  type WindowDefinition,
  type WindowGeometry,
} from "./state";

export const LABEL_ICON_PANEL = "icon-panel";
export const LABEL_COCKPIT = "cockpit";
export const LABEL_SYSTEM_TRAY = "system-tray";
export const LABEL_DISPLAY_TRAY = "display-tray";

const HUD_MARGIN = 8;
const ICON_PANEL_WIDTH = 58;
const ICON_PANEL_DEFAULT_HEIGHT = 660;
const COCKPIT_WIDTH = 908;
const COCKPIT_HEIGHT = 76;
const SYSTEM_TRAY_WIDTH = 176;
const SYSTEM_TRAY_HEIGHT = 40;
const DISPLAY_TRAY_WIDTH = 228;
const DISPLAY_TRAY_HEIGHT = 40;
const HUD_BOTTOM_RESERVED = COCKPIT_HEIGHT + HUD_MARGIN;
const HUD_RIGHT_BOTTOM_RESERVED = SYSTEM_TRAY_HEIGHT + HUD_MARGIN;
const HUD_LEFT_RESERVED = ICON_PANEL_WIDTH + HUD_MARGIN;

const TOPMENU_WIDTH = 800;
const TOPMENU_HEIGHT = 600;
const HINTBAR_HEIGHT = 68;
const TUTORIAL_HEIGHT = 200;
const TIPS_WIDTH = 680;
const TIPS_HEIGHT = 380;
const PARAMETER_SETUP_WIDTH = 620;
const PARAMETER_SETUP_HEIGHT = 560;
const NOTIFICATION_WIDTH = 320;
const NOTIFICATION_MIN_HEIGHT = 56;
const NOTIFICATION_MAX_HEIGHT = 200;

/** 非表示で再利用を待つポップアップの上限(`state.rs` の MAX_HIDDEN_WINDOWS)。 */
const MAX_HIDDEN_WINDOWS = 10;

/** 閉じたときに Unity を通常モードへ戻すウィンドウ(`window.rs` の EDIT_MODE_WINDOW_TYPES)。 */
const EDIT_MODE_WINDOW_TYPES = [
  "parcel_creation",
  "path_build",
  "building_construction",
  "resource_edit",
  "prop_placement",
  "transit_management",
  "transit_creation",
  "agent_creation",
  "grading",
];

let nextWindowId = 1;

/** 常駐 HUD のウィンドウ。表示・非表示はまとめて切り替える。 */
export interface HudWindowSpec {
  label: string;
  title: string;
  width: number;
  height: number;
  /** iframe が読むページ。省略時は各ウィンドウ共通のページ */
  page?: string;
}

/** exe と同じ HUD の組(`lib.rs` の setup)。 */
export const DESKTOP_HUD: HudWindowSpec[] = [
  { label: LABEL_ICON_PANEL, title: "Genesis Icon Panel", width: ICON_PANEL_WIDTH, height: ICON_PANEL_DEFAULT_HEIGHT },
  { label: LABEL_COCKPIT, title: "Genesis Cockpit", width: COCKPIT_WIDTH, height: COCKPIT_HEIGHT },
  { label: LABEL_SYSTEM_TRAY, title: "Genesis System Tray", width: SYSTEM_TRAY_WIDTH, height: SYSTEM_TRAY_HEIGHT },
  { label: LABEL_DISPLAY_TRAY, title: "Genesis Display Tray", width: DISPLAY_TRAY_WIDTH, height: DISPLAY_TRAY_HEIGHT },
];

let hudLabels: string[] = [];

function appUrl(query = ""): string {
  return `${APP_PAGE}${query}`;
}

function centeredOnMonitor(width: number, height: number): Placement {
  const m = host.monitor();
  return { x: m.x + (m.width - width) / 2, y: m.y + (m.height - height) / 2, width, height };
}

function hintbarPlacement(): Placement {
  const m = host.monitor();
  const width = m.width * 0.55;
  return { x: m.x + (m.width - width) / 2, y: m.y + 4, width, height: HINTBAR_HEIGHT };
}

function tutorialPlacement(): Placement {
  const m = host.monitor();
  const width = m.width * 0.55;
  return { x: m.x + (m.width - width) / 2, y: m.y + 4 + HINTBAR_HEIGHT + 4, width, height: TUTORIAL_HEIGHT };
}

function notificationPlacement(height: number): Placement {
  const m = host.monitor();
  const margin = 16;
  return {
    x: m.x + m.width - NOTIFICATION_WIDTH - margin,
    y: m.y + m.height - HUD_RIGHT_BOTTOM_RESERVED - height - margin,
    width: NOTIFICATION_WIDTH,
    height,
  };
}

/**
 * 起動時に作る常駐ウィンドウ(`lib.rs` の setup)。どれも非表示で作り、必要になったときに表示する。
 * HUD の組とヒントバーの有無はページの入口が決める(開発用の地図ページはコックピットの代わりに上部バーを置き、
 * 3D 画面の操作を案内するヒントバーは置かない)。
 */
export function createStartupWindows(hud: HudWindowSpec[] = DESKTOP_HUD, hintbar = true): void {
  host.createWindow("topmenu", {
    url: appUrl(),
    placement: centeredOnMonitor(TOPMENU_WIDTH, TOPMENU_HEIGHT),
    visible: false,
    title: "City Genesis",
  });
  hudLabels = hud.map((spec) => spec.label);
  for (const { label, title, width, height, page } of hud) {
    host.createWindow(label, { url: page ?? appUrl(), placement: { x: 0, y: 0, width, height }, visible: false, title });
  }
  layoutHudWindows(null, null);
  if (hintbar) host.createWindow("hintbar", { url: appUrl(), placement: hintbarPlacement(), visible: false, title: "Hintbar", clickThrough: true });
  host.createWindow("tutorial", { url: appUrl(), placement: tutorialPlacement(), visible: false, title: "Tutorial" });
  host.createWindow("notification", {
    url: appUrl(),
    placement: notificationPlacement(NOTIFICATION_MAX_HEIGHT),
    visible: false,
    title: "Notification",
  });
}

/** 表示領域の大きさが変わったとき、モニターに合わせて置く固定のウィンドウを置き直す。 */
export function relayoutForViewport(): void {
  layoutHudWindows(null, null);
  if (host.hasWindow("topmenu")) host.place("topmenu", centeredOnMonitor(TOPMENU_WIDTH, TOPMENU_HEIGHT));
  if (host.hasWindow("hintbar")) host.place("hintbar", hintbarPlacement());
  if (host.hasWindow("tutorial")) host.place("tutorial", tutorialPlacement());
  if (host.hasWindow("tips")) host.place("tips", centeredOnMonitor(TIPS_WIDTH, TIPS_HEIGHT));
  if (host.hasWindow("parameter-setup")) host.place("parameter-setup", centeredOnMonitor(PARAMETER_SETUP_WIDTH, PARAMETER_SETUP_HEIGHT));
  const notification = host.rect("notification");
  if (notification) host.place("notification", notificationPlacement(notification.height));
  host.refitMaximized();
}

/**
 * HUD を定位置(左端中央・下部中央・右下・左下)へ置く。iconPanelHeight と systemTrayWidth は React が実測した寸法で、
 * null なら今の寸法のまま位置だけ取り直す。
 */
export function layoutHudWindows(iconPanelHeight: number | null, systemTrayWidth: number | null): void {
  const m = host.monitor();
  const iconPanel = host.rect(LABEL_ICON_PANEL);
  if (iconPanel) {
    const current = iconPanel.height > 1 ? iconPanel.height : ICON_PANEL_DEFAULT_HEIGHT;
    const height = clamp(iconPanelHeight ?? current, 1, m.height - HUD_MARGIN * 2);
    host.place(LABEL_ICON_PANEL, { x: m.x + HUD_MARGIN, y: m.y + (m.height - height) / 2, width: ICON_PANEL_WIDTH, height });
  }
  if (host.hasWindow(LABEL_COCKPIT)) {
    host.place(LABEL_COCKPIT, {
      x: m.x + (m.width - COCKPIT_WIDTH) / 2,
      y: m.y + m.height - COCKPIT_HEIGHT - HUD_MARGIN,
      width: COCKPIT_WIDTH,
      height: COCKPIT_HEIGHT,
    });
  }
  const systemTray = host.rect(LABEL_SYSTEM_TRAY);
  if (systemTray) {
    const current = systemTray.width > 1 ? systemTray.width : SYSTEM_TRAY_WIDTH;
    const width = clamp(systemTrayWidth ?? current, 1, m.width - HUD_MARGIN * 2);
    host.place(LABEL_SYSTEM_TRAY, {
      x: m.x + m.width - width - HUD_MARGIN,
      y: m.y + m.height - SYSTEM_TRAY_HEIGHT - HUD_MARGIN,
      width,
      height: SYSTEM_TRAY_HEIGHT,
    });
  }
  if (host.hasWindow(LABEL_DISPLAY_TRAY)) {
    host.place(LABEL_DISPLAY_TRAY, {
      x: m.x + HUD_MARGIN,
      y: m.y + m.height - DISPLAY_TRAY_HEIGHT - HUD_MARGIN,
      width: DISPLAY_TRAY_WIDTH,
      height: DISPLAY_TRAY_HEIGHT,
    });
  }
}

export function setHudVisible(visible: boolean): void {
  for (const label of hudLabels) {
    if (visible) host.show(label);
    else host.hide(label);
  }
}

/** iframe は常に 3D 画面の上にあるので、最前面の打ち直し(`reassert_topmost`)は要らない。 */
export function reassertTopmost(): void {}

/** フライ・撮影の間、表示状態を変えずに UI を全部隠す(`set_ui_cloaked`)。 */
export function setUiCloaked(cloaked: boolean): void {
  host.setCloaked(cloaked);
}

export function resizeIconPanel(height: number): void {
  layoutHudWindows(height, null);
}

export function resizeSystemTray(width: number): void {
  layoutHudWindows(null, width);
}

function isDockedLayout(definition: WindowDefinition): boolean {
  return definition.layout !== "default";
}

/** ポップアップの今の位置とサイズを種別ごとに記憶する(`record_window_geometry`)。ドック配置はサイズだけ記憶する。 */
export function recordWindowGeometry(label: string, recordPosition: boolean): void {
  const window = state.openWindows.get(label);
  if (!window) return;
  const definition = state.registry[window.windowType];
  const docked = definition ? isDockedLayout(definition) : false;
  if (host.isMaximized(label)) return;
  const rect = host.rect(label);
  if (!rect) return;
  const entry: WindowGeometry = state.savedGeometries.get(window.windowType) ?? { x: 0, y: 0, width: 0, height: 0 };
  entry.width = rect.width;
  entry.height = rect.height;
  if (recordPosition && !docked) {
    entry.x = rect.x;
    entry.y = rect.y;
  }
  state.savedGeometries.set(window.windowType, entry);
}

/**
 * ポップアップの配置を種別のレイアウトから計算する(`compute_window_geometry`)。
 * 表示先にHUDがあるので、ドック配置は常駐HUD(左のアイコンパネル・下部中央のコックピット・右下のシステムトレイ)を避ける。
 */
function computeWindowGeometry(definition: WindowDefinition, payload: OpenWindowPayload, saved: WindowGeometry | null): Placement {
  const m = host.monitor();
  const [leftReserved, bottomReserved, rightBottomReserved] = [HUD_LEFT_RESERVED, HUD_BOTTOM_RESERVED, HUD_RIGHT_BOTTOM_RESERVED];
  const margin = HUD_MARGIN;
  switch (definition.layout) {
    case "left_panel":
    case "left_panel_narrow": {
      const defaultRatio = definition.layout === "left_panel_narrow" ? 0.2 : 0.4;
      const widthRatio = definition.width_ratio !== null && definition.width_ratio > 0 ? definition.width_ratio : defaultRatio;
      return {
        x: m.x + leftReserved + margin,
        y: m.y + margin,
        width: saved?.width ?? Math.round(m.width * widthRatio),
        height: saved?.height ?? m.height - bottomReserved - margin * 2,
      };
    }
    case "right_panel": {
      const width = saved?.width ?? Math.round(m.width * 0.2);
      return {
        x: m.x + m.width - width - margin,
        y: m.y + margin,
        width,
        height: saved?.height ?? m.height - rightBottomReserved - margin * 2,
      };
    }
    case "bottom_center": {
      const width = saved?.width ?? Math.round(m.width * (definition.width_ratio ?? 0.55));
      const height = saved?.height ?? definition.height;
      return { x: m.x + (m.width - width) / 2, y: m.y + m.height - bottomReserved - height - margin, width, height };
    }
    case "left_top":
      return {
        x: m.x + leftReserved + margin,
        y: m.y + margin,
        width: saved?.width ?? definition.width,
        height: saved?.height ?? definition.height,
      };
    case "right_top": {
      const workHeight = Math.max(m.height - bottomReserved - margin * 2, 200);
      let defaultWidth = definition.width;
      let defaultHeight = definition.height;
      if (definition.width_ratio !== null && definition.width_ratio > 0) {
        defaultWidth = Math.round(m.width * definition.width_ratio);
        defaultHeight =
          definition.height_ratio !== null && definition.height_ratio > 0
            ? Math.round(workHeight * definition.height_ratio)
            : Math.min(defaultWidth, workHeight);
      }
      const width = saved?.width ?? defaultWidth;
      return { x: m.x + m.width - width - margin, y: m.y + margin, width, height: saved?.height ?? defaultHeight };
    }
    default: {
      if (saved) return { x: saved.x, y: saved.y, width: saved.width, height: saved.height };
      let width = definition.width;
      let height = definition.height;
      if (definition.width_ratio !== null && definition.width_ratio > 0 && definition.height_ratio !== null && definition.height_ratio > 0) {
        width = Math.round(m.width * definition.width_ratio);
        height = Math.round(Math.max(m.height - bottomReserved, 100) * definition.height_ratio);
      }
      const preferred = payload.params?.preferred_position;
      const position =
        preferred && preferred.x !== null && preferred.y !== null
          ? { x: preferred.x, y: preferred.y }
          : { x: m.x + (m.width - width) / 2, y: m.y + (m.height - bottomReserved - height) / 2 };
      return { ...position, width, height };
    }
  }
}

/** JSON で届いた開く要求を serde の既定値で埋める(`OpenWindowPayload` の Deserialize に当たる)。 */
export function normalizeOpenWindowPayload(raw: unknown): OpenWindowPayload {
  const value = (raw ?? {}) as Record<string, unknown>;
  if (typeof value.window_type !== "string") throw "missing field `window_type`";
  return {
    window_type: value.window_type,
    entity: normalizeEntity(value.entity),
    params: normalizeOpenParams(value.params),
  };
}

export function normalizeEntity(raw: unknown): EntityRef | null {
  if (raw === null || typeof raw !== "object") return null;
  const value = raw as Record<string, unknown>;
  return {
    type: typeof value.type === "string" ? value.type : null,
    id: typeof value.id === "string" ? value.id : null,
  };
}

export function normalizeOpenParams(raw: unknown): OpenWindowParams | null {
  if (raw === null || typeof raw !== "object") return null;
  const value = raw as Record<string, unknown>;
  const hint = value.preferred_position as Record<string, unknown> | null | undefined;
  return {
    preferred_position:
      hint && typeof hint === "object"
        ? {
            mode: typeof hint.mode === "string" ? hint.mode : null,
            x: typeof hint.x === "number" ? hint.x : null,
            y: typeof hint.y === "number" ? hint.y : null,
          }
        : null,
    extra: value.extra ?? null,
  };
}

/**
 * ポップアップを開く(`open_window`)。同じ instance_key のウィンドウがあれば再表示し、
 * 無ければ再利用待ちのウィンドウを転用し、それも無ければ新しく作る。
 */
export async function openWindow(payload: OpenWindowPayload): Promise<void> {
  const definition = state.registry[payload.window_type];
  if (!definition) throw `unknown window_type: ${payload.window_type}`;
  const instanceKey = makeInstanceKey(payload.window_type, payload.entity);

  const existing = [...state.openWindows.values()].find((window) => window.instanceKey === instanceKey);
  if (existing) {
    const label = existing.label;
    const wasHidden = existing.hidden;
    if (wasHidden) {
      state.hiddenLru = state.hiddenLru.filter((hidden) => hidden !== label);
      applyMutexRules(payload.window_type);
      existing.hidden = false;
      existing.openSeq += 1;
    }
    existing.entity = payload.entity;
    existing.params = payload.params;
    if (host.hasWindow(label)) {
      host.show(label);
      host.focus(label);
      const context = toContext(label);
      if (context) emitTo(label, "window-context-updated", context);
    }
    return;
  }

  applyMutexRules(payload.window_type);
  const saved = state.savedGeometries.get(payload.window_type) ?? null;

  const reuseLabel = state.hiddenLru[state.hiddenLru.length - 1];
  if (reuseLabel !== undefined) {
    const placement = computeWindowGeometry(definition, payload, saved);
    state.hiddenLru = state.hiddenLru.filter((hidden) => hidden !== reuseLabel);
    const window = state.openWindows.get(reuseLabel);
    if (window) {
      window.windowType = payload.window_type;
      window.instanceKey = instanceKey;
      window.title = definition.title;
      window.entity = payload.entity;
      window.params = payload.params;
      window.hidden = false;
      window.openSeq += 1;
    }
    if (host.hasWindow(reuseLabel)) {
      host.setTitle(reuseLabel, definition.title);
      host.setResizable(reuseLabel, definition.resizable);
      placePopup(reuseLabel, placement, definition.maximized);
      host.show(reuseLabel);
      host.focus(reuseLabel);
      const context = toContext(reuseLabel);
      if (context) emitTo(reuseLabel, "window-context-updated", context);
    }
    return;
  }

  const label = `popup-${nextWindowId++}`;
  state.openWindows.set(label, {
    label,
    instanceKey,
    windowType: payload.window_type,
    title: definition.title,
    entity: payload.entity,
    params: payload.params,
    hidden: false,
    openSeq: 0,
  });
  const placement = computeWindowGeometry(definition, payload, saved);
  host.createWindow(label, { url: appUrl(), placement, visible: false, title: definition.title, resizable: definition.resizable });
  if (definition.maximized) host.maximize(label);
  host.show(label);
  host.focus(label);
}

function placePopup(label: string, placement: Placement, maximized: boolean): void {
  host.unmaximize(label);
  host.place(label, placement);
  if (maximized) host.maximize(label);
}

/** セッションの切り替えでは、古い HTTP 応答で Tips や結果を再表示させない。 */
export function cancelEpochPresentation(): void {
  const id = state.epochPresentationId;
  if (id !== null) {
    finishEpochPresentation(id);
    emit("epoch:darkened", { id });
  }
}

export function finishEpochPresentation(id: string): void {
  if (state.epochPresentationId !== id) return;
  state.epochPresentationId = null;
  const connected = state.graphicConnected;
  closeTipsWindow();
  if (connected) {
    try {
      sendUiAction("", null, "epoch:executing", { executing: false });
    } catch (error) {
      console.error("epoch presentation finish:", error);
    }
  }
  emit("epoch:executing", { executing: false });
}

function sendQuietly(windowType: string, action: string, params: unknown): void {
  try {
    sendUiAction(windowType, null, action, params);
  } catch {
    // 閉じる処理は Unity へ送れなくても続ける(exe でも結果を見ない)。
  }
}

/**
 * ポップアップを閉じる(`close_window_by_label`)。破棄せず非表示にして再利用を待たせ、上限を超えた古いものから破棄する。
 * 閉じても React はアンマウントされないので、Unity の編集状態などの後始末はここで送る。
 */
export function closeWindowByLabel(label: string): void {
  const window = state.openWindows.get(label);
  if (!window || window.hidden) return;
  const windowType = window.windowType;
  const entityId = window.entity?.id && /^-?\d+$/.test(window.entity.id) ? Number(window.entity.id) : null;

  if (windowType === "epoch_result") {
    const extra = (window.params?.extra ?? null) as Record<string, unknown> | null;
    const id = extra?.epochPresentationId;
    if (typeof id === "string") finishEpochPresentation(id);
  }

  if (EDIT_MODE_WINDOW_TYPES.includes(windowType)) {
    sendQuietly(windowType, "world:change_state", { state: "STATE_NORMAL" });
    if (windowType === "path_build") {
      sendQuietly(windowType, "view:set_grid", { mode: "off" });
      sendQuietly(windowType, "world:clear_highlights", { source: "path_build" });
      closeWindowsByType("path_shape_editor");
    }
    if (windowType === "building_construction") {
      sendQuietly(windowType, "world:clear_highlights", { source: "building_construction" });
    }
  } else if (windowType === "agent_detail") {
    sendQuietly(windowType, "edit_connector:hide", {});
  } else if (windowType === "zone_merge") {
    sendQuietly(windowType, "edit_zone:finish_merge", { merged: 0 });
  } else if (windowType === "parcel_detail") {
    sendQuietly(windowType, "parcel:hide_front_path", {});
    sendQuietly(windowType, "view:restore_preset", { preset: "parcel_detail" });
  } else if (windowType === "road_detail") {
    const params = entityId !== null ? { road_id: entityId } : {};
    sendQuietly(windowType, "world:clear_road_focus", params);
    sendQuietly(windowType, "edit_road:cancel", params);
    emitTo(label, "road:panel_closing", {});
  } else if (windowType === "path_detail") {
    sendQuietly(windowType, "world:clear_highlights", { source: "path_detail" });
    sendQuietly(windowType, "edit_path:cancel_topology_edit", {});
  }

  const toDestroy: string[] = [];
  while (state.hiddenLru.length >= MAX_HIDDEN_WINDOWS) {
    const old = state.hiddenLru.shift();
    if (old !== undefined) toDestroy.push(old);
  }
  state.hiddenLru.push(label);
  window.hidden = true;
  for (const old of toDestroy) {
    state.openWindows.delete(old);
    host.destroy(old);
  }

  host.hide(label);
  emitTo("tutorial", "tutorial:window_visibility", { label, window: windowType, entity: entityId, visible: false });
}

export function closeWindowsByType(windowType: string): void {
  const labels = [...state.openWindows.values()].filter((window) => !window.hidden && window.windowType === windowType).map((window) => window.label);
  for (const label of labels) closeWindowByLabel(label);
}

function applyMutexRules(windowType: string): void {
  if (EDIT_MODE_WINDOW_TYPES.includes(windowType)) emit("hud:mode_changed", { mode: null });
  const blocked = state.mutexGroups[windowType] ?? [];
  if (blocked.length === 0) return;
  const labels = [...state.openWindows.values()].filter((window) => blocked.includes(window.windowType)).map((window) => window.label);
  for (const label of labels) closeWindowByLabel(label);
}

function makeInstanceKey(windowType: string, entity: EntityRef | null): string {
  return entity?.id ? `${windowType}:${entity.id}` : windowType;
}

/** 破棄したポップアップを管理から外す(`on_window_destroyed`)。ポップアップ以外の常駐・専用ウィンドウは管理の対象外。 */
export function onWindowDestroyed(label: string): void {
  if (!label.startsWith("popup-")) return;
  state.openWindows.delete(label);
  state.hiddenLru = state.hiddenLru.filter((hidden) => hidden !== label);
  emit("popup-count-changed", {});
}

export function closeAllPopupWindows(): void {
  for (const label of [...state.openWindows.keys()]) closeWindowByLabel(label);
}

export function getConnectionStatus() {
  return {
    api_base_url: state.apiBaseUrl,
    backend_connected: state.backendHealth.connected,
    open_window_count: [...state.openWindows.values()].filter((window) => !window.hidden).length,
  };
}

/** パラメータ設定ウィンドウを開く。editMode はテンプレート作成モード中の再編集(完了しても game:start を送らない)。 */
export function openParameterSetupWindow(editMode: boolean): void {
  state.parameterSetupEditMode = editMode;
  if (host.hasWindow("parameter-setup")) {
    host.focus("parameter-setup");
    return;
  }
  host.createWindow("parameter-setup", {
    url: appUrl(),
    placement: centeredOnMonitor(PARAMETER_SETUP_WIDTH, PARAMETER_SETUP_HEIGHT),
    visible: true,
    title: "ゲームパラメータ設定",
  });
}

export function closeParameterSetupWindow(): void {
  host.destroy("parameter-setup");
}

/** Tips のウィンドウ。初期ロードは開始ボタンを待ち、エポック待機(epoch)は呼び出し元が完了時に閉じる。 */
export function openTipsWindow(epoch = false): void {
  if (host.hasWindow("tips")) return;
  host.createWindow("tips", {
    url: appUrl(epoch ? "?tipsMode=epoch" : ""),
    placement: centeredOnMonitor(TIPS_WIDTH, TIPS_HEIGHT),
    visible: true,
    title: "Today's Tips",
  });
}

export function closeTipsWindow(): void {
  host.destroy("tips");
}

/** 通知を待ち行列に積み、表示中でなければ表示を始める(`enqueue_notification`)。 */
export function enqueueNotification(payload: NotificationPayload): void {
  state.notificationQueue.push(payload);
  if (!state.notificationShowing) setTimeout(processNotificationQueue, 50);
}

function processNotificationQueue(): void {
  if (state.notificationShowing) return;
  const payload = state.notificationQueue.shift();
  if (!payload) return;
  state.notificationShowing = true;
  state.notificationGeneration += 1;
  const generation = state.notificationGeneration;
  if (!host.hasWindow("notification")) {
    host.createWindow("notification", {
      url: appUrl(),
      placement: notificationPlacement(NOTIFICATION_MAX_HEIGHT),
      visible: false,
      title: "Notification",
    });
  }
  // 最大高さの透明な領域を見せないよう、表示は本文を実測したリサイズの後に行う。
  emit("notification-show", payload);
  setTimeout(() => dismissNotificationGeneration(generation), payload.duration_ms ?? 5000);
}

export function dismissNotification(): void {
  dismissNotificationGeneration(null);
}

function dismissNotificationGeneration(generation: number | null): void {
  if ((generation !== null && generation !== state.notificationGeneration) || !state.notificationShowing) return;
  state.notificationShowing = false;
  state.notificationGeneration += 1;
  host.hide("notification");
  if (state.notificationQueue.length > 0) setTimeout(processNotificationQueue, 300);
}

export function resizeNotificationWindow(height: number): void {
  if (!state.notificationShowing || !host.hasWindow("notification")) return;
  host.place("notification", notificationPlacement(clamp(height, NOTIFICATION_MIN_HEIGHT, NOTIFICATION_MAX_HEIGHT)));
  host.show("notification");
}

/** 記憶した位置・サイズを捨て、開いているポップアップを初期配置へ戻す(`reset_window_geometries`)。 */
export function resetWindowGeometries(): void {
  state.savedGeometries.clear();
  for (const window of state.openWindows.values()) {
    const definition = state.registry[window.windowType];
    if (!definition || !host.hasWindow(window.label)) continue;
    if (window.hidden && definition.maximized) continue;
    const payload: OpenWindowPayload = { window_type: window.windowType, entity: null, params: null };
    placePopup(window.label, computeWindowGeometry(definition, payload, null), definition.maximized);
  }
}

/** ヒントバーは設定で非表示にしていれば出さない(`show_hintbar_if_enabled`)。 */
export function showHintbarIfEnabled(): void {
  if (loadSettings().hintbar_visible) host.show("hintbar");
  else host.hide("hintbar");
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max);
}
