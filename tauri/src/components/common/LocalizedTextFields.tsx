/**
 * 多言語テキストの入力欄(シナリオの紹介情報・クエストの名前と説明)。
 *
 * 言語のタブを切り替えて、同じ言語の全項目をまとめて書く。表示する言語の文が無いと英語を出すので、
 * 英語のタブを先頭に置いて「必須」を示す。文が入っている言語のタブには印を付ける。最初に開くタブは表示言語。
 * タブで切り替わる項目はタブ列に接した枠(tabpanel)で囲み、枠の外に並ぶ言語共通の項目と見分けられるようにする。
 */
import { useState } from "react";
import { useTranslation } from "react-i18next";
import {
  LOCALIZED_TEXT_FALLBACK, SUPPORTED_LANGUAGES, resolveSupportedLanguage, type LocalizedText, type SupportedLanguage,
} from "../../utils/locale";
import "../achievement/ScenarioInfoSection.css";
import "./LocalizedTextFields.css";

export type LocalizedTextField = { key: string; label: string; maxLength: number; multiline?: boolean };

const TAB_ORDER: SupportedLanguage[] = [
  LOCALIZED_TEXT_FALLBACK, ...SUPPORTED_LANGUAGES.filter(code => code !== LOCALIZED_TEXT_FALLBACK),
];

export function LocalizedTextFields({ fields, value, onChange, disabled = false }: {
  fields: readonly LocalizedTextField[];
  value: Record<string, LocalizedText | undefined>;
  onChange: (value: Record<string, LocalizedText | undefined>) => void;
  disabled?: boolean;
}) {
  const { t, i18n } = useTranslation();
  const [language, setLanguage] = useState<SupportedLanguage>(() => resolveSupportedLanguage(i18n.language));
  const filled = (code: SupportedLanguage) => fields.some(field => value[field.key]?.[code]?.trim());
  const update = (key: string, text: string) => onChange({ ...value, [key]: { ...value[key], [language]: text } });
  return <div className="localized-text">
    <div className="localized-text__tabs" role="tablist" aria-label={t("localizedText.languages")}>
      {TAB_ORDER.map(code => <button key={code} type="button" role="tab" aria-selected={code === language}
        className={`localized-text__tab${code === language ? " localized-text__tab--active" : ""}${filled(code) ? " localized-text__tab--filled" : ""}`}
        disabled={disabled} onClick={() => setLanguage(code)}>
        {t(`settings.language.${code}`)}
        {code === LOCALIZED_TEXT_FALLBACK && <span className="localized-text__required">{t("localizedText.required")}</span>}
      </button>)}
    </div>
    <div className="localized-text__panel" role="tabpanel">
      {fields.map(field => <label className="scenario-info__field" key={field.key}>
        <span className="scenario-info__label">{field.label}</span>
        {field.multiline
          ? <textarea className="scenario-info__textarea" rows={3} maxLength={field.maxLength} disabled={disabled} lang={language}
            value={value[field.key]?.[language] ?? ""} onChange={e => update(field.key, e.target.value)} />
          : <input type="text" className="scenario-info__input" maxLength={field.maxLength} disabled={disabled} lang={language}
            value={value[field.key]?.[language] ?? ""} onChange={e => update(field.key, e.target.value)} />}
      </label>)}
    </div>
  </div>;
}
