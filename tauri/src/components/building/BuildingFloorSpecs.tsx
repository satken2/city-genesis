/**
 * Building variant floor specifications (floor type icons and allowed floor count).
 *
 * One row below the description on construction panel description cards.
 *
 * Floor types use icons without text; names appear in tooltips. Sources are space_limits keys:
 * - Generic floors (bit values such as SPACE_RESIDENCE): same order/colors as the six floor-coloring
 *   categories in the display panel (residential=green, retail=blue, factory=yellow, office=purple,
 *   farmland=brown, other=gray). Add hotel lodging floors (SPACE_LODGING) with a pink bed.
 * - Dedicated floors (key = agent variant ID): floors accepting only specific business types.
 *   Use a lock icon with allowed business names in its tooltip.
 *
 * Show floor counts as text right of the icons (1F-10F; one value when min_floors=max_floors).
 * When fixed externally (fixedFloors), as for landmarks, show only that value,
 * since the floor stepper is fixed and disabled too.
 */

import { useTranslation } from "react-i18next";
import { useMasterData } from "../../contexts/MasterDataContext";
import type { AgentMasterEntry } from "../../types";
import {
  SPACE_OTHER,
  SPACE_RESIDENCE,
  SPACE_RETAIL,
  SPACE_FACTORY,
  SPACE_OFFICE,
  SPACE_FARM,
  SPACE_LODGING,
} from "../../constants";

type BuildingFloorSpecsProps = {
  entry: AgentMasterEntry;
  /** Externally fixed floor count (each landmark defines its logical floor count). */
  fixedFloors?: number | null;
};

/** 24px square filled icon. Color supplied through currentColor. */
const ICON_PROPS = {
  className: "bc-floor-icon",
  viewBox: "0 0 24 24",
  fill: "currentColor",
  "aria-hidden": true,
};

/** Generic floor category definitions. Match order/colors to DisplayOptionsPanel's legend. */
const GENERIC_FLOORS: { spaceType: number; color: string; tooltipKey: string; icon: React.ReactNode }[] = [
  {
    spaceType: SPACE_RESIDENCE,
    color: "#4caf50",
    tooltipKey: "display.subSpaceResidence",
    icon: (
      <svg {...ICON_PROPS}>
        <path d="M12 3 L2 12 H5 V21 H10 V15 H14 V21 H19 V12 H22 Z" />
      </svg>
    ),
  },
  {
    spaceType: SPACE_RETAIL,
    color: "#2196f3",
    tooltipKey: "display.subSpaceRetail",
    icon: (
      <svg {...ICON_PROPS}>
        <path d="M3 4 H21 L22 9 H2 Z" />
        <path d="M4 10 H20 V21 H14 V15 H10 V21 H4 Z" />
      </svg>
    ),
  },
  {
    spaceType: SPACE_FACTORY,
    color: "#fbc02d",
    tooltipKey: "display.subSpaceFactory",
    icon: (
      <svg {...ICON_PROPS}>
        <path d="M2 21 V10 L8 13.5 V10 L14 13.5 V10 L20 13.5 V3 H23 V21 Z" />
      </svg>
    ),
  },
  {
    spaceType: SPACE_OFFICE,
    color: "#9c27b0",
    tooltipKey: "display.subSpaceOffice",
    icon: (
      <svg {...ICON_PROPS}>
        <path
          fillRule="evenodd"
          d="M5 2 H19 V22 H5 Z M8 5 H11 V8 H8 Z M13 5 H16 V8 H13 Z M8 10 H11 V13 H8 Z M13 10 H16 V13 H13 Z M8 15 H11 V18 H8 Z M13 15 H16 V18 H13 Z"
        />
      </svg>
    ),
  },
  {
    spaceType: SPACE_FARM,
    color: "#795548",
    tooltipKey: "display.subSpaceFarm",
    icon: (
      <svg {...ICON_PROPS}>
        <path d="M11 22 V12 H13 V22 Z" />
        <path d="M12 12 C12 6 8 3 2 3 C2 9 6 12 12 12 Z" />
        <path d="M12 12 C12 6 16 3 22 3 C22 9 18 12 12 12 Z" />
      </svg>
    ),
  },
  {
    spaceType: SPACE_LODGING,
    color: "#e91e63",
    tooltipKey: "display.subSpaceLodging",
    icon: (
      <svg {...ICON_PROPS}>
        <path d="M2 18 V7 H4 V13 H22 V18 H20 V16 H4 V18 Z" />
        <path d="M5 8 H10 V12 H5 Z" />
        <path d="M11 9 H21 A1 1 0 0 1 22 10 V12 H11 Z" />
      </svg>
    ),
  },
  {
    spaceType: SPACE_OTHER,
    color: "#9e9e9e",
    tooltipKey: "display.subSpaceOther",
    icon: (
      <svg {...ICON_PROPS}>
        <path fillRule="evenodd" d="M3 5 H21 V19 H3 Z M6 8 V16 H18 V8 Z" />
      </svg>
    ),
  },
];

/** Dedicated floor lock icon (gray, matching "Other"). */
const ICON_DEDICATED = (
  <svg {...ICON_PROPS}>
    <path fillRule="evenodd" d="M7 10 V7 A5 5 0 0 1 17 7 V10 H19 V22 H5 V10 Z M9 10 H15 V7 A3 3 0 0 0 9 7 Z" />
  </svg>
);

export function BuildingFloorSpecs({ entry, fixedFloors }: BuildingFloorSpecsProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const spaceTypes = Object.keys(entry.space_limits ?? {}).map(Number);
  const generic = GENERIC_FLOORS.filter((floor) => spaceTypes.includes(floor.spaceType));
  const dedicated = spaceTypes.filter((st) => !GENERIC_FLOORS.some((floor) => floor.spaceType === st));

  const minFloors = entry.min_floors;
  const maxFloors = entry.max_floors;
  const floorRange =
    fixedFloors != null
      ? t("building_construction.floorRangeFixed", { value: fixedFloors })
      : minFloors == null || maxFloors == null
        ? null
        : minFloors === maxFloors
          ? t("building_construction.floorRangeFixed", { value: maxFloors })
          : t("building_construction.floorRange", { min: minFloors, max: maxFloors });

  if (spaceTypes.length === 0 && floorRange === null) return null;

  return (
    <div className="bc-description-floors">
      {generic.map((floor) => (
        <span key={floor.spaceType} className="bc-floor" style={{ color: floor.color }} title={t(floor.tooltipKey)}>
          {floor.icon}
        </span>
      ))}
      {dedicated.map((st) => (
        <span
          key={st}
          className="bc-floor"
          style={{ color: "#9e9e9e" }}
          title={t("building_construction.floorDedicated", {
            name: masterData?.agents[st]?.display_name ?? `#${st}`,
          })}
        >
          {ICON_DEDICATED}
        </span>
      ))}
      {floorRange !== null && (
        <span className="bc-floor-range" title={t("building_construction.floorRangeTooltip")}>
          {floorRange}
        </span>
      )}
    </div>
  );
}
