/**
 * 天然資源のアイコン画像を解決するユーティリティ
 *
 * product_id の値に対応する画像ファイルを返す。
 * product_id → ファイル名の対応は constants.ts の REROUCE_* 定数に基づく:
 * - REROUCE_OIL        (1) → resources/resource_1.png
 * - REROUCE_ORE        (2) → resources/resource_2.png
 * - REROUCE_RARE_METAL (3) → resources/resource_3.png
 * - REROUCE_WOOD       (4) → resources/resource_4.png
 * - REROUCE_WATER      (5) → resources/resource_5.png
 *
 * 対応するファイルが存在しない場合は resource_1.png にフォールバックする。
 */

const resourceModules = import.meta.glob<{ default: string }>(
  "../assets/images/resources/*.png",
  { eager: true }
);

/**
 * 製品IDに対応する天然資源アイコン画像パスを返す
 */
export function resolveResourceIcon(productId: number): string {
  const key = `../assets/images/resources/resource_${productId}.png`;
  if (resourceModules[key]) {
    return resourceModules[key].default;
  }
  const fallback = "../assets/images/resources/resource_1.png";
  return resourceModules[fallback]?.default ?? "";
}
