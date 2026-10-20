import type { PublicServiceSupply } from "../../types";

/** A virtual calendar year has 12 months of 30 days. Used only to convert epoch counts, never actual quantities. */
const CALENDAR_YEAR_DAYS = 360;
const TREND_YEARS = 5;
const FORECAST_YEARS = 10;

/** Convert internal period counts into virtual calendar years shown to players. */
export function epochsToYears(epochs: number, simulationDays: number): number {
  return epochs * simulationDays / CALENDAR_YEAR_DAYS;
}

export type PublicServiceProductHistory = {
  product_id: number;
  total_order: number[];
  total_production: number[];
};

/** Economic trends API fields used in the public service chart. */
export type PublicServiceHistoryData = {
  epoch: number;
  epochs: number[];
  products: PublicServiceProductHistory[];
};

export type PublicServiceTrendPoint = {
  epoch: number;
  demand?: number;
  production?: number;
  forecast?: number;
};

export type PublicServiceTrend = {
  points: PublicServiceTrendPoint[];
  currentEpoch: number;
  trendYears: number;
  forecastYears: number;
  trendEpochs: number;
  forecastEpochs: number;
  /** Number of actual periods usable for regression. Omit forecasts if fewer than two. */
  sampleCount: number;
  /** Periods until capacity is reached within the forecast horizon: 0 if already reached, null if never reached. */
  capacityReachedIn: number | null;
  /** Current capacity of whichever limits supply: ordinary service or high-pressure goods. */
  capacityLimit: number;
};

/**
 * Build all saved actuals plus a forecast extending the demand slope over the recent target period.
 * History ends before the supply/demand API's current period, so append the latest supply/demand.
 * Start the forecast at latest demand; do not account for facility expansion or operating condition changes.
 */
export function buildPublicServiceTrend(
  epochs: number[],
  history: PublicServiceProductHistory | undefined,
  currentEpoch: number,
  supply: PublicServiceSupply | undefined,
  simulationDays: number,
  infrastructureCapacity?: number,
): PublicServiceTrend {
  const trendEpochs = Math.ceil(CALENDAR_YEAR_DAYS * TREND_YEARS / simulationDays);
  const forecastEpochs = Math.ceil(CALENDAR_YEAR_DAYS * FORECAST_YEARS / simulationDays);
  const points: PublicServiceTrendPoint[] = epochs
    .map((epoch, i) => ({ epoch, demand: history?.total_order[i], production: history?.total_production[i] }))
    .filter((point) => point.epoch < currentEpoch && Number.isFinite(point.demand) && Number.isFinite(point.production));
  if (currentEpoch > 0 && supply) {
    points.push({ epoch: currentEpoch, demand: supply.demand, production: supply.production });
  }
  const recent = points.slice(-trendEpochs);
  const capacityLimit = Math.min(supply?.supply_capacity ?? 0, infrastructureCapacity ?? supply?.supply_capacity ?? 0);
  const result: PublicServiceTrend = {
    points,
    currentEpoch,
    trendYears: TREND_YEARS,
    forecastYears: FORECAST_YEARS,
    trendEpochs,
    forecastEpochs,
    sampleCount: recent.length,
    capacityReachedIn: null,
    capacityLimit,
  };
  const current = recent[recent.length - 1];
  if (!current || !supply) return result;
  if (current.demand! >= capacityLimit && current.demand! > 0) result.capacityReachedIn = 0;
  if (recent.length < 2) return result;

  const meanEpoch = recent.reduce((sum, p) => sum + p.epoch, 0) / recent.length;
  const meanDemand = recent.reduce((sum, p) => sum + p.demand!, 0) / recent.length;
  let covariance = 0;
  let variance = 0;
  for (const point of recent) {
    covariance += (point.epoch - meanEpoch) * (point.demand! - meanDemand);
    variance += (point.epoch - meanEpoch) ** 2;
  }
  const slope = variance > 0 ? covariance / variance : 0;
  current.forecast = current.demand;
  for (let offset = 1; offset <= forecastEpochs; offset++) {
    points.push({ epoch: current.epoch + offset, forecast: Math.max(0, current.demand! + slope * offset) });
  }
  if (result.capacityReachedIn === null && slope > 0) {
    const untilCapacity = (capacityLimit - current.demand!) / slope;
    if (untilCapacity >= 0 && untilCapacity <= forecastEpochs) result.capacityReachedIn = untilCapacity;
  }
  return result;
}

/** Leave headroom above displayed quantities and limiting capacity so larger facility capacity does not flatten actual values. */
export function publicServiceChartUpperBound(trend: PublicServiceTrend): number {
  let maximum = trend.capacityLimit;
  for (const point of trend.points) {
    maximum = Math.max(maximum, point.demand ?? 0, point.production ?? 0, point.forecast ?? 0);
  }
  return maximum > 0 ? maximum / 0.75 : 1;
}
