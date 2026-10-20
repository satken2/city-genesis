/**
 * Shared parking meters for building parking and street parking. Backend counts/capacities are float32 and may
 * exceed capacity (street parking is intentionally unclipped). Calculate meter utilization after rounding to the
 * same precision as displayed counts. Dividing raw values could highlight overcapacity from fractional
 * differences even when the display reads 20 / 20 vehicles.
 */

export type ParkingUsage = {
  /** Used spaces rounded to display precision. */
  used: number;
  /** Capacity rounded to display precision. */
  capacity: number;
  /**
   * Utilization passed to Meter. If capacity is zero with positive usage, all usage is excess: return +Infinity
   * (Meter caps the bar at 100% and shows overcapacity).
   */
  ratio: number;
};

export function parkingUsage(used: number, capacity: number, fractionDigits: number): ParkingUsage {
  const scale = 10 ** fractionDigits;
  const roundedUsed = Math.round(used * scale) / scale;
  const roundedCapacity = Math.round(capacity * scale) / scale;
  let ratio = 0;
  if (roundedCapacity > 0) {
    ratio = roundedUsed / roundedCapacity;
  } else if (roundedUsed > 0) {
    ratio = Number.POSITIVE_INFINITY;
  }
  return { used: roundedUsed, capacity: roundedCapacity, ratio };
}
