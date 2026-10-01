/**
 * 建設情報カード(共通部品)
 *
 * 建設の見積もり情報(建設期間・概算コストと標準コストに対する倍率バッジ・概算維持費)と、
 * 建設中・解体中の実績情報(1エポックあたりのコスト・残りエポック数)を表示する共通カード。
 * 以下の6箇所で共用する。
 * - 建物建設パネル (building/BuildingConstructionPanel)
 * - 道路建設パネル (path/PathConstructionPanel)
 * - 経済主体詳細パネルの建設工事タブ (agent/BuildingConstructionInfoPanel)
 * - パス詳細パネルの建設工事情報 (path/PathConstructionInfoPanel)
 * - 造成パネル (grading/GradingPanel)
 * - 経済主体詳細パネルの造成工事の建設工事タブ (grading/GradingConstructionInfoPanel)
 *
 * 表示ルール:
 * - leadingRows を渡すと、呼び出し側が組んだ行をグリッドの先頭に付ける(造成の種類・切土量など、
 *   ドメイン固有の数量をコストと同じ表の見た目で並べるため)。
 * - buildingArea / totalHeight / totalFloorArea を渡すと物理情報の行が先頭に付く
 *   (建物系のみ。パス系は渡さない=非表示。null は取得中・未確定として "—" を表示)。
 * - 標準コストが正の値のときのみ、推定コストの右に「x1.3」のような倍率バッジを表示する
 *   (標準コスト0/nullは未舗装路など cost_ratio=0 のケースがあるため0除算を回避する)。
 * - 倍率バッジのホバー説明(割高になる理由)はドメインごとに文言が異なるため、
 *   翻訳済み文字列を costRatioHelp で受け取る。
 * - nextEpochConstructionBudget を渡すと「来期の建設予算の X%」の注記を出す(建設パネル用)。
 * - maintenanceCost を渡すと概算維持費(1エポックあたり)の行が付く(パス建設パネル用)。
 * - epochRevenue(直近に終わったエポックの市の収入合計)が正のとき、概算コストと概算維持費の
 *   横にその収入に対する割合を出す。
 * - perEpoch を渡すと「1エポックのコスト・残り期間」ブロックを表示する(建設中・解体中の詳細パネル用)。
 * - supplyRatio / expectedDurationDays を渡すと、都市の建設サービス供給力から見た
 *   需給見通しを表示する(建設パネル用)。充足率が SUPPLY_RATIO_OK 未満のときだけ
 *   「見込み建設期間」の行と警告バナーが付く(閾値は下記の定数)。
 * - availableFunds / requiredTotal を渡すと、必要総額が利用可能額を超えるときに資金不足の
 *   警告バナーを付ける(建設パネル用。確定ボタンの無効化は呼び出し側が
 *   utils/constructionFunding の同じ判定で行う)。showAvailableFunds が false でなければ
 *   「利用可能な資金」の行も出す。
 */

import { Fragment, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Money } from "./Money";
import { costRatioColor } from "../../utils/costRatio";
import { constructionFundsShortfall } from "../../utils/constructionFunding";
import "./ConstructionInfoCard.css";

// 建設サービス充足率(construction_supply_ratio)の表示閾値。
// OK 以上: 表示なし / MILD 以上: 黄色の注意 / MILD 未満: オレンジの警告。
// ゲームルールではなく表示上の区切りのため、UI側の定数として持つ。
const SUPPLY_RATIO_OK = 0.9;
const SUPPLY_RATIO_MILD = 0.5;

/** 収入に対する割合(%)。維持費のような小さい割合も 0% に潰れないよう、1% 未満は有効数字2桁にする */
function formatRevenuePercent(amount: number, revenue: number): string {
  const percent = (amount / revenue) * 100;
  if (percent >= 100) return percent.toFixed(0);
  if (percent >= 1) return percent.toFixed(1);
  if (percent > 0 && percent < 0.01) return "<0.01";
  return String(Number(percent.toPrecision(2)));
}

type ConstructionPerEpochInfo = {
  /** 1エポックあたりの建設(解体)コスト */
  costPerEpoch: number | null;
  /** 残りエポック数 */
  remainingEpochs: number | null;
  /** 解体中ならラベルを「解体コスト」「残り解体期間」に切り替える */
  isDemolishing: boolean;
};

export type ConstructionInfoRow = {
  label: string;
  value: ReactNode;
};

type ConstructionInfoCardProps = {
  /** カード見出し。省略時は見出しなし(呼び出し側のセクション見出しに任せる) */
  title?: string;
  /** グリッドの先頭に付ける行(翻訳済みのラベルと整形済みの値) */
  leadingRows?: ConstructionInfoRow[];
  /** 建築面積(m²)。undefined なら行ごと非表示(パス系)、null なら "—" */
  buildingArea?: number | null;
  /** 建物高さ(m)。undefined なら行ごと非表示、null なら "—" */
  totalHeight?: number | null;
  /** 延床面積(m²)。undefined なら行ごと非表示、null なら "—" */
  totalFloorArea?: number | null;
  /** パス長(m)。undefined なら行ごと非表示(建物系)、null なら "—"(パス系で未確定) */
  pathLength?: number | null;
  /** 概算コスト(総額) */
  estimatedCost: number | null;
  /** 完全に平坦な地形で建設した場合の標準コスト。倍率バッジの分母 */
  standardCost: number | null;
  /** 倍率バッジのホバー説明(翻訳済み文字列) */
  costRatioHelp?: string;
  /** 建設期間(日) */
  estimatedDurationDays: number | null;
  /** 建設コスト取得中フラグ */
  isCostLoading: boolean;
  /** 来期の建設予算。渡すと予算に対する割合の注記を表示する */
  nextEpochConstructionBudget?: number | null;
  /** 概算維持費(1エポックあたり)。undefined なら行ごと非表示、null なら "—" */
  maintenanceCost?: number | null;
  /** 直近に終わったエポックの市の収入合計。正のとき概算コスト・概算維持費に割合を添える */
  epochRevenue?: number | null;
  /** 建設サービス財の調達充足率(0〜1)。1.0=推定建設期間どおりに進む見込み */
  supplyRatio?: number | null;
  /** 充足率を織り込んだ見込み建設期間(日) */
  expectedDurationDays?: number | null;
  /** 発注に使える市の資金(現預金 − 貯蓄目標 − 既存案件の拘束額)。渡すと行を表示する */
  availableFunds?: number | null;
  /** 資金審査に使う必要総額(建設費 + 用地買収見積)。availableFunds と比較する */
  requiredTotal?: number | null;
  /** false なら「利用可能な資金」の行を出さない(資金不足の警告は出す) */
  showAvailableFunds?: boolean;
  /** 建設中・解体中の実績情報。渡すと1エポックコスト・残り期間ブロックを表示する */
  perEpoch?: ConstructionPerEpochInfo;
};

export function ConstructionInfoCard({
  title,
  leadingRows,
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
  maintenanceCost,
  epochRevenue,
  supplyRatio,
  expectedDurationDays,
  availableFunds,
  requiredTotal,
  showAvailableFunds = true,
  perEpoch,
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

  const revenueRatio = (amount: number) =>
    epochRevenue != null && epochRevenue > 0 ? (
      <span className="construction-revenue-ratio">
        {t("construction.revenueRatio", { percent: formatRevenuePercent(amount, epochRevenue) })}
      </span>
    ) : null;

  return (
    <div className="construction-info-card">
      {title && <div className="construction-info-title">{title}</div>}

      <div className="construction-info-grid">
        {leadingRows?.map((row) => (
          <Fragment key={row.label}>
            <span className="construction-info-label">{row.label}</span>
            <span className="construction-info-value">{row.value}</span>
          </Fragment>
        ))}

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

        <span className="construction-info-label">{t("construction.estimatedDuration")}</span>
        <span className="construction-info-value">
          {isCostLoading
            ? t("common.loading")
            : estimatedDurationDays != null
            ? t("construction.durationDays", { value: Math.ceil(estimatedDurationDays) })
            : "—"}
        </span>

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
              {revenueRatio(estimatedCost)}
            </>
          ) : (
            "—"
          )}
        </span>

        {maintenanceCost !== undefined && (
          <>
            <span className="construction-info-label">{t("construction.estimatedMaintenanceCost")}</span>
            <span className="construction-info-value">
              {isCostLoading ? (
                t("common.loading")
              ) : maintenanceCost != null ? (
                <>
                  <Money value={maintenanceCost} unit />
                  {revenueRatio(maintenanceCost)}
                </>
              ) : (
                "—"
              )}
            </span>
          </>
        )}
      </div>

      {/* 建設サービスの供給不足警告 */}
      {supplyShortageLevel && (
        <div className={`construction-supply-note construction-supply-note-${supplyShortageLevel}`}>
          {supplyShortageLevel === "severe"
            ? t("construction.supplyShortageSevere")
            : t("construction.supplyShortageMild")}
        </div>
      )}

      {/* 発注に使える資金と、資金不足の警告(建設パネルでの発注可否の先回り表示) */}
      {showAvailableFunds && availableFunds !== undefined && (
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
