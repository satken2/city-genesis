import { useTranslation } from "react-i18next";
import { GroundCoverIcon } from "../taskbar/GroundCoverIcon";
import "./TerrainFacts.css";

/**
 * Map size, ground cover, and climate facts (new game scenario details).
 * Ground cover uses our SVG GroundCoverIcon; climate uses the name saved in the scenario.
 */
export function TerrainFacts({ mapSize, groundCover, climate }: {
  /** Format "7 × 6 km" (formatMapSize in utils/mapSize). Null when unknown, displayed as "Unknown". */
  mapSize: string | null;
  /** Ground cover type after normalizeGroundCover. */
  groundCover: number;
  climate: string;
}) {
  const { t } = useTranslation();
  return (
    <dl className="terrain-facts">
      <div className="terrain-fact">
        <dt>{t("newGame.detailMapSizeLabel")}</dt>
        <dd className="terrain-fact-size">{mapSize ?? t("newGame.detailUnknown")}</dd>
      </div>
      <div className="terrain-fact">
        <dt>{t("groundCover.label")}</dt>
        <dd><GroundCoverIcon cover={groundCover} label={t(`groundCover.${groundCover}`)} /></dd>
      </div>
      <div className="terrain-fact">
        <dt>{t("climate.label")}</dt>
        <dd>{climate || t("newGame.detailUnknown")}</dd>
      </div>
    </dl>
  );
}
