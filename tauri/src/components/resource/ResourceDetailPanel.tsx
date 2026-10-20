/**
 * Natural resource detail panel
 *
 * Dedicated panel for the window_type="resource_detail" popup.
 * Show the selected natural resource's icon/basic information card and remaining quantity/extraction history
 * charts.
 *
 * Layout:
 * 1. Information card: icon, resource name, current/maximum quantity, recovery quantity resolved from master
 *    data.
 * 2. Remaining quantity chart: current_amount history with a max_amount reference line.
 * 3. Extraction chart: consumption_amount history with a recovery-per-epoch reference line.
 *
 * Trim trailing zero padding from the API's newest-first arrays, then reverse() to plot oldest to newest.
 */
import { PLAY_MODE_SCENARIO } from "../../constants";


import { useEffect, useMemo, useState, useRef } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { invoke } from "@tauri-apps/api/core";
import type { ResourceData } from "../../types/generated";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { usePlayMode } from "../../hooks/usePlayMode";
import { resolveResourceIcon } from "../../utils/resourceIcon";
import { useMasterData } from "../../contexts/MasterDataContext";
import { TimeSeriesChart } from "../common/TimeSeriesChart";
import { CameraFocusButton } from "../common/CameraFocusButton";
import {
  sendCameraFocus,
  sendChangeDisplay,
  sendViewQueryState,
  IPC_EVENT_VIEW_CURRENT_STATE,
} from "../../utils/ipc";
import "./ResourceDetailPanel.css";

type ResourceDetailPanelProps = {
  apiBaseUrl: string;
  pathTemplate: string;
  initialEntityId: string;
};

/**
 * Remove trailing zero padding from the API's newest-first arrays.
 * Slots exceeding the number of executed epochs are zero-filled, which would otherwise keep most of the chart
 * near zero.
 */
function trimTrailingZeros(arr: number[]): number[] {
  let end = arr.length;
  while (end > 0 && arr[end - 1] === 0) end--;
  return arr.slice(0, end);
}

export function ResourceDetailPanel({
  apiBaseUrl,
  pathTemplate,
  initialEntityId,
}: ResourceDetailPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const scenarioMode = usePlayMode(apiBaseUrl) === PLAY_MODE_SCENARIO;
  const [resourceData, setResourceData] = useState<ResourceData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const url = buildApiUrl(apiBaseUrl, pathTemplate, initialEntityId);

  // Ref retaining the original natural resource display mode ("hidden" | "visible" | null).
  // Remember the mode on opening and restore it when closing (unmounting).
  const originalResourceModeRef = useRef<string | null>(null);
  
  // Whether display mode has already been switched to visible.
  // Track this so the switch happens only once on mount.
  const isSwitchedRef = useRef<boolean>(false);

  // On mount, retrieve the current natural resource display mode and make resources visible automatically.
  // On unmount, restore hidden mode if that was the original state.
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    const appWindow = getCurrentWindow();
    let cancelled = false;

    const setup = async () => {
      unlisten = await appWindow.listen<{ layers: Record<string, { mode: string }> }>(
        IPC_EVENT_VIEW_CURRENT_STATE,
        (event) => {
          const resourceState = event.payload?.layers?.resource;
          if (resourceState && originalResourceModeRef.current === null && !isSwitchedRef.current) {
            originalResourceModeRef.current = resourceState.mode;
            isSwitchedRef.current = true;

            // If natural resources are hidden, switch to visible for the detail view.
            if (resourceState.mode !== "visible") {
              sendChangeDisplay("resource", "visible").catch((err) => {
                console.error("[ResourceDetailPanel] Failed to show resource:", err);
              });
            }
          }
        }
      );

      if (cancelled) {
        unlisten();
        unlisten = undefined;
        return;
      }

      // Query Unity's current display state to determine the initial mode.
      await sendViewQueryState();
    };

    setup().catch((err) => {
      console.warn("[ResourceDetailPanel] setup failed:", err);
    });

    return () => {
      cancelled = true;
      unlisten?.();

      // If resources were hidden before opening, hide them again when the panel closes (unmounts).
      if (originalResourceModeRef.current === "hidden") {
        sendChangeDisplay("resource", "hidden").catch((err) => {
          console.error("[ResourceDetailPanel] Failed to restore resource display:", err);
        });
      }
    };
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    fetchApi<{ resource: ResourceData }>(url, { signal: controller.signal })
      .then((response) => {
        if (!controller.signal.aborted) {
          setResourceData(response.resource);
          setLoading(false);
        }
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : "Unknown error");
          setLoading(false);
        }
      });

    return () => {
      controller.abort();
    };
  }, [url]);

  // Extraction history is per epoch, so multiply recovery by epoch length to compare on the same basis.
  const [simulationDays, setSimulationDays] = useState<number | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    fetchApi<{ city: { simulation_days: number } }>(buildApiUrl(apiBaseUrl, "/api/v1/city/"), {
      signal: controller.signal,
    })
      .then((response) => {
        if (!controller.signal.aborted) setSimulationDays(response.city.simulation_days);
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) console.error("[ResourceDetailPanel] Failed to fetch city:", err);
      });
    return () => controller.abort();
  }, [apiBaseUrl]);

  // Trim trailing zeros, then order chart data from oldest to newest.
  const reserveSeries = useMemo(
    () => (resourceData ? trimTrailingZeros([...resourceData.current_amount]).reverse() : []),
    [resourceData]
  );
  const consumptionSeries = useMemo(
    () => (resourceData ? trimTrailingZeros([...resourceData.consumption_amount]).reverse() : []),
    [resourceData]
  );

  if (loading) {
    return <p className="resource-detail__loading">{t("common.loading")}</p>;
  }

  if (error || !resourceData) {
    return (
      <div className="resource-detail__error">
        <strong>{t("common.fetchError")}</strong>
        <p>{error}</p>
      </div>
    );
  }

  const iconSrc = resolveResourceIcon(resourceData.product_id);
  const productMaster = masterData?.products[resourceData.product_id];
  const displayName = productMaster?.display_name ?? t("resource.unknownResource");
  const unit = productMaster?.unit ?? "";

  const formatWithUnit = (value: number): string =>
    `${Math.round(value).toLocaleString()} ${unit}`;

  // Remaining quantity history is oldest-first, so the last value is the latest quantity.
  const latestReserve =
    reserveSeries.length > 0 ? reserveSeries[reserveSeries.length - 1] : null;
  const reserveRatioPct =
    resourceData.max_amount > 0 && latestReserve != null
      ? Math.round((latestReserve / resourceData.max_amount) * 100)
      : null;

  /** Daily recovery = maximum quantity × recovery rate */
  const recoveryAmount = resourceData.max_amount * resourceData.recovery_rate;
  // Recovery in one epoch cannot exceed maximum capacity; it stops when full.
  const recoveryPerEpoch =
    simulationDays != null ? Math.min(resourceData.max_amount, recoveryAmount * simulationDays) : null;

  return (
    <div className="resource-detail">
      {/* ===== Information card ===== */}
      <div className="resource-detail__card">
        {iconSrc && (
          <img
            src={iconSrc}
            alt=""
            className="resource-detail__card-icon"
            draggable={false}
          />
        )}
        <div className="resource-detail__card-body">
          <div className="resource-detail__card-name">{displayName}</div>
          <div className="resource-detail__card-stats">
            <span className="resource-detail__card-label">
              {t("resource.currentAmount")}
            </span>
            <span className="resource-detail__card-value">
              {latestReserve != null ? formatWithUnit(latestReserve) : "—"}
            </span>

            <span className="resource-detail__card-label">
              {t("resource.maxAmount")}
            </span>
            <span className="resource-detail__card-value">
              {formatWithUnit(resourceData.max_amount)}
            </span>

            <span className="resource-detail__card-label">
              {t("statistics.resourceReserveRatio")}
            </span>
            <span className="resource-detail__card-value">
              {reserveRatioPct != null ? `${reserveRatioPct}%` : "—"}
            </span>

            <span className="resource-detail__card-label">
              {t("statistics.resourceRecoveryPerDay")}
            </span>
            <span className="resource-detail__card-value">
              {formatWithUnit(recoveryAmount)}
            </span>
          </div>
        </div>
        <CameraFocusButton
          onClick={() => void sendCameraFocus("resource_detail", resourceData.polygon).catch(console.error)}
        />
      </div>

      {/* ===== Remaining quantity history chart ===== */}
      <section className="resource-detail__chart">
        <h3 className="resource-detail__chart-title">
          {t("resource.amountTrend")}
        </h3>
        <TimeSeriesChart
          data={reserveSeries}
          color="#4ea1ff"
          height={180}
          referenceLine={{
            value: resourceData.max_amount,
            label: `${t("resource.maxAmount")}: ${resourceData.max_amount.toLocaleString()} ${unit}`,
          }}
          formatter={formatWithUnit}
        />
      </section>

      {/* ===== Extraction history chart ===== */}
      <section className="resource-detail__chart">
        <h3 className="resource-detail__chart-title">
          {t("resource.consumptionTrend")}
        </h3>
        <TimeSeriesChart
          data={consumptionSeries}
          color="#f87171"
          height={180}
          referenceLine={
            recoveryPerEpoch != null
              ? {
                  value: Math.round(recoveryPerEpoch),
                  label: `${t("resource.recoveryAmount")}: ${Math.round(recoveryPerEpoch).toLocaleString()} ${unit}`,
                }
              : undefined
          }
          formatter={formatWithUnit}
        />
      </section>

      {scenarioMode && (
        <button
          className="property-edit-open-btn"
          type="button"
          onClick={() => {
            void invoke("open_registered_window", {
              windowType: "property_edit",
              params: { extra: { entityType: "resource", entityId: String(resourceData.resource_id) } },
            });
          }}
        >
          {t("property_edit.openEditor", "Edit properties")}
        </button>
      )}
    </div>
  );
}
