import { useTranslation } from "react-i18next";
import type { MasterData } from "../../types";
import "./PathConstructionPanel.css";

/** Choose the lower deck built together with the placed path (choices come from its master attach_variants). */
export function PathAttachedVariantSelect({ choices, masterData, value, onChange }: {
  choices: number[];
  masterData: MasterData;
  value: number | null;
  onChange: (value: number | null) => void;
}) {
  const { t } = useTranslation();
  return (
      <label className="pc-settings-row">
        <span className="pc-settings-label">{t("path_construction.attachedVariant")}</span>
        <select className="pc-select" value={value ?? ""}
          onChange={(event) => onChange(event.target.value === "" ? null : Number(event.target.value))}>
          <option value="">{t("path_construction.attachedVariantNone")}</option>
          {choices.map((id) => (
            <option key={id} value={id}>{masterData.paths[id]?.display_name ?? id}</option>
          ))}
        </select>
      </label>
  );
}
