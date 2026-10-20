/**
 * Interchange connection diagram.
 *
 * List departing roads (arms) on the left and arriving roads on the right in the same order, and connect reachable pairs with arrows.
 * Route finding treats an interchange as one intersection, so this shows only which roads can reach which others, without drawing individual ramps.
 * Use the record's backend-computed connections (pairs reachable without sharp turns) directly.
 */

import { useTranslation } from "react-i18next";
import { PATH_MOTORWAY, PATH_STREET } from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import type { InterchangeArmDetail } from "../../types";
import "./InterchangeConnectionDiagram.css";

/** Road box height and spacing, and arrow band width (px). Arrow coordinates derive from these values, so these also define box dimensions. */
const ROW_HEIGHT = 44;
const ROW_GAP = 8;
const LINK_WIDTH = 120;
/** Maximum vertical spacing between arrows entering or leaving one box, and padding from the box's top and bottom edges (px). */
const PORT_SPACING = 8;
const PORT_MARGIN = 8;
const ARROW_LENGTH = 7;
const ARROW_HALF_WIDTH = 4;

/**
 * Colors for departing roads. Keep this order: adjacent colors remain distinguishable across color vision differences (verified on a dark surface).
 * When there are too many roads, use FOLD_COLOR for the remainder instead of cycling the palette.
 */
const ARM_COLORS = ["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767"];
const FOLD_COLOR = "#8c99a8";

type InterchangeConnectionDiagramProps = {
  arms: InterchangeArmDetail[];
  connections: [number, number][];
};

type Link = { from: number; to: number };

function typeRank(arm: InterchangeArmDetail): number {
  if (arm.path_type === PATH_MOTORWAY) return 0;
  if (arm.path_type === PATH_STREET) return 1;
  return 2;
}

function rowCenter(row: number): number {
  return row * (ROW_HEIGHT + ROW_GAP) + ROW_HEIGHT / 2;
}

/** Vertical offsets from a box's center when count arrows enter or leave it. */
function portOffsets(count: number): number[] {
  if (count <= 1) return [0];
  const spacing = Math.min(PORT_SPACING, (ROW_HEIGHT - 2 * PORT_MARGIN) / (count - 1));
  return Array.from({ length: count }, (_, i) => (i - (count - 1) / 2) * spacing);
}

/**
 * Determine the vertical positions of arrow endpoints for each box.
 * Arrange them from top to bottom in the order of the opposite rows to prevent arrows from crossing near the box.
 */
function portYs(links: Link[], rowOf: (link: Link) => number, otherRowOf: (link: Link) => number): Map<Link, number> {
  const groups = new Map<number, Link[]>();
  for (const link of links) {
    const group = groups.get(rowOf(link));
    if (group) group.push(link);
    else groups.set(rowOf(link), [link]);
  }
  const ys = new Map<Link, number>();
  for (const [row, group] of groups) {
    const offsets = portOffsets(group.length);
    group.sort((a, b) => otherRowOf(a) - otherRowOf(b));
    group.forEach((link, i) => ys.set(link, rowCenter(row) + offsets[i]));
  }
  return ys;
}

export function InterchangeConnectionDiagram({ arms, connections }: InterchangeConnectionDiagramProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  // Use the same order on both sides so that each road faces itself horizontally.
  const rows = [...arms].sort((a, b) => typeRank(a) - typeRank(b) || a.path_id - b.path_id);
  const rowByPath = new Map(rows.map((arm, i) => [arm.path_id, i]));
  const links: Link[] = [];
  for (const [fromPid, toPid] of connections) {
    const from = rowByPath.get(fromPid);
    const to = rowByPath.get(toPid);
    if (from !== undefined && to !== undefined && from !== to) links.push({ from, to });
  }
  const startY = portYs(links, (link) => link.from, (link) => link.to);
  const endY = portYs(links, (link) => link.to, (link) => link.from);

  const colorOf = (row: number) => ARM_COLORS[row] ?? FOLD_COLOR;
  const labelOf = (arm: InterchangeArmDetail) => {
    const typeName = arm.path_type != null ? masterData?.path_types[arm.path_type]?.display_name : undefined;
    return typeName
      ? t("interchange.connections.road", { type: typeName, id: arm.path_id })
      : t("path.titleWithId", { id: arm.path_id });
  };
  // Show districts only for streets. Leave highways blank even if they have a district.
  const zoneOf = (arm: InterchangeArmDetail) => (arm.path_type === PATH_STREET ? arm.zone_name ?? "" : "");

  const height = rows.length * ROW_HEIGHT + Math.max(rows.length - 1, 0) * ROW_GAP;
  const x0 = 2;
  const tipX = LINK_WIDTH - 2;
  const endX = tipX - ARROW_LENGTH;
  const midX = (x0 + endX) / 2;

  const renderRoads = (withColor: boolean) => (
    <div className="ic-conn__roads" style={{ gap: ROW_GAP }}>
      {rows.map((arm, row) => (
        <div
          key={arm.path_id}
          className="ic-conn__road"
          style={{ height: ROW_HEIGHT, boxShadow: withColor ? `inset 3px 0 0 ${colorOf(row)}` : undefined }}
        >
          <span className="ic-conn__road-name">{labelOf(arm)}</span>
          <span className="ic-conn__road-zone">{zoneOf(arm)}</span>
        </div>
      ))}
    </div>
  );

  return (
    <div className="ic-conn">
      <div className="ic-conn__grid">
        <span className="ic-conn__head">{t("interchange.connections.from")}</span>
        <span />
        <span className="ic-conn__head">{t("interchange.connections.to")}</span>
        {renderRoads(true)}
        <svg className="ic-conn__links" width={LINK_WIDTH} height={height} aria-hidden="true">
          {links.map((link) => {
            const color = colorOf(link.from);
            const y1 = startY.get(link)!;
            const y2 = endY.get(link)!;
            return (
              <g key={`${link.from}-${link.to}`}>
                <path
                  d={`M ${x0} ${y1} C ${midX} ${y1} ${midX} ${y2} ${endX} ${y2}`}
                  fill="none"
                  stroke={color}
                  strokeWidth={2}
                />
                <polygon
                  points={`${tipX},${y2} ${endX},${y2 - ARROW_HALF_WIDTH} ${endX},${y2 + ARROW_HALF_WIDTH}`}
                  fill={color}
                />
              </g>
            );
          })}
        </svg>
        {renderRoads(false)}
      </div>
      {links.length === 0 && <p className="agent-detail-muted">{t("interchange.connections.none")}</p>}
    </div>
  );
}
