/**
 * Resolve agent icon images. Priority: 1. agent_variants/variant_{variant}.png (variant image). 2. For
 * TYPE_BUILDING, buildings/bd_{bd_type}.png (building image). 3. agent_types/type_{type}.png (type image).
 * Preload images with Vite import.meta.glob; resolve the matching image by key lookup at runtime.
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
 * Return only the variant-specific image without falling back to building/type images. Return an empty string
 * when absent so callers leave the icon slot empty.
 */
export function resolveAgentVariantIcon(variant: number): string {
  return variantModules[`../assets/images/agent_variants/variant_${variant}.png`]?.default ?? "";
}

/**
 * Get an agent display icon, falling back from variant image to building image to type image.
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
