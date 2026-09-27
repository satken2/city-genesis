/**
 * シナリオ（テンプレート）のサムネイル画像を表示する共通部品。
 *
 * まずバックエンドの GET /api/v1/saves/{templateId}/thumbnail?template=true から
 * シナリオ固有のサムネイル画像を取得する。
 * 画像が登録されていない（404）場合は、舞台となる地形マップのサムネイル(TerrainMapThumbnail)にフォールバックする。
 * 地形マップも未設定またはサムネイルが無い場合は、プレースホルダー(div)を表示する。
 */

import { useState } from "react";
import { TerrainMapThumbnail } from "./TerrainMapThumbnail";

type ScenarioThumbnailProps = {
  apiBaseUrl: string;
  templateId: string;
  revision?: number;
  terrainMapId?: string | null;
  /** 画像・プレースホルダ双方に付けるサイズ用クラス */
  className?: string;
  /** フォールバック時に追加する色指定クラス */
  placeholderModifier?: string;
};

export function ScenarioThumbnail({
  apiBaseUrl,
  templateId,
  revision,
  terrainMapId,
  className = "new-game-tile-image",
  placeholderModifier = "new-game-tile-image--template",
}: ScenarioThumbnailProps) {
  // 404だったテンプレートIDを覚える
  const [failedId, setFailedId] = useState<string | null>(null);

  const base = apiBaseUrl ? apiBaseUrl.replace(/\/+$/, "") : "";
  const hasFailed = !apiBaseUrl || failedId === templateId;

  if (!hasFailed) {
    return (
      <img
        className={className}
        src={`${base}/api/v1/saves/${encodeURIComponent(templateId)}/thumbnail?template=true${revision === undefined ? "" : `&revision=${revision}`}`}
        alt=""
        onError={() => setFailedId(templateId)}
      />
    );
  }

  // シナリオ固有の画像が無い場合は地形サムネイルへフォールバック
  if (terrainMapId) {
    return (
      <TerrainMapThumbnail
        apiBaseUrl={apiBaseUrl}
        mapId={terrainMapId}
        className={className}
        placeholderModifier={placeholderModifier}
      />
    );
  }

  return <div className={`${className} ${placeholderModifier}`} />;
}
