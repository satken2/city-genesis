/**
 * Frame overlaying an insufficient-funds badge
 *
 * Overlays "Insufficient funds" on currently unaffordable variant cards in construction lists.
 * BuildingVariantCard / PathVariantCard are reused in lists and construction summaries, so keep
 * list-only display in this outer frame. Card disabled state communicates unavailability;
 * this frame does not intercept clicks.
 */

import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import "./UnaffordableOverlay.css";

type UnaffordableOverlayProps = {
  /** Overlay the badge only when true. */
  unaffordable: boolean;
  children: ReactNode;
};

export function UnaffordableOverlay({ unaffordable, children }: UnaffordableOverlayProps) {
  const { t } = useTranslation();
  return (
    <div className="unaffordable-overlay">
      {children}
      {unaffordable && (
        <div className="unaffordable-overlay__veil">
          <span className="unaffordable-overlay__label">{t("construction.variantUnaffordable")}</span>
        </div>
      )}
    </div>
  );
}
