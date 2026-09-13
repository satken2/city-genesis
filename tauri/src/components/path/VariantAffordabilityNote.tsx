/**
 * バリアント選択プルダウンの下に出す資金目安の注記(道路・軌道・建物で共用)。
 *
 * - 利用可能な資金を1行で示す(神モードなど unlimited のときは出さない)。
 * - 選択不可(灰色)のバリアントが1つでもあれば、その理由(道路=平地{{length}}mの建設にも
 *   足りない / 建物=最小規模の建設にも足りない)を添える。
 * affordability が null(未取得・取得失敗)のときは何も出さない。
 */

import { useTranslation } from "react-i18next";
import type { VariantAffordability } from "../../hooks/useVariantAffordability";
import "./VariantAffordabilityNote.css";

type Props = {
  affordability: VariantAffordability | null;
  anyUnaffordable: boolean;
  kind: "path" | "building";
};

export function VariantAffordabilityNote({ affordability, anyUnaffordable, kind }: Props) {
  const { t } = useTranslation();
  if (affordability == null || affordability.unlimited || affordability.availableFunds == null) {
    return null;
  }
  return (
    <div className="pc-affordability-note">
      <div>
        {t("construction.availableFunds")}:{" "}
        {t("common.currency", { value: Math.round(affordability.availableFunds) })}
      </div>
      {anyUnaffordable && (
        <div className="pc-affordability-warning">
          {kind === "path"
            ? t("construction.pathVariantUnaffordableNote", { length: affordability.referenceLength })
            : t("construction.buildingVariantUnaffordableNote")}
        </div>
      )}
    </div>
  );
}
