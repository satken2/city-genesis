/**
 * Cockpit (persistent panel at the bottom center)
 *
 * Combines the most frequently used information and controls:
 * [common tools] [public service adequacy] [city name/date / realized climate / score/budget] [alerts] [start
 * epoch].
 *
 * - Tracking list: open citizens added through Track in the citizen panel. This persistent window also shows
 *   notifications explaining why a citizen could not be identified after clicking a car or pedestrian in the
 *   paused 3D view (`citizen:pick_failed`).
 * - Common tools: a 2×2 grid with display options/city map above and tracking list/bulldozer below.
 *   Bulldozer enters Unity's removal state without opening a window, mutually exclusive with district editing in
 *   the icon panel (`hudMode`, shared across windows).
 *   While enabled, use construction-site yellow/black stripes (`cockpit--bulldozer`) across the panel to prevent
 *   accidental deletion after forgetting to exit.
 *   In scenario authoring mode (scenarioMode), clicking buildings/roads deletes them immediately without
 *   confirmation or undo. Normally, only vacant land, planned buildings/roads, and terrain props are eligible.
 * - Public service adequacy: vertical bars show electricity/water demand against the smaller of service and
 *   high-voltage goods capacity. Click to open public facilities.
 * - Realized climate: temperature, precipitation, sunlight, and wind speed of the most recently started epoch in
 *   a 2×2 layout. Click to open climate settings in scenario authoring mode, or the climate window otherwise.
 * - Alerts: L1 notifications use two severity levels. One or more warnings (city functions are deteriorating and
 *   action can be taken now) trigger blinking and a red badge. Cautions (not immediately solvable, or affecting
 *   only growth/spare capacity) use a yellow badge without blinking. Persistent blinking for nonurgent issues
 *   would weaken the signal for real crises. Blinking is sufficient to attract attention, so no interrupting
 *   toast is shown.
 *
 * This is the only HUD window guaranteed to exist once, so it owns BGM playback and opening windows for 3D
 * object selections, preventing duplicate playback/opening.
 * It also handles the 3D view's `drive:request` so the selection screen receives text in the current language.
 */
import { PLAY_MODE_SCENARIO } from "../../constants";


import { useCallback, useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useTranslation } from "react-i18next";

import {
  sendChangeState,
  openDriveMode,
  sendRemoveObjectConfirmResult,
  IPC_EVENT_REMOVE_OBJECT_REQUEST_CONFIRM,
  IPC_EVENT_WORLD_SELECT_OBJECT,
  IPC_EVENT_CITIZEN_PICK_FAILED,
  IPC_EVENT_DRIVE_REQUEST,
} from "../../utils/ipc";
import { clearFollowedCitizen, listenFollowState } from "../../utils/citizenTracking";
import { showNotification } from "../../utils/notification";
import { useMasterData } from "../../contexts/MasterDataContext";
import { setHintbarText } from "../../utils/hintbar";
import { formatEpochDate } from "../../utils/date";
import { resolveIntlLocale } from "../../utils/locale";
import { BgmManager } from "../../utils/bgm";
import { requestConfirm } from "../../utils/confirmDialog";
import {
  fetchL1Notifications,
  isCautionNotification,
  type L1Notification,
} from "../../utils/cityNotifications";
import { BudgetMiniMeter } from "./BudgetMiniMeter";
import { PublicServiceGauges } from "./PublicServiceGauges";
import { ScoreMiniMeter } from "./ScoreMiniMeter";
import { TOOL_ICONS, iconUrl, openRegisteredWindow, type HudIconDef } from "./hudCommon";
import { setHudMode, useHudMode } from "./hudMode";
import { useHudSession } from "./useHudSession";
import { useWeather } from "./useWeather";
import { WeatherStatus } from "./WeatherStatus";
import { CLIMATE_ELEMENTS, ClimateIcon, formatClimateValue, type ClimateValues } from "../climate/climateDisplay";
import "./hud.css";
import "./Cockpit.css";

/** Polling interval for L1 (major) notifications in milliseconds. A longer interval suffices because notifications mainly change during epoch execution. */
const L1_NOTIFICATION_POLL_INTERVAL_MS = 30_000;

export function Cockpit() {
  const { t, i18n } = useTranslation();
  const { visible, apiBaseUrl, isLocked, populationLimitReached, cityName, currentDate, epochExecuting, playMode,
    epochTemperature, epochPrecipitation, epochSunlight, epochWindSpeed } = useHudSession();
  const scenarioMode = playMode === PLAY_MODE_SCENARIO;
  const { weather, failed: weatherFailed, select: selectWeather } = useWeather(visible, scenarioMode);
  const mode = useHudMode();
  const panelRef = useRef<HTMLDivElement | null>(null);
  // Used to show the prop type name in the bulldozer deletion confirmation.
  const masterData = useMasterData();

  // Currently active L1 notifications (major events requiring immediate action), used for badges and blinking.
  const [l1Notifications, setL1Notifications] = useState<L1Notification[]>([]);

  // Report only content width changes caused by language, date, or font size, keeping transparent margins from blocking the 3D view.
  useEffect(() => {
    const element = panelRef.current;
    if (!element) return;
    let lastSent = 0;
    const observer = new ResizeObserver(() => {
      const width = Math.ceil(element.getBoundingClientRect().width);
      if (width <= 0 || width === lastSent) return;
      lastSent = width;
      void invoke("resize_cockpit", { width }).catch(console.error);
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, [visible]);

  // Manage the BGM controller lifecycle.
  useEffect(() => {
    const bgm = new BgmManager();
    return () => {
      bgm.destroy();
    };
  }, []);

  // Receive object selection events from the graphics program and open the corresponding detail window.
  useEffect(() => {
    const appWindow = getCurrentWindow();
    let unlistenSelect: (() => void) | undefined;

    appWindow
      .listen<{ window_type: string; entity: { type: string; id: string } }>(
        IPC_EVENT_WORLD_SELECT_OBJECT,
        (event) => {
          const payload = event.payload;
          if (payload.window_type === "zone_detail" && payload.entity?.type === "zone") {
            void invoke("open_registered_window", {
              windowType: "zone_detail",
              entityId: payload.entity.id,
            });
          }
        },
      )
      .then((fn) => {
        unlistenSelect = fn;
      })
      .catch(console.error);

    return () => {
      unlistenSelect?.();
    };
  }, []);

  /**
   * Confirmed bulldozer deletion. Unity asks this persistent window before removing area-placed props (such as
   * forests).
   * Removing an entire scatter area is irreversible, so show confirmation and always return an answer, including
   * cancellation.
   * Otherwise Unity remains pending and rejects the next click on area-placed props.
   */
  useEffect(() => {
    const appWindow = getCurrentWindow();
    let unlisten: (() => void) | undefined;

    appWindow
      .listen<{ object_type: string; id: number; prop_type?: number }>(
        IPC_EVENT_REMOVE_OBJECT_REQUEST_CONFIRM,
        async (event) => {
          const payload = event.payload;
          if (!payload?.object_type || payload.id == null) return;
          const name =
            payload.prop_type != null
              ? masterData?.prop_types?.[payload.prop_type]?.display_name ?? String(payload.prop_type)
              : "";
          // Cancel and closing with × also return false, ensuring Unity's pending confirmation is cleared.
          const accepted = await requestConfirm({
            message: t("taskbar.bulldozerConfirmScatter", { name }),
            confirmLabel: t("common.delete"),
            tone: "danger",
          });
          void sendRemoveObjectConfirmResult(payload.object_type, payload.id, accepted).catch(console.error);
        },
      )
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);

    return () => {
      unlisten?.();
    };
  }, [masterData, t]);

  // Clear the shared followed-citizen state at app startup (Unity also restarts with no citizen being followed),
  // then keep receiving updates even while the citizen panel and tracking list are closed.
  useEffect(() => {
    clearFollowedCitizen();
    return listenFollowState();
  }, []);

  /** Notify why a citizen could not be identified after clicking a car or pedestrian while paused. */
  useEffect(() => {
    const appWindow = getCurrentWindow();
    let unlisten: (() => void) | undefined;
    appWindow
      .listen<{ status: string }>(IPC_EVENT_CITIZEN_PICK_FAILED, (event) => {
        const fallback = t("citizenTracking.pickFailed.error");
        void showNotification({
          message: t(`citizenTracking.pickFailed.${event.payload?.status ?? "error"}`, { defaultValue: fallback }),
          durationMs: 5000,
        }).catch(console.error);
      })
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);
    return () => {
      unlisten?.();
    };
  }, [t]);

  /**
   * Start request from the 3D drive button. Exit persistent bulldozer/district editing modes, then start with
   * selection text in the current language.
   * Reject requests until City Hall has been placed.
   */
  useEffect(() => {
    const appWindow = getCurrentWindow();
    let unlisten: (() => void) | undefined;
    appWindow
      .listen(IPC_EVENT_DRIVE_REQUEST, () => {
        if (isLocked) return;
        void (async () => {
          if (mode !== null) {
            await sendChangeState("STATE_NORMAL", undefined, "cockpit");
            await setHudMode(null);
            await setHintbarText("");
          }
          await openDriveMode(t("drive", { returnObjects: true }) as Record<string, string>);
        })().catch(console.error);
      })
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);
    return () => {
      unlisten?.();
    };
  }, [isLocked, mode, t]);

  // Clear notifications on session disconnection so reloading a different city
  // does not retain the previous city's count badge.
  useEffect(() => {
    if (visible) return;
    setL1Notifications([]);
  }, [visible]);

  /**
   * Poll L1 (major) notifications.
   * - When at least one notification exists, blink the alert icon and show the count badge; click the icon to read
   *   the list.
   * - When epoch execution finishes (epochExecuting: true→false), rerun this effect to apply the latest state
   *   immediately. Stop polling during execution.
   */
  useEffect(() => {
    if (!apiBaseUrl || epochExecuting) return;
    const controller = new AbortController();
    let cancelled = false;

    async function refresh() {
      try {
        const items = await fetchL1Notifications(apiBaseUrl, controller.signal);
        if (cancelled) return;
        setL1Notifications(items);
      } catch {
        // Keep the previous value on retrieval failure (including aborts) to avoid flicker, as in BudgetMiniMeter.
      }
    }

    void refresh();
    const timer = setInterval(() => void refresh(), L1_NOTIFICATION_POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      controller.abort();
      clearInterval(timer);
    };
  }, [apiBaseUrl, epochExecuting]);

  /**
   * Toggle bulldozer (object removal) mode.
   * Enable sends STATE_REMOVE_OBJECTS; disable restores STATE_NORMAL in Unity.
   * In scenario authoring mode (scenarioMode), include instant_remove to delete buildings/roads immediately,
   * regardless of status and without confirmation. The UI supplies this because Unity does not know the play mode.
   * windowType "bulldozer" is only a sender label; no window opens.
   */
  const toggleBulldozer = useCallback(async () => {
    const next = mode !== "bulldozer";
    try {
      await sendChangeState(
        next ? "STATE_REMOVE_OBJECTS" : "STATE_NORMAL",
        next && scenarioMode ? { instant_remove: true } : undefined,
        "bulldozer",
      );
      await setHintbarText(next ? t(scenarioMode ? "hint.bulldozerInstant" : "hint.bulldozer") : "");
      await setHudMode(next ? "bulldozer" : null);
    } catch (error) {
      console.error("failed to toggle bulldozer mode", error);
    }
  }, [mode, t, scenarioMode]);

  const handleToolClick = useCallback(
    async (icon: HudIconDef) => {
      if (icon.id === "bulldozer") {
        await toggleBulldozer();
        return;
      }
      if (icon.windowType) await openRegisteredWindow(icon.windowType);
    },
    [toggleBulldozer],
  );

  if (!visible) return null;

  const locale = resolveIntlLocale(i18n.language);
  const formattedDate = currentDate != null ? formatEpochDate(currentDate, locale) : "—";
  const epochClimate: ClimateValues = {
    temperature: epochTemperature, precipitation: epochPrecipitation, sunlight: epochSunlight, wind_speed: epochWindSpeed,
  };
  // Only warnings (crises with actions available now) trigger blinking and a red badge. Cautions (not immediately
  // solvable or affecting growth only) use a nonblinking yellow badge to preserve the warning signal.
  const cautionCount = l1Notifications.filter(isCautionNotification).length;
  const warningCount = l1Notifications.length - cautionCount;

  return (
    <div
      ref={panelRef}
      className={`hud-panel cockpit ${mode === "bulldozer" ? "cockpit--bulldozer" : ""} ${epochExecuting ? "hud-disabled" : ""}`}
    >
      <div className="cockpit-tools">
        {TOOL_ICONS.map((icon) => {
          const active = icon.id === "bulldozer" && mode === "bulldozer";
          const bulldozerTooltipKey = scenarioMode
            ? (active ? "taskbar.bulldozerInstantOn" : "taskbar.bulldozerInstantOff")
            : (active ? "taskbar.bulldozerOn" : "taskbar.bulldozerOff");
          const tooltip = icon.id === "bulldozer" ? t(bulldozerTooltipKey) : t(icon.tooltipKey);
          return (
            <button
              key={icon.id}
              id={icon.id}
              className={`hud-icon-btn cockpit-tool${active ? " hud-icon-btn--active" : ""}`}
              type="button"
              title={tooltip}
              aria-pressed={icon.id === "bulldozer" ? active : undefined}
              style={{ padding: icon.padding }}
              onClick={() => void handleToolClick(icon)}
            >
              <img src={icon.src} alt={tooltip} className="hud-icon-img" draggable={false} />
            </button>
          );
        })}
      </div>

      <PublicServiceGauges
        apiBaseUrl={apiBaseUrl}
        epochExecuting={epochExecuting}
        onOpen={() => void openRegisteredWindow("public_facilities")}
      />

      <div className="cockpit-status">
        <button
          className="cockpit-city"
          type="button"
          title={t("cityGovernment.tooltip")}
          onClick={() => void openRegisteredWindow("city_government")}
        >
          {cityName || "—"}
        </button>
        <button type="button" className="cockpit-climate"
          disabled={isLocked || epochExecuting} data-guide="hud.climate"
          aria-label={t(scenarioMode ? "climateSettings.title" : "windowType.climate_effects")}
          title={t(scenarioMode ? "climateSettings.title" : "windowType.climate_effects")}
          onClick={() => void openRegisteredWindow(scenarioMode ? "climate_settings" : "climate_effects")}>
          {CLIMATE_ELEMENTS.map(({ key, label }) => <span key={key} className="cockpit-climate-row" title={t(label)}>
            <ClimateIcon element={key} />
            <span>{formatClimateValue(key, epochClimate[key], locale)}</span>
          </span>)}
        </button>
        <ScoreMiniMeter apiBaseUrl={apiBaseUrl} onOpen={() => void openRegisteredWindow("achievements")} />
        <div className="cockpit-date-weather" data-guide="hud.date">
          <span className="cockpit-date">{formattedDate}</span>
          <WeatherStatus weather={weather} editable={scenarioMode} failed={weatherFailed} onSelect={selectWeather} />
        </div>
        <BudgetMiniMeter
          apiBaseUrl={apiBaseUrl}
          onOpen={() => void openRegisteredWindow("city_budget")}
        />
      </div>

      <button
        id="notifications"
        className={`hud-icon-btn cockpit-alert${warningCount > 0 ? " cockpit-alert--active" : ""}`}
        type="button"
        title={t("notifications.trayTooltip")}
        onClick={() => void openRegisteredWindow("city_notifications")}
      >
        <img src={iconUrl("tray_notifications.png")} alt={t("notifications.trayTooltip")} className="hud-icon-img" draggable={false} />
        {warningCount > 0 && <span className="cockpit-alert-badge">{warningCount}</span>}
        {cautionCount > 0 && (
          <span className="cockpit-alert-badge cockpit-alert-badge--caution">{cautionCount}</span>
        )}
      </button>

      <button
        id="execute"
        className="cockpit-execute"
        type="button"
        title={t("taskbar.menuExecute")}
        disabled={isLocked || populationLimitReached}
        onClick={() => void openRegisteredWindow("epoch_confirm")}
      >
        {t("taskbar.menuExecute")}
        <img src={iconUrl("menu_execute.png")} alt="" className="cockpit-execute-img" draggable={false} />
      </button>
    </div>
  );
}
