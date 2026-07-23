import { useTranslation } from "react-i18next";
import { useMasterData } from "../../contexts/MasterDataContext";
import { resolveResourceIcon } from "../../utils/resourceIcon";
import { Meter } from "./Meter";
import "./ResourceCard.css";

export type ResourceCardProps = {
  resourceId: number;
  productId: number;
  maxAmount: number;
  resourceAmount: number;
  onClick?: (resourceId: number) => void;
};

/**
  * 天然資源カード共通コンポーネント
  * 資源アイコン・資源名・現在残量・残量率メーターを表示する。
  * クリック可能（onClickが渡されている）な場合はボタンとして振る舞う。
  */
export function ResourceCard({
  resourceId,
  productId,
  maxAmount,
  resourceAmount,
  onClick,
}: ResourceCardProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const productMaster = masterData?.products[productId];
  const displayName = productMaster?.display_name ?? t("resource.unknownResource");
  const unit = productMaster?.unit ?? "";
  const iconSrc = resolveResourceIcon(productId);
  const ratioPct =
    maxAmount > 0 ? Math.round((resourceAmount / maxAmount) * 100) : 0;

  const cardContent = (
    <>
      {iconSrc && (
        <img
          src={iconSrc}
          alt=""
          className="resource-card__icon"
          draggable={false}
        />
      )}
      <div className="resource-card__body">
        <div className="resource-card__name">{displayName}</div>
        <div className="resource-card__amount">
          {Math.round(resourceAmount).toLocaleString()} / {maxAmount.toLocaleString()} {unit}
        </div>
        <Meter value={ratioPct / 100} />
      </div>
      <div className="resource-card__pct">{ratioPct}%</div>
    </>
  );

  if (onClick) {
    return (
      <button
        className="resource-card resource-card--clickable"
        type="button"
        onClick={() => onClick(resourceId)}
      >
        {cardContent}
      </button>
    );
  }

  return (
    <div className="resource-card">
      {cardContent}
    </div>
  );
}
