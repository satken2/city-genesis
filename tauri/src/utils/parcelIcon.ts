/**
 * Resolve parcel icons by parcel_type, falling back to PARCEL_LAND (1) if missing.
 *
 * parcel_type → filename:
 * - PARCEL_LAND (1) → parcels/parcel_1.png
 * - PARCEL_SHORE (2) → parcels/parcel_2.png
 * - PARCEL_WATER (3) → parcels/parcel_3.png
 * - PARCEL_RECLAIMED (4) → parcels/parcel_4.png
 */

const parcelModules = import.meta.glob<{ default: string }>(
  "../assets/images/parcels/*.png",
  { eager: true }
);

/**
 * Return the icon image path for a parcel type.
 */
export function resolveParcelIcon(parcelType: number): string {
  const key = `../assets/images/parcels/parcel_${parcelType}.png`;
  if (parcelModules[key]) {
    return parcelModules[key].default;
  }
  const fallback = "../assets/images/parcels/parcel_1.png";
  return parcelModules[fallback]?.default ?? "";
}
