import "./FlatTabs.css";

export type FlatTab<T extends string> = {
  id: T;
  label: string;
  disabled?: boolean;
  /** ラベルの右に添える札(未提供のタブの「Coming soon」など) */
  badge?: string;
};

/**
 * フラット意匠のウィンドウ上部に置くタブ列(ニューゲーム・シナリオ管理)。
 * 選択状態は呼び出し側が持ち、中身の出し分けも呼び出し側が行う。
 */
export function FlatTabs<T extends string>({ tabs, active, onChange, label }: {
  tabs: FlatTab<T>[];
  active: T;
  onChange: (id: T) => void;
  /** 読み上げ用のタブ列の名前 */
  label?: string;
}) {
  return (
    <div className="flat-tabs" role="tablist" aria-label={label}>
      {tabs.map(tab => (
        <button
          key={tab.id}
          type="button"
          role="tab"
          className="flat-tab"
          aria-selected={tab.id === active}
          disabled={tab.disabled}
          onClick={() => { if (tab.id !== active) onChange(tab.id); }}
        >
          <span>{tab.label}</span>
          {tab.badge && <span className="flat-tab-badge">{tab.badge}</span>}
        </button>
      ))}
    </div>
  );
}
