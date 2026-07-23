/**
 * パスのアイコン画像を解決するユーティリティ
 *
 * path_type の値に対応する画像ファイルを返す。
 * 対応するファイルが存在しない場合は PATH_DEFAULT (0) にフォールバックする。
 *
 * path_type → ファイル名の対応:
 * - PATH_DEFAULT   (0)  → paths/path_0.png
 * - PATH_MOTORWAY  (1)  → paths/path_1.png
 * - PATH_STREET    (2)  → paths/path_2.png
 * - PATH_WALKWAY   (3)  → paths/path_3.png
 * - PATH_TRACK     (4)  → paths/path_4.png
 * - PATH_TRANSPORT (10) → paths/path_10.png
 */

import { PATH_TRANSPORT } from "../constants";

const pathModules = import.meta.glob<{ default: string }>(
  "../assets/images/paths/*.png",
  { eager: true }
);

/**
 * パスタイプに対応するアイコン画像パスを返す
 */
export function resolvePathIcon(pathType: number): string {
  const key = `../assets/images/paths/path_${pathType}.png`;
  if (pathModules[key]) {
    return pathModules[key].default;
  }
  const fallback = "../assets/images/paths/path_0.png";
  return pathModules[fallback]?.default ?? "";
}

const transportModules = import.meta.glob<{ default: string }>(
  "../assets/images/transport/*.png",
  { eager: true }
);

/**
 * 公共交通の区間パスバリアント(transit_master の transit_path_id = path_master の
 * PATH_TRANSPORT バリアント)に対応する車両アイコンのファイル名。マスターにアイコン指定の
 * 項目が無いため、バリアントIDとアイコン画像(assets/images/transport/*.png)の対応をここで持つ。
 * 未対応のバリアントは汎用の公共交通アイコンにフォールバックする。
 */
const TRANSIT_VARIANT_ICON_NAME: Record<number, string> = {
  1001: "t_bus", // バス路線
  1002: "t_metro", // メトロ路線
  1003: "t_tram", // LRT路線
  1004: "t_monorail", // モノレール路線
  1005: "t_train", // 中距離鉄道路線
  1006: "t_ropeway", // ロープウェイ路線
};

/**
 * 公共交通の区間パスバリアントに対応する車両アイコン画像パスを返す。
 */
export function resolveTransitIcon(pathVariant: number): string {
  const name = TRANSIT_VARIANT_ICON_NAME[pathVariant];
  if (name) {
    const key = `../assets/images/transport/${name}.png`;
    if (transportModules[key]) {
      return transportModules[key].default;
    }
  }
  return resolvePathIcon(PATH_TRANSPORT);
}
