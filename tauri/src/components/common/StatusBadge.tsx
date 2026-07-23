/**
 * ステータスバッジ（共通部品）
 *
 * 経済主体・建物などの status(数値) を、色付きバッジとして表示する。
 * 背景色・文字色は local-constants の STATUS_STYLE_MAP で定義され、
 * ラベルは i18n の `status.<番号>` キーで解決する。
 *
 * 検索ウィンドウ・統計ウィンドウなど、複数の一覧UIで同一デザインを共有するため
 * common に配置している。
 */

import { useTranslation } from "react-i18next";
import { STATUS_STYLE_MAP } from "../../local-constants";
import "./StatusBadge.css";

type StatusBadgeProps = {
  status: number;
};

export function StatusBadge({ status }: StatusBadgeProps) {
  const { t } = useTranslation();
  const style = STATUS_STYLE_MAP[status] ?? { bg: "#6b7280", text: "#ffffff" };
  const label = t(`status.${status}`, { defaultValue: String(status) });
  return (
    <span
      className="status-badge"
      style={{ backgroundColor: style.bg, color: style.text }}
    >
      {label}
    </span>
  );
}
