/**
 * Prop placement panel.
 *
 * Left dock panel with window_type="prop_placement", placing terrain decorations (forest, rocks, shrubs, etc.) on the
 * 3D map. Combines ResourceConstructionPanel area polygons with TransitCreationPanel individual point placement.
 *
 * Two modes:
 *   - Individual (instance): one click places one PropInstance, with rotation/scale settings.
 *   - Area (scatter): draw a polygon and scatter using density/seed (PropScatter; graphics generates individual positions from seed).
 *
 * Flow:
 *   1. Choose individual/area mode.
 *   2. Filter by category and select a prop tile → switch Unity to STATE_EDIT_PROP via IPC.
 *      Individual mode retains provisional placements across type changes, allowing mixed types to be confirmed together.
 *   3. Individual: click in 3D to place (placed objects opaque; cursor-following ghost translucent).
 *      Area: click vertices to draw a polygon (translucent scatter preview).
 *   4. Adjust rotation/scale/height or density/seed, immediately reflected in Unity's preview.
 *      Individual mode also supports middle-button drag for rotation and PageUp/PageDown for height in 3D.
 *      edit_prop:placement_settings writes these results back to steppers without echoing from the panel.
 *   5. Confirm → UI directly POSTs to the backend (same responsibilities as road construction/resource editing).
 *      Individual: POST /api/v1/props/instances; area: POST /api/v1/props/scatters.
 *
 * Tauri/UI sends directly to the backend, then resets Unity placement with edit_prop:finish after success.
 * Delete existing props with the cockpit bulldozer; this panel handles placement only.
 */
import { PLAY_MODE_SCENARIO } from "../../constants";
import { usePlayMode } from "../../hooks/usePlayMode";


import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useMasterData } from "../../contexts/MasterDataContext";
import { NumberStepper } from "../common/NumberStepper";
import { PropTypeSelector } from "./PropTypeSelector";
import { setHintbarText } from "../../utils/hintbar";
import { showNotification } from "../../utils/notification";
import { ApiError, buildApiUrl, fetchApi } from "../../utils/api";
import {
  sendChangeState,
  sendEditPropChangeSettings,
  sendEditPropFinish,
  sendEditPropReset,
  IPC_EVENT_EDIT_PROP_UPDATE_INSTANCES,
  IPC_EVENT_EDIT_PROP_UPDATE_SHAPE,
  IPC_EVENT_EDIT_PROP_PLACEMENT_SETTINGS,
} from "../../utils/ipc";
// Share the basic pc-* layout with the resource editing panel.
import "../resource/ResourceConstructionPanel.css";
import "./PropPlacementPanel.css";

type PropPlacementPanelProps = {
  apiBaseUrl?: string;
  onClose: () => void;
};

/** Placement mode: individual PropInstance or area PropScatter. */
type PlacementMode = "instance" | "scatter";

/** Individual placement instance received from Unity. */
type PropInstance = {
  prop_type: number;
  x: number;
  y: number;
  z: number;
  rotation: number;
  scale: number;
};

const DEFAULT_ROTATION = 0;
const DEFAULT_SCALE = 1.0;
const DEFAULT_DENSITY = 400;
const DEFAULT_SEED = 12345;

export function PropPlacementPanel({ apiBaseUrl, onClose }: PropPlacementPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const scenarioMode = usePlayMode(apiBaseUrl ?? "") === PLAY_MODE_SCENARIO;
  const [mode, setMode] = useState<PlacementMode>("instance");
  const [selectedType, setSelectedType] = useState<number | null>(null);

  // Individual-mode placement settings.
  const [rotation, setRotation] = useState<number>(DEFAULT_ROTATION);
  const [scale, setScale] = useState<number>(DEFAULT_SCALE);
  const [height, setHeight] = useState<number>(0);

  // Area-mode scatter settings.
  const [density, setDensity] = useState<number>(DEFAULT_DENSITY);
  const [seed, setSeed] = useState<number>(DEFAULT_SEED);

  // Placement data from Unity: instance sequence for individual mode; polygon vertices [x,z] for area mode.
  const [instances, setInstances] = useState<PropInstance[]>([]);
  const [polygon, setPolygon] = useState<number[][]>([]);
  const [isShapeFinalized, setIsShapeFinalized] = useState<boolean>(false);

  const [isConfirming, setIsConfirming] = useState(false);

  const propTypeEntries = useMemo(() => {
    if (!masterData) return [];
    return Object.entries(masterData.prop_types);
  }, [masterData]);

  /**
 * Switch Unity to STATE_EDIT_PROP with current mode/settings.
 * Do nothing without a selected type. Changing types within individual mode retains provisional placements.
 */
  const enterEditMode = useCallback(
    (nextMode: PlacementMode, typeId: number | null) => {
      if (typeId === null) return;
      void sendChangeState(
        "STATE_EDIT_PROP",
        {
          prop_type: typeId,
          placement_mode: nextMode,
          height,
          rotation,
          scale,
          density,
          seed,
        },
        "prop_placement",
      );
    },
    [rotation, scale, height, density, seed],
  );

  /* ---- Receive placement data and session state from Unity ---- */
  useEffect(() => {
    const appWindow = getCurrentWindow();
    let unlistenInstances: (() => void) | undefined;
    let unlistenShape: (() => void) | undefined;
    let unlistenSettings: (() => void) | undefined;

    // Apply settings changed in 3D (middle-button rotation, PageUp/PageDown) to steppers.
    // Unity is authoritative; use setters only and do not echo with change_settings.
    appWindow
      .listen<{ rotation?: number; height?: number }>(IPC_EVENT_EDIT_PROP_PLACEMENT_SETTINGS, (event) => {
        const payload = event.payload ?? {};
        if (typeof payload.rotation === "number") setRotation(Math.round(payload.rotation));
        if (typeof payload.height === "number") setHeight(Math.round(payload.height * 100) / 100);
      })
      .then((fn) => {
        unlistenSettings = fn;
      })
      .catch(console.error);

    appWindow
      .listen<{ instances: PropInstance[] }>(IPC_EVENT_EDIT_PROP_UPDATE_INSTANCES, (event) => {
        setInstances(event.payload?.instances ?? []);
      })
      .then((fn) => {
        unlistenInstances = fn;
      })
      .catch(console.error);

    appWindow
      .listen<{ polygon: number[][]; finalized?: boolean }>(IPC_EVENT_EDIT_PROP_UPDATE_SHAPE, (event) => {
        setPolygon(event.payload?.polygon ?? []);
        setIsShapeFinalized(event.payload?.finalized ?? false);
      })
      .then((fn) => {
        unlistenShape = fn;
      })
      .catch(console.error);

    return () => {
      unlistenInstances?.();
      unlistenShape?.();
      unlistenSettings?.();
    };
  }, []);

  // Force individual (instance) mode when scenario mode is off.
  useEffect(() => {
    if (!scenarioMode && mode === "scatter") {
      setMode("instance");
      setInstances([]);
      setPolygon([]);
      setIsShapeFinalized(false);
      enterEditMode("instance", selectedType);
    }
  }, [scenarioMode, mode, selectedType, enterEditMode]);

  /* ---- Hint bar text, cleared when the panel closes ---- */
  useEffect(() => {
    void setHintbarText(t("prop_placement.hint"));
    return () => {
      void setHintbarText("");
    };
  }, [t]);

  /* ---- Always restore Unity to STATE_NORMAL on unmount ---- */
  useEffect(() => {
    return () => {
      void sendChangeState("STATE_NORMAL", {}, "prop_placement");
    };
  }, []);

  const handleModeChange = useCallback(
    (nextMode: PlacementMode) => {
      if (nextMode === mode) return;
      setMode(nextMode);
      setInstances([]);
      setPolygon([]);
      setIsShapeFinalized(false);
      enterEditMode(nextMode, selectedType);
    },
    [mode, selectedType, enterEditMode],
  );

  const handleTypeChange = useCallback(
    (typeId: number) => {
      if (typeId === selectedType || isConfirming) return;
      setSelectedType(typeId);
      if (mode === "scatter") {
        setPolygon([]);
        setIsShapeFinalized(false);
      }
      enterEditMode(mode, typeId);
    },
    [mode, enterEditMode, selectedType, isConfirming],
  );

  /* ---- Apply numeric setting changes immediately to Unity previews ---- */
  const handleRotationChange = useCallback((v: number) => {
    setRotation(v);
    void sendEditPropChangeSettings({ rotation: v });
  }, []);

  const handleScaleChange = useCallback((v: number) => {
    setScale(v);
    void sendEditPropChangeSettings({ scale: v });
  }, []);

  const handleHeightChange = useCallback((v: number) => {
    setHeight(v);
    void sendEditPropChangeSettings({ height: v });
  }, []);

  const handleDensityChange = useCallback((v: number) => {
    setDensity(v);
    void sendEditPropChangeSettings({ density: v });
  }, []);

  const handleSeedChange = useCallback((v: number) => {
    setSeed(v);
    void sendEditPropChangeSettings({ seed: v });
  }, []);

  const handleCancel = useCallback(() => {
    setSelectedType(null);
    setInstances([]);
    setPolygon([]);
    setIsShapeFinalized(false);
    void sendChangeState("STATE_NORMAL", {}, "prop_placement");
  }, []);

  const handleReset = useCallback(async () => {
    setInstances([]);
    setPolygon([]);
    setIsShapeFinalized(false);
    await sendEditPropReset();
  }, []);

  const handleConfirm = useCallback(async () => {
    if (selectedType === null || !apiBaseUrl) return;

    setIsConfirming(true);
    try {
      if (mode === "instance") {
        await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/props/instances"), {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            instances,
          }),
        });
      } else {
        await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/props/scatters"), {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            prop_type: selectedType,
            polygon,
            density,
            seed,
          }),
        });
      }
      await sendEditPropFinish();
      await sendChangeState("STATE_NORMAL", {}, "prop_placement");
      onClose();
    } catch (err) {
      setIsConfirming(false);
      let displayMessage: string;
      if (err instanceof ApiError) {
        const i18nKey = `errors.${err.code}`;
        displayMessage =
          i18nKey !== "errors.UNKNOWN_ERROR"
            ? t(i18nKey, { defaultValue: err.message })
            : err.message;
      } else {
        displayMessage = t("prop_placement.confirmFailedUnknown");
      }
      void showNotification({ message: displayMessage, durationMs: 8000 });
    }
  }, [mode, selectedType, instances, polygon, density, seed, apiBaseUrl, onClose, t]);

  if (!masterData) {
    return <p className="pc-muted">{t("common.loading")}</p>;
  }

  // Confirmation requirements: at least one individual instance, or an area polygon with at least three vertices and provisional confirmation.
  const canConfirm =
    selectedType !== null &&
    (mode === "instance" ? instances.length > 0 : (polygon.length >= 3 && isShapeFinalized));

  return (
    <div className="pc-layout">
      {/* Prop type selection. */}
      <div className="pc-section pp-type-section">
        {propTypeEntries.length === 0 ? (
          <div className="pc-no-nodes-message">{t("prop_placement.noVariants")}</div>
        ) : (
          <PropTypeSelector entries={propTypeEntries} selectedType={selectedType}
            onSelect={handleTypeChange} disabled={isConfirming} />
        )}
      </div>

      {/* Placement mode toggle (individual/area). */}
      {scenarioMode && (
        <div className="pc-section">
          <div className="pc-section-title">{t("prop_placement.modeLabel")}</div>
          <div className="pp-mode-toggle">
            <button
              className={`pp-mode-btn ${mode === "instance" ? "active" : ""}`}
              type="button"
              onClick={() => handleModeChange("instance")}
            >
              {t("prop_placement.modeInstance")}
            </button>
            <button
              className={`pp-mode-btn ${mode === "scatter" ? "active" : ""}`}
              type="button"
              onClick={() => handleModeChange("scatter")}
            >
              {t("prop_placement.modeScatter")}
            </button>
          </div>
        </div>
      )}

      {/* Settings by mode. */}
      {selectedType !== null && (
        <div className="pc-section">
          <div className="pc-section-title">{t("prop_placement.settings")}</div>

          {mode === "instance" ? (
            <>
              <div className="pc-settings-row">
                <span className="pc-settings-label">{t("prop_placement.height")}</span>
                <div className="pc-stepper-row">
                  <NumberStepper
                    value={height}
                    onChange={handleHeightChange}
                    step={0.1}
                    integerOnly={false}
                    label={t("prop_placement.height")}
                  />
                </div>
              </div>
              <div className="pc-settings-row">
                <span className="pc-settings-label">{t("prop_placement.rotation")}</span>
                <div className="pc-stepper-row">
                  <NumberStepper
                    value={rotation}
                    onChange={handleRotationChange}
                    min={0}
                    max={360}
                    step={15}
                    integerOnly={true}
                  />
                </div>
              </div>
              <div className="pc-settings-row">
                <span className="pc-settings-label">{t("prop_placement.scale")}</span>
                <div className="pc-stepper-row">
                  <NumberStepper
                    value={scale}
                    onChange={handleScaleChange}
                    min={0.1}
                    max={5}
                    step={0.1}
                    integerOnly={false}
                  />
                </div>
              </div>
            </>
          ) : (
            <>
              <div className="pc-settings-row">
                <span className="pc-settings-label">{t("prop_placement.density")}</span>
                <div className="pc-stepper-row">
                  <NumberStepper
                    value={density}
                    onChange={handleDensityChange}
                    min={1}
                    max={100000}
                    step={10}
                    integerOnly={true}
                  />
                </div>
              </div>
              <div className="pc-settings-row">
                <span className="pc-settings-label">{t("prop_placement.seed")}</span>
                <div className="pc-stepper-row">
                  <NumberStepper
                    value={seed}
                    onChange={handleSeedChange}
                    min={0}
                    max={2147483647}
                    step={1}
                    integerOnly={true}
                  />
                </div>
              </div>
            </>
          )}
        </div>
      )}


      {/* Action buttons */}
      {selectedType !== null && (
        <div className="pc-section pc-action-bar">
          <div className="pc-actions">
            <button className="pc-btn" type="button" onClick={handleCancel}>
              {t("prop_placement.cancel")}
            </button>
            <button
              className="pc-btn"
              disabled={isConfirming}
              type="button"
              onClick={handleReset}
            >
              {t("prop_placement.reset")}
            </button>
            <button
              className="pc-btn pc-btn-confirm"
              disabled={!canConfirm || isConfirming}
              type="button"
              onClick={handleConfirm}
            >
              {isConfirming ? t("prop_placement.confirming") : t("prop_placement.confirm")}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
