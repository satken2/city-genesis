import { useCallback, useState, useRef, useEffect } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { sendSetGrid, sendChangeState, sendFocusGraphic, IPC_EVENT_VIEW_GRID_ANGLE_CHANGED } from "../../utils/ipc";
import type { GraphicState } from "../../utils/ipc";
import "./GridSettingsPanel.css";

const uiModules = import.meta.glob<{ default: string }>(
  "../../assets/images/ui/*.png",
  { eager: true }
);

function resolveIcon(name: string): string {
  const key = `../../assets/images/ui/${name}.png`;
  return uiModules[key]?.default ?? "";
}

export type GridDisplayMode = "off" | "line";

const SIZE_STEP = 0.5;
const ANGLE_STEP = 15;

type GridSettingsPanelProps = {
  mode: GridDisplayMode;
  size: number;
  angle: number;
  onModeChange: (mode: GridDisplayMode) => void;
  onSizeChange: (size: number) => void;
  onAngleChange: (angle: number) => void;
  onOriginChange?: (origin: { x: number; z: number } | null) => void;
  originalState?: GraphicState;
  originalExtra?: Record<string, unknown>;
  onStartSetAngleMode?: () => void;
  onEndSetAngleMode?: () => void;
};

export function GridSettingsPanel({
  mode,
  size,
  angle,
  onModeChange,
  onSizeChange,
  onAngleChange,
  onOriginChange,
  originalState,
  originalExtra,
  onStartSetAngleMode,
  onEndSetAngleMode,
}: GridSettingsPanelProps) {
  const { t } = useTranslation();
  const [isSelectingNodes, setIsSelectingNodes] = useState(false);
  const unlistenRef = useRef<(() => void) | null>(null);

  const emitGrid = useCallback(
    (nextMode: GridDisplayMode, nextSize: number, nextAngle: number) => {
      sendSetGrid(nextMode, nextSize, nextAngle);
    },
    [],
  );

  const handleModeClick = useCallback(
    (nextMode: GridDisplayMode) => {
      onModeChange(nextMode);
      emitGrid(nextMode, size, angle);
    },
    [onModeChange, size, angle, emitGrid],
  );

  const handleScaleUp = useCallback(() => {
    const next = Math.min(50, size + SIZE_STEP);
    onSizeChange(next);
    emitGrid(mode, next, angle);
  }, [mode, size, angle, onSizeChange, emitGrid]);

  const handleScaleDown = useCallback(() => {
    const next = Math.max(0.5, size - SIZE_STEP);
    onSizeChange(next);
    emitGrid(mode, next, angle);
  }, [mode, size, angle, onSizeChange, emitGrid]);

  const handleRotateLeft = useCallback(() => {
    const next = angle <= 0 ? 360 - ANGLE_STEP : angle - ANGLE_STEP;
    onAngleChange(next);
    emitGrid(mode, size, next);
  }, [mode, size, angle, onAngleChange, emitGrid]);

  const handleRotateRight = useCallback(() => {
    const next = angle >= 360 ? ANGLE_STEP : angle + ANGLE_STEP;
    onAngleChange(next);
    emitGrid(mode, size, next);
  }, [mode, size, angle, onAngleChange, emitGrid]);

  const handleCancelNodeSelection = useCallback(async () => {
    if (unlistenRef.current) {
      unlistenRef.current();
      unlistenRef.current = null;
    }
    setIsSelectingNodes(false);
    if (onEndSetAngleMode) {
      onEndSetAngleMode();
    }
    if (originalState) {
      await sendChangeState(originalState, originalExtra);
      await sendFocusGraphic();
    }
  }, [originalState, originalExtra, onEndSetAngleMode]);

  const handleStartNodeSelection = useCallback(async () => {
    if (!originalState) return;
    setIsSelectingNodes(true);
    if (onStartSetAngleMode) {
      onStartSetAngleMode();
    }

    try {
      // 1. Switch to the STATE_SET_GRID state.
      await sendChangeState("STATE_SET_GRID");
      await sendFocusGraphic(); // Focus Unity.

      // 2. Register the angle-change event listener.
      const appWindow = getCurrentWindow();
      const unlisten = await appWindow.listen<{ angle: number; origin_x?: number; origin_z?: number }>(
        IPC_EVENT_VIEW_GRID_ANGLE_CHANGED,
        async (event) => {
          const nextAngle = Math.round(event.payload.angle);
          onAngleChange(nextAngle);

          if (event.payload.origin_x !== undefined && event.payload.origin_z !== undefined) {
            onOriginChange?.({ x: event.payload.origin_x, z: event.payload.origin_z });
          } else {
            onOriginChange?.(null);
          }

          // 3. Restore the original state.
          await sendChangeState(originalState, originalExtra);
          await sendFocusGraphic(); // Focus Unity again.

          if (onEndSetAngleMode) {
            onEndSetAngleMode();
          }
          setIsSelectingNodes(false);
          if (unlistenRef.current) {
            unlistenRef.current();
            unlistenRef.current = null;
          }
        }
      );
      unlistenRef.current = unlisten;
    } catch (err) {
      console.error("[GridSettingsPanel] Failed to start set grid mode:", err);
      setIsSelectingNodes(false);
      if (onEndSetAngleMode) {
        onEndSetAngleMode();
      }
    }
  }, [originalState, originalExtra, onAngleChange, onOriginChange, onStartSetAngleMode, onEndSetAngleMode]);

  useEffect(() => {
    return () => {
      if (unlistenRef.current) {
        unlistenRef.current();
      }
    };
  }, []);

  return (
    <div className="gs-panel">
      <div className="gs-section-title">{t("grid_settings.title")}</div>

      {/* Mode selection (on/off toggles). */}
      <div className="gs-mode-row">
        <button
          className={`gs-icon-btn ${mode === "off" ? "selected" : ""}`}
          title={t("grid_settings.off")}
          type="button"
          onClick={() => handleModeClick("off")}
          disabled={isSelectingNodes}
        >
          <img src={resolveIcon("invisible")} alt="" className="gs-icon" draggable={false} />
        </button>
        <button
          className={`gs-icon-btn ${mode === "line" ? "selected" : ""}`}
          title={t("grid_settings.line")}
          type="button"
          onClick={() => handleModeClick("line")}
          disabled={isSelectingNodes}
        >
          <img src={resolveIcon("grid")} alt="" className="gs-icon" draggable={false} />
        </button>
      </div>

      {mode !== "off" && !isSelectingNodes && (
        <div className="gs-adjust">
          <div className="gs-adjust-group">
            <button className="gs-sm-btn" title={t("grid_settings.scaleDown")} type="button" onClick={handleScaleDown}>
              <img src={resolveIcon("scale_down")} alt="" className="gs-sm-icon" draggable={false} />
            </button>
            <span className="gs-value-label">{size.toFixed(1)}m</span>
            <button className="gs-sm-btn" title={t("grid_settings.scaleUp")} type="button" onClick={handleScaleUp}>
              <img src={resolveIcon("scale_up")} alt="" className="gs-sm-icon" draggable={false} />
            </button>
          </div>

          <div className="gs-adjust-group">
            <button className="gs-sm-btn" title={t("grid_settings.rotateLeft")} type="button" onClick={handleRotateLeft}>
              <img src={resolveIcon("rotate_l")} alt="" className="gs-sm-icon" draggable={false} />
            </button>
            <span className="gs-value-label">{angle}°</span>
            <button className="gs-sm-btn" title={t("grid_settings.rotateRight")} type="button" onClick={handleRotateRight}>
              <img src={resolveIcon("rotate_r")} alt="" className="gs-sm-icon" draggable={false} />
            </button>
          </div>

          {originalState && (
            <div className="gs-adjust-group">
              <button
                className={`gs-icon-btn ${isSelectingNodes ? "selected" : ""}`}
                title={t("grid_settings.setAngleFromNodes")}
                type="button"
                onClick={isSelectingNodes ? handleCancelNodeSelection : handleStartNodeSelection}
              >
                <svg
                  className="gs-icon gs-vector-icon"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  style={{
                    width: 24,
                    height: 24,
                    color: isSelectingNodes ? "#4ea1ff" : "#c5d4e3",
                  }}
                >
                  <circle cx="12" cy="12" r="10" />
                  <line x1="22" y1="12" x2="18" y2="12" />
                  <line x1="6" y1="12" x2="2" y2="12" />
                  <line x1="12" y1="6" x2="12" y2="2" />
                  <line x1="12" y1="22" x2="12" y2="18" />
                  <circle cx="12" cy="12" r="3" />
                </svg>
              </button>
            </div>
          )}
        </div>
      )}

      {isSelectingNodes && (
        <div className="gs-guidance-container">
          <div className="gs-guidance-text">
            {t("grid_settings.selectingNodesGuidance")}
          </div>
          <button
            className="gs-cancel-btn"
            type="button"
            onClick={handleCancelNodeSelection}
          >
            {t("common.cancel")}
          </button>
        </div>
      )}
    </div>
  );
}
