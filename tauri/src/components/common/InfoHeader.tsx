/**
 * Information header component
 *
 * Displays entity icon, status, and name horizontally.
 * Shared by agent, parcel, district, path, and resource detail panels.
 *
 * CONSTRUCTING status shows a progress bar from constructionProgress (0-100).
 * Other statuses show solid-color badges based on STATUS_STYLE_MAP.
 *
 * iconLabel adds a rounded frame around the icon and a black label below it (e.g. public facility).
 * The frame is yellow by default; iconFrameColor customizes its color by use case.
 *
 * onNameSave turns the name into a toggle-editable label (EditableLabel). Only player-managed
 * targets (public facilities, paths, districts) may be renamed, so callers decide eligibility
 * and express it by supplying or omitting the handler.
 *
 * onFocusCamera adds a button at the upper right, beside the status bar, to move the 3D camera
 * to the target. Omit it for targets without a viewable position.
 */

import { useTranslation } from "react-i18next";
import { STATUS_STYLE_MAP } from "../../local-constants";
import { CameraFocusButton } from "./CameraFocusButton";
import { EditableLabel } from "./EditableLabel";
import "./InfoHeader.css";

type InfoHeaderProps = {
  /** Resolved icon image path. */
  iconSrc: string;
  /** Status code. */
  status?: number;
  /** Reason for the current status (REASON_*). Values other than 0 (REASON_NONE) appear in parentheses on the badge. */
  statusReason?: number;
  /** Variant/type name, displayed between status and entity name. */
  variantName?: string;
  /** Entity name. */
  name: string;
  /** Construction progress (0-100), used while under construction. */
  constructionProgress?: number;
  /** Whether to display status (default true). */
  showStatus?: boolean;
  /** Label inside the icon frame (e.g. Public facility). Omit for normal icon display. */
  iconLabel?: string;
  /** Frame color (CSS color), yellow if omitted. */
  iconFrameColor?: string;
  /** Name save handler. Supplying it makes the name editable. Throw on failure. */
  onNameSave?: (next: string) => Promise<void> | void;
  /** Initial name edit value. Supply the raw name when name falls back to an ID display. */
  nameValue?: string;
  /** Handler moving the 3D camera to the target. Supplying it displays the button. */
  onFocusCamera?: () => void;
};

export function InfoHeader({
  iconSrc,
  status,
  statusReason,
  variantName,
  name,
  constructionProgress,
  showStatus = true,
  iconLabel,
  iconFrameColor,
  onNameSave,
  nameValue,
  onFocusCamera,
}: InfoHeaderProps) {
  const { t } = useTranslation();
  const style = status !== undefined ? STATUS_STYLE_MAP[status] ?? { bg: "#6b7280", text: "#ffffff" } : { bg: "#6b7280", text: "#ffffff" };
  const reasonLabel =
    statusReason !== undefined && statusReason !== 0
      ? t(`reason.${statusReason}`, { defaultValue: "" })
      : "";
  const statusLabel = status !== undefined
    ? t(`status.${status}`, { defaultValue: `Status ${status}` })
      + (reasonLabel ? `（${reasonLabel}）` : "")
    : "";
  const progress = Math.max(0, Math.min(100, constructionProgress ?? 0));

  return (
    <div className="info-header">
      {iconLabel ? (
        // For labeled icons, add a rounded frame and display the label in its bottom padding.
        <div
          className="info-header-icon-frame"
          style={iconFrameColor ? { backgroundColor: iconFrameColor } : undefined}
        >
          <img className="info-header-icon" src={iconSrc} alt={name} />
          <span className="info-header-icon-frame-label">{iconLabel}</span>
        </div>
      ) : (
        <img className="info-header-icon" src={iconSrc} alt={name} />
      )}
      <div className="info-header-main">
        <div className="info-header-body">
          {showStatus && status !== undefined && (
            <div
              className={
                style.showProgress
                  ? "info-header-status info-header-status--progress"
                  : "info-header-status"
              }
              style={{ backgroundColor: style.bg, color: style.text }}
            >
              {style.showProgress ? (
                <div className="info-header-progress-bar">
                  <div
                    className="info-header-progress-fill"
                    style={{ width: `${progress}%`, backgroundColor: style.bg }}
                  />
                  <span className="info-header-progress-label">
                    {statusLabel} {progress.toFixed(0)}%
                  </span>
                </div>
              ) : (
                <span>{statusLabel}</span>
              )}
            </div>
          )}
          {variantName && (
            <span className="info-header-variant">{variantName}</span>
          )}
          {onNameSave ? (
            <EditableLabel
              value={nameValue ?? name}
              displayText={name}
              textClassName="info-header-name"
              onSave={onNameSave}
            />
          ) : (
            <span className="info-header-name">{name}</span>
          )}
        </div>
        {onFocusCamera && <CameraFocusButton onClick={onFocusCamera} />}
      </div>
    </div>
  );
}
