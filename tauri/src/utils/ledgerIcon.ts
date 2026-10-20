/**
 * Resolve budget category (LEDGER_*) icons. Images use numeric category IDs at assets/images/ledger/{id}.png.
 * Preload through Vite import.meta.glob and look up by key at runtime. Missing categories return an empty string
 * so callers leave the icon slot empty.
 */

const ledgerModules = import.meta.glob<{ default: string }>(
  "../assets/images/ledger/*.png",
  { eager: true }
);

/** Return a budget category icon URL, or an empty string if absent. */
export function resolveLedgerIcon(categoryId: number): string {
  const key = `../assets/images/ledger/${categoryId}.png`;
  return ledgerModules[key]?.default ?? "";
}
