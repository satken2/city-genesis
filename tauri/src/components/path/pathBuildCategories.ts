/**
 * パス建設パネルのカテゴリ(最上位のタイル)と、カテゴリごとのバリアント一覧
 *
 * カテゴリは path_type そのもので、対象と並び順は local-constants の PATH_BUILD_CATEGORIES が正。
 * 一覧はカードを縦に並べるだけで区切りを持たないため、並び順(軌道は構造順)もここで決める。
 */

import type { MasterData, PathMasterEntry } from "../../types";
import { PATH_STATION_HOUSE } from "../../constants";
import { PATH_BUILD_CATEGORIES, PATH_TYPES_WITH_STATION_HOUSE } from "../../local-constants";
import { isRailType, railVariantsInOrder } from "./railBuild";

export type PathVariantOption = { id: number; data: PathMasterEntry };

/** path_type のバリアントをID昇順で返す。 */
function variantsOfType(masterData: MasterData, type: number): PathVariantOption[] {
  const matches: PathVariantOption[] = [];
  for (const key in masterData.paths) {
    const data = masterData.paths[key];
    if (data.type === type) matches.push({ id: parseInt(key), data });
  }
  return matches.sort((a, b) => a.id - b.id);
}

/**
 * カテゴリのカード一覧に並べるバリアント。軌道は railVariantsInOrder の構造順、それ以外はID昇順。
 * 駅を持つ軌道(PATH_TYPES_WITH_STATION_HOUSE)は、プラットフォームの後ろに駅舎を並べる。
 */
export function buildCategoryVariants(masterData: MasterData | null, category: number): PathVariantOption[] {
  if (!masterData?.paths) return [];
  const variants = isRailType(category)
    ? railVariantsInOrder(masterData, category)
    : variantsOfType(masterData, category);
  if (PATH_TYPES_WITH_STATION_HOUSE.includes(category)) {
    variants.push(...variantsOfType(masterData, PATH_STATION_HOUSE));
  }
  return variants;
}

/**
 * タイルに並べるカテゴリ。その種別のバリアントを1つも持たないカテゴリは出さない(建物建設パネルと同じ)。
 * 駅舎だけでは敷けるものが無いので、足した駅舎は数えない。
 */
export function availableBuildCategories(masterData: MasterData | null): number[] {
  if (!masterData?.paths) return [];
  const present = new Set(Object.values(masterData.paths).map((entry) => entry.type));
  return PATH_BUILD_CATEGORIES.filter((category) => present.has(category));
}
