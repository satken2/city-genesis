/**
 * Parts shared by the regulations window tabs
 *
 * Every tab is a stack of RegulationSection blocks, each with a heading, an optional description, and its rows.
 */

import type { ReactNode } from "react";

export function RegulationSection({
  title,
  description,
  children,
}: {
  title: string;
  description?: string;
  children: ReactNode;
}) {
  return (
    <section className="regulation-section">
      <h3 className="agent-detail-section-title">{title}</h3>
      {description && <p className="regulation-section__description">{description}</p>}
      {children}
    </section>
  );
}

/**
 * Row with a name, a numeric input, and a unit. Values are in display units.
 * modified highlights the name and input frame so unsaved edits stay visible.
 */
export function RegulationNumberRow({
  label,
  unit,
  value,
  modified,
  min = 0,
  max,
  step,
  onChange,
}: {
  label: string;
  unit: string;
  value: number;
  modified: boolean;
  min?: number;
  max?: number;
  step: number;
  /** Receives the raw input text; callers validate and convert units. */
  onChange: (rawValue: string) => void;
}) {
  return (
    <div className="regulation-building-row">
      <span
        className={["regulation-building-row__name", modified && "regulation-building-row__name--modified"]
          .filter(Boolean)
          .join(" ")}
      >
        {label}
      </span>
      <div className="regulation-building-row__input-group">
        <input
          type="number"
          min={min}
          max={max}
          step={step}
          className={["regulation-building-input", modified && "regulation-building-input--modified"]
            .filter(Boolean)
            .join(" ")}
          value={value}
          onChange={(e) => onChange(e.target.value)}
        />
        <span className="regulation-building-unit">{unit}</span>
      </div>
    </div>
  );
}
