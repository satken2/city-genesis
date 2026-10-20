/**
 * Path variant card (icon, name, description, alignment/structure standards).
 *
 * Used in construction/rebuild panels in two ways (same composition as BuildingVariantCard):
 * 1. Variant list after category selection (with onClick): render as a button.
 * 2. Construction screen after variant selection (without onClick): static summary card for the selected variant.
 *
 * Below the description, show placement-validation constraints as small icons and values only
 * (minimum curve radius, maximum grade, maximum span length, minimum height). Put names in icon tooltips.
 * Omit constraints without meaning: radius/grade 0 means unrestricted; span length does not apply to terrain-bound
 * variants (only nonfollowing spans); minimum height does not apply to variants without elevation editing.
 */

import { useTranslation } from "react-i18next";
import type { PathMasterEntry } from "../../types";
import { resolvePathIcon } from "../../utils/pathIcon";
import "./PathConstructionPanel.css";
import "../building/BuildingConstructionPanel.css";

type PathVariantCardProps = {
  /** Variant ID, used by tutorial guidance to target one card (data-guide-value). */
  variantId?: number;
  entry: PathMasterEntry;
  /** True when unavailable due to insufficient funds, tutorial restrictions, etc. */
  disabled?: boolean;
  /** When provided, render as a button. */
  onClick?: () => void;
};

/** One standard value. i18n keys are under path_construction.spec; value includes units. */
type SpecItem = {
  key: "minCurveRadius" | "maxGrade" | "maxSpan" | "minHeight";
  value: string;
  icon: React.ReactNode;
};

/** Format to at most one decimal and remove trailing zeros (100.0 → "100", 2.5 → "2.5"). */
function formatSpecNumber(value: number): string {
  return String(Number(value.toFixed(1)));
}

/** 24 px square line-art icon using the text color. */
const SPEC_ICON_PROPS = {
  className: "pc-spec-icon",
  viewBox: "0 0 24 24",
  fill: "none",
  stroke: "currentColor",
  strokeWidth: 2,
  strokeLinecap: "round" as const,
  strokeLinejoin: "round" as const,
  "aria-hidden": true,
};

const ICON_CURVE_RADIUS = (
  <svg {...SPEC_ICON_PROPS}>
    <path d="M4 20 A16 16 0 0 1 20 4" />
    <path d="M20 20 L8.7 8.7" />
  </svg>
);

const ICON_GRADE = (
  <svg {...SPEC_ICON_PROPS}>
    <path d="M3 19 H21 V8 Z" />
  </svg>
);

const ICON_SPAN = (
  <svg {...SPEC_ICON_PROPS}>
    <path d="M2 8 H22" />
    <path d="M6 8 V20 M18 8 V20" />
    <path d="M8 15 H16 M10 13 l-2 2 2 2 M14 13 l2 2 -2 2" />
  </svg>
);

const ICON_MIN_HEIGHT = (
  <svg {...SPEC_ICON_PROPS}>
    <path d="M3 20 H21" />
    <path d="M12 4 V16 M8 12 l4 4 4 -4" />
  </svg>
);

/** Return only meaningful variant constraints in display order. */
function collectSpecs(entry: PathMasterEntry): SpecItem[] {
  const specs: SpecItem[] = [];
  if (entry.min_curve_radius && entry.min_curve_radius > 0) {
    specs.push({ key: "minCurveRadius", value: `${formatSpecNumber(entry.min_curve_radius)}m`, icon: ICON_CURVE_RADIUS });
  }
  if (entry.max_grade && entry.max_grade > 0) {
    specs.push({ key: "maxGrade", value: `${formatSpecNumber(entry.max_grade)}%`, icon: ICON_GRADE });
  }
  // max_span applies only to nonfollowing spans supported by pillars/girders, so omit for variants that must follow terrain.
  const canLeaveGround = entry.height_editable || entry.follow_terrain === false;
  if (entry.max_span && entry.max_span > 0 && canLeaveGround) {
    specs.push({ key: "maxSpan", value: `${formatSpecNumber(entry.max_span)}m`, icon: ICON_SPAN });
  }
  if (entry.height_editable) {
    specs.push({ key: "minHeight", value: `${formatSpecNumber(entry.min_height ?? 0)}m`, icon: ICON_MIN_HEIGHT });
  }
  return specs;
}

export function PathVariantCard({ variantId, entry, disabled = false, onClick }: PathVariantCardProps) {
  const { t } = useTranslation();
  const specs = collectSpecs(entry);

  const cardBody = (
    <>
      <img
        src={resolvePathIcon(entry.type, variantId)}
        alt={entry.display_name}
        className="pc-description-icon"
        draggable={false}
      />
      <div className="pc-description-body">
        <div className="pc-description-name">{entry.display_name}</div>
        <div className="pc-description-text">{entry.description || "—"}</div>
        {specs.length > 0 && (
          <div className="pc-description-specs">
            {specs.map((spec) => (
              <span key={spec.key} className="pc-spec" title={t(`path_construction.spec.${spec.key}`)}>
                {spec.icon}
                <span className="pc-spec-value">{spec.value}</span>
              </span>
            ))}
          </div>
        )}
      </div>
    </>
  );

  if (onClick) {
    return (
      <button
        type="button"
        data-guide="path.variant"
        data-guide-value={variantId}
        className="pc-description-frame bc-variant-card-btn"
        disabled={disabled}
        onClick={onClick}
      >
        {cardBody}
      </button>
    );
  }

  return <div className="pc-description-frame">{cardBody}</div>;
}
