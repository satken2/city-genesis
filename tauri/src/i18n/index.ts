/**
 * i18next の初期化設定
 *
 * react-i18next を使用して多言語対応を行う。
 * 翻訳リソースは言語ディレクトリ配下にドメインごとの JSON として分割し、
 * デフォルト言語（＝原本）は日本語とする。
 *
 * 言語設定は Rust 側の設定ファイル（settings.json）に永続化され、
 * 各ウィンドウのマウント時に loadAppSettings から復元される。
 * そのため i18n の初期化時にはデフォルト言語を使用し、
 * 非同期で読み込んだ設定値を後から適用する。
 */

import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import { DEFAULT_LANGUAGE, SUPPORTED_LANGUAGES } from "../utils/locale";

/**
 * 翻訳リソースはドメインごとに <言語>/*.json へ分割して管理する。
 * 各ファイルはトップレベル名前空間(agent, budget など)をそのまま含み、
 * ここで全ファイルを1つの translation 名前空間へ集約する。
 * これにより i18next へ渡す形は従来と同一で、コンポーネント側の
 * t("agent.foo") のようなキー参照は変更不要。
 *
 * glob のパターンにはリテラルしか書けないため、全言語をまとめて1回で取得し、
 * パスの言語ディレクトリ名で振り分ける。
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
  // "zh-Hans" のような字体サブタグ付きコードを "zh" へ切り詰めさせない
  // （簡体字・繁体字を別リソースとして持つため）。
  load: "currentOnly",
  nonExplicitSupportedLngs: false,
  supportedLngs: [...SUPPORTED_LANGUAGES],
  interpolation: {
    escapeValue: false,
  },
});

export default i18n;
