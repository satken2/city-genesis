/**
 * Shared scenario thumbnail component.
 *
 * Fetch from GET /api/v1/saves/{scenarioId}/thumbnail?scenario=true through useApiImageUrl,
 * which produces a Blob URL. If unregistered (404), show a placeholder div.
 * Scenarios imported from elevation files receive a terrain plan image immediately on creation.
 */

import { useApiImageUrl } from "../../hooks/useApiImageUrl";

type ScenarioThumbnailProps = {
  apiBaseUrl: string;
  scenarioId: string;
  /** Content revision (content_revision). Images are HTTP-cached, so use it to change the URL only when content changes. */
  revision?: number;
  /** Sizing class applied to both image and placeholder. */
  className?: string;
  /** Additional color class for fallback display. */
  placeholderModifier?: string;
};

export function ScenarioThumbnail({
  apiBaseUrl,
  scenarioId,
  revision,
  className = "new-game-tile-image",
  placeholderModifier = "new-game-tile-image--scenario",
}: ScenarioThumbnailProps) {
  const url = apiBaseUrl
    ? `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/saves/${encodeURIComponent(scenarioId)}/thumbnail?scenario=true${revision === undefined ? "" : `&revision=${revision}`}`
    : null;
  const { src, failed, onError } = useApiImageUrl(url);

  if (url && !failed) {
    return src
      ? <img className={className} src={src} alt="" onError={onError} />
      : <div className={className} />;
  }
  return <div className={`${className} ${placeholderModifier}`} />;
}
