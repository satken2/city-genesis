/** 予算ウィンドウとコックピットの予算メーターが共有する、次エポックの支出見込みの導出を確かめる。 */
import { deriveBudgets, expectedSpending, ratioPercent } from "../src/utils/budgetForecast";

function expectEqual(label: string, actual: number, expected: number) {
  if (Math.abs(actual - expected) > 1e-6) throw new Error(`${label}: ${actual} (期待値 ${expected})`);
}

const required = { "2": 200, "6": 100, "8": 50, "10": 0 };
const payment = { "2": 30, "6": 100, "8": 50, "10": 0 };
const ratios = { "2": ratioPercent(1.0), "6": ratioPercent(0.5), "8": ratioPercent(undefined) };

// 現金が足りるとき: 予算 = 必要額 × 比率。支出は予算と支払見込みの小さい方
const rich = expectedSpending(deriveBudgets(ratios, required, {}, 1_000).budgets, payment);
expectEqual("在庫で賄える分は出ていかない", rich["2"], 30);
expectEqual("比率で絞った予算が上限になる", rich["6"], 50);
expectEqual("比率未設定は100%", rich["8"], 50);
expectEqual("貯蓄は支出ではない", rich["10"], 0);

// 現金不足(希望300に対して240): Tier の低い建設(8)から削られ、残りの不足は Tier 1 を等比で削る
const poor = expectedSpending(deriveBudgets(ratios, required, {}, 240).budgets, payment);
expectEqual("建設は全額削られる", poor["8"], 0);
expectEqual("削られた予算が上限になる", poor["6"], 48);
expectEqual("予算が支払見込みを上回る分は影響しない", poor["2"], 30);

// 債務超過: 予算は全カテゴリ0なので支出見込みも0
const broke = expectedSpending(deriveBudgets(ratios, required, {}, -1).budgets, payment);
expectEqual("債務超過の支出見込み", Object.values(broke).reduce((s, v) => s + v, 0), 0);

console.log("予算の支出見込み: 比率・支払見込み・資金不足の削減・債務超過を確認");
