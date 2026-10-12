import type { PublicServiceSupply } from "../../types";

/** 仮想暦の1年は12か月×30日。エポック数への換算だけに使い、実績量は換算しない。 */
const CALENDAR_YEAR_DAYS = 360;
const TREND_YEARS = 5;
const FORECAST_YEARS = 10;

/** 内部の期数を、プレイヤーへ表示する仮想暦の年数に換算する。 */
export function epochsToYears(epochs: number, simulationDays: number): number {
  return epochs * simulationDays / CALENDAR_YEAR_DAYS;
}

export type PublicServiceProductHistory = {
  product_id: number;
  total_order: number[];
  total_production: number[];
};

/** 経済推移APIのうち、公共サービスのグラフで使用する項目。 */
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
  /** 回帰に使えた実績の期数。2期未満なら見込みを描かない。 */
  sampleCount: number;
  /** 予測期間内の容量到達までの期数。既に到達していれば0、到達しなければnull。 */
  capacityReachedIn: number | null;
  /** 通常サービスと高圧財のうち、供給を制限する側の現在容量。 */
  capacityLimit: number;
};

/**
 * 保存済みの実績すべてと、直近の対象期間の需要の傾きを延ばした見込みを組み立てる。
 * 履歴APIの末尾は需給APIの現在値より前の期なので、最新の需給を末尾に加える。
 * 見込みは最新の需要から始め、施設の増設・稼働条件の変化は織り込まない。
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

/** 大きい方の施設容量で実績が潰れないよう、表示量と制約側の容量に上の余白を確保する。 */
export function publicServiceChartUpperBound(trend: PublicServiceTrend): number {
  let maximum = trend.capacityLimit;
  for (const point of trend.points) {
    maximum = Math.max(maximum, point.demand ?? 0, point.production ?? 0, point.forecast ?? 0);
  }
  return maximum > 0 ? maximum / 0.75 : 1;
}
