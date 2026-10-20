/**
 * Resolve path icons by path_type, falling back to PATH_DEFAULT (0) if missing.
 *
 * path_type → filename:
 * - PATH_DEFAULT (0) → paths/path_0.png
 * - PATH_STREET (1) → paths/path_1.png
 * - PATH_MOTORWAY (2) → paths/path_2.png
 * - PATH_WALKWAY (3) → paths/path_3.png
 * - PATH_TRACK (4) → paths/path_4.png
 * - PATH_TRANSPORT (10) → paths/path_10.png
 */

import { PATH_TRANSPORT } from "../constants";

const pathModules = import.meta.glob<{ default: string }>(
  "../assets/images/paths/*.png",
  { eager: true }
);

/**
 * Map path variant IDs to individual icon filenames. master.yaml has no icon field; define mappings here for
 * variants with distinct designs from path_{path_type}.png, such as railway bridges and station platforms.
 */
const PATH_VARIANT_ICON_MAP: Record<number, string> = {
  413: "path_4b", // Railway bridge
  423: "path_4b", // Double-track railway bridge
  461: "path_4s", // Railway platform
  462: "path_4s", // Platform with stairs (side)
  463: "path_4s", // Container loading/unloading platform
  464: "path_4s", // Crushed stone loading/unloading platform
  465: "path_4s", // Crude oil loading/unloading platform
  471: "path_4s", // Railway platform (side)
  472: "path_4s", // Railway platform (island)
  502: "path_5s", // Monorail platform (side)
  602: "path_6s", // Ropeway station
};

/**
 * Return an icon path for the path type/variant. Prefer variant-specific images or mappings, then the path type
 * image, then PATH_DEFAULT (0).
 */
export function resolvePathIcon(pathType: number, variantId?: number): string {
  if (variantId != null) {
    const variantKey = `../assets/images/paths/path_${variantId}.png`;
    if (pathModules[variantKey]) {
      return pathModules[variantKey].default;
    }
    const mappedName = PATH_VARIANT_ICON_MAP[variantId];
    if (mappedName) {
      const mappedKey = `../assets/images/paths/${mappedName}.png`;
      if (pathModules[mappedKey]) {
        return pathModules[mappedKey].default;
      }
    }
  }
  const key = `../assets/images/paths/path_${pathType}.png`;
  if (pathModules[key]) {
    return pathModules[key].default;
  }
  const fallback = "../assets/images/paths/path_0.png";
  return pathModules[fallback]?.default ?? "";
}

const transportModules = import.meta.glob<{ default: string }>(
  "../assets/images/transport/*.png",
  { eager: true }
);

/**
 * Vehicle icon filenames for public transport section path variants (transit_master transit_path_id =
 * path_master PATH_TRANSPORT variant). With no master icon field, map variant IDs to
 * assets/images/transport/*.png here. Unsupported variants fall back to the generic public transport icon.
 */
const TRANSIT_VARIANT_ICON_NAME: Record<number, string> = {
  1001: "t_bus", // Bus line
  1002: "t_metro", // Metro line
  1003: "t_tram", // LRT line
  1004: "t_monorail", // Monorail line
  1005: "t_train", // Regional railway line
};

/**
 * Return the vehicle icon image path for a public transport section path variant.
 */
export function resolveTransitIcon(pathVariant: number): string {
  const name = TRANSIT_VARIANT_ICON_NAME[pathVariant];
  if (name) {
    const key = `../assets/images/transport/${name}.png`;
    if (transportModules[key]) {
      return transportModules[key].default;
    }
  }
  return resolvePathIcon(PATH_TRANSPORT);
}
