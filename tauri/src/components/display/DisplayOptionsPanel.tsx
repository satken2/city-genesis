import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";
import { resolveDisplayIcon, DISPLAY_LAYERS } from "./displayConstants";
import type { DisplayLayer, DisplayMode } from "./displayConstants";
import type { DisplaySelection } from "./useDisplayOptions";
import { useDisplayOptions } from "./useDisplayOptions";
import { useMasterData } from "../../contexts/MasterDataContext";
import {
  ALERT_AGENT_CONSTRUCTION_SHORTAGE,
  ALERT_AGENT_ON_FIRE,
  ALERT_AGENT_CRIME,
  ALERT_AGENT_INFRA_DISCONNECTED,
  ALERT_AGENT_CAPACITY_TIGHT,
  ALERT_AGENT_NONCONFORMING,
  ALERT_AGENT_ICON_BITS,
  ALERT_AGENT_VFX_BITS,
  ALERT_PATH_CONSTRUCTION_SHORTAGE,
  ALERT_PATH_ACCIDENT,
  ALERT_PATH_NO_STATION_HOUSE,
  ALERT_PATH_ICON_BITS,
  ALERT_PATH_MASK_SHIFT,
  ALERT_DISPLAY_RUIN,
  ALERT_DISPLAY_RESOURCE_WARNING,
  ALERT_DISPLAY_FACILITY_WARNING,
  ALERT_DISPLAY_SANITATION_WARNING,
  ALERT_DISPLAY_INFRA_WARNING,
} from "../../constants";
import { resolveTransitIcon } from "../../utils/pathIcon";
import { resolveAgentVariantIcon } from "../../utils/agentIcon";
import {
  useInfraProducts,
  COVERAGE_IN_RANGE_COLOR,
  COVERAGE_OUT_OF_RANGE_COLOR,
} from "../map/infraOverlay";
import { playSound } from "../../utils/playSound";
import clickSrc from "../../assets/sounds/click.opus";
import "./DisplayOptionsPanel.css";

type DisplayOptionsPanelProps = {
  apiBaseUrl: string;
};

/**
 * Vertical tab order in the display panel.
 * UI order: buildings → roads → parcels → districts → resources → public transport → coverage → props → terrain
 * → alerts.
 */
const TAB_ORDER = [
  "building",
  "road",
  "parcel",
  "district",
  "resource",
  "transit",
  "coverage",
  "prop",
  "terrain",
  "alert",
];

/** Legend type. */
type LegendType = "none" | "color" | "meter";

/**
 * Legend information.
 * Defines the supplementary explanation and legend for each mode.
 */
type LegendInfo = {
  type: LegendType;
  /** Mode description, displayed at the panel's bottom area when present. */
  description?: string;
  /** Color legend entries, used when type is "color". */
  items?: { color: string; label: string }[];
  /** Left endpoint color of the meter legend, used when type is "meter". */
  meterStartColor?: string;
  /** Right endpoint color of the meter legend, used when type is "meter". */
  meterEndColor?: string;
  /**
   * Meter legend label, used when type is "meter".
   * The bar is not mirrored in right-to-left languages, so their translations order the arrows and end values
   * to match the bar's left and right ends.
   */
  meterLabel?: string;
};

/**
 * Resolve legend information from layer and mode IDs.
 * Treat modes without a defined legend as "none" and return placeholder explanatory text.
 */
function getLegendInfo(layerId: string, modeId: string, t: TFunction): LegendInfo {
  if (layerId === "transit") {
    return { type: "none" };
  }

  if (layerId === "coverage") {
    return {
      type: "color",
      items: [
        { color: COVERAGE_IN_RANGE_COLOR, label: t("display.coverageInRange") },
        { color: COVERAGE_OUT_OF_RANGE_COLOR, label: t("display.coverageOutOfRange") },
      ],
    };
  }

  if (layerId === "road") {
    switch (modeId) {
      case "traffic_color":
        return {
          type: "meter",
          description: t("display.descRoadTraffic"),
          meterStartColor: "#ffffff",
          meterEndColor: "#ff4444",
          meterLabel: t("display.meterTraffic"),
        };
      case "speed_color":
        return {
          type: "meter",
          description: t("display.descRoadSpeed"),
          meterStartColor: "#ff0000",
          meterEndColor: "#00ff00",
          meterLabel: t("display.meterSpeed"),
        };
      case "durability_color":
        return {
          type: "meter",
          description: t("display.descRoadDurability"),
          meterStartColor: "#ff4444",
          meterEndColor: "#ffffff",
          meterLabel: t("display.meterDurability"),
        };
      case "zone_color":
        return {
          type: "none",
          description: t("display.descRoadZone"),
        };
      case "hidden":
        return { type: "none", description: t("display.descRoadHidden") };
      default:
        return {
          type: "none",
          description: t("display.descRoadDefault"),
        };
    }
  }

  if (layerId === "building") {
    switch (modeId) {
      case "floor_color":
        return {
          type: "color",
          description: t("display.descBuildingFloor"),
          items: [
            { color: "#4caf50", label: t("display.subSpaceResidence") },
            { color: "#2196f3", label: t("display.subSpaceRetail") },
            { color: "#fbc02d", label: t("display.subSpaceFactory") },
            { color: "#9c27b0", label: t("display.subSpaceOffice") },
            { color: "#9e9e9e", label: t("display.subSpaceOther") },
          ],
        };
      case "durability_color":
        return {
          type: "meter",
          description: t("display.descBuildingDurability"),
          meterStartColor: "#ff4444",
          meterEndColor: "#ffffff",
          meterLabel: t("display.meterDurability"),
        };
      case "zone_color":
        return { type: "none", description: t("display.descBuildingZone") };
      case "hidden":
        return { type: "none", description: t("display.descBuildingHidden") };
      default:
        return {
          type: "none",
          description: t("display.descBuildingDefault"),
        };
    }
  }

  if (layerId === "parcel") {
    switch (modeId) {
      case "land_price_color":
        return {
          type: "meter",
          description: t("display.descParcelLandPrice"),
          meterStartColor: "#ffffff",
          meterEndColor: "#4caf50",
          meterLabel: t("display.meterLandPrice"),
        };
      case "zone_color":
        return { type: "none", description: t("display.descParcelZone") };
      case "buildable_color":
        return {
          type: "color",
          description: t("display.descParcelBuildable"),
          items: [
            { color: "#a5d6a7", label: t("display.subBdHouse") },
            { color: "#2e7d32", label: t("display.subBdApartment") },
            { color: "#2196f3", label: t("display.subBdRetail") },
            { color: "#fbc02d", label: t("display.subBdFactory") },
            { color: "#9c27b0", label: t("display.subBdOffice") },
            { color: "#795548", label: t("display.subBdFarm") },
            { color: "#ff9800", label: t("display.subBdMine") },
            { color: "#00bcd4", label: t("display.subBdPort") },
            { color: "#9e9e9e", label: t("display.subBdOther") },
            { color: "#f44336", label: t("display.subBdPublic") },
          ],
        };
      // Environmental scores are sampled at parcel centers. Noise, air pollution, and public safety colors
      // are relative values stretched across the epoch's full minimum-to-maximum range (relative severity
      // within the city). Sunlight and nature use an absolute 0–1 scale (the fraction of time in shade, and
      // the share of surrounding land covered by forests and parks).
      case "noise_color":
        return {
          type: "meter",
          description: t("display.descParcelNoise"),
          meterStartColor: "#fde68a",
          meterEndColor: "#e65100",
          meterLabel: t("display.meterNoise"),
        };
      case "air_pollution_color":
        return {
          type: "meter",
          description: t("display.descParcelAirPollution"),
          meterStartColor: "#d1e7dd",
          meterEndColor: "#4a148c",
          meterLabel: t("display.meterAirPollution"),
        };
      case "crime_color":
        return {
          type: "meter",
          description: t("display.descParcelCrime"),
          meterStartColor: "#ffcdd2",
          meterEndColor: "#b71c1c",
          meterLabel: t("display.meterCrime"),
        };
      case "sunshine_color":
        return {
          type: "meter",
          description: t("display.descParcelSunshine"),
          meterStartColor: "#fff59d",
          meterEndColor: "#37474f",
          meterLabel: t("display.meterSunshine"),
        };
      // Nature is the only score where higher is better, so the legend runs from scarce to plentiful.
      case "nature_color":
        return {
          type: "meter",
          description: t("display.descParcelNature"),
          meterStartColor: "#e8f5e9",
          meterEndColor: "#1b5e20",
          meterLabel: t("display.meterNature"),
        };
      case "hidden":
        return { type: "none", description: t("display.descParcelHidden") };
      case "border":
        return { type: "none", description: t("display.descParcelBorder") };
      default:
        return { type: "none", description: t("display.descParcelDefault") };
    }
  }
  if (layerId === "resource") {
    switch (modeId) {
      case "visible":
        return {
          type: "color",
          description: t("display.descResourceVisible"),
          items: [
            { color: "#262626", label: t("display.resourceOil") },
            { color: "#e6991a", label: t("display.resourceOre") },
            { color: "#9c27b0", label: t("display.resourceRareMetal") },
            { color: "#2e7d32", label: t("display.resourceForest") },
            { color: "#0099e6", label: t("display.resourceWater") },
          ],
        };
      case "hidden":
      default:
        return { type: "none", description: t("display.descResourceHidden") };
    }
  }

  // All other layers have no legend and placeholder explanatory text.
  const dummyDescriptions: Record<string, string> = {
    district: t("display.descDistrict", { mode: t("display.modeBuildingZone") }),
    terrain: t("display.descTerrain"),
    alert: t("display.descAlert"),
    prop: t("display.descProp"),
  };
  return {
    type: "none",
    description: dummyDescriptions[layerId] ?? t("display.descOther"),
  };
}

/**
 * Card definitions for alert display filters. Include ALERT_AGENT_ICON_BITS + ALERT_AGENT_VFX_BITS /
 * Shared constant ALERT_PATH_ICON_BITS: the alerts Unity displays in 3D (fire uses particles rather than icons).
 * Exclude production bottleneck bits used only in detail panels.
 * Building (agent) and road (path) bit values overlap, so shift road bits upward by ALERT_PATH_MASK_SHIFT in the
 * filter mask to separate namespaces, matching Unity's AlertManager.
 * ALERT_DISPLAY_RUIN derives from status (ruins); ALERT_DISPLAY_*_WARNING represents spatial warnings from L1.
 * They occupy a display-only area separate from entity alerts.
 */
const ALERT_CARDS: { bit: number; icon: string; labelKey: string; iconFilter?: string }[] = [
  ...[
    { bit: ALERT_AGENT_CONSTRUCTION_SHORTAGE, icon: "alerts/bribery.png", labelKey: "display.alertAgentConstructionShortage" },
    { bit: ALERT_AGENT_ON_FIRE, icon: "alerts/fire.png", labelKey: "display.alertAgentOnFire" },
    { bit: ALERT_AGENT_CRIME, icon: "alerts/shooting.png", labelKey: "display.alertAgentCrime" },
    { bit: ALERT_AGENT_INFRA_DISCONNECTED, icon: "alerts/infra-disconnected.svg", labelKey: "display.alertAgentInfraDisconnected" },
    { bit: ALERT_AGENT_CAPACITY_TIGHT, icon: "alerts/capacity-tight.svg", labelKey: "display.alertAgentCapacityTight" },
    { bit: ALERT_AGENT_NONCONFORMING, icon: "alerts/nonconforming.svg", labelKey: "display.alertAgentNonconforming" },
  ].filter((card) => ALERT_AGENT_ICON_BITS.includes(card.bit) || ALERT_AGENT_VFX_BITS.includes(card.bit)),
  ...[
    { bit: ALERT_PATH_CONSTRUCTION_SHORTAGE, icon: "alerts/road.png", labelKey: "display.alertPathConstructionShortage" },
    { bit: ALERT_PATH_ACCIDENT, icon: "alerts/car-accident.png", labelKey: "display.alertPathAccident" },
    { bit: ALERT_PATH_NO_STATION_HOUSE, icon: "transport/t_train.png", labelKey: "display.alertPathNoStationHouse" },
  ]
    .filter((card) => ALERT_PATH_ICON_BITS.includes(card.bit))
    .map((card) => ({ ...card, bit: card.bit << ALERT_PATH_MASK_SHIFT })),
  { bit: ALERT_DISPLAY_RUIN, icon: "alerts/rubble.png", labelKey: "display.alertDisplayRuin" },
  { bit: ALERT_DISPLAY_RESOURCE_WARNING, icon: "alerts/resource-warning.png", labelKey: "display.alertResourceWarning", iconFilter: "invert(1)" },
  { bit: ALERT_DISPLAY_FACILITY_WARNING, icon: "alerts/facility-inoperative.svg", labelKey: "display.alertFacilityWarning" },
  { bit: ALERT_DISPLAY_SANITATION_WARNING, icon: "alerts/sanitation.png", labelKey: "display.alertSanitationWarning" },
  { bit: ALERT_DISPLAY_INFRA_WARNING, icon: "alerts/electricity.png", labelKey: "display.alertInfraWarning" },
];

/** Mask enabling every type. Default for an unset subMode; must match Unity's default. */
const ALERT_MASK_ALL = ALERT_CARDS.reduce((mask, card) => mask | card.bit, 0);

function AlertSection({
  selection,
  onSelect,
}: {
  selection: DisplaySelection;
  onSelect: (layerId: string, mode: string, subMode?: string) => void;
}) {
  const { t } = useTranslation();
  const currentMask = selection.subMode ? parseInt(selection.subMode, 10) : ALERT_MASK_ALL;

  const handleToggle = (bit: number) => {
    playSound(clickSrc);
    const nextMask = currentMask ^ bit;
    onSelect("alert", "default", nextMask.toString());
  };

  return (
    <div className="do-alert-list">
      {ALERT_CARDS.map((card) => {
        const isEnabled = (currentMask & card.bit) !== 0;
        return (
          <div
            key={card.bit}
            className={`do-alert-card ${isEnabled ? "enabled" : ""}`}
            onClick={() => handleToggle(card.bit)}
          >
            <img
              src={resolveDisplayIcon(card.icon)}
              alt=""
              className="do-alert-card-icon"
              style={card.iconFilter ? { filter: card.iconFilter } : undefined}
              draggable={false}
            />
            <span className="do-alert-card-text">{t(card.labelKey)}</span>
            <div className="do-alert-card-toggle" />
          </div>
        );
      })}
    </div>
  );
}

/**
 * Public transport layer section.
 * Independent toggle buttons per line type (transit_master) control visibility by type.
 * Keep enabled types in subMode as a CSV string of transit_path_id values (multiple toggles, analogous to the
 * alerts bitmask).
 */
function TransitSection({
  selection,
  onSelect,
}: {
  selection: DisplaySelection;
  onSelect: (layerId: string, mode: string, subMode?: string) => void;
}) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  // Convert subMode (CSV) to a set of enabled transit_path_id values.
  const enabled = new Set<number>(
    (selection.subMode ?? "")
      .split(",")
      .map((s) => parseInt(s, 10))
      .filter((n) => Number.isFinite(n)),
  );

  const entries = masterData
    ? (Object.values(masterData.transit) as { display_name: string; transit_path_id: number }[])
    : [];

  const handleToggle = (transitPathId: number) => {
    playSound(clickSrc);
    const next = new Set(enabled);
    if (next.has(transitPathId)) {
      next.delete(transitPathId);
    } else {
      next.add(transitPathId);
    }
    onSelect("transit", selection.mode, Array.from(next).join(","));
  };

  if (entries.length === 0) {
    return <div className="do-alert-list">{t("display.transitNoTypes")}</div>;
  }

  return (
    <div className="do-alert-list">
      <button
        type="button"
        className={`do-alert-card ${selection.mode !== "hidden" ? "enabled" : ""}`}
        onClick={() => {
          playSound(clickSrc);
          onSelect("transit", selection.mode === "hidden" ? "default" : "hidden",
            selection.subMode ?? entries.map((entry) => entry.transit_path_id).join(","));
        }}
        aria-pressed={selection.mode !== "hidden"}
      >
        <span className="do-alert-card-text">{t("display.transitHighlight")}</span>
        <div className="do-alert-card-toggle" />
      </button>
      <div className="do-mode-row">
        {entries.map((entry) => {
          const isEnabled = enabled.has(entry.transit_path_id);
          return (
            <button
              key={entry.transit_path_id}
              type="button"
              className={`do-contour-btn ${isEnabled ? "selected" : ""}`}
              aria-pressed={isEnabled}
              onClick={() => handleToggle(entry.transit_path_id)}
            >
              <img
                src={resolveTransitIcon(entry.transit_path_id)}
                alt=""
                className="do-contour-icon"
                draggable={false}
              />
              <span className="do-contour-label">{entry.display_name}</span>
            </button>
          );
        })}
      </div>
    </div>
  );
}

/**
 * Coverage layer section. Select one service good with coverage (electricity, water, etc.).
 * Send the selected product ID as subMode; Unity draws facility coverage outlines and colors buildings by
 * whether they are in range.
 * The core owns the goods list and coverage decisions. Icons come from the facilities supplying each good.
 */
function CoverageSection({
  apiBaseUrl,
  selection,
  onSelect,
}: {
  apiBaseUrl: string;
  selection: DisplaySelection;
  onSelect: (layerId: string, mode: string, subMode?: string) => void;
}) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const products = useInfraProducts(apiBaseUrl).filter((p) => p.kind === "coverage");
  const selected = selection.mode === "hidden" ? null : Number(selection.subMode);

  const choose = (productId: number | null) => {
    playSound(clickSrc);
    if (productId === null) {
      onSelect("coverage", "hidden");
    } else {
      onSelect("coverage", "default", String(productId));
    }
  };

  return (
    <div className="do-section">
      <div className="do-mode-row">
        <button
          type="button"
          className={`do-contour-btn ${selected === null ? "selected" : ""}`}
          aria-pressed={selected === null}
          onClick={() => choose(null)}
        >
          <img
            src={resolveDisplayIcon("visual/invisible.png")}
            alt=""
            className="do-contour-icon"
            draggable={false}
          />
          <span className="do-contour-label">{t("display.coverageNone")}</span>
        </button>
        {products.map((product) => {
          const label = String(masterData?.products[product.productId]?.display_name ?? product.productId);
          return (
            <button
              key={product.productId}
              type="button"
              className={`do-contour-btn ${selected === product.productId ? "selected" : ""}`}
              aria-pressed={selected === product.productId}
              onClick={() => choose(product.productId)}
            >
              <img
                src={resolveAgentVariantIcon(product.supplierVariants[0])}
                alt=""
                className="do-contour-icon"
                draggable={false}
              />
              <span className="do-contour-label">{label}</span>
            </button>
          );
        })}
      </div>
    </div>
  );
}

export function DisplayOptionsPanel({ apiBaseUrl }: DisplayOptionsPanelProps) {
  const { selections, selectMode, toggleFlag } = useDisplayOptions();
  const [activeTabId, setActiveTabId] = useState<string>(TAB_ORDER[0]);
  const { t } = useTranslation();

  const orderedLayers = TAB_ORDER.map(
    (id) => DISPLAY_LAYERS.find((l) => l.id === id)
  ).filter(Boolean) as DisplayLayer[];

  const activeLayer = orderedLayers.find((l) => l.id === activeTabId);
  const activeSelection = activeLayer ? selections[activeLayer.id] : undefined;

  return (
    <div className="do-panel-root">
      <div className="do-panel-frame">
        {/* Vertical tab list */}
        <div className="do-tabs" data-guide="display.tabs">
          {orderedLayers.map((layer) => {
            const sel = selections[layer.id];
            const modeDef = layer.modes.find((m) => m.id === sel.mode);
            return (
              <button
                key={layer.id}
                className={`do-tab ${activeTabId === layer.id ? "selected" : ""}`}
                type="button"
                onClick={() => {
                  playSound(clickSrc);
                  setActiveTabId(layer.id);
                }}
              >
                <img
                  src={modeDef ? resolveDisplayIcon(modeDef.icon) : ""}
                  alt=""
                  className="do-tab-icon"
                  draggable={false}
                />
                <span className="do-tab-label">{t(layer.titleKey)}</span>
              </button>
            );
          })}
        </div>

        {/* Active tab content */}
        {activeLayer && activeSelection && (
          <div className="do-content">
            <div className="do-content-top">
              {activeLayer.id === "alert" ? (
                <AlertSection
                  selection={activeSelection}
                  onSelect={selectMode}
                />
              ) : activeLayer.id === "transit" ? (
                <TransitSection
                  selection={activeSelection}
                  onSelect={selectMode}
                />
              ) : activeLayer.id === "coverage" ? (
                <CoverageSection
                  apiBaseUrl={apiBaseUrl}
                  selection={activeSelection}
                  onSelect={selectMode}
                />
              ) : (
                <LayerSection
                  layer={activeLayer}
                  selection={activeSelection}
                  onSelect={selectMode}
                  onToggleFlag={toggleFlag}
                />
              )}
            </div>
            <LegendArea
              layerId={activeLayer.id}
              modeId={activeSelection.mode}
            />
          </div>
        )}
      </div>
    </div>
  );
}

function LayerSection({
  layer,
  selection,
  onSelect,
  onToggleFlag,
}: {
  layer: DisplayLayer;
  selection: DisplaySelection;
  onSelect: (layerId: string, mode: string, subMode?: string) => void;
  onToggleFlag: (layerId: string, flagId: string) => void;
}) {
  const { t } = useTranslation();
  const selectedModeDef = layer.modes.find((m) => m.id === selection.mode);
  const hasSubModes =
    selectedModeDef?.subModes && selectedModeDef.subModes.length > 0;

  return (
    <div className="do-section">
      <div className="do-mode-row" data-guide="display.modes">
        {layer.modes.map((mode) => (
          <ModeButton
            key={mode.id}
            mode={mode}
            selected={selection.mode === mode.id}
            onClick={() => {
              playSound(clickSrc);
              onSelect(layer.id, mode.id, selection.subMode || undefined);
            }}
          />
        ))}
      </div>
      {hasSubModes && (
        <div className="do-submode-row">
          {selectedModeDef.subModes!.map((sub) => (
            <SubModeButton
              key={sub.id}
              iconPath={sub.icon}
              tooltip={t(sub.tooltipKey)}
              selected={selection.subMode === sub.id}
              onClick={() => {
                playSound(clickSrc);
                onSelect(layer.id, selection.mode, sub.id);
              }}
            />
          ))}
        </div>
      )}
      {layer.toggles && layer.toggles.length > 0 && (
        <div className="do-terrain-extra">
          {layer.toggles.map((toggle) => {
            const isEnabled = selection.flags.includes(toggle.id);
            return (
              <button
                key={toggle.id}
                type="button"
                aria-pressed={isEnabled}
                className={`do-contour-btn ${isEnabled ? "selected" : ""}`}
                onClick={() => {
                  playSound(clickSrc);
                  onToggleFlag(layer.id, toggle.id);
                }}
              >
                <img
                  src={resolveDisplayIcon(toggle.icon)}
                  alt=""
                  className="do-contour-icon"
                  draggable={false}
                />
                <span className="do-contour-label">{t(toggle.labelKey)}</span>
              </button>
            );
          })}
        </div>
      )}
      {layer.id === "terrain" && (
        <div className="do-terrain-extra">
          <button
            className={`do-contour-btn ${selection.subMode === "contour_on" ? "selected" : ""}`}
            type="button"
            onClick={() => {
              playSound(clickSrc);
              const nextState = selection.subMode === "contour_on" ? "contour_off" : "contour_on";
              onSelect(layer.id, selection.mode, nextState);
            }}
          >
            <span className="do-contour-icon">〰</span>
            <span className="do-contour-label">{t("display.showContours")}</span>
          </button>
          <button
            className={`do-contour-btn ${selection.flags.includes("seabed") ? "selected" : ""}`}
            type="button"
            onClick={() => {
              playSound(clickSrc);
              onToggleFlag(layer.id, "seabed");
            }}
          >
            <span className="do-contour-icon">🌊</span>
            <span className="do-contour-label">{t("display.showSeabed")}</span>
          </button>
        </div>
      )}
    </div>
  );
}

function ModeButton({
  mode,
  selected,
  onClick,
}: {
  mode: DisplayMode;
  selected: boolean;
  onClick: () => void;
}) {
  const { t } = useTranslation();
  return (
    <button
      className={`do-mode-btn ${selected ? "selected" : ""}`}
      title={t(mode.tooltipKey)}
      type="button"
      onClick={onClick}
    >
      <img
        src={resolveDisplayIcon(mode.icon)}
        alt={t(mode.tooltipKey)}
        className="do-mode-icon"
        draggable={false}
      />
    </button>
  );
}

function SubModeButton({
  iconPath,
  tooltip,
  selected,
  onClick,
}: {
  iconPath: string;
  tooltip: string;
  selected: boolean;
  onClick: () => void;
}) {
  return (
    <button
      className={`do-submode-btn ${selected ? "selected" : ""}`}
      title={tooltip}
      type="button"
      onClick={onClick}
    >
      <img
        src={resolveDisplayIcon(iconPath)}
        alt={tooltip}
        className="do-submode-icon"
        draggable={false}
      />
    </button>
  );
}

/**
 * Description and legend area in the right pane.
 * Render the content resolved by getLegendInfo for the current layer and mode.
 */
function LegendArea({
  layerId,
  modeId,
}: {
  layerId: string;
  modeId: string;
}) {
  const { t } = useTranslation();
  const info = getLegendInfo(layerId, modeId, t);

  if (!info.description && info.type === "none") {
    return null;
  }

  return (
    <div className="do-content-bottom">
      <div className="do-legend-root">
        {info.description && (
          <>
            <div className="do-description">{info.description}</div>
            <div className="do-legend-divider" />
          </>
        )}
        <div className="do-legend-area">
          {info.type === "none" && (
            <span className="do-legend-none">{t("display.legendNone")}</span>
          )}
          {info.type === "color" && info.items && (
            <div className="do-legend-color-list">
              {info.items.map((item) => (
                <div key={item.label} className="do-legend-color-item">
                  <span
                    className="do-legend-color-box"
                    style={{ backgroundColor: item.color }}
                  />
                  <span className="do-legend-color-label">{item.label}</span>
                </div>
              ))}
            </div>
          )}
          {info.type === "meter" && (
            <div className="do-legend-meter">
              <div
                className="do-legend-meter-bar"
                style={{
                  background: `linear-gradient(to right, ${info.meterStartColor}, ${info.meterEndColor})`,
                }}
              />
              <span className="do-legend-meter-label">{info.meterLabel}</span>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
