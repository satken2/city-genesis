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
  /** Mode description, displayed at the panel's upper right. */
  description: string;
  /** Color legend entries, used when type is "color". */
  items?: { color: string; label: string }[];
  /** Left endpoint color of the meter legend, used when type is "meter". */
  meterStartColor?: string;
  /** Right endpoint color of the meter legend, used when type is "meter". */
  meterEndColor?: string;
  /** Meter legend label, used when type is "meter". */
  meterLabel?: string;
};

/**
 * Resolve legend information from layer and mode IDs.
 * Treat modes without a defined legend as "none" and return placeholder explanatory text.
 */
function getLegendInfo(layerId: string, modeId: string, t: TFunction): LegendInfo {
  if (layerId === "coverage") {
    return {
      type: "color",
      description: t("display.coverageDescription"),
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
          description: "道路や線路の交通量で色分けします",
          meterStartColor: "#ffffff",
          meterEndColor: "#ff4444",
          meterLabel: "← 交通量 →",
        };
      case "speed_color":
        return {
          type: "meter",
          description:
            "道路や線路の通過速度(制限速度に加え、カーブ・勾配を含めて形状から決まる速度。交差点での減速・停止は含まない)で色分けします。0km/hが赤、100km/h以上が緑です",
          meterStartColor: "#ff0000",
          meterEndColor: "#00ff00",
          meterLabel: "0 ← 通過速度 → 100km/h",
        };
      case "durability_color":
        return {
          type: "meter",
          description: "道路や線路の耐久度で色分けします",
          meterStartColor: "#ff4444",
          meterEndColor: "#ffffff",
          meterLabel: "← 耐久度 →",
        };
      case "zone_color":
        return {
          type: "none",
          description: "道路が所属する地区で色分けします",
        };
      case "hidden":
        return { type: "none", description: "道路を非表示にします" };
      default:
        return {
          type: "none",
          description: "道路を通常表示します。計画中の道路の表示は下のスイッチで切り替えます",
        };
    }
  }

  if (layerId === "building") {
    switch (modeId) {
      case "floor_color":
        return {
          type: "color",
          description: "指定したフロアを持つ建物を色付けします",
          items: [
            { color: "#4caf50", label: "住居" },
            { color: "#2196f3", label: "商業" },
            { color: "#fbc02d", label: "工場" },
            { color: "#9c27b0", label: "オフィス" },
            { color: "#795548", label: "農地" },
            { color: "#9e9e9e", label: "その他" },
          ],
        };
      case "durability_color":
        return {
          type: "meter",
          description: "建物の耐久度で色分けします",
          meterStartColor: "#ff4444",
          meterEndColor: "#ffffff",
          meterLabel: "← 耐久度 →",
        };
      case "zone_color":
        return { type: "none", description: "建物が所属する地区で色分けします" };
      case "hidden":
        return { type: "none", description: "建物を非表示にします" };
      default:
        return {
          type: "none",
          description:
            "建物を通常表示します。計画中の建物の表示と公共施設のハイライト(敷地を黄色い光で示します)は下のスイッチで切り替えます",
        };
    }
  }

  if (layerId === "parcel") {
    switch (modeId) {
      case "land_price_color":
        return {
          type: "meter",
          description: "地価で色分けします",
          meterStartColor: "#ffffff",
          meterEndColor: "#4caf50",
          meterLabel: "← 地価 →",
        };
      case "zone_color":
        return { type: "none", description: "区画が所属する地区で色分けします" };
      case "buildable_color":
        return {
          type: "color",
          description: "区画に建築可能な建物で色分けします",
          items: [
            { color: "#a5d6a7", label: "一戸建て" },
            { color: "#2e7d32", label: "集合住宅" },
            { color: "#2196f3", label: "商業施設" },
            { color: "#fbc02d", label: "工場" },
            { color: "#9c27b0", label: "オフィス" },
            { color: "#795548", label: "農地" },
            { color: "#ff9800", label: "鉱山" },
            { color: "#00bcd4", label: "港" },
            { color: "#9e9e9e", label: "その他" },
            { color: "#f44336", label: "公共施設" },
          ],
        };
      // Environmental scores are sampled at parcel centers. Noise, air pollution, and public safety colors
      // are relative values stretched across the epoch's full minimum-to-maximum range (relative severity
      // within the city). Sunlight alone uses an absolute 0–1 scale based on the fraction of time in shade.
      case "noise_color":
        return {
          type: "meter",
          description: "区画の騒音の悪さで色分けします。エポック実行後に反映されます",
          meterStartColor: "#fde68a",
          meterEndColor: "#e65100",
          meterLabel: "← 騒音 →",
        };
      case "air_pollution_color":
        return {
          type: "meter",
          description: "区画の大気汚染の悪さで色分けします。エポック実行後に反映されます",
          meterStartColor: "#d1e7dd",
          meterEndColor: "#4a148c",
          meterLabel: "← 大気汚染 →",
        };
      case "crime_color":
        return {
          type: "meter",
          description: "区画の治安の悪さで色分けします。エポック実行後に反映されます",
          meterStartColor: "#ffcdd2",
          meterEndColor: "#b71c1c",
          meterLabel: "← 治安の悪さ →",
        };
      case "sunshine_color":
        return {
          type: "meter",
          description: "区画の日当たりの悪さ(周辺の建物の影になっている時間の割合)で色分けします。エポック実行後に反映されます",
          meterStartColor: "#fff59d",
          meterEndColor: "#37474f",
          meterLabel: "← 日照の悪さ →",
        };
      case "hidden":
        return { type: "none", description: "区画を非表示にします" };
      case "border":
        return { type: "none", description: "区画の境界線を表示します" };
      default:
        return { type: "none", description: "区画を通常表示します" };
    }
  }
  if (layerId === "resource") {
    switch (modeId) {
      case "visible":
        return {
          type: "color",
          description: "天然資源の分布を表示します",
          items: [
            { color: "#262626", label: "油田" },
            { color: "#e6991a", label: "鉱脈" },
            { color: "#9c27b0", label: "レアメタル" },
            { color: "#2e7d32", label: "森林" },
            { color: "#0099e6", label: "水源" },
          ],
        };
      case "hidden":
      default:
        return { type: "none", description: "天然資源の表示を非表示にします" };
    }
  }

  // All other layers have no legend and placeholder explanatory text.
  const dummyDescriptions: Record<string, string> = {
    district: "地区の枠線と名称・アイコンを表示します。地区ごとの色分けは建物/区画/道路の「地区別色塗り」モードを使います。",
    terrain: "地形の標高や透明度を変更して表示します。",
    alert: "街のアラートを種別ごとに表示/非表示できます。建物・道路のアラートアイコンと、倒壊・焼失した廃墟のアイコンが対象です。",
    transit: "設置済みの公共交通路線を種別色の帯で表示します。路線種別ごとに表示/非表示を切り替えられます。",
    prop: "プロップの表示/非表示を切り替えます。",
  };
  return {
    type: "none",
    description: dummyDescriptions[layerId] ?? "表示設定を変更します。",
  };
}

/**
 * Card definitions for alert display filters. Include ALERT_AGENT_ICON_BITS + ALERT_AGENT_VFX_BITS /
 * ALERT_PATH_ICON_BITS from consts.py: the alerts Unity displays in 3D (fire uses particles rather than icons).
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
    { bit: ALERT_AGENT_INFRA_DISCONNECTED, icon: "alerts/electricity.png", labelKey: "display.alertAgentInfraDisconnected" },
    { bit: ALERT_AGENT_CAPACITY_TIGHT, icon: "alerts/capacity-tight.svg", labelKey: "display.alertAgentCapacityTight" },
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
  { bit: ALERT_DISPLAY_FACILITY_WARNING, icon: "alerts/bribery.png", labelKey: "display.alertFacilityWarning" },
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
      {entries.map((entry) => {
        const isEnabled = enabled.has(entry.transit_path_id);
        return (
          <div
            key={entry.transit_path_id}
            className={`do-alert-card ${isEnabled ? "enabled" : ""}`}
            onClick={() => handleToggle(entry.transit_path_id)}
          >
            <img
              src={resolveTransitIcon(entry.transit_path_id)}
              alt=""
              className="do-alert-card-icon"
              draggable={false}
            />
            <span className="do-alert-card-text">{entry.display_name}</span>
            <div className="do-alert-card-toggle" />
          </div>
        );
      })}
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
    <div className="do-alert-list">
      <button
        type="button"
        className={`do-alert-card ${selected === null ? "enabled" : ""}`}
        aria-pressed={selected === null}
        onClick={() => choose(null)}
      >
        <img src={resolveDisplayIcon("visual/invisible.png")} alt="" className="do-alert-card-icon" draggable={false} />
        <span className="do-alert-card-text">{t("display.coverageNone")}</span>
        <div className="do-alert-card-toggle" />
      </button>
      {products.map((product) => (
        <button
          key={product.productId}
          type="button"
          className={`do-alert-card ${selected === product.productId ? "enabled" : ""}`}
          aria-pressed={selected === product.productId}
          onClick={() => choose(product.productId)}
        >
          <img
            src={resolveAgentVariantIcon(product.supplierVariants[0])}
            alt=""
            className="do-alert-card-icon"
            draggable={false}
          />
          <span className="do-alert-card-text">
            {masterData?.products[product.productId]?.display_name ?? product.productId}
          </span>
          <div className="do-alert-card-toggle" />
        </button>
      ))}
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
            <div className="do-content-bottom">
              <LegendArea
                layerId={activeLayer.id}
                modeId={activeSelection.mode}
              />
            </div>
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
        <div className="do-toggle-list">
          {layer.toggles.map((toggle) => {
            const isEnabled = selection.flags.includes(toggle.id);
            return (
              <button
                key={toggle.id}
                type="button"
                aria-pressed={isEnabled}
                className={`do-alert-card ${isEnabled ? "enabled" : ""}`}
                onClick={() => {
                  playSound(clickSrc);
                  onToggleFlag(layer.id, toggle.id);
                }}
              >
                <img
                  src={resolveDisplayIcon(toggle.icon)}
                  alt=""
                  className="do-alert-card-icon"
                  draggable={false}
                />
                <span className="do-alert-card-text">{t(toggle.labelKey)}</span>
                <div className="do-alert-card-toggle" />
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

  return (
    <div className="do-legend-root">
      <div className="do-description">{layerId === "transit" ? t("display.transitHighlightDescription") : info.description}</div>
      <div className="do-legend-divider" />
      <div className="do-legend-area">
        {info.type === "none" && (
          <span className="do-legend-none">凡例はありません</span>
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
  );
}
