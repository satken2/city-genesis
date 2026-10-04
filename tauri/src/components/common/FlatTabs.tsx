import type { ReactNode } from "react";
import "./FlatTabs.css";

export type FlatTab<T extends string> = {
  id: T;
  label: string;
  disabled?: boolean;
  icon?: ReactNode;
  /** タブ内の選択肢と共通の操作名・値。許可された選択肢へ移るタブも操作できる。 */
  guide?: { name: string; values: (string | number)[] };
  /** ラベルの右に添える札(未提供のタブの「Coming soon」など) */
  badge?: string;
};

/**
 * フラット意匠のタブ列。アイコン専用表示は呼び出し側で寸法や色を上書きできる。
 * 選択状態は呼び出し側が持ち、中身の出し分けも呼び出し側が行う。
 */
export function FlatTabs<T extends string>({ tabs, active, onChange, label, iconOnly = false, idPrefix }: {
  tabs: FlatTab<T>[];
  active: T;
  onChange: (id: T) => void;
  /** 読み上げ用のタブ列の名前 */
  label?: string;
  /** ラベルは読み上げとツールチップに残し、ボタンにはアイコンだけを表示する。 */
  iconOnly?: boolean;
  /** 対応するtabpanelのIDは `${idPrefix}-panel-${tab.id}` とする。 */
  idPrefix?: string;
}) {
  return (
    <div className="flat-tabs" role="tablist" aria-label={label}>
      {tabs.map(tab => (
        <button
          key={tab.id}
          type="button"
          role="tab"
          data-guide={tab.guide?.name}
          data-guide-values={tab.guide?.values.join(" ")}
          id={idPrefix ? `${idPrefix}-tab-${tab.id}` : undefined}
          aria-controls={idPrefix ? `${idPrefix}-panel-${tab.id}` : undefined}
          aria-label={iconOnly ? tab.label : undefined}
          title={iconOnly ? tab.label : undefined}
          className="flat-tab"
          aria-selected={tab.id === active}
          tabIndex={iconOnly ? (tab.id === active ? 0 : -1) : undefined}
          disabled={tab.disabled}
          onClick={() => { if (tab.id !== active) onChange(tab.id); }}
          onKeyDown={event => {
            if (!iconOnly || !["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
            event.preventDefault();
            const buttons = event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>("[role=tab]");
            const enabled = tabs.filter((item, index) => !item.disabled && !buttons?.[index]?.inert);
            if (enabled.length === 0) return;
            const current = enabled.findIndex(item => item.id === tab.id);
            const index = event.key === "Home" ? 0 : event.key === "End" ? enabled.length - 1
              : (current + (event.key === "ArrowRight" ? 1 : -1) + enabled.length) % enabled.length;
            const next = enabled[index];
            onChange(next.id);
            buttons?.[tabs.findIndex(item => item.id === next.id)]?.focus();
          }}
        >
          {tab.icon && <span aria-hidden="true">{tab.icon}</span>}
          {!iconOnly && <span>{tab.label}</span>}
          {tab.badge && <span className="flat-tab-badge">{tab.badge}</span>}
        </button>
      ))}
    </div>
  );
}
