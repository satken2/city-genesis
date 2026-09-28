/**
 * バックエンドが管理する地形マップ(GET /api/v1/terrain/maps で一覧取得できるもの)の
 * サムネイル画像を表示する共通部品。
 *
 * サムネイルは GET /api/v1/terrain/maps/{map_id}/thumbnail から取得する(認証ヘッダーが要るので
 * `useApiImageUrl` で Blob URL にして表示する)。海が透過の平面図PNGで、未生成のマップでは404になる。
 * 取得できなければプレースホルダ(背景色のみのdiv)にフォールバックする。
 *
 * サイズはCSSクラスで決まる。既定はタイル用(new-game-tile-image)で、
 * ニューゲーム画面の詳細パネルのように大きく出したい場合は className を渡す。
 */

import { useApiImageUrl } from "../../hooks/useApiImageUrl";

type TerrainMapThumbnailProps = {
  apiBaseUrl: string;
  mapId: string;
  /** 画像・プレースホルダ双方に付けるサイズ用クラス */
  className?: string;
  /** フォールバック時に追加する色指定クラス */
  placeholderModifier?: string;
};

export function TerrainMapThumbnail({
  apiBaseUrl,
  mapId,
  className = "new-game-tile-image",
  placeholderModifier = "new-game-tile-image--terrain",
}: TerrainMapThumbnailProps) {
  const url = apiBaseUrl
    ? `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/terrain/maps/${encodeURIComponent(mapId)}/thumbnail`
    : null;
  const { src, failed, onError } = useApiImageUrl(url);

  if (!url || failed) {
    return <div className={`${className} ${placeholderModifier}`} />;
  }
  if (!src) {
    return <div className={className} />;
  }
  return (
    <img
      className={`${className} terrain-map-thumbnail`}
      src={src}
      alt=""
      onError={onError}
    />
  );
}
