/**
 * Building variant card
 *
 * Shared card for variant information in the building construction panel.
 * Combines icon, name, description, and floor specifications (allowed floors/type icons).
 *
 * Uses:
 * 1. Variant list after category selection (onClick supplied): render as a clickable button.
 * 2. Detailed settings after building selection (no onClick): reuse as a static summary card.
 */

import type { AgentMasterEntry } from "../../types";
import { resolveAgentIcon } from "../../utils/agentIcon";
import { BuildingFloorSpecs } from "./BuildingFloorSpecs";

export type BuildingVariantCardProps = {
  variantId: number;
  entry: AgentMasterEntry;
  /** Externally fixed floor count (landmarks, etc.). */
  fixedFloors?: number | null;
  /** True when unavailable for selection, e.g. due to insufficient funds. */
  disabled?: boolean;
  /** Click handler. When supplied, render as an interactive button. */
  onClick?: () => void;
};

export function BuildingVariantCard({
  variantId,
  entry,
  fixedFloors,
  disabled = false,
  onClick,
}: BuildingVariantCardProps) {
  const icon = resolveAgentIcon(
    variantId,
    entry.type,
    entry.building ?? false,
    entry.bd_type ?? 0
  );

  const cardBody = (
    <>
      <img
        src={icon}
        alt={entry.display_name}
        className="bc-description-icon"
        draggable={false}
      />
      <div className="bc-description-body">
        <div className="bc-description-name">{entry.display_name}</div>
        <div className="bc-description-text">{entry.description || "—"}</div>
        <BuildingFloorSpecs entry={entry} fixedFloors={fixedFloors} />
      </div>
    </>
  );

  if (onClick) {
    return (
      <button
        type="button"
        data-guide="building.variant"
        data-guide-value={variantId}
        className="bc-description-frame bc-variant-card-btn"
        disabled={disabled}
        onClick={onClick}
      >
        {cardBody}
      </button>
    );
  }

  return <div className="bc-description-frame">{cardBody}</div>;
}
