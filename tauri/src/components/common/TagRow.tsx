/**
 * Construction project tag row, shared by building/road detail panels.
 *
 * Combines project tag selection and showing/hiding this object in the 3D view on one row.
 * Tags persist in the backend; visibility exists only in Unity's session and is not saved
 * (see `hooks/useObjectVisibility`).
 */

import { useTranslation } from "react-i18next";

import { useObjectVisibility } from "../../hooks/useObjectVisibility";
import { TagSelect } from "./TagSelect";
import "./TagSelect.css";

type TagRowProps = {
  apiBaseUrl: string;
  kind: "agent" | "path";
  entityId: number;
};

export function TagRow({ apiBaseUrl, kind, entityId }: TagRowProps) {
  const { t } = useTranslation();
  const visibility = useObjectVisibility(kind === "path" ? "path_detail" : "agent_detail");
  const hidden = visibility.isHidden(kind, entityId);

  return (
    <div className="tag-row">
      <span className="tag-row__label">{t("tag.label")}</span>
      <TagSelect apiBaseUrl={apiBaseUrl} kind={kind} entityId={entityId} />
      <button
        type="button"
        className={`tag-row__eye${hidden ? " tag-row__eye--hidden" : ""}`}
        title={hidden ? t("tag.showObject") : t("tag.hideObject")}
        onClick={() => visibility.toggleObject(kind, entityId)}
      >
        {hidden ? "🚫" : "👁"}
      </button>
    </div>
  );
}
