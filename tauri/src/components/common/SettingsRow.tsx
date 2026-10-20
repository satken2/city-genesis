import type { ReactNode } from "react";
import "./SettingsForm.css";

/**
 * Row for one setting.
 * Name/details on the left, controls on the right. stretch makes controls full-width (volume sliders).
 * disabled affects presentation; also disable child inputs/buttons to prevent keyboard interaction.
 */
export function SettingsRow({
  label,
  description,
  badge,
  stretch = false,
  disabled = false,
  children,
}: {
  label: string;
  description?: ReactNode;
  badge?: string;
  stretch?: boolean;
  disabled?: boolean;
  children: ReactNode;
}) {
  const modifiers = [
    stretch ? "settings-row--stretch" : "",
    disabled ? "settings-row--disabled" : "",
  ]
    .filter(Boolean)
    .join(" ");
  return (
    <div className={`settings-row ${modifiers}`}>
      <div className="settings-row-label">
        <span className="settings-row-name">
          {label}
          {badge != null && <span className="settings-row-badge">{badge}</span>}
        </span>
        {description != null && <span className="settings-row-desc">{description}</span>}
      </div>
      <div className="settings-row-control">{children}</div>
    </div>
  );
}

