/**
 * 「path_type をタブで選び、その中身のプルダウンでバリアントを選ぶ」共通の選択UI。
 *
 * 道路(自動車専用道路/街路/歩道)とインフラ(電気/水道)はどちらもこの形なので、
 * RoadVariantSelector / InfraVariantSelector が本コンポーネントを種別リストだけ変えて包む。
 *
 * 種別を切り替えると下に続く内容(バリアント・説明・形状設定・費用)がすべて入れ替わるため、
 * 見た目・支援技術の双方でタブとして扱う(tablist/tab/tabpanel)。タブバーの意匠は
 * 軌道建設パネルのタブと共通の pc-tabbar / pc-tab(PathConstructionPanel.css)。
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
    <>
      {/* 種別タブ */}
      <div className="pc-tabbar" role="tablist">
        {options.map((opt) => {
          const label =
            opt.label ?? masterData.path_types[opt.type]?.display_name ?? `Type ${opt.type}`;
          return (
            <button
              key={opt.type}
              type="button"
              role="tab"
              aria-selected={selectedType === opt.type}
              className={`pc-tab ${selectedType === opt.type ? "active" : ""}`}
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

      {/* 選択中タブの中身(バリアントプルダウン) */}
      <div className="pc-section" role="tabpanel">
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
    </>
  );
}
