/**
 * Resolve construction panel category (=path_type) icons. Images use numeric path_type in
 * assets/images/paths/path_{path_type}.png. Preload with Vite import.meta.glob and look up by key at runtime.
 * Missing categories intentionally show an empty slot; return an empty string without falling back to general
 * utils/pathIcon icons.
 */

const pathModules = import.meta.glob<{ default: string }>(
  "../assets/images/paths/*.png",
  { eager: true }
);

/** Return the category (path_type) icon URL, or an empty string if absent. */
export function resolvePathCategoryIcon(pathType: number): string {
  const keyWithPrefix = `../assets/images/paths/path_${pathType}.png`;
  const keyWithoutPrefix = `../assets/images/paths/${pathType}.png`;
  return pathModules[keyWithPrefix]?.default ?? pathModules[keyWithoutPrefix]?.default ?? "";
}
