/**
 * 資金不足の札を重ねる枠
 *
 * 建設パネルのバリアント一覧で、今の資金では建てられない候補のカードの上に「資金不足」の札を重ねる。
 * カード(BuildingVariantCard / PathVariantCard)は一覧でも建設画面の概要でも同じものを使うので、
 * 一覧にしか要らない表示はカードに持たせず、この枠が外から重ねる。
 * 選べないこと自体はカードの disabled で表す(この枠はクリックを止めない)。
 */

import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import "./UnaffordableOverlay.css";

type UnaffordableOverlayProps = {
  /** true のときだけ札を重ねる */
  unaffordable: boolean;
  children: ReactNode;
};

export function UnaffordableOverlay({ unaffordable, children }: UnaffordableOverlayProps) {
  const { t } = useTranslation();
  return (
    <div className="unaffordable-overlay">
      {children}
      {unaffordable && (
        <div className="unaffordable-overlay__veil">
          <span className="unaffordable-overlay__label">{t("construction.variantUnaffordable")}</span>
        </div>
      )}
    </div>
  );
}
