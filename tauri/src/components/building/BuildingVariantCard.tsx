/**
 * 建物バリアントカード
 *
 * 建物建設パネルでバリアント情報を表示するための共通カード。
 * アイコン、名称、説明文、フロア仕様(許容階数・フロア種別アイコン)を一体で表示する。
 *
 * 用途:
 * 1. カテゴリ選択後のバリアント一覧(onClick あり): クリック可能なボタンとして描画する
 * 2. 建物選択後の詳細設定画面(onClick なし): 概要を表示する静的な情報カードとして流用する
 */

import type { AgentMasterEntry } from "../../types";
import { resolveAgentIcon } from "../../utils/agentIcon";
import { BuildingFloorSpecs } from "./BuildingFloorSpecs";

export type BuildingVariantCardProps = {
  variantId: number;
  entry: AgentMasterEntry;
  /** 階数が外から固定されている場合の階数(観光名所など) */
  fixedFloors?: number | null;
  /** 資金不足等で選択不可の場合に true */
  disabled?: boolean;
  /** 名称の後ろに付与する補足文字列(「 (資金不足)」など) */
  titleSuffix?: string;
  /** クリック時のハンドラ。指定時はインタラクティブなボタンとして描画される */
  onClick?: () => void;
};

export function BuildingVariantCard({
  variantId,
  entry,
  fixedFloors,
  disabled = false,
  titleSuffix,
  onClick,
}: BuildingVariantCardProps) {
  const icon = resolveAgentIcon(
    variantId,
    entry.type,
    entry.building ?? false,
    entry.bd_type ?? 0
  );

  const cardBody = (
    <>
      <img
        src={icon}
        alt={entry.display_name}
        className="bc-description-icon"
        draggable={false}
      />
      <div className="bc-description-body">
        <div className="bc-description-name">
          {entry.display_name}
          {titleSuffix}
        </div>
        <div className="bc-description-text">{entry.description || "—"}</div>
        <BuildingFloorSpecs entry={entry} fixedFloors={fixedFloors} />
      </div>
    </>
  );

  if (onClick) {
    return (
      <button
        type="button"
        className="bc-description-frame bc-variant-card-btn"
        disabled={disabled}
        onClick={onClick}
      >
        {cardBody}
      </button>
    );
  }

  return <div className="bc-description-frame">{cardBody}</div>;
}
