/**
 * Unity との IPC。`src-tauri/src/ipc.rs` の移植で、Envelope・イベント名・振り分けは exe と同じ。
 * 運び方だけが違い、MMF の代わりに同じページで動く Unity の jslib(`Assets/Plugins/WebGL/GenesisWebIpc.jslib`)と
 * `globalThis.__genesisGraphicBridge` を通して受け渡す。
 */
import { DEVELOPMENT } from "./config";
import { emit, emitTo } from "./events";
import { requestAppExit } from "./process";
import { beginInitialLoading, publishBackendConnection, publishVerification, sendGameStartIpc } from "./session";
import { state, type EntityRef, type OpenWindowPayload } from "./state";
import { definition } from "./version";
import { LABEL_COCKPIT, closeAllPopupWindows, normalizeOpenWindowPayload, openWindow, reassertTopmost, setUiCloaked } from "./windows";

export interface Envelope {
  version: string;
  request_id: string;
  timestamp: string;
  source: string;
  destination: string;
  event: string;
  payload: unknown;
}

/** Unity の jslib との取り決め。attach は Unity が受信を始めたとき、receive は Unity が送ったときに呼ばれる。 */
interface GraphicBridge {
  attach(deliver: (json: string) => void): void;
  detach(): void;
  receive(json: string): void;
}

/** MMF のリングバッファに溜められる量。Unity の受信開始前に送った分はここまで溜め、超えたら exe と同じく失敗にする。 */
const PENDING_CAPACITY = 1024 * 1024 - 8;

let deliver: ((json: string) => void) | null = null;
const pending: string[] = [];
let pendingBytes = 0;
let nextMessageId = 1;

export function installGraphicBridge(): void {
  const bridge: GraphicBridge = {
    attach(callback) {
      deliver = callback;
      for (const json of pending.splice(0)) callback(json);
      pendingBytes = 0;
    },
    detach() {
      deliver = null;
    },
    receive(json) {
      // Unity の呼び出しの途中で Unity へ送り返さないよう、受信の処理は Unity の処理が戻ってから行う。
      queueMicrotask(() => receiveFromGraphic(json));
    },
  };
  (globalThis as unknown as { __genesisGraphicBridge: GraphicBridge }).__genesisGraphicBridge = bridge;
}

export function makeEnvelope(source: string, destination: string, event: string, payload: unknown): Envelope {
  return {
    version: "1.0",
    request_id: `msg-${nextMessageId++}`,
    timestamp: String(Math.floor(Date.now() / 1000)),
    source,
    destination,
    event,
    payload,
  };
}

/** Unity へ送る。互換性の不一致が分かったあとは system イベント以外を送らない。 */
export function sendToParent(envelope: Envelope): void {
  if (!envelope.event.startsWith("system:") && state.graphicContractError !== null) throw state.graphicContractError;
  const json = JSON.stringify(envelope);
  if (deliver) {
    deliver(json);
    return;
  }
  const size = new TextEncoder().encode(json).length + 4;
  if (pendingBytes + size > PENDING_CAPACITY) throw "MMF buffer full";
  pending.push(json);
  pendingBytes += size;
}

export function sendUiAction(windowType: string, entity: EntityRef | null, action: string, params: unknown): void {
  sendToParent(makeEnvelope("tauri", "parent", "ui:action", { action, window_type: windowType, entity, params }));
}

function receiveFromGraphic(json: string): void {
  let envelope: Envelope;
  try {
    envelope = JSON.parse(json) as Envelope;
  } catch {
    return;
  }
  if (typeof envelope?.event !== "string") return;
  envelope.payload ??= null;
  handleIncomingMessage(envelope).catch((error) => console.error(`[ipc] ${envelope.event} の処理に失敗しました`, error));
}

/** Unity から届いたイベントを全ウィンドウへ中継するもの。届け先の画面が名前で購読する。 */
const BROADCAST_EVENTS = new Set([
  "edit_path:select_node",
  "edit_path:update_shape",
  "edit_path:select_merge_target",
  "edit_road:toggle_path",
  "edit_road:ended",
  "edit_path:select_split_point",
  "edit_building:select_parcel",
  "edit_building:placement_state",
  "edit_building:select_point",
  "edit_building:invalid_parcel",
  "building_construction:copy_source",
  "edit_building:update_direct_angle",
  "edit_parcel:select_point",
  "edit_parcel:select_path",
  "edit_parcel:select_parcel",
  "edit_parcel:set_mode",
  "edit_parcel:update_shape",
  "edit_parcel:update_rotation",
  "edit_resource:update_shape",
  "edit_grading:update_shape",
  "edit_transit:update_points",
  "edit_zone:selected",
  "edit_zone:request_merge",
  "edit_prop:update_instances",
  "edit_prop:update_shape",
  "edit_prop:placement_settings",
  "view:current_state",
  "view:object_filter_state",
  "view:grid_angle_changed",
  "view:grid_toggled",
  "camera:state",
  "citizen:follow_state",
  "camera:thumbnail_captured",
  "asset_manager:models",
  "asset_manager:thumbnail",
]);

/** 常駐するコックピットだけが扱うもの。 */
const COCKPIT_EVENTS = new Set(["remove_object:request_confirm", "weather:state", "citizen:pick_failed", "drive:request"]);

async function handleIncomingMessage(envelope: Envelope): Promise<void> {
  if (!envelope.event.startsWith("system:") && !state.graphicConnected) return;
  const payload = envelope.payload as Record<string, unknown> | null;
  const event = envelope.event;
  if (BROADCAST_EVENTS.has(event)) {
    emit(event, payload);
    return;
  }
  if (COCKPIT_EVENTS.has(event)) {
    emitTo(LABEL_COCKPIT, event, payload);
    return;
  }
  switch (event) {
    case "world:select_object":
      await openWindow(normalizeOpenWindowPayload(payload) as OpenWindowPayload);
      break;
    case "game:time_update":
      state.gameTime = payload;
      emit("game:time_update", payload);
      break;
    case "tutorial:applied":
      emitTo("tutorial", "tutorial:applied", payload);
      break;
    case "world:roads_changed":
      emit("roads:changed", payload);
      break;
    case "epoch:ready":
      if (state.epochPresentationId !== null && payload?.id === state.epochPresentationId) emit("epoch:ready", payload);
      break;
    case "view:ui_visibility":
      setUiCloaked(!(typeof payload?.visible === "boolean" ? payload.visible : true));
      break;
    case "system:ping":
      sendToParent(makeEnvelope("tauri", "parent", "system:pong", {}));
      break;
    case "system:shutdown":
      requestAppExit();
      break;
    case "system:focus_gained":
      reassertTopmost();
      break;
    case "game:loaded": {
      const loadingId = typeof payload?.loading_id === "number" ? payload.loading_id : null;
      if (state.isLoading && state.loadingStarted && !state.awaitingParameterSetup && loadingId === state.loadingId) {
        state.graphicLoadReady = true;
      }
      break;
    }
    case "system:graphic_ready":
      onGraphicReady(payload);
      break;
    case "system:graphic_stopped":
      setUiCloaked(false);
      state.graphicConnected = false;
      if (DEVELOPMENT && state.isLoading && state.loadingStarted && !state.awaitingParameterSetup) state.graphicLoadReady = true;
      break;
    case "notification:show":
      emit("show-toast", payload);
      break;
    default:
      sendToParent(makeEnvelope("tauri", "parent", "ui:error", { message: `unsupported event: ${event}` }));
  }
}

/**
 * Unity の起動完了。互換性を照合して system:ready で答え、ロード中なら描画を始めさせる。
 * 2回目以降(Unity の起動し直し)だけポップアップを一掃する。初回に一掃すると、Unity より先に
 * 開いていたロード画面などまで閉じてしまう。
 */
function onGraphicReady(payload: Record<string, unknown> | null): void {
  setUiCloaked(false);
  const expected = definition();
  const compatible = payload?.ipc_contract_version === expected.ipc_contract_version && payload?.core_compatible === true;
  state.graphicVersion = typeof payload?.frontend_version === "string" ? payload.frontend_version : null;
  state.graphicContractError = compatible ? null : "IPC_CONTRACT_MISMATCH";
  state.graphicConnected = compatible;
  sendToParent(makeEnvelope("tauri", "parent", "system:ready", { ...expected, accepted: compatible }));
  if (!compatible) {
    emit("compatibility-error", "IPC_CONTRACT_MISMATCH");
    return;
  }
  publishBackendConnection(state.backendHealth.connected);
  publishVerification();

  const isReconnect = state.graphicReadySeen;
  state.graphicReadySeen = true;
  state.graphicConnected = true;
  if (isReconnect) closeAllPopupWindows();
  if (state.isLoading && state.loadingStarted) {
    sendGameStartIpc();
  } else if (!state.isLoading && state.lastSessionId !== null) {
    try {
      beginInitialLoading(false);
      sendGameStartIpc();
    } catch (error) {
      console.error("[ipc] 初期ロードを始められません", error);
    }
  }
  reassertTopmost();
  emit("graphic-reconnected", payload);
}
