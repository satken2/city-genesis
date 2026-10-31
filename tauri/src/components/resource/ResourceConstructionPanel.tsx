import { describeError } from "../../utils/errors";
/**
 * Resource editor panel
 *
 * Dedicated panel for the window_type="resource_edit" popup.
 * Available only in scenario authoring mode (play_mode=PLAY_MODE_SCENARIO); the icon panel button is shown only
 * in that mode (see hud/IconPanel.tsx).
 * Uses the same single-column layout as PathConstructionPanel.
 *
 * Workflow:
 * 1. Select a resource type and send IPC to enter resource area editing (STATE_EDIT_RESOURCE) in the graphics
 *    program.
 * 2. Click terrain to place vertices and create an arbitrary polygon, using ParcelShapeEditManager-style
 *    controls: drag vertices, click edges to add vertices, right-click to delete vertices.
 * 3. Receive polygon updates via IPC (edit_resource:update_shape).
 * 4. Set maximum reserves and recovery rate. Defaults use type-specific presets (data/resourcePresets.ts);
 *    area-proportional presets follow the shape's area.
 * 5. Confirm submits directly to POST /api/v1/resources. As with road construction, Tauri sends to the backend
 *    directly and resets Unity's edit state after success.
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useMasterData } from "../../contexts/MasterDataContext";
import { NumberStepper } from "../common/NumberStepper";
import { ringSignedArea } from "../terrain/riverGeometry";
import { ResourcePresetHint } from "./ResourcePresetHint";
import { useResourcePresetInputs } from "../../hooks/useResourcePresetInputs";
import { RESOURCE_MAX_AMOUNT_LIMIT } from "../../data/resourcePresets";

import { setHintbarText } from "../../utils/hintbar";
import { showNotification } from "../../utils/notification";
import { buildApiUrl, fetchApi } from "../../utils/api";
import {
  sendChangeState,
  sendEditResourceFinish,
  IPC_EVENT_EDIT_RESOURCE_UPDATE_SHAPE,
} from "../../utils/ipc";
import "./ResourceConstructionPanel.css";

type ResourceConstructionPanelProps = {
  apiBaseUrl?: string;
  onClose: () => void;
};

export function ResourceConstructionPanel({ apiBaseUrl, onClose }: ResourceConstructionPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const [selectedType, setSelectedType] = useState<number | null>(null);
  const [polygon, setPolygon] = useState<number[][]>([]);
  const [isConfirming, setIsConfirming] = useState(false);

  // Vertices arrive as [x, z] in meters. Fewer than three points means zero area, leaving area-proportional presets waiting for geometry.
  const areaM2 = polygon.length >= 3 ? Math.abs(ringSignedArea(polygon)) : 0;
  const productId = selectedType !== null ? masterData?.resource_types[selectedType]?.product_id ?? null : null;
  const presetInputs = useResourcePresetInputs(productId, areaM2);
  const { maxAmount, recoveryRate, reset: resetPresetInputs } = presetInputs;

  /* ---- Receive notifications whenever Unity's edited polygon changes ---- */
  useEffect(() => {
    const appWindow = getCurrentWindow();
    let unlisten: (() => void) | undefined;

    appWindow
      .listen<{ polygon: number[][] }>(IPC_EVENT_EDIT_RESOURCE_UPDATE_SHAPE, (event) => {
        setPolygon(event.payload.polygon ?? []);
      })
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);

    return () => {
      unlisten?.();
    };
  }, []);

  /**
   * Resource type selection handler.
   * Notify Unity to enter STATE_EDIT_RESOURCE and allow vertex placement.
   * Changing the type resets the placed polygon, analogous to resetting variants/nodes when changing road types.
   */
  const handleTypeChange = useCallback((e: React.ChangeEvent<HTMLSelectElement>) => {
    const val = e.target.value;
    const typeId = val ? parseInt(val) : null;
    setSelectedType(typeId);
    setPolygon([]);
    if (typeId !== null) {
      sendChangeState("STATE_EDIT_RESOURCE", { resource_type: typeId }, "resource_edit").catch((err) => {
        console.error("[ResourceConstructionPanel] Failed to enter edit mode:", err);
      });
    }
  }, []);

  /* ---- Hint bar text, cleared when the panel closes ---- */
  useEffect(() => {
    setHintbarText(t("hint.resourceEdit")).catch((err) => {
      console.error("[ResourceConstructionPanel] setHintbarText failed:", err);
    });
    return () => {
      void setHintbarText("");
    };
  }, [t]);

  /* ---- Ensure Unity returns to STATE_NORMAL when the component is destroyed ---- */
  useEffect(() => {
    return () => {
      sendChangeState("STATE_NORMAL").catch(() => {});
    };
  }, []);

  const handleCancel = useCallback(() => {
    setSelectedType(null);
    setPolygon([]);
    resetPresetInputs();
    sendChangeState("STATE_NORMAL").catch(() => {});
  }, [resetPresetInputs]);

  /**
   * Confirm button handler.
   * Tauri sends resource data directly to the backend, with the same responsibility split as road construction.
   * On success, notify Unity that editing is finished and close the panel. On failure, show an error and allow
   * retry.
   */
  const handleConfirm = useCallback(async () => {
    if (selectedType === null || polygon.length < 3 || !apiBaseUrl) return;

    setIsConfirming(true);
    try {
      await fetchApi<{ resource: { resource_id: number } }>(
        buildApiUrl(apiBaseUrl, "/api/v1/resources"),
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            resource_type: selectedType,
            max_amount: maxAmount,
            recovery_rate: recoveryRate,
            polygon,
          }),
        }
      );
      await sendEditResourceFinish();
      await sendChangeState("STATE_NORMAL");
      onClose();
    } catch (err) {
      setIsConfirming(false);
      const displayMessage = describeError(err, t);
      void showNotification({ message: displayMessage, durationMs: 8000 });
    }
  }, [selectedType, polygon, maxAmount, recoveryRate, apiBaseUrl, onClose, t]);

  if (!masterData) {
    return <p className="pc-muted">{t("common.loading")}</p>;
  }

  const resourceTypeEntries = Object.entries(masterData.resource_types);

  return (
    <div className="pc-layout">
      {/* Resource type selection */}
      <div className="pc-section">
        <select className="pc-select" value={selectedType ?? ""} onChange={handleTypeChange}>
          <option value="">{t("resource_construction.selectType")}</option>
          {resourceTypeEntries.map(([id, entry]) => (
            <option key={id} value={id}>
              {entry.display_name}
            </option>
          ))}
        </select>
      </div>

      {/* Maximum reserves and recovery rate settings */}
      {selectedType !== null && (
        <div className="pc-section">
          <div className="pc-section-title">{t("resource_construction.settings")}</div>

          {presetInputs.presets.length > 1 && (
            <div className="pc-settings-row">
              <span className="pc-settings-label">{t("resourcePreset.label")}</span>
              <select
                className="pc-select"
                value={presetInputs.preset?.key ?? ""}
                onChange={(e) => presetInputs.selectPreset(e.target.value)}
              >
                {presetInputs.presets.map((preset) => (
                  <option key={preset.key} value={preset.key}>
                    {t(`resourcePreset.presets.${preset.key}.name`)}
                  </option>
                ))}
              </select>
            </div>
          )}

          <div className="pc-settings-row">
            <span className="pc-settings-label">{t("resource_construction.maxAmount")}</span>
            <div className="pc-stepper-row">
              <NumberStepper
                className="number-stepper--wide"
                value={maxAmount}
                onChange={presetInputs.setMaxAmount}
                min={1}
                max={RESOURCE_MAX_AMOUNT_LIMIT}
                step={100}
                integerOnly={true}
              />
            </div>
          </div>

          <div className="pc-settings-row">
            <span className="pc-settings-label">{t("resource_construction.recoveryRate")}</span>
            <div className="pc-stepper-row">
              <NumberStepper
                value={recoveryRate}
                onChange={presetInputs.setRecoveryRate}
                min={0}
                max={1}
                step={0.0001}
                integerOnly={false}
              />
            </div>
          </div>

          <ResourcePresetHint
            preset={presetInputs.preset}
            areaM2={areaM2}
            maxAmount={maxAmount}
            recoveryRate={recoveryRate}
            awaitingShape={presetInputs.awaitingShape}
            className="pc-hint"
          />
        </div>
      )}

      {/* Polygon information */}
      {selectedType !== null && (
        <div className="pc-section">
          <div className="pc-section-title">{t("resource_construction.shapeInfo")}</div>
          {polygon.length === 0 ? (
            <div className="pc-no-nodes-message">{t("resource_construction.noVerticesYet")}</div>
          ) : (
            <div className="pc-info-grid">
              <span className="pc-info-label">{t("resource_construction.vertexCount")}</span>
              <span className="pc-info-value">{polygon.length}</span>
            </div>
          )}
        </div>
      )}

      {/* Action buttons */}
      {selectedType !== null && (
        <div className="pc-section pc-action-bar">
          <div className="pc-actions">
            <button className="pc-btn" type="button" onClick={handleCancel}>
              {t("resource_construction.cancel")}
            </button>
            <button
              className="pc-btn pc-btn-confirm"
              disabled={polygon.length < 3 || isConfirming}
              type="button"
              onClick={handleConfirm}
            >
              {isConfirming ? t("resource_construction.confirming") : t("resource_construction.confirm")}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
