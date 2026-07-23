/**
 * 土地区画のアイコン画像を解決するユーティリティ
 *
 * parcel_type の値に対応する画像ファイルを返す。
 * 対応するファイルが存在しない場合は PARCEL_LAND (1) にフォールバックする。
 *
 * parcel_type → ファイル名の対応:
 * - PARCEL_LAND      (1) → parcels/parcel_1.png
 * - PARCEL_SHORE     (2) → parcels/parcel_2.png
 * - PARCEL_WATER     (3) → parcels/parcel_3.png
 * - PARCEL_RECLAIMED (4) → parcels/parcel_4.png
 */

const parcelModules = import.meta.glob<{ default: string }>(
  "../assets/images/parcels/*.png",
  { eager: true }
);

/**
 * 土地区画タイプに対応するアイコン画像パスを返す
 */
export function resolveParcelIcon(parcelType: number): string {
  const key = `../assets/images/parcels/parcel_${parcelType}.png`;
  if (parcelModules[key]) {
    return parcelModules[key].default;
  }
  const fallback = "../assets/images/parcels/parcel_1.png";
  return parcelModules[fallback]?.default ?? "";
}
