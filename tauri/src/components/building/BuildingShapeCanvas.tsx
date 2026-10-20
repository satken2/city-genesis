/**
 * Building shape preview canvas
 *
 * Static preview on a square black canvas: parcel polygon in white outline,
 * building shape polygon in green.
 *
 * Coordinates:
 * The API returns [x, y] pairs; y corresponds to the game's horizontal Z axis.
 * Map x to screenX and y to screenY; automatically fit and center the whole shape.
 */

import { useEffect, useRef } from "react";
import "./BuildingShapeCanvas.css";

type BuildingShapeCanvasProps = {
  parcelPolygon: number[][] | null;
  buildingPolygon: number[][] | null;
  buildingHeight?: number;
  size?: number;
};

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

export function BuildingShapeCanvas({
  parcelPolygon,
  buildingPolygon,
  size = 200,
}: BuildingShapeCanvasProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext("2d");
    if (!ctx) return;

    // DPI support.
    const dpr = window.devicePixelRatio || 1;
    // Draw internally at the specified size, then stretch to 100% with CSS.
    canvas.width = size * dpr;
    canvas.height = size * dpr;
    ctx.scale(dpr, dpr);

    // Clear.
    ctx.fillStyle = "#000000";
    ctx.fillRect(0, 0, size, size);

    if (!parcelPolygon || parcelPolygon.length === 0) return;

    // Calculate the bounding box, including the building polygon.
    const allPolygons: number[][][] = [parcelPolygon];
    if (buildingPolygon && buildingPolygon.length > 0) {
      allPolygons.push(buildingPolygon);
    }

    const bounds = computeBounds(allPolygons);
    const rangeX = bounds.maxX - bounds.minX || 1;
    const rangeY = bounds.maxY - bounds.minY || 1;

    // 15% margin.
    const margin = 0.15;
    const innerSize = size * (1 - 2 * margin);
    const scale = Math.min(innerSize / rangeX, innerSize / rangeY);

    const offsetX = margin * size + (innerSize - rangeX * scale) / 2;
    const offsetY = margin * size + (innerSize - rangeY * scale) / 2;

    const transform = (x: number, y: number): [number, number] => [
      offsetX + (x - bounds.minX) * scale,
      offsetY + (y - bounds.minY) * scale,
    ];

    // Draw the parcel with a white outline.
    ctx.strokeStyle = "#ffffff";
    ctx.lineWidth = 2;
    drawPolygon(ctx, parcelPolygon, transform);
    ctx.stroke();

    // Draw the building shape in green.
    if (buildingPolygon && buildingPolygon.length > 0) {
      // Fill.
      ctx.fillStyle = "rgba(166, 227, 161, 0.3)";
      drawPolygon(ctx, buildingPolygon, transform);
      ctx.fill();

      // Outline.
      ctx.strokeStyle = "#a6e3a1";
      ctx.lineWidth = 2;
      drawPolygon(ctx, buildingPolygon, transform);
      ctx.stroke();
    }
  }, [parcelPolygon, buildingPolygon, size]);

  return (
    <div className="building-shape-canvas">
      <canvas ref={canvasRef} />
    </div>
  );
}
