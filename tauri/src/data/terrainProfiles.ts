/** 表示名(実在の地名)を翻訳リソースに持つマップ。 */
const TERRAIN_PROFILE_IDS = [
  "flat", "yoron", "aogashima", "kumejima", "iejima", "ikishima", "naoshima",
  "chichijima", "santorini", "hongkong", "manhattan", "bahrain", "dakar", "chongqing",
  "xiamen", "dalian",
] as const;

export type TerrainProfileId = typeof TERRAIN_PROFILE_IDS[number];

export function getTerrainProfile(id: string): { id: TerrainProfileId } | null {
  const profileId = TERRAIN_PROFILE_IDS.find((profileId) => profileId === id);
  return profileId ? { id: profileId } : null;
}
