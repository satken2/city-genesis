/**
 * インターチェンジの接続図
 *
 * 左に出発する道路(腕)、右に到着する道路を同じ順で並べ、行ける組を矢印で結ぶ。
 * 経路探索はインターチェンジを1つの交差点として扱うので、中のランプの形は描かず
 * 「どの道路からどの道路へ行けるか」だけを示す。行けるかどうかはバックエンドが判定した
 * レコードの connections(鋭角転回せずに行ける組)をそのまま使う。
 */

import { useTranslation } from "react-i18next";
import { PATH_MOTORWAY, PATH_STREET } from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import type { InterchangeArmDetail } from "../../types";
import "./InterchangeConnectionDiagram.css";

/** 道路の箱の高さと間隔、矢印の帯の幅(px)。矢印の座標をこの値から計算するので、箱の寸法もここを正とする */
const ROW_HEIGHT = 44;
const ROW_GAP = 8;
const LINK_WIDTH = 120;
/** 1つの箱に出入りする矢印どうしの縦の間隔の上限と、箱の上下端から空ける余白(px) */
const PORT_SPACING = 8;
const PORT_MARGIN = 8;
const ARROW_LENGTH = 7;
const ARROW_HALF_WIDTH = 4;

/**
 * 出発する道路ごとの色。並び順は色覚の違いがあっても隣どうしを見分けられる順(暗い面で検証済み)なので、
 * 入れ替えないこと。本数が足りないときは循環させず、残りを FOLD_COLOR にまとめる。
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

/** 1つの箱に count 本の矢印が出入りするときの、箱の中心からの縦のずれ */
function portOffsets(count: number): number[] {
  if (count <= 1) return [0];
  const spacing = Math.min(PORT_SPACING, (ROW_HEIGHT - 2 * PORT_MARGIN) / (count - 1));
  return Array.from({ length: count }, (_, i) => (i - (count - 1) / 2) * spacing);
}

/**
 * 箱ごとに、そこへ出入りする矢印の端の縦位置を決める。
 * 相手の行の順に上から並べ、箱の近くで矢印どうしが交差しないようにする。
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

  // 左右を同じ順に並べ、同じ道路どうしが横に向かい合うようにする。
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
  // 地区は街路にだけ書く。高速道路は地区を持っていても空欄にする。
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
