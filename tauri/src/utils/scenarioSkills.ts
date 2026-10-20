/**
 * Citizen skill composition (/api/v1/scenario-tools/citizen-skills), targeting settled, working-age residents
 * not attending school. Ratios are percentages: high_skill_ratio is of target residents; majors ratio is of
 * highly skilled residents.
 */
export type ScenarioSkillDistribution = {
  eligible_citizens: number;
  high_skill_citizens: number;
  high_skill_ratio: number;
  min_working_age: number;
  majors: { skill_id: number; citizens: number; ratio: number }[];
  updated_citizens?: number;
};

/** Allocate fractional people from largest remainder to smallest, preserving the total. Break ties by input order (skill ID order). */
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
