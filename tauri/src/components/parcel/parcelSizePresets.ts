/**
 * Parcel size presets: typical frontage × depth (m) per use.
 * Shared by parcel creation and by the roadside lot area chosen when laying a street.
 */
export const SIZE_PRESETS = [
  { nameKey: "parcel_creation.presetSmallHouse", width: 10, depth: 10 },
  { nameKey: "parcel_creation.presetHouseShop", width: 15, depth: 15 },
  { nameKey: "parcel_creation.presetMidRise", width: 30, depth: 30 },
  { nameKey: "parcel_creation.presetHighRise", width: 60, depth: 60 },
  { nameKey: "parcel_creation.presetLargeFactory", width: 150, depth: 150 },
];
