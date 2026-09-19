/**
 * 道路カテゴリのバリアント選択UI。
 * 「街路(PATH_STREET)」「高速道路(PATH_MOTORWAY)」「歩道(PATH_WALKWAY)」を
 * タブで選び、その中身のプルダウンでバリアントを選ぶ。
 *
 * タブの並びは、プレイヤーが最も頻繁に敷く街路を先頭に置いた順にしている。
 */

import type { MasterData, PathMasterEntry } from "../../types";
import { PATH_STREET, PATH_MOTORWAY, PATH_WALKWAY } from "../../constants";
import { TypeVariantSelector, type TypeOption } from "./TypeVariantSelector";
import type { VariantAffordability } from "../../hooks/useVariantAffordability";

const ROAD_TYPES: TypeOption[] = [
  { type: PATH_STREET },
  { type: PATH_MOTORWAY },
  { type: PATH_WALKWAY },
];

type RoadVariantSelectorProps = {
  masterData: MasterData;
  selectedType: number | null;
  selectedVariant: number | null;
  variantsForType: (type: number) => { id: number; data: PathMasterEntry }[];
  onSelectType: (type: number) => void;
  onSelectVariant: (variantId: number) => void;
  disabled?: boolean;
  affordability?: VariantAffordability | null;
};

export function RoadVariantSelector(props: RoadVariantSelectorProps) {
  return <TypeVariantSelector options={ROAD_TYPES} {...props} />;
}
