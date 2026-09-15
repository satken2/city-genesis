/**
 * 建設情報カード(共通部品)
 *
 * 建設の見積もり情報(推定建設コスト・標準コストに対する倍率バッジ・推定建設期間)と、
 * 建設中・解体中の実績情報(1エポックあたりのコスト・残りエポック数)を表示する共通カード。
 * 以下の4箇所で共用する。
 * - 建物建設パネル (building/BuildingConstructionPanel)
 * - 道路建設パネル (path/PathConstructionPanel)
 * - 経済主体詳細パネルの建設工事タブ (agent/BuildingConstructionInfoPanel)
 * - パス詳細パネルの建設工事情報 (path/PathConstructionInfoPanel)
 *
 * 表示ルール:
 * - buildingArea / totalHeight / totalFloorArea を渡すと物理情報の行が先頭に付く
 *   (建物系のみ。パス系は渡さない=非表示。null は取得中・未確定として "—" を表示)。
 * - 標準コストが正の値のときのみ、推定コストの右に「x1.3」のような倍率バッジを表示する
 *   (標準コスト0/nullは未舗装路など cost_ratio=0 のケースがあるため0除算を回避する)。
 * - 倍率バッジのホバー説明(割高になる理由)はドメインごとに文言が異なるため、
 *   翻訳済み文字列を costRatioHelp で受け取る。
 * - nextEpochConstructionBudget を渡すと「来期の建設予算の X%」の注記を出す(建設パネル用)。
 * - perEpoch を渡すと「1エポックのコスト・残り期間」ブロックを表示する(建設中・解体中の詳細パネル用)。
 * - supplyRatio / expectedDurationDays を渡すと、都市の建設サービス供給力から見た
 *   需給見通しを表示する(建設パネル用)。充足率が SUPPLY_RATIO_OK 未満のときだけ
 *   「見込み建設期間」の行と警告バナーが付く(閾値は下記の定数)。
 * - availableFunds / requiredTotal を渡すと「利用可能な資金」の行を出し、必要総額が
 *   利用可能額を超えるときは資金不足の警告バナーを付ける(建設パネル用。確定ボタンの
 *   無効化は呼び出し側が utils/constructionFunding の同じ判定で行う)。
 * - alignment を渡すと通過速度・最小曲線半径・最大勾配の行が付く(パス建設パネル用)。
 *   規格の閾値(min_curve_radius / max_grade)を超える値は赤字にし、警告バナーを付ける
 *   (確定ボタンの無効化は呼び出し側=PathBuildSections が alignmentOk で行う)。
 */

import { useTranslation } from "react-i18next";
import type { PathAlignmentSummary } from "../../types";
import { Money } from "./Money";
import { costRatioColor } from "../../utils/costRatio";
import { constructionFundsShortfall } from "../../utils/constructionFunding";
import "./ConstructionInfoCard.css";

// 建設サービス充足率(construction_supply_ratio)の表示閾値。
// OK 以上: 表示なし / MILD 以上: 黄色の注意 / MILD 未満: オレンジの警告。
// ゲームルールではなく表示上の区切りのため、UI側の定数として持つ。
const SUPPLY_RATIO_OK = 0.9;
const SUPPLY_RATIO_MILD = 0.5;

type ConstructionPerEpochInfo = {
  /** 1エポックあたりの建設(解体)コスト */
  costPerEpoch: number | null;
  /** 残りエポック数 */
  remainingEpochs: number | null;
  /** 解体中ならラベルを「解体コスト」「残り解体期間」に切り替える */
  isDemolishing: boolean;
};

type ConstructionInfoCardProps = {
  /** カード見出し。省略時は見出しなし(呼び出し側のセクション見出しに任せる) */
  title?: string;
  /** 建築面積(m²)。undefined なら行ごと非表示(パス系)、null なら "—" */
  buildingArea?: number | null;
  /** 建物高さ(m)。undefined なら行ごと非表示、null なら "—" */
  totalHeight?: number | null;
  /** 延床面積(m²)。undefined なら行ごと非表示、null なら "—" */
  totalFloorArea?: number | null;
  /** パス長(m)。undefined なら行ごと非表示(建物系)、null なら "—"(パス系で未確定) */
  pathLength?: number | null;
  /** 推定建設コスト(総額) */
  estimatedCost: number | null;
  /** 完全に平坦な地形で建設した場合の標準コスト。倍率バッジの分母 */
  standardCost: number | null;
  /** 倍率バッジのホバー説明(翻訳済み文字列) */
  costRatioHelp?: string;
  /** 推定建設期間(日) */
  estimatedDurationDays: number | null;
  /** 建設コスト取得中フラグ */
  isCostLoading: boolean;
  /** 来期の建設予算。渡すと予算に対する割合の注記を表示する */
  nextEpochConstructionBudget?: number | null;
  /** 建設サービス財の調達充足率(0〜1)。1.0=推定建設期間どおりに進む見込み */
  supplyRatio?: number | null;
  /** 充足率を織り込んだ見込み建設期間(日) */
  expectedDurationDays?: number | null;
  /** 発注に使える市の資金(現預金 − 貯蓄目標 − 既存案件の拘束額)。渡すと行を表示する */
  availableFunds?: number | null;
  /** 資金審査に使う必要総額(建設費 + 用地買収見積)。availableFunds と比較する */
  requiredTotal?: number | null;
  /** 建設中・解体中の実績情報。渡すと1エポックコスト・残り期間ブロックを表示する */
  perEpoch?: ConstructionPerEpochInfo;
  /** 線形指標(パス系)。undefined なら行ごと非表示、null なら "—"(取得中・未確定) */
  alignment?: PathAlignmentSummary | null;
};

export function ConstructionInfoCard({
  title,
  buildingArea,
  totalHeight,
  totalFloorArea,
  pathLength,
  estimatedCost,
  standardCost,
  costRatioHelp,
  estimatedDurationDays,
  isCostLoading,
  nextEpochConstructionBudget,
  supplyRatio,
  expectedDurationDays,
  availableFunds,
  requiredTotal,
  perEpoch,
  alignment,
}: ConstructionInfoCardProps) {
  const { t } = useTranslation();

  // 資金不足額。判定材料が揃っていて必要総額が利用可能額を超えるときだけ正の値になる。
  const fundsShortfall = isCostLoading ? null : constructionFundsShortfall(requiredTotal, availableFunds);

  // 建設サービスの需給警告レベル。充足率が十分(OK以上)なら何も表示しない。
  const supplyShortageLevel: "mild" | "severe" | null =
    !isCostLoading && supplyRatio != null && supplyRatio < SUPPLY_RATIO_OK
      ? supplyRatio < SUPPLY_RATIO_MILD
        ? "severe"
        : "mild"
      : null;

  return (
    <div className="construction-info-card">
      {title && <div className="construction-info-title">{title}</div>}

      <div className="construction-info-grid">
        {buildingArea !== undefined && (
          <>
            <span className="construction-info-label">{t("construction.buildingArea")}</span>
            <span className="construction-info-value">
              {buildingArea != null
                ? `${buildingArea.toFixed(1)} ${t("construction.unitSqm")}`
                : "—"}
            </span>
          </>
        )}

        {totalHeight !== undefined && (
          <>
            <span className="construction-info-label">{t("construction.height")}</span>
            <span className="construction-info-value">
              {totalHeight != null
                ? `${totalHeight.toFixed(1)} ${t("construction.unitMeter")}`
                : "—"}
            </span>
          </>
        )}

        {totalFloorArea !== undefined && (
          <>
            <span className="construction-info-label">{t("construction.floorArea")}</span>
            <span className="construction-info-value">
              {totalFloorArea != null
                ? `${totalFloorArea.toFixed(1)} ${t("construction.unitSqm")}`
                : "—"}
            </span>
          </>
        )}

        {pathLength !== undefined && (
          <>
            <span className="construction-info-label">{t("construction.pathLength")}</span>
            <span className="construction-info-value">
              {pathLength != null
                ? `${pathLength.toFixed(1)} ${t("construction.unitMeter")}`
                : "—"}
            </span>
          </>
        )}

        <span className="construction-info-label">{t("construction.estimatedCost")}</span>
        <span className="construction-info-value">
          {isCostLoading ? (
            t("common.loading")
          ) : estimatedCost != null ? (
            <>
              <Money value={estimatedCost} unit />
              {standardCost != null && standardCost > 0 && (
                <span
                  className="construction-cost-ratio-badge"
                  title={costRatioHelp}
                  style={{ backgroundColor: costRatioColor(estimatedCost / standardCost) }}
                >
                  x{(estimatedCost / standardCost).toFixed(1)}
                </span>
              )}
            </>
          ) : (
            "—"
          )}
        </span>

        <span className="construction-info-label">{t("construction.estimatedDuration")}</span>
        <span className="construction-info-value">
          {isCostLoading
            ? t("common.loading")
            : estimatedDurationDays != null
            ? t("construction.durationDays", { value: Math.ceil(estimatedDurationDays) })
            : "—"}
        </span>

        {/* 線形指標(パス系のみ)。規格の閾値を超える値は赤字 */}
        {alignment !== undefined && (
          <>
            <span className="construction-info-label">{t("construction.passSpeed")}</span>
            <span className="construction-info-value">
              {isCostLoading
                ? t("common.loading")
                : alignment != null
                ? t("construction.passSpeedValue", {
                    speed: Math.round(alignment.baseSpeed),
                    max: Math.round(alignment.maxSpeed),
                  })
                : "—"}
            </span>

            <span className="construction-info-label">{t("construction.minCurveRadius")}</span>
            <span
              className={`construction-info-value${alignment?.curveViolated ? " construction-alignment-violated" : ""}`}
            >
              {isCostLoading
                ? t("common.loading")
                : alignment != null
                ? alignment.minCurveRadius != null
                  ? `${Math.round(alignment.minCurveRadius)} ${t("construction.unitMeter")}`
                  : t("construction.straightLine")
                : "—"}
            </span>

            <span className="construction-info-label">{t("construction.maxGrade")}</span>
            <span
              className={`construction-info-value${alignment?.gradeViolated ? " construction-alignment-violated" : ""}`}
            >
              {isCostLoading
                ? t("common.loading")
                : alignment != null
                ? `${alignment.maxGrade.toFixed(1)} %`
                : "—"}
            </span>
          </>
        )}

        {/* 都市の建設能力から見た見込み建設期間。充足率が十分なときは行ごと出さない */}
        {supplyShortageLevel && expectedDurationDays != null && (
          <>
            <span className="construction-info-label">
              {t("construction.expectedDuration")}
            </span>
            <span
              className={`construction-info-value construction-supply-value-${supplyShortageLevel}`}
            >
              {t("construction.expectedDurationDays", {
                value: Math.ceil(expectedDurationDays),
              })}
            </span>
          </>
        )}
      </div>

      {/* 線形制約の違反警告(規格の最小曲線半径・最大勾配を超えている) */}
      {!isCostLoading && alignment?.curveViolated && (
        <div className="construction-supply-note construction-supply-note-severe">
          {t("construction.curveTooTight", { limit: Math.round(alignment.curveRadiusLimit) })}
        </div>
      )}
      {!isCostLoading && alignment?.gradeViolated && (
        <div className="construction-supply-note construction-supply-note-severe">
          {t("construction.gradeTooSteep", { limit: alignment.gradeLimit })}
        </div>
      )}

      {/* 建設サービスの供給不足警告 */}
      {supplyShortageLevel && (
        <div className={`construction-supply-note construction-supply-note-${supplyShortageLevel}`}>
          {supplyShortageLevel === "severe"
            ? t("construction.supplyShortageSevere")
            : t("construction.supplyShortageMild")}
        </div>
      )}

      {/* 発注に使える資金と、資金不足の警告(建設パネルでの発注可否の先回り表示) */}
      {availableFunds !== undefined && (
        <div className="construction-info-grid">
          <span className="construction-info-label">{t("construction.availableFunds")}</span>
          <span className="construction-info-value">
            {availableFunds != null
              ? t("common.currency", { value: Math.round(availableFunds) })
              : "—"}
          </span>
        </div>
      )}
      {fundsShortfall != null && fundsShortfall > 0 && (
        <div className="construction-supply-note construction-supply-note-severe">
          {t("construction.insufficientFunds", {
            value: t("common.currency", { value: Math.round(fundsShortfall) }),
          })}
        </div>
      )}

      {/* 来期の建設予算に対する推定建設コストの割合(建設パネルでの意思決定支援用) */}
      {!isCostLoading
        && estimatedCost != null
        && nextEpochConstructionBudget != null
        && nextEpochConstructionBudget > 0 && (
        <div className="construction-budget-note">
          {t("construction.budgetRatioNote", {
            percent: ((estimatedCost / nextEpochConstructionBudget) * 100).toFixed(1),
          })}
        </div>
      )}

      {/* 建設中・解体中: 1エポックあたりのコストと残り期間 */}
      {perEpoch && (
        <div className="construction-info-grid construction-per-epoch">
          <span className="construction-info-label">
            {perEpoch.isDemolishing
              ? t("construction.demolitionCostPerEpoch")
              : t("construction.constructionCostPerEpoch")}
          </span>
          <span className="construction-info-value">
            {perEpoch.costPerEpoch != null
              ? t("common.currency", { value: Math.round(perEpoch.costPerEpoch) })
              : "—"}
          </span>

          <span className="construction-info-label">
            {perEpoch.isDemolishing
              ? t("construction.remainingDemolition")
              : t("construction.remainingConstruction")}
          </span>
          <span className="construction-info-value">
            {perEpoch.remainingEpochs != null
              ? t("construction.remainingEpochs", { n: Math.ceil(perEpoch.remainingEpochs) })
              : "—"}
          </span>
        </div>
      )}
    </div>
  );
}
