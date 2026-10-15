/**
 * 市民のスキル構成(/api/v1/templates/citizen-skills)。対象は定住済み・就労可能年齢・非在学の住民。
 * 比率は百分率で、high_skill_ratio は対象の住民に占める割合、majors の ratio は高度人材に占める割合。
 */
export type ScenarioSkillDistribution = {
  eligible_citizens: number;
  high_skill_citizens: number;
  high_skill_ratio: number;
  min_working_age: number;
  majors: { skill_id: number; citizens: number; ratio: number }[];
  updated_citizens?: number;
};

/** 人数の端数は小数部の大きい順に配り、合計人数を保つ。同値は入力順（スキルID順）。 */
export function apportionSkillCounts(total: number, weights: number[]): number[] {
  const sum = weights.reduce((a, b) => a + b, 0);
  if (sum <= 0) return weights.map(() => 0);
  const quotas = weights.map(weight => total * weight / sum);
  const counts = quotas.map(Math.floor);
  const order = quotas.map((quota, index) => ({ index, remainder: quota - counts[index] }))
    .sort((a, b) => b.remainder - a.remainder || a.index - b.index);
  const remaining = total - counts.reduce((a, b) => a + b, 0);
  for (let i = 0; i < remaining; i++) counts[order[i].index]++;
  return counts;
}
