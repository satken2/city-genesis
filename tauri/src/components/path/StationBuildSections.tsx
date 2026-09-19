/**
 * 駅タブ(軌道建設パネルの簡易モード)のボディ
 *
 * 詳細モードのように形状を引かせず、置ける駅を「相対式(対面ホーム)の複線 + 駅舎」で固定する。
 * プレイヤーが決めるのは次の4つだけ:
 *   - プラットフォームの規格(電化/非電化・鉄道/モノレール)
 *   - 駅舎の種類
 *   - ホーム長(両数。m換算を併記する)
 *   - 高さと角度(どちらも3Dビューの操作が主で、ここは数値の微調整用)
 *
 * 高さは3DビューのPageUp/PageDown、角度は中ボタンドラッグでも変えられ、その結果は
 * `edit_station:placement_state` でここのステッパーへ書き戻される(パネルからのエコーはしない)。
 * 線路間隔・ホーム設置側・建屋の配置端・曲線は固定で、変えたい場合は詳細モードを使う。
 */

import { useTranslation } from "react-i18next";
import { NumberStepper } from "../common/NumberStepper";
import { ConstructionInfoCard } from "../common/ConstructionInfoCard";
import { PathVariantDescription } from "./PathVariantDescription";
import { RailVariantSelector } from "./RailVariantSelector";
import { isConstructionAffordable } from "../../utils/constructionFunding";
import type { PathMasterEntry } from "../../types";
import type { VariantAffordability } from "../../hooks/useVariantAffordability";
import type { useStationConstruction } from "./useStationConstruction";
import { STATION_MAX_CAR_COUNT, STATION_MIN_CAR_COUNT } from "../../local-constants";

type StationBuildSectionsProps = {
  sc: ReturnType<typeof useStationConstruction>;
  /** プラットフォームの候補(鉄道種別で絞った駅バリアント)。 */
  platformVariantOptions: { id: number; data: PathMasterEntry }[];
  /** 駅舎の候補(type=PATH_STATION_HOUSE の全バリアント)。 */
  houseVariantOptions: { id: number; data: PathMasterEntry }[];
  affordability: VariantAffordability | null;
  isConfirming: boolean;
  onConfirm: () => void;
  onCancel: () => void;
};

export function StationBuildSections({
  sc,
  platformVariantOptions,
  houseVariantOptions,
  affordability,
  isConfirming,
  onConfirm,
  onCancel,
}: StationBuildSectionsProps) {
  const { t } = useTranslation();

  return (
    <>
      {/* プラットフォームの規格(詳細モードのプラットフォームタブと同じ候補) */}
      <RailVariantSelector
        variantOptions={platformVariantOptions}
        selectedVariant={sc.platformVariant}
        onSelectVariant={sc.selectPlatformVariant}
        affordability={affordability}
      />

      <PathVariantDescription entry={sc.platformData} />

      {/* 駅舎の種類。簡易モードで唯一の駅舎に関する選択肢。 */}
      <div className="pc-section">
        <div className="pc-settings-row">
          <span className="pc-settings-label">{t("track_construction.station.houseVariant")}</span>
          <select
            className="pc-select"
            value={sc.houseVariant ?? ""}
            onChange={(e) => {
              const val = e.target.value;
              if (val) sc.selectHouseVariant(parseInt(val));
            }}
          >
            {houseVariantOptions.map((v) => (
              <option key={v.id} value={v.id}>
                {v.data.display_name}
              </option>
            ))}
          </select>
        </div>
      </div>

      {/* 駅の寸法・向き。ホーム長は両数指定で、実長(m)を併記する。 */}
      <div className="pc-section">
        <div className="pc-section-title">{t("track_construction.station.settings")}</div>

        <div className="pc-settings-row">
          <span className="pc-settings-label">{t("track_construction.station.carCount")}</span>
          <div className="pc-stepper-row">
            <NumberStepper
              value={sc.carCount}
              onChange={sc.setCarCount}
              min={STATION_MIN_CAR_COUNT}
              max={STATION_MAX_CAR_COUNT}
              step={1}
            />
            <span className="pc-stepper-unit">
              {t("track_construction.station.carCountUnit", { length: Math.round(sc.platformLength) })}
            </span>
          </div>
        </div>

        {/* 高さ。3DビューのPageUp/PageDownと双方向で、地上・高架・地下を切り替える。 */}
        {sc.heightEditable && (
          <div className="pc-settings-row">
            <span className="pc-settings-label">{t("path_construction.elevation")}</span>
            <div className="pc-stepper-row">
              <NumberStepper
                value={sc.height}
                onChange={sc.setHeight}
                min={sc.minHeight}
                max={500}
                step={0.1}
                integerOnly={false}
              />
              <span className="pc-stepper-unit">m</span>
            </div>
          </div>
        )}

        {/* 角度。3Dビューの中ボタンドラッグと双方向。0度=北(ワールド+Z)向き。 */}
        <div className="pc-settings-row">
          <span className="pc-settings-label">{t("track_construction.station.angle")}</span>
          <div className="pc-stepper-row">
            <NumberStepper value={sc.angle} onChange={sc.setAngle} min={0} max={360} step={5} />
            <span className="pc-stepper-unit">°</span>
          </div>
        </div>

        <div className="pc-no-nodes-message">{t("track_construction.station.fixedNote")}</div>
      </div>

      {/* 建設費用(プラットフォーム2本+駅舎1本の合計)。地点を置くまでは算出できない。 */}
      <div className="pc-section">
        <div className="pc-section-title">{t("path_construction.costInfo")}</div>
        {!sc.hasPlacement ? (
          <div className="pc-no-nodes-message">{t("track_construction.station.pickPointFirst")}</div>
        ) : (
          <ConstructionInfoCard
            pathLength={sc.platformLength}
            estimatedCost={sc.estimatedCost}
            standardCost={sc.estimatedStandardCost}
            costRatioHelp={t("path_construction.costRatioHelp")}
            estimatedDurationDays={sc.estimatedDurationDays}
            isCostLoading={sc.isCostLoading}
            nextEpochConstructionBudget={sc.nextEpochConstructionBudget}
            availableFunds={sc.availableFunds}
            requiredTotal={sc.estimatedCost}
            supplyRatio={sc.supplyRatio}
            expectedDurationDays={sc.expectedDurationDays}
            alignment={sc.alignment}
          />
        )}
      </div>

      <div className="pc-action-bar">
        {sc.hasPlacement && (!sc.canConfirm || !sc.alignmentOk) && (
          <div role="status">
            {(sc.canConfirm ? [t("path_construction.alignmentUnready")] : sc.validationMessages)
              .map((message, index) => <p key={index}>{message}</p>)}
            {sc.canRetryValidation && (
              <button className="pc-btn" type="button" onClick={sc.retryValidation}>
                {t("path_construction.validationRetry")}
              </button>
            )}
          </div>
        )}
        <div className="pc-actions">
          <button className="pc-btn" type="button" onClick={onCancel}>
            {t("path_construction.cancel")}
          </button>
          <button
            className="pc-btn pc-btn-confirm"
            disabled={
              !sc.canConfirm
              || isConfirming
              || !sc.alignmentOk
              || !isConstructionAffordable(sc.estimatedCost, sc.availableFunds)
            }
            type="button"
            onClick={onConfirm}
          >
            {isConfirming ? t("path_construction.confirming") : t("path_construction.confirm")}
          </button>
        </div>
      </div>
    </>
  );
}
