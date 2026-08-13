/**
 * バックエンドが管理する地形マップ(GET /api/v1/terrain/maps で一覧取得できるもの)の
 * サムネイル画像を表示する共通部品。
 *
 * サムネイルは GET /api/v1/terrain/maps/{map_id}/thumbnail から取得する。地形エディタで
 * 編集した地形を .npy エクスポートした際にだけ同じ場所へサムネイルPNGが生成されるため、
 * 組み込みマップ(与論島・青ヶ島等)や未生成のマップでは404になる。その場合は
 * プレースホルダ(背景色のみのdiv)にフォールバックする。
 *
 * サイズはCSSクラスで決まる。既定はタイル用(new-game-tile-image)で、
 * ニューゲーム画面の詳細パネルのように大きく出したい場合は className を渡す。
 */

import { useState } from "react";

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
  // 404だったマップIDを覚える。真偽値で持つと、同じ要素位置でmapIdだけが
  // differ した場合(詳細パネルの選択切り替え)に前のマップの失敗状態を引き継いでしまう。
  const [failedId, setFailedId] = useState<string | null>(null);

  if (!apiBaseUrl || failedId === mapId) {
    return <div className={`${className} ${placeholderModifier}`} />;
  }

  const base = apiBaseUrl.replace(/\/+$/, "");
  return (
    <img
      className={className}
      src={`${base}/api/v1/terrain/maps/${mapId}/thumbnail`}
      alt=""
      onError={() => setFailedId(mapId)}
    />
  );
}
