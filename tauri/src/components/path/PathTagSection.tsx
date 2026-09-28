/**
 * パス建設パネルの「建設プロジェクト」セクション
 *
 * これから敷くパスに付けるタグを選ぶ。選んだ値はパネルが持ち、確定時に作成リクエストの
 * 各パスへ `tag` として載せる(連続設置の間は同じタグを使い続ける)。
 */

import { useTranslation } from "react-i18next";
import { TagPicker } from "../common/TagPicker";
import "./PathConstructionPanel.css";

type PathTagSectionProps = {
  apiBaseUrl: string;
  value: string;
  onChange: (tag: string) => void;
  disabled?: boolean;
};

export function PathTagSection({ apiBaseUrl, value, onChange, disabled }: PathTagSectionProps) {
  const { t } = useTranslation();
  return (
    <div className="pc-section">
      <div className="pc-section-title">{t("tag.label")}</div>
      <TagPicker apiBaseUrl={apiBaseUrl} value={value} onChange={onChange} disabled={disabled} />
    </div>
  );
}
