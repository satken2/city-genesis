import { useTranslation } from "react-i18next";
import { GroundCoverIcon } from "../taskbar/GroundCoverIcon";
import "./TerrainFacts.css";

/**
 * 地形の大きさ・地表・気候を並べる事実欄(ニューゲームの詳細とシナリオ管理の地形タブで共用)。
 * 地表は GroundCoverIcon の自作SVG、気候は `climate.<値>` で表示する。
 */
export function TerrainFacts({ mapSize, groundCover, climate }: {
  mapSize: string | null;
  /** マップを特定できないときは null(「不明」と表示する) */
  groundCover: number | null;
  /** normalizeClimate 済みの気候区分 */
  climate: number;
}) {
  const { t } = useTranslation();
  return (
    <dl className="terrain-facts">
      <div className="terrain-fact">
        <dt>{t("newGame.detailMapSizeLabel")}</dt>
        <dd className="terrain-fact-size">{mapSize ?? t("terrainProfiles.unknown")}</dd>
      </div>
      <div className="terrain-fact">
        <dt>{t("groundCover.label")}</dt>
        <dd>
          {groundCover !== null
            ? <GroundCoverIcon cover={groundCover} label={t(`groundCover.${groundCover}`)} />
            : t("terrainProfiles.unknown")}
        </dd>
      </div>
      <div className="terrain-fact terrain-fact--climate">
        <dt>{t("climate.label")}</dt>
        <dd>{t(`climate.${climate}`)}</dd>
      </div>
    </dl>
  );
}
