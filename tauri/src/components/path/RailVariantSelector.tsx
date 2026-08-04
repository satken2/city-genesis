/**
 * 軌道カテゴリのバリアント選択プルダウン。
 *
 * 構造(通常/橋梁/駅)・電化/非電化・線路規格(地上/高架/地下、軌間・設計速度など)の違いは
 * すべてこのプルダウン1本に集約し、プレイヤーに敷設するバリアントを直接選ばせる。
 * 候補の絞り込み(鉄道種別・タブによる構造の限定)は呼び出し側が行い、ここには
 * 絞り込み済みの候補一覧だけが渡る。選択肢の表示はマスターの display_name そのまま。
 *
 * 選んだバリアントの説明は PathVariantDescription、線路数などの複線構成は
 * RailTrackLayoutSettings が別セクションとして描画する。
 */

import { useTranslation } from "react-i18next";
import type { PathMasterEntry } from "../../types";

type RailVariantSelectorProps = {
  /** 敷設できる規格(単線バリアント)の候補一覧。呼び出し側で絞り込み済み。 */
  variantOptions: { id: number; data: PathMasterEntry }[];
  /** プルダウンで選択中のバリアントID(未解決なら null) */
  selectedVariant: number | null;
  onSelectVariant: (variantId: number) => void;
  disabled?: boolean;
};

export function RailVariantSelector({
  variantOptions,
  selectedVariant,
  onSelectVariant,
  disabled = false,
}: RailVariantSelectorProps) {
  const { t } = useTranslation();

  return (
    <div className="pc-section">
      {variantOptions.length === 0 ? (
        <div className="pc-empty-variants">
          {t("path_construction.rail.noVariantForCombo", "この組み合わせに対応する線路がありません")}
        </div>
      ) : (
        <select
          className="pc-select"
          value={selectedVariant ?? ""}
          disabled={disabled}
          onChange={(e) => {
            const val = e.target.value;
            if (val) {
              const variantId = parseInt(val);
              if (variantId !== selectedVariant) onSelectVariant(variantId);
            }
          }}
        >
          {variantOptions.map((v) => (
            <option key={v.id} value={v.id}>
              {v.data.display_name}
            </option>
          ))}
        </select>
      )}
    </div>
  );
}
