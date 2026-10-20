/**
 * Fleet section (line detail basic information tab)
 *
 * Stacks simple train side views to show how many trains run and how many cars each has.
 * One train per row; each car is a wide rectangular body with front/rear wheels,
 * without distinguishing leading/intermediate cars. Fill bodies with line color and overlay
 * a thin full-length coupling line to connect cars.
 *
 * Draw only complete operating trains (floor of procured cars / cars per train), and ensure
 * the total-car heading matches the illustration exactly; exclude leftover cars unable to form a train.
 * For the shortfall from required train_count at target headway, append outline-only dashed trains
 * so the missing amount is immediately visible.
 */

import { useTranslation } from "react-i18next";
import "./TransitFleetSection.css";

type TransitFleetSectionProps = {
  /** Cars per train (player setting). */
  carsPerTrain: number;
  /** Cars procured this epoch. */
  vehicleCount: number;
  /** Trains required to meet the target headway (backend-derived). */
  requiredTrains: number;
  /** Line color ("#rrggbb"), used for bodies, wheels, and coupling lines. */
  color: string;
};

/** Maximum illustrated train count. Express excess counts in text instead of more rows. */
const MAX_TRAINS_DRAWN = 12;

/** Base car width (px). Shrink longer trains to fit the panel width. */
const CAR_WIDTH_MAX = 44;
const CAR_WIDTH_MIN = 12;
const TRAIN_WIDTH_BUDGET = 290;

/** Body aspect ratio (height/width), making wide boxes resembling real rail vehicles. */
const CAR_ASPECT = 0.32;

export function TransitFleetSection({
  carsPerTrain,
  vehicleCount,
  requiredTrains,
  color,
}: TransitFleetSectionProps) {
  const { t } = useTranslation();

  const cars = Math.max(1, Math.round(carsPerTrain));
  const procured = Math.max(0, Math.round(vehicleCount));
  // Leftover cars cannot form an operating train, so round train counts down.
  const runningTrains = Math.floor(procured / cars);
  const missingTrains = Math.max(0, Math.round(requiredTrains) - runningTrains);
  const totalCars = runningTrains * cars;

  const drawnRunning = Math.min(runningTrains, MAX_TRAINS_DRAWN);
  const drawnMissing = Math.min(missingTrains, MAX_TRAINS_DRAWN - drawnRunning);
  const hidden = runningTrains + missingTrains - drawnRunning - drawnMissing;

  const carWidth = Math.max(CAR_WIDTH_MIN, Math.min(CAR_WIDTH_MAX, Math.floor(TRAIN_WIDTH_BUDGET / cars)));

  return (
    <section className="agent-detail-section">
      <h3 className="agent-detail-section-title">{t("transit.fleet")}</h3>

      <div className="transit-fleet__header">
        <span className="transit-fleet__total">{t("transit.fleetTotalCars", { count: totalCars })}</span>
        <span className="transit-fleet__formation">
          {t("transit.fleetFormation", { cars, trains: runningTrains })}
        </span>
      </div>

      {runningTrains === 0 && missingTrains === 0 ? (
        <p className="agent-detail-muted">{t("transit.fleetNone")}</p>
      ) : (
        <div className="transit-fleet__trains">
          {Array.from({ length: drawnRunning }, (_, i) => (
            <TrainGlyph key={`run-${i}`} cars={cars} carWidth={carWidth} color={color} />
          ))}
          {Array.from({ length: drawnMissing }, (_, i) => (
            <TrainGlyph key={`miss-${i}`} cars={cars} carWidth={carWidth} color={color} missing />
          ))}
        </div>
      )}

      {hidden > 0 && (
        <p className="transit-fleet__note">{t("transit.fleetMoreTrains", { count: hidden })}</p>
      )}
      {missingTrains > 0 && (
        <p className="transit-fleet__note transit-fleet__note--warn">
          {t("transit.fleetShortage", { count: missingTrains })}
        </p>
      )}
    </section>
  );
}

type TrainGlyphProps = {
  cars: number;
  carWidth: number;
  color: string;
  /** Train unavailable due to vehicle shortage. Draw bodies as faded dashed outlines only. */
  missing?: boolean;
};

/**
 * One train's side view: wide body boxes and front/rear wheels, without leading/intermediate
 * car distinctions. Overlay a thin coupling line through the full train around mid-body height,
 * bridging gaps so it reads as one train. Coordinates map 1:1 to px;
 * use the actual size determined by car count directly as the viewBox.
 */
function TrainGlyph({ cars, carWidth, color, missing = false }: TrainGlyphProps) {
  const gap = Math.max(2, Math.round(carWidth * 0.12));
  const bodyHeight = Math.max(6, Math.round(carWidth * CAR_ASPECT));
  const wheelR = Math.max(2, Math.round(carWidth * 0.08));
  const bodyY = 1;
  const wheelY = bodyY + bodyHeight + wheelR - 1;
  const width = cars * carWidth + (cars - 1) * gap;
  const height = wheelY + wheelR + 1;
  const couplerHeight = Math.max(2, Math.round(bodyHeight * 0.3));
  const couplerY = bodyY + (bodyHeight - couplerHeight) / 2;

  const bodyProps = missing
    ? { fill: "none", stroke: color, strokeWidth: 1, strokeDasharray: "3 3" }
    : { fill: color };

  return (
    <svg
      className={`transit-fleet__train${missing ? " transit-fleet__train--missing" : ""}`}
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      role="presentation"
    >
      {Array.from({ length: cars }, (_, i) => {
        const x = i * (carWidth + gap);
        return (
          <g key={i}>
            <rect x={x} y={bodyY} width={carWidth} height={bodyHeight} {...bodyProps} />
            <circle cx={x + carWidth * 0.22} cy={wheelY} r={wheelR} fill={color} />
            <circle cx={x + carWidth * 0.78} cy={wheelY} r={wheelR} fill={color} />
          </g>
        );
      })}
      {/*
 * Coupling line. Draw after bodies to overlay and bridge car gaps.
 * Do not draw it for unavailable trains, which are not coupled.
 */}
      {cars > 1 && !missing && (
        <rect x={0} y={couplerY} width={width} height={couplerHeight} fill={color} />
      )}
    </svg>
  );
}
