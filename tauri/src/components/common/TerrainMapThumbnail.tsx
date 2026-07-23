/**
 * バックエンドが管理する地形マップ(GET /api/v1/terrain/maps で一覧取得できるもの)の
 * サムネイル画像を表示する共通部品。
 *
 * サムネイルは GET /api/v1/terrain/maps/{map_id}/thumbnail から取得する。地形エディタで
 * 編集した地形を .npy エクスポートした際にだけ同じ場所へサムネイルPNGが生成されるため、
 * 組み込みマップ(与論島・青ヶ島等)や未生成のマップでは404になる。その場合は既存の
 * プレースホルダ用CSSクラス(new-game-tile-image--terrain)にフォールバックする。
 */

import { useState } from "react";

type TerrainMapThumbnailProps = {
  apiBaseUrl: string;
  mapId: string;
};

export function TerrainMapThumbnail({ apiBaseUrl, mapId }: TerrainMapThumbnailProps) {
  const [failed, setFailed] = useState(false);

  if (failed || !apiBaseUrl) {
    return <div className="new-game-tile-image new-game-tile-image--terrain" />;
  }

  const base = apiBaseUrl.replace(/\/+$/, "");
  return (
    <img
      className="new-game-tile-image"
      src={`${base}/api/v1/terrain/maps/${mapId}/thumbnail`}
      alt=""
      onError={() => setFailed(true)}
    />
  );
}
