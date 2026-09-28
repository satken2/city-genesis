/**
 * パス建設パネルの「所属道路」セクション
 *
 * 新しく敷く街路・高速道路をどの道路(名前付き道路)に属させるかを選ぶ。状態は usePathRoadAssignment が持ち、
 * ここは選択肢(なし / 既存の道路 / 新しい道路)と、既存の道路の端から敷いているときの
 * 「〇〇を延長します」の案内だけを描く。
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
