import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import "./CityBudgetWindow.css";

/**
 * Overlay inside the budget window (budget category details and per-product tax rates).
 * The backdrop covers the entire window; click it or the close button to close.
 * When content exceeds the window height, scroll only the body and retain the heading.
 */
export function BudgetOverlay({
  title,
  onClose,
  children,
}: {
  title: string;
  onClose: () => void;
  children: ReactNode;
}) {
  const { t } = useTranslation();
  return (
    <div className="budget-overlay" onMouseDown={onClose}>
      <div
        className="budget-overlay__dialog"
        role="dialog"
        aria-modal="true"
        aria-label={title}
        onMouseDown={(e) => e.stopPropagation()}
      >
        <div className="budget-overlay__header">
          <h3 className="budget-window__section-title">{title}</h3>
          <button
            type="button"
            className="budget-overlay__close"
            aria-label={t("common.close")}
            title={t("common.close")}
            onClick={onClose}
          >
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round">
              <path d="M6 6l12 12M18 6L6 18" />
            </svg>
          </button>
        </div>
        <div className="budget-overlay__body">{children}</div>
      </div>
    </div>
  );
}
