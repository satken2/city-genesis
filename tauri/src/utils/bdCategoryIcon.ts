/**
 * Resolve building construction category (BD_CATEGORY_*) icons. Images use numeric category IDs at
 * `assets/images/building_categories/{id}.png`. Preload with Vite import.meta.glob and look up by key at
 * runtime. Missing categories return an empty string so callers display an empty icon slot.
 */

const categoryModules = import.meta.glob<{ default: string }>(
  "../assets/images/building_categories/*.png",
  { eager: true }
);

/** Return the icon image URL for a category ID, or an empty string if absent. */
export function resolveBdCategoryIcon(categoryId: number): string {
  const key = `../assets/images/building_categories/${categoryId}.png`;
  return categoryModules[key]?.default ?? "";
}
