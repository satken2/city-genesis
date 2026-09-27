import type { ReactNode } from "react";
import "./SettingsForm.css";

/**
 * 設定1項目分の行
 * 左に項目名と補足文、右に操作UIを置く。
 * stretch を立てると操作UIが横幅いっぱいに伸びる(音量スライダー用)。
 * disabled は表示用。キーボード操作も防ぐため、子の入力・ボタンにも disabled を指定する。
 */
export function SettingsRow({
  label,
  description,
  badge,
  stretch = false,
  disabled = false,
  children,
}: {
  label: string;
  description?: ReactNode;
  badge?: string;
  stretch?: boolean;
  disabled?: boolean;
  children: ReactNode;
}) {
  const modifiers = [
    stretch ? "settings-row--stretch" : "",
    disabled ? "settings-row--disabled" : "",
  ]
    .filter(Boolean)
    .join(" ");
  return (
    <div className={`settings-row ${modifiers}`}>
      <div className="settings-row-label">
        <span className="settings-row-name">
          {label}
          {badge != null && <span className="settings-row-badge">{badge}</span>}
        </span>
        {description != null && <span className="settings-row-desc">{description}</span>}
      </div>
      <div className="settings-row-control">{children}</div>
    </div>
  );
}

