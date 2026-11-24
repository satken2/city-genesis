/**
 * Roadside lot section in the path construction panel.
 *
 * When a street is completed, the core divides each side into land parcels of the area chosen here (0 = none).
 * Left and right are relative to the drawing direction (first point → last point), shown by the arrow in the diagram.
 * The panel owns the values and sends them as left_lot_area / right_lot_area with every path it creates.
 * Unless the sides are split, both sides share one value.
 */

import { useTranslation } from "react-i18next";
import { NumberStepper } from "../common/NumberStepper";
import { SIZE_PRESETS } from "../parcel/parcelSizePresets";
import { resolveIntlLocale } from "../../utils/locale";
import "./PathConstructionPanel.css";

export type LotAreas = { left: number; right: number };

export const DEFAULT_LOT_AREA = 400; // 20 m × 20 m, the same default size as roadside parcel creation

const PRESET_AREAS = SIZE_PRESETS.map((preset) => ({ nameKey: preset.nameKey, area: preset.width * preset.depth }));
const AREA_MIN = 25;
const AREA_MAX = 90000; // 300 m × 300 m, the largest size roadside parcel creation accepts
const AREA_STEP = 25;

type Side = "left" | "right";

type PathLotSectionProps = {
  value: LotAreas;
  onChange: (value: LotAreas) => void;
  split: boolean;
  onSplitChange: (split: boolean) => void;
  /** Grid placement lays streets in several directions, so left/right cannot be chosen separately. */
  canSplit: boolean;
};

export function PathLotSection({ value, onChange, split, onSplitChange, canSplit }: PathLotSectionProps) {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);
  const formatArea = (area: number) => area.toLocaleString(locale, { maximumFractionDigits: 0 });
  const separate = split && canSplit;

  const setSide = (side: Side | "both", area: number) => {
    onChange(side === "both" ? { left: area, right: area } : { ...value, [side]: area });
  };

  const renderSide = (side: Side | "both") => {
    const area = side === "right" ? value.right : value.left;
    const presetIndex = PRESET_AREAS.findIndex((preset) => preset.area === area);
    const choice = area <= 0 ? "none" : presetIndex >= 0 ? String(presetIndex) : "custom";
    return (
      <div key={side} className="pc-lot-side">
        <label className="pc-settings-row">
          <span className="pc-settings-label">{t(`path_construction.lots.${side}`)}</span>
          <select
            className="pc-select"
            value={choice}
            onChange={(event) => {
              const next = event.target.value;
              setSide(side, next === "none" ? 0 : PRESET_AREAS[Number(next)].area);
            }}
          >
            <option value="none">{t("path_construction.lots.none")}</option>
            {PRESET_AREAS.map((preset, index) => (
              <option key={preset.nameKey} value={String(index)}>
                {t("path_construction.lots.preset", { name: t(preset.nameKey), area: formatArea(preset.area) })}
              </option>
            ))}
            {choice === "custom" && (
              <option value="custom">{t("path_construction.lots.custom", { area: formatArea(area) })}</option>
            )}
          </select>
        </label>
        {area > 0 && (
          <div className="pc-stepper-row">
            <span className="pc-settings-label">{t("path_construction.lots.area")}</span>
            <NumberStepper
              value={area}
              onChange={(next) => setSide(side, next)}
              min={AREA_MIN}
              max={AREA_MAX}
              step={AREA_STEP}
              integerOnly={true}
            />
            <span className="pc-stepper-unit">{t("parcel_creation.unitSqm")}</span>
          </div>
        )}
      </div>
    );
  };

  return (
    <div className="pc-section">
      <div className="pc-section-title">{t("path_construction.lots.title")}</div>
      <LotDiagram value={separate ? value : { left: value.left, right: value.left }} formatArea={formatArea} />
      {canSplit && (
        <label className="pc-lot-split">
          <input
            type="checkbox"
            checked={split}
            onChange={(event) => {
              const next = event.currentTarget.checked;
              if (!next) onChange({ left: value.left, right: value.left });
              onSplitChange(next);
            }}
          />
          {t("path_construction.lots.split")}
        </label>
      )}
      {separate ? (["left", "right"] as const).map(renderSide) : renderSide("both")}
    </div>
  );
}

/* ---- Diagram ---- */

const VIEW_WIDTH = 300;
const MARGIN = 10;
const ROAD_TOP = 50;
const ROAD_HEIGHT = 24;
const LOT_DEPTH = 36;
const LOT_GAP = 2;
const ROAD_LENGTH_M = 120; // Road length represented by the drawing, used to decide how many lots fit along it
const MAX_DRAWN_LOTS = 30;

/** Simple plan view: the road with its drawing direction, left-side lots above and right-side lots below. */
function LotDiagram({ value, formatArea }: { value: LotAreas; formatArea: (area: number) => string }) {
  const { t } = useTranslation();
  const usable = VIEW_WIDTH - MARGIN * 2;
  const roadMid = ROAD_TOP + ROAD_HEIGHT / 2;

  const renderLots = (side: Side) => {
    const area = value[side];
    const top = side === "left" ? ROAD_TOP - LOT_GAP - LOT_DEPTH : ROAD_TOP + ROAD_HEIGHT + LOT_GAP;
    const label = area > 0
      ? t("path_construction.lots.diagramArea", { side: t(`path_construction.lots.${side}`), area: formatArea(area) })
      : t("path_construction.lots.diagramNone", { side: t(`path_construction.lots.${side}`) });
    const count = area > 0
      ? Math.min(MAX_DRAWN_LOTS, Math.max(1, Math.round(ROAD_LENGTH_M / Math.sqrt(area))))
      : 0;
    const lotWidth = count > 0 ? usable / count : 0;
    return (
      <g key={side}>
        {Array.from({ length: count }, (_, index) => (
          <rect
            key={index}
            className="pc-lot-diagram-lot"
            x={MARGIN + index * lotWidth + LOT_GAP / 2}
            y={top}
            width={Math.max(1, lotWidth - LOT_GAP)}
            height={LOT_DEPTH}
          />
        ))}
        <text className="pc-lot-diagram-label" x={VIEW_WIDTH / 2} y={top + LOT_DEPTH / 2}>{label}</text>
      </g>
    );
  };

  return (
    <svg className="pc-lot-diagram" viewBox={`0 0 ${VIEW_WIDTH} ${ROAD_TOP * 2 + ROAD_HEIGHT}`} role="img"
      aria-label={t("path_construction.lots.title")}>
      {renderLots("left")}
      <rect className="pc-lot-diagram-road" x={0} y={ROAD_TOP} width={VIEW_WIDTH} height={ROAD_HEIGHT} />
      <line className="pc-lot-diagram-arrow" x1={MARGIN * 2} y1={roadMid} x2={VIEW_WIDTH - MARGIN * 3} y2={roadMid} />
      <polygon
        className="pc-lot-diagram-arrowhead"
        points={`${VIEW_WIDTH - MARGIN * 3},${roadMid - 6} ${VIEW_WIDTH - MARGIN},${roadMid} ${VIEW_WIDTH - MARGIN * 3},${roadMid + 6}`}
      />
      {renderLots("right")}
    </svg>
  );
}
