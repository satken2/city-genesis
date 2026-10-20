/**
 * Construction project section in the path construction panel.
 *
 * Choose the tag for paths about to be placed. The panel owns the value and includes it as tag on every path
 * in the creation request (reuse the same tag throughout continuous placement).
 */

import { useTranslation } from "react-i18next";
import { TagPicker } from "../common/TagPicker";
import "./PathConstructionPanel.css";

type PathTagSectionProps = {
  apiBaseUrl: string;
  value: string;
  onChange: (tag: string) => void;
  disabled?: boolean;
};

export function PathTagSection({ apiBaseUrl, value, onChange, disabled }: PathTagSectionProps) {
  const { t } = useTranslation();
  return (
    <div className="pc-section">
      <div className="pc-section-title">{t("tag.label")}</div>
      <TagPicker apiBaseUrl={apiBaseUrl} value={value} onChange={onChange} disabled={disabled} />
    </div>
  );
}
