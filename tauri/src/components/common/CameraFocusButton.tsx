/**
 * 3Dビューのカメラを対象へ動かすボタン。詳細パネルのヘッダー右上に置く。
 * アイコンは自前のSVG(外部素材を取り込まないため、クレジット表記の対象にならない)。
 * 市民パネルのカメラのアイコンは「追いかけ続ける」意味なので、1回だけ動かすこのボタンは照準で区別する。
 */

import { useTranslation } from "react-i18next";
import "./CameraFocusButton.css";

export function CameraFocusButton({ onClick }: { onClick: () => void }) {
  const { t } = useTranslation();
  return (
    <button
      className="camera-focus-btn"
      type="button"
      title={t("common.focusCamera")}
      aria-label={t("common.focusCamera")}
      onClick={onClick}
    >
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" aria-hidden="true">
        <circle cx="12" cy="12" r="6.5" />
        <circle cx="12" cy="12" r="1.5" fill="currentColor" stroke="none" />
        <path d="M12 2.5v4M12 17.5v4M2.5 12h4M17.5 12h4" />
      </svg>
    </button>
  );
}
