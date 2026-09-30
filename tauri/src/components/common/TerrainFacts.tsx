import { useTranslation } from "react-i18next";
import { GroundCoverIcon } from "../taskbar/GroundCoverIcon";
import "./TerrainFacts.css";

/**
 * マップの大きさ・地表・気候を並べる事実欄(ニューゲームのシナリオの詳細)。
 * 地表は GroundCoverIcon の自作SVG、気候は `climate.<値>` で表示する。
 */
export function TerrainFacts({ mapSize, groundCover, climate }: {
  /** 「7 × 6 km」の形(utils/mapSize の formatMapSize)。不明なら null(「不明」と表示する) */
  mapSize: string | null;
  /** normalizeGroundCover 済みの地表種別 */
  groundCover: number;
  /** normalizeClimate 済みの気候区分 */
  climate: number;
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
        <dd>{t(`climate.${climate}`)}</dd>
      </div>
    </dl>
  );
}
