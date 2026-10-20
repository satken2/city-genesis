/**
 * Road membership section in the path construction panel.
 *
 * Select the named road for newly placed streets/highways. usePathRoadAssignment owns state; this only renders
 * choices (none / existing road / new road) and an "Extend ..." notice when placing from an existing road's endpoint.
 */

import { useTranslation } from "react-i18next";
import { RoadSelect } from "../road/RoadMembershipRow";
import { roadDisplayName } from "../road/roadApi";
import type { PathRoadAssignment } from "./usePathRoadAssignment";
import "./PathConstructionPanel.css";

export function PathRoadAssignmentSection({ assignment, disabled }: { assignment: PathRoadAssignment; disabled?: boolean }) {
  const { t } = useTranslation();
  const { extending } = assignment;
  return (
    <div className="pc-section">
      <div className="pc-section-title">{t("path_construction.road.title")}</div>
      <RoadSelect
        roads={assignment.roads}
        value={assignment.choice}
        onChange={assignment.setChoice}
        disabled={disabled}
        deferred
      />
      {extending && (
        <div className="road-select__note" role="status">
          <span className="road-select__swatch" style={{ background: extending.color }} aria-hidden="true" />
          {t("path_construction.road.extending", { name: roadDisplayName(extending.name, extending.road_id, t) })}
        </div>
      )}
      {assignment.choice.kind === "new" && (
        <div className="road-select__note">{t("path_construction.road.newNote")}</div>
      )}
    </div>
  );
}
