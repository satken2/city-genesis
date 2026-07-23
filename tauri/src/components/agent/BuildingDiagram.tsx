/**
 * 建物の断面図SVGコンポーネント
 *
 * 建物を直方体で表現し、space categoryごとの容量を色分けして積み上げ表示する。
 * 数学の図形問題における寸法表示のようなスタイルで、建物の物理的属性を視覚的に示す。
 *
 * 構成:
 * - 前面: カテゴリごとに色分けされた積み上げ矩形
 * - 右側面・上面: 奥行き感を出すための平行四辺形
 * - 寸法線: 高さ（右側）と建築面積（底部）
 */

import { useTranslation } from "react-i18next";
import { useMasterData } from "../../contexts/MasterDataContext";
import { SPACE_CATEGORY_COLORS } from "../../local-constants";

type BuildingDiagramProps = {
  /** space category ID → 面積(㎡) のマッピング */
  capacity: Record<string, number>;
  /** 建物の高さ(m) */
  height: number;
  /** 建築面積(㎡) */
  baseArea: number;
};

/** 前面の左端X座標 */
const FRONT_X = 30;
/** 前面の上端Y座標 */
const FRONT_TOP_Y = 35;
/** 前面の幅 */
const FRONT_WIDTH = 100;
/** 前面の最大高さピクセル数 */
const FRONT_MAX_HEIGHT = 180;
/** 3DオフセットのX成分（右方向） */
const DX = 22;
/** 3DオフセットのY成分（上方向、負数） */
const DY = -16;
/** セグメント間の隙間ピクセル数 */
const SEGMENT_GAP = 1;
/** ラベル表示を省略するセグメントの最小高さピクセル数 */
const MIN_LABEL_HEIGHT = 22;
/** セグメントの最小高さピクセル数。極端に薄いセグメントでも視認できるように下限を設ける */
const MIN_SEG_HEIGHT = 2;

/**
 * hexカラーコードの各RGB成分をamountだけ増減する
 * amount > 0 で明るく、< 0 で暗くなる
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

  // 面積0のカテゴリを除外する。
  // これを怠ると、面積0でもMIN_SEG_HEIGHTの高さで描画され、
  // それぞれに別の色が割り当てられて建物上部に虹色の帯が現れる。
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
   * クランプにより合計が availableHeight を超過する場合、
   * 全セグメントを均一に縮小して FRONT_MAX_HEIGHT に収める。
   * これを怠るとセグメントが建物上部にはみ出し、
   * 複数の色が上部に積み重なって虹色の帯のように見えてしまう。
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

  // 前面のY座標を下から上に積み上げる
  let currentY = FRONT_TOP_Y + FRONT_MAX_HEIGHT;
  const positionedSegments = segments.map((seg) => {
    currentY -= seg.segHeight;
    const y = currentY;
    currentY -= SEGMENT_GAP;
    return { ...seg, y };
  });

  const frontBottom = FRONT_TOP_Y + FRONT_MAX_HEIGHT;
  const rightEdge = FRONT_X + FRONT_WIDTH;

  // 寸法線の座標
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
      {/* 右側面のセグメント（前面の後ろに見える奥行き部分） */}
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

      {/* 前面のセグメント（カテゴリごとの色分け） */}
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

      {/* 上面（最上段セグメントの天面） */}
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

      {/* 前面セグメント内のカテゴリ名と面積ラベル */}
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

      {/* ---- 高さ寸法線 ---- */}
      {/* 上端の水平タック線 */}
      <line
        x1={rightEdge + DX + 5}
        y1={dimTopY}
        x2={dimLineX + tickLen}
        y2={dimTopY}
        stroke="#93a9c0"
        strokeWidth="0.8"
      />
      {/* 下端の水平タック線 */}
      <line
        x1={rightEdge + DX + 5}
        y1={frontBottom}
        x2={dimLineX + tickLen}
        y2={frontBottom}
        stroke="#93a9c0"
        strokeWidth="0.8"
      />
      {/* 縦の寸法線 */}
      <line
        x1={dimLineX}
        y1={dimTopY}
        x2={dimLineX}
        y2={frontBottom}
        stroke="#93a9c0"
        strokeWidth="0.8"
        strokeDasharray="3,2"
      />
      {/* 高さラベル */}
      <text
        x={dimLineX + tickLen + 3}
        y={(dimTopY + frontBottom) / 2 + 5}
        fill="#c0d0e0"
        fontSize="12"
        fontWeight="600"
      >
        {t("building.heightLabel", { value: height })}
      </text>

      {/* ---- 建築面積寸法線 ---- */}
      {/* 左端の垂直タック線 */}
      <line
        x1={FRONT_X}
        y1={frontBottom + 5}
        x2={FRONT_X}
        y2={baseLabelY - 10}
        stroke="#93a9c0"
        strokeWidth="0.8"
      />
      {/* 右端の垂直タック線 */}
      <line
        x1={FRONT_X + FRONT_WIDTH}
        y1={frontBottom + 5}
        x2={FRONT_X + FRONT_WIDTH}
        y2={baseLabelY - 10}
        stroke="#93a9c0"
        strokeWidth="0.8"
      />
      {/* 水平の寸法線 */}
      <line
        x1={FRONT_X}
        y1={baseLabelY - 12}
        x2={FRONT_X + FRONT_WIDTH}
        y2={baseLabelY - 12}
        stroke="#93a9c0"
        strokeWidth="0.8"
        strokeDasharray="3,2"
      />
      {/* 建築面積ラベル */}
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
