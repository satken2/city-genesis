/**
 * Resolve line suspension reason codes (API pause_reason) to display i18n keys.
 * Shared by public transport list tooltips and line detail warning banners.
 */

const PAUSE_REASON_KEYS: Record<string, string> = {
  route_disconnected: "transit.pauseReason.routeDisconnected",
  no_underlay: "transit.pauseReason.noUnderlay",
  train_too_long: "transit.pauseReason.trainTooLong",
};

/** Use generic text for suspension without a reason code (e.g. vehicle shortages) or unknown codes. */
export function pauseReasonKey(reason: string | null | undefined): string {
  return (reason && PAUSE_REASON_KEYS[reason]) || "transit.pauseReason.generic";
}
