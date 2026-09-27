/** 名称と自然地形の紹介文を翻訳リソースに持つマップ。気候はAPIの設定値を使う。 */
const TERRAIN_PROFILE_IDS = [
  "flat", "yoron", "aogashima", "kumejima", "iejima", "ikishima", "naoshima",
  "chichijima", "santorini", "hongkong", "manhattan", "bahrain", "dakar",
] as const;

export type TerrainProfileId = typeof TERRAIN_PROFILE_IDS[number];

export function getTerrainProfile(id: string): { id: TerrainProfileId } | null {
  const profileId = TERRAIN_PROFILE_IDS.find((profileId) => profileId === id);
  return profileId ? { id: profileId } : null;
}
