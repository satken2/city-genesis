/**
 * 建設可能建物種別マスク入力コンポーネント
 *
 * allowed_bd_mask の値をビットフラグとして扱い、
 * 建物種別ごとのアイコン付きトグルボタンで入力する。
 * 各ボタンは対応するビットのオン・オフを切り替える。
 *
 * disabled=true の場合は全ボタンが非活性になり、グレーアウトして表示する。
 */

import {
  BUILDING_APARTMENT,
  BUILDING_HOUSE,
  BUILDING_RETAIL,
  BUILDING_FACTORY,
  BUILDING_OFFICE,
  BUILDING_FARM,
  BUILDING_MINE,
  BUILDING_PORT,
  BUILDING_OTHER,
  BUILDING_PUBLIC,
} from "../../constants";
import "./AllowedBdMaskInput.css";

const buildingModules = import.meta.glob<{ default: string }>(
  "../../assets/images/buildings/*.png",
  { eager: true }
);

function getBuildingIcon(bdMask: number): string {
  const key = `../../assets/images/buildings/bd_${bdMask}.png`;
  return buildingModules[key]?.default ?? "";
}

type BuildingTypeDef = {
  mask: number;
  labelKey: string;
};

const BUILDING_TYPES: BuildingTypeDef[] = [
  { mask: BUILDING_APARTMENT, labelKey: "parcel.bdApartment" },
  { mask: BUILDING_HOUSE,     labelKey: "parcel.bdHouse"     },
  { mask: BUILDING_RETAIL,    labelKey: "parcel.bdRetail"    },
  { mask: BUILDING_FACTORY,   labelKey: "parcel.bdFactory"   },
  { mask: BUILDING_OFFICE,    labelKey: "parcel.bdOffice"    },
  { mask: BUILDING_FARM,      labelKey: "parcel.bdFarm"      },
  { mask: BUILDING_MINE,      labelKey: "parcel.bdMine"      },
  { mask: BUILDING_PORT,      labelKey: "parcel.bdPort"      },
  { mask: BUILDING_OTHER,     labelKey: "parcel.bdOther"     },
  { mask: BUILDING_PUBLIC,    labelKey: "parcel.bdPublic"    },
];

type AllowedBdMaskInputProps = {
  /** 建設可能建物種別のビットマスク値 */
  value: number;
  /** ビットマスク変更時のコールバック */
  onChange: (value: number) => void;
  /** true の場合は全ボタンを非活性にしてグレーアウト表示する */
  disabled: boolean;
  /** 各ボタンのラベルを解決する関数(i18n用) */
  getLabel: (key: string) => string;
};

export function AllowedBdMaskInput({
  value,
  onChange,
  disabled,
  getLabel,
}: AllowedBdMaskInputProps) {
  function toggleBit(mask: number) {
    if (disabled) return;
    onChange(value ^ mask);
  }

  return (
    <div className={`bd-mask-grid${disabled ? " bd-mask-grid--disabled" : ""}`}>
      {BUILDING_TYPES.map(({ mask, labelKey }) => {
        const active = (value & mask) !== 0;
        return (
          <button
            key={mask}
            type="button"
            className={`bd-mask-btn${active ? " bd-mask-btn--active" : ""}`}
            onClick={() => toggleBit(mask)}
            disabled={disabled}
            title={getLabel(labelKey)}
            aria-pressed={active}
          >
            <img
              src={getBuildingIcon(mask)}
              alt={getLabel(labelKey)}
              className="bd-mask-btn-icon"
              draggable={false}
            />
          </button>
        );
      })}
    </div>
  );
}
