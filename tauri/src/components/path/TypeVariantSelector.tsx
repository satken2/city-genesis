/**
 * 「path_type をボタンで選び、その下のプルダウンでバリアントを選ぶ」共通の選択UI。
 *
 * 道路(自動車専用道路/街路/歩道)とインフラ(電気/水道)はどちらもこの形なので、
 * RoadVariantSelector / InfraVariantSelector が本コンポーネントを種別リストだけ変えて包む。
 */

import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import type { MasterData, PathMasterEntry } from "../../types";
import type { VariantAffordability } from "../../hooks/useVariantAffordability";

export type TypeOption = {
  /** path_type の値 */
  type: number;
  /** ボタンに表示するラベル(未指定なら path_types マスターの display_name にフォールバック) */
  label?: string;
};

type TypeVariantSelectorProps = {
  masterData: MasterData;
  options: TypeOption[];
  selectedType: number | null;
  selectedVariant: number | null;
  variantsForType: (type: number) => { id: number; data: PathMasterEntry }[];
  onSelectType: (type: number) => void;
  onSelectVariant: (variantId: number) => void;
  /** 既存パス編集など、種別・バリアントを変更させない場合 true */
  disabled?: boolean;
  /** バリアント単位の資金目安。渡すと基準費用を賄えないバリアントを選択不可にする */
  affordability?: VariantAffordability | null;
};

export function TypeVariantSelector({
  masterData,
  options,
  selectedType,
  selectedVariant,
  variantsForType,
  onSelectType,
  onSelectVariant,
  disabled = false,
  affordability = null,
}: TypeVariantSelectorProps) {
  const { t } = useTranslation();
  const isUnaffordable = (variantId: number) =>
    affordability != null && !affordability.unlimited && affordability.variants[variantId]?.affordable === false;

  const variants = useMemo(
    () => (selectedType !== null ? variantsForType(selectedType) : []),
    [selectedType, variantsForType]
  );

  return (
    <div className="pc-section">
      {/* 種別ボタン */}
      <div className="pc-type-group">
        {options.map((opt) => {
          const label =
            opt.label ?? masterData.path_types[opt.type]?.display_name ?? `Type ${opt.type}`;
          return (
            <button
              key={opt.type}
              type="button"
              className={`pc-type-btn ${selectedType === opt.type ? "active" : ""}`}
              disabled={disabled}
              onClick={() => {
                if (selectedType !== opt.type) onSelectType(opt.type);
              }}
            >
              {label}
            </button>
          );
        })}
      </div>

      {/* バリアントプルダウン */}
      {selectedType === null ? (
        <div className="pc-empty-variants">{t("path_construction.selectTypeFirst")}</div>
      ) : variants.length === 0 ? (
        <div className="pc-empty-variants">{t("path_construction.noVariants")}</div>
      ) : (
        <select
          className="pc-select"
          value={selectedVariant ?? ""}
          disabled={disabled}
          onChange={(e) => {
            const val = e.target.value;
            if (val) {
              const variantId = parseInt(val);
              if (selectedVariant !== variantId) onSelectVariant(variantId);
            }
          }}
          style={{ marginTop: 10 }}
        >
          <option value="">{t("path_construction.selectTypeFirst")}</option>
          {variants.map((v) => (
            <option key={v.id} value={v.id} disabled={isUnaffordable(v.id)}>
              {v.data.display_name}
              {isUnaffordable(v.id) ? t("construction.variantUnaffordableSuffix") : ""}
            </option>
          ))}
        </select>
      )}
    </div>
  );
}
