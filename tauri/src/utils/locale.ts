/**
 * 表示言語まわりの共通定義
 *
 * UI の表示言語コード（i18next の言語コード）と、数値・日付整形に渡す
 * BCP47 ロケールを一箇所で管理する。言語を追加するときはこのファイルの
 * SUPPORTED_LANGUAGES と INTL_LOCALES にだけ手を入れればよい。
 *
 * 言語コードは i18n リソースのディレクトリ名（src/i18n/<code>/）と一致させる。
 * 中国語は簡体字・繁体字で語彙が異なるため、地域ではなく字体で分けている。
 * スペイン語はラテンアメリカ向けの綴り・語彙を採用するため es-419 とする。
 */

/** UI が対応する表示言語コード。設定ウィンドウ・ニューゲームの選択肢もこの順で並ぶ。 */
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
 * 言語コード → Intl API に渡すロケール。
 * Intl は言語コードだけでも動くが、通貨・数値のコンパクト表記（万/億、K/M/B）は
 * 地域まで指定した方が意図した表記に安定するため、代表地域を明示している。
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

/** 未知の言語コードが渡されたときのフォールバック。i18n の fallbackLng と揃える。 */
export const DEFAULT_LANGUAGE: SupportedLanguage = "ja";

/** 任意の文字列が対応言語コードかを判定する（設定ファイル由来の値の検証に使う）。 */
export function isSupportedLanguage(value: unknown): value is SupportedLanguage {
  return (
    typeof value === "string" &&
    (SUPPORTED_LANGUAGES as readonly string[]).includes(value)
  );
}

/**
 * 任意の言語コードを対応言語コードへ丸める。
 * i18next は "ja-JP" のような地域付きコードを返すことがあるため、
 * 完全一致しない場合は先頭の言語サブタグでも照合し、それでも該当が無ければ既定言語を返す。
 * 「必ずどれか1つが選ばれている」プルダウンの現在値解決に使う。
 */
export function resolveSupportedLanguage(language: string | undefined): SupportedLanguage {
  if (isSupportedLanguage(language)) {
    return language;
  }
  const primary = (language ?? "").split("-")[0];
  return SUPPORTED_LANGUAGES.find((code) => code.split("-")[0] === primary) ?? DEFAULT_LANGUAGE;
}

/**
 * i18n.language を Intl 用ロケールへ変換する。
 */
export function resolveIntlLocale(language: string | undefined): string {
  return INTL_LOCALES[resolveSupportedLanguage(language)];
}
