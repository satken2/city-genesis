/** グラフ編集の式評価器が、コアの取り込みスクリプトが検査する Python の式と同じ値を返すことを確かめる。 */
import { compileFormula } from "../src/utils/formula";

const cases: [string, Record<string, number>, number][] = [
  ["(L^3 + (x - L)^3) / PF + 1", { L: 70, PF: 64000, x: 50 }, (70 ** 3 + (50 - 70) ** 3) / 64000 + 1],
  ["HEALTH_FACTOR1 * LIFESPAN_FACTOR^x / 16", { HEALTH_FACTOR1: 2e-5, LIFESPAN_FACTOR: 1.12, x: 80 }, 2e-5 * 1.12 ** 80 / 16],
  ["-x^2", { x: 3 }, -9],
  ["2^3^2", {}, 512],
  ["2^-1", {}, 0.5],
  ["10 - 4 - 3", {}, 3],
  ["12 / 3 / 2", {}, 2],
  ["max(a, 2 * b, 1.5e1) + min(1, 2)", { a: 3, b: 4 }, 16],
  ["exp(log(sqrt(abs(-16))))", {}, 4],
  ["pow(2, .5) ^ 2", {}, 2.0000000000000004],
];
for (const [src, vars, expected] of cases) {
  const actual = compileFormula(src)(vars);
  if (Math.abs(actual - expected) > 1e-12 * Math.max(1, Math.abs(expected))) {
    throw new Error(`${src}: ${actual} (期待値 ${expected})`);
  }
}
for (const bad of ["1 +", "(1", "1 2", "foo(1)", "a % 2", "a.b"]) {
  let threw = false;
  try {
    compileFormula(bad);
  } catch {
    threw = true;
  }
  if (!threw) throw new Error(`不正な式を受け付けた: ${bad}`);
}
console.log(`式評価器: ${cases.length}件の値と不正な式の拒否を確認`);
