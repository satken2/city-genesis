/**
 * Button moving the 3D camera to a target, placed at the detail header's top right.
 * Uses our own SVG, with no external asset or required credit.
 * A crosshair distinguishes this one-time movement from the citizen panel camera icon's continuous tracking.
 */

import { useTranslation } from "react-i18next";
import "./CameraFocusButton.css";

export function CameraFocusButton({ onClick }: { onClick: () => void }) {
  const { t } = useTranslation();
  return (
    <button
      className="camera-focus-btn"
      type="button"
      title={t("common.focusCamera")}
      aria-label={t("common.focusCamera")}
      onClick={onClick}
    >
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" aria-hidden="true">
        <circle cx="12" cy="12" r="6.5" />
        <circle cx="12" cy="12" r="1.5" fill="currentColor" stroke="none" />
        <path d="M12 2.5v4M12 17.5v4M2.5 12h4M17.5 12h4" />
      </svg>
    </button>
  );
}
