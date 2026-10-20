/**
 * Speed limit tab
 *
 * Set speed limits (km/h) per path_variant (road variant).
 * - Display the city.max_speed override, or default_speed_limit from master data if absent.
 * - Group by path_type in collapsible accordions, initially all expanded.
 * - Highlight inputs/names when the speed differs from the master default.
 *
 * Saving immediately affects existing paths, except those with individual speed overrides.
 */

import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMasterData } from "../../../contexts/MasterDataContext";
import { PATH_MOTORWAY, PATH_STREET } from "../../../constants";
import type { PathVariantInfo, SpeedMap } from "../regulationTypes";

/**
 * Allowlisted path_type IDs supporting speed limits
 *
 * Eligible types are roads carrying cars (streets/highways) and underlying paths carrying public transport
 * vehicles (tracks/monorails).
 * Exclude sidewalks, which use pedestrian speed parameters. LRT on pedestrian roads uses the variant's default
 * speed limit, with individual overrides available in path details.
 * People movers use variant-specific cruising speeds. Public transport section paths, infrastructure, and
 * station buildings have no traversal time and are excluded.
 *
 * Use an allowlist so adding a path type to master data cannot silently expose an ineffective setting.
 */
const EDITABLE_PATH_TYPE_IDS = new Set([PATH_STREET, PATH_MOTORWAY]);

/** Default speed (km/h) for variants without default_speed_limit in master data */
export const FALLBACK_SPEED_LIMIT = 40;

export function SpeedLimitTab({
  pathVariants,
  cityMaxSpeed,
  pendingSpeeds,
  onChange,
}: {
  pathVariants: PathVariantInfo[];
  /** Saved API values; unset variants have no key. */
  cityMaxSpeed: SpeedMap;
  /** Draft values */
  pendingSpeeds: SpeedMap;
  onChange: (variantId: number, rawValue: string) => void;
}) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  /**
   * Group variants by path_type.
   * Key: path_type ID; value: variants belonging to that type.
   */
  const groupedVariants = useMemo(() => {
    const groups = new Map<number, PathVariantInfo[]>();
    for (const variant of pathVariants) {
      if (!EDITABLE_PATH_TYPE_IDS.has(variant.type)) continue;
      const existing = groups.get(variant.type);
      if (existing) {
        existing.push(variant);
      } else {
        groups.set(variant.type, [variant]);
      }
    }
    return groups;
  }, [pathVariants]);

  /** Path type ID → display name, used for accordion headers */
  const pathTypeNames = useMemo(() => {
    if (!masterData?.path_types) return {} as Record<number, string>;
    const result: Record<number, string> = {};
    for (const [id, entry] of Object.entries(masterData.path_types)) {
      result[Number(id)] = (entry as { display_name: string }).display_name;
    }
    return result;
  }, [masterData?.path_types]);

  /**
   * Expanded path_type group IDs.
   * Expand all initially because the tab has enough space.
   */
  const [expandedGroups, setExpandedGroups] = useState<Set<number>>(
    () => new Set(groupedVariants.keys()),
  );

  const toggleGroup = useCallback((typeId: number) => {
    setExpandedGroups((prev) => {
      const next = new Set(prev);
      if (next.has(typeId)) {
        next.delete(typeId);
      } else {
        next.add(typeId);
      }
      return next;
    });
  }, []);

  /** Speed displayed in the input: draft → saved value → master default. */
  const getDisplaySpeed = useCallback(
    (variant: PathVariantInfo): number => {
      const key = String(variant.id);
      if (key in pendingSpeeds) return pendingSpeeds[key];
      if (key in cityMaxSpeed) return cityMaxSpeed[key];
      return variant.default_speed_limit;
    },
    [pendingSpeeds, cityMaxSpeed],
  );

  /** Whether the speed differs from the master default */
  const isModified = useCallback(
    (variant: PathVariantInfo): boolean => getDisplaySpeed(variant) !== variant.default_speed_limit,
    [getDisplaySpeed],
  );

  return (
    <>
      <p className="regulation-pane__description">
        {t("regulation.speedLimitDescription")}
      </p>

      <div className="regulation-speed-list">
        {Array.from(groupedVariants.entries()).map(([typeId, variants]) => {
          const isExpanded = expandedGroups.has(typeId);
          return (
            <div key={typeId} className="regulation-speed-group">
              <button
                type="button"
                className="regulation-speed-group__header"
                onClick={() => toggleGroup(typeId)}
                aria-expanded={isExpanded}
              >
                <span className="regulation-speed-group__toggle-icon">
                  {isExpanded ? "▼" : "▶"}
                </span>
                <h4 className="regulation-speed-group__label">
                  {pathTypeNames[typeId] ?? t("regulation.unknownType")}
                </h4>
                <span className="regulation-speed-group__count">{variants.length}</span>
              </button>
              {isExpanded && (
                <div className="regulation-speed-group__content">
                  {variants.map((variant) => (
                    <div key={variant.id} className="regulation-speed-row">
                      <span
                        className={[
                          "regulation-speed-row__name",
                          isModified(variant) && "regulation-speed-row__name--modified",
                        ]
                          .filter(Boolean)
                          .join(" ")}
                        title={variant.display_name}
                      >
                        {variant.display_name}
                      </span>
                      <div className="regulation-speed-row__input-group">
                        <input
                          type="number"
                          min={0}
                          step={1}
                          className={[
                            "regulation-speed-input",
                            isModified(variant) && "regulation-speed-input--modified",
                          ]
                            .filter(Boolean)
                            .join(" ")}
                          value={getDisplaySpeed(variant)}
                          onChange={(e) => onChange(variant.id, e.target.value)}
                        />
                        <span className="regulation-speed-unit">
                          {t("regulation.speedUnit")}
                        </span>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </>
  );
}
