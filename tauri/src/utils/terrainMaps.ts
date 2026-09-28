import type { TFunction } from "i18next";
import { getTerrainProfile } from "../data/terrainProfiles";
import { resolveIntlLocale } from "./locale";

/** GET /api/v1/terrain/maps の要素。オリジナル地形(標高・サムネイル・範囲)の登録表の1行。 */
export type TerrainMapEntry = {
  id: string;
  name: string;
  settings: Record<string, unknown>;
  /** 地表種別(GROUND_COVER_*) */
  ground_cover?: number;
  /** 気候区分(CLIMATE_*) */
  climate?: number;
};

/**
 * 地形の表示名。紹介を翻訳リソースに持つ地形は翻訳名を使う。
 * 登録表の name はマップIDと同じ内部名(固有地名を含みうる)なので、翻訳の無い地形だけの代用にする。
 */
export function terrainMapTitle(map: Pick<TerrainMapEntry, "id" | "name">, t: TFunction): string {
  const profile = getTerrainProfile(map.id);
  return profile ? t(`terrainProfiles.maps.${profile.id}.name`) : map.name;
}

/** 地形の自然地理の紹介文。翻訳が無い地形は共通の「説明なし」文言を返す。 */
export function terrainMapDescription(mapId: string, t: TFunction): string {
  const profile = getTerrainProfile(mapId);
  return profile ? t(`terrainProfiles.maps.${profile.id}.description`) : t("terrainProfiles.noDescription");
}

/**
 * 地形の settings からキロメートル単位の大きさ(例: 7 × 6 km)を組み立てる。
 * map_width / map_height はメートル単位。欠けている・不正な値なら null。
 */
export function formatMapSize(settings: Record<string, unknown> | undefined, language: string): string | null {
  if (!settings) return null;
  const width = settings.map_width;
  const height = settings.map_height;
  if (typeof width !== "number" || typeof height !== "number" || !Number.isFinite(width) || !Number.isFinite(height) || width <= 0 || height <= 0) return null;
  const format = new Intl.NumberFormat(resolveIntlLocale(language), { maximumFractionDigits: 3 });
  return `${format.format(width / 1000)} × ${format.format(height / 1000)} km`;
}
