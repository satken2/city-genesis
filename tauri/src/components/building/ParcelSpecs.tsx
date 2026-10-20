/**
 * Row showing parcel area and regulations (height/coverage ratio/volume ratio) as icons and values.
 *
 * Display as one row instead of description text on construction panel site cards.
 * Use icon tooltips rather than text labels, as in `BuildingFloorSpecs`.
 * Omit unregulated (null) fields.
 */

import { useTranslation } from "react-i18next";
import type { ParcelData } from "../../types";

type ParcelSpecsProps = {
  parcel: ParcelData;
};

/** 24px square outline icon. Color supplied through currentColor. */
const ICON_PROPS = {
  className: "bc-parcel-spec-icon",
  viewBox: "0 0 24 24",
  fill: "none",
  stroke: "currentColor",
  strokeWidth: 1.8,
  strokeLinecap: "round" as const,
  strokeLinejoin: "round" as const,
  "aria-hidden": true,
};

/** Site area: parcel outline with diagonal double arrows extending toward all four corners. */
const ICON_AREA = (
  <svg {...ICON_PROPS}>
    <rect x="3.5" y="3.5" width="17" height="17" rx="1.5" />
    <path d="M7.5 16.5 L16.5 7.5 M12 7.5 H16.5 V12 M12 16.5 H7.5 V12" />
  </svg>
);

/** Height regulation: a building beneath a dashed upper limit. */
const ICON_HEIGHT = (
  <svg {...ICON_PROPS}>
    <path d="M3 3.5 H21" strokeDasharray="3 2.4" />
    <path d="M5 21 V8 H13 V21 M13 12.5 H19 V21 M3 21 H21" />
    <path d="M8 11.5 H10 M8 15 H10" />
  </svg>
);

/** Building coverage ratio: building footprint inside a top-down parcel outline. */
const ICON_COVERAGE = (
  <svg {...ICON_PROPS}>
    <rect x="3.5" y="3.5" width="17" height="17" rx="1.5" />
    <rect x="7" y="7" width="10" height="10" rx="1" fill="currentColor" fillOpacity="0.55" stroke="none" />
  </svg>
);

/** Volume ratio: stacked floors (total floor area). */
const ICON_VOLUME = (
  <svg {...ICON_PROPS}>
    <path d="M12 3 L21 8 L12 13 L3 8 Z" />
    <path d="M3 12 L12 17 L21 12" />
    <path d="M3 16 L12 21 L21 16" />
  </svg>
);

export function ParcelSpecs({ parcel }: ParcelSpecsProps) {
  const { t } = useTranslation();

  const specs: { key: string; tooltipKey: string; icon: React.ReactNode; value: string }[] = [
    {
      key: "area",
      tooltipKey: "building_construction.landArea",
      icon: ICON_AREA,
      value: `${parcel.area.toFixed(1)}${t("building_construction.unitSqm")}`,
    },
  ];
  if (parcel.limit_height != null) {
    specs.push({
      key: "height",
      tooltipKey: "building_construction.heightLimit",
      icon: ICON_HEIGHT,
      value: `${parcel.limit_height.toFixed(1)}${t("building_construction.unitMeter")}`,
    });
  }
  if (parcel.limit_coverage_ratio != null) {
    specs.push({
      key: "coverage",
      tooltipKey: "building_construction.coverageRatioLimit",
      icon: ICON_COVERAGE,
      value: `${Math.round(parcel.limit_coverage_ratio * 100)}%`,
    });
  }
  if (parcel.limit_volume_ratio != null) {
    specs.push({
      key: "volume",
      tooltipKey: "building_construction.volumeRatioLimit",
      icon: ICON_VOLUME,
      value: `${Math.round(parcel.limit_volume_ratio * 100)}%`,
    });
  }

  return (
    <div className="bc-parcel-specs">
      {specs.map((spec) => (
        <span key={spec.key} className="bc-parcel-spec" title={t(spec.tooltipKey)}>
          {spec.icon}
          <span className="bc-parcel-spec-value">{spec.value}</span>
        </span>
      ))}
    </div>
  );
}
