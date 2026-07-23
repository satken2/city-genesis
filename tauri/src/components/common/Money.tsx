/**
 * 金額表示コンポーネント
 *
 * 言語設定に応じて金額をコンパクト化して表示する（日本語: 52.5億 / 英語: 5.25B）。
 * マウスオーバー時には title 属性で正確な金額を表示する。
 * EpochDate と同型の構成（i18n からロケール導出 → utils の純粋関数でフォーマット）。
 */

import { useTranslation } from "react-i18next";
import { formatCompactAmount, formatExactAmount } from "../../utils/money";
import "./Money.css";

type MoneyProps = {
  /** 表示する金額（数値） */
  value: number;
  /** 通貨単位（¢）を表示するか。既定 false */
  unit?: boolean;
  /** 追加クラス（既存レイアウトへの差し込み用、任意） */
  className?: string;
};

export function Money({ value, unit = false, className }: MoneyProps) {
  const { i18n } = useTranslation();
  const locale = i18n.language === "ja" ? "ja-JP" : "en-US";
  return (
    <span
      className={className ? `money ${className}` : "money"}
      title={formatExactAmount(value, locale, unit)}
    >
      {formatCompactAmount(value, locale, unit)}
    </span>
  );
}
