/**
 * Path construction panel categories (top-level tiles) and variants in each category.
 *
 * Each category is a path_type with variants in the master (except PATH_TYPES_HIDDEN_IN_BUILD_PANEL).
 * Its cards list only variants of that path_type. Tiles are grouped into roads, tracks, and infrastructure.
 * Grouping is defined here: path types in neither PATH_TYPES_TRACK_GROUP nor PATH_TYPES_INFRA belong to roads.
 */

import type { MasterData, PathMasterEntry } from "../../types";
import { PATH_TRACK } from "../../constants";
import { PATH_TYPES_HIDDEN_IN_BUILD_PANEL, PATH_TYPES_TRACK_GROUP, isInfraPathType } from "../../local-constants";
import { isRailType, railVariantsInOrder } from "./railBuild";

export type PathVariantOption = { id: number; data: PathMasterEntry };

export type PathVariantTab = {
  id: string;
  labelKey: string;
  icon: "track" | "passenger" | "freight";
  variants: PathVariantOption[];
};

type VariantTabBuilder = (masterData: MasterData, variants: PathVariantOption[]) => PathVariantTab[];

/** Identify freight-only platforms from supported stopping uses; list mixed passenger/freight platforms under passengers. */
function buildRailwayTabs(masterData: MasterData, variants: PathVariantOption[]): PathVariantTab[] {
  const passengerStops = new Set<number>();
  const freightStops = new Set<number>();
  for (const transit of Object.values(masterData.transit ?? {})) {
    const stops = transit.freight ? freightStops : passengerStops;
    for (const id of transit.stop_path_variants) stops.add(id);
  }
  const hasPlatform = ({ data }: PathVariantOption) => (data.platform_width ?? 0) > 0;
  const freightOnly = ({ id }: PathVariantOption) => freightStops.has(id) && !passengerStops.has(id);
  return [
    { id: "track", labelKey: "path_construction.variantTab.track", icon: "track", variants: variants.filter(v => !hasPlatform(v)) },
    { id: "passenger", labelKey: "path_construction.variantTab.passenger", icon: "passenger", variants: variants.filter(v => hasPlatform(v) && !freightOnly(v)) },
    { id: "freight", labelKey: "path_construction.variantTab.freight", icon: "freight", variants: variants.filter(v => hasPlatform(v) && freightOnly(v)) },
  ];
}

/** Register classifications per category. Rendering code does not contain type or classification conditions. */
const CATEGORY_TAB_BUILDERS: Partial<Record<number, VariantTabBuilder>> = {
  [PATH_TRACK]: buildRailwayTabs,
};

/** Use the existing list for categories without registered tabs. Hide empty classifications. */
export function buildCategoryVariantTabs(masterData: MasterData | null, category: number): PathVariantTab[] {
  if (!masterData) return [];
  return (CATEGORY_TAB_BUILDERS[category]?.(masterData, buildCategoryVariants(masterData, category)) ?? [])
    .filter(tab => tab.variants.length > 0);
}

/** Groups of category tiles, in display order. */
export const PATH_BUILD_GROUPS = ["road", "track", "infra"] as const;
export type PathBuildGroup = (typeof PATH_BUILD_GROUPS)[number];

function groupOf(pathType: number): PathBuildGroup {
  if (isInfraPathType(pathType)) return "infra";
  if (PATH_TYPES_TRACK_GROUP.includes(pathType)) return "track";
  return "road";
}

/** Hide template_only variants outside scenario authoring mode (the core also rejects their creation). */
export function buildablePathMaster(masterData: MasterData | null, scenarioMode: boolean): MasterData | null {
  if (!masterData || scenarioMode) return masterData;
  return { ...masterData, paths: Object.fromEntries(Object.entries(masterData.paths).filter(([, entry]) => !entry.template_only)) };
}

/** Return variants of path_type in ascending ID order. */
function variantsOfType(masterData: MasterData, type: number): PathVariantOption[] {
  const matches: PathVariantOption[] = [];
  for (const key in masterData.paths) {
    const data = masterData.paths[key];
    if (data.type === type) matches.push({ id: parseInt(key), data });
  }
  return matches.sort((a, b) => a.id - b.id);
}

/** Variants for category cards. Tracks use structural order from railVariantsInOrder; others use ascending IDs. */
export function buildCategoryVariants(masterData: MasterData | null, category: number): PathVariantOption[] {
  if (!masterData?.paths) return [];
  return isRailType(category) ? railVariantsInOrder(masterData, category) : variantsOfType(masterData, category);
}

/** Return tile categories grouped and sorted by ascending path_type. Omit groups with no categories. */
export function buildCategoryGroups(masterData: MasterData | null): { group: PathBuildGroup; categories: number[] }[] {
  if (!masterData?.paths) return [];
  const categories = [...new Set(Object.values(masterData.paths).map((entry) => entry.type))]
    .filter((type) => !PATH_TYPES_HIDDEN_IN_BUILD_PANEL.includes(type))
    .sort((a, b) => a - b);
  return PATH_BUILD_GROUPS
    .map((group) => ({ group, categories: categories.filter((type) => groupOf(type) === group) }))
    .filter(({ categories: members }) => members.length > 0);
}
