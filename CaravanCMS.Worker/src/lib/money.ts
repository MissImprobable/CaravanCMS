/**
 * Money fields are stored as integer cents in D1 (see migrations/0001_init.sql
 * for why — JS has no native decimal type). Convert to/from display decimal
 * only at the API boundary; never do math on the float form.
 */
export function centsToDecimal(cents: number | null | undefined): number {
  return Math.round(cents ?? 0) / 100;
}

export function decimalToCents(value: number | null | undefined): number {
  return Math.round((value ?? 0) * 100);
}

/** Quantity uses the same integer-scaling trick, at hundredths precision. */
export function hundredthsToDecimal(hundredths: number | null | undefined): number {
  return Math.round(hundredths ?? 0) / 100;
}

export function decimalToHundredths(value: number | null | undefined): number {
  return Math.round((value ?? 0) * 100);
}
