/**
 * 選択中パスバリアントの説明カード(アイコン+名称+説明文)。
 *
 * 表示位置がパネルごとに違う(道路建設=バリアント選択の直下、軌道建設=規格プルダウンと
 * 線路構成の設定の間)ため、共通ボディ(PathBuildSections)には含めず独立したセクションにしている。
 * 未選択のときは何も描画しない(=区切り線も出ない)。
 */

import type { PathMasterEntry } from "../../types";
import { resolvePathIcon } from "../../utils/pathIcon";

type PathVariantDescriptionProps = {
  entry: PathMasterEntry | null;
};

export function PathVariantDescription({ entry }: PathVariantDescriptionProps) {
  if (!entry) return null;

  return (
    <div className="pc-section">
      <div className="pc-description-frame">
        <img
          src={resolvePathIcon(entry.type)}
          alt={entry.display_name}
          className="pc-description-icon"
          draggable={false}
        />
        <div className="pc-description-body">
          <div className="pc-description-name">{entry.display_name}</div>
          <div className="pc-description-text">{entry.description || "—"}</div>
        </div>
      </div>
    </div>
  );
}
