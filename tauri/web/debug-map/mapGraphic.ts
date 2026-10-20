/**
 * 開発用の地図ページのグラフィック。Unity の代わりに都市の2D地図(debug-map ウィンドウ)を上部バーの下の全面に置く。
 *
 * Web シェルとは Unity と同じ取り決め(`__genesisGraphicBridge` と Envelope)でやり取りし、シェルの処理は PC 向けページと変えない。
 * 地図は自分でデータを取りに行き、描画の準備を待たせる必要がないので、Unity が準備を終えて返す合図
 * (起動・初期描画・エポック前の暗転)はすぐに返す。版はこのページと同じビルドなので、互換性は常に合う。
 * 地図のウィンドウは都市を開くたび(game:start)に作り直す(都市が無い間はデータを取れず、前の都市の表示位置も引き継がないため)。
 * 表示するのは都市を見せる間(game:reveal から game:end・session:reset まで)で、Unity が受ける再読み込みは
 * 同じ名前のイベント(session:reload)で地図へ渡す。地図のタップで開く詳細は、地図がウィンドウを開くコマンドで開く。
 */
import { WINDOW_LABEL_ATTRIBUTE } from "../shared/shellApi";
import { emitTo, removeListenersOf } from "../shell/events";
import { makeEnvelope, type Envelope } from "../shell/ipc";
import { markGraphicLoaded } from "../shell/process";
import { definition } from "../shell/version";
import { DEBUG_FRAME_PAGE, LABEL_DEBUG_MAP } from "./labels";
import { BAR_HEIGHT } from "./layout";

interface GraphicBridge {
  attach(deliver: (json: string) => void): void;
  receive(json: string): void;
}

let container: HTMLElement;
let frame: HTMLIFrameElement | null = null;
let bridge: GraphicBridge;

function send(event: string, payload: unknown): void {
  bridge.receive(JSON.stringify(makeEnvelope("unity", "tauri", event, payload)));
}

function createMapFrame(): HTMLIFrameElement {
  const created = document.createElement("iframe");
  created.setAttribute(WINDOW_LABEL_ATTRIBUTE, LABEL_DEBUG_MAP);
  created.title = "City Map";
  created.className = "debug-map-frame";
  created.style.top = `${BAR_HEIGHT}px`;
  created.style.height = `calc(100% - ${BAR_HEIGHT}px)`;
  created.style.visibility = "hidden";
  created.src = DEBUG_FRAME_PAGE;
  container.appendChild(created);
  return created;
}

function setMapVisible(visible: boolean): void {
  if (frame) frame.style.visibility = visible ? "" : "hidden";
}

function receive(json: string): void {
  const envelope = JSON.parse(json) as Envelope;
  if (envelope.event !== "ui:action") return;
  const { action, params } = (envelope.payload ?? {}) as { action?: string; params?: Record<string, unknown> | null };
  switch (action) {
    case "game:start":
      frame?.remove();
      removeListenersOf(LABEL_DEBUG_MAP);
      frame = createMapFrame();
      send("game:loaded", { loading_id: params?.loading_id });
      break;
    case "game:reveal":
      setMapVisible(true);
      break;
    case "game:end":
    case "session:reset":
      setMapVisible(false);
      break;
    case "epoch:prepare":
      send("epoch:darkened", { id: params?.id });
      break;
    case "session:reload":
    case "session:force_reload":
      emitTo(LABEL_DEBUG_MAP, "session:reload", {});
      break;
  }
}

export async function startMapGraphic(element: HTMLElement): Promise<void> {
  container = element;
  bridge = (globalThis as unknown as { __genesisGraphicBridge: GraphicBridge }).__genesisGraphicBridge;
  bridge.attach(receive);
  send("system:graphic_ready", { ...definition(), core_compatible: true });
  markGraphicLoaded();
}
