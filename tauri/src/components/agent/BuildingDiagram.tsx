/**
 * Building cross-section SVG component
 *
 * Represents a building as a rectangular prism with capacity stacked and colored by space category.
 * Conveys physical attributes using dimension markings like those in geometry exercises.
 *
 * Structure:
 * - Front: stacked rectangles colored by category.
 * - Right and top: parallelograms conveying depth.
 * - Dimension lines: height (right) and building footprint area (bottom).
 */

import { useTranslation } from "react-i18next";
import { useMasterData } from "../../contexts/MasterDataContext";
import { SPACE_CATEGORY_COLORS } from "../../local-constants";

type BuildingDiagramProps = {
  /** Mapping from space category ID to area (m²). */
  capacity: Record<string, number>;
  /** Building height (m). */
  height: number;
  /** Building footprint area (m²). */
  baseArea: number;
};

/** X coordinate of the left edge of the front face. */
const FRONT_X = 30;
/** Y coordinate of the top edge of the front face. */
const FRONT_TOP_Y = 35;
/** Front face width. */
const FRONT_WIDTH = 100;
/** Maximum front face height in pixels. */
const FRONT_MAX_HEIGHT = 180;
/** X component of the 3D offset (rightward). */
const DX = 22;
/** Y component of the 3D offset (upward, negative). */
const DY = -16;
/** Gap between segments in pixels. */
const SEGMENT_GAP = 1;
/** Minimum segment height in pixels required to display its label. */
const MIN_LABEL_HEIGHT = 22;
/** Minimum segment height in pixels. A lower limit keeps even very thin segments visible. */
const MIN_SEG_HEIGHT = 2;

/**
 * Adjust each RGB component of a hex color code by amount.
 * Positive amount lightens it; negative amount darkens it.
 */
function adjustColor(hex: string, amount: number): string {
  const num = parseInt(hex.slice(1), 16);
  const r = Math.min(255, Math.max(0, ((num >> 16) & 0xff) + amount));
  const g = Math.min(255, Math.max(0, ((num >> 8) & 0xff) + amount));
  const b = Math.min(255, Math.max(0, (num & 0xff) + amount));
  return `#${((r << 16) | (g << 8) | b).toString(16).padStart(6, "0")}`;
}

export function BuildingDiagram({
  capacity,
  height,
  baseArea,
}: BuildingDiagramProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  // Exclude categories with zero area.
  // Otherwise, even zero-area categories render at MIN_SEG_HEIGHT,
  // and their separate colors create a rainbow band at the top of the building.
  const entries = Object.entries(capacity).filter(([, area]) => area > 0);
  const totalCapacity = entries.reduce((sum, [, area]) => sum + area, 0);

  if (entries.length === 0 || totalCapacity === 0) {
    return null;
  }

  const gapTotal = SEGMENT_GAP * Math.max(0, entries.length - 1);
  const availableHeight = FRONT_MAX_HEIGHT - gapTotal;

  const rawHeights = entries.map(([, area]) => {
    const ratio = area / totalCapacity;
    return Math.max(MIN_SEG_HEIGHT, ratio * availableHeight);
  });

  /*
 * If clamping makes the total exceed availableHeight, scale all segments down uniformly
 * to fit FRONT_MAX_HEIGHT. Otherwise, segments protrude above the building,
 * stacking multiple colors into what looks like a rainbow band.
 */
  const totalRawHeight = rawHeights.reduce((s, h) => s + h, 0);
  const segHeights =
    totalRawHeight > availableHeight
      ? rawHeights.map((h) => Math.max(1, h * (availableHeight / totalRawHeight)))
      : rawHeights;

  const segments = entries.map(([categoryId, area], index) => {
    const ratio = area / totalCapacity;
    const segHeight = segHeights[index];
    const color = SPACE_CATEGORY_COLORS[index % SPACE_CATEGORY_COLORS.length];
    const categoryName =
      masterData?.space_categories[Number(categoryId)]?.display_name ??
      categoryId;
    return { area, ratio, segHeight, color, categoryName };
  });

  // Stack front face Y coordinates from bottom to top.
  let currentY = FRONT_TOP_Y + FRONT_MAX_HEIGHT;
  const positionedSegments = segments.map((seg) => {
    currentY -= seg.segHeight;
    const y = currentY;
    currentY -= SEGMENT_GAP;
    return { ...seg, y };
  });

  const frontBottom = FRONT_TOP_Y + FRONT_MAX_HEIGHT;
  const rightEdge = FRONT_X + FRONT_WIDTH;

  // Dimension line coordinates.
  const dimLineX = rightEdge + DX + 22;
  const dimTopY = FRONT_TOP_Y + DY;
  const tickLen = 8;
  const baseLabelY = frontBottom + 35;

  const svgWidth = dimLineX + tickLen + 90;
  const svgHeight = baseLabelY + 10;

  return (
    <svg
      viewBox={`0 0 ${svgWidth} ${svgHeight}`}
      className="building-diagram"
      xmlns="http://www.w3.org/2000/svg"
    >
      {/* Right face segments (the depth visible behind the front face). */}
      {positionedSegments.map((seg, i) => (
        <polygon
          key={`right-${i}`}
          points={`
            ${rightEdge},${seg.y}
            ${rightEdge + DX},${seg.y + DY}
            ${rightEdge + DX},${seg.y + seg.segHeight + DY}
            ${rightEdge},${seg.y + seg.segHeight}
          `}
          fill={adjustColor(seg.color, -40)}
          stroke="rgba(0,0,0,0.3)"
          strokeWidth="0.5"
        />
      ))}

      {/* Front face segments (colored by category). */}
      {positionedSegments.map((seg, i) => (
        <rect
          key={`front-${i}`}
          x={FRONT_X}
          y={seg.y}
          width={FRONT_WIDTH}
          height={seg.segHeight}
          fill={seg.color}
          stroke="rgba(0,0,0,0.2)"
          strokeWidth="0.5"
        />
      ))}

      {/* Top face (top surface of the highest segment). */}
      {(() => {
        const topSeg = positionedSegments[positionedSegments.length - 1];
        if (!topSeg) return null;
        return (
          <polygon
            points={`
              ${FRONT_X},${topSeg.y}
              ${FRONT_X + DX},${topSeg.y + DY}
              ${rightEdge + DX},${topSeg.y + DY}
              ${rightEdge},${topSeg.y}
            `}
            fill={adjustColor(topSeg.color, 40)}
            stroke="rgba(0,0,0,0.3)"
            strokeWidth="0.5"
          />
        );
      })()}

      {/* Category name and area labels inside front segments. */}
      {positionedSegments.map((seg, i) => {
        if (seg.segHeight < MIN_LABEL_HEIGHT) return null;
        const midY = seg.y + seg.segHeight / 2;
        return (
          <text
            key={`label-${i}`}
            x={FRONT_X + FRONT_WIDTH / 2}
            y={midY + 5}
            textAnchor="middle"
            fill="#fff"
            fontSize="12"
            fontWeight="600"
            style={{ pointerEvents: "none" }}
          >
            {seg.categoryName}: {Math.round(seg.area)}㎡
          </text>
        );
      })}

      {/* ---- Height dimension line ---- */}
      {/* Top horizontal tick. */}
      <line
        x1={rightEdge + DX + 5}
        y1={dimTopY}
        x2={dimLineX + tickLen}
        y2={dimTopY}
        stroke="#93a9c0"
        strokeWidth="0.8"
      />
      {/* Bottom horizontal tick. */}
      <line
        x1={rightEdge + DX + 5}
        y1={frontBottom}
        x2={dimLineX + tickLen}
        y2={frontBottom}
        stroke="#93a9c0"
        strokeWidth="0.8"
      />
      {/* Vertical dimension line. */}
      <line
        x1={dimLineX}
        y1={dimTopY}
        x2={dimLineX}
        y2={frontBottom}
        stroke="#93a9c0"
        strokeWidth="0.8"
        strokeDasharray="3,2"
      />
      {/* Height label. */}
      <text
        x={dimLineX + tickLen + 3}
        y={(dimTopY + frontBottom) / 2 + 5}
        fill="#c0d0e0"
        fontSize="12"
        fontWeight="600"
      >
        {t("building.heightLabel", { value: height })}
      </text>

      {/* ---- Building footprint area dimension line ---- */}
      {/* Left vertical tick. */}
      <line
        x1={FRONT_X}
        y1={frontBottom + 5}
        x2={FRONT_X}
        y2={baseLabelY - 10}
        stroke="#93a9c0"
        strokeWidth="0.8"
      />
      {/* Right vertical tick. */}
      <line
        x1={FRONT_X + FRONT_WIDTH}
        y1={frontBottom + 5}
        x2={FRONT_X + FRONT_WIDTH}
        y2={baseLabelY - 10}
        stroke="#93a9c0"
        strokeWidth="0.8"
      />
      {/* Horizontal dimension line. */}
      <line
        x1={FRONT_X}
        y1={baseLabelY - 12}
        x2={FRONT_X + FRONT_WIDTH}
        y2={baseLabelY - 12}
        stroke="#93a9c0"
        strokeWidth="0.8"
        strokeDasharray="3,2"
      />
      {/* Building footprint area label. */}
      <text
        x={FRONT_X + FRONT_WIDTH / 2}
        y={baseLabelY + 3}
        textAnchor="middle"
        fill="#c0d0e0"
        fontSize="12"
        fontWeight="600"
      >
        {t("building.baseAreaLabel", { area: Math.round(baseArea) })}
      </text>
    </svg>
  );
}
