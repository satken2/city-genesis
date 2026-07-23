/**
 * 土地区画プレビューキャンバス
 *
 * 複数のポリゴン（分割後の区画群）を異なる色で描画する静的プレビュー。
 * 分割元の元区画輪郭を白破線で表示することも可能。
 *
 * 座標系は BuildingShapeCanvas と同じ:
 *   APIは [x, y] ペアを返す。y はゲームの Z 軸に対応。
 *   キャンバス上では x → screenX, y → screenY にマッピングする。
 */

import { useEffect, useRef } from "react";
import "./ParcelPreviewCanvas.css";

export type ParcelPreviewItem = {
  polygon: number[][];
  center_point: number[];
  area: number;
};

type ParcelPreviewCanvasProps = {
  parcels: ParcelPreviewItem[] | null;
  originalPolygon?: number[][] | null;
  size?: number;
};

/** プレビュー描画用のカラーパレット */
const PREVIEW_COLORS = [
  { fill: "rgba(166, 227, 161, 0.3)", stroke: "#a6e3a1" },
  { fill: "rgba(137, 180, 250, 0.3)", stroke: "#89b4fa" },
  { fill: "rgba(249, 226, 175, 0.3)", stroke: "#f9e2af" },
  { fill: "rgba(243, 139, 168, 0.3)", stroke: "#f38ba8" },
  { fill: "rgba(203, 166, 247, 0.3)", stroke: "#cba6f7" },
  { fill: "rgba(148, 226, 213, 0.3)", stroke: "#94e2d5" },
  { fill: "rgba(250, 179, 135, 0.3)", stroke: "#fab387" },
  { fill: "rgba(166, 173, 200, 0.3)", stroke: "#a6adc8" },
];

function computeBounds(polygons: number[][][]): {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
} {
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;

  for (const poly of polygons) {
    for (const pt of poly) {
      if (pt[0] < minX) minX = pt[0];
      if (pt[1] < minY) minY = pt[1];
      if (pt[0] > maxX) maxX = pt[0];
      if (pt[1] > maxY) maxY = pt[1];
    }
  }

  return { minX, minY, maxX, maxY };
}

function drawPolygon(
  ctx: CanvasRenderingContext2D,
  polygon: number[][],
  transform: (x: number, y: number) => [number, number]
) {
  ctx.beginPath();
  for (let i = 0; i < polygon.length; i++) {
    const [sx, sy] = transform(polygon[i][0], polygon[i][1]);
    if (i === 0) ctx.moveTo(sx, sy);
    else ctx.lineTo(sx, sy);
  }
  ctx.closePath();
}

export function ParcelPreviewCanvas({
  parcels,
  originalPolygon,
  size = 200,
}: ParcelPreviewCanvasProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext("2d");
    if (!ctx) return;

    const dpr = window.devicePixelRatio || 1;
    canvas.width = size * dpr;
    canvas.height = size * dpr;
    ctx.scale(dpr, dpr);

    ctx.fillStyle = "#000000";
    ctx.fillRect(0, 0, size, size);

    if (!parcels || parcels.length === 0) return;

    const allPolygons: number[][][] = parcels.map((p) => p.polygon);
    if (originalPolygon && originalPolygon.length > 0) {
      allPolygons.push(originalPolygon);
    }

    const bounds = computeBounds(allPolygons);
    const rangeX = bounds.maxX - bounds.minX || 1;
    const rangeY = bounds.maxY - bounds.minY || 1;

    const margin = 0.15;
    const innerSize = size * (1 - 2 * margin);
    const scale = Math.min(innerSize / rangeX, innerSize / rangeY);

    const offsetX = margin * size + (innerSize - rangeX * scale) / 2;
    const offsetY = margin * size + (innerSize - rangeY * scale) / 2;

    const transform = (x: number, y: number): [number, number] => [
      offsetX + (x - bounds.minX) * scale,
      offsetY + (y - bounds.minY) * scale,
    ];

    // 元区画を白破線で描画
    if (originalPolygon && originalPolygon.length > 0) {
      ctx.setLineDash([6, 4]);
      ctx.strokeStyle = "#ffffff";
      ctx.lineWidth = 1.5;
      drawPolygon(ctx, originalPolygon, transform);
      ctx.stroke();
      ctx.setLineDash([]);
    }

    // 各区画を色分けして描画
    for (let i = 0; i < parcels.length; i++) {
      const color = PREVIEW_COLORS[i % PREVIEW_COLORS.length];

      ctx.fillStyle = color.fill;
      drawPolygon(ctx, parcels[i].polygon, transform);
      ctx.fill();

      ctx.strokeStyle = color.stroke;
      ctx.lineWidth = 2;
      drawPolygon(ctx, parcels[i].polygon, transform);
      ctx.stroke();
    }
  }, [parcels, originalPolygon, size]);

  return (
    <div className="parcel-preview-canvas">
      <canvas ref={canvasRef} />
    </div>
  );
}
