/**
 * Shared types for the regulations window
 *
 * Keep only types referenced by both CityRegulationWindow and its tab components.
 * Logic such as unit conversion and key lists belongs in the owning tab's file.
 */

/**
 * Fields used by the regulations window from GET /api/v1/city.
 * Only speed limits and repair thresholds are needed.
 */
export type CityData = {
  city: {
    max_speed: Record<string, number>;
    repair_threshold?: number;
    repair_threshold_path?: number;
  };
};

/**
 * Response type for GET /api/v1/city/regulations.
 * Includes building regulations (volume ratio, height limit, building coverage ratio) and immigration
 * regulations.
 */
export type RegulationsData = {
  work_hours: number;
  limit_volume_ratio: number;
  limit_height: number;
  limit_coverage_ratio: number;
  // Immigration regulations (admission conditions set as player policy)
  immigration_age_min: number;
  immigration_age_max: number;
  immigration_enabled: boolean;
  immigration_allow_single: boolean;
  immigration_allow_married_no_children: boolean;
  immigration_allow_with_children: boolean;
  immigration_gender_policy: number; // 0 = unrestricted, 1 = male household heads only, 2 = female household heads only
  immigration_skill_thresholds: Record<string, number>; // skill_id (string) → minimum value
  immigration_quota: number; // Maximum people admitted per epoch. 0 = unlimited.
  // Social security policies (benefit rates as 0–1 ratios)
  retirement_age: number;
  unemployment_benefit_ratio: number;
  pension_benefit_ratio: number;
  livelihood_assistance_ratio: number;
};

/**
 * Draft social security policy values.
 * Retirement age in years, unemployment benefits/pensions as ratios of district standard wages, and income
 * support as a ratio of the shortfall.
 */
export type WelfareRegulations = {
  retirement_age: number;
  unemployment_benefit_ratio: number;
  pension_benefit_ratio: number;
  livelihood_assistance_ratio: number;
};

/**
 * Draft immigration regulation values.
 * Manage separately from building regulations because fields include bool/dict types.
 */
export type ImmigrationRegulations = {
  immigration_age_min: number;
  immigration_age_max: number;
  immigration_enabled: boolean;
  immigration_allow_single: boolean;
  immigration_allow_married_no_children: boolean;
  immigration_allow_with_children: boolean;
  immigration_gender_policy: number;
  immigration_skill_thresholds: Record<string, number>;
  immigration_quota: number;
};

/**
 * Keys for numeric building regulations.
 * Immigration includes bool/dict fields, so numeric building regulation handlers use only these keys.
 */
export type BuildingRegKey = "limit_volume_ratio" | "limit_height" | "limit_coverage_ratio";

/**
 * Draft speed limits.
 * Keys are path_variant IDs (numeric strings); values are km/h.
 */
export type SpeedMap = Record<string, number>;

/**
 * Path variant information from master data.
 * Shared by save payload construction in the window and list rendering in the speed limit tab.
 */
export type PathVariantInfo = {
  id: number;
  display_name: string;
  type: number;
  default_speed_limit: number;
};
