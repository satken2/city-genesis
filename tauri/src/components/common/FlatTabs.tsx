import type { ReactNode } from "react";
import "./FlatTabs.css";

export type FlatTab<T extends string> = {
  id: T;
  label: string;
  disabled?: boolean;
  icon?: ReactNode;
  /** Operation name/value shared with tab choices. Tabs navigating to allowed choices remain operable too. */
  guide?: { name: string; values: (string | number)[] };
  /** Badge beside the label (e.g. "Coming soon" for unavailable tabs). */
  badge?: string;
};

/**
 * Flat-style tab row. Callers may override dimensions/colors for icon-only display.
 * The caller owns selection state and chooses the content to display.
 */
export function FlatTabs<T extends string>({ tabs, active, onChange, label, iconOnly = false, idPrefix }: {
  tabs: FlatTab<T>[];
  active: T;
  onChange: (id: T) => void;
  /** Accessible name for the tab list. */
  label?: string;
  /** Show only icons on buttons; retain labels for accessibility and tooltips. */
  iconOnly?: boolean;
  /** Corresponding tabpanel IDs must be `${idPrefix}-panel-${tab.id}`. */
  idPrefix?: string;
}) {
  return (
    <div className="flat-tabs" role="tablist" aria-label={label}>
      {tabs.map(tab => (
        <button
          key={tab.id}
          type="button"
          role="tab"
          data-guide={tab.guide?.name}
          data-guide-values={tab.guide?.values.join(" ")}
          id={idPrefix ? `${idPrefix}-tab-${tab.id}` : undefined}
          aria-controls={idPrefix ? `${idPrefix}-panel-${tab.id}` : undefined}
          aria-label={iconOnly ? tab.label : undefined}
          title={iconOnly ? tab.label : undefined}
          className="flat-tab"
          aria-selected={tab.id === active}
          tabIndex={iconOnly ? (tab.id === active ? 0 : -1) : undefined}
          disabled={tab.disabled}
          onClick={() => { if (tab.id !== active) onChange(tab.id); }}
          onKeyDown={event => {
            if (!iconOnly || !["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
            event.preventDefault();
            const buttons = event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>("[role=tab]");
            const enabled = tabs.filter((item, index) => !item.disabled && !buttons?.[index]?.inert);
            if (enabled.length === 0) return;
            const current = enabled.findIndex(item => item.id === tab.id);
            const index = event.key === "Home" ? 0 : event.key === "End" ? enabled.length - 1
              : (current + (event.key === "ArrowRight" ? 1 : -1) + enabled.length) % enabled.length;
            const next = enabled[index];
            onChange(next.id);
            buttons?.[tabs.findIndex(item => item.id === next.id)]?.focus();
          }}
        >
          {tab.icon && <span aria-hidden="true">{tab.icon}</span>}
          {!iconOnly && <span>{tab.label}</span>}
          {tab.badge && <span className="flat-tab-badge">{tab.badge}</span>}
        </button>
      ))}
    </div>
  );
}
