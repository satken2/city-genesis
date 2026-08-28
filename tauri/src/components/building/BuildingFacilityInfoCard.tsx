/**
 * 施設情報カード(ディスパッチャ)
 *
 * 建物建設パネルで表示する「施設情報」カード。建設情報カードと違い、内容は経済主体エージェントの
 * type(TYPE_STANDARD / TYPE_BUILDING / インフラ生産施設 など)によって異なる。
 *
 * ここは type を見て専用の子コンポーネントへ委譲するディスパッチャに徹する。AgentDetailPanel が
 * type ごとに専用パネルへ委譲するのと同じ構成。type ごとの詳細な表示内容を追加する場合は、
 * switch に case を 1 つ増やし、対応する子コンポーネントを足すだけでよい。
 *
 * 現状:
 *   - TYPE_STANDARD … StandardFacilityInfo(推定生産量・雇用人数・ランニングコスト)
 *   - それ以外       … default フォールバック(null。カード枠自体を出さない)
 */

import { useTranslation } from "react-i18next";
import type { AgentMasterEntry } from "../../types";
import { TYPE_STANDARD } from "../../constants";
import { Money } from "../common/Money";
import { resolveLedgerIcon } from "../../utils/ledgerIcon";

type BuildingFacilityInfoCardProps = {
  /** 選択中バリアントのマスターデータ。type による出し分けに使う。 */
  variantData: AgentMasterEntry | null;
  /** TYPE_STANDARD のときの推定最大生産量。 */
  estimatedMaxProduction: number | null;
  /** TYPE_STANDARD のときの推定雇用人数。 */
  estimatedEmployment: number | null;
  /** TYPE_STANDARD のときの推定稼働コスト(1エポックあたり。人件費+原料費。専用建物のため家賃なし)。 */
  estimatedOperatingCost: number | null;
  /** 稼働コストの内訳: 人件費(1エポックあたり)。 */
  operatingWageCost: number | null;
  /** 稼働コストの内訳: 原料費(1エポックあたり)。 */
  operatingMaterialCost: number | null;
  /** 稼働コストの支出元となる予算カテゴリ(LEDGER_*)。未定義なら null。 */
  operatingCostCategory: number | null;
  /** 1エポックの日数。「1エポック(n日)あたり」の注記に使う。 */
  simulationDays: number | null;
  /** 建設コスト取得中フラグ。 */
  isCostLoading: boolean;
};

export function BuildingFacilityInfoCard({
  variantData,
  estimatedMaxProduction,
  estimatedEmployment,
  estimatedOperatingCost,
  operatingWageCost,
  operatingMaterialCost,
  operatingCostCategory,
  simulationDays,
  isCostLoading,
}: BuildingFacilityInfoCardProps) {
  switch (variantData?.type) {
    case TYPE_STANDARD:
      return (
        <StandardFacilityInfo
          estimatedMaxProduction={estimatedMaxProduction}
          estimatedEmployment={estimatedEmployment}
          estimatedOperatingCost={estimatedOperatingCost}
          operatingWageCost={operatingWageCost}
          operatingMaterialCost={operatingMaterialCost}
          operatingCostCategory={operatingCostCategory}
          simulationDays={simulationDays}
          isCostLoading={isCostLoading}
        />
      );
    // 今後: case TYPE_BUILDING(フロア内訳), インフラ生産施設(効果範囲) などを追加する。
    default:
      return null;
  }
}

/* ---- TYPE_STANDARD(一般施設)の施設情報 ---- */

type StandardFacilityInfoProps = {
  estimatedMaxProduction: number | null;
  estimatedEmployment: number | null;
  estimatedOperatingCost: number | null;
  operatingWageCost: number | null;
  operatingMaterialCost: number | null;
  operatingCostCategory: number | null;
  simulationDays: number | null;
  isCostLoading: boolean;
};

/**
 * 一般施設(TYPE_STANDARD)の施設情報。
 *
 * 最大稼働時の推定生産量・雇用人数に加え、稼働後に毎エポック発生するランニングコスト
 * (人件費+原料費)を内訳つきで表示する。値はバックエンドの construction-cost エンドポイント
 * が新規スポーン判定と同じ calc_rough_cost で算出したもので、専用建物のため家賃は含まない。
 * 支出元の予算カテゴリ(LEDGER_*)が定義されていれば、費目アイコン+ラベルも表示し、
 * 「どの予算から毎エポック支出されるか」をプレイヤーが建設前に把握できるようにする。
 */
function StandardFacilityInfo({
  estimatedMaxProduction,
  estimatedEmployment,
  estimatedOperatingCost,
  operatingWageCost,
  operatingMaterialCost,
  operatingCostCategory,
  simulationDays,
  isCostLoading,
}: StandardFacilityInfoProps) {
  const { t } = useTranslation();

  /** ローディング中は共通文言、null は "—"、それ以外は Money 表示。 */
  const renderMoney = (value: number | null) =>
    isCostLoading ? (
      t("common.loading")
    ) : value != null ? (
      <Money value={value} unit />
    ) : (
      "—"
    );

  const ledgerIcon =
    operatingCostCategory != null ? resolveLedgerIcon(operatingCostCategory) : "";

  return (
    <div className="bc-section">
      <div className="bc-section-title">{t("building_construction.facilityInfo")}</div>
      <div className="bc-info-grid">
        <span className="bc-info-label">{t("building_construction.estimatedMaxProduction")}</span>
        <span className="bc-info-value">
          {isCostLoading
            ? t("common.loading")
            : estimatedMaxProduction != null
            ? Math.round(estimatedMaxProduction).toLocaleString()
            : "—"}
        </span>

        <span className="bc-info-label">{t("building_construction.estimatedEmployment")}</span>
        <span className="bc-info-value">
          {isCostLoading
            ? t("common.loading")
            : estimatedEmployment != null
            ? t("building_construction.unitPeopleValue", { value: Math.round(estimatedEmployment) })
            : "—"}
        </span>

        <span className="bc-info-label">{t("building_construction.estimatedRunningCost")}</span>
        <span className="bc-info-value">{renderMoney(estimatedOperatingCost)}</span>

        {/* 内訳(人件費・原料費)。取得済みのときのみ表示する。 */}
        {!isCostLoading && operatingWageCost != null && (
          <>
            <span className="bc-info-label bc-info-label--sub">
              {t("building_construction.runningCostWage")}
            </span>
            <span className="bc-info-value bc-info-value--sub">
              <Money value={operatingWageCost} unit />
            </span>
          </>
        )}
        {!isCostLoading && operatingMaterialCost != null && (
          <>
            <span className="bc-info-label bc-info-label--sub">
              {t("building_construction.runningCostMaterial")}
            </span>
            <span className="bc-info-value bc-info-value--sub">
              <Money value={operatingMaterialCost} unit />
            </span>
          </>
        )}

        {/* 支出元の予算カテゴリ(公共施設として建てた場合の運営費の費目)。 */}
        {!isCostLoading && operatingCostCategory != null && (
          <>
            <span className="bc-info-label">{t("building_construction.runningCostBudget")}</span>
            <span className="bc-info-value bc-ledger-value">
              {ledgerIcon && <img src={ledgerIcon} alt="" className="bc-ledger-icon" />}
              {t(`budget.category.${operatingCostCategory}`)}
            </span>
          </>
        )}
      </div>

      {/* ランニングコストの前提の注記(1エポックあたり・フル稼働時)。 */}
      {!isCostLoading && estimatedOperatingCost != null && (
        <div className="bc-muted bc-running-cost-note">
          {simulationDays != null
            ? t("building_construction.runningCostNote", { days: Math.round(simulationDays) })
            : t("building_construction.runningCostNoteNoDays")}
        </div>
      )}
    </div>
  );
}
