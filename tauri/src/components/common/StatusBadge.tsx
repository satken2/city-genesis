/**
 * Shared status badge
 *
 * Displays numeric status values for agents/buildings/etc. as colored badges.
 * Background/text colors come from local-constants STATUS_STYLE_MAP;
 * labels resolve through i18n `status.<number>` keys.
 *
 * Located in common to share one design across list UIs such as search/statistics windows.
 */

import { useTranslation } from "react-i18next";
import { STATUS_STYLE_MAP } from "../../local-constants";
import "./StatusBadge.css";

type StatusBadgeProps = {
  status: number;
};

export function StatusBadge({ status }: StatusBadgeProps) {
  const { t } = useTranslation();
  const style = STATUS_STYLE_MAP[status] ?? { bg: "#6b7280", text: "#ffffff" };
  const label = t(`status.${status}`, { defaultValue: String(status) });
  return (
    <span
      className="status-badge"
      style={{ backgroundColor: style.bg, color: style.text }}
    >
      {label}
    </span>
  );
}
