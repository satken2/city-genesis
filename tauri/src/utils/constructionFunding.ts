/**
 * Public works funding checks (advance UI validation). Backend cost previews return available_funds = city cash
 * − savings target − commitments to existing projects; order APIs reject using the same criterion. The UI
 * compares before confirmation, disabling it and showing the shortfall when insufficient. While either value is
 * null, do not block in the UI; the backend makes the final decision.
 */
export function isConstructionAffordable(
  requiredTotal: number | null | undefined,
  availableFunds: number | null | undefined,
): boolean {
  if (requiredTotal == null || availableFunds == null) return true;
  if (requiredTotal <= 0) return true;
  return requiredTotal <= availableFunds;
}

/** Shortfall (nonnegative), or null if validation inputs are incomplete. */
export function constructionFundsShortfall(
  requiredTotal: number | null | undefined,
  availableFunds: number | null | undefined,
): number | null {
  if (requiredTotal == null || availableFunds == null) return null;
  return Math.max(requiredTotal - availableFunds, 0);
}
