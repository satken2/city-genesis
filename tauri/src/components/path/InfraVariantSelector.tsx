/**
 * インフラカテゴリのバリアント選択UI。
 * 「電気(PATH_ELECTRICITY)」「水道(PATH_WATER)」「石油(PATH_PIPELINE)」をタブで選び、
 * その中身のプルダウンでバリアントを選ぶ。
 *
 * タブのラベルは master.yaml の path_types(送電/導水/パイプライン)ではなく i18n で与える。
 * マスターの表示名は日本語のみで多言語化できないうえ、ここで示したいのは網の設備名ではなく
 * プレイヤーが運ぶ財のカテゴリだからである。
 */

import { useTranslation } from "react-i18next";
import type { MasterData, PathMasterEntry } from "../../types";
import { PATH_ELECTRICITY, PATH_WATER, PATH_PIPELINE } from "../../constants";
import { TypeVariantSelector, type TypeOption } from "./TypeVariantSelector";
import type { VariantAffordability } from "../../hooks/useVariantAffordability";

type InfraVariantSelectorProps = {
  masterData: MasterData;
  selectedType: number | null;
  selectedVariant: number | null;
  variantsForType: (type: number) => { id: number; data: PathMasterEntry }[];
  onSelectType: (type: number) => void;
  onSelectVariant: (variantId: number) => void;
  disabled?: boolean;
  affordability?: VariantAffordability | null;
};

export function InfraVariantSelector(props: InfraVariantSelectorProps) {
  const { t } = useTranslation();
  const infraTypes: TypeOption[] = [
    { type: PATH_ELECTRICITY, label: t("path_construction.infra.electricity") },
    { type: PATH_WATER, label: t("path_construction.infra.water") },
    { type: PATH_PIPELINE, label: t("path_construction.infra.oil") },
  ];
  return <TypeVariantSelector options={infraTypes} {...props} />;
}
