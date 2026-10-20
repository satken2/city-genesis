/**
 * i18next initialization. Use react-i18next for localization. Split resources into domain JSON files under
 * language directories; Japanese is the default and source language. Persist language in Rust's settings.json
 * and restore through loadAppSettings on each window mount. Initialize i18n with the default, then apply
 * asynchronously loaded settings.
 */

import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import { DEFAULT_LANGUAGE, SUPPORTED_LANGUAGES } from "../utils/locale";

/**
 * Manage translation resources in <language>/*.json by domain. Each file includes its top-level namespace
 * (agent, budget, etc.); combine them into one translation namespace here. This preserves the existing i18next
 * structure and component keys such as t("agent.foo"). Glob patterns require literals, so fetch all languages
 * together and distribute by language directory name in each path.
 */
const allModules = import.meta.glob("./*/*.json", { eager: true });

function assembleResources(language: string): Record<string, unknown> {
  const merged: Record<string, unknown> = {};
  const prefix = `./${language}/`;
  for (const [path, mod] of Object.entries(allModules)) {
    if (!path.startsWith(prefix)) continue;
    Object.assign(merged, (mod as { default: Record<string, unknown> }).default);
  }
  return merged;
}

const resources = Object.fromEntries(
  SUPPORTED_LANGUAGES.map((language) => [
    language,
    { translation: assembleResources(language) },
  ])
);

i18n.use(initReactI18next).init({
  resources,
  lng: DEFAULT_LANGUAGE,
  fallbackLng: DEFAULT_LANGUAGE,
  // Do not truncate script-subtag codes such as "zh-Hans" to "zh",
  // since Simplified and Traditional Chinese have separate resources.
  load: "currentOnly",
  nonExplicitSupportedLngs: false,
  supportedLngs: [...SUPPORTED_LANGUAGES],
  interpolation: {
    escapeValue: false,
  },
});

export default i18n;
