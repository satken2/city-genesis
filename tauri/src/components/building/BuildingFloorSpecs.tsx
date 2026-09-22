/**
 * 建物バリアントのフロア仕様(フロア種別アイコン + 建設できる階数)。
 *
 * 建設パネルの説明カードの説明文の下に1行で並べる。
 *
 * フロア種別は文字なしのアイコンだけで示す(種別名はツールチップ)。出典はマスターの
 * space_limits のキーで、2種類ある:
 *   - 汎用フロア(SPACE_RESIDENCE などのビット値): 表示パネルの「フロア色分け」で選べる
 *     6分類と同じ並び・同じ色で描き(住居=緑・小売=青・工場=黄・オフィス=紫・農地=茶・その他=灰)、
 *     ホテルの宿泊フロア(SPACE_LODGING)は桃色のベッドで足す。
 *   - 専用フロア(キー=経済主体の variant ID): 特定の業種しか入居できないフロア。
 *     錠前アイコンにして、ツールチップに入居できる業種名を出す。
 *
 * 階数はアイコンの右に文字だけで出す(1F〜10F形式。min_floors=max_floors の固定階数は1つだけ)。
 * ここに出すのはバリアント自身の許容階数で、構造種別(木造は3階まで等)による上限は含めない
 * (構造は説明カードの下で選ぶ独立した軸なので、バリアントの仕様としては分けて示す)。
 * ただし観光名所のように階数そのものが外から固定される場合(fixedFloors)は、
 * 階数ステッパーも固定値のまま操作できないため、その1つだけを出す。
 */

import { useTranslation } from "react-i18next";
import { useMasterData } from "../../contexts/MasterDataContext";
import type { AgentMasterEntry } from "../../types";
import {
  SPACE_OTHER,
  SPACE_RESIDENCE,
  SPACE_RETAIL,
  SPACE_FACTORY,
  SPACE_OFFICE,
  SPACE_FARM,
  SPACE_LODGING,
} from "../../constants";

type BuildingFloorSpecsProps = {
  entry: AgentMasterEntry;
  /** 階数が外から固定されている場合の階数(観光名所は名所ごとに論理階数が決まる)。 */
  fixedFloors?: number | null;
};

/** 24px正方の塗りつぶしアイコン。色は文字色(currentColor)で与える。 */
const ICON_PROPS = {
  className: "bc-floor-icon",
  viewBox: "0 0 24 24",
  fill: "currentColor",
  "aria-hidden": true,
};

/** 汎用フロア分類の定義。並び順・色は表示パネルの凡例(DisplayOptionsPanel)と揃える。 */
const GENERIC_FLOORS: { spaceType: number; color: string; tooltipKey: string; icon: React.ReactNode }[] = [
  {
    spaceType: SPACE_RESIDENCE,
    color: "#4caf50",
    tooltipKey: "display.subSpaceResidence",
    icon: (
      <svg {...ICON_PROPS}>
        <path d="M12 3 L2 12 H5 V21 H10 V15 H14 V21 H19 V12 H22 Z" />
      </svg>
    ),
  },
  {
    spaceType: SPACE_RETAIL,
    color: "#2196f3",
    tooltipKey: "display.subSpaceRetail",
    icon: (
      <svg {...ICON_PROPS}>
        <path d="M3 4 H21 L22 9 H2 Z" />
        <path d="M4 10 H20 V21 H14 V15 H10 V21 H4 Z" />
      </svg>
    ),
  },
  {
    spaceType: SPACE_FACTORY,
    color: "#fbc02d",
    tooltipKey: "display.subSpaceFactory",
    icon: (
      <svg {...ICON_PROPS}>
        <path d="M2 21 V10 L8 13.5 V10 L14 13.5 V10 L20 13.5 V3 H23 V21 Z" />
      </svg>
    ),
  },
  {
    spaceType: SPACE_OFFICE,
    color: "#9c27b0",
    tooltipKey: "display.subSpaceOffice",
    icon: (
      <svg {...ICON_PROPS}>
        <path
          fillRule="evenodd"
          d="M5 2 H19 V22 H5 Z M8 5 H11 V8 H8 Z M13 5 H16 V8 H13 Z M8 10 H11 V13 H8 Z M13 10 H16 V13 H13 Z M8 15 H11 V18 H8 Z M13 15 H16 V18 H13 Z"
        />
      </svg>
    ),
  },
  {
    spaceType: SPACE_FARM,
    color: "#795548",
    tooltipKey: "display.subSpaceFarm",
    icon: (
      <svg {...ICON_PROPS}>
        <path d="M11 22 V12 H13 V22 Z" />
        <path d="M12 12 C12 6 8 3 2 3 C2 9 6 12 12 12 Z" />
        <path d="M12 12 C12 6 16 3 22 3 C22 9 18 12 12 12 Z" />
      </svg>
    ),
  },
  {
    spaceType: SPACE_LODGING,
    color: "#e91e63",
    tooltipKey: "display.subSpaceLodging",
    icon: (
      <svg {...ICON_PROPS}>
        <path d="M2 18 V7 H4 V13 H22 V18 H20 V16 H4 V18 Z" />
        <path d="M5 8 H10 V12 H5 Z" />
        <path d="M11 9 H21 A1 1 0 0 1 22 10 V12 H11 Z" />
      </svg>
    ),
  },
  {
    spaceType: SPACE_OTHER,
    color: "#9e9e9e",
    tooltipKey: "display.subSpaceOther",
    icon: (
      <svg {...ICON_PROPS}>
        <path fillRule="evenodd" d="M3 5 H21 V19 H3 Z M6 8 V16 H18 V8 Z" />
      </svg>
    ),
  },
];

/** 専用フロアの錠前アイコン(色は「その他」と同じ灰色)。 */
const ICON_DEDICATED = (
  <svg {...ICON_PROPS}>
    <path fillRule="evenodd" d="M7 10 V7 A5 5 0 0 1 17 7 V10 H19 V22 H5 V10 Z M9 10 H15 V7 A3 3 0 0 0 9 7 Z" />
  </svg>
);

export function BuildingFloorSpecs({ entry, fixedFloors }: BuildingFloorSpecsProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const spaceTypes = Object.keys(entry.space_limits ?? {}).map(Number);
  const generic = GENERIC_FLOORS.filter((floor) => spaceTypes.includes(floor.spaceType));
  const dedicated = spaceTypes.filter((st) => !GENERIC_FLOORS.some((floor) => floor.spaceType === st));

  const minFloors = entry.min_floors;
  const maxFloors = entry.max_floors;
  const floorRange =
    fixedFloors != null
      ? t("building_construction.floorRangeFixed", { value: fixedFloors })
      : minFloors == null || maxFloors == null
        ? null
        : minFloors === maxFloors
          ? t("building_construction.floorRangeFixed", { value: maxFloors })
          : t("building_construction.floorRange", { min: minFloors, max: maxFloors });

  if (spaceTypes.length === 0 && floorRange === null) return null;

  return (
    <div className="bc-description-floors">
      {generic.map((floor) => (
        <span key={floor.spaceType} className="bc-floor" style={{ color: floor.color }} title={t(floor.tooltipKey)}>
          {floor.icon}
        </span>
      ))}
      {dedicated.map((st) => (
        <span
          key={st}
          className="bc-floor"
          style={{ color: "#9e9e9e" }}
          title={t("building_construction.floorDedicated", {
            name: masterData?.agents[st]?.display_name ?? `#${st}`,
          })}
        >
          {ICON_DEDICATED}
        </span>
      ))}
      {floorRange !== null && (
        <span className="bc-floor-range" title={t("building_construction.floorRangeTooltip")}>
          {floorRange}
        </span>
      )}
    </div>
  );
}
