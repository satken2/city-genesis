import assert from "node:assert/strict";
import { buildPublicServiceTrend, epochsToYears, publicServiceChartUpperBound } from "../src/components/budget/publicServiceTrend";

assert.equal(epochsToYears(12, 30), 1);
assert.equal(epochsToYears(1, 30) * 12, 1, "30日分は1か月として表示する");
assert.equal(epochsToYears(360 / 7, 7), 1, "7日進行も仮想暦の年数に換算する");
assert.equal(epochsToYears(-24, 30), -2, "過去の履歴は現在からの負の年数で表示する");

const supply = (demand: number, capacity = 200) => ({ product_id: 303, demand, production: demand - 5, supply_capacity: capacity });
const epochs = Array.from({ length: 80 }, (_, i) => i + 1);
const history = {
  product_id: 303,
  total_order: epochs.map(epoch => epoch < 22 ? 10000 : epoch * 2),
  total_production: epochs.map(epoch => epoch - 5),
};
const monthly = buildPublicServiceTrend(epochs, history, 81, supply(162), 30);
assert.equal(monthly.trendEpochs, 60, "30日エポックの直近5年は60期");
assert.equal(monthly.forecastEpochs, 120, "30日エポックの未来10年は120期");
assert.equal(monthly.sampleCount, 60);
assert.equal(monthly.points[0].epoch, 1, "回帰対象より古い実績もすべて表示する");
assert.equal(monthly.points[0].demand, 10000);
assert.equal(monthly.points.find(p => p.epoch === 81)?.forecast, 162, "予測を最新の実績からつなぐ");
assert.equal(monthly.points[monthly.points.length - 1].forecast, 402, "5年以上前の急増を回帰から除き、未来10年まで延ばす");
assert.equal(monthly.points[monthly.points.length - 1].epoch, 201);
assert.equal(monthly.capacityReachedIn, 19);
assert.equal(buildPublicServiceTrend(epochs, history, 81, supply(162, 500), 30).capacityReachedIn, null,
  "予測期間より先の容量到達を期間内と判定しない");
assert.equal(buildPublicServiceTrend(epochs, history, 81, supply(162, 180), 30).capacityReachedIn, 9);

const weeklyHistory = { ...history, total_order: epochs.map(epoch => epoch * 2) };
const weekly = buildPublicServiceTrend(epochs, weeklyHistory, 81, supply(162), 7);
assert.equal(weekly.trendEpochs, 258, "7日エポックの直近5年は258期");
assert.equal(weekly.forecastEpochs, 515, "7日エポックの未来10年は515期");
assert.equal(weekly.sampleCount, 81, "5年分に足りなければ取得できた実績をすべて使う");
assert.equal(weekly.points[0].epoch, 1);
assert.equal(weekly.points[weekly.points.length - 1].forecast, 1192);
assert.equal(weekly.points[weekly.points.length - 1].epoch, 596, "履歴が短くても予測期間を縮めない");
assert.equal(weekly.capacityReachedIn, 19);

const longEpochs = Array.from({ length: 300 }, (_, i) => i + 1);
const longHistory = { product_id: 303, total_order: longEpochs.map(epoch => epoch < 44 ? 10000 : epoch * 2),
  total_production: longEpochs.map(epoch => epoch - 5) };
const longWeekly = buildPublicServiceTrend(longEpochs, longHistory, 301, supply(602, 1000), 7);
assert.equal(longWeekly.sampleCount, 258);
assert.equal(longWeekly.points[0].epoch, 1, "7日エポックでも回帰対象外の履歴を残す");
assert.equal(longWeekly.points[longWeekly.points.length - 1].forecast, 1632, "5年以上前の実績を表示しても回帰へ混ぜない");

const noHistory = buildPublicServiceTrend([], undefined, 1, supply(10), 30);
assert.equal(noHistory.sampleCount, 1);
assert.ok(noHistory.points.every(p => p.forecast === undefined), "実績1期からは見込みを作らない");
assert.equal(buildPublicServiceTrend([], undefined, 0, supply(10), 30).points.length, 0, "未実行の初期値を実績に含めない");
assert.equal(buildPublicServiceTrend([], undefined, 1, supply(201), 30).capacityReachedIn, 0, "履歴不足でも現在の容量到達は表示できる");

const flat = { product_id: 303, total_order: [100, 100], total_production: [90, 95] };
const flatTrend = buildPublicServiceTrend([1, 2], flat, 3, supply(100), 30);
assert.equal(flatTrend.capacityReachedIn, null);
assert.ok(flatTrend.points.slice(3).every(p => p.forecast === 100));
const upstreamLimit = buildPublicServiceTrend([1, 2], { ...flat, total_order: [10, 20] }, 3, supply(30, 200), 30, 50);
assert.equal(upstreamLimit.capacityLimit, 50);
assert.equal(upstreamLimit.capacityReachedIn, 2, "高圧財側の容量が小さければ、そちらの到達時期を案内する");
const upstreamLarge = buildPublicServiceTrend([1, 2], flat, 3, supply(100, 200), 30, 1000000);
assert.equal(publicServiceChartUpperBound(upstreamLarge), 200 / 0.75, "大きい方の容量は縦軸を押し広げず、基準量に25%の高さの余白を残す");
assert.equal(publicServiceChartUpperBound(buildPublicServiceTrend([1, 2], flat, 3, supply(100, 1000000), 30, 200)), 200 / 0.75,
  "通常財側の容量が大きくても、小さい高圧財側の容量を縦軸の基準にする");
assert.equal(publicServiceChartUpperBound(monthly), 10000 / 0.75, "過去の実績も縦軸の範囲に収める");
assert.equal(publicServiceChartUpperBound(buildPublicServiceTrend([], undefined, 0, supply(0, 0), 30)), 1);
const declining = { ...flat, total_order: [100, 50] };
const declineTrend = buildPublicServiceTrend([1, 2], declining, 3, supply(10), 30);
assert.equal(declineTrend.capacityReachedIn, null);
assert.equal(declineTrend.points[declineTrend.points.length - 1].forecast, 0, "需要の見込みを負の量にしない");

const overlapping = buildPublicServiceTrend([1, 2, 3], { ...flat, total_order: [10, 20, 999], total_production: [5, 15, 999] }, 3, supply(30), 30);
assert.equal(overlapping.sampleCount, 3, "現在値と同じ期の履歴を二重に足さない");
assert.equal(overlapping.points.find(p => p.epoch === 3)?.demand, 30);
assert.deepEqual(history.total_order, epochs.map(epoch => epoch < 22 ? 10000 : epoch * 2), "グラフの組み立てでAPIの履歴配列を書き換えない");
console.log("PASS: full service history, five-year trend and ten-year forecast");
