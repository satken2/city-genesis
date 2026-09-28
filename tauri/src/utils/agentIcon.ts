/**
 * 経済主体のアイコン画像を解決するユーティリティ
 *
 * 優先順位:
 * 1. agent_variants/variant_{variant}.png (バリアント画像)
 * 2. TYPE_BUILDING の場合、buildings/bd_{bd_type}.png (建物画像)
 * 3. agent_types/type_{type}.png (タイプ画像)
 *
 * Vite の import.meta.glob で画像を事前一括取得し、
 * ランタイムではキー検索で該当画像を特定する。
 */

import { TYPE_BUILDING } from "../constants";

const variantModules = import.meta.glob<{ default: string }>(
  "../assets/images/agent_variants/*.png",
  { eager: true }
);

const buildingModules = import.meta.glob<{ default: string }>(
  "../assets/images/buildings/*.png",
  { eager: true }
);

const typeModules = import.meta.glob<{ default: string }>(
  "../assets/images/agent_types/*.png",
  { eager: true }
);

/**
 * バリアント専用の画像だけを取得する(建物画像・タイプ画像へはフォールバックしない)。
 * 専用画像の無いバリアントは空文字を返すので、呼び出し側でアイコン枠を空けておく。
 */
export function resolveAgentVariantIcon(variant: number): string {
  return variantModules[`../assets/images/agent_variants/variant_${variant}.png`]?.default ?? "";
}

/**
 * 経済主体の表示アイコンを取得する
 * バリアント画像 → 建物画像 → タイプ画像の順にフォールバックする
 */
export function resolveAgentIcon(
  variant: number,
  type: number,
  isBd: boolean,
  bdType: number
): string {
  const variantIcon = resolveAgentVariantIcon(variant);
  if (variantIcon) {
    return variantIcon;
  }

  if (isBd || type === TYPE_BUILDING) {
    const bdKey = `../assets/images/buildings/bd_${bdType}.png`;
    if (buildingModules[bdKey]) {
      return buildingModules[bdKey].default;
    }
  }

  const typeKey = `../assets/images/agent_types/type_${type}.png`;
  if (typeModules[typeKey]) {
    return typeModules[typeKey].default;
  }

  const fallbackKey = "../assets/images/agent_types/type_0.png";
  return typeModules[fallbackKey]?.default ?? "";
}
