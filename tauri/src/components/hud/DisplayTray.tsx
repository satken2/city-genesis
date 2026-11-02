/**
 * Display tray (persistent bottom-left panel)
 *
 * Simplified display controls for toggling frequently used layers.
 * Detailed settings such as color modes and alert type filters belong to `display/DisplayOptionsPanel`; this
 * tray only temporarily hides/shows visible content.
 *
 * Unity (`IpcViewCoordinator`) owns the state. `useDisplayOptions` synchronizes from `view:current_state`.
 * When Unity changes layers itself, for example during road placement, its broadcast updates the tray too.
 */

import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";

import { sendViewQueryState } from "../../utils/ipc";
import { useDisplayOptions } from "../display/useDisplayOptions";
import { useHudSession } from "./useHudSession";
import buildingIcon from "../../assets/images/menu/display-building.svg";
import roadIcon from "../../assets/images/menu/display-road.svg";
import propIcon from "../../assets/images/menu/display-prop.svg";
import parcelIcon from "../../assets/images/menu/display-parcel.svg";
import terrainIcon from "../../assets/images/menu/display-terrain.svg";
import districtIcon from "../../assets/images/menu/display-district.svg";
import planIcon from "../../assets/images/menu/display-plan.svg";
import "./hud.css";
import "./DisplayTray.css";

/** Definition of one tray toggle button */
type DisplayTrayItem = {
  /** Target display layer ID (from `displayConstants.DISPLAY_LAYERS`) */
  layerId: string;
  /** URL of the button's icon image */
  icon: string;
  /** i18n key for the layer name in the tooltip */
  labelKey: string;
  /** Color mode when enabled. All layers use "hidden" when disabled. */
  visibleMode: string;
  /** Tooltip i18n keys for enabled and disabled states */
  onStateKey: string;
  offStateKey: string;
};

/**
 * Array order is the left-to-right button order. The planned-object button follows after a divider.
 * The button count determines DISPLAY_TRAY_WIDTH in `window.rs`; update the width if it changes.
 *
 * Terrain alone toggles normal/contour display instead of visibility. Replace the submode (`contour_on` /
 * `contour_off`), not the color mode, matching the display options panel.
 */
const TRAY_ITEMS: DisplayTrayItem[] = [
  {
    layerId: "building",
    icon: buildingIcon,
    labelKey: "display.layerBuilding",
    visibleMode: "default",
    onStateKey: "display.trayStateVisible",
    offStateKey: "display.trayStateHidden",
  },
  {
    layerId: "road",
    icon: roadIcon,
    labelKey: "display.layerRoad",
    visibleMode: "default",
    onStateKey: "display.trayStateVisible",
    offStateKey: "display.trayStateHidden",
  },
  {
    layerId: "prop",
    icon: propIcon,
    labelKey: "display.layerProp",
    visibleMode: "visible",
    onStateKey: "display.trayStateVisible",
    offStateKey: "display.trayStateHidden",
  },
  {
    layerId: "parcel",
    icon: parcelIcon,
    labelKey: "display.layerParcel",
    visibleMode: "border",
    onStateKey: "display.trayStateVisible",
    offStateKey: "display.trayStateHidden",
  },
  {
    layerId: "terrain",
    icon: terrainIcon,
    labelKey: "display.layerTerrain",
    visibleMode: "default",
    onStateKey: "display.trayStateContour",
    offStateKey: "display.trayStateTerrainNormal",
  },
  {
    layerId: "district",
    icon: districtIcon,
    labelKey: "display.layerDistrict",
    visibleMode: "border_icon",
    onStateKey: "display.trayStateVisible",
    offStateKey: "display.trayStateHidden",
  },
];

/**
 * Layers whose "plan" flag the planned-object button switches together.
 * This is the same flag as the per-layer "show planned" toggles in the display options panel.
 */
const PLAN_LAYER_IDS = ["road", "building"];

type TrayButtonProps = {
  id: string;
  icon: string;
  on: boolean;
  tooltip: string;
  onClick: () => void;
};

function TrayButton({ id, icon, on, tooltip, onClick }: TrayButtonProps) {
  return (
    <button
      id={id}
      className={`hud-icon-btn display-tray-icon${on ? "" : " display-tray-icon--off"}`}
      type="button"
      title={tooltip}
      aria-label={tooltip}
      aria-pressed={on}
      onClick={onClick}
    >
      <img src={icon} alt="" className="hud-icon-img" draggable={false} />
    </button>
  );
}

export function DisplayTray() {
  const { t } = useTranslation();
  const { visible, epochExecuting } = useHudSession();
  const { selections, selectMode, toggleFlag } = useDisplayOptions();

  /**
   * Remember each layer's color mode immediately before hiding it and restore it when shown.
   * Otherwise, hiding a layer from the tray would reset a selected mode such as land price coloring to normal.
   */
  const lastVisibleModeRef = useRef<Record<string, string>>({});

  /**
   * Query Unity's current display state again when the HUD appears at session start.
   * This tray stays mounted from app startup, so `useDisplayOptions` may send its mount-time query before Unity is
   * ready.
   * Also discard remembered color modes from the previous city; Unity resets defaults at load start.
   */
  useEffect(() => {
    if (!visible) return;
    lastVisibleModeRef.current = {};
    sendViewQueryState().catch((err) => {
      console.warn("[DisplayTray] sendViewQueryState failed:", err);
    });
  }, [visible]);

  function isOn(item: DisplayTrayItem): boolean {
    const selection = selections[item.layerId];
    if (!selection) return false;
    if (item.layerId === "terrain") return selection.subMode === "contour_on";
    return selection.mode !== "hidden";
  }

  function handleToggle(item: DisplayTrayItem) {
    const selection = selections[item.layerId];
    if (!selection) return;

    if (item.layerId === "terrain") {
      selectMode(item.layerId, selection.mode, isOn(item) ? "contour_off" : "contour_on");
      return;
    }

    // Preserve submodes (e.g. target type for buildability coloring) across hiding and showing.
    const subMode = selection.subMode ?? undefined;
    if (isOn(item)) {
      lastVisibleModeRef.current[item.layerId] = selection.mode;
      selectMode(item.layerId, "hidden", subMode);
    } else {
      selectMode(item.layerId, lastVisibleModeRef.current[item.layerId] ?? item.visibleMode, subMode);
    }
  }

  const planOn = PLAN_LAYER_IDS.every((layerId) => selections[layerId]?.flags.includes("plan"));

  /** Flip only the layers that differ from the target, so a mixed state (only one layer shown) turns both on. */
  function handlePlanToggle() {
    for (const layerId of PLAN_LAYER_IDS) {
      if ((selections[layerId]?.flags.includes("plan") ?? false) === planOn) toggleFlag(layerId, "plan");
    }
  }

  if (!visible) return null;

  return (
    <div className={`hud-panel display-tray ${epochExecuting ? "hud-disabled" : ""}`}>
      {TRAY_ITEMS.map((item) => {
        const on = isOn(item);
        return (
          <TrayButton
            key={item.layerId}
            id={`display_tray_${item.layerId}`}
            icon={item.icon}
            on={on}
            tooltip={t("display.trayTooltip", {
              name: t(item.labelKey),
              state: t(on ? item.onStateKey : item.offStateKey),
            })}
            onClick={() => handleToggle(item)}
          />
        );
      })}
      <div className="display-tray-divider" />
      <TrayButton
        id="display_tray_plan"
        icon={planIcon}
        on={planOn}
        tooltip={t("display.trayTooltip", {
          name: t("display.trayPlan"),
          state: t(planOn ? "display.trayStateVisible" : "display.trayStateHidden"),
        })}
        onClick={handlePlanToggle}
      />
    </div>
  );
}
