/**
 * ウィンドウ(iframe)の置き場。exe の OS ウィンドウ1枚を、ページ内の枠1つと iframe 1枚で表す。
 * 座標とサイズはページの CSS ピクセルで、表示領域全体をモニター1枚として扱う。
 * どのウィンドウをどこに置くかの規則は windows.ts が持ち、ここは置く・見せる・動かすだけを行う。
 * 開発用の地図ページは置き方を差し替え(setArrangement)、求められた位置ではなく種類ごとの定位置へ置く。
 */
import { WINDOW_LABEL_ATTRIBUTE } from "../shared/shellApi";

export interface Placement {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface CreateOptions {
  url: string;
  placement: Placement;
  visible: boolean;
  title?: string;
  resizable?: boolean;
  /** 透明な部分も含めてクリックを下(3D画面)へ通す。Tauri の set_ignore_cursor_events(true) に当たる。 */
  clickThrough?: boolean;
}

interface HostWindow {
  label: string;
  box: HTMLDivElement;
  frame: HTMLIFrameElement;
  /** 実際に置いている位置とサイズ */
  rect: Placement;
  /** ウィンドウの規則・操作が求めた位置とサイズ。置き方の差し替えが無ければ rect と同じ */
  requested: Placement;
  visible: boolean;
  maximized: boolean;
}

/** 求められた位置とサイズから、実際に置く位置とサイズを決める。 */
export type Arrangement = (label: string, requested: Placement) => Placement;

interface HostHooks {
  /** 枠を破棄した(Tauri の WindowEvent::Destroyed)。 */
  destroyed(label: string): void;
  /** 位置かサイズが変わった(Tauri の WindowEvent::Moved / Resized)。moved は位置が変わったか。 */
  geometryChanged(label: string, moved: boolean): void;
}

/** OS のウィンドウの最小サイズに当たる、ユーザーの操作で縮められる下限。 */
const MIN_WIDTH = 120;
const MIN_HEIGHT = 40;
const RESIZE_EDGES = ["n", "s", "e", "w", "ne", "nw", "se", "sw"] as const;

const windows = new Map<string, HostWindow>();
let layer: HTMLElement;
let hooks: HostHooks;
let zOrder = 0;
let arrangement: Arrangement | null = null;

/**
 * iframe は1枚ずつ読み込む。ブラウザは1ページの同時リクエスト数に上限があり、開発サーバーでは iframe ごとに
 * モジュールを数百件取得するので、起動時の常駐ウィンドウをまとめて読み込むと上限を超えて取得に失敗する。
 */
const loadQueue: { frame: HTMLIFrameElement; url: string }[] = [];
let loadingFrame: HTMLIFrameElement | null = null;

function requestLoad(frame: HTMLIFrameElement, url: string): void {
  loadQueue.push({ frame, url });
  loadNext();
}

function loadNext(): void {
  while (loadingFrame === null && loadQueue.length > 0) {
    const next = loadQueue.shift()!;
    if (!next.frame.isConnected) continue;
    loadingFrame = next.frame;
    const done = () => {
      next.frame.removeEventListener("load", done);
      if (loadingFrame === next.frame) loadingFrame = null;
      loadNext();
    };
    next.frame.addEventListener("load", done);
    next.frame.src = next.url;
  }
}

export function initHost(element: HTMLElement, callbacks: HostHooks): void {
  layer = element;
  hooks = callbacks;
}

/**
 * ウィンドウの置き方を差し替える。開発用の地図ページは、ウィンドウの規則や操作が求めた位置によらず種類ごとの定位置へ置く。
 * 差し替えた置き方は表示領域の大きさで決まるので、表示領域が変わったら rearrange で置き直す。
 */
export function setArrangement(arrange: Arrangement): void {
  arrangement = arrange;
}

export function rearrange(): void {
  for (const entry of windows.values()) setRect(entry, entry.requested);
}

/** 表示領域。exe のモニター1枚に当たる。 */
export function monitor(): Placement {
  return { x: 0, y: 0, width: layer.clientWidth || window.innerWidth, height: layer.clientHeight || window.innerHeight };
}

export function createWindow(label: string, options: CreateOptions): void {
  const box = document.createElement("div");
  box.className = "shell-window";
  if (options.clickThrough) box.classList.add("shell-window--click-through");
  const frame = document.createElement("iframe");
  frame.setAttribute(WINDOW_LABEL_ATTRIBUTE, label);
  frame.title = options.title ?? label;
  box.appendChild(frame);
  const entry: HostWindow = { label, box, frame, rect: options.placement, requested: options.placement, visible: options.visible, maximized: false };
  windows.set(label, entry);
  setRect(entry, options.placement);
  box.style.display = options.visible ? "" : "none";
  if (options.visible) raise(label);
  layer.appendChild(box);
  setResizable(label, options.resizable === true);
  requestLoad(frame, options.url);
}

export function hasWindow(label: string): boolean {
  return windows.has(label);
}

export function labels(): string[] {
  return [...windows.keys()];
}

/** OS のウィンドウと同じく、非表示から表示に変わったときだけ前面に出す(表示中への再要求では重なり順を変えない)。 */
export function show(label: string): void {
  const entry = windows.get(label);
  if (!entry || entry.visible) return;
  entry.visible = true;
  entry.box.style.display = "";
  raise(label);
}

export function hide(label: string): void {
  const entry = windows.get(label);
  if (!entry) return;
  entry.visible = false;
  entry.box.style.display = "none";
}

export function isVisible(label: string): boolean {
  return windows.get(label)?.visible === true;
}

export function rect(label: string): Placement | null {
  const entry = windows.get(label);
  return entry ? { ...entry.rect } : null;
}

export function place(label: string, placement: Placement): void {
  const entry = windows.get(label);
  if (!entry) return;
  const { x, y } = entry.rect;
  setRect(entry, placement);
  hooks.geometryChanged(label, entry.rect.x !== x || entry.rect.y !== y);
}

export function setSize(label: string, width: number, height: number): void {
  const entry = windows.get(label);
  if (entry) place(label, { x: entry.requested.x, y: entry.requested.y, width, height });
}

export function moveBy(label: string, dx: number, dy: number): void {
  const entry = windows.get(label);
  if (!entry || entry.maximized) return;
  setRect(entry, { ...entry.requested, x: entry.requested.x + dx, y: entry.requested.y + dy });
}

/** ドラッグでの移動を終えたときに、位置の変化を一度だけ知らせる。 */
export function moveEnd(label: string): void {
  if (windows.has(label)) hooks.geometryChanged(label, true);
}

export function raise(label: string): void {
  const entry = windows.get(label);
  if (entry) entry.box.style.zIndex = String(++zOrder);
}

/** 前面に出して入力を受けるようにする(Tauri の set_focus)。 */
export function focus(label: string): void {
  const entry = windows.get(label);
  if (!entry) return;
  raise(label);
  entry.frame.contentWindow?.focus();
}

export function destroy(label: string): void {
  const entry = windows.get(label);
  if (!entry) return;
  windows.delete(label);
  entry.box.remove();
  if (loadingFrame === entry.frame) {
    loadingFrame = null;
    loadNext();
  }
  hooks.destroyed(label);
}

export function setTitle(label: string, title: string): void {
  const entry = windows.get(label);
  if (entry) entry.frame.title = title;
}

export function isMaximized(label: string): boolean {
  return windows.get(label)?.maximized === true;
}

export function maximize(label: string): void {
  const entry = windows.get(label);
  if (!entry) return;
  entry.maximized = true;
  setRect(entry, monitor());
}

export function unmaximize(label: string): void {
  const entry = windows.get(label);
  if (entry) entry.maximized = false;
}

/** 表示領域が変わったら、最大化中のウィンドウを合わせ直す。 */
export function refitMaximized(): void {
  for (const entry of windows.values()) {
    if (entry.maximized) setRect(entry, monitor());
  }
}

/** 表示状態を変えずに全ウィンドウを隠す・戻す(DWM のクローキングに当たる)。 */
export function setCloaked(cloaked: boolean): void {
  layer.style.visibility = cloaked ? "hidden" : "";
}

export function hideAll(): void {
  for (const label of windows.keys()) hide(label);
}

/** ユーザーが端をつかんで大きさを変えられるか(Tauri の resizable)。つまみは枠の外側に置き、中身を覆わない。 */
export function setResizable(label: string, resizable: boolean): void {
  const entry = windows.get(label);
  if (!entry) return;
  for (const handle of entry.box.querySelectorAll(".shell-window__resize")) handle.remove();
  if (!resizable) return;
  for (const edge of RESIZE_EDGES) {
    const handle = document.createElement("div");
    handle.className = `shell-window__resize shell-window__resize--${edge}`;
    handle.addEventListener("pointerdown", (event) => startResize(entry, edge, event));
    entry.box.appendChild(handle);
  }
}

function startResize(entry: HostWindow, edge: (typeof RESIZE_EDGES)[number], event: PointerEvent): void {
  if (event.button !== 0 || entry.maximized) return;
  event.preventDefault();
  const handle = event.currentTarget as HTMLElement;
  handle.setPointerCapture(event.pointerId);
  raise(entry.label);
  const start = { ...entry.rect };
  const origin = { x: event.clientX, y: event.clientY };
  const move = (moveEvent: PointerEvent) => {
    const dx = moveEvent.clientX - origin.x;
    const dy = moveEvent.clientY - origin.y;
    const next = { ...start };
    if (edge.includes("e")) next.width = Math.max(MIN_WIDTH, start.width + dx);
    if (edge.includes("s")) next.height = Math.max(MIN_HEIGHT, start.height + dy);
    if (edge.includes("w")) {
      next.width = Math.max(MIN_WIDTH, start.width - dx);
      next.x = start.x + start.width - next.width;
    }
    if (edge.includes("n")) {
      next.height = Math.max(MIN_HEIGHT, start.height - dy);
      next.y = start.y + start.height - next.height;
    }
    setRect(entry, next);
  };
  const end = () => {
    handle.removeEventListener("pointermove", move);
    handle.removeEventListener("pointerup", end);
    handle.removeEventListener("pointercancel", end);
    hooks.geometryChanged(entry.label, entry.rect.x !== start.x || entry.rect.y !== start.y);
  };
  handle.addEventListener("pointermove", move);
  handle.addEventListener("pointerup", end);
  handle.addEventListener("pointercancel", end);
}

function setRect(entry: HostWindow, requested: Placement): void {
  entry.requested = { ...requested };
  entry.rect = arrangement ? arrangement(entry.label, entry.requested) : { ...requested };
  const { x, y, width, height } = entry.rect;
  entry.box.style.left = `${x}px`;
  entry.box.style.top = `${y}px`;
  entry.box.style.width = `${width}px`;
  entry.box.style.height = `${height}px`;
}
