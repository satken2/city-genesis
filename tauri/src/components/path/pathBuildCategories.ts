/**
 * パス建設パネルのカテゴリ(最上位のタイル)と、カテゴリごとのバリアント一覧
 *
 * カテゴリは path_type そのもので、マスターにバリアントがある path_type から作る
 * (PATH_TYPES_HIDDEN_IN_BUILD_PANEL は出さない)。カードの一覧にはそのカテゴリの path_type のバリアントだけを並べる。
 * タイルは道路・軌道・インフラの3区分に分けて並べる。区分はここで決め打ちしていて、
 * 軌道(PATH_TYPES_TRACK_GROUP)・インフラ(PATH_TYPES_INFRA)のどちらにも入らない path_type は道路に入れる。
 */

import type { MasterData, PathMasterEntry } from "../../types";
import { PATH_TYPES_HIDDEN_IN_BUILD_PANEL, PATH_TYPES_TRACK_GROUP, isInfraPathType } from "../../local-constants";
import { isRailType, railVariantsInOrder } from "./railBuild";

export type PathVariantOption = { id: number; data: PathMasterEntry };

/** カテゴリのタイルをまとめる区分。この順に並べる。 */
export const PATH_BUILD_GROUPS = ["road", "track", "infra"] as const;
export type PathBuildGroup = (typeof PATH_BUILD_GROUPS)[number];

function groupOf(pathType: number): PathBuildGroup {
  if (isInfraPathType(pathType)) return "infra";
  if (PATH_TYPES_TRACK_GROUP.includes(pathType)) return "track";
  return "road";
}

/** path_type のバリアントをID昇順で返す。 */
function variantsOfType(masterData: MasterData, type: number): PathVariantOption[] {
  const matches: PathVariantOption[] = [];
  for (const key in masterData.paths) {
    const data = masterData.paths[key];
    if (data.type === type) matches.push({ id: parseInt(key), data });
  }
  return matches.sort((a, b) => a.id - b.id);
}

/** カテゴリのカード一覧に並べるバリアント。軌道は railVariantsInOrder の構造順、それ以外はID昇順。 */
export function buildCategoryVariants(masterData: MasterData | null, category: number): PathVariantOption[] {
  if (!masterData?.paths) return [];
  return isRailType(category) ? railVariantsInOrder(masterData, category) : variantsOfType(masterData, category);
}

/** タイルに並べるカテゴリを区分ごとに path_type 昇順で返す。カテゴリが1つも無い区分は返さない。 */
export function buildCategoryGroups(masterData: MasterData | null): { group: PathBuildGroup; categories: number[] }[] {
  if (!masterData?.paths) return [];
  const categories = [...new Set(Object.values(masterData.paths).map((entry) => entry.type))]
    .filter((type) => !PATH_TYPES_HIDDEN_IN_BUILD_PANEL.includes(type))
    .sort((a, b) => a - b);
  return PATH_BUILD_GROUPS
    .map((group) => ({ group, categories: categories.filter((type) => groupOf(type) === group) }))
    .filter(({ categories: members }) => members.length > 0);
}
