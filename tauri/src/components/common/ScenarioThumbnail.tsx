/**
 * シナリオ（テンプレート）のサムネイル画像を表示する共通部品。
 *
 * バックエンドの GET /api/v1/saves/{templateId}/thumbnail?template=true からシナリオ画像を取得する
 * (認証ヘッダーが要るので `useApiImageUrl` で Blob URL にする)。画像が登録されていない（404）場合は
 * プレースホルダー(div)を表示する。標高ファイルから取り込んだシナリオは、作成直後に地形の平面図が登録される。
 */

import { useApiImageUrl } from "../../hooks/useApiImageUrl";

type ScenarioThumbnailProps = {
  apiBaseUrl: string;
  templateId: string;
  revision?: number;
  /** 画像・プレースホルダ双方に付けるサイズ用クラス */
  className?: string;
  /** フォールバック時に追加する色指定クラス */
  placeholderModifier?: string;
};

export function ScenarioThumbnail({
  apiBaseUrl,
  templateId,
  revision,
  className = "new-game-tile-image",
  placeholderModifier = "new-game-tile-image--template",
}: ScenarioThumbnailProps) {
  const url = apiBaseUrl
    ? `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/saves/${encodeURIComponent(templateId)}/thumbnail?template=true${revision === undefined ? "" : `&revision=${revision}`}`
    : null;
  const { src, failed, onError } = useApiImageUrl(url);

  if (url && !failed) {
    return src
      ? <img className={className} src={src} alt="" onError={onError} />
      : <div className={className} />;
  }
  return <div className={`${className} ${placeholderModifier}`} />;
}
