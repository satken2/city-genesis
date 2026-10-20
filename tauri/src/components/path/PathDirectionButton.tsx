import { useTranslation } from "react-i18next";
import { DIR_FORWARD, DIR_BACKWARD } from "../../constants";
import upArrowIcon from "../../assets/images/ui/up-arrow.png";
import downArrowIcon from "../../assets/images/ui/down-arrow.png";
import bothArrowIcon from "../../assets/images/ui/both-arrow.png";
import "./PathDirectionButton.css";

type PathDirectionButtonProps = {
  direction: number;
  onClick: () => void;
  disabled?: boolean;
};

/** Three-state toggle showing current traffic direction, shared by construction settings and path details. */
export function PathDirectionButton({ direction, onClick, disabled = false }: PathDirectionButtonProps) {
  const { t } = useTranslation();
  const icon = direction === DIR_FORWARD ? upArrowIcon : direction === DIR_BACKWARD ? downArrowIcon : bothArrowIcon;
  const title = direction === DIR_FORWARD
    ? t("path_construction.rail.dirForward")
    : direction === DIR_BACKWARD
      ? t("path_construction.rail.dirBackward")
      : t("path_construction.rail.dirBoth");

  return (
    <button type="button" className="pc-track-arrow" disabled={disabled}
      title={title} aria-label={title} onClick={onClick}>
      <img src={icon} alt="" className="pc-track-arrow-img" draggable={false} />
    </button>
  );
}
