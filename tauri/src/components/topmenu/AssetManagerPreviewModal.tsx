import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import {
  AssetManagerPreview,
  IPC_EVENT_ASSET_MANAGER_PREVIEW,
  closeAssetManagerPreview,
  requestAssetManagerPreview,
} from "../../utils/ipc";

export type BuildingPreviewTarget = {
  packId: string;
  row: number;
  name: string;
  model: number;
  thumbnail: string | null | undefined;
};

const DEFAULT_YAW = 35;
const DEFAULT_PITCH = 25;
const DEFAULT_ZOOM = 1;
const MIN_PITCH = 0;
const MAX_PITCH = 85;
const MIN_ZOOM = 0.5;
const MAX_ZOOM = 3.2;
const ZOOM_STEP = 1.18;
const DRAG_SENSITIVITY = 0.45; // 1px ドラッグあたりの回転角(度)
const PREVIEW_WIDTH = 720;
const PREVIEW_HEIGHT = 540;

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

/**
 * アセットマネージャー内の建物モデル拡大プレビューモーダル。
 *
 * ドラッグで視点回転(yaw/pitch)、ホイールまたはボタンで拡大縮小を行い、Unity へ
 * `asset_manager:preview` を送って再描画を受け取る。通信中の変化は最新の1件だけ保留して
 * 応答後に追いかけ、操作中の遅延蓄積を防ぐ。終了時に `asset_manager:preview_close` で
 * Unity 側のキャッシュを解放する。
 */
export function AssetManagerPreviewModal({
  target,
  onClose,
}: {
  target: BuildingPreviewTarget;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const [imageUrl, setImageUrl] = useState<string | null>(target.thumbnail ?? null);
  const [failed, setFailed] = useState(false);
  const [dragging, setDragging] = useState(false);
  const [zoomPercent, setZoomPercent] = useState(100);
  const viewportRef = useRef<HTMLDivElement | null>(null);
  const viewRef = useRef({ yaw: DEFAULT_YAW, pitch: DEFAULT_PITCH, zoom: DEFAULT_ZOOM });
  const dragRef = useRef<{ pointerId: number; x: number; y: number } | null>(null);
  const inFlightRef = useRef(false);
  const pendingRef = useRef(false);
  const requestIdRef = useRef("");
  const disposedRef = useRef(false);

  const sendFrame = useCallback(() => {
    if (disposedRef.current) return;
    if (inFlightRef.current) {
      pendingRef.current = true;
      return;
    }
    inFlightRef.current = true;
    pendingRef.current = false;
    const requestId = crypto.randomUUID();
    requestIdRef.current = requestId;
    const { yaw, pitch, zoom } = viewRef.current;
    void requestAssetManagerPreview({
      requestId,
      packId: target.packId,
      row: target.row,
      model: target.model,
      yaw,
      pitch,
      zoom,
      width: PREVIEW_WIDTH,
      height: PREVIEW_HEIGHT,
    }).catch(() => {
      if (disposedRef.current || requestIdRef.current !== requestId) return;
      inFlightRef.current = false;
      setFailed(true);
      if (pendingRef.current) sendFrame();
    });
  }, [target.packId, target.row, target.model]);

  useEffect(() => {
    disposedRef.current = false;
    inFlightRef.current = false;
    pendingRef.current = false;
    let unlisten: (() => void) | undefined;

    void listen<AssetManagerPreview>(IPC_EVENT_ASSET_MANAGER_PREVIEW, event => {
      const { request_id, file } = event.payload;
      if (disposedRef.current || request_id !== requestIdRef.current) return;
      if (file === null) {
        inFlightRef.current = false;
        setFailed(true);
        if (pendingRef.current) sendFrame();
        return;
      }
      void invoke<string>("read_asset_thumbnail", { file })
        .then(data => {
          if (disposedRef.current || request_id !== requestIdRef.current) return;
          const mime = file.endsWith(".jpg") ? "image/jpeg" : "image/png";
          setImageUrl(`data:${mime};base64,${data}`);
          setFailed(false);
        })
        .catch(() => {
          if (disposedRef.current || request_id !== requestIdRef.current) return;
          setFailed(true);
        })
        .finally(() => {
          if (disposedRef.current || request_id !== requestIdRef.current) return;
          inFlightRef.current = false;
          if (pendingRef.current) sendFrame();
        });
    }).then(fn => {
      if (disposedRef.current) {
        fn();
        return;
      }
      unlisten = fn;
      sendFrame();
    });

    return () => {
      disposedRef.current = true;
      unlisten?.();
      void closeAssetManagerPreview().catch(() => {});
    };
  }, [sendFrame]);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onClose]);

  const applyZoom = useCallback((nextZoom: number) => {
    const clamped = clamp(nextZoom, MIN_ZOOM, MAX_ZOOM);
    if (Math.abs(clamped - viewRef.current.zoom) < 1e-4) return;
    viewRef.current.zoom = clamped;
    setZoomPercent(Math.round(clamped * 100));
    sendFrame();
  }, [sendFrame]);

  useEffect(() => {
    const element = viewportRef.current;
    if (!element) return;
    const onWheel = (event: WheelEvent) => {
      event.preventDefault();
      if (event.deltaY === 0) return;
      const factor = event.deltaY < 0 ? ZOOM_STEP : 1 / ZOOM_STEP;
      applyZoom(viewRef.current.zoom * factor);
    };
    element.addEventListener("wheel", onWheel, { passive: false });
    return () => element.removeEventListener("wheel", onWheel);
  }, [applyZoom]);

  function handlePointerDown(event: React.PointerEvent<HTMLDivElement>) {
    if (event.button !== 0) return;
    event.currentTarget.setPointerCapture(event.pointerId);
    dragRef.current = { pointerId: event.pointerId, x: event.clientX, y: event.clientY };
    setDragging(true);
  }

  function handlePointerMove(event: React.PointerEvent<HTMLDivElement>) {
    const drag = dragRef.current;
    if (!drag || drag.pointerId !== event.pointerId) return;
    const dx = event.clientX - drag.x;
    const dy = event.clientY - drag.y;
    if (dx === 0 && dy === 0) return;
    drag.x = event.clientX;
    drag.y = event.clientY;
    viewRef.current.yaw -= dx * DRAG_SENSITIVITY;
    viewRef.current.pitch = clamp(viewRef.current.pitch + dy * DRAG_SENSITIVITY, MIN_PITCH, MAX_PITCH);
    sendFrame();
  }

  function handlePointerEnd(event: React.PointerEvent<HTMLDivElement>) {
    const drag = dragRef.current;
    if (!drag || drag.pointerId !== event.pointerId) return;
    dragRef.current = null;
    setDragging(false);
  }

  function handleReset() {
    viewRef.current = { yaw: DEFAULT_YAW, pitch: DEFAULT_PITCH, zoom: DEFAULT_ZOOM };
    setZoomPercent(100);
    sendFrame();
  }

  const title = `${target.name} (${target.row} / #${target.model})`;

  return (
    <div className="asset-manager-preview" onMouseDown={onClose}>
      <div
        className="asset-manager-preview__dialog"
        role="dialog"
        aria-modal="true"
        aria-label={title}
        onMouseDown={event => event.stopPropagation()}
      >
        <div className="asset-manager-preview__header">
          <h3 className="asset-manager-preview__title">{title}</h3>
          <button
            type="button"
            className="asset-manager-preview__close"
            aria-label={t("common.close")}
            title={t("common.close")}
            onClick={onClose}
          >
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round">
              <path d="M6 6l12 12M18 6L6 18" />
            </svg>
          </button>
        </div>
        <div
          ref={viewportRef}
          className={`asset-manager-preview__viewport${dragging ? " is-dragging" : ""}`}
          onPointerDown={handlePointerDown}
          onPointerMove={handlePointerMove}
          onPointerUp={handlePointerEnd}
          onPointerCancel={handlePointerEnd}
        >
          {imageUrl
            ? <img src={imageUrl} alt={title} draggable={false} />
            : <span className="asset-manager-preview__status">{failed ? t("assetManager.noImage") : "…"}</span>}
        </div>
        <div className="asset-manager-preview__footer">
          <span className="asset-manager-preview__hint">{t("assetManager.preview.hint")}</span>
          <div className="asset-manager-preview__controls">
            <button
              type="button"
              className="asset-manager-preview__btn"
              onClick={() => applyZoom(viewRef.current.zoom / ZOOM_STEP)}
              disabled={zoomPercent <= Math.round(MIN_ZOOM * 100)}
              aria-label={t("assetManager.preview.zoomOut")}
              title={t("assetManager.preview.zoomOut")}
            >
              −
            </button>
            <span className="asset-manager-preview__zoom">{zoomPercent}%</span>
            <button
              type="button"
              className="asset-manager-preview__btn"
              onClick={() => applyZoom(viewRef.current.zoom * ZOOM_STEP)}
              disabled={zoomPercent >= Math.round(MAX_ZOOM * 100)}
              aria-label={t("assetManager.preview.zoomIn")}
              title={t("assetManager.preview.zoomIn")}
            >
              +
            </button>
            <button type="button" className="asset-manager-preview__btn" onClick={handleReset}>
              {t("assetManager.preview.reset")}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
