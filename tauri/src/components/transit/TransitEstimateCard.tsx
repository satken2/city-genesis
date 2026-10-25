/**
 * Line profitability estimate card
 *
 * Display-only component listing backend estimates (hooks/useTransitEstimate): required vehicles,
 * loop distance/time, operating cost (vehicle leasing), fare income at assumed occupancy,
 * balance, and break-even occupancy. Does not calculate values.
 * Used in creation panels (bc-*) and line detail settings (agent-detail-*), so host panels own
 * headings/frames; this renders only the content without a background.
 */

import { useTranslation } from "react-i18next";
import type { TransitLineEstimate } from "../../types";
import { resolveIntlLocale } from "../../utils/locale";
import "./TransitEstimateCard.css";

// Estimation failure code → i18n key. Use generic text for unknown codes.
const UNAVAILABLE_KEYS: Record<string, string> = {
  insufficient_stops: "transit.estimate.unavailable.insufficientStops",
  invalid_stop: "transit.estimate.unavailable.invalidStop",
  no_distance: "transit.estimate.unavailable.noDistance",
  route_disconnected: "transit.estimate.unavailable.routeDisconnected",
  no_underlay: "transit.estimate.unavailable.noUnderlay",
  train_too_long: "transit.estimate.unavailable.trainTooLong",
};

type TransitEstimateCardProps = {
  estimate: TransitLineEstimate | null;
  loading: boolean;
  error: string | null;
  /** Instructions while estimate inputs are incomplete (e.g. fewer than two stops). */
  placeholder?: string;
};

export function TransitEstimateCard({ estimate, loading, error, placeholder }: TransitEstimateCardProps) {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);

  if (error) {
    return <p className="transit-estimate__error">{error}</p>;
  }
  if (!estimate) {
    const text = loading ? t("transit.estimate.loading") : placeholder;
    return text ? <p className="transit-estimate__muted">{text}</p> : null;
  }
  // Retain the previous result faded during fetching to prevent content from disappearing and jumping on each input.
  const rootClass = `transit-estimate${loading ? " transit-estimate--stale" : ""}`;
  if (!estimate.ok) {
    return (
      <div className={rootClass}>
        <p className="transit-estimate__warning">
          {t((estimate.reason && UNAVAILABLE_KEYS[estimate.reason]) || "transit.estimate.unavailable.generic")}
        </p>
      </div>
    );
  }

  const number = (value: number, digits = 0) =>
    value.toLocaleString(locale, { maximumFractionDigits: digits, minimumFractionDigits: digits });
  const money = (value: number) => `${number(Math.round(value))}${t("transit.currencyUnit")}`;
  const balance = estimate.balance;
  const breakEven = estimate.break_even_load_factor;
  // Even with several overloaded tracks, showing only the highest load ratio suffices to decide between longer headways and more tracks.
  // Track overload (frequency) and single-track section overload (occupancy time) use different units; compare capacity ratios.
  const worstOverload = estimate.track_overloads.reduce<TransitLineEstimate["track_overloads"][number] | null>(
    (worst, o) => (worst === null || o.load / o.capacity > worst.load / worst.capacity ? o : worst),
    null,
  );

  return (
    <div className={rootClass}>
      <div className="transit-estimate__rows">
        <EstimateRow
          label={t("transit.estimate.vehicles")}
          value={t("transit.estimate.vehiclesValue", { count: estimate.vehicles_required ?? 0 })}
        />
        <EstimateRow
          label={t("transit.estimate.roundTrip")}
          value={t("transit.estimate.roundTripValue", {
            km: number((estimate.round_trip_length_m ?? 0) / 1000, 1),
            minutes: number(estimate.round_trip_minutes ?? 0, 1),
          })}
        />
        <EstimateRow
          label={t("transit.estimate.operatingCost")}
          value={estimate.operating_cost != null ? money(estimate.operating_cost) : "—"}
        />
        <EstimateRow label={t("transit.estimate.revenue")} value={money(estimate.revenue ?? 0)} />
        <EstimateRow
          label={t("transit.estimate.balance")}
          value={balance != null ? `${balance > 0 ? "+" : ""}${money(balance)}` : "—"}
          total
          tone={balance == null || balance === 0 ? undefined : balance > 0 ? "positive" : "negative"}
        />
        <EstimateRow
          label={t("transit.estimate.breakEven")}
          value={
            breakEven == null
              ? "—"
              : breakEven > 1
                ? t("transit.estimate.breakEvenOverFull")
                : t("transit.estimate.breakEvenValue", { percent: formatPercent(breakEven, locale) })
          }
        />
      </div>

      {estimate.depot_connected === false && (
        <p className="transit-estimate__warning">{t("transit.estimate.noDepot")}</p>
      )}
      {worstOverload && (
        <p className="transit-estimate__warning">
          {!worstOverload.single_track
            ? t("transit.estimate.trackOverload", {
                load: number(Math.round(worstOverload.load)),
                capacity: number(Math.round(worstOverload.capacity)),
              })
            : worstOverload.min_headway != null
              ? t("transit.estimate.singleTrackOverload", {
                  minutes: number(Math.ceil(worstOverload.min_headway * 10) / 10, 1),
                })
              : t("transit.estimate.singleTrackFull")}
        </p>
      )}
    </div>
  );
}

/**
 * Format a 0-1 proportion as the numeric part of a percentage. Break-even occupancy can be
 * around 0.01%, so use two significant digits below 10% to avoid rounding it to 0%.
 */
function formatPercent(ratio: number, locale: string): string {
  const percent = ratio * 100;
  const options: Intl.NumberFormatOptions =
    percent >= 10 ? { maximumFractionDigits: 0 } : { maximumSignificantDigits: 2 };
  return new Intl.NumberFormat(locale, options).format(percent);
}

type EstimateRowProps = {
  label: string;
  value: string;
  /** Add a surface to emphasize the conclusion row (estimated balance). */
  total?: boolean;
  /** Balance surplus/deficit colors. */
  tone?: "positive" | "negative";
};

function EstimateRow({ label, value, total, tone }: EstimateRowProps) {
  const className = [
    "transit-estimate__row",
    total && "transit-estimate__row--total",
    tone && `transit-estimate__row--${tone}`,
  ]
    .filter(Boolean)
    .join(" ");
  return (
    <div className={className}>
      <span className="transit-estimate__label">{label}</span>
      <span className="transit-estimate__main">{value}</span>
    </div>
  );
}
