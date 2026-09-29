/**
 * パスのアイコン画像を解決するユーティリティ
 *
 * path_type の値に対応する画像ファイルを返す。
 * 対応するファイルが存在しない場合は PATH_DEFAULT (0) にフォールバックする。
 *
 * path_type → ファイル名の対応:
 * - PATH_DEFAULT   (0)  → paths/path_0.png
 * - PATH_STREET    (1)  → paths/path_1.png
 * - PATH_MOTORWAY  (2)  → paths/path_2.png
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
 * パスバリアントIDと個別アイコンファイル名の対応。
 * master.yaml にアイコン指定項目が無いため、線路の橋梁や駅プラットフォームなど
 * 種別画像(path_{path_type}.png)とは明確に異なる意匠を持つバリアントの対応をここで持つ。
 */
const PATH_VARIANT_ICON_MAP: Record<number, string> = {
  413: "path_4b", // 鉄道橋
  423: "path_4b", // 複線鉄道橋
  461: "path_4s", // 鉄道PF
  462: "path_4s", // 階段付PF(対向)
  463: "path_4s", // コンテナ積卸PF
  464: "path_4s", // 砕石積卸PF
  465: "path_4s", // 原油積卸PF
  471: "path_4s", // 鉄道PF(対向)
  472: "path_4s", // 鉄道PF(島)
  502: "path_5s", // モノレールPF(対向)
  602: "path_6s", // ロープウェイ駅
};

/**
 * パスタイプおよびバリアントに対応するアイコン画像パスを返す。
 * バリアント個別画像(または対応マップ)が存在すれば優先し、無ければパスタイプの画像、
 * それも無ければ PATH_DEFAULT (0) にフォールバックする。
 */
export function resolvePathIcon(pathType: number, variantId?: number): string {
  if (variantId != null) {
    const variantKey = `../assets/images/paths/path_${variantId}.png`;
    if (pathModules[variantKey]) {
      return pathModules[variantKey].default;
    }
    const mappedName = PATH_VARIANT_ICON_MAP[variantId];
    if (mappedName) {
      const mappedKey = `../assets/images/paths/${mappedName}.png`;
      if (pathModules[mappedKey]) {
        return pathModules[mappedKey].default;
      }
    }
  }
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
