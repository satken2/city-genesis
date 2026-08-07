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
 */

import { useTranslation } from "react-i18next";
import { Money } from "./Money";
import { costRatioColor } from "../../utils/costRatio";
import "./ConstructionInfoCard.css";

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
  /** 建設中・解体中の実績情報。渡すと1エポックコスト・残り期間ブロックを表示する */
  perEpoch?: ConstructionPerEpochInfo;
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
  perEpoch,
}: ConstructionInfoCardProps) {
  const { t } = useTranslation();

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
      </div>

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
