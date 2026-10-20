/**
 * Shared display language definitions. Manage UI language codes (i18next) and BCP47 number/date formatting
 * locales together. Add languages only through SUPPORTED_LANGUAGES and INTL_LOCALES here. Codes must match
 * src/i18n/<code>/ directories. Chinese uses script rather than region because Simplified/Traditional vocabulary
 * differs. Spanish uses es-419 for Latin American spelling/vocabulary.
 */

/** Supported UI language codes. Settings/new-game choices follow this order. */
export const SUPPORTED_LANGUAGES = [
  "ja",
  "en",
  "es-419",
  "zh-Hans",
  "zh-Hant",
  "fr",
  "ko",
] as const;

export type SupportedLanguage = (typeof SUPPORTED_LANGUAGES)[number];

/**
 * Language code → locale for Intl APIs. Intl accepts bare language codes, but explicit representative regions
 * make currency and compact number notation (万/億, K/M/B) more predictable.
 */
const INTL_LOCALES: Record<SupportedLanguage, string> = {
  ja: "ja-JP",
  en: "en-US",
  "es-419": "es-419",
  "zh-Hans": "zh-Hans-CN",
  "zh-Hant": "zh-Hant-TW",
  fr: "fr-FR",
  ko: "ko-KR",
};

/** Fallback for unknown language codes. Match i18n fallbackLng. */
export const DEFAULT_LANGUAGE: SupportedLanguage = "ja";

/** Check whether a string is a supported language code (for validating settings values). */
export function isSupportedLanguage(value: unknown): value is SupportedLanguage {
  return (
    typeof value === "string" &&
    (SUPPORTED_LANGUAGES as readonly string[]).includes(value)
  );
}

/**
 * Normalize an arbitrary code to a supported language. i18next may return regional codes such as ja-JP; try an
 * exact match, then the leading language subtag, then the default. Used to resolve dropdown values that must
 * always select one option.
 */
export function resolveSupportedLanguage(language: string | undefined): SupportedLanguage {
  if (isSupportedLanguage(language)) {
    return language;
  }
  const primary = (language ?? "").split("-")[0];
  return SUPPORTED_LANGUAGES.find((code) => code.split("-")[0] === primary) ?? DEFAULT_LANGUAGE;
}

/**
 * Convert i18n.language to an Intl locale.
 */
export function resolveIntlLocale(language: string | undefined): string {
  return INTL_LOCALES[resolveSupportedLanguage(language)];
}

/**
 * Author-written multilingual text ({language code: text}) for scenario descriptions, initial city names, and
 * quest names/descriptions. English is mandatory as the fallback when display-language text is absent, matching
 * core localized_text rules.
 */
export type LocalizedText = Partial<Record<SupportedLanguage, string>>;

/** Fallback language when display-language text is absent. */
export const LOCALIZED_TEXT_FALLBACK: SupportedLanguage = "en";

/** Select the first available text in order: requested language → English → supported languages. Return an empty string if none. */
export function resolveLocalizedText(texts: LocalizedText | null | undefined, language: string | undefined): string {
  if (!texts) return "";
  for (const code of [resolveSupportedLanguage(language), LOCALIZED_TEXT_FALLBACK, ...SUPPORTED_LANGUAGES]) {
    const text = texts[code];
    if (text && text.trim()) return text;
  }
  return "";
}

/** Remove whitespace-only language entries before sending. */
export function compactLocalizedText(texts: LocalizedText | null | undefined): LocalizedText {
  const result: LocalizedText = {};
  for (const code of SUPPORTED_LANGUAGES) {
    const text = texts?.[code];
    if (text && text.trim()) result[code] = text;
  }
  return result;
}

/** Whether English text is missing. Even optional fields require English if any language has text. */
export function missingEnglish(texts: LocalizedText | null | undefined, required: boolean): boolean {
  const compact = compactLocalizedText(texts);
  if (compact[LOCALIZED_TEXT_FALLBACK]) return false;
  return required || Object.keys(compact).length > 0;
}
