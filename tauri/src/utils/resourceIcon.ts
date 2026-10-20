/**
 * Resolve natural resource icons by product_id. Mappings follow REROUCE_* constants in constants.ts:
 * - REROUCE_OIL (1) → resources/resource_1.png
 * - REROUCE_ORE (2) → resources/resource_2.png
 * - REROUCE_RARE_METAL (3) → resources/resource_3.png
 * - REROUCE_WOOD (4) → resources/resource_4.png
 * - REROUCE_WATER (5) → resources/resource_5.png
 *
 * Fall back to resource_1.png when no matching file exists.
 */

const resourceModules = import.meta.glob<{ default: string }>(
  "../assets/images/resources/*.png",
  { eager: true }
);

/**
 * Return the natural resource icon path for a product ID.
 */
export function resolveResourceIcon(productId: number): string {
  const key = `../assets/images/resources/resource_${productId}.png`;
  if (resourceModules[key]) {
    return resourceModules[key].default;
  }
  const fallback = "../assets/images/resources/resource_1.png";
  return resourceModules[fallback]?.default ?? "";
}
