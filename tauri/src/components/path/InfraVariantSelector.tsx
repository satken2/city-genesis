/**
 * インフラカテゴリのバリアント選択UI。
 * 「電気(PATH_ELECTRICITY)」「水道(PATH_WATER)」をボタンで選び、
 * その下のプルダウンでバリアントを選ぶ。
 *
 * 注: path_type 定数(電気=11/水道=12)は最近分割されたが master.yaml の path_types が
 * 未更新のため、ボタンのラベルはここで明示的に与えてフォールバックする。
 */

import { useTranslation } from "react-i18next";
import type { MasterData, PathMasterEntry } from "../../types";
import { PATH_ELECTRICITY, PATH_WATER } from "../../constants";
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
    { type: PATH_ELECTRICITY, label: t("path_construction.infra.electricity", "電気") },
    { type: PATH_WATER, label: t("path_construction.infra.water", "水道") },
  ];
  return <TypeVariantSelector options={infraTypes} {...props} />;
}
